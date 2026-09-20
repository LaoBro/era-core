"""eracore_agent: one-call-one-turn CLI over the Headless HTTP API."""
import argparse
import json
import os
import secrets
import signal
import subprocess
import sys
import time
from pathlib import Path

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
from .maui import cmd_maui_status, cmd_maui_games, cmd_maui_load


SERVER_NOT_RUNNING = "server 未运行，先 `eracore_agent start`"


def _die(message: str, code: int = 1) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(code)


def _emit(payload) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _emit_compact(client, payload, compact: bool, pages: int = 3, tail: int | None = None, carry: dict | None = None) -> None:
    """compact=True 时用低噪紧凑视图（纯文本行 + 按钮 id/label + 截断元数据）替换 diff 输出。

    复用 GET /snapshot 在 agent 侧投影（页高取 /state 的 visibleRows）。窗口 = pages × pageRows
    页语义；tail 显式给出时为绝对行数。
    carry 为要并回视图的顶层控制字段（acquire 的 controller/turnsAdvanced、advance 的
    advancedCount/limitReached）——紧凑视图是投影不是原 payload 的子集，控制字段需显式携带，
    否则会被丢弃（advance 曾因此丢 advancedCount/limitReached）。
    投影失败（如 snapshot 未就绪）时降级为原 diff 输出。
    """
    if not compact:
        _emit(payload)
        return
    try:
        view = client.get_compact_view(pages=pages, tail=tail)
    except EmueraHttpError:
        _emit(payload)
        return
    # 用 turn/diff 的顶层 inputType/needValue 兜底；紧凑视图自身已带 state/generation。
    view["inputType"] = (payload or {}).get("inputType") or view.get("inputType")
    view["needValue"] = (payload or {}).get("needValue")
    if carry:
        view.update(carry)
    # 目标 1：错误 turn 的异常信息（error/errorInfo）是诊断核心，低噪投影不得丢弃。
    # 紧凑视图读 /snapshot，不含 turn 顶层错误字段——这里显式并回。
    for key in ("error", "errorInfo"):
        if key in (payload or {}) and payload.get(key) is not None:
            view[key] = payload[key]
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


def _display_report_flags(value: str) -> list[str]:
    """加载报告覆盖三态 → EraCore.Cli 启动 flag。

    off=--no-loading-report（强制关，默认——逐文件报告对回合输出是噪音）；
    on=--loading-report（强制开——逐文件报告 + 游戏目录 time.log 阶段计时，性能排查用）；
    auto=不传 flag（跟随游戏 emuera.config）。两 flag 在 Cli 侧互斥，此处单选不会冲突。
    """
    if value == "on":
        return ["--loading-report"]
    if value == "off":
        return ["--no-loading-report"]
    return []


def _start_eracore_server(binary_path: str, game_dir: str | None, port: int,
                          display_report: str = "off", settings: list[str] | None = None) -> subprocess.Popen:
    """Launch Emuera.Headless.Cli --server in the background."""
    if binary_path.lower().endswith(".dll"):
        cmd = ["dotnet", "exec", binary_path]
    else:
        cmd = [binary_path]
    cmd.extend(["--server", "--port", str(port)])
    if game_dir:
        cmd.extend(["--ExeDir", game_dir])
    # 加载报告覆盖：见 spec「启动日志覆盖」。仅对 agent 自己拉起的 server 生效；
    # 复用已运行 server 时以该 server 自己的启动参数为准。
    cmd.extend(_display_report_flags(display_report))
    # ADR-0030：--setting key=value 种子（可重复）——仅新拉起的 server 生效（headless 内存 store）。
    for setting in settings or []:
        cmd.extend(["--setting", setting])

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
            proc = _start_eracore_server(binary, game_dir, port, args.display_report, args.setting)
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
    confirm = {
        "controller": result.get("controller"),
        "state": result.get("state"),
        "turnsAdvanced": result.get("turnsAdvanced"),
    }
    if args.full:
        # 完整 turn（可能带复杂布局的 TUI/按钮 JSON），仅 debug 用
        confirm["turn"] = result.get("turn")
        _emit(confirm)
        return 0
    # 默认：低噪确认 = 紧凑画面(纯文本行+按钮+截断元数据) + 控制确认字段，不含完整 turn JSON，
    # 避免复杂布局的完整 turn 拖慢 agent 处理。carry 显式列键——confirm 未来加键不应自动进低噪视图。
    _emit_compact(
        client, confirm, compact=True, pages=args.pages, tail=args.tail,
        carry={
            "controller": confirm["controller"],
            "state": confirm["state"],
            "turnsAdvanced": confirm["turnsAdvanced"],
        })
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
    _emit_compact(client, turn, not args.full, pages=args.pages, tail=args.tail)
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
            view = client.get_compact_view(pages=args.pages, tail=args.tail)
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

    turns: list = []
    count = 0

    def emit_exit(stopped, limit_reached: bool) -> None:
        """统一出口：--full 原样四键（turns/stopped/advancedCount/limitReached）；
        默认紧凑 = 视图 + advancedCount/limitReached（两条分支同一形状）。"""
        _emit_compact(
            client,
            {"turns": turns, "stopped": stopped, "advancedCount": count, "limitReached": limit_reached},
            not args.full, pages=args.pages, tail=args.tail,
            carry={"advancedCount": count, "limitReached": limit_reached})

    # 当前等待态：若已是真实输入（或非翻页），不推进，直接返回当前态。
    try:
        snap = client.get_snapshot()
    except EmueraHttpError as exc:
        _fail_http(exc)
    if not _is_auto_advanceable(snap):
        emit_exit(snap, limit_reached=False)
        return 0

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
        # 停在需真实输入：--full 的 stopped 即所停回合。
        emit_exit(turn, limit_reached=False)
        return 0

    emit_exit(None, limit_reached=True)
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
        # 目标 3：显式信号——threadAlive（游戏循环 Task 存活）+ lastError（崩溃因果）
        "isRunning": state.get("isRunning"),
        "threadAlive": state.get("threadAlive"),
        "lastError": state.get("lastError"),
        "game": state,
        "activeSession": _session_active(state),
    })
    return 0


def _logs_path() -> Path:
    """agent.log 路径，与 C# AppDataPaths.Directory 默认值对齐（%LOCALAPPDATA%/Emuera/agent.log）。

    注意：假定服务端未改 AppDataPaths 目录（CLI/Server 均未调 Configure，走默认目录）。
    若未来引入平台注入目录，此处需要同步获取方式，避免与真实日志文件失配。
    """
    base = os.environ.get("LOCALAPPDATA") or ""
    root = Path(base) if base else Path.home()
    return root / "Emuera" / "agent.log"


def cmd_logs(args) -> int:
    """直读 agent.log 诊断日志（目标 2）。纯本地文件操作，无需 server 在线——崩溃后也能取现场。"""
    path = _logs_path()
    if not path.is_file():
        _die(f"未找到 agent.log（{path}）——server 尚未产生诊断日志？")
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        _die(f"读取 agent.log 失败: {exc}")
    lines = text.splitlines()
    total = len(lines)
    # 单一钳制：want → 归一化 tail（0..2000 且不超过 total）。
    # 负参／0 → tail=0（空列表），不再因 max(0) 落到"返回全量却截断标志为真"的边界 bug。
    want = args.tail if args.tail is not None else total
    tail = max(0, min(want, 2000, total))
    part = lines[total - tail:] if tail else []
    _emit({
        "file": str(path),
        "tail": tail,
        "totalLines": total,
        "truncated": tail < total,
        "lines": part,
    })
    return 0


def cmd_watch(_args) -> int:
    from .watch import watch_session

    client, _record = _require_server()
    return watch_session(client)


def cmd_settings(args) -> int:
    """settings 子命令（ADR-0030）——list / get <key> / set <key> <value> / reset，走 /settings 端点。

    生效语义：perf 覆盖/诊断旋钮下次 load-game 生效（ApplyTo）；displayReportOverride / agentLogEnabled 即时。
    --adb 时连接 Android MAUI 控制 server（logcat 解析端口 + adb forward，复用 maui 机制）。
    """
    if getattr(args, "adb", False):
        from .maui import _maui_client
        client, _record, _source = _maui_client(args)
    else:
        client, _record = _require_server()
    if args.settings_command == "list":
        _emit(client.get_settings())
        return 0
    if args.settings_command == "get":
        try:
            settings = client.get_settings()
        except EmueraHttpError as exc:
            _fail_http(exc)
        if args.key not in settings:
            _die(f"未知设置键: {args.key}（settings list 查看全部 13 键）")
        _emit({"key": args.key, "value": settings[args.key]})
        return 0
    if args.settings_command == "set":
        try:
            result = client.put_settings({args.key: args.value})
        except EmueraHttpError as exc:
            _fail_http(exc)
        _emit({"key": args.key, "value": result.get(args.key)})
        return 0
    if args.settings_command == "reset":
        _emit(client.reset_settings())
        return 0
    _die(f"未知 settings 子命令: {args.settings_command}")


def _add_view_window_args(parser) -> None:
    """四个紧凑视图命令共用的截断参数：页语义默认 + 绝对行数逃生口。"""
    parser.add_argument("--pages", type=int, default=3,
                        help="紧凑视图截取最近 N 页，1 页 = 服务端 visibleRows 行（默认 3）")
    parser.add_argument("--tail", type=int, default=None,
                        help="紧凑视图仅返回最近 N 行（绝对行数，优先于 --pages）")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="eracore_agent", description="Emuera 中转 CLI（一次调用 = 一个回合）")
    sub = parser.add_subparsers(dest="command", required=True)

    start = sub.add_parser("start", help="拉起或复用 server，加载游戏并返回初始 turn")
    start.add_argument("--port", type=int, default=8080, help="server 端口（默认 8080）")
    start.add_argument("--host", default="127.0.0.1", help="server 主机（默认 127.0.0.1）")
    start.add_argument("--game-dir", default=None, help="游戏目录")
    start.add_argument("--eracore-path", default=None, help="EraCore.Cli 二进制路径")
    start.add_argument("--display-report", choices=["auto", "off", "on"], default="off",
                       help="加载报告覆盖（仅对本次新拉起的 server 生效）：off=强制关（默认，隐藏启动读取日志）；"
                            "on=强制开（逐文件报告 + 游戏目录 time.log 阶段计时）；auto=跟随游戏配置")
    start.add_argument("--setting", action="append", default=[], metavar="KEY=VALUE",
                       help="全局设置种子（ADR-0030，仅对本次新拉起的 server 生效，可重复）："
                            "displayReportOverride/agentLogEnabled/sweepGatePolicy/safReadDegree/"
                            "maxParallelErbFiles/dimLockPolicy/lazyFunctionLoad/irDiskCacheLoad/"
                            "irFirstDirList/irFileListCache/srcContentCache/parallelErbLoad/parallelParseScript")
    start.set_defaults(func=cmd_start)

    acquire = sub.add_parser("acquire", help="获取控制权并返回状态确认（默认低噪紧凑视图）")
    acquire.add_argument("--full", action="store_true", help="输出完整 diff（默认紧凑视图）")
    _add_view_window_args(acquire)
    acquire.set_defaults(func=cmd_acquire)

    step = sub.add_parser("step", help="提交输入并读取下一回合（默认低噪紧凑视图）")
    step.add_argument("--value", required=True, help="提交给游戏的输入值")
    step.add_argument("--full", action="store_true", help="输出完整 diff（默认紧凑视图）")
    _add_view_window_args(step)
    step.set_defaults(func=cmd_step)

    advance = sub.add_parser("advance", help="自动代按回车推进翻页，直到需真实输入；返回全部被推进回合")
    advance.add_argument("--max-steps", type=int, default=50, help="推进步数上限（默认 50）")
    advance.add_argument("--full", action="store_true", help="输出完整 diff（默认紧凑视图）")
    _add_view_window_args(advance)
    advance.set_defaults(func=cmd_advance)

    view = sub.add_parser("view", help="只读输出当前屏幕低噪紧凑视图（纯文本行 + 按钮 id/label），不推进")
    view.add_argument("--full", action="store_true", help="输出完整 /snapshot 而非低噪紧凑视图")
    _add_view_window_args(view)
    view.set_defaults(func=cmd_view)

    release = sub.add_parser("release", help="释放控制权")
    release.set_defaults(func=cmd_release)

    stop = sub.add_parser("stop", help="结束会话；若由本 CLI 拉起则同时停止 server")
    stop.set_defaults(func=cmd_stop)

    status = sub.add_parser("status", help="汇总控制权与游戏状态")
    status.set_defaults(func=cmd_status)

    logs = sub.add_parser("logs", help="读取 agent.log 诊断日志（直读本机文件，无需 server 在线）")
    logs.add_argument("--tail", type=int, default=None,
                      help="仅返回最近 N 行（默认全量，上限 2000）")
    logs.set_defaults(func=cmd_logs)

    watch = sub.add_parser("watch", help="只读旁观终端输出")
    watch.set_defaults(func=cmd_watch)

    # ADR-0030：全局设置读写——list / get / set / reset，走 /settings 端点
    settings = sub.add_parser("settings", help="全局设置（ADR-0030）：list / get <key> / set <key> <value> / reset")
    settings_sub = settings.add_subparsers(dest="settings_command", required=True)

    settings_list = settings_sub.add_parser("list", help="列出全部 13 个设置键当前值")
    settings_list.add_argument("--adb", action="store_true",
                               help="连接 Android MAUI 控制 server（adb logcat 解析端口 + adb forward；缺省用本地 server 记录）")
    settings_list.set_defaults(func=cmd_settings)

    settings_get = settings_sub.add_parser("get", help="读取单个设置键")
    settings_get.add_argument("key")
    settings_get.add_argument("--adb", action="store_true",
                              help="连接 Android MAUI 控制 server（adb logcat 解析端口 + adb forward；缺省用本地 server 记录）")
    settings_get.set_defaults(func=cmd_settings)

    settings_set = settings_sub.add_parser("set", help="设置单个键（perf/诊断旋钮下次 load-game 生效；displayReport/agentLog 即时）")
    settings_set.add_argument("key")
    settings_set.add_argument("value")
    settings_set.add_argument("--adb", action="store_true",
                              help="连接 Android MAUI 控制 server（adb logcat 解析端口 + adb forward；缺省用本地 server 记录）")
    settings_set.set_defaults(func=cmd_settings)

    settings_reset = settings_sub.add_parser("reset", help="恢复默认（清 headless 内存 store，回出厂 auto）")
    settings_reset.add_argument("--adb", action="store_true",
                                help="连接 Android MAUI 控制 server（adb logcat 解析端口 + adb forward；缺省用本地 server 记录）")
    settings_reset.set_defaults(func=cmd_settings)

    # issues/13 M6：MAUI 控制面命令组——面向「app 已启动但还没加载游戏」的场景
    maui = sub.add_parser("maui", help="驱动 MAUI app 的控制面（未加载游戏时也可用）")
    maui_sub = maui.add_subparsers(dest="maui_command", required=True)

    maui_status = maui_sub.add_parser("status", help="查看 MAUI 控制 server 与会话状态")
    maui_status.add_argument("--adb", action="store_true",
                             help="强制走 adb logcat 解析端口（Android；缺省先试 Windows 发现记录）")
    maui_status.set_defaults(func=cmd_maui_status)

    maui_games = maui_sub.add_parser("games", help="列出主目录下的游戏（走 MAUI 宿主的 scan 实现）")
    maui_games.add_argument("--root-dir", default=None,
                            help="逃生口：指定主目录（缺省由 app 扫它自己当前生效的主目录——"
                                 "推荐，app 手上的路径会过期）")
    maui_games.add_argument("--adb", action="store_true",
                            help="强制走 adb logcat 解析端口（Android）")
    maui_games.set_defaults(func=cmd_maui_games)

    maui_load = maui_sub.add_parser("load", help="触发 MAUI 加载某个游戏（异步受理 + 轮询确认）")
    target = maui_load.add_mutually_exclusive_group(required=True)
    target.add_argument("--path", help="游戏目录绝对路径（Windows）或 SAF content URI（Android）")
    target.add_argument("--name", help="主目录下的游戏名（先取 app 的游戏列表解析出 fullPath）")
    maui_load.add_argument("--root-dir", default=None,
                           help="逃生口：--name 解析用的主目录（缺省由 app 扫自己当前生效的主目录）")
    maui_load.add_argument("--wait-timeout", type=float, default=15.0,
                           help="等待 gameDir 出现的超时秒数（默认 15；超时不算失败）")
    maui_load.add_argument("--adb", action="store_true",
                           help="强制走 adb logcat 解析端口（Android）")
    maui_load.set_defaults(func=cmd_maui_load)
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
