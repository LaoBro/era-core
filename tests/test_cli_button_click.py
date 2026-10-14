"""Regression test for CLI button click hit-testing (bottom-anchored layout).

Context: M4 exposed that button hit regions were registered with **viewport-
relative** rows while SGR mouse reports **terminal absolute** rows. As long as
rendering was top-aligned the two coincided; after the 2026-10-14 bottom-anchor
change (spec §3.3 / B2) they diverge by the top padding (= `BaseRow`), so the
tracker must register `Row = baseRow + i` to stay aligned with
`TerminalRenderer`'s write rows.

Click path: `VtInputHandler.OnMouseEvent` -> `_tracker.HitTest(row, col)` ->
`DispatchMouseClick` (row/col come straight from SGR, absolute).

Two assertions:
1. Clicking **row 0** (pre-bottom-anchor numbering) must NOT submit.
2. Clicking the row where the button is actually rendered must submit.

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

# 与 spec §7 场景一致：终端 33 行
ROWS, COLS = 33, 100

ERB = """@SYSTEM_TITLE
PRINTL A
PRINTL B
PRINTL [42] ClickMe
INPUT
PRINTL AfterInput
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
    """Minimal VT screen model: enough to locate the row a button rendered on."""

    def __init__(self, rows, cols):
        self.rows = [""] * rows
        self.cols = cols
        self.r = 0
        self.c = 0

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
                    elif cmd == "J" and nums and nums[0] == 2:
                        self.rows = [""] * len(self.rows)
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


def _sgr_press(col, row):
    """SGR 1000 press (0-based col/row)."""
    return f"\x1b[<0;{col + 1};{row + 1}M"


def _run_and_click(binary, clicks_for_row):
    """Spawn CLI, wait for the button, then click.

    clicks_for_row: callable(button_row) -> list of (col, row) to click in order.
    Returns (output_text, button_row).
    """
    temp_dir, game_dir = copy_test_game_with_erb(ERB)
    try:
        proc = PtyProcess.spawn([str(binary), "--ExeDir", str(game_dir), "--protocol", "cli"])
        try:
            proc.setwinsize(ROWS, COLS)
        except Exception:
            pass  # 尺寸设置失败时按默认尺寸跑，按钮行由屏幕内容动态定位

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

            screen = Screen(ROWS, COLS)
            screen.feed("".join(output))
            button_row = screen.find_row("ClickMe")

            for col, row in clicks_for_row(button_row):
                proc.write(_sgr_press(col, row))
                time.sleep(0.4)
                if "AfterInput" in "".join(output):
                    break

            time.sleep(1.0)
            return "".join(output), button_row
        finally:
            stop = True
            if proc.isalive():
                proc.terminate()
    finally:
        shutil.rmtree(temp_dir, ignore_errors=True)


def test_button_click_hits_rendered_row(binary, game_dir):
    """Clicking the rendered (bottom-anchored) row submits the button."""
    text, button_row = _run_and_click(binary, lambda r: [(c, r) for c in range(12)] if r >= 0 else [])

    check(button_row >= 0, "屏幕中定位到按钮渲染行")
    check(button_row > 0, f"按钮渲染在贴底行（0-based {button_row}），不在第 0 行")
    check("AfterInput" in text, f"渲染行 {button_row} 点击命中按钮（游戏走过 INPUT）")


def test_click_row_zero_does_not_submit(binary, game_dir):
    """Clicking row 0 must not submit — that row is top padding after bottom-anchor."""
    text, _ = _run_and_click(binary, lambda r: [(c, 0) for c in range(12)])

    check("AfterInput" not in text, "第 0 行点击未提交（贴底后此处是留白行）")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()
    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]
    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = (ROOT / game_dir).resolve()

    print("--- test_button_click_hits_rendered_row ---")
    test_button_click_hits_rendered_row(binary_path, game_dir)
    print("--- test_click_row_zero_does_not_submit ---")
    test_click_row_zero_does_not_submit(binary_path, game_dir)

    print(f"\n=== CLI button click test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
