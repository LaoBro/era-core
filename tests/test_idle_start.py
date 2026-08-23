"""Test: T-025 server 模式空闲启动（无预绑游戏目录）。

Verifies:
1. 无 --ExeDir 启动 server → GET /state 返 gameDir==null / state=="Idle"（D5）
2. 空闲态 POST /session → 503 {"error":"No game loaded"}（D4）
3. POST /load-game {合法目录} → GET /state 变有效 → POST /session 成功（D8/D10）
4. POST /load-game {非法目录} → 400 且 server 仍空闲（gameDir 仍 null，D7 回滚）
5. 快速重开路径：DELETE /session + POST /load-game 同目录 → 重新进入（D14）
6. 快速重开失败回退：DELETE /session + POST /load-game 非法目录 → 400 + server 回 idle

Usage:
    python test_idle_start.py
    python test_idle_start.py --binary path/to/EraCore.Cli.exe
    python test_idle_start.py --game-dir test_game
"""
import argparse
import json
import os
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


def norm(p):
    """规范化路径用于比较（C# 的 Path 保留尾部斜杠，Python 不）。"""
    return os.path.normpath(str(p))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--suite-name", default="idle start", help="Test suite name")
    args = parser.parse_args()

    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = _project_dir / game_dir

    server = None
    try:
        # === 无 --ExeDir 启动 server（T-025 空闲启动）===
        print("\n[启动] 无 --ExeDir 启动 server（空闲模式）")
        server = start_server(binary=args.binary)

        # --- Test 1: GET /state 空闲态 → gameDir==null + state=="Idle"（D5）---
        print("\n[1] GET /state 空闲态 → gameDir==null + state==Idle")
        s, body = server.get_state()
        check(s == 200, f"GET /state returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(obj.get("state") == "Idle",
                  f"state==Idle (idle start), got {obj.get('state')!r}")
            check(obj.get("gameDir") is None,
                  f"gameDir is None (D5 idle), got {obj.get('gameDir')!r}")
            # 窗口元信息字段照常由默认 ConfigData 提供
            check(isinstance(obj.get("windowWidth"), int),
                  f"windowWidth is int (default ConfigData), got {obj.get('windowWidth')!r}")
            check(isinstance(obj.get("fontSize"), int),
                  f"fontSize is int, got {obj.get('fontSize')!r}")
            check(isinstance(obj.get("visibleRows"), int) and obj.get("visibleRows", 0) >= 1,
                  f"visibleRows is int >= 1 (页高), got {obj.get('visibleRows')!r}")

        # --- Test 2: 空闲态 POST /session → 503 {"error":"No game loaded"}（D4）---
        print("\n[2] 空闲态 POST /session → 503 No game loaded")
        s, body = server.create_session()
        check(s == 503, f"POST /session idle returns 503, got {s}")
        if s == 503:
            obj = json.loads(body)
            check(obj.get("error") == "No game loaded",
                  f"error=='No game loaded', got {obj.get('error')!r}")

        # --- Test 2b: 空闲态 POST /load-game {非法目录} → 400 + server 仍空闲（spec 场景 4）---
        # spec L119：空闲态 load-game 非法目录 → 400 且 GET /state 仍 gameDir==null（不污染 Current）
        print("\n[2b] 空闲态 POST /load-game {非法目录} → 400 + server 仍空闲")
        bogus_idle = os.path.join(tempfile.gettempdir(), "emuera_idle_test_bogus_idle_9999")
        s, body = server.load_game(bogus_idle)
        check(s == 400, f"load-game bogus (idle) returns 400, got {s}")
        if s == 400:
            obj = json.loads(body)
            err = obj.get("error", {})
            check(err.get("code") == "DIR_NOT_FOUND",
                  f"error.code==DIR_NOT_FOUND, got {err.get('code')!r}")
        # GET /state 确认仍空闲——gameDir==null + state==Idle（D7 回滚，不污染 Current）
        s, body = server.get_state()
        check(s == 200, f"GET /state after idle bogus load-game returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(obj.get("state") == "Idle",
                  f"state==Idle (still idle after bogus), got {obj.get('state')!r}")
            check(obj.get("gameDir") is None,
                  f"gameDir is None (not polluted, D7 rollback), got {obj.get('gameDir')!r}")

        # --- Test 3: POST /load-game {合法目录} → 200 + GET /state 变有效 + POST /session 成功 ---
        # spec L118：load-game → GET /state 有效 → POST /session 成功（非 503 idle）
        print("\n[3] POST /load-game {合法目录} → 200 + GET /state 有效 + POST /session 成功")
        s, body = server.load_game(str(game_dir))
        check(s == 200, f"load-game valid returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(isinstance(obj.get("sessionId"), str),
                  f"has string sessionId: {obj.get('sessionId')!r}")
            check(norm(obj.get("gameDir")) == norm(game_dir),
                  f"gameDir=={game_dir}, got {obj.get('gameDir')!r}")
            # state 应为 "Loading" 或已推进状态，不为 "Idle"
            check(obj.get("state") != "Idle",
                  f"state != Idle (session created), got {obj.get('state')!r}")

        # GET /state 确认 gameDir 非 null + state != "Idle"
        s, body = server.get_state()
        check(s == 200, f"GET /state after load-game returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(norm(obj.get("gameDir")) == norm(game_dir),
                  f"gameDir=={game_dir}, got {obj.get('gameDir')!r}")
            check(obj.get("state") != "Idle",
                  f"state != Idle, got {obj.get('state')!r}")

        # spec L118：POST /session 成功——load-game 已建 session，POST /session 返 409（非 503 idle）
        # 409 = "已有活跃会话"——connection store ensureSession 将 201/409 均视为成功
        s, body = server.create_session()
        check(s in (201, 409),
              f"POST /session after load-game returns 201/409 (not 503 idle), got {s}")
        check(s != 503, f"POST /session not idle 503 after load-game, got {s}")

        # 等待游戏到达 WaitInput
        s, turn_body = server.get_turn(timeout=20)
        check(s == 200, f"GET /turn after load-game returns 200, got {s}")

        # --- Test 4: POST /load-game {非法目录} → 400 + server 状态不变 ---
        # 此时已有 session（test_game），尝试 load-game 一个 bogus 路径
        print("\n[4] POST /load-game {非法目录} → 400 + server 状态不变")
        bogus = os.path.join(tempfile.gettempdir(), "emuera_idle_test_bogus_9999")
        s, body = server.load_game(bogus)
        check(s == 400, f"load-game bogus returns 400, got {s}")
        if s == 400:
            obj = json.loads(body)
            err = obj.get("error", {})
            check(err.get("code") == "DIR_NOT_FOUND",
                  f"error.code==DIR_NOT_FOUND, got {err.get('code')!r}")

        # GET /state 确认 gameDir 仍是旧值（D7 回滚）
        s, body = server.get_state()
        check(s == 200, f"GET /state after failed load-game returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(norm(obj.get("gameDir")) == norm(game_dir),
                  f"gameDir rolled back to {game_dir}, got {obj.get('gameDir')!r}")

        # --- Test 5: 快速重开路径 — DELETE /session + POST /load-game 同目录 ---
        print("\n[5] 快速重开：DELETE /session + POST /load-game 同目录 → 重新进入")
        s, body = server.delete_session()
        check(s == 200, f"DELETE /session returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(obj.get("removed") is True, f"removed==true, got {obj.get('removed')!r}")

        # DELETE 后 GET /state → idle（gameDir==null + state==Idle）— D10 复位
        s, body = server.get_state()
        check(s == 200, f"GET /state after DELETE returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(obj.get("state") == "Idle",
                  f"state==Idle after DELETE (D10 reset), got {obj.get('state')!r}")
            check(obj.get("gameDir") is None,
                  f"gameDir is None after DELETE (D5), got {obj.get('gameDir')!r}")

        # DELETE 后 POST /session → 503（_session==null 复位，D4 守卫）
        s, body = server.create_session()
        check(s == 503, f"POST /session after DELETE returns 503, got {s}")

        # POST /load-game 同目录 → 重新进入
        s, body = server.load_game(str(game_dir))
        check(s == 200, f"load-game same dir returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(norm(obj.get("gameDir")) == norm(game_dir),
                  f"gameDir=={game_dir} (reloaded), got {obj.get('gameDir')!r}")
            check(obj.get("state") != "Idle",
                  f"state != Idle (reloaded), got {obj.get('state')!r}")

        # 等待游戏到达 WaitInput
        s, turn_body = server.get_turn(timeout=20)
        check(s == 200, f"GET /turn after reload returns 200, got {s}")

        # --- Test 6: 快速重开失败回退 — DELETE + POST /load-game 非法目录 ---
        print("\n[6] 快速重开失败：DELETE + POST /load-game 非法目录 → 400 + server idle")
        s, body = server.delete_session()
        check(s == 200, f"DELETE /session returns 200, got {s}")

        # POST /load-game 非法目录 → 400
        s, body = server.load_game(bogus)
        check(s == 400, f"load-game bogus after DELETE returns 400, got {s}")

        # server 回 idle（gameDir==null + state==Idle）— D5/D10
        s, body = server.get_state()
        check(s == 200, f"GET /state after failed reload returns 200, got {s}")
        if s == 200:
            obj = json.loads(body)
            check(obj.get("state") == "Idle",
                  f"state==Idle (failed reload → idle), got {obj.get('state')!r}")
            check(obj.get("gameDir") is None,
                  f"gameDir is None (failed reload → idle), got {obj.get('gameDir')!r}")

        # POST /session → 503（server 回到空闲态）
        s, body = server.create_session()
        check(s == 503, f"POST /session after failed reload returns 503, got {s}")

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
