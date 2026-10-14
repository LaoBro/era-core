"""Test: SELECTCASE 编译期跳转表的外部行为（.scratch/selectcase-jumptable/spec.md）。

在真实引擎（server 模式）下验证跳转表的运行期行为零回归：
    * 大 int 块（10 键 ≥ 閾値 8）fast path 派発：命中 CASE → 该分支输出；未命中 → CASEELSE 兜底。
    * 无 CASEELSE 的 int 块：未命中 → 直接跳到 ENDSELECT 之后（块体不执行）。
    * string 块（4 键 = 閾値）Ordinal 派発：命中 / 兜底。
    * RELOAD（重新 /load-game）后行为不变（表随加载重建）。
    * eager（遅延:NO）与惰性（遅延:YES，默认）两形态外部行为完全一致。

注：PRINTFORML 中 {RESULTS}（字符串变量插值）在本引擎报错为既有行为（最小无 SELECTCASE 的
ERB 同样复现），与本优化无关——string 兜底断言用字面量标记，不插值。

Usage:
    python test_selectcase_jumptable.py --binary <path> --game-dir test_game
"""
import argparse
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, diff_text, find_binary, snapshot_text, start_server, turn_state

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


def _build_erb():
    """SYSTEM_TITLE 驱动的 SELECTCASE 场景（三个输入点，各对应一个块）：
    1) INPUT → int 大块（10 键 + CASEELSE）—— fast path 命中 / CASEELSE 兜底
    2) INPUT → int 无 CASEELSE 块（10 键）—— 未命中跳 ENDSELECT（块体不执行）
    3) INPUTS → string 块（4 键 + CASEELSE）—— Ordinal 派発 / 兜底
    """
    sb = ["@SYSTEM_TITLE", "PRINTL JumpTableE2E", "INPUT"]
    # 块 1：大 int 块（10 键 ≥ 8 → 建表）+ CASEELSE
    sb.append("SELECTCASE RESULT")
    for i in range(10):
        sb.append(f"\tCASE {i}")
        sb.append(f"\t\tPRINTFORML Big:{i}")
    sb.append("\tCASEELSE")
    sb.append("\t\tPRINTFORML Fallback:{RESULT}")
    sb.append("ENDSELECT")
    sb.append("PRINTL AfterBig")
    # 块 2：int 无 CASEELSE（10 键 → 建表）；未命中 → 跳 ENDSELECT
    sb.append("INPUT")
    sb.append("SELECTCASE RESULT")
    for i in range(100, 110):
        sb.append(f"\tCASE {i}")
        sb.append(f"\t\tPRINTFORML NoElse:{i}")
    sb.append("ENDSELECT")
    sb.append("PRINTL AfterNoElse")
    # 块 3：string 块（4 键 = 閾値 → 建表）+ CASEELSE
    sb.append("INPUTS")
    sb.append("SELECTCASE RESULTS")
    for ch in "abcd":
        sb.append(f'\tCASE "{ch}"')
        sb.append(f"\t\tPRINTFORML Str:{ch}")
    sb.append("\tCASEELSE")
    sb.append("\t\tPRINTFORML StrFallback")
    sb.append("ENDSELECT")
    sb.append("PRINTL AfterStr")
    sb.append("QUIT")
    return "\n".join(sb) + "\n"


def _set_lazy(cfg_path, lazy):
    """改写 emuera.config 的惰性物化开关（YES/NO）。"""
    key = "未呼び出し関数の本体解析を遅延する"
    lines = [l for l in cfg_path.read_text(encoding="utf-8").splitlines()
             if l.strip() and not l.startswith(key)]
    lines.append(f"{key}:{'YES' if lazy else 'NO'}")
    cfg_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def _drive(server, value):
    """提交输入并取下一 turn，返回 (status, turn_json)。"""
    server.post_input(value)
    status, body = server.get_turn(timeout=10)
    return status, json.loads(body)


def _run_flow(server, game_dir, tag):
    """会话 1：int 命中 / 无 CASEELSE 跳过 / string 命中。"""
    create_status, create_body = server.load_game(game_dir)
    check(create_status == 200, f"[{tag}] POST /load-game returns 200, got {create_status}")

    initial_status, initial_body = server.get_turn(timeout=10)
    check(initial_status == 200, f"[{tag}] initial GET /turn returns 200, got {initial_status}")
    initial = json.loads(initial_body)
    check(turn_state(initial) == "WaitInput", f"[{tag}] initial state is WaitInput (awaiting.input), got {turn_state(initial)}")
    snap_status, snap_body = server.get_snapshot(timeout=10)
    check(snap_status == 200 and "JumpTableE2E" in snapshot_text(json.loads(snap_body)),
          f"[{tag}] initial snapshot contains test start")

    # 块 1：int fast path 命中 CASE 7
    status, turn = _drive(server, "7")
    text = diff_text(turn)
    check(status == 200 and "Big:7" in text, f"[{tag}] int fast-path dispatch hit CASE 7")
    check("AfterBig" in text, f"[{tag}] execution continued past ENDSELECT")
    check(turn_state(turn) == "WaitInput", f"[{tag}] state is WaitInput after block 1 (awaiting.input)")

    # 块 2：无 CASEELSE → 未命中直接跳 ENDSELECT（块体不执行）
    status, turn = _drive(server, "300")
    text = diff_text(turn)
    check(status == 200 and "AfterNoElse" in text, f"[{tag}] no-else block skipped to ENDSELECT")
    check("NoElse:300" not in text, f"[{tag}] no-else block body did not execute")

    # 块 3：string fast path 命中 CASE c
    status, turn = _drive(server, "c")
    text = diff_text(turn)
    check(status == 200 and "Str:c" in text, f"[{tag}] string dispatch hit CASE c")
    check("AfterStr" in text, f"[{tag}] execution continued past string ENDSELECT")
    check(turn_state(turn) == "Quit", f"[{tag}] final state is Quit (awaiting.ended), got {turn_state(turn)}")


def _run_reload_flow(server, game_dir, tag):
    """会话 2（RELOAD）：/load-game 重新加载后行为不变——表重建，CASEELSE 兜底 / string 兜底依旧。"""
    server.delete_session()
    create_status, _ = server.load_game(game_dir)
    check(create_status == 200, f"[{tag}] reload POST /load-game returns 200, got {create_status}")
    server.get_turn(timeout=10)

    # 块 1：CASEELSE 兜底（未命中）
    status, turn = _drive(server, "999")
    text = diff_text(turn)
    check(status == 200 and "Fallback:999" in text, f"[{tag}] after reload CASEELSE fallback on miss")

    # 块 2：无 CASEELSE 跳过
    status, turn = _drive(server, "5")
    text = diff_text(turn)
    check(status == 200 and "AfterNoElse" in text, f"[{tag}] after reload no-else block skipped")

    # 块 3：string CASEELSE 兜底
    status, turn = _drive(server, "zz")
    text = diff_text(turn)
    check(status == 200 and "StrFallback" in text, f"[{tag}] after reload string fallback")
    check(turn_state(turn) == "Quit", f"[{tag}] after reload final state is Quit (awaiting.ended), got {turn_state(turn)}")


def _run_mode(binary, game_dir, lazy, tag):
    """以指定惰性形态跑完整流程（会话 1 + RELOAD 会话 2）。"""
    temp_dir, temp_game = copy_test_game_with_erb(_build_erb())
    _set_lazy(Path(temp_game) / "emuera.config", lazy)
    server = None
    try:
        server = start_server(temp_game, binary=binary)
        _run_flow(server, temp_game, tag)
        _run_reload_flow(server, temp_game, tag)
    finally:
        if server is not None:
            server.delete_session()
            server.close()
        shutil.rmtree(temp_dir, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to EraCore binary (exe or dll)")
    parser.add_argument("--game-dir", default=str(ROOT / "test_game"), help="Source game dir (copied to temp)")
    args = parser.parse_args()

    binary = args.binary or find_binary(str(ROOT))[0]

    print("\n--- 惰性形态（未呼び出し関数の本体解析を遅延する:YES，默认） ---")
    _run_mode(binary, args.game_dir, lazy=True, tag="lazy")

    print("\n--- eager 形态（遅延:NO） ---")
    _run_mode(binary, args.game_dir, lazy=False, tag="eager")

    print(f"\n=== SELECTCASE jump table e2e: {passed} passed, {failed} failed ===")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
