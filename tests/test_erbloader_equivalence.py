"""Test: ERB 加载并行/串行等价性检查（erb-loader-parallel Issue 04）。

同一游戏分别以并行（默认）与串行 fallback 加载，断言两者产出完全一致：
    * ParseScript 控制台三数统计（非注释行数 / 全函数合计 / 被呼出函数数，即 TotalFunc 行）
    * 规范化后的警告列表（按文件 + 行号排序）

这是 spec「Testing Decisions 接缝 2」落地为可回归套件：此后任何加载路径改动的语义安全网。
不新增产品 API —— 直接从 server 的 GET /snapshot 抓取加载报告（--loading-report 强制 DisplayReport on）。

模式：
    * 默认（CI / 常规回归）：基于仓库 test_game 构造临时语料，额外注入会触发警告的 ERB
      （firstDir #PRI、跨文件重名函数、$ 重名、#ONLY 重复、引数重载），使警告列表非空且有序。
    * eraTW / 任意大游戏（本机手动，不进 CI）：--game-dir <araTW> --no-inject，
      直接在临时副本上做并行 vs 串行等价性（不注入、不修改原游戏）。
    * 区分能力自检：独立临时副本注入一处标签重命名，断言等价比较器能感知差异（套件能变红）。

Usage:
    python test_erbloader_equivalence.py --binary <path>
    python test_erbloader_equivalence.py --binary <path> --game-dir D:/era/TW4.881shoess --no-inject
    python test_erbloader_equivalence.py --binary <path> --record eratw-equiv.json --no-inject
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from emuera_server import find_binary, wait_for_port

PROJECT_ROOT = Path(__file__).resolve().parents[1]
REPO_TEST_GAME = PROJECT_ROOT / "test_game"

# 警告行：`警告Lv{level}:{file}:{line}行目:{message}`（日文）；英文 `Warning Lv{level}:{file}: at line {line}:{message}`
_WARN_LINE = re.compile(
    r"^(?:警告Lv|Warning Lv)(\d+):(?P<file>.+?):(?:(?:第)?(\d+)行目|at line (\d+)):(?P<msg>.+)$"
)
# 三数统计行（日文）；英文为 `Number of non-comment lines: ...`
_TOTAL_RE = re.compile(r"非コメント行数[:：]\s*(\d+).*?全関数合計[:：]\s*(\d+).*?被呼出関数合計[:：]\s*(\d+)")
_TOTAL_EN_RE = re.compile(r"Number of non-comment lines[:：]\s*(\d+).*?All functions total[:：]\s*(\d+).*?Total called functions[:：]\s*(\d+)")

passed = [0]
failed = [0]


def check(condition, message):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


class Server:
    """带 --loading-report 的 server runner（等价性需要 DisplayReport on 才有 TotalFunc 行）。"""

    def __init__(self, binary):
        self.binary = binary
        self.proc = None
        self.port = None
        self.base_url = None
        self._drain = None

    def start(self):
        import socket
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            self.port = s.getsockname()[1]
        use_dotnet = str(self.binary).endswith(".dll")
        cmd = (["dotnet", "exec", str(self.binary)] if use_dotnet else [str(self.binary)])
        cmd += ["--server", "--port", str(self.port), "--loading-report"]
        self.proc = subprocess.Popen(
            cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace",
        )
        self._drain = threading.Thread(target=self._drain_stdout, args=(self.proc,), daemon=True)
        self._drain.start()
        self.base_url = f"http://localhost:{self.port}"
        wait_for_port(self.port)
        return self

    @staticmethod
    def _drain_stdout(proc):
        try:
            for _ in iter(proc.stdout.readline, ""):
                pass
        except (OSError, ValueError):
            pass

    def request(self, method, path, body=None, timeout=120):
        data = None if body is None else json.dumps(body).encode("utf-8")
        headers = {"Content-Type": "application/json"} if body is not None else {}
        req = urllib.request.Request(
            f"{self.base_url}{path}", data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.status, resp.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8", "replace")

    def load_game_wait(self, game_dir, timeout=300):
        """POST /load-game 并轮询 /state 直至离开 loading（加载完成/出错），返回 snapshot lines。"""
        status, _ = self.request("POST", "/load-game", {"gameDir": str(game_dir)}, timeout=timeout)
        if status != 200:
            raise RuntimeError(f"load-game failed: {status}")
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                _, state_body = self.request("GET", "/state", timeout=timeout)
                state = json.loads(state_body).get("state", "")
                if state.lower() != "loading":
                    break
            except Exception:
                pass
            time.sleep(0.5)
        else:
            raise RuntimeError("session stuck in Loading")
        self.request("GET", "/turn", timeout=timeout)  # 首帧 turn 冲刷显示缓冲
        _, snap_body = self.request("GET", "/snapshot", timeout=timeout)
        return _snapshot_lines(json.loads(snap_body))

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.kill()
            try:
                self.proc.wait(timeout=15)
            except subprocess.TimeoutExpired:
                pass


def _snapshot_lines(snap):
    out = []
    for line in snap.get("lines", []):
        for entry in line.get("entries", []):
            for seg in entry.get("segments", []):
                txt = seg.get("text", "")
                if txt:
                    out.append(txt)
    return out


def _totals(lines):
    """解析 TotalFunc 三数（非注释行数 / 全函数合计 / 被呼出函数数）。返回 (n1,n2,n3) 或 None。"""
    for line in lines:
        m = _TOTAL_RE.search(line) or _TOTAL_EN_RE.search(line)
        if m:
            return (int(m.group(1)), int(m.group(2)), int(m.group(3)))
    return None


def _warnings(lines):
    """规范化警告：解析 警告Lv{level}:{file}:{line}行目:{message}，按 (file, line) 排序。"""
    entries = []
    for line in lines:
        m = _WARN_LINE.match(line)
        if not m:
            continue
        level = int(m.group(1))
        filename = m.group("file")
        lineno = int(m.group(3) or m.group(4) or 0)
        entries.append((filename, lineno, level, m.group("msg")))
    entries.sort(key=lambda x: (x[0].lower(), x[1], x[2], x[3]))
    return entries


def _set_config_lines(cfg_path, overrides):
    """改写临时语料 emuera.config：overrides = [(key, value), ...]，删旧键行后追加（与 _set_parallel 同款语义）。"""
    keys = [k for k, _ in overrides]
    lines = [l for l in cfg_path.read_text(encoding="utf-8").splitlines()
             if l.strip() and not any(l.startswith(k) for k in keys)]
    lines.extend(f"{k}:{v}" for k, v in overrides)
    cfg_path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def _set_parallel(cfg_path, parallel):
    """改写临时语料 emuera.config 的并行加载双开关（erb-loader-parallel 08：ParallelErbLoad + ParallelParseScript）。"""
    flag = "YES" if parallel else "NO"
    _set_config_lines(cfg_path, [
        ("ERBファイルを並列して読み込む", flag),
        ("未呼び出し関数の解析を並列して行う", flag),
    ])


def _build_corpus(src_game, inject_warnings):
    """复制源游戏到临时目录；注入警告语料（可选）。返回 temp game 目录。"""
    tmp = tempfile.mkdtemp(prefix="erbloader_eq_")
    game = Path(tmp) / "game"
    shutil.copytree(str(src_game), str(game))
    if inject_warnings:
        erb = game / "erb"
        os.makedirs(erb / "1#PRI", exist_ok=True)
        (erb / "1#PRI" / "SUB.ERB").write_text("@FOO\nRETURN\n", encoding="utf-8")
        (erb / "A.ERB").write_text(
            "@FOO\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n@SYS\nRETURN\n", encoding="utf-8")
        (erb / "B.ERB").write_text(
            "@FOO\n$LAB\n$LAB\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n@BFOO(X, Y)\nRETURN\n@BFOO\nRETURN\n",
            encoding="utf-8")
        # erb-loader-parallel 08：else 分支真解析语料（未调用函数 + FNF + nestCheck 警告）——
        # 默认 IgnoreUncalledFunction:YES 下未调用遍无解析体，等价性覆盖不到并行 sweep
        (erb / "C.ERB").write_text(
            "@UNCALLED_C1\nBREAK\nCALL NOEXIST_FUNC_C\nRETURN\n@UNCALLED_C2\nCALL NOEXIST_FUNC_C2\nRETURN\n",
            encoding="utf-8")
        cfg = game / "emuera.config"
        if cfg.exists():
            _set_config_lines(cfg, [
                ("同名の非イベント関数が複数定義されたとき警告する", "YES"),
                ("呼び出されなかった関数を無視する", "NO"),
                ("関数が見つからない警告の扱い", "ONCE"),
                ("関数が呼び出されなかった警告の扱い", "ONCE"),
            ])
    return game


def _run_path(binary, game_dir):
    srv = Server(binary).start()
    try:
        lines = srv.load_game_wait(game_dir)
    finally:
        srv.stop()
    return _totals(lines), _warnings(lines)


def _equiv_report(totals, warnings):
    return {"totals": list(totals) if totals else None, "warnings": warnings}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary")
    parser.add_argument("--game-dir", default=str(REPO_TEST_GAME))
    parser.add_argument("--no-inject", action="store_true",
                        help="不注入警告语料（直接对源游戏做等价性，eraTW 等大语料用）")
    parser.add_argument("--record", help="将比对结果写为 JSON 文件（eraTW 本机记录用）")
    args = parser.parse_args()

    source = Path(args.game_dir).resolve()
    inject = not args.no_inject
    binary = args.binary or find_binary(str(PROJECT_ROOT))[0]

    # 源语料必须存在
    if not source.is_dir():
        print(f"FAIL: game dir not found: {source}")
        sys.exit(1)

    print(f"[Equivalence] source={source} inject_warnings={inject}")
    print(f"[Equivalence] binary={binary}")

    # ---- 同一游戏，两条路径各加载一次 ----
    game = _build_corpus(source, inject)

    print("\n--- 并行路径加载 ---")
    par_totals, par_warnings = _run_path(binary, game)
    print(f"  三数统计={par_totals} 警告条数={len(par_warnings)}")

    print("\n--- 串行 fallback 加载 ---")
    _set_parallel(game / "emuera.config", parallel=False)
    ser_totals, ser_warnings = _run_path(binary, game)
    print(f"  三数统计={ser_totals} 警告条数={len(ser_warnings)}")

    # 断言：三数统计完全一致
    ok_totals = par_totals is not None and par_totals == ser_totals
    check(par_totals is not None, "并行 / 串行均捕获到 TotalFunc 三数统计")
    check(ok_totals, f"三数统计完全一致: parallel={par_totals} serial={ser_totals}")
    if not ok_totals:
        print("  detail parallel=", par_totals, " serial=", ser_totals)

    # 断言：规范化警告列表完全一致
    ok_warnings = par_warnings == ser_warnings
    check(ok_warnings,
          f"规范化警告列表完全一致: {len(par_warnings)} 条 == {len(ser_warnings)} 条")
    if not ok_warnings:
        diff = set(par_warnings) ^ set(ser_warnings)
        for d in list(diff)[:20]:
            print("  diff:", d)

    report = {
        "gameDirs": str(game),
        "injectWarnings": inject,
        "parallel": _equiv_report(par_totals, par_warnings),
        "serial": _equiv_report(ser_totals, ser_warnings),
        "asserted": {
            "totalsIdentical": ok_totals,
            "warningsIdentical": ok_warnings,
        },
    }

    # ---- 区分能力自检：注入一处标签差异，等价性比较器必须能感知（套件能变红）----
    if inject:
        print("\n--- 区分能力自检：注入标签差异 ---")
        tampered = _build_corpus(source, inject)
        a = tampered / "erb" / "A.ERB"
        a.write_text(a.read_text(encoding="utf-8")
                     .replace("@FOO\nRETURN\n", "@FOO_NEW\nRETURN\n", 1), encoding="utf-8")
        t_totals, t_warnings = _run_path(binary, tampered)
        print(f"  注入后三数={t_totals} 警告条数={len(t_warnings)}")
        # 与并行基线相比，注入标签差异必须导致规范化警告改变——证明比较器有区分力
        distinguishable = t_warnings != par_warnings or t_totals != par_totals
        check(distinguishable,
              "注入一处标签差异后，等价比较器能感知差异（套件将变红）")
        if not distinguishable:
            print("  NOTE: 注入未改变输出，比较器可能缺失区分能力")
        report["distinguishTest"] = {
            "injectedTotals": list(t_totals) if t_totals else None,
            "injectedWarnings": t_warnings,
            "distinguishable": distinguishable,
        }
    else:
        check(True, "（--no-inject）跳过区分能力自检（大语料本机场景）")

    if args.record:
        Path(args.record).write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"[Equivalence] result recorded to {args.record}")

    print(f"\n{passed[0]} passed, {failed[0]} failed")
    sys.exit(1 if failed[0] else 0)


if __name__ == "__main__":
    main()