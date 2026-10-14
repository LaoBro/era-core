"""Test: GET /snapshot endpoint — DisplaySnapshot 全量快照（ADR-0013 决策一）。

Verifies:
1. No active session → 404 with error message.
2. Active session → 200 with DisplaySnapshot JSON structure:
   - lines[] (each with entries[], align, isLineEnd)
   - bgColor (string|null), awaiting (object: input/wait/ended, v12 起取代 state/inputType/needValue)
   - protocolVersion == 12
3. Snapshot content matches turn diff content (after applying diff, awaiting matches snapshot).
4. Button geometry (col/width) present in snapshot entries.
5. Session ended (Quit) → 200 with awaiting.ended.terminal=="Quit" (not 404).

plan C (v5): migrated from v4 truncate/replace_all to clear_line_diff/clear_screen.
- First turn: diff is null → snapshot is the sole source of truth; compare snapshot with itself (trivially true).
- Step turns: diff_text(turn) extracts incremental text from diff.lineOps; snapshot_text(snap) extracts full state text.
  The diff text should be a subset of (or equal to) the snapshot text (diff carries only the new/changed lines).

Usage:
    python test_snapshot.py
    python test_snapshot.py --binary path/to/EraCore.Cli.exe
    python test_snapshot.py --game-dir test_game
"""
import argparse
import json
import os
import sys
import time
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


VALID_ALIGN = {"left", "center", "right"}


def check(condition, message, passed, failed):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def validate_snapshot_structure(snap, passed, failed, label="snapshot"):
    """Validate DisplaySnapshot JSON structure field by field."""
    check("lines" in snap and isinstance(snap["lines"], list),
          f"{label} has lines[] list", passed, failed)
    # v12（issue 09）：state / inputType / needValue 退场，唯一契约字段是 awaiting
    check("awaiting" in snap and isinstance(snap["awaiting"], dict),
          f"{label} has awaiting object", passed, failed)
    for legacy in ("state", "inputType", "needValue", "timeLimit", "displayTime", "timeUpMessage", "timedOut"):
        check(legacy not in snap, f"{label} has no legacy field {legacy} (v12: removed)", passed, failed)
    check("protocolVersion" in snap and snap["protocolVersion"] == PROTOCOL_VERSION,
          f"{label} protocolVersion == {PROTOCOL_VERSION}", passed, failed)

    for i, line in enumerate(snap.get("lines", [])):
        check("entries" in line and isinstance(line["entries"], list),
              f"{label} line[{i}] has entries[] list", passed, failed)
        check("isLineEnd" in line and isinstance(line["isLineEnd"], bool),
              f"{label} line[{i}] has isLineEnd bool", passed, failed)
        if "align" in line:
            check(line["align"] in VALID_ALIGN,
                  f"{label} line[{i}] align valid: {line['align']}", passed, failed)

        for j, entry in enumerate(line.get("entries", [])):
            check("segments" in entry and isinstance(entry["segments"], list) and len(entry["segments"]) > 0,
                  f"{label} line[{i}] entry[{j}] has non-empty segments[]", passed, failed)
            for k, seg in enumerate(entry.get("segments", [])):
                check("text" in seg and isinstance(seg["text"], str),
                      f"{label} line[{i}] entry[{j}] segment[{k}] has text", passed, failed)
            if "button" in entry:
                btn = entry["button"]
                check("value" in btn, f"{label} line[{i}] entry[{j}] button has value", passed, failed)
                check("isInteger" in btn, f"{label} line[{i}] entry[{j}] button has isInteger", passed, failed)
                check("col" in btn and isinstance(btn["col"], int),
                      f"{label} line[{i}] entry[{j}] button has int col (geometry)", passed, failed)
                check("width" in btn and isinstance(btn["width"], int),
                      f"{label} line[{i}] entry[{j}] button has int width (geometry)", passed, failed)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--suite-name", default="GET /snapshot endpoint", help="Test suite name")
    args = parser.parse_args()

    passed = [0]
    failed = [0]

    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = _project_dir / game_dir

    server = None
    try:
        server = start_server(str(game_dir), binary=args.binary)

        # --- Test 1: GET /snapshot without session → 404 ---
        print("\n[1] GET /snapshot without session → 404")
        status, body = server.get_snapshot()
        check(status == 404, f"no-session snapshot returns 404, got {status}", passed, failed)
        err_body = json.loads(body)
        check("error" in err_body, "404 body has error field", passed, failed)

        # --- Test 2: GET /snapshot with active session → 200 + full structure ---
        print("\n[2] GET /snapshot with active session → 200 + DisplaySnapshot")
        # T-025：POST /load-game 建立会话（空闲态 POST /session 返 503）。
        s, _ = server.load_game(str(game_dir))
        check(s == 200, f"POST /load-game returns 200, got {s}", passed, failed)

        # 先拿 initial turn，让游戏进入 WaitInput 稳定状态
        s, turn_body = server.get_turn(timeout=20)
        check(s == 200, f"initial GET /turn returns 200, got {s}", passed, failed)
        turn = json.loads(turn_body)
        check(turn.get("protocolVersion") == PROTOCOL_VERSION, f"initial turn protocolVersion == {PROTOCOL_VERSION}", passed, failed)

        # 现在 GET /snapshot
        status, snap_body = server.get_snapshot(timeout=10)
        check(status == 200, f"active-session snapshot returns 200, got {status}", passed, failed)
        snap = json.loads(snap_body)

        validate_snapshot_structure(snap, passed, failed, "active-snapshot")

        # awaiting 应与 turn 一致（都是 input 维度 → WaitInput）
        check(turn_state(snap) == turn_state(turn),
              f"snapshot awaiting matches turn awaiting: {turn_state(snap)}", passed, failed)
        check(turn_state(snap) == "WaitInput",
              f"snapshot awaiting implies WaitInput, got {turn_state(snap)}", passed, failed)

        # --- Test 3: Snapshot content matches turn diff content ---
        # Phase 5: first turn diff is null → snapshot is the sole source.
        # Verify snapshot contains expected initial content.
        print("\n[3] Snapshot content matches initial display (first turn diff is null)")
        snap_text = snapshot_text(snap)
        check("Agent Test Start" in snap_text,
              "snapshot lines contain 'Agent Test Start'", passed, failed)
        # First turn diff is null → diff_text returns empty; snapshot is authoritative
        check(diff_text(turn) == "",
              "first turn diff is null (diff_text returns empty)", passed, failed)

        # --- Test 4: Play a turn, then snapshot reflects updated state ---
        print("\n[4] Snapshot reflects state after input")
        server.post_input("0")
        s, turn2_body = server.get_turn(timeout=20)
        check(s == 200, f"GET /turn after input returns 200, got {s}", passed, failed)
        turn2 = json.loads(turn2_body)

        status, snap2_body = server.get_snapshot(timeout=10)
        check(status == 200, f"post-input snapshot returns 200, got {status}", passed, failed)
        snap2 = json.loads(snap2_body)
        validate_snapshot_structure(snap2, passed, failed, "post-input-snapshot")

        check("You entered: 0" in snapshot_text(snap2),
              "post-input snapshot contains 'You entered: 0'", passed, failed)
        # diff_text for step turn should carry the incremental text
        check("You entered: 0" in diff_text(turn2),
              "post-input turn diff contains 'You entered: 0'", passed, failed)

        # --- Test 5: Session ended → 200 + awaiting.ended.terminal=="Quit" ---
        print("\n[5] GET /snapshot after session ended → 200 + awaiting.ended=Quit")
        # test_game: [0]→[0]→Quit. 已输入 0 一次，再输入 0 应到 Quit
        server.post_input("0")
        # 等待 session 结束
        deadline = time.time() + 15
        ended = False
        while time.time() < deadline:
            _, state_body = server.get_state()
            state = json.loads(state_body)
            if state.get("state") in ("Quit", "Error"):
                ended = True
                break
            time.sleep(0.5)
            # 可能还有 turn 未取
            try:
                server.get_turn(timeout=2)
            except Exception:
                pass
        check(ended, "session reached Quit/Error state", passed, failed)

        status, snap3_body = server.get_snapshot(timeout=10)
        check(status == 200, f"ended-session snapshot returns 200 (not 404), got {status}", passed, failed)
        if status == 200:
            snap3 = json.loads(snap3_body)
            check(turn_state(snap3) in ("Quit", "Error"),
                  f"ended-session snapshot awaiting.ended is Quit/Error, got {turn_state(snap3)}", passed, failed)
            check(snap3.get("protocolVersion") == PROTOCOL_VERSION,
                  f"ended-session snapshot protocolVersion == {PROTOCOL_VERSION}", passed, failed)
            validate_snapshot_structure(snap3, passed, failed, "ended-snapshot")

        # --- Test 6: After DELETE /session → 404 ---
        print("\n[6] GET /snapshot after DELETE /session → 404")
        server.delete_session()
        status, body = server.get_snapshot()
        check(status == 404, f"post-delete snapshot returns 404, got {status}", passed, failed)

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



