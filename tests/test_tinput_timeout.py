import json
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import awaiting_input_type, awaiting_need_value, copy_test_game_with_erb, diff_text, snapshot_text, start_server, turn_state

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


server = None
temp_dir = None
try:
    temp_dir, game_dir = copy_test_game_with_erb(
        """@SYSTEM_TITLE
PRINTL TINPUT Timeout Test
TINPUT 300, 7, 0, "TIME UP", 0
PRINTFORML RESULT={RESULT}
INPUT
PRINTFORML INPUT RESULT={RESULT}
QUIT
"""
    )
    server = start_server(game_dir)

    # T-025：POST /load-game 建立会话（空闲态 POST /session 返 503）。
    create_status, create_body = server.load_game(game_dir)
    check(create_status == 200, f"POST /load-game returns 200, got {create_status}")
    session_id = json.loads(create_body)["sessionId"]

    initial_status, initial_body = server.get_turn(timeout=10)
    check(initial_status == 200, f"initial GET /turn returns 200, got {initial_status}")
    initial = json.loads(initial_body)
    check(turn_state(initial) == "WaitInput", f"initial state is WaitInput (awaiting.input), got {turn_state(initial)}")
    # Phase 5: first turn diff is null → fetch snapshot for content check
    snap_status, snap_body = server.get_snapshot(timeout=10)
    check(snap_status == 200, f"GET /snapshot for initial content returns 200, got {snap_status}")
    check("TINPUT Timeout Test" in snapshot_text(json.loads(snap_body)), "initial snapshot contains TINPUT test start")
    check(awaiting_input_type(initial) == "IntValue", f"initial inputType is IntValue (awaiting.input.type), got {awaiting_input_type(initial)}")
    check(awaiting_need_value(initial) is True, "initial request needs a value (awaiting.input present)")

    time.sleep(1.0)

    timeout_status, timeout_body = server.get_turn(timeout=10)
    check(timeout_status == 200, f"timeout GET /turn returns 200, got {timeout_status}")
    timeout_turn = json.loads(timeout_body)
    check("TIME UP" in diff_text(timeout_turn), "timeout turn diff contains TimeUpMes")
    check("RESULT=7" in diff_text(timeout_turn), "timeout turn diff executes default value 7")
    check(turn_state(timeout_turn) == "WaitInput", f"timeout advances to next WaitInput (awaiting.input), got {turn_state(timeout_turn)}")

    input_status, input_body = server.post_input("1")
    check(input_status == 200, f"input after timeout returns 200, got {input_status}")

    final_status, final_body = server.get_turn(timeout=10)
    check(final_status == 200, f"final GET /turn returns 200, got {final_status}")
    final_turn = json.loads(final_body)
    check("INPUT RESULT=1" in diff_text(final_turn), "later input is not swallowed or shifted")
    check(turn_state(final_turn) == "Quit", f"final state is Quit (awaiting.ended), got {turn_state(final_turn)}")

finally:
    if server is not None:
        server.delete_session()
    if server is not None:
        server.close()
    if temp_dir is not None:
        shutil.rmtree(temp_dir, ignore_errors=True)

print(f"\n=== TINPUT timeout test: {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
