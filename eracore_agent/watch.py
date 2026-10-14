"""Read-only terminal spectator for eracore_agent watch."""
import json
import sys
import threading
from typing import Any, Dict, List, Optional

from .emuera_client import EmueraClient, EmueraHttpError, frame_input_type, frame_need_value, frame_state


IMAGE_PLACEHOLDER = "〔图〕"


def apply_line_ops(lines: List[Dict[str, Any]], line_ops: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Apply DisplayDiff.lineOps. Semantics match EraCore.Web/src/lib/opsApplier.ts applyDiff."""
    result = list(lines)
    for op in line_ops:
        op_type = op.get("type")
        if op_type == "append":
            result.extend(op.get("newLines") or [])
        elif op_type == "clear_line_diff":
            n = min(int(op.get("clearCount") or 0), len(result))
            if n > 0:
                result = result[:-n]
        elif op_type == "clear_screen":
            result = []
        elif op_type == "shift_head":
            n = min(int(op.get("count") or 0), len(result))
            if n > 0:
                result = result[n:]
        else:
            raise ValueError(f"Unknown LineOp type: {op_type}")
    return result


def line_text(line: Dict[str, Any]) -> str:
    parts = []
    for entry in line.get("entries") or []:
        for seg in entry.get("segments") or []:
            if seg.get("image") is not None:
                parts.append(IMAGE_PLACEHOLDER)
            else:
                parts.append(seg.get("text") or "")
    return "".join(parts)


def render_screen(lines: List[Dict[str, Any]], status: Dict[str, Any]) -> str:
    body = "\n".join(line_text(line) for line in lines)
    controller = status.get("controller") or {}
    kind = controller.get("kind") or "idle"
    bits = [
        f"state={status.get('state')}",
        f"inputType={status.get('inputType')}",
        f"needValue={status.get('needValue')}",
        f"controller={kind}",
    ]
    return f"{body}\n--- {' | '.join(bits)} ---\n"


def _print_screen(text: str) -> None:
    if sys.stdout.isatty():
        sys.stdout.write("\x1b[2J\x1b[H")
    sys.stdout.write(text)
    sys.stdout.flush()


def _merge_status(snapshot: Dict[str, Any], control: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    # v12（issue 09）：snapshot 顶层 state/inputType/needValue 退场，改由 awaiting 推导。
    return {
        "state": frame_state(snapshot),
        "inputType": frame_input_type(snapshot),
        "needValue": frame_need_value(snapshot),
        "controller": (control or {}).get("controller"),
        "controlState": (control or {}).get("state"),
    }


def watch_session(client: EmueraClient) -> int:
    try:
        snapshot = client.get_snapshot()
    except EmueraHttpError as exc:
        if exc.status == 404:
            print("无活跃会话", file=sys.stderr)
            return 1
        print(f"无法读取 snapshot: {exc}", file=sys.stderr)
        return 1

    try:
        control = client.get_control()
    except EmueraHttpError:
        control = None

    lines = list(snapshot.get("lines") or [])
    status = _merge_status(snapshot, control)
    stop = threading.Event()

    def _poll_control():
        while not stop.is_set():
            try:
                event = client.wait_control(timeout=10.0)
                if event is None:
                    continue
                status["controller"] = event.get("controller")
                if event.get("state"):
                    status["controlState"] = event.get("state")
                _print_screen(render_screen(lines, status))
            except EmueraHttpError:
                if stop.wait(0.5):
                    return
            except Exception:
                if stop.wait(0.5):
                    return

    poller = threading.Thread(target=_poll_control, name="watch-control", daemon=True)
    poller.start()
    _print_screen(render_screen(lines, status))

    try:
        import asyncio
        import websockets
    except ImportError:
        stop.set()
        print("watch 需要 websockets：pip install websockets", file=sys.stderr)
        return 1

    ws_url = client.base_url.replace("http://", "ws://", 1).replace("https://", "wss://", 1) + "/ws"

    async def _run():
        nonlocal lines, status
        try:
            async with websockets.connect(ws_url) as ws:
                async for raw in ws:
                    try:
                        frame = json.loads(raw)
                    except json.JSONDecodeError:
                        continue
                    diff = frame.get("diff") if isinstance(frame, dict) else None
                    if isinstance(diff, dict) and diff.get("lineOps"):
                        lines = apply_line_ops(lines, diff["lineOps"])
                    if isinstance(frame, dict):
                        # v12：帧为 TurnRecord——state/inputType/needValue 不在顶层，
                        # 由 awaiting 推导（见 emuera_client 桥接 helper）。
                        status["state"] = frame_state(frame)
                        status["inputType"] = frame_input_type(frame)
                        status["needValue"] = frame_need_value(frame)
                    _print_screen(render_screen(lines, status))
        except websockets.exceptions.ConnectionClosed as exc:
            if exc.code == 4004:
                print("无活跃会话", file=sys.stderr)
                return 1
            print(f"WebSocket 已关闭 ({exc.code})", file=sys.stderr)
            return 1
        return 0

    try:
        return asyncio.run(_run())
    except KeyboardInterrupt:
        print("\n已退出 watch", file=sys.stderr)
        return 0
    finally:
        stop.set()
