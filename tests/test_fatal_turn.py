"""Test: Fatal turn structure on script runtime exception.

Verifies that when an ERB script throws (THROW), the server returns a turn
with a proper error structure.

Note: ERB THROW 抛出的 CodeEE 被 Process.DoScript() 内部 catch 捕获并交由
handleException 处理（Process.cs:345），不会传播到 AgentJsonlProtocol.StepAsync
的 catch 块。StepAsync catch 是防御性兜底，仅捕获 Process 未预料的 C# 异常（如 NRE）。
因此此测试验证的是 THROW 被 DoScript 处理后的 Error 状态 turn，而非 StepAsync catch
的 fatal turn（diff=null / error=ex.Message）。

The test verifies:
- Initial turn has protocolVersion == 7 and diff is null (first turn)
- After THROW, the turn has state=Error with error text in diff.lineOps
- protocolVersion is absent from non-initial turns

plan C (v5): migrated from v4 truncate/replace_all to clear_line_diff/clear_screen.
- First-turn content checks use GET /snapshot (diff is null on first turn).
- Step-turn content checks use diff_text(turn).

Usage:
    python test_fatal_turn.py
    python test_fatal_turn.py --binary path/to/EraCore.Cli.exe
    python test_fatal_turn.py --game-dir test_game
"""
import argparse
import json
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import PROTOCOL_VERSION, copy_test_game_with_erb, diff_text, snapshot_text, start_server, turn_state


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()

    erb_script = """@SYSTEM_TITLE
PRINTL Fatal Turn Test
PRINTL [0] Trigger fatal
PRINTL [1] Quit
INPUT
IF RESULT == 0
    THROW "fatal-test-marker"
ELSEIF RESULT == 1
    QUIT
ENDIF
"""

    passed = [0]
    failed = [0]

    def check(condition, message):
        if condition:
            passed[0] += 1
            print(f"  PASS: {message}")
        else:
            failed[0] += 1
            print(f"  FAIL: {message}")

    server = None
    temp_dir = None
    try:
        temp_dir, game_dir = copy_test_game_with_erb(erb_script)
        server = start_server(game_dir, binary=args.binary)

        # T-025：POST /load-game 建立会话（空闲态 POST /session 返 503）。
        create_status, create_body = server.load_game(game_dir)
        check(create_status == 200, f"POST /load-game returns 200, got {create_status}")

        initial_status, initial_body = server.get_turn(timeout=15)
        check(initial_status == 200, f"initial GET /turn returns 200, got {initial_status}")
        initial = json.loads(initial_body)
        check(turn_state(initial) == "WaitInput", f"initial state is WaitInput (awaiting.input), got {turn_state(initial)}")
        check("text" not in initial, "initial turn has no text field (v2)")
        check("buttons" not in initial, "initial turn has no buttons field (v2)")
        check("ops" not in initial, "initial turn has no ops field (v5: removed)")
        check(initial.get("protocolVersion") == PROTOCOL_VERSION, f"Initial turn has protocolVersion == {PROTOCOL_VERSION}")
        check(initial.get("diff") is None, "initial turn diff is null (first turn)")
        # First-turn content: diff is null → fetch snapshot
        snap_status, snap_body = server.get_snapshot(timeout=10)
        check(snap_status == 200, f"GET /snapshot for initial content returns 200, got {snap_status}")
        check("Fatal Turn Test" in snapshot_text(json.loads(snap_body)),
              "initial snapshot contains 'Fatal Turn Test'")

        input_status, _ = server.post_input("0")
        check(input_status == 200, f"POST /input 0 returns 200, got {input_status}")

        deadline = time.time() + 30
        error_turn = None
        while time.time() < deadline:
            turn_status, turn_body = server.get_turn(timeout=10)
            check(turn_status == 200, f"GET /turn for error returns 200, got {turn_status}")
            turn = json.loads(turn_body)
            if turn_state(turn) == "Error" or ("fatal-test-marker" in diff_text(turn) and "THROW" in diff_text(turn)):
                error_turn = turn
                break
            time.sleep(0.5)
        else:
            check(False, "error turn received within 30s")

        if error_turn is not None:
            check("text" not in error_turn, "error turn has no text field (v2)")
            check("buttons" not in error_turn, "error turn has no buttons field (v2)")
            check("ops" not in error_turn, "error turn has no ops field (v5: removed)")
            check("fatal-test-marker" in diff_text(error_turn), "error turn diff contains 'fatal-test-marker'")
            check("THROW" in diff_text(error_turn), "error turn diff contains THROW error info")
            check(turn_state(error_turn) != "WaitInput", f"error turn state is not WaitInput (awaiting.ended), got {turn_state(error_turn)}")
            check("protocolVersion" not in error_turn, "error turn has no protocolVersion field")

    finally:
        if server is not None:
            server.delete_session()
        if server is not None:
            server.close()
        if temp_dir is not None:
            shutil.rmtree(temp_dir, ignore_errors=True)

    print(f"\n=== Fatal turn test: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())

