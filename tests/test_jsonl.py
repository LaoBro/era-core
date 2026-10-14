"""Test: JSONL protocol v12 turn structure (diff model + awaiting) via server mode.

Verifies:
1. Initial turn has protocolVersion: 12 and diff: null (first turn, no previous snapshot).
2. v1 fields (text, buttons) are NOT present; v12 收缩后旧顶层字段（state / inputType /
   needValue / timeLimit / displayTime / timeUpMessage / timedOut）同样不出现。
3. awaiting 是唯一契约字段：WaitInput ↔ awaiting.input，Quit/Error ↔ awaiting.ended。
4. Step turns carry diff.lineOps with append/clear_line_diff/clear_screen ops.
5. append ops carry newLines[] with entries[].segments[] (per-segment style) + optional button.
6. clear_line_diff ops carry clearCount (int); clear_screen has no fields.
7. Step turns have no protocolVersion field.
8. Final turn has no protocolVersion.

plan C (v5): migrated from v4 truncate/replace_all to clear_line_diff/clear_screen.
- First-turn content checks use GET /snapshot (diff is null on first turn).
- Step-turn content checks use diff_text(turn) (extracts text from diff.lineOps).

Usage:
    python test_jsonl.py
    python test_jsonl.py --binary path/to/EraCore.Cli.exe
    python test_jsonl.py --game-dir test_game
"""
import argparse
import json
import os
import sys
from pathlib import Path

_project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _project_dir)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from emuera_server import (
    PROTOCOL_VERSION,
    TEST_GAME_DIR,
    diff_text,
    snapshot_text,
    start_server,
    turn_state,
)


VALID_LINE_OP_TYPES = {"append", "clear_line_diff", "clear_screen"}
VALID_ALIGN = {"left", "center", "right"}


def check_diff(turn, passed, failed, turn_name):
    """Validate diff.lineOps structure for a turn. First-turn/no-op turns (diff=null) skip."""
    diff = turn.get("diff")
    if diff is None:
        return  # first turn or no-op: nothing to validate

    line_ops = diff.get("lineOps", [])
    check(isinstance(line_ops, list), f"{turn_name} diff.lineOps is a list", passed, failed)

    for i, op in enumerate(line_ops):
        check("type" in op, f"{turn_name} lineOp[{i}] has type field", passed, failed)
        check(op["type"] in VALID_LINE_OP_TYPES,
              f"{turn_name} lineOp[{i}] type is valid: {op['type']}", passed, failed)

        if op["type"] == "append":
            check("newLines" in op and isinstance(op["newLines"], list),
                  f"{turn_name} append lineOp[{i}] has newLines list", passed, failed)
            for j, line in enumerate(op.get("newLines", [])):
                check("entries" in line and isinstance(line["entries"], list) and len(line["entries"]) > 0,
                      f"{turn_name} append lineOp[{i}] newLines[{j}] has non-empty entries[]", passed, failed)
                for k, entry in enumerate(line.get("entries", [])):
                    check("segments" in entry and isinstance(entry["segments"], list) and len(entry["segments"]) > 0,
                          f"{turn_name} append lineOp[{i}] newLines[{j}] entry[{k}] has non-empty segments[]", passed, failed)
                    for s, seg in enumerate(entry.get("segments", [])):
                        check("text" in seg and isinstance(seg["text"], str),
                              f"{turn_name} append lineOp[{i}] newLines[{j}] entry[{k}] segment[{s}] has text", passed, failed)
                    if "button" in entry:
                        btn = entry["button"]
                        check("value" in btn, f"{turn_name} append lineOp[{i}] entry[{k}] button has value", passed, failed)
                        check("isInteger" in btn, f"{turn_name} append lineOp[{i}] entry[{k}] button has isInteger", passed, failed)
                        check("col" in btn and isinstance(btn["col"], int),
                              f"{turn_name} append lineOp[{i}] entry[{k}] button has int col (geometry)", passed, failed)
                        check("width" in btn and isinstance(btn["width"], int),
                              f"{turn_name} append lineOp[{i}] entry[{k}] button has int width (geometry)", passed, failed)
            if "align" in op:
                check(op["align"] in VALID_ALIGN,
                      f"{turn_name} append lineOp[{i}] align is valid: {op['align']}", passed, failed)

        elif op["type"] == "clear_line_diff":
            check("clearCount" in op and isinstance(op["clearCount"], int),
                  f"{turn_name} clear_line_diff lineOp[{i}] has int clearCount", passed, failed)

        elif op["type"] == "clear_screen":
            # clear_screen 无附带字段（其后若有 append 则携带 newLines）
            pass


def check(condition, message, passed, failed):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--suite-name", default="JSONL v12 diff+awaiting", help="Test suite name shown in the summary")
    args = parser.parse_args()

    passed = [0]
    failed = [0]

    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = _project_dir / game_dir

    server = None
    try:
        server = start_server(str(game_dir), binary=args.binary)

        # T-025：POST /load-game 建立会话（空闲态 POST /session 返 503）。
        s, body = server.load_game(str(game_dir))
        check(s == 200, f"POST /load-game returns 200, got {s}", passed, failed)
        check(json.loads(body).get("state") is not None, "load-game response includes state", passed, failed)

        # Turn 1: @SYSTEM_TITLE shows initial menu. First turn: diff is null (no previous snapshot).
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"initial GET /turn returns 200, got {s}", passed, failed)
        turn1 = json.loads(body)
        diff1 = turn1.get("diff")
        lineops1_count = len(diff1.get("lineOps", [])) if diff1 else 0
        print(f"Turn 1: state={turn_state(turn1)} inputType={turn1.get('awaiting', {}).get('input', {}).get('type')} diff.lineOps={lineops1_count}")
        check(turn_state(turn1) == "WaitInput", "Turn 1 is WaitInput (awaiting.input)", passed, failed)
        check("text" not in turn1, "Turn 1 has no text field (v2)", passed, failed)
        check("buttons" not in turn1, "Turn 1 has no buttons field (v2)", passed, failed)
        check("ops" not in turn1, "Turn 1 has no ops field (v5: removed)", passed, failed)
        for legacy in ("state", "inputType", "needValue", "timeLimit", "displayTime", "timeUpMessage", "timedOut"):
            check(legacy not in turn1, f"Turn 1 has no legacy field {legacy} (v12: removed)", passed, failed)
        check(turn1.get("protocolVersion") == PROTOCOL_VERSION, f"Turn 1 has protocolVersion == {PROTOCOL_VERSION}", passed, failed)
        check(diff1 is None, "Turn 1 diff is null (first turn, no previous snapshot)", passed, failed)
        check_diff(turn1, passed, failed, "Turn 1")

        # First-turn content: diff is null → fetch snapshot for content checks
        s, snap_body = server.get_snapshot(timeout=10)
        check(s == 200, f"GET /snapshot for Turn 1 content returns 200, got {s}", passed, failed)
        snap1 = json.loads(snap_body)
        check("Agent Test Start" in snapshot_text(snap1),
              "Turn 1 snapshot contains 'Agent Test Start'", passed, failed)
        check("You entered" not in snapshot_text(snap1),
              "Turn 1 snapshot has not processed an input yet", passed, failed)

        # Turn 2: select [0] Hello → shows second menu. Step turn: diff carries append ops.
        s, _ = server.post_input("0")
        check(s == 200, f"POST /input returns 200, got {s}", passed, failed)
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"GET /turn after input returns 200, got {s}", passed, failed)
        turn2 = json.loads(body)
        diff2 = turn2.get("diff")
        lineops2_count = len(diff2.get("lineOps", [])) if diff2 else 0
        print(f"\nTurn 2: state={turn_state(turn2)} inputType={turn2.get('awaiting', {}).get('input', {}).get('type')} diff.lineOps={lineops2_count}")
        check(turn_state(turn2) == "WaitInput", "Turn 2 is WaitInput (awaiting.input)", passed, failed)
        check("text" not in turn2, "Turn 2 has no text field (v2)", passed, failed)
        check("buttons" not in turn2, "Turn 2 has no buttons field (v2)", passed, failed)
        check("protocolVersion" not in turn2, "Turn 2 has no protocolVersion field", passed, failed)
        check_diff(turn2, passed, failed, "Turn 2")

        check("You entered: 0" in diff_text(turn2), "Turn 2 diff shows first input result", passed, failed)

        # Turn 3: select [0] World → game ends.
        s, _ = server.post_input("0")
        check(s == 200, f"POST /input returns 200, got {s}", passed, failed)
        s, body = server.get_turn(timeout=15)
        check(s == 200, f"GET /turn after input returns 200, got {s}", passed, failed)
        turn3 = json.loads(body)
        diff3 = turn3.get("diff")
        lineops3_count = len(diff3.get("lineOps", [])) if diff3 else 0
        print(f"\nTurn 3: state={turn_state(turn3)} inputType={turn3.get('awaiting', {}).get('input', {}).get('type')} diff.lineOps={lineops3_count}")
        check("text" not in turn3, "Turn 3 has no text field (v2)", passed, failed)
        check("buttons" not in turn3, "Turn 3 has no buttons field (v2)", passed, failed)
        check("protocolVersion" not in turn3, "Turn 3 has no protocolVersion field", passed, failed)
        check_diff(turn3, passed, failed, "Turn 3")

        check("You entered: 0" in diff_text(turn3), "Turn 3 diff shows second input result", passed, failed)
        check("Agent Test End" in diff_text(turn3), "Turn 3 diff shows end message", passed, failed)
        check(turn_state(turn3) == "Quit", "Turn 3 state is Quit (awaiting.ended.terminal)", passed, failed)
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()

    print(f"\n=== {args.suite_name}: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())


