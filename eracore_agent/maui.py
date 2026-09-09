"""issues/13 M6：驱动 MAUI 应用的控制面命令（status / games / load）。

与既有子命令的差异：既有命令面向「server 已在跑、会话已有」的场景（``.eracore-server.json``，
由 ``start`` 写入）；本模块面向「MAUI app 已启动但还没加载游戏」的场景——
MauiControlServer（app 级）在启动期就写好发现记录（Windows）或打 logcat 标记行（Android），
agent 因此可以在**未加载游戏**时列出游戏并触发加载。

端口 / token / 主目录来源：

- Windows：``%LOCALAPPDATA%\\EmueraCore\\eracore-maui-server.json``
- Android：``adb logcat -d -s EmueraMaui`` 里的 ``eracore-control-server`` 行，
  解析出端口后先 ``adb forward`` 再走本机 HTTP（发现文件在 app 私有目录，PC 不可读）。

加载语义：``POST /load-game`` 在 MAUI 宿主下是**异步受理**（C# 转 Vue 代加载，走与手点
相同的 OnReloadGame 链路），因此 ``load`` 默认轮询 ``/state`` 等 ``gameDir`` 变为目标目录。
"""

from __future__ import annotations

import json
import re
import subprocess
import sys
import time
from typing import Any, Dict, Optional

from .config import load_maui_server_record
from .emuera_client import EmueraClient, EmueraHttpError

LOGCAT_TAG = "EmueraMaui"
CONTROL_MARKER = "eracore-control-server"

# gameDir= 与 mainGameDir= 共享 "gameDir=" 子串——负向断言避免误匹配
_RE_PORT = re.compile(r"port=(\d+)")
_RE_TOKEN = re.compile(r"token=([0-9a-fA-F]+)")
_RE_MAIN_DIR = re.compile(r"mainGameDir=(\S+)")
_RE_GAME_DIR = re.compile(r"(?<!main)gameDir=(\S+)")


def _die(message: str, code: int = 1) -> None:
    print(message, file=sys.stderr)
    raise SystemExit(code)


def _emit(payload) -> None:
    sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def _windows_record() -> Optional[Dict[str, Any]]:
    """读 MAUI 发现记录（Windows）。Android 上此函数返回 None（config 层已按平台守卫）。"""
    rec = load_maui_server_record()
    if rec and rec.get("port"):
        return {
            "host": rec.get("host") or "127.0.0.1",
            "port": rec["port"],
            "token": rec.get("token"),
            "mainGameDir": rec.get("mainGameDir"),
        }
    return None


def _android_record() -> Optional[Dict[str, Any]]:
    """从 adb logcat 历史缓冲解析控制 server 端口/token/主目录，并建立 adb forward。

    用 ``-d``（dump 后退出）读**历史**而非跟随——app 启动时打的标记行大概率仍在缓冲里，
    无需清缓冲重抓（清了反而丢掉唯一线索）。取最后一次出现的值（app 重启后端口会变）。
    """
    try:
        proc = subprocess.run(
            ["adb", "logcat", "-d", "-s", LOGCAT_TAG],
            capture_output=True, text=True, timeout=10)
        if proc.returncode != 0:
            return None
    except (OSError, subprocess.TimeoutExpired):
        return None

    port: Optional[int] = None
    token: Optional[str] = None
    main_dir: Optional[str] = None
    for line in proc.stdout.splitlines():
        if CONTROL_MARKER not in line:
            continue
        m = _RE_PORT.search(line)
        if m:
            port = int(m.group(1))
        m = _RE_TOKEN.search(line)
        if m:
            token = m.group(1)
        m = _RE_MAIN_DIR.search(line)
        if m:
            main_dir = None if m.group(1) == "null" else m.group(1)
    if not port:
        return None

    # PC → 设备的端口转发；失败不致命（模拟器/已转发场景可能成功连接）
    subprocess.run(["adb", "forward", f"tcp:{port}", f"tcp:{port}"],
                   capture_output=True, timeout=10)
    return {"host": "127.0.0.1", "port": port, "token": token, "mainGameDir": main_dir}


def _resolve_record(prefer_adb: bool) -> tuple[Optional[Dict[str, Any]], Optional[str]]:
    """Windows 记录优先；``--adb`` 或 Windows 记录缺失时尝试 adb logcat。

    返回 (record, source)；两者都拿不到时返回 (None, None)。
    """
    if not prefer_adb:
        rec = _windows_record()
        if rec:
            return rec, "windows"
    rec = _android_record()
    if rec:
        return rec, "android"
    return None, None


def _maui_client(args) -> tuple[EmueraClient, Dict[str, Any], str]:
    record, source = _resolve_record(getattr(args, "adb", False))
    if not record:
        _die(
            "MAUI 控制 server 未就绪：Windows 发现记录不存在，adb logcat 也未解析到端口行。\n"
            "确认 MAUI app 已启动到游戏列表页（发现记录在那时写入）。",
            1)
    client = EmueraClient(f"http://{record['host']}:{record['port']}", token=record.get("token"))
    # 探活：区分「记录是上次运行的残留（app 已退出）」与「记录新鲜但别的问题」
    try:
        client.get_state(timeout=2.0)
    except EmueraHttpError as exc:
        _die(
            f"MAUI 控制 server 探活失败（{record['host']}:{record['port']}）：{exc}\n"
            "app 可能已退出（发现记录是上次运行的残留）——重启 MAUI app 后重试。",
            1)
    return client, record, source


def cmd_maui_status(args) -> int:
    """查看 MAUI 控制 server 与会话状态——S2 验收的探活命令。"""
    client, record, source = _maui_client(args)
    try:
        state = client.get_state(timeout=5.0)
    except EmueraHttpError as exc:
        _die(f"GET /state 失败: {exc}", 1)
    _emit({
        "source": source,
        "port": record["port"],
        "mainGameDir": record.get("mainGameDir"),
        "state": state.get("state"),
        "gameDir": state.get("gameDir"),
        "threadAlive": state.get("threadAlive"),
    })
    return 0


def cmd_maui_games(args) -> int:
    """列出主目录下的游戏——走 MAUI 宿主的 scan 实现（Android SAF 可用）。"""
    client, record, _source = _maui_client(args)
    root = args.root_dir or record.get("mainGameDir")
    if not root:
        _die("未指定 --root-dir，且 app 未同步主目录（MAUI 尚未完成首次 scanGames）", 1)
    try:
        status, payload, _raw = client.request("POST", "/game/scan", {"rootDir": root})
    except EmueraHttpError as exc:
        _die(f"POST /game/scan 失败: {exc}", 1)
    if status != 200:
        _die(f"POST /game/scan 返回 {status}: {payload}", 1)
    _emit({
        "rootDir": root,
        "rootDirExists": payload.get("rootDirExists"),
        "games": payload.get("games") or [],
    })
    return 0


def cmd_maui_load(args) -> int:
    """触发加载——``--path`` 直连；``--name`` 先 scan 主目录再按名解析出 fullPath。"""
    client, record, _source = _maui_client(args)

    if args.path:
        game_dir = args.path
    elif args.name:
        root = args.root_dir or record.get("mainGameDir")
        if not root:
            _die("--name 需要主目录：传 --root-dir，或等 MAUI 完成首次 scanGames", 1)
        try:
            status, payload, _raw = client.request("POST", "/game/scan", {"rootDir": root})
        except EmueraHttpError as exc:
            _die(f"POST /game/scan 失败: {exc}", 1)
        if status != 200:
            _die(f"POST /game/scan 返回 {status}: {payload}", 1)
        games = payload.get("games") or []
        matches = [g for g in games
                   if str(g.get("name", "")).lower() == args.name.lower()]
        if not matches:
            names = ", ".join(str(g.get("name", "?")) for g in games)
            _die(f"未找到名为 {args.name!r} 的游戏（可用: {names or '（空）'}）", 1)
        game_dir = matches[0]["fullPath"]
    else:
        _die("需要 --path 或 --name 之一", 1)

    try:
        accepted = client.load_game(game_dir)
    except EmueraHttpError as exc:
        _die(f"POST /load-game 失败: {exc}", 1)

    # 受理是异步的——轮询 /state 等 gameDir 变为目标目录（超时不算失败：加载可能仍在前台进行）
    deadline = time.monotonic() + args.wait_timeout
    state: Optional[Dict[str, Any]] = None
    while time.monotonic() < deadline:
        try:
            state = client.get_state(timeout=5.0)
        except EmueraHttpError:
            state = None
        if state and state.get("gameDir"):
            break
        time.sleep(0.5)

    _emit({
        "accepted": accepted,
        "requested": game_dir,
        "loaded": bool(state and state.get("gameDir")),
        "state": (state or {}).get("state"),
        "gameDir": (state or {}).get("gameDir"),
    })
    return 0
