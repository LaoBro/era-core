"""Regression test for CLI bottom-anchored layout (spec §4 / B1).

原版 Emuera 是贴底渲染（`GetLinePointY`：内容末行恒锚可绘制区底部）。CLI 侧 B1
（`7ea0e7c`）把 `TerminalRenderer` 改为贴底，本文件把「贴底」落成机器判据：

- 稀疏：`baseRow = visibleLines − renderedLines` ⇒ 最后一条内容写在 `visibleLines − 1`
- 满屏：`renderedLines == visibleLines` ⇒ `baseRow = 0` ⇒ 首行写在 0（贴底 = 顶部对齐，
  与改动前逐字节一致）—— 这正是「满屏行为不变」的精确表述

**终端高度怎么定**：不是 pywinpty 的 `setwinsize` / `spawn(dimensions=)` —— CLI 启动时
会用自己的尺寸覆盖（`HeadlessRunner.TrySetConsoleSize(gameColumns, gameRows + 4)`）。
实测高度由游戏配置决定：`H = WindowY / LineHeight + 8`（LineHeight=19）。
故本测试改 `emuera.config` 的 `ウィンドウ高さ` 来拿到 29 / 33 行两种终端：
- `WindowY=400` ⇒ gameRows=21 ⇒ H=29（spec §7 点名的场景：3 行探针有 2 行落到 y ≥ 480）
- `WindowY=480` ⇒ gameRows=25 ⇒ H=33（默认）
并且断言实测 H 与期望一致 —— 万一环境变了会响，不会静默假绿。

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
# 屏幕模型留足行数，避免截断（写屏行号由输出决定，不预设）
MODEL_ROWS = 80

# (WindowY, 期望终端行数)
CASES = [(400, 29), (480, 33)]

# 稀疏：3 行内容（末行是按钮 ⇒ 顺带断言按钮行 = 渲染末行）
ERB_SPARSE = """@SYSTEM_TITLE
PRINTL A
PRINTL B
PRINTL [42] ClickMe
INPUT
QUIT
"""

# 满屏：40 行 > 最大终端的 visibleLines(32)
ERB_FULL = "@SYSTEM_TITLE\n" + "".join(f"PRINTL x{i}\n" for i in range(1, 41)) + "INPUT\nQUIT\n"

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
    """Minimal VT screen model（与 test_cli_button_click.py 同款，独立副本）。"""

    def __init__(self, rows, cols):
        self.rows = [""] * rows
        self.cols = cols
        self.r = 0
        self.c = 0
        self.max_cursor_row = 0  # 1-based，最后一次光标定位

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

    def non_empty_rows(self):
        return [idx for idx, line in enumerate(self.rows) if line.strip()]

    def last_non_empty(self):
        rows = self.non_empty_rows()
        return rows[-1] if rows else -1

    def first_non_empty(self):
        rows = self.non_empty_rows()
        return rows[0] if rows else -1


def _capture(binary, erb, window_y, wait_text):
    """跑 CLI（改配置为 window_y）到 wait_text 出现，返回重建的 Screen。"""
    temp_dir, game_dir = copy_test_game_with_erb(erb)
    try:
        cfg = Path(game_dir) / "emuera.config"
        text = cfg.read_text(encoding="utf-8", errors="replace")
        cfg.write_text(
            re.sub(r"ウィンドウ高さ:\d+", f"ウィンドウ高さ:{window_y}", text),
            encoding="utf-8",
        )

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
            deadline = time.time() + 20.0
            while time.time() < deadline:
                if wait_text in "".join(output):
                    break
                if not proc.isalive():
                    break
                time.sleep(0.2)
            time.sleep(0.8)

            screen = Screen(MODEL_ROWS, COLS)
            screen.feed("".join(output))
            return screen
        finally:
            stop = True
            if proc.isalive():
                proc.terminate()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def _geometry(screen):
    """从输出反推几何：光标停在预留行 visibleLines ⇒ 终端高度 H = visibleLines + 1。"""
    visible_lines = screen.max_cursor_row - 1  # max_cursor_row 是 1-based
    return visible_lines, visible_lines + 1


def test_sparse_content_is_bottom_anchored(binary, window_y, expected_h):
    screen = _capture(binary, ERB_SPARSE, window_y, "ClickMe")
    visible_lines, height = _geometry(screen)

    check(height == expected_h, f"WindowY={window_y} ⇒ 终端 {height} 行（期望 {expected_h}）")
    expected_last = visible_lines - 1

    last = screen.last_non_empty()
    check(last >= 0, f"终端 {height} 行：屏幕有内容（末行 {last}）")
    check(last == expected_last, f"终端 {height} 行：内容末行锚在 {expected_last}（实得 {last}）")
    check(
        last >= 0 and "ClickMe" in screen.rows[last],
        f"终端 {height} 行：末行是按钮行（渲染行与命中区一致）",
    )
    non_empty = screen.non_empty_rows()
    if non_empty:
        base_row = non_empty[0]
        check(
            all(idx in non_empty for idx in range(base_row, last + 1)),
            f"终端 {height} 行：内容块连续（{base_row}..{last}），上方 {base_row} 行为留白",
        )


def test_full_screen_keeps_top_alignment(binary, window_y, expected_h):
    screen = _capture(binary, ERB_FULL, window_y, "x40")
    visible_lines, height = _geometry(screen)

    check(height == expected_h, f"WindowY={window_y} ⇒ 终端 {height} 行（期望 {expected_h}）")
    expected_last = visible_lines - 1

    first, last = screen.first_non_empty(), screen.last_non_empty()
    check(first == 0, f"终端 {height} 行：满屏时首行在 0（baseRow=0，与改动前一致；实得 {first}）")
    check(last == expected_last, f"终端 {height} 行：满屏时末行在 {expected_last}（实得 {last}）")
    check(
        screen.non_empty_rows() == list(range(visible_lines)),
        f"终端 {height} 行：可见区 0..{expected_last} 全被写满",
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()
    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]
    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = (ROOT / game_dir).resolve()

    for window_y, expected_h in CASES:
        print(f"--- WindowY={window_y}（终端 {expected_h} 行）：稀疏内容贴底 ---")
        test_sparse_content_is_bottom_anchored(binary_path, window_y, expected_h)
        print(f"--- WindowY={window_y}（终端 {expected_h} 行）：满屏保持顶部对齐 ---")
        test_full_screen_keeps_top_alignment(binary_path, window_y, expected_h)

    print(f"\n=== CLI bottom anchor test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
