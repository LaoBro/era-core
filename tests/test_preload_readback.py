"""Test: preload-cache-lifecycle 01 — 报错回读（ADR-0023）。

加载收尾清空 Preload 源文本缓存后，运行期报错的源码行回显走按需回读：
- 断言一（回读成功路径）：源文件在磁盘上 → THROW 报错输出含原行文本（原行独有子串标记）。
- 断言二（降级路径）：加载后移走源文件再触发同一报错 → 输出占位文本（括号 + 文件名 + 行号，
  jpn/eng/zhs 三语形态同构，正则语言无关断言），原行 marker 不出现，且进程存活（GET /state 仍 200）。

Corpus: test_game 变体（TEST.ERB 带 THROW 行）。原行独有标记 = 'THROW "fatal-test-marker"'
（THROW+空格+引号仅出现在磁盘原行；HasThrow 消息为「THROWが発生」、ThrowMessage 为
「THROW内容：…」均不含该子串 → 出现即证明按需回读成功；THROW 的 FORM 参数会吞行尾注释，
故不能用注释做标记）。

Usage:
    python test_preload_readback.py
    python test_preload_readback.py --binary path/to/EraCore.Cli.exe
    python test_preload_readback.py --game-dir test_game
"""
import argparse
import json
import re
import shutil
import sys
import tempfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import copy_test_game_with_erb, diff_text, start_server

# 原行独有子串：THROW+空格+引号。错误消息两处 THROW 关键字（THROWが / THROW内容：）均不匹配。
RAW_MARKER = 'THROW "fatal-test-marker"'
# 占位三语形态：(ソース行を表示できません: {0} {1}行目) / (source line unavailable: {0} line {1})
#              / (无法显示源码行: {0} 第{1}行) —— 括号 + 文件名同构，正则语言无关断言
PLACEHOLDER_RE = re.compile(r"\([^)]*TEST\.ERB[^)]*\)")

ERB_SCRIPT = """@SYSTEM_TITLE
PRINTL [0] Trigger error
PRINTL [1] Quit
INPUT
IF RESULT == 0
\tTHROW "fatal-test-marker"
ELSE
\tQUIT
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


def wait_error_turn(server, timeout=30):
    """轮询 GET /turn 直到 Error 态（或 marker 出现）；返回 (turn, text)。"""
    deadline = time.time() + timeout
    while time.time() < deadline:
        s, body = server.get_turn(timeout=10)
        if s != 200:
            return None, ""
        turn = json.loads(body)
        text = diff_text(turn)
        if turn.get("state") == "Error" or "fatal-test-marker" in text:
            return turn, text
        time.sleep(0.5)
    return None, ""


def run_readback_success(binary):
    """断言一：源文件在磁盘 → THROW 报错输出含原行文本（按需回读成功路径）。"""
    print("\n[1] 报错回读成功路径（源文件在磁盘）")
    temp_dir, gd = copy_test_game_with_erb(ERB_SCRIPT)
    server = start_server(gd, binary=binary)
    try:
        s, _ = server.load_game(gd)
        check(s == 200, f"POST /load-game returns 200, got {s}")
        s, _ = server.get_turn(timeout=15)
        check(s == 200, f"initial GET /turn returns 200, got {s}")

        s, _ = server.post_input("0")
        check(s == 200, f"POST /input 0 returns 200, got {s}")

        turn, text = wait_error_turn(server)
        check(turn is not None, "error turn received within 30s")
        if turn is not None:
            check(turn.get("state") == "Error", f"turn state==Error, got {turn.get('state')!r}")
            # 加载收尾后缓存恒空——原行文本只能来自磁盘按需回读
            check(RAW_MARKER in text,
                  "error output contains raw source line marker (disk readback)")
    finally:
        try:
            server.delete_session()
        except Exception:
            pass
        server.close()
        shutil.rmtree(temp_dir, ignore_errors=True)


def run_readback_placeholder(binary):
    """断言二：加载后移走源文件 → 同一报错输出占位文本且进程存活。"""
    print("\n[2] 报错回读降级路径（源文件已移走）")
    temp_dir, gd = copy_test_game_with_erb(ERB_SCRIPT)
    server = start_server(gd, binary=binary)
    erb_path = Path(gd) / "erb" / "TEST.ERB"
    moved = Path(tempfile.gettempdir()) / f"emuera_moved_{int(time.time())}_TEST.ERB"
    try:
        s, _ = server.load_game(gd)
        check(s == 200, f"POST /load-game returns 200, got {s}")
        s, _ = server.get_turn(timeout=15)
        check(s == 200, f"initial GET /turn returns 200, got {s}")

        # 加载完成后移走源文件（标签已在内存，执行不受影响；仅回读通道失去目标）
        shutil.move(str(erb_path), str(moved))
        check(not erb_path.exists(), "source file moved away")

        s, _ = server.post_input("0")
        check(s == 200, f"POST /input 0 returns 200, got {s}")

        turn, text = wait_error_turn(server)
        check(turn is not None, "error turn received within 30s")
        if turn is not None:
            check(RAW_MARKER not in text, "raw source line marker absent (readback failed)")
            m = PLACEHOLDER_RE.search(text)
            check(m is not None, f"placeholder text present (parenthesized TEST.ERB), text={text!r}")
            if m:
                check("6" in m.group(0), f"placeholder carries line number 6, got {m.group(0)!r}")

        # 进程存活：报错回读降级不得影响 server 存活
        s, body = server.get_state()
        check(s == 200, f"GET /state after placeholder error still 200, got {s}")
    finally:
        try:
            server.delete_session()
        except Exception:
            pass
        server.close()
        if moved.exists():
            moved.unlink()
        shutil.rmtree(temp_dir, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    parser.add_argument("--suite-name", default="preload readback", help="Test suite name")
    args = parser.parse_args()

    run_readback_success(args.binary)
    run_readback_placeholder(args.binary)

    print(f"\n=== {args.suite_name}: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
