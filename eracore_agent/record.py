"""record 子命令：录制 WS 原始帧并逐帧摘要 diff 形态。

为什么需要它：`view` / `watch` 只给**某一瞬间**的画面，看不出**每帧搬运了几行**。
「画面只有一部分在更新」「行数恒定帧流」「帧间隔是否积压」这类问题必须看连续帧的
`diff.lineOps` 才能判定——那是渲染/协议类缺陷的定位入口，靠 view 是看不出来的。

典型用法（抓动画的第 1 帧起）：

    eracore_agent record --seconds 3 --post-input 2 --out frames.jsonl

判据读法：
- `net` = append 行数 − clear_line_diff 行数。**恒为 0** 即「行数不变帧流」
  （前端按行数变化触发的滚动/吸底在这种帧流里全程不会触发）。
- `lines` = 以录制起点的 `GET /snapshot` 行数为基线、逐帧应用 lineOps 后的总行数。
  与 `net` 互为印证：`net` 恒 0 时 `lines` 也应恒定。
- `gap` = 相邻帧到达间隔。与脚本里的 `AWAIT t` 对比即可判断服务端是否积压。
"""
from __future__ import annotations

import asyncio
import json
import time
from typing import Any, Dict, List, Optional, Tuple

from .emuera_client import EmueraClient, EmueraHttpError
from .watch import apply_line_ops


class RecordError(Exception):
    """record 的可预期失败（缺依赖 / WS 连接失败 / 写文件失败）。"""


def summarize_line_ops(line_ops: Optional[List[Dict[str, Any]]]) -> Dict[str, int]:
    """把 lineOps 压成 {'append':K,'clear_line_diff':K,...}。未知 op 原样计数。"""
    out: Dict[str, int] = {}
    for op in line_ops or []:
        t = op.get("type")
        if t == "append":
            out["append"] = out.get("append", 0) + len(op.get("newLines") or [])
        elif t == "clear_line_diff":
            out["clear_line_diff"] = out.get("clear_line_diff", 0) + int(op.get("clearCount") or 0)
        elif t == "shift_head":
            out["shift_head"] = out.get("shift_head", 0) + int(op.get("count") or 0)
        elif t == "clear_screen":
            out["clear_screen"] = out.get("clear_screen", 0) + 1
        else:
            key = str(t)
            out[key] = out.get(key, 0) + 1
    return out


def _ws_url(base_url: str) -> str:
    base = base_url.rstrip("/")
    for scheme in ("http://", "https://"):
        if base.startswith(scheme):
            return base.replace(scheme, "ws://" if scheme == "http://" else "wss://", 1) + "/ws"
    return base + "/ws"


async def _collect(
    ws_url: str,
    seconds: float,
    post_input: Optional[str],
    base_url: str,
    token: Optional[str],
) -> List[Tuple[float, str]]:
    import websockets

    frames: List[Tuple[float, str]] = []
    async with websockets.connect(ws_url) as ws:
        if post_input is not None:
            # 🔴 必须先连上 WS 再提交：反序会漏掉被触发动画的**第 1 帧**——
            # 而那帧往往是唯一能看出「本帧到底打印了多少显示行」的一帧
            # （eraTW @BADAPPLE 的帧 0 是 append 156，后续帧才退化成 clear 11 + append 11）。
            payload: Dict[str, Any] = {"value": post_input}
            if token:
                payload["token"] = token
            await asyncio.get_running_loop().run_in_executor(
                None, lambda: _post_input_sync(base_url, payload))
        t0 = time.perf_counter()
        while True:
            remaining = seconds - (time.perf_counter() - t0)
            if remaining <= 0:
                break
            try:
                raw = await asyncio.wait_for(ws.recv(), timeout=remaining)
            except asyncio.TimeoutError:
                break
            except websockets.exceptions.ConnectionClosed as exc:
                # 4004 = 服务端无活跃会话（游戏已 Quit / 会话被拆）——给出可行动的提示，
                # 而不是让 websockets 的异常直接穿透成 traceback。
                raise RecordError(
                    f"WebSocket 已关闭（code={exc.code}）：{exc.reason or ''}。"
                    f"会话可能已结束（脚本 QUIT / 被 stop）；先 `eracore_agent status` 确认 activeSession。"
                ) from exc
            frames.append((time.perf_counter() - t0, raw))
    return frames


def _post_input_sync(base_url: str, payload: Dict[str, Any]) -> None:
    import urllib.request

    req = urllib.request.Request(
        base_url.rstrip("/") + "/input",
        data=json.dumps(payload).encode("utf-8"),
        method="POST",
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=20):
        pass


def run_record(
    client: EmueraClient,
    seconds: float,
    out: Optional[str] = None,
    post_input: Optional[str] = None,
) -> Dict[str, Any]:
    """录制 WS 原始帧。stdout 语义由调用方决定，本函数只返回结果。

    @returns `{"rows":[...], "summary":{...}, "out":..., "frameCount":N, "seconds":S}`
    @throws RecordError 缺 websockets / WS 连接失败 / 写文件失败
    """
    try:
        import websockets  # noqa: F401
    except ImportError:
        raise RecordError("record 需要 websockets：pip install websockets")

    # 基线行数：用于逐帧算总行数（判定「行数恒定」）。失败不致命——置 null。
    baseline: Optional[int] = None
    try:
        snap = client.get_snapshot()
        baseline = len(snap.get("lines") or [])
    except EmueraHttpError:
        baseline = None

    ws_url = _ws_url(client.base_url)
    try:
        frames = asyncio.run(
            _collect(ws_url, seconds, post_input, client.base_url, client.token))
    except OSError as exc:
        raise RecordError(f"WebSocket 连接失败（{ws_url}）：{exc}")

    if out:
        try:
            with open(out, "w", encoding="utf-8") as fh:
                for _, raw in frames:
                    fh.write(raw if raw.endswith("\n") else raw + "\n")
        except OSError as exc:
            raise RecordError(f"写入 {out} 失败：{exc}")

    rows: List[Dict[str, Any]] = []
    prev_t: Optional[float] = None
    line_count = baseline
    for t, raw in frames:
        try:
            frame = json.loads(raw)
        except json.JSONDecodeError:
            rows.append({"t": round(t * 1000, 1), "ops": None, "net": None,
                         "lines": line_count, "awaiting": None, "gap": None,
                         "raw": raw[:200]})
            prev_t = t
            continue

        line_ops = ((frame.get("diff") or {}).get("lineOps") or []) if isinstance(frame, dict) else []
        ops = summarize_line_ops(line_ops)
        net = ops.get("append", 0) - ops.get("clear_line_diff", 0)
        if line_count is not None:
            # 只关心行数 ⇒ 用占位行跑一遍真实归约，避免自己另写一套 op 语义。
            line_count = len(apply_line_ops([{}] * line_count, line_ops))
        gap = None if prev_t is None else round((t - prev_t) * 1000, 1)
        prev_t = t
        rows.append({
            "t": round(t * 1000, 1),
            "ops": ops,
            "net": net,
            "lines": line_count,
            "awaiting": frame.get("awaiting") if isinstance(frame, dict) else None,
            "gap": gap,
        })

    gaps = [r["gap"] for r in rows if r.get("gap") is not None]
    nets = [r["net"] for r in rows if r.get("net") is not None]
    lines = [r["lines"] for r in rows if r.get("lines") is not None]
    summary = {
        "frameCount": len(rows),
        "seconds": seconds,
        "baselineLines": baseline,
        "netConstant": len(set(nets)) <= 1,
        "netValues": sorted(set(nets))[:8],
        "linesMin": min(lines) if lines else None,
        "linesMax": max(lines) if lines else None,
        "linesConstant": len(set(lines)) <= 1,
        "gapMinMs": min(gaps) if gaps else None,
        "gapAvgMs": round(sum(gaps) / len(gaps), 1) if gaps else None,
        "gapMaxMs": max(gaps) if gaps else None,
    }

    return {
        "rows": rows,
        "summary": summary,
        "out": out,
        "frameCount": len(rows),
        "seconds": seconds,
    }


def format_rows(rows: List[Dict[str, Any]]) -> List[str]:
    """人读表格（打 stderr，不污染 stdout 的 JSON 契约）。"""
    lines = ["%4s %8s  %-42s %5s %6s  %s" % ("#", "t_ms", "ops", "net", "lines", "awaiting")]
    for i, r in enumerate(rows):
        gap = "" if r.get("gap") is None else " (+%.1f)" % r["gap"]
        lines.append("%4d %8s  %-42s %5s %6s  %s%s" % (
            i,
            r.get("t"),
            str(r.get("ops"))[:42],
            r.get("net"),
            r.get("lines"),
            json.dumps(r.get("awaiting"), ensure_ascii=False),
            gap,
        ))
    return lines
