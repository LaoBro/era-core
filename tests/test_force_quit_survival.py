"""I-11 回归测试：脚本请求退出后 server 存活并能重启 session。

覆盖两条退出路径：

1. @QUIT 系统命令 → ConsoleInputHandler.DoSystemCommand → HeadlessConsole.Close()
   修复前：Environment.Exit(0) 直接杀进程，server 死亡，后续请求连接失败
   修复后：抛 GameExitException，被 StepAsync 的 catch(Exception) 捕获后调 Stop()，
           RunLoopAsync 下一轮退出，Session.GameLoopAsync 走 finally 清理。

2. FORCE_QUIT ERB 指令 → ConsoleStateManager.ForceQuit → HeadlessConsole.ExitApplication()
   修复前：Environment.Exit(0) 直接杀进程
   修复后：抛 GameExitException。但 Process.DoScript 的 catch(Exception) 会吃掉
           异常导致僵尸 session，故加 #if HEADLESS rethrow 让异常穿透到 StepAsync。

前置修复（I-11 测试发现）：
- AgentProtocolBase.DispatchInput 原先对 IntValue/AnyValue 输入做 long.TryParse
  预校验，@QUIT 因非数字被拒绝，永远到不了 PressEnterKey → DoSystemCommand。
  修复：在 switch 前拦截 @ 前缀，直接走 PressEnterKey。
"""
import json
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import TEST_GAME_DIR, copy_test_game_with_erb, start_server, turn_state

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


def wait_session_ended(server, timeout=10):
    """轮询 GET /state 直到 isRunning=false，返回是否在超时内结束。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        s, b = server.get_state()
        if s == 200 and json.loads(b).get("isRunning") is False:
            return True
        time.sleep(0.3)
    return False


def test_at_quit_survival():
    """@QUIT 系统命令 → _ui.Close() → GameExitException → session 终结 → server 存活。"""
    print("\n--- Test: @QUIT survival ---")
    server = None
    try:
        server = start_server(str(TEST_GAME_DIR))

        # 1. T-025：load-game 建立会话，等游戏进入 WaitInput
        s, _ = server.load_game(str(TEST_GAME_DIR))
        check(s == 200, f"first POST /load-game returns 200, got {s}")

        s, body = server.get_turn(timeout=10)
        check(s == 200, f"initial GET /turn returns 200, got {s}")
        check(turn_state(json.loads(body)) == "WaitInput",
              f"initial state is WaitInput (awaiting.input), got {turn_state(json.loads(body))}")

        # 2. 提交 @QUIT → DoSystemCommand → _ui.Close() → GameExitException
        s, _ = server.post_input("@QUIT")
        check(s == 200, f"POST /input @QUIT returns 200, got {s}")

        # 3. 拿到退出后的 turn（error turn 或 final turn）
        s, _ = server.get_turn(timeout=10)
        check(s in (200, 404), f"GET /turn after @QUIT returns 200 or 404, got {s}")

        # 4. 核心断言：server 仍存活，session 走完 finally（isRunning=false）
        ended = wait_session_ended(server)
        check(ended, "session fully ended after @QUIT (isRunning=false, server alive)")

        # 5. 能重启 session（验证 GlobalStatic.Reset 生效）——T-025 后用 load_game 重建
        s, _ = server.load_game(str(TEST_GAME_DIR))
        check(s == 200, f"POST /load-game after @QUIT returns 200 (Reset worked), got {s}")

        # 6. 第二个 session 能正常进入 WaitInput
        s, body = server.get_turn(timeout=10)
        check(s == 200, f"second session GET /turn returns 200, got {s}")
        if s == 200:
            check(turn_state(json.loads(body)) == "WaitInput",
                  f"second session state is WaitInput (awaiting.input), got {turn_state(json.loads(body))}")
    finally:
        if server is not None:
            server.delete_session()
            server.close()


def test_force_quit_survival():
    """FORCE_QUIT ERB 指令 → _ui.ExitApplication() → GameExitException → session 终结。"""
    print("\n--- Test: FORCE_QUIT survival ---")
    temp_dir = None
    server = None
    try:
        # 自定义 ERB：输入任意整数后执行 FORCE_QUIT
        erb = "@SYSTEM_TITLE\nPRINTL ForceQuit Test\nPRINTL [0] Go\nINPUT\nFORCE_QUIT\n"
        temp_dir, game_dir = copy_test_game_with_erb(erb)

        server = start_server(game_dir)

        # 1. T-025：load-game 建立会话，等游戏进入 WaitInput
        s, _ = server.load_game(game_dir)
        check(s == 200, f"first POST /load-game returns 200, got {s}")

        s, body = server.get_turn(timeout=10)
        check(s == 200, f"initial GET /turn returns 200, got {s}")
        check(turn_state(json.loads(body)) == "WaitInput",
              f"initial state is WaitInput (awaiting.input), got {turn_state(json.loads(body))}")

        # 2. 提交 "0" → 脚本继续执行 FORCE_QUIT → _ui.ExitApplication() → GameExitException
        s, _ = server.post_input("0")
        check(s == 200, f"POST /input 0 returns 200, got {s}")

        # 3. 拿到退出后的 turn
        s, _ = server.get_turn(timeout=10)
        check(s in (200, 404), f"GET /turn after FORCE_QUIT returns 200 or 404, got {s}")

        # 4. 核心断言：server 仍存活，session 走完 finally（isRunning=false）
        ended = wait_session_ended(server)
        check(ended, "session fully ended after FORCE_QUIT (isRunning=false, server alive)")

        # 5. 能重启 session（验证 GlobalStatic.Reset 生效）——T-025 后用 load_game 重建
        s, _ = server.load_game(game_dir)
        check(s == 200, f"POST /load-game after FORCE_QUIT returns 200 (Reset worked), got {s}")

        # 6. 第二个 session 能正常进入 WaitInput
        s, body = server.get_turn(timeout=10)
        check(s == 200, f"second session GET /turn returns 200, got {s}")
        if s == 200:
            check(turn_state(json.loads(body)) == "WaitInput",
                  f"second session state is WaitInput (awaiting.input), got {turn_state(json.loads(body))}")
    finally:
        if server is not None:
            server.delete_session()
            server.close()
        if temp_dir is not None:
            shutil.rmtree(temp_dir, ignore_errors=True)


test_at_quit_survival()
test_force_quit_survival()

print(f"\n=== I-11 survival tests: {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
