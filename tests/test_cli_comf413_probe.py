"""CLI 侧 `COMF413:135` 等价出口 **缺口记录**（真实 ConPTY + VT 鼠标字节）。

## 这是「已知缺口」的锚点，不是验收判据

`COMF413:135` 的出口要 `GETKEYTRIGGERED(0x01)` **且** `ISNUMERIC(MOUSE_OVER)`
（`MOUSE_OVER` 来自**上一圈**的 `MOUSEB()`）。

| 通道 | Web（M5a/M2b）| CLI（2026-10-15 实测）|
|---|---|---|
| 按下 ⇒ `GETKEYTRIGGERED(0x01)` | ✅ 前端 `mousedown` 帧 | ✅ **M4b**（VT 鼠标键 → `SubmitKey`）|
| 「指针在哪」⇒ `MOUSEB()` | ✅ 前端 `mouseenter` 上报（**Hover**）| 🔴 **读不到**（Void 下为 `""`）|

CLI 有**两处**成因，且互相叠加：

1. **Void 等待下命中区被清空**（代码确证）：`ButtonSelectionMode.RefreshButtonRegionsFromSnapshot`
   在 `_console.CurrentRequest == null` 时 `ClearRegions()`（`ButtonSelectionMode.cs:174-181`），
   而 `COMF413` 的搜索模式正是 Void（`req == null`）⇒ 命中区恒空。
2. **没有持续指针位置**：`EnableSgrMouse` 开的是 `\\x1b[?1000h`（normal tracking，**只报按下/释放**），
   不是 1003（any-event）⇒ 纯移动不产生事件 ⇒ 坐标只在按下时更新一次。
   且 `MOUSEB()` 走的 `MoveMouse → PointingSring` 命中路径实测也拿不到值（3/3 空）。

⇒ 与 Web 的差别是**结构性的**：Web 的 `MOUSEB()` 走 `HoverValue` **快照**（前端直接上报，
不依赖命中区、不依赖坐标），CLI 只有 `Geometry`，而 Geometry 那条路在 Void 下两头都不通。

⚠️ **本文件断言的是「缺口仍然存在」**（反向锚点）：一旦 CLI 补上 `Hover` 上报
（DOM 等价物 = VT 1003 + `SubmitHover(命中值)`），这两条断言会**变红** ——
那是信号，提示把本文件改成正向验收（「一次按下即出口」，与
`tests/test_web_input_to_script.py` 同构）。届时**不要**直接改断言消红，先确认是缺口真的修了。

Uses pywinpty (ConPTY) for real VT terminal interaction.
"""
import argparse
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

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, find_binary

ROWS, COLS = 33, 100

# 与 tests/test_web_input_to_script.py 的探针同构，但把出口拆成两级 + 加电平诊断：
# 这样能区分「触发到达但 MOUSE_OVER 空」与「触发根本没到达」——两者根因完全不同。
ERB = """@SYSTEM_TITLE
#DIMS DYNAMIC MOUSE_OVER
#DIM HOVER_SEEN
#DIM LEVEL_SEEN
#DIM TRIG_NOHOVER_SEEN
HOVER_SEEN = 0
LEVEL_SEEN = 0
TRIG_NOHOVER_SEEN = 0
PRINTL COMF_PROBE_READY
PRINTL [42] ClickMe
$CLI_WAIT
AWAIT 1
IF GETKEY(1) && LEVEL_SEEN == 0
	LEVEL_SEEN = 1
	PRINTFORML COMF_PROBE_LEVEL_DOWN
ENDIF
IF GETKEYTRIGGERED(0x01)
	IF ISNUMERIC(MOUSE_OVER)
		PRINTFORML COMF_PROBE_OK:%MOUSE_OVER%
		INPUT
	ELSE
		IF TRIG_NOHOVER_SEEN == 0
			TRIG_NOHOVER_SEEN = 1
			PRINTFORML COMF_PROBE_TRIGGER_NO_HOVER
		ENDIF
	ENDIF
ENDIF
IF MOUSEB() != ""
	MOUSE_OVER = %MOUSEB()%
	IF HOVER_SEEN == 0
		HOVER_SEEN = 1
		PRINTFORML COMF_PROBE_HOVER_SEEN:%MOUSE_OVER%
	ENDIF
ELSE
	MOUSE_OVER =
ENDIF
GOTO CLI_WAIT
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
    """Minimal VT screen model（定位按钮渲染行）。"""

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


def _sgr(col, row, is_press):
    """SGR press/release（1-based 坐标）。"""
    return f"\x1b[<0;{col + 1};{row + 1}{'M' if is_press else 'm'}"


def run_probe(binary):
    temp_dir, game_dir = copy_test_game_with_erb(ERB)
    proc = None
    try:
        proc = PtyProcess.spawn([str(binary), "--ExeDir", str(game_dir), "--protocol", "cli"])
        try:
            proc.setwinsize(ROWS, COLS)
        except Exception:
            pass

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

        def text():
            return "".join(output)

        def wait_for(needle, timeout):
            deadline = time.time() + timeout
            while time.time() < deadline:
                if needle in text():
                    return True
                if proc is not None and not proc.isalive():
                    return needle in text()
                time.sleep(0.05)
            return needle in text()

        check(wait_for("COMF_PROBE_READY", 20.0), "脚本进入 AWAIT 1 循环（COMF_PROBE_READY）")
        time.sleep(0.5)

        screen = Screen(ROWS, COLS)
        screen.feed(text())
        button_row = screen.find_row("ClickMe")
        check(button_row >= 0, f"定位到按钮渲染行（0-based {button_row}）")
        if button_row < 0:
            print("---- 末屏 ----")
            print("\n".join(screen.rows))
            return

        # ---- 第一次按下：Web 上已足以出口（hover 早已上报）；CLI 上预期不足 ----
        proc.write(_sgr(2, button_row, True))
        ok_first = wait_for("COMF_PROBE_OK:42", 3.0)
        hover_seen = "COMF_PROBE_HOVER_SEEN" in text()

        # ---- 第二次按下：坐标已在第一次按下时更新 ⇒ 预期这一次出口 ----
        ok_second = False
        if not ok_first:
            proc.write(_sgr(2, button_row, False))   # 先松开（否则 down 不再触发版本号）
            time.sleep(0.4)
            proc.write(_sgr(2, button_row, True))
            ok_second = wait_for("COMF_PROBE_OK:42", 5.0)

        print(f"  [diag] 第一次按下即出口={ok_first}；第二次按下出口={ok_second}；"
              f"期间是否读到过 MOUSEB={hover_seen}")

        # 🔴 缺口锚点（见文件头）：断言「当前不支持」。它们变红 = CLI 的 Hover 通道已落地。
        check(
            not hover_seen,
            "🔴 缺口锚点：CLI 在 Void 等待下 `MOUSEB()` 读不到值（Web 已可）"
            " —— 本断言变红说明缺口已修，请把本文件改成正向验收",
        )
        check(
            not (ok_first or ok_second),
            "🔴 缺口锚点：CLI 上 `COMF413:135` 等价出口不成立"
            " —— 本断言变红说明缺口已修",
        )

        print("  [diag] 成因：① Void（`CurrentRequest == null`）下 `ButtonSelectionMode` 清空命中区"
              "（`ButtonSelectionMode.cs:174-181`）；② `EnableSgrMouse` 只开 1000（不报纯移动），"
              "且 `MOUSEB()` 的 `MoveMouse → PointingSring` 路径在 CLI 实测也拿不到值。")
    finally:
        if proc is not None and proc.isalive():
            proc.terminate()
        shutil.rmtree(temp_dir, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to EraCore.Cli binary")
    args = parser.parse_args()
    binary_path = Path(args.binary) if args.binary else find_binary(str(ROOT))[0]

    print("--- CLI：COMF413:135 等价出口探针 ---")
    run_probe(binary_path)

    print(f"\n=== CLI COMF413 probe: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
