"""一致性套件（conformance kit）——实现票 25。

参数化 `--game-dir <dir> [--profile <id>]` 的可机械执行验收，含三层：

1. **解析 smoke**：加载期零「无法解析的标识符」/ 零未定义函数警告 + 无（era-core 归因的）`warnLevel ≥ 2`；
2. **面快照**：复用票 17 的 `DialectSurfaceSnapshotTests`（`dotnet test --filter`，可关）；
3. **playtest 冒烟**：以目标 profile 启动 → 进入输入循环（`expectState`），无 fatal。

game→profile 为**显式映射**（`games.json` 的 `matchHints → profile`，不建 resolver）；`anchors` 为可选内容
锚点冒烟（防误配）；`knownLv2` 为经参考引擎判定为「游戏数据缺陷」的已知 Lv2 子串，逐条排除于 DoD
（口径见 spec §3/§9 与 issues/33）。

**新游戏只加一条 `games.json` fixture，不改本文件。**

设计来源：spec §6.3/§7 · issues/14（一致性套件与映射）· issues/25（本票）。
"""
import json
import shutil
import subprocess
import sys
import time
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
ROOT_DIR = TESTS_DIR.parent
DEFAULT_GAMES_JSON = TESTS_DIR / "games.json"

sys.path.insert(0, str(TESTS_DIR))

# 加载期告警识别（zhs 语言包；与 EraCore.Core/Shared/Properties/lang/emuera-zhs.xml 对齐，
# 并保留日语原文兜底：语言包可切换，同一语义三语同构）。
UNRECOGNIZED_MARKERS = ("无法解析的标识符", "无法解析的行", "識別できない", "解釈できない")
UNDEFINED_FUNC_MARKERS = ("未定义函数", "定義されていない関数")
# warnLevel≥2 行前缀（emuera-zhs.xml 的警告形态：`警告Lv2:<loc>:<msg>`）。
LEVEL2_PREFIX = "警告Lv2:"
# 加载未稳定的瞬态——轮询首屏时跳过。
# v12 前：snapshot 顶层 state ∈ ("Initializing", "Running")；
# v12 起（issue 09）：state 字段退场，未稳定瞬态表现为 awaiting 双空（awaiting:{}），
# 停稳点判据见 emuera_server.awaiting_is_settled（input 或 ended 出现）。
UNSETTLED_STATES = ("Initializing", "Running")


# ===== fixture / 映射 =====

def load_config(path=None):
    p = Path(path) if path else DEFAULT_GAMES_JSON
    with open(p, encoding="utf-8") as f:
        return json.load(f)


def find_fixture(config, game_dir):
    """按 matchHints（abs 路径子串，忽略大小写）匹配 fixture；无匹配返回 None。"""
    low = str(Path(game_dir).resolve()).lower()
    for fx in config.get("games", []):
        for hint in fx.get("matchHints", []):
            if hint.lower() in low:
                return fx
    return None


def resolve_profile(config, game_dir, explicit_profile):
    """返回 (profile, fixture, notes)。显式 --profile 优先；否则按 fixture 解析；都没有 → (None, None, note)。"""
    notes = []
    fixture = find_fixture(config, game_dir)
    if explicit_profile:
        if fixture and fixture.get("profile") != explicit_profile:
            notes.append(
                f"显式 profile={explicit_profile} 覆盖 fixture {fixture.get('id')!r} 的 {fixture.get('profile')!r}"
            )
        return explicit_profile, fixture, notes
    if fixture:
        return fixture.get("profile"), fixture, notes
    known = ", ".join(fx.get("id", "?") for fx in config.get("games", [])) or "(空)"
    notes.append("game-dir 未匹配任何 games.json fixture，且未显式 --profile；已知 fixture: " + known)
    return None, None, notes


def _resolve_rel(game_dir, rel):
    """相对路径解析：先精确，再逐级忽略大小写（跨平台/大小写不敏感文件系统）。"""
    base = Path(game_dir)
    exact = base / rel
    if exact.exists():
        return exact
    cur = base
    for part in Path(rel).parts:
        if not cur.is_dir():
            return None
        match = None
        for child in cur.iterdir():
            if child.name.lower() == part.lower():
                match = child
                break
        if match is None:
            return None
        cur = match
    return cur


def check_anchors(game_dir, fixture):
    """内容锚点冒烟：返回问题列表（空 = 通过）。用于防 profile 误配。"""
    problems = []
    for anchor in fixture.get("anchors", []):
        rel = anchor.get("path")
        resolved = _resolve_rel(game_dir, rel) if rel else None
        if resolved is None or not resolved.is_file():
            problems.append(f"锚点文件缺失: {rel}")
            continue
        contains = anchor.get("contains")
        if contains:
            text = resolved.read_text(encoding="utf-8", errors="replace")
            if contains not in text:
                problems.append(f"锚点内容缺失: {rel} 应含 {contains!r}")
    return problems


# ===== 运行步骤 =====

def snapshot_lines(snap):
    """把 DisplaySnapshot 投影为控制台行列表（entries[].segments[].text 拼接）。"""
    lines = []
    for line in snap.get("lines", []):
        parts = []
        for entry in line.get("entries", []):
            for seg in entry.get("segments", []):
                parts.append(seg.get("text", ""))
        lines.append("".join(parts))
    return lines


def run_game_smoke(game_dir, profile, binary, settle_timeout=120):
    """启动 server（带 profile）→ load-game → 轮询首屏至稳定。

    加载是异步的：POST /load-game 后前几次 /snapshot 可能仍在脚本执行瞬态
    （v12 起 awaiting 双空 awaiting:{}——「既不等输入也不等定时器」，见
    UNSETTLED_STATES 的 v12 等价判据）；轮询到停稳点（awaiting.input 或
    awaiting.ended 出现）再取 lines/state 作为解析 smoke 与 playtest 的输入。
    返回 dict（含 lines/state/load_status；state 经 turn_state 从 awaiting 推导，
    语义等同旧顶层 state 字段）。
    """
    from emuera_server import start_server, turn_state, awaiting_is_settled  # 延迟导入：仅运行期需要

    extra_args = ["--profile", profile] if profile else []
    server = start_server(str(game_dir), binary=binary, extra_args=extra_args)
    result = {"load_status": None, "state": None, "lines": []}
    try:
        status, _ = server.load_game(str(game_dir))
        result["load_status"] = status
        deadline = time.time() + settle_timeout
        while time.time() < deadline:
            status, body = server.get_snapshot(timeout=120)
            if status == 200:
                snap = json.loads(body)
                result["state"] = turn_state(snap)
                result["lines"] = snapshot_lines(snap)
                if awaiting_is_settled(snap):
                    break
            time.sleep(0.3)
    finally:
        try:
            server.delete_session()
        except Exception:
            pass
        server.close()
    return result


def analyze_warnings(lines, known_lv2):
    """返回 (unrecognized, undefined_func, unexpected_lv2, known_hit)。

    - unrecognized：命中 UNRECOGNIZED_MARKERS 的行。
    - undefined_func：命中 UNDEFINED_FUNC_MARKERS 的行。
    - unexpected_lv2：`警告Lv2:` 行中不匹配 knownLv2 子串者（era-core 归因）。
    - known_hit：匹配 knownLv2 的 Lv2 行（游戏数据缺陷，单列不计入 DoD）。
    """
    unrecognized, undefined_func, unexpected_lv2, known_hit = [], [], [], []
    for line in lines:
        if any(m in line for m in UNRECOGNIZED_MARKERS):
            unrecognized.append(line)
        if any(m in line for m in UNDEFINED_FUNC_MARKERS):
            undefined_func.append(line)
        if LEVEL2_PREFIX in line:
            if any(pat in line for pat in known_lv2):
                known_hit.append(line)
            else:
                unexpected_lv2.append(line)
    return unrecognized, undefined_func, unexpected_lv2, known_hit


def run_surface_snapshot(timeout=300):
    """面快照门禁（票 17）：`dotnet test --filter DialectSurfaceSnapshotTests`。返回 (ok, note)。"""
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        return None, "SKIP: dotnet 不在 PATH"
    cp = subprocess.run(
        [dotnet, "test", "EraCore.Tests/EraCore.Tests.csproj", "--nologo",
         "--filter", "FullyQualifiedName~DialectSurfaceSnapshotTests"],
        cwd=str(ROOT_DIR), capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=timeout,
    )
    tail = [l for l in cp.stdout.splitlines() if ("通过" in l or "失败" in l or "Passed" in l or "Failed" in l)]
    return cp.returncode == 0, (tail[-1].strip() if tail else f"exit {cp.returncode}")


def run_build(timeout=600):
    """DoD 单入口的 build 步：dotnet build EraCore.Cli。返回 (ok, note)。"""
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        return False, "dotnet 不在 PATH"
    cp = subprocess.run(
        [dotnet, "build", "EraCore.Cli/EraCore.Cli.csproj", "-c", "Debug", "-m:1", "--nologo"],
        cwd=str(ROOT_DIR), capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=timeout,
    )
    note = "build ok" if cp.returncode == 0 else "build FAILED"
    return cp.returncode == 0, note
