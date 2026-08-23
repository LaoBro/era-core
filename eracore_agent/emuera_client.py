"""HTTP client for the current single-session Emuera Headless server."""
import json
import urllib.error
import urllib.request
from typing import Any, Dict, Optional
from urllib.parse import urlencode

# pageRows 兜底值：旧 server 无 /state visibleRows 字段时的页高估计。
# 有界误差（era 游戏常见 25~45 行），仅供 --pages 换算，输出 pageRows 字段会如实反映。
DEFAULT_PAGE_ROWS = 40


class EmueraHttpError(Exception):
    """Raised when the server returns an HTTP error or cannot be reached."""

    def __init__(self, status: int, payload: Any, body: str = ""):
        self.status = status
        self.payload = payload if isinstance(payload, dict) else {"error": payload or body}
        self.body = body
        super().__init__(f"HTTP {status} {self.code}")

    @property
    def code(self) -> str:
        error = self.payload.get("error")
        if isinstance(error, dict):
            return str(error.get("code") or "ERROR")
        if error:
            return str(error)
        return str(self.payload.get("code") or self.status)


def _parse_json(raw: str) -> Any:
    if not raw:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw


class EmueraClient:
    """HTTP client for the single-session Headless API."""

    def __init__(self, base_url: str = "http://localhost:8080", token: Optional[str] = None):
        self.base_url = base_url.rstrip("/")
        self.token = token

    def load_game(self, game_dir: str) -> Dict[str, Any]:
        """POST /load-game {gameDir}."""
        status, payload, _ = self.request("POST", "/load-game", {"gameDir": game_dir})
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_turn(self, timeout: float = 35.0) -> Optional[Dict[str, Any]]:
        """GET /turn (long poll). 200 → turn JSON; 204 → None; 404/409 → EmueraHttpError."""
        status, payload, _ = self.request("GET", "/turn", timeout=timeout)
        if status == 204:
            return None
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def post_input(self, value: str = "") -> Dict[str, Any]:
        """POST /input {value}."""
        status, payload, _ = self.request("POST", "/input", {"value": value})
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_state(self, timeout: float = 10.0) -> Dict[str, Any]:
        """GET /state."""
        status, payload, _ = self.request("GET", "/state", timeout=timeout)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def delete_session(self) -> Dict[str, Any]:
        """DELETE /session. 404 means nothing to remove."""
        try:
            status, payload, _ = self.request("DELETE", "/session")
        except EmueraHttpError as exc:
            if exc.status == 404:
                return exc.payload if isinstance(exc.payload, dict) else {"removed": False}
            raise
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload if isinstance(payload, dict) else {"removed": True}

    def acquire_control(self) -> Dict[str, Any]:
        """POST /control/acquire."""
        body = {"token": self.token} if self.token else None
        status, payload, _ = self.request("POST", "/control/acquire", body)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def release_control(self) -> Dict[str, Any]:
        """POST /control/release."""
        body = {"token": self.token} if self.token else None
        status, payload, _ = self.request("POST", "/control/release", body)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_control(self, timeout: float = 10.0) -> Dict[str, Any]:
        """GET /control."""
        status, payload, _ = self.request("GET", "/control", timeout=timeout)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def wait_control(self, timeout: float = 35.0) -> Optional[Dict[str, Any]]:
        """GET /control/wait. 200 → event; 204 → None."""
        status, payload, _ = self.request("GET", "/control/wait", timeout=timeout)
        if status == 204:
            return None
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_snapshot(self, timeout: float = 15.0) -> Dict[str, Any]:
        """GET /snapshot."""
        status, payload, _ = self.request("GET", "/snapshot", timeout=timeout)
        if status != 200:
            raise EmueraHttpError(status, payload)
        return payload

    def get_compact_view(self, pages: int = 3, tail: Optional[int] = None, timeout: float = 15.0) -> Dict[str, Any]:
        """紧凑只读视图：读 GET /snapshot 后做低噪投影（纯文本行 + 按钮 id/label）。

        复用权威 /snapshot 端点（最新当前态、非阻塞、可重复调用），投影在 agent 侧完成：
        每行拼接 segments[].text，按钮抽 {id,label}，去掉颜色/坐标/几何。
        页高不是投影产物——来自 GET /state 的 visibleRows（服务端 WindowMetrics 换算）。

        截断窗口以"页"为默认单位：pageRows（一屏行数，GET /state 的 visibleRows，
        服务端按游戏 emuera.config 的 WindowY/LineHeight 换算）× pages。
        旧 server 无该字段或 /state 不可达时回退 DEFAULT_PAGE_ROWS——有界误差，
        输出里的 pageRows 字段会如实反映实际生效值，agent 事后可发现兜底生效。
        tail 显式给出时为绝对行数逃生口，优先于 pages。

        元数据：totalLines（全缓冲行数）、truncated（本次是否截断）、pageRows。
        按钮按当前最大 generation 过滤——被翻页留在历史行里的旧代按钮会被丢弃。
        """
        snap = self.get_snapshot(timeout=timeout)
        src_lines = snap.get("lines") or []
        total_lines = len(src_lines)

        page_rows = DEFAULT_PAGE_ROWS
        try:
            state = self.get_state(timeout=timeout)
            visible = state.get("visibleRows")
            if isinstance(visible, int) and visible >= 1:
                page_rows = visible
        except EmueraHttpError:
            pass

        window = total_lines
        if tail is not None and tail > 0:
            window = tail
        elif pages and pages > 0:
            window = pages * page_rows
        start = max(0, total_lines - window) if window < total_lines else 0
        view_lines = src_lines[start:]

        def line_text(line: Dict[str, Any]) -> str:
            parts = [seg.get("text") for e in (line.get("entries") or []) for seg in (e.get("segments") or [])]
            return "".join(p for p in parts if p)

        lines = [line_text(ln) for ln in view_lines]

        # 过时按钮过滤判据：快照顶层 generation = console.LastButtonGeneration（当前直播按钮代）。
        # 被翻页留在历史行里的旧按钮其 button.generation < 该值，据此丢弃；只保留当前有效代。
        live_gen = snap.get("generation")
        buttons: list = []
        seen: set = set()
        for ln in view_lines:
            for entry in ln.get("entries") or []:
                btn = entry.get("button")
                if not btn or btn.get("value") is None:
                    continue
                if live_gen is not None and btn.get("generation") not in (None, live_gen):
                    continue  # 旧代按钮（前几页残留）
                val = btn.get("value")
                if val in seen:
                    continue
                seen.add(val)
                label = "".join(seg.get("text") or "" for seg in entry.get("segments") or []).strip()
                buttons.append({"id": val, "label": label})

        return {
            "state": snap.get("state"),
            "inputType": snap.get("inputType"),
            "needValue": snap.get("needValue"),
            "generation": snap.get("generation"),
            "lines": lines,
            "buttons": buttons,
            "pageRows": page_rows,
            "totalLines": total_lines,
            "truncated": start > 0,
        }

    def request(self, method: str, path: str, body: Optional[Dict[str, Any]] = None, timeout: float = 35.0):
        headers = {"Accept": "application/json"}
        data = None
        if body is not None:
            payload = dict(body)
            if self.token and "token" not in payload:
                payload["token"] = self.token
            data = json.dumps(payload).encode("utf-8")
            headers["Content-Type"] = "application/json"
        if self.token:
            headers["X-Control-Token"] = self.token

        url = f"{self.base_url}{path}"
        if method == "GET" and self.token and "token=" not in path:
            sep = "&" if "?" in path else "?"
            url = f"{url}{sep}{urlencode({'token': self.token})}"

        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8")
                return resp.status, _parse_json(raw), raw
        except urllib.error.HTTPError as exc:
            raw = exc.read().decode("utf-8", errors="replace")
            raise EmueraHttpError(exc.code, _parse_json(raw), raw) from exc
        except urllib.error.URLError as exc:
            raise EmueraHttpError(0, {"error": "CONNECTION_FAILED", "message": str(exc.reason)}) from exc
