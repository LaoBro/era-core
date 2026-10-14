"""CLI 鼠标键电平：`GETKEY` / `GETKEYTRIGGERED(0x01)`（ADR-0031 / spec-v3 M4b）。

**为什么需要这一环**：`COMF413:135` 的出口要两件同时成立 ——
`GETKEYTRIGGERED(0x01)` 且 `ISNUMERIC(MOUSE_OVER)`。`MOUSEB()` 那半由 M4 的 Geometry
通道给了，但 `GETKEY` 那半在 CLI 上恒 0：VT 路径此前**从不**写 `IInputHost`
（`SubmitKey` 的生产调用点只有 JSONL 协议一处），而 `GetKeyStateMethod` 又在
`KeyboardState` 能力位上门卫。M4b 把 VT 鼠标事件接到键状态上，本文件是它的判据。

判据用「真实 VT 字节」表达：pywinpty 发一条 SGR 按下（`\\x1b[<0;5;5M`，**不发松开** ——
`GETKEYTRIGGERED` 要求「此刻按着」），脚本轮询读到触发即退出。

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

COLS = 100
MODEL_ROWS = 80

# 探针：按住左键期间 GETKEY(1)=1 只打印一次；读到 GETKEYTRIGGERED(1) 即退出。
# ⚠️ `#DIM` 变量名不可单字母（保留名 → 冷加载警告 → State=Error），故用 KSDOWN_SEEN。
ERB = """@SYSTEM_TITLE
#DIM KSDOWN_SEEN
KSDOWN_SEEN = 0
PRINTL PROBE_READY
$PROBE_LOOP
AWAIT 10
IF GETKEY(1) && KSDOWN_SEEN == 0
    KSDOWN_SEEN = 1
    PRINTFORML KS_DOWN
ENDIF
IF GETKEYTRIGGERED(1)
    PRINTFORML KS_TRIGGER
    QUIT
ENDIF
GOTO PROBE_LOOP
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
    """Minimal VT screen model（与 test_cli_mouse_geometry.py 同款，独立副本）。"""

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


def run_probe(binary, release_after=None):
    """跑探针：发一次左键按下，`release_after` 秒后松开（None = 一直按住）。

    返回 `(flat_screen, triggered, elapsed)`。
    """
    temp_dir, game_dir = copy_test_game_with_erb(ERB)
    proc = None
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

        threading.Thread(target=reader, daemon=True).start()

        def joined():
            return "".join(output)

        def wait_until(predicate, timeout):
            deadline = time.time() + timeout
            while time.time() < deadline:
                if predicate():
                    return True
                if proc is not None and not proc.isalive():
                    return predicate()
                time.sleep(0.05)
            return predicate()

        ready = wait_until(lambda: "PROBE_READY" in joined(), 15.0)
        if not ready:
            return "<PROBE_READY 未出现>", False, 0.0

        started = time.time()
        # SGR 左键按下（cb=0, 'M'）；松开是同一序列的 'm'。
        proc.write("\x1b[<0;5;5M")
        if release_after is not None:
            time.sleep(release_after)
            proc.write("\x1b[<0;5;5m")

        triggered = wait_until(lambda: "KS_TRIGGER" in joined(), 10.0)
        elapsed = time.time() - started

        screen = Screen(MODEL_ROWS, COLS)
        screen.feed(joined())
        flat = "\n".join(screen.rows)

        if not triggered:
            print("---- 末屏 ----")
            print(flat)
            print("--------------")

        return flat, triggered, elapsed
    finally:
        if proc is not None and proc.isalive():
            proc.terminate()
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_left_button_press_drives_getkeytriggered(binary):
    """按住左键 ⇒ `GETKEY(1)` 置位、`GETKEYTRIGGERED(1)` 成立。"""
    flat, triggered, elapsed = run_probe(binary, release_after=None)

    check(triggered, "读到 KS_TRIGGER（GETKEYTRIGGERED(1) 成立）")
    check("KS_DOWN" in flat, "读到 KS_DOWN（GETKEY(1) 电平置位）")
    if triggered and "KS_DOWN" in flat:
        check(
            flat.index("KS_DOWN") < flat.index("KS_TRIGGER"),
            "电平先于边沿（GETKEY 先置位、再由 GETKEYTRIGGERED 消费）",
        )
    check(elapsed < 3.0, f"从按下到读到触发 < 3s（实得 {elapsed:.2f}s）")


def test_quick_click_still_triggers(binary):
    """**快速单击**（按下 60ms 后松开）仍能读到触发 —— 玩家真实操作形态。

    🔴 这条守的是「按下与松开塌缩进同一帧」这类回归：`GETKEYTRIGGERED` 要求
    「此刻按着」，若 down/up 在同一帧内被处理完，电平回到 0 ⇒ 触发整条丢失
    （对应 Web 侧 §3.6 约束一/二同源的风险）。VT 侧靠 SGR 序列逐字节 drain
    拉开两帧，本用例把它钉住。
    """
    flat, triggered, elapsed = run_probe(binary, release_after=0.06)

    check(triggered, "60ms 快速单击仍读到 KS_TRIGGER")
    check("KS_DOWN" in flat, "单击期间 GETKEY(1) 电平被观测到")
    check(elapsed < 3.0, f"从按下到读到触发 < 3s（实得 {elapsed:.2f}s）")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()
    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]

    print("--- CLI 鼠标键：GETKEY / GETKEYTRIGGERED(0x01) ---")
    test_left_button_press_drives_getkeytriggered(binary_path)
    print("--- CLI 鼠标键：快速单击 ---")
    test_quick_click_still_triggers(binary_path)

    print(f"\n=== CLI mouse keystate test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
