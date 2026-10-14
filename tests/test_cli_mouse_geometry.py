"""Regression test for CLI mouse geometry: `MY` / 按钮点击取值（spec §7 的 CLI 列）。

原版判据（标定记录 §4 / spec §7）：终端 33 行、`LH=19`、`H = visibleLines × LH = 608`
⇒ `MY` 终端顶行 = `−H = −608`、内容末行 = `−LH = −19`；`MOUSEB` 点按钮 ⇒ 取到按钮值。

**怎么读出来**：ERB 用 `PRINTFORML MY={MOUSEY()}` 把引擎换算后的 `MOUSEY` 打印到终端，
Python 侧从重建的屏幕里抓 `MY=`。语法要点（踩过坑）：
- `{MOUSEY}` **不行**（`MOUSEY` 是方法，必须带括号）⇒ 用 `{MOUSEY()}`
- `%MOUSEB()%` 语法可编译，但**回合推进后命中区被清空** ⇒ 提交后读到空串
  （`RefreshButtonRegionsFromSnapshot` 无请求时 ClearRegions），故不作为判据
- 因此 `MOUSEB` 的判据用「点击按钮行 ⇒ `RESULT` 取到 42」表达 —— 点击路径就是
  `VtInputHandler.OnMouseEvent → _tracker.HitTest(SGR 绝对行) → DispatchMouseClick`，
  这正是 `MOUSEB` 消费的同一个命中结果

Uses pywinpty (ConPTY) for real VT terminal interaction.
"""
import re
import shutil
import sys
import threading
import time
from pathlib import Path

HAS_WINPTY = False
try:
    from winpty import PtyProcess

    HAS_WINPTY = True
except ImportError:
    pass

if sys.platform != "win32" or not HAS_WINPTY:
    print("Skipped: CLI test requires Windows + pywinpty")
    sys.exit(0)

import argparse

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, find_binary

LINE_HEIGHT = 19  # test_game/emuera.config: 一行の高さ
COLS = 100
MODEL_ROWS = 80

# MY 判据：点击后 INPUT 返回，脚本打印 MY 与 RS
ERB = """@SYSTEM_TITLE
PRINTL A
PRINTL B
PRINTL [42] ClickMe
INPUT
PRINTFORML MY={MOUSEY()}
PRINTFORML RS={RESULT}
QUIT
"""

passed = 0
failed = 0


def check(condition, message):
    global passed, failed
    if condition:
        passed += 1
        print(f"  PASS: {message}")
    else:
        failed += 1
        print(f"  FAIL: {message}")


class Screen:
    """Minimal VT screen model（与 test_cli_bottom_anchor.py 同款，独立副本）。"""

    def __init__(self, rows, cols):
        self.rows = [""] * rows
        self.cols = cols
        self.r = 0
        self.c = 0
        self.max_cursor_row = 0

    def feed(self, data):
        i = 0
        while i < len(data):
            ch = data[i]
            if ch == "\x1b":
                m = re.match(r"\x1b\[([0-9;?]*)([A-Za-z])", data[i:])
                if m:
                    params, cmd = m.group(1), m.group(2)
                    nums = [int(x) for x in params.split(";") if x.isdigit()]
                    if cmd in ("H", "f"):
                        self.r = (nums[0] - 1) if nums else 0
                        self.c = (nums[1] - 1) if len(nums) > 1 else 0
                        if nums and nums[0] > self.max_cursor_row:
                            self.max_cursor_row = nums[0]
                    elif cmd == "J" and nums and nums[0] == 2:
                        self.rows = [""] * len(self.rows)
                        self.r = self.c = 0
                    elif cmd == "K" and 0 <= self.r < len(self.rows):
                        self.rows[self.r] = self.rows[self.r][: self.c]
                    i += m.end()
                    continue
                i += 2
                continue
            if ch == "\r":
                self.c = 0
            elif ch == "\n":
                self.r = min(self.r + 1, len(self.rows) - 1)
            else:
                if 0 <= self.r < len(self.rows) and self.c < self.cols:
                    self.rows[self.r] += " " * (self.c - len(self.rows[self.r])) + ch
                self.c += 1
            i += 1

    def find_row(self, needle):
        for idx, line in enumerate(self.rows):
            if needle in line:
                return idx
        return -1


def _run(binary, clicks, keys=None):
    """跑 ERB，按 clicks 下发 SGR 按下，可选再发键盘输入；返回屏幕。"""
    temp_dir, game_dir = copy_test_game_with_erb(ERB)
    try:
        proc = PtyProcess.spawn([str(binary), "--ExeDir", str(game_dir), "--protocol", "cli"])
        output = []
        stop = False

        def reader():
            while not stop:
                try:
                    data = proc.read()
                    if data:
                        output.append(data)
                    elif not proc.isalive():
                        break
                except Exception:
                    break

        t = threading.Thread(target=reader, daemon=True)
        t.start()
        try:
            deadline = time.time() + 15.0
            while time.time() < deadline:
                if "ClickMe" in "".join(output):
                    break
                if not proc.isalive():
                    break
                time.sleep(0.2)
            time.sleep(0.6)

            for col, row in clicks:
                proc.write(f"\x1b[<0;{col + 1};{row + 1}M")
                time.sleep(0.5)
            if keys:
                proc.write(keys)
                time.sleep(0.8)
            time.sleep(1.2)

            screen = Screen(MODEL_ROWS, COLS)
            screen.feed("".join(output))
            return screen
        finally:
            stop = True
            if proc.isalive():
                proc.terminate()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def _read_value(screen, prefix):
    """从屏幕里抓 `prefix<值>`（如 'MY=' → −608）。"""
    for line in screen.rows:
        idx = line.find(prefix)
        if idx >= 0:
            return line[idx + len(prefix) :].strip()
    return None


def test_mouse_y_top_row_is_minus_h(binary):
    """点击终端顶行 ⇒ MY = −H（= −visibleLines × LH）。"""
    screen = _run(binary, [(0, 0)], keys="0\r")
    visible_lines = screen.max_cursor_row - 1
    h = visible_lines * LINE_HEIGHT

    my = _read_value(screen, "MY=")
    check(my is not None, f"读到 MY（终端 {visible_lines + 1} 行 ⇒ H={h}）")
    check(my == str(-h), f"终端顶行 MY == −H == {-h}（实得 {my}）")


def test_mouse_y_content_last_row_is_minus_line_height(binary):
    """点击按钮所在的内容末行 ⇒ MY = −LH（= −19），且取到按钮值 42。"""
    screen = _run(binary, [(0, 0)])  # 先占位跑一次拿到几何（首行点击不提交）
    visible_lines = screen.max_cursor_row - 1

    # 按钮行 = 内容末行 = visibleLines − 1
    btn_row = screen.find_row("ClickMe")
    check(btn_row == visible_lines - 1, f"按钮渲染在内容末行 {visible_lines - 1}（实得 {btn_row}）")

    screen = _run(binary, [(0, btn_row)])  # 点击按钮行：提交 42 + 更新坐标
    my = _read_value(screen, "MY=")
    rs = _read_value(screen, "RS=")
    check(my == str(-LINE_HEIGHT), f"内容末行 MY == −LH == {-LINE_HEIGHT}（实得 {my}）")
    check(rs == "42", f"点击按钮行取到按钮值 42（实得 {rs}）")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()
    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]
    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = (ROOT / game_dir).resolve()

    print("--- MY：终端顶行 = −H ---")
    test_mouse_y_top_row_is_minus_h(binary_path)
    print("--- MY：内容末行 = −LH / 按钮取值 42 ---")
    test_mouse_y_content_last_row_is_minus_line_height(binary_path)

    print(f"\n=== CLI mouse geometry test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
