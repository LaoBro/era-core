"""eracore_agent: one-call-one-turn CLI over the Headless HTTP API."""
import argparse
import json
import os
import secrets
import signal
import subprocess
import sys
import time

from .config import (
    PROJECT_DIR,
    delete_server_record,
    load_config,
    load_maui_server_record,
    load_server_record,
    resolve_path,
    save_server_record,
)
from .emuera_client import EmueraClient, EmueraHttpError


SERVER_NOT_RUNNING = "server 未运行，先 `eracore_agent start`"


def _die(message: str, code: int = 1) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(code)


def _emit(payload) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _emit_compact(client, payload, compact: bool, tail: int | None = None) -> None:
    """compact=True 时用低噪紧凑视图（纯文本行 + 按钮 id/label）替换 diff 输出。

    复用 GET /snapshot 在 agent 侧投影，不要服务端改动。tail 截断最近 N 行。
    投影失败（如 snapshot 未就绪）时降级为原 diff 输出。
    """
    if not compact:
        _emit(payload)
        return
    try:
        view = client.get_compact_view(tail=tail)
    except EmueraHttpError:
        _emit(payload)
        return
    # 用 turn/diff 的顶层 inputType/needValue 兜底；紧凑视图自身已带 state/generation。
    view["inputType"] = (payload or {}).get("inputType") or view.get("inputType")
    view["needValue"] = (payload or {}).get("needValue")
    _emit(view)


def _base_url(record) -> str:
    host = record.get("host") or "127.0.0.1"
    return f"http://{host}:{record['port']}"


def _probe(client: EmueraClient):
    try:
        return client.get_state()
    except EmueraHttpError:
        return None


def _require_server():
    record = load_server_record()
    if not record or "port" not in record:
        _die(SERVER_NOT_RUNNING)
    client = EmueraClient(_base_url(record), token=record.get("token"))
    if _probe(client) is None:
        _die(SERVER_NOT_RUNNING)
    return client, record


def _fail_http(exc: EmueraHttpError) -> None:
    if exc.code == "CONNECTION_FAILED" or exc.status == 0:
        _die(SERVER_NOT_RUNNING)
    if exc.code == "CONTROL_HELD_BY_USER":
        _die("控制权由用户持有，等待 control wait 或询问用户")
    if exc.code == "CONTROL_HELD":
        _die("控制权由其他 agent 持有")
    if exc.code == "CONTROL_LOST":
        _die("控制权已丢失（CONTROL_LOST），停止操作")
    message = ""
    error = exc.payload.get("error")
    if isinstance(error, dict):
        message = error.get("message") or ""
    elif isinstance(exc.payload.get("message"), str):
        message = exc.payload["message"]
    detail = f"{exc.code}" + (f": {message}" if message else "")
    _die(detail)


def _find_binary(explicit: str | None) -> str | None:
    if explicit:
        return resolve_path(explicit)
    env_binary = os.environ.get("EMUERA_BINARY")
    if env_binary and os.path.isfile(env_binary):
        return env_binary
    config = load_config() or {}
    configured = config.get("binaryPath") or config.get("dllPath")
    if configured:
        resolved = resolve_path(configured)
        if os.path.isfile(resolved):
            return resolved
    candidates = (
        os.path.join(PROJECT_DIR, "EraCore.Cli", "bin", "Debug", "net10.0", "EraCore.Cli.exe"),
        os.path.join(PROJECT_DIR, "EraCore.Cli", "bin", "Release", "net10.0", "EraCore.Cli.exe"),
    )
    for path in candidates:
        if os.path.isfile(path):
            return path
    return None


def _start_eracore_server(binary_path: str, game_dir: str | None, port: int) -> subprocess.Popen:
    """Launch Emuera.Headless.Cli --server in the background."""
    if binary_path.lower().endswith(".dll"):
        cmd = ["dotnet", "exec", binary_path]
    else:
        cmd = [binary_path]
    cmd.extend(["--server", "--port", str(port)])
    if game_dir:
        cmd.extend(["--ExeDir", game_dir])
    # 隐藏启动读取日志（agent 用）：覆盖游戏配置 DisplayReport=false，见 spec「启动日志覆盖」。
    # 仅对 agent 自己拉起的 server 生效；复用已运行 server 时不受影响。
    cmd.extend(["--no-loading-report"])

    kwargs = {
        "stdout": subprocess.DEVNULL,
        "stderr": subprocess.DEVNULL,
        "stdin": subprocess.DEVNULL,
    }
    if sys.platform == "win32":
        flags = subprocess.CREATE_NEW_PROCESS_GROUP
        no_window = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        kwargs["creationflags"] = flags | no_window
    else:
        kwargs["start_new_session"] = True
    return subprocess.Popen(cmd, **kwargs)


def _wait_for_state(client: EmueraClient, timeout: float = 30.0):
    deadline = time.time() + timeout
    last_error = None
    while time.time() < deadline:
        try:
            return client.get_state()
        except EmueraHttpError as exc:
            last_error = exc
            time.sleep(0.2)
    if last_error:
        _fail_http(last_error)
    _die("server 启动超时")


def _read_turn(client: EmueraClient, timeout: float = 35.0):
    deadline = time.time() + timeout
    last_empty = None
    while time.time() < deadline:
        try:
            turn = client.get_turn(timeout=min(35.0, max(1.0, deadline - time.time())))
        except EmueraHttpError as exc:
            _fail_http(exc)
        if turn is not None:
            return turn
        last_empty = time.time()
        time.sleep(0.1)
    _die("GET /turn 超时（204）" + (f" at {last_empty}" if last_empty else ""))


def _session_active(state: dict) -> bool:
    if not state:
        return False
    if state.get("isRunning") is False:
        return False
    if state.get("state") in (None, "Idle", "Quit", "Error"):
        return False
    return bool(state.get("sessionId"))


def cmd_start(args) -> int:
    port = args.port
    host = args.host
    game_dir = args.game_dir
    if game_dir:
        game_dir = resolve_path(game_dir)
    else:
        config = load_config() or {}
        configured = config.get("gameDir")
        game_dir = resolve_path(configured) if configured else os.path.join(PROJECT_DIR, "test_game")

    existing = load_server_record()
    reused = False
    started_by_agent = False
    maui_hosted = False
    token = None
    pid = None

    # issue 05（H 缺陷修复）：MAUI 托管 server 发现**优先于**本地记录。
    # MAUI 是用户正在玩的那一局的权威单实例——它重开时端口/token 都会变，本地 .eracore-server.json
    # 可能是旧会话残留。先探 MAUI 发现记录（%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json），
    # 命中即复用（单实例共享，agent 连的是用户正在玩的那一局，不另起 server）。
    # 只有 MAUI 记录缺失或探活失败时，才回落本地 existing 记录。
    maui_record = load_maui_server_record()
    if maui_record and maui_record.get("port"):
        maui_host = maui_record.get("host") or "127.0.0.1"
        maui_client = EmueraClient(
            f"http://{maui_host}:{maui_record['port']}", token=maui_record.get("token"))
        if _probe(maui_client) is not None:
            reused = True
            port = maui_record["port"]
            host = maui_host
            token = maui_record.get("token") or secrets.token_urlsafe(24)
            started_by_agent = False
            maui_hosted = True
            pid = None
            game_dir = maui_record.get("gameDir") or game_dir

    if not reused and existing and existing.get("port"):
        token = existing.get("token")
        probe_client = EmueraClient(_base_url(existing), token=token)
        if _probe(probe_client) is not None:
            reused = True
            port = existing["port"]
            host = existing.get("host") or host
            pid = existing.get("pid")
            started_by_agent = bool(existing.get("startedByAgent"))
            maui_hosted = bool(existing.get("mauiHosted"))
            if not token:
                token = secrets.token_urlsafe(24)

    if not reused:
        probe_client = EmueraClient(f"http://{host}:{port}")
        if _probe(probe_client) is not None:
            reused = True
            token = secrets.token_urlsafe(24)
            started_by_agent = False
            pid = None
        else:
            binary = _find_binary(args.eracore_path)
            if not binary or not os.path.isfile(binary):
                _die("找不到 Emuera 二进制，请传 --eracore-path 或在 .eracore-agent.json 配置 binaryPath")
            if not os.path.isdir(game_dir):
                _die(f"游戏目录不存在: {game_dir}")
            token = secrets.token_urlsafe(24)
            proc = _start_eracore_server(binary, game_dir, port)
            if proc.poll() is not None:
                _die("Emuera server 启动后立即退出")
            pid = proc.pid
            started_by_agent = True

    record = {
        "host": host,
        "port": port,
        "pid": pid,
        "token": token,
        "gameDir": game_dir,
        "startedByAgent": started_by_agent,
        "mauiHosted": maui_hosted,
    }
    save_server_record(record)

    client = EmueraClient(f"http://{host}:{port}", token=token)
    state = _wait_for_state(client)
    # issue 05：MAUI 托管 server 的会话生命周期归 MAUI 应用——即便当前无活跃会话（用户退出/结束）
    # 也不自动 load_game 抢占（避免替用户重开一局）；直接上报当前状态。
    if not _session_active(state) and not maui_hosted:
        try:
            client.load_game(game_dir)
        except EmueraHttpError as exc:
            _fail_http(exc)
        turn = _read_turn(client)
        _emit(turn)
        return 0

    try:
        snapshot = client.get_snapshot()
    except EmueraHttpError:
        _emit(state)
        return 0
    _emit({
        "state": snapshot.get("state"),
        "inputType": snapshot.get("inputType"),
        "needValue": snapshot.get("needValue"),
        "protocolVersion": snapshot.get("protocolVersion"),
        "diff": None,
    })
    return 0


def cmd_acquire(args) -> int:
    client, _record = _require_server()
    try:
        result = client.acquire_control()
    except EmueraHttpError as exc:
        _fail_http(exc)
    payload = {
        "controller": result.get("controller"),
        "state": result.get("state"),
        "turn": result.get("turn"),
        "turnsAdvanced": result.get("turnsAdvanced"),
    }
    # acquire 是「获取控制权 + 返回状态确认」的命令，须输出控制器与末位回合的结构化字段；
    # 不走 _emit_compact（紧凑屏幕视图会丢弃 controller/turn/turnsAdvanced）。
    _emit(payload)
    return 0


def cmd_step(args) -> int:
    client, _record = _require_server()
    try:
        control = client.get_control()
    except EmueraHttpError as exc:
        _fail_http(exc)
    controller = control.get("controller") or {}
    if controller.get("kind") != "agent":
        _die("当前未持有控制权，先 `eracore_agent acquire`")
    try:
        client.post_input(args.value)
        turn = _read_turn(client)
    except EmueraHttpError as exc:
        _fail_http(exc)
    _emit_compact(client, turn, not args.full, args.tail)
    return 0


def cmd_view(args) -> int:
    client, _record = _require_server()
    # 防偷窥 / 排他语义：读取当前画面需先获取控制权，校验己方为 Controller。
    # 读取后**不自动 release**——保持持有直到任务完成主动 `release`，或 user 强行夺回（CONTROL_LOST）。
    # 否则"一闪而过"既锁不住画面，也无法支撑用户与 agent 交替操作的真实需求。
    try:
        client.acquire_control()
    except EmueraHttpError as exc:
        # CONTROL_HELD_BY_USER / CONTROL_HELD 在此即防偷窥拦截——不让无权者读到画面。
        _fail_http(exc)
    try:
        if args.full:
            view = client.get_snapshot()
        else:
            view = client.get_compact_view(tail=args.tail)
    except EmueraHttpError as exc:
        _fail_http(exc)
    _emit(view)
    return 0


def cmd_release(_args) -> int:
    client, _record = _require_server()
    try:
        result = client.release_control()
    except EmueraHttpError as exc:
        _fail_http(exc)
    _emit(result)
    return 0


# 自动推进只针对"按回车/任意键显示下一句"的翻页等待（needValue=false）。
_AUTO_ADVANCE_TYPES = {"EnterKey", "AnyKey"}


def _is_auto_advanceable(state: dict) -> bool:
    """是否需要真实输入之外的空输入推进（EnterKey/AnyKey 且 needValue=false）。"""
    return state.get("needValue") is False and state.get("inputType") in _AUTO_ADVANCE_TYPES


def cmd_advance(args) -> int:
    client, _record = _require_server()
    try:
        control = client.get_control()
    except EmueraHttpError as exc:
        _fail_http(exc)
    controller = control.get("controller") or {}
    if controller.get("kind") != "agent":
        _die("当前未持有控制权，先 `eracore_agent acquire`")

    # 当前等待态：若已是真实输入（或非翻页），不推进，直接返回当前态。
    try:
        snap = client.get_snapshot()
    except EmueraHttpError as exc:
        _fail_http(exc)
    if not _is_auto_advanceable(snap):
        # 非翻页：不推进。默认 stopped 用紧凑投影；--full 时原样。
        if not args.full:
            stopped = client.get_compact_view(tail=args.tail)
            _emit({"turns": [], "stopped": stopped, "advancedCount": 0, "limitReached": False})
        else:
            _emit({"turns": [], "stopped": snap, "advancedCount": 0, "limitReached": False})
        return 0

    turns: list = []
    count = 0
    max_steps = args.max_steps
    # 循环推进：每次提交空输入（回车），收集被推进回合；直到需真实输入或离开 WaitInput。
    while count < max_steps:
        try:
            client.post_input("")
            turn = _read_turn(client)
        except EmueraHttpError as exc:
            # CONTROL_LOST / CONTROL_HELD_* 等走 _fail_http：立即停止并原样上报，不重试。
            _fail_http(exc)
        count += 1
        if _is_auto_advanceable(turn):
            turns.append(turn)
            continue
        _emit_compact(client, {"turns": turns, "advancedCount": count, "limitReached": False}, not args.full, args.tail)
        return 0

    _emit_compact(client, {"turns": turns, "advancedCount": count, "limitReached": True}, not args.full, args.tail)
    return 0


def _kill_pid(pid) -> None:
    if not pid:
        return
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return
    if sys.platform == "win32":
        subprocess.run(
            ["taskkill", "/PID", str(pid), "/T", "/F"],
            capture_output=True,
            timeout=15,
        )
        return
    try:
        os.kill(pid, signal.SIGTERM)
    except OSError:
        return


def cmd_stop(_args) -> int:
    record = load_server_record()
    if not record or "port" not in record:
        _die(SERVER_NOT_RUNNING)
    # issue 05：MAUI 托管的 server 生命周期归 MAUI 应用（用户关闭游戏/退出应用时随会话结束）——
    # stop 只清本地 .eracore-server.json，不 DELETE /session（会杀掉用户正在玩的会话）、不杀进程。
    if record.get("mauiHosted"):
        delete_server_record()
        _emit({"stopped": True, "session": None, "serverStopped": False, "mauiHosted": True})
        return 0
    client = EmueraClient(_base_url(record), token=record.get("token"))
    result = {"removed": False}
    if _probe(client) is not None:
        try:
            result = client.delete_session()
        except EmueraHttpError as exc:
            if exc.status not in (0, 404) and exc.code != "CONNECTION_FAILED":
                _fail_http(exc)
    if record.get("startedByAgent"):
        _kill_pid(record.get("pid"))
        deadline = time.time() + 10
        while time.time() < deadline and _probe(client) is not None:
            time.sleep(0.2)
    delete_server_record()
    _emit({"stopped": True, "session": result, "serverStopped": bool(record.get("startedByAgent"))})
    return 0


def cmd_status(_args) -> int:
    client, _record = _require_server()
    try:
        control = client.get_control()
        state = client.get_state()
    except EmueraHttpError as exc:
        _fail_http(exc)
    _emit({
        "controller": control.get("controller"),
        "controlState": control.get("state"),
        "state": state.get("state"),
        "game": state,
        "activeSession": _session_active(state),
    })
    return 0


def cmd_watch(_args) -> int:
    from .watch import watch_session

    client, _record = _require_server()
    return watch_session(client)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="eracore_agent", description="Emuera 中转 CLI（一次调用 = 一个回合）")
    sub = parser.add_subparsers(dest="command", required=True)

    start = sub.add_parser("start", help="拉起或复用 server，加载游戏并返回初始 turn")
    start.add_argument("--port", type=int, default=8080, help="server 端口（默认 8080）")
    start.add_argument("--host", default="127.0.0.1", help="server 主机（默认 127.0.0.1）")
    start.add_argument("--game-dir", default=None, help="游戏目录")
    start.add_argument("--eracore-path", default=None, help="EraCore.Cli 二进制路径")
    start.set_defaults(func=cmd_start)

    acquire = sub.add_parser("acquire", help="获取控制权并返回状态确认（默认低噪紧凑视图）")
    acquire.add_argument("--full", action="store_true", help="输出完整 diff（默认紧凑视图）")
    acquire.add_argument("--tail", type=int, default=None, help="紧凑视图仅返回最近 N 行（默认全部）")
    acquire.set_defaults(func=cmd_acquire)

    step = sub.add_parser("step", help="提交输入并读取下一回合（默认低噪紧凑视图）")
    step.add_argument("--value", required=True, help="提交给游戏的输入值")
    step.add_argument("--full", action="store_true", help="输出完整 diff（默认紧凑视图）")
    step.add_argument("--tail", type=int, default=None, help="紧凑视图仅返回最近 N 行（默认全部）")
    step.set_defaults(func=cmd_step)

    advance = sub.add_parser("advance", help="自动代按回车推进翻页，直到需真实输入；返回全部被推进回合")
    advance.add_argument("--max-steps", type=int, default=50, help="推进步数上限（默认 50）")
    advance.add_argument("--full", action="store_true", help="输出完整 diff（默认紧凑视图）")
    advance.add_argument("--tail", type=int, default=None, help="紧凑视图仅返回最近 N 行（默认全部）")
    advance.set_defaults(func=cmd_advance)

    view = sub.add_parser("view", help="只读输出当前屏幕低噪紧凑视图（纯文本行 + 按钮 id/label），不推进")
    view.add_argument("--tail", type=int, default=None, help="仅返回最近 N 行（默认全部）")
    view.add_argument("--full", action="store_true", help="输出完整 /snapshot 而非低噪紧凑视图")
    view.set_defaults(func=cmd_view)

    release = sub.add_parser("release", help="释放控制权")
    release.set_defaults(func=cmd_release)

    stop = sub.add_parser("stop", help="结束会话；若由本 CLI 拉起则同时停止 server")
    stop.set_defaults(func=cmd_stop)

    status = sub.add_parser("status", help="汇总控制权与游戏状态")
    status.set_defaults(func=cmd_status)

    watch = sub.add_parser("watch", help="只读旁观终端输出")
    watch.set_defaults(func=cmd_watch)
    return parser


def main(argv=None) -> None:
    parser = build_parser()
    args = parser.parse_args(argv)
    try:
        code = args.func(args)
    except KeyboardInterrupt:
        print("已中断", file=sys.stderr)
        raise SystemExit(130)
    raise SystemExit(code if code is not None else 0)


if __name__ == "__main__":
    main()
