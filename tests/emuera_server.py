"""Shared helpers for Emuera.Headless server-mode tests."""
import json

# ADR-0016 / ADR-0013：Agent 协议版本，与 TurnRecord.CurrentProtocolVersion 一一对应。
# 升级协议时需手动同步此处——C# 侧改 TurnRecord.cs:CurrentProtocolVersion 后，
# 将本常量改至同一值。这种「双重确认」确保测试确实感知到协议变化。
# v8（issue 02）：PrintSegment 加 image/shape；DisplaySnapshot/DisplayDiff 加 bgImages；
# TurnOp 加 set_bg_image/remove_bg_image/clear_bg_image。
# v9（issue 07）：SegmentImage 加 crop?（裁切矩形几何）。
# v10：PrintSegment 加 underline?；v11：PrintSegment 加 strikeout?（PRINT_SLIDER 实际用删除线）。
# v12（issue 05 / 09）：七个旧顶层字段（state / inputType / needValue / timeLimit /
# displayTime / timeUpMessage / timedOut）退场，wire 上只剩唯一契约字段 `awaiting`
# （input / wait / ended 三键，见 AwaitInfo.cs）。本文件的断言侧桥接 helper 见下方
# turn_state / awaiting_input_type / awaiting_wait。
PROTOCOL_VERSION = 12

import os
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path


PROJECT_DIR = Path(__file__).resolve().parents[1]
TEST_GAME_DIR = PROJECT_DIR / "test_game"


# ===== v12 awaiting 语义桥接（wire 旧字段 → awaiting）=====
#
# v12（issue 09）起 turn / snapshot 顶层不再有 state / inputType / needValue /
# timeLimit / timedOut，只剩 `awaiting`（AwaitInfo.cs 三键）：
#   awaiting.input  = {type: "IntValue"|"EnterKey"|...}  期待客户端发输入
#   awaiting.wait   = {durationMs, displayTime?, timeUpMessage?, timedOut?}  有上界等待
#   awaiting.ended  = {terminal: "Quit"|"Error"}         互斥终态
# 下列 helper 让测试断言保持旧语义写法（state == "WaitInput" 等），一处映射、处处复用。

def awaiting_of(frame):
    """取 turn / snapshot 的 awaiting 对象（缺失时为空 dict）。"""
    return frame.get("awaiting") or {}


def turn_state(frame):
    """v12 语义桥接：从 awaiting 推导旧顶层 `state` 值。

    - awaiting.ended  → "Quit" / "Error"（terminal 原值）
    - awaiting.input  → "WaitInput"
    - 其余（仅 wait / 双空）→ None（旧口径的 Running/瞬态，等待由 input 维度表达）
    """
    aw = awaiting_of(frame)
    ended = aw.get("ended")
    if ended is not None:
        return ended.get("terminal")
    if aw.get("input") is not None:
        return "WaitInput"
    return None


def awaiting_input_type(frame):
    """v12 语义桥接：旧顶层 `inputType`（awaiting.input.type，无输入时为 None）。"""
    aw = awaiting_of(frame)
    inp = aw.get("input")
    return inp.get("type") if inp is not None else None


def awaiting_need_value(frame):
    """v12 语义桥接：旧顶层 `needValue`——期待输入即需要值（needValue = input 维度出现）。"""
    return awaiting_of(frame).get("input") is not None


def awaiting_wait(frame):
    """v12 语义桥接：旧顶层 TINPUT 计时元数据所在对象（awaiting.wait，无上界等待为 None）。"""
    return awaiting_of(frame).get("wait")


def awaiting_is_settled(frame):
    """帧是否处于停稳点（旧口径的「稳定 state」）：input 或 ended 出现即稳定。

    双空（awaiting:{}）只可能出现在 GET /snapshot 的「脚本还在跑」瞬态。
    """
    aw = awaiting_of(frame)
    return aw.get("input") is not None or aw.get("ended") is not None


def find_binary(project_dir=None):
    """Auto-detect Emuera binary. Search order:
    1. EMUERA_BINARY environment variable
    2. EraCore.Cli/bin/Debug/.../EraCore.Cli.exe  (issue 01 拆分后入口)
    3. EraCore.Cli/bin/Release/.../EraCore.Cli.exe
    4. Emuera.Headless/bin/Debug/.../Emuera.Headless.exe          (兼容旧构建产物)
    5. Emuera.Headless/bin/Release/.../Emuera.Headless.exe
    Returns (path, use_dotnet) tuple.
    """
    if project_dir is None:
        project_dir = PROJECT_DIR

    # 1. Environment variable
    env_binary = os.environ.get("EMUERA_BINARY")
    if env_binary and os.path.isfile(env_binary):
        use_dotnet = env_binary.endswith(".dll")
        return env_binary, use_dotnet

    # 2-3. issue 01 拆分后的新入口 EraCore.Cli
    cli_debug = os.path.join(
        project_dir, "EraCore.Cli", "bin", "Debug", "net10.0", "EraCore.Cli.exe"
    )
    if os.path.isfile(cli_debug):
        return cli_debug, False

    cli_release = os.path.join(
        project_dir, "EraCore.Cli", "bin", "Release", "net10.0", "EraCore.Cli.exe"
    )
    if os.path.isfile(cli_release):
        return cli_release, False

    # 4-5. 兼容旧 Emuera.Headless 构建产物（拆分前的 dev 机器可能残留）
    headless_debug = os.path.join(
        project_dir, "Emuera.Headless", "bin", "Debug", "net10.0", "Emuera.Headless.exe"
    )
    if os.path.isfile(headless_debug):
        return headless_debug, False

    headless_release = os.path.join(
        project_dir, "Emuera.Headless", "bin", "Release", "net10.0", "Emuera.Headless.exe"
    )
    if os.path.isfile(headless_release):
        return headless_release, False

    raise FileNotFoundError(
        f"Cannot find Emuera binary. Searched:\n"
        f"  - EMUERA_BINARY env var\n"
        f"  - {cli_debug}\n"
        f"  - {cli_release}\n"
        f"  - {headless_debug}\n"
        f"  - {headless_release}\n"
        f"Set EMUERA_BINARY or build the project first."
    )


class ServerProcess:
    def __init__(self, proc, port, game_dir=None):
        self.proc = proc
        self.port = port
        self.base_url = f"http://localhost:{port}"
        # T-025：缓存 game_dir，供 start_session() 自动 load_game 使用
        self.game_dir = game_dir

    def request(self, method, path, body=None, timeout=35, headers=None):
        data = None if body is None else json.dumps(body).encode("utf-8")
        req_headers = {}
        if body is not None:
            req_headers["Content-Type"] = "application/json"
        if headers:
            req_headers.update(headers)
        req = urllib.request.Request(
            f"{self.base_url}{path}",
            data=data,
            method=method,
            headers=req_headers,
        )
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return resp.status, resp.read().decode("utf-8")
        except urllib.error.HTTPError as e:
            return e.code, e.read().decode("utf-8")

    def create_session(self):
        return self.request("POST", "/session")

    def start_session(self):
        """T-025：建立会话的推荐方式——空闲态 POST /session 返 503，需走 POST /load-game。
        使用 start_server 时缓存的 game_dir。返回 (200, body) 成功。"""
        if self.game_dir is None:
            raise RuntimeError("start_session 需要 game_dir——start_server(game_dir) 时缓存")
        return self.load_game(self.game_dir)

    def delete_session(self):
        return self.request("DELETE", "/session")

    def get_turn(self, timeout=35):
        return self.request("GET", "/turn", timeout=timeout)

    def post_input(self, value):
        return self.request("POST", "/input", {"value": value})

    def get_state(self):
        return self.request("GET", "/state")

    def get_snapshot(self, timeout=35):
        return self.request("GET", "/snapshot", timeout=timeout)

    def load_game(self, game_dir):
        """T-025：POST /load-game 辅助方法——issue 05 端点。"""
        return self.request("POST", "/load-game", {"gameDir": game_dir})

    def close(self):
        if self.proc.poll() is None:
            try:
                self.proc.stdin.write("\n")
                self.proc.stdin.flush()
            except Exception:
                pass
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
                self.proc.wait(timeout=5)


def diff_text(turn):
    """Extract concatenated text from v5 turn diff.lineOps for text-content assertions.

    Returns empty string when diff is null/absent (first turn or no-op turn).
    For first-turn content checks, fetch GET /snapshot and use snapshot_text() instead.

    plan C (v5): diff.lineOps carries append/clear_line_diff/clear_screen.
    only append.newLines carry text. clear_line_diff (clearCount) and clear_screen are
    pure tail-cuts / full-clears (no text).
    """
    diff = turn.get("diff")
    if diff is None:
        return ""
    parts = []
    for op in diff.get("lineOps", []):
        op_type = op.get("type")
        if op_type == "append":
            lines = op.get("newLines", [])
        else:
            continue  # clear_line_diff / clear_screen are pure tail-cut / full-clear (no text)
        for line in lines:
            for entry in line.get("entries", []):
                for seg in entry.get("segments", []):
                    parts.append(seg.get("text", ""))
    return " ".join(parts)


def snapshot_text(snap):
    """Extract concatenated text from DisplaySnapshot lines[].entries[].segments[].text."""
    return " ".join(
        seg.get("text", "")
        for line in snap.get("lines", [])
        for entry in line.get("entries", [])
        for seg in entry.get("segments", [])
    )


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def wait_for_port(port, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with socket.create_connection(("localhost", port), timeout=0.2):
                return
        except OSError:
            time.sleep(0.2)
    raise TimeoutError(f"Server did not listen on port {port}")


def _drain_stdout(proc):
    """持续排空子进程 stdout 管道，防止缓冲写满阻塞。

    I-11 根因：server 的 Console.WriteLine / Console.Out.Flush() 诊断日志（Preload/Initialize
    阶段）每次加载游戏约 2.5KB（经验值，随游戏规模变化）；Windows PIPE 默认缓冲约 4KB——
    测试从不读取 stdout 时，第二个 session 的加载日志会把缓冲写满，Flush() 阻塞 → server
    卡死（GET /turn 超时）。后台线程读掉 stdout 即消除阻塞；诊断日志本身对测试无消费需求，
    直接丢弃。
    """
    try:
        for _line in iter(proc.stdout.readline, ""):
            pass
    except (OSError, ValueError):
        # 管道已关闭 / 文本解码失败（UnicodeDecodeError 是 ValueError 子类）：
        # 排空线程属尽力而为，结束即止。其余未预期异常不吞——让 traceback 打印以便诊断。
        pass


def start_server(game_dir=None, project_dir=None, binary=None, port=None, extra_env=None, extra_args=None):
    """启动 Emuera.Headless server。

    T-025：game_dir 改为可选——None 时不传 --ExeDir，server 进入空闲启动模式
    （Program.Main 跳过 Validate，等待 /load-game 加载游戏）。
    extra_env：合并进子进程环境（如 EMUERA_CONTROL_LEASE_SECONDS）。
    extra_args：追加到启动命令（实现票 25，如 ["--profile", "eratw"]）。
    """
    if project_dir is None:
        project_dir = PROJECT_DIR
    if port is None:
        port = free_port()

    binary_path, use_dotnet = find_binary(str(project_dir)) if binary is None else (binary, binary.endswith(".dll"))
    cmd = ["dotnet", "exec", binary_path] if use_dotnet else [binary_path]
    cmd.extend(["--server", "--port", str(port)])
    if game_dir is not None:
        cmd.extend(["--ExeDir", str(game_dir)])
    if extra_args:
        cmd.extend(str(a) for a in extra_args)

    env = os.environ.copy()
    if extra_env:
        env.update(extra_env)

    print(f"[EmueraServer] Starting: {' '.join(cmd)}")
    proc = subprocess.Popen(
        cmd,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        encoding="utf-8",
        errors="replace",
        env=env,
    )
    # 立即启动 stdout 排空线程（早于 wait_for_port）：PIPE 缓冲写满会让 server 的
    # Console.Out.Flush() 阻塞——第二个 session 加载时必然触发（见 _drain_stdout 注释）。
    threading.Thread(target=_drain_stdout, args=(proc,), daemon=True).start()
    try:
        wait_for_port(port)
    except Exception:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        raise

    return ServerProcess(proc, port, game_dir=str(game_dir) if game_dir is not None else None)


def copy_test_game_with_erb(erb_text):
    temp_dir = tempfile.mkdtemp(prefix="emuera_server_test_")
    game_dir = Path(temp_dir) / "game"
    shutil.copytree(TEST_GAME_DIR, game_dir)
    (game_dir / "erb" / "TEST.ERB").write_text(erb_text, encoding="utf-8")
    return temp_dir, str(game_dir)
