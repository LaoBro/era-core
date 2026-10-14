"""端到端：Web/HTTP 输入帧 → **脚本侧可观测值**（真实 server + 真协议 + 真 ERB）。

## 为什么需要它

M2b / M5a / M6 各自都有判据，但它们分处三层，中间那根**枢纽**没人钉：

| 层 | 判据 | 覆盖 |
|---|---|---|
| 前端发帧 | `e2e/hover-report.spec.ts`（真实 Edge）| 帧发出了、内容对 |
| 协议写宿主 | `KestrelGameServerEndpointTests` / `InputHost*Tests` | 200 + 直接驱动 host |
| 引擎读值 | `InputHostKeyboardTests` / `InputHostFocusTests` | 脚本能读到值 |
| **枢纽** | **（此前无）** | Void 等待（`AWAIT n`，`Timelimit > 0`）期间，`RunLoopAsync` 是否仍会读到 POST 来的状态帧并写进 `IInputHost`？|

`COMF413:135` 的出口正是「**Void 等待中**读 `MOUSEB()` + `GETKEYTRIGGERED(0x01)`」——
这根枢纽断了，前面三层全绿也照样卡死，且没有任何测试会红。

探针 ERB 逐字复刻 `コマンド関連/COMF/日常系/COMF413 料理を作る.ERB:133-144` 的出口结构：
`AWAIT 1` 循环 → `MOUSEB()` 值记入 `MOUSE_OVER` → `GETKEYTRIGGERED(0x01) && ISNUMERIC(...)` 出口。

用 `HOVER_SEEN` 标志把「悬停已被引擎读到」变成一个**可观测的稳定点**再发按下帧，
避免用 sleep 赌时序（悬停必须先于按下被读到 —— 那是原版时序，也是判据要守的东西）。
"""
import json
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import awaiting_of, awaiting_wait, copy_test_game_with_erb, snapshot_text, start_server

# 复刻 COMF413:133-144 的出口结构（含 Void 等待、悬停记值、按下 + 数字校验）。
PROBE_ERB = """@SYSTEM_TITLE
#DIMS DYNAMIC MOUSE_OVER
#DIM HOVER_SEEN
HOVER_SEEN = 0
PRINTL COMF_PROBE_READY
$START
AWAIT 1
IF GETKEYTRIGGERED(0x01) && ISNUMERIC(MOUSE_OVER)
	PRINTFORML COMF_PROBE_OK:%MOUSE_OVER%
	INPUT
ENDIF
IF MOUSEB() != ""
	MOUSE_OVER = %MOUSEB()%
	IF HOVER_SEEN == 0
		HOVER_SEEN = 1
		PRINTFORML COMF_PROBE_HOVER_SEEN
	ENDIF
ELSE
	MOUSE_OVER =
ENDIF
GOTO START
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


class ProbeRun:
    """一次 server 会话 + 探针交互。"""

    def __init__(self, server):
        self.server = server

    def screen(self):
        """当前 snapshot 的可见文本（404/503 时返回空串）。"""
        status, body = self.server.get_snapshot()
        if status != 200:
            return ""
        try:
            return snapshot_text(json.loads(body))
        except Exception:
            return ""

    def wait_for_text(self, needle, timeout):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if needle in self.screen():
                return True
            time.sleep(0.05)
        return needle in self.screen()

    def awaiting(self):
        """当前 snapshot 的 `awaiting` 维度（取不到时为空 dict）。"""
        status, body = self.server.get_snapshot()
        if status != 200:
            return {}
        try:
            return awaiting_of(json.loads(body))
        except Exception:
            return {}

    def state_frame(self, body):
        """状态类输入帧（无 `value` ⇒ 只写宿主、不派发、不产帧）。"""
        return self.server.request("POST", "/input", body)


def run_probe(binary=None):
    temp_dir, game_dir = copy_test_game_with_erb(PROBE_ERB)
    server = None
    try:
        server = start_server(game_dir, binary=binary)
        run = ProbeRun(server)

        status, body = server.start_session()
        check(status == 200, f"POST /load-game 成功（实得 {status}）")
        if status != 200:
            print(body[:400])
            return
        check(run.wait_for_text("COMF_PROBE_READY", 20), "脚本进入 AWAIT 1 循环（COMF_PROBE_READY）")

        # 形态自检：探针必须停在「Void + 有计时」的等待上（= `AWAIT 1`），
        # 与 `COMF413` 搜索模式同形。若哪天探针被改写成非 Void（或零时长让路点），
        # 本测试就测了别的东西 —— 那种假绿必须立刻暴露。
        aw = run.awaiting()
        check(aw.get("input") is None, "探针停在 Void 等待（无 input 维度 ⇒ 与搜索模式同形）")
        check(aw.get("wait") is not None, "该等待带计时（AWAIT 1 ⇒ Timelimit>0，不是零时长让路点）")

        # ---- ① 悬停：状态帧（`hoverValue`，无 value）必须穿过协议、写进宿主、被 AWAIT 中的脚本读到 ----
        status, _ = run.state_frame({"hoverValue": "42"})
        check(status == 200, f"悬停状态帧被接受（实得 {status}）")
        check(
            run.wait_for_text("COMF_PROBE_HOVER_SEEN", 10),
            "🔒 Void（AWAIT 1）等待期间，`MOUSEB()` 读到悬停值 —— 状态帧被主循环消费",
        )

        # ---- ② 按下：keys 帧（VK_LBUTTON=1）⇒ GETKEYTRIGGERED(0x01) 成立 ⇒ 出口 ----
        status, _ = run.state_frame({"keys": [{"code": 1, "down": True}]})
        check(status == 200, f"按下帧被接受（实得 {status}）")
        opened = run.wait_for_text("COMF_PROBE_OK:42", 10)
        if not opened:
            print("---- 末屏 ----")
            print(run.screen()[:800])
            print("--------------")
        check(opened, "🔒 COMF413:135 等价出口成立（`GETKEYTRIGGERED(0x01) && ISNUMERIC(MOUSE_OVER)`）")
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()
        shutil.rmtree(temp_dir, ignore_errors=True)


def run_same_frame(binary=None):
    """**同帧**情形（悬停与按下挤在同一条帧里）⇒ 预期不出口，需再按一次。

    这是「为什么必须分帧」的锚点，也是 Web 与 CLI 的分界所在：

    - Web 上，`mouseenter` 与 `mousedown` 是**两个浏览器事件**、走**两条帧**，
      且物理顺序必然是「先进入元素、后按下」⇒ 悬停帧天然早于按下帧。
    - 但若有人图省事把 `hoverValue` 与 `keys` 合并成一条帧（协议上完全合法），
      `MOUSE_OVER` 取的是**上一圈**的值 ⇒ 悬停来不及被脚本记下，出口不成立。

    前端当前不会产生这种形态（`TerminalDisplay` 报悬停、`useKeyMouseInput` 报键，各发各的帧）。
    本用例守的就是「**将来别合并**」。
    """
    temp_dir, game_dir = copy_test_game_with_erb(PROBE_ERB)
    server = None
    try:
        server = start_server(game_dir, binary=binary)
        run = ProbeRun(server)

        status, _ = server.start_session()
        check(status == 200, f"[同帧] POST /load-game 成功（实得 {status}）")
        if status != 200:
            return
        check(run.wait_for_text("COMF_PROBE_READY", 20), "[同帧] 脚本进入 AWAIT 1 循环")

        # 一次「点击」的两种内容挤在同一帧：悬停值 + 按下。
        status, _ = run.state_frame({"hoverValue": "42", "keys": [{"code": 1, "down": True}]})
        check(status == 200, f"[同帧] 单帧被接受（实得 {status}）")

        ok_same_frame = run.wait_for_text("COMF_PROBE_OK", 3.0)
        check(
            not ok_same_frame,
            "[同帧] 🔒 悬停与按下挤在同一帧时**不**出口 —— `MOUSE_OVER` 取上一圈的值，"
            "而该圈的触发已被消费 ⇒ 正是「必须分帧」的锚点",
        )

        # 悬停此时已被脚本记下 ⇒ 再按一次即出口（证明差的就是「悬停先到一圈」）
        run.state_frame({"keys": [{"code": 1, "down": False}]})
        time.sleep(0.3)
        run.state_frame({"keys": [{"code": 1, "down": True}]})
        check(
            run.wait_for_text("COMF_PROBE_OK", 5.0),
            "[同帧] 补一次按下后出口成立（差的确实只是「悬停先到一圈」）",
        )
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()
        shutil.rmtree(temp_dir, ignore_errors=True)


def run_back_to_back(binary=None):
    """**背靠背**发悬停帧与按下帧（不等待、不轮询）—— 模拟**触屏 tap**。

    触屏没有"把指针移过去"这个独立动作：浏览器在 tap 时**合成** compatibility mouse
    events（`mousemove → mouseenter → mousedown → mouseup → click`），
    于是悬停帧与按下帧几乎同时出去（`e2e/touch-tap.spec.ts` 已实测顺序正确）。

    于是问题只剩：两帧的**间隔**是否大于一个脚本圈（`AWAIT 1` = 1ms）。
    这里直接测脚本侧能不能一次出口 —— 这决定触屏要不要点两次。

    为什么「顺序发、每条等响应」就忠实于 Web 触屏：前端 `connection.ts:703-741` 的
    `stateQueue` 正是「前一帧 settle 后才发下一帧」（`await fetch` 到响应），
    故两帧到达间隔 ≈ 一次完整 HTTP 往返。

    🔴 **但这是环境依赖，不是代码保证** —— 前端**没有**任何显式间隔（无 delay、无节流），
    间隔完全来自前一条 POST 的往返。而且真正的判据不只是「间隔 > 1ms」，而是
    「第二帧到达时，脚本**已经推进过至少一圈**」：

    - 服务端主循环每轮只 `ReadLineAsync` **一行**，但 `continue` 后立刻再读；
    - `AWAIT 1` **不是让路点**（`IsYieldPoint()` 要求 `Timelimit <= 0`）⇒ `DrainYieldsAsync`
      **不会**推进脚本，脚本只在 **1ms 超时**（`CancelAfter(1)`）时推进一圈。

    ⇒ 两行只要落进同一个 1ms 窗口，就是「同圈」⇒ 悬停来不及被脚本记入 `MOUSE_OVER`。
    实测（localhost）**5/5 一次即出口**（往返 ≳ 1ms，稳定跨圈）；但换更快的通道就不保证。

    ⚠️ **最可能翻车的是 MAUI / Android WebView**：走 `postInput` **桥**（同步投递、无网络往返）
    ⇒ 两帧间隔 → 0 ⇒ 大概率同圈 ⇒ **可能需要两次点击**。而 Android 恰是
    「触屏是主要输入方式」的主场景（`ui.ts:179`）。本机无法诚实模拟
    （Python 侧做不到「顺序确定 + 间隔 <1ms」）⇒ 需真机/模拟器确认。

    💡 若要在 MAUI 上做结构性加固，杠杆在**发送侧的最小间隔**（不是客户端 delay 拍脑袋，
    而是「同一批状态帧之间保证跨过一个事件循环轮次」）；⚠️ 但它也**无法普遍保证** ——
    脚本圈长由脚本自己决定（`AWAIT 1` / `TWAIT t,1` / 让路点各不相同）。
    """
    temp_dir, game_dir = copy_test_game_with_erb(PROBE_ERB)
    server = None
    try:
        server = start_server(game_dir, binary=binary)
        run = ProbeRun(server)

        status, _ = server.start_session()
        check(status == 200, f"[背靠背] POST /load-game 成功（实得 {status}）")
        if status != 200:
            return
        check(run.wait_for_text("COMF_PROBE_READY", 20), "[背靠背] 脚本进入 AWAIT 1 循环")

        # 触屏形态：两条帧紧邻发出，不等任何稳定点
        run.state_frame({"hoverValue": "42"})
        run.state_frame({"keys": [{"code": 1, "down": True}]})

        ok_once = run.wait_for_text("COMF_PROBE_OK", 3.0)

        ok_twice = False
        if not ok_once:
            run.state_frame({"keys": [{"code": 1, "down": False}]})
            time.sleep(0.2)
            run.state_frame({"keys": [{"code": 1, "down": True}]})
            ok_twice = run.wait_for_text("COMF_PROBE_OK", 5.0)

        print(f"  [diag] 背靠背：一次点击即出口={ok_once}；补一次后出口={ok_twice}")
        check(
            ok_once,
            "🔒 背靠背两帧（触屏形态）⇒ **一次点击即出口** —— 说明悬停帧的到达早于按下帧"
            "至少一个脚本圈（HTTP 往返 > `AWAIT 1` 的 1ms）",
        )
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()
        shutil.rmtree(temp_dir, ignore_errors=True)


def main():
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to EraCore.Cli binary")
    args = parser.parse_args()

    print("--- Web 输入帧 → 脚本侧可观测值（COMF413 等价探针）---")
    run_probe(args.binary)
    print("--- 同帧（悬停 + 按下挤在一起）---")
    run_same_frame(args.binary)
    print("--- 背靠背（触屏 tap 形态）---")
    run_back_to_back(args.binary)

    print(f"\n=== Web input→script test: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
