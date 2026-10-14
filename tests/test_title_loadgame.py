"""Test LOADGAME inside @SYSTEM_TITLE transitions to the load screen.

Guards against the regression fixed after the ADR-0011 ExecutionState refactor:
Process.state was an independent field that drifted from
executionState.CurrentState after saveCurrentState() cloned the state. As a
result, DoScript (reading the stale field) and ScriptProc/SystemProc (reading
CurrentState) saw different ProcessState objects, so LOADGAME never handed over
to the system process: the script looped back to the title menu (first click
"no response"), and a second LOADGAME threw "cannot use command".

Driven flow:
1. @SYSTEM_TITLE prints a menu and waits for INPUT
2. select [1] (LOADGAME) -> must enter the load screen: slot list with empty
   slots ("----") and a [100] back entry, state=WaitInput, NOT Error
3. select [100] (back) -> LoadPrevState restores @SYSTEM_TITLE, script
   continues past LOADGAME ("After LoadGame Return")
"""
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, diff_text, snapshot_text, start_server, turn_state

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


ERB_TEXT = """@SYSTEM_TITLE
PRINTL TitleLoadGame Test
PRINTL [0] Hello
PRINTL [1] LoadGame
INPUT
IF RESULT == 0
	PRINTL You chose 0
ELSEIF RESULT == 1
	LOADGAME
ENDIF
PRINTL After LoadGame Return
QUIT
"""

server = None
temp_dir = None
try:
    temp_dir, game_dir = copy_test_game_with_erb(ERB_TEXT)
    server = start_server(game_dir)

    create_status, create_body = server.load_game(game_dir)
    check(create_status == 200, f"POST /load-game returns 200, got {create_status}")

    # First turn: title menu is displayed and waits for input.
    initial_status, initial_body = server.get_turn(timeout=10)
    check(initial_status == 200, f"initial GET /turn returns 200, got {initial_status}")
    initial = json.loads(initial_body)
    check(turn_state(initial) == "WaitInput", f"initial state is WaitInput (awaiting.input), got {turn_state(initial)}")

    snap_status, snap_body = server.get_snapshot(timeout=10)
    check(snap_status == 200, f"GET /snapshot for title returns 200, got {snap_status}")
    check("TitleLoadGame Test" in snapshot_text(json.loads(snap_body)), "title menu is shown")

    # Select [1] LOADGAME -> must enter the load screen (not Error, not loop back to title).
    input_status, _ = server.post_input("1")
    check(input_status == 200, f"POST /input 1 returns 200, got {input_status}")

    turn_status, turn_body = server.get_turn(timeout=10)
    check(turn_status == 200, f"GET /turn after LOADGAME returns 200, got {turn_status}")
    turn = json.loads(turn_body)
    check(turn_state(turn) == "WaitInput",
          f"after LOADGAME state is WaitInput (load screen), got {turn_state(turn)}")
    check(turn_state(turn) != "Error", "after LOADGAME is NOT Error")

    # Load screen must show empty save slots ("----"). Snapshot is the full
    # screen, so the title menu may still be visible above the load screen —
    # the load screen itself is appended below without clearing.
    snap_status, snap_body = server.get_snapshot(timeout=10)
    check(snap_status == 200, f"GET /snapshot for load screen returns 200, got {snap_status}")
    load_screen = snapshot_text(json.loads(snap_body))
    check("----" in load_screen, "load screen shows empty save slots ('----')")

    # Select [100] back -> LoadPrevState restores @SYSTEM_TITLE, script continues past LOADGAME.
    input_status, _ = server.post_input("100")
    check(input_status == 200, f"POST /input 100 returns 200, got {input_status}")

    turn_status, turn_body = server.get_turn(timeout=10)
    check(turn_status == 200, f"GET /turn after back returns 200, got {turn_status}")
    turn = json.loads(turn_body)
    check("After LoadGame Return" in diff_text(turn),
          "script continues after LOADGAME once load screen is cancelled")

finally:
    if server is not None:
        try:
            server.delete_session()
        except Exception:
            pass
        server.close()
    if temp_dir is not None:
        try:
            shutil.rmtree(temp_dir, ignore_errors=True)
        except Exception:
            pass

print(f"\n=== title LOADGAME regression test: {passed} passed, {failed} failed ===")
sys.exit(1 if failed else 0)
