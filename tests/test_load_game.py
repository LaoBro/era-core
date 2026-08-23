"""Test: POST /load-game / GET /state gameDir / POST /native/pick-directory (issue 05)。

Verifies:
1. POST /native/pick-directory → 200 stub `{platform:"web", supported:false, message}`
2. GET /state 无 session 时包含 gameDir 字段（server 启动 --ExeDir 的初始值）
3. POST /load-game 不存在路径 → 400 `{error:{code:"DIR_NOT_FOUND",message}}`
4. POST /load-game 缺 csv 目录 → 400 `{error:{code:"MISSING_CSV",message}}`
5. POST /load-game 缺 erb 目录 → 400 `{error:{code:"MISSING_ERB",message}}`
6. POST /load-game 有效 test_game 路径 → 200 `{sessionId, state, gameDir}`
7. /load-game 后 GET /turn 能拿到 turn（隐式建了 session）
8. /load-game 后 GET /state 返回新 gameDir
9. /load-game 错误时旧 session 不丢（路径级错误不拆旧）

Usage:
    python test_load_game.py
    python test_load_game.py --binary path/to/EraCore.Cli.exe
    python test_load_game.py --game-dir test_game
"""
import argparse
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path

_project_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, _project_dir)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from emuera_server import TEST_GAME_DIR, start_server


passed = [0]
failed = [0]


def check(condition, message):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def make_partial_game_dir(remove_subdir):
    """复制 test_game 后删掉指定子目录（'csv' 或 'erb'），构造"缺子目录"测试夹具。"""
    tmp = Path(tempfile.mkdtemp(prefix="emuera_load_game_"))
    game = tmp / "game"
    shutil.copytree(TEST_GAME_DIR, game)
    shutil.rmtree(game / remove_subdir)
    return str(game), tmp


def norm(p):
    """规范化路径用于比较（C# 的 Path 保留尾部斜杠，Python 不）。"""
    return os.path.normpath(str(p))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--suite-name", default="load-game endpoints", help="Test suite name")
    args = parser.parse_args()

    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = _project_dir / game_dir

    tmp_dirs = []
    server = None
    try:
        server = start_server(str(game_dir), binary=args.binary)

        # --- Test 1: POST /native/pick-directory → 200 stub ---
        print("\n[1] POST /native/pick-directory → 200 stub")
        s, body = server.request("POST", "/native/pick-directory")
        check(s == 200, f"pick-directory returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(obj.get("platform") == "web", f"platform=='web', got {obj.get('platform')}")
            check(obj.get("supported") is False, f"supported is false, got {obj.get('supported')}")
            check("message" in obj and isinstance(obj["message"], str),
                  f"message is string: {obj.get('message')!r}")

        # --- Test 2: GET /state 无 session 时 gameDir 字段 = null（T-025 D5）---
        # T-025 D5：空闲态 gameDir 显式置 null（前端据此可靠判 idle 并展示选择器）。
        # 旧断言 norm(gameDir) == norm(game_dir) 已推翻——即使带了 --ExeDir valid_dir，
        # idle 态仍返 gameDir: null（"已加载游戏的目录"与 GamePaths.Current 内部路径是两个概念）。
        print("\n[2] GET /state no session → gameDir == null (T-025 D5)")
        s, body = server.get_state()
        check(s == 200, f"GET /state returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check("gameDir" in obj, f"state has gameDir field")
            check(obj.get("gameDir") is None,
                  f"gameDir is None (idle state, T-025 D5), got {obj.get('gameDir')!r}")
            check(obj.get("state") == "Idle", f"state==Idle (no session), got {obj.get('state')}")
            # Issue 12：windowWidth / fontSize / lineHeight / gameColumns / fontName 五个布局元信息字段
            check("windowWidth" in obj and isinstance(obj["windowWidth"], int),
                  f"state has int windowWidth: {obj.get('windowWidth')!r}")
            check("fontSize" in obj and isinstance(obj["fontSize"], int),
                  f"state has int fontSize: {obj.get('fontSize')!r}")
            check("lineHeight" in obj and isinstance(obj["lineHeight"], int),
                  f"state has int lineHeight: {obj.get('lineHeight')!r}")
            check("gameColumns" in obj and isinstance(obj["gameColumns"], int),
                  f"state has int gameColumns: {obj.get('gameColumns')!r}")
            check("fontName" in obj and isinstance(obj["fontName"], str),
                  f"state has str fontName: {obj.get('fontName')!r}")
            # 默认值校验——test_game 无 emuera.config，应为 ConfigData 默认 760/18/19
            # gameColumns = (760 - max(2, 18/6=3)) / max(18/2, 1) = (760-3)/9 = 757/9 = 84
            # fontName 默认 "ＭＳ ゴシック"（ConfigData.SetDefault）
            check(obj.get("windowWidth") == 760,
                  f"windowWidth==760 (default), got {obj.get('windowWidth')}")
            check(obj.get("fontSize") == 18,
                  f"fontSize==18 (default), got {obj.get('fontSize')}")
            check(obj.get("lineHeight") == 19,
                  f"lineHeight==19 (default), got {obj.get('lineHeight')}")
            check(obj.get("gameColumns") == 84,
                  f"gameColumns==84 (default, (760-3)/9), got {obj.get('gameColumns')}")
            check(obj.get("fontName") == "ＭＳ ゴシック",
                  f"fontName=='ＭＳ ゴシック' (default), got {obj.get('fontName')!r}")
            # visibleRows = 默认 WindowY 480 / LineHeight 19 = 25（整除向下）
            check(obj.get("visibleRows") == 25,
                  f"visibleRows==25 (default, 480/19), got {obj.get('visibleRows')!r}")

        # --- Test 3: POST /load-game 不存在路径 → 400 DIR_NOT_FOUND ---
        print("\n[3] POST /load-game 不存在路径 → 400 DIR_NOT_FOUND")
        bogus = os.path.join(tempfile.gettempdir(), "emuera_nonexistent_dir_xyz_9999")
        s, body = server.request("POST", "/load-game", {"gameDir": bogus})
        check(s == 400, f"load-game bogus path returns 400, got {s}")
        if s == 400:
            obj = json.loads(body)
            err = obj.get("error", {})
            check(err.get("code") == "DIR_NOT_FOUND",
                  f"error.code==DIR_NOT_FOUND, got {err.get('code')!r}")
            check("message" in err, "error.message present")

        # --- Test 4: POST /load-game 缺 csv 目录 → 400 MISSING_CSV ---
        print("\n[4] POST /load-game 缺 csv 目录 → 400 MISSING_CSV")
        no_csv_game, tmp_no_csv = make_partial_game_dir("csv")
        tmp_dirs.append(tmp_no_csv)
        s, body = server.request("POST", "/load-game", {"gameDir": no_csv_game})
        check(s == 400, f"load-game no-csv returns 400, got {s}")
        if s == 400:
            obj = json.loads(body)
            err = obj.get("error", {})
            check(err.get("code") == "MISSING_CSV",
                  f"error.code==MISSING_CSV, got {err.get('code')!r}")

        # --- Test 5: POST /load-game 缺 erb 目录 → 400 MISSING_ERB ---
        print("\n[5] POST /load-game 缺 erb 目录 → 400 MISSING_ERB")
        no_erb_game, tmp_no_erb = make_partial_game_dir("erb")
        tmp_dirs.append(tmp_no_erb)
        s, body = server.request("POST", "/load-game", {"gameDir": no_erb_game})
        check(s == 400, f"load-game no-erb returns 400, got {s}")
        if s == 400:
            obj = json.loads(body)
            err = obj.get("error", {})
            check(err.get("code") == "MISSING_ERB",
                  f"error.code==MISSING_ERB, got {err.get('code')!r}")

        # --- Test 6: POST /load-game 有效 test_game 路径 → 200 + 结构 ---
        print("\n[6] POST /load-game 有效路径 → 200 + {sessionId, state, gameDir}")
        s, body = server.request("POST", "/load-game", {"gameDir": str(game_dir)})
        check(s == 200, f"load-game valid returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check("sessionId" in obj and isinstance(obj["sessionId"], str),
                  f"has string sessionId: {obj.get('sessionId')!r}")
            check("state" in obj and isinstance(obj["state"], str),
                  f"has string state: {obj.get('state')!r}")
            check(norm(obj.get("gameDir")) == norm(game_dir),
                  f"gameDir=={game_dir}, got {obj.get('gameDir')!r}")

        # --- Test 7: /load-game 后 GET /turn 能拿到 turn（隐式建了 session）---
        print("\n[7] /load-game 后 GET /turn → 200")
        s, turn_body = server.get_turn(timeout=20)
        check(s == 200, f"GET /turn after load-game returns 200, got {s}")
        if s == 200:
            turn = json.loads(turn_body)
            # protocolVersion 不固定具体值——TurnRecord 协议会演进（ADR-0016 v7）
            check("protocolVersion" in turn and isinstance(turn["protocolVersion"], int),
                  f"has int protocolVersion: {turn.get('protocolVersion')}")
            check(turn.get("state") == "WaitInput",
                  f"turn state==WaitInput, got {turn.get('state')}")

        # --- Test 8: /load-game 后 GET /state 返回新 gameDir + isRunning ---
        print("\n[8] /load-game 后 GET /state → gameDir + isRunning")
        s, body = server.get_state()
        check(s == 200, f"GET /state returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(norm(obj.get("gameDir")) == norm(game_dir),
                  f"gameDir=={game_dir}, got {obj.get('gameDir')!r}")
            check(obj.get("isRunning") is True,
                  f"isRunning==true, got {obj.get('isRunning')}")
            check(obj.get("state") == "WaitInput",
                  f"state==WaitInput, got {obj.get('state')}")
            # Issue 12：active session 分支也应携带布局元信息字段（含 gameColumns + fontName）
            check("windowWidth" in obj and isinstance(obj["windowWidth"], int),
                  f"active state has int windowWidth: {obj.get('windowWidth')!r}")
            check("fontSize" in obj and isinstance(obj["fontSize"], int),
                  f"active state has int fontSize: {obj.get('fontSize')!r}")
            check("lineHeight" in obj and isinstance(obj["lineHeight"], int),
                  f"active state has int lineHeight: {obj.get('lineHeight')!r}")
            check("gameColumns" in obj and isinstance(obj["gameColumns"], int),
                  f"active state has int gameColumns: {obj.get('gameColumns')!r}")
            check("fontName" in obj and isinstance(obj["fontName"], str),
                  f"active state has str fontName: {obj.get('fontName')!r}")

        # --- Test 9: 路径级错误时旧 session 不丢 + Current 不被污染 ---
        # 此时已有 session（test_game 隐式建的），尝试 load-game 一个 bogus 路径，
        # 应返 400 且不拆旧 session——后续 GET /turn 仍能拿到旧 turn。
        # spec L229 要求"拆旧前拦路径级错误"——server 状态不应被失败的 /load-game 改动，
        # 故 GET /state 的 gameDir 应仍是旧值（test_game 路径），不是被拒绝的 bogus 路径。
        print("\n[9] /load-game 路径级错误不拆旧 session + Current 不污染")
        # 此时 turn 已经被 Test 7 取走，需先 post_input 让游戏进入下一回合
        server.post_input("0")
        bogus2 = os.path.join(tempfile.gettempdir(), "emuera_nonexistent_dir_xyz_9999_b")
        s, body = server.request("POST", "/load-game", {"gameDir": bogus2})
        check(s == 400, f"load-game bogus path returns 400, got {s}")
        # 验证 GET /state 的 gameDir 仍是旧值——校验失败回滚 Current 修复后的不变性
        s, state_body = server.get_state()
        check(s == 200, f"GET /state after failed load-game returns 200, got {s}")
        if s == 200:
            state_obj = json.loads(state_body)
            check(norm(state_obj.get("gameDir")) == norm(game_dir),
                  f"gameDir 滚回到旧值 {game_dir}, got {state_obj.get('gameDir')!r}")
        # 旧 session 应仍存活——GET /turn 应能拿到下一回合
        s, turn_body = server.get_turn(timeout=20)
        check(s == 200, f"GET /turn after failed load-game still 200 (old session alive), got {s}")
        if s == 200:
            turn = json.loads(turn_body)
            check(turn.get("state") in ("WaitInput", "Quit", "Error"),
                  f"old turn state is valid, got {turn.get('state')}")

    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()
        for tmp in tmp_dirs:
            try:
                shutil.rmtree(tmp, ignore_errors=True)
            except Exception:
                pass

    print(f"\n=== {args.suite_name}: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
