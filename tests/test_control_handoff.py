"""End-to-end checks for the server control handoff contract (Seam 1)."""
import json
import sys
import threading
import time
import traceback
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(TESTS_DIR))

from emuera_server import TEST_GAME_DIR, start_server, turn_state

TOKEN = "control-test-agent"
OTHER = "other-agent"
GAME_DIR = str(TEST_GAME_DIR)


def assert_json(status, body, expected_status):
    assert status == expected_status, (status, body)
    return json.loads(body) if body else {}


def wait_ready(server, timeout=10):
    deadline = time.time() + timeout
    while time.time() < deadline:
        status, body = server.get_state()
        if status == 200 and json.loads(body).get("state") == "WaitInput":
            return
        time.sleep(0.1)
    raise TimeoutError("game did not reach WaitInput")


def wait_snapshot_contains(server, needle, timeout=10):
    deadline = time.time() + timeout
    while time.time() < deadline:
        status, body = server.get_snapshot()
        if status == 200 and needle in body:
            return
        time.sleep(0.1)
    raise TimeoutError(f"snapshot did not contain {needle!r}")


def wait_ended(server, timeout=10):
    deadline = time.time() + timeout
    while time.time() < deadline:
        status, body = server.get_state()
        if status == 200 and json.loads(body).get("isRunning") is False:
            return
        time.sleep(0.1)
    raise TimeoutError("session did not end")


def drain_until_quiet(server, token, quiet=0.3, timeout=5):
    deadline = time.time() + timeout
    empty_since = None
    while time.time() < deadline:
        status, body = server.request("POST", "/control/acquire", {"token": token})
        taken = assert_json(status, body, 200)
        if taken["turnsAdvanced"] == 0:
            if empty_since is None:
                empty_since = time.time()
            elif time.time() - empty_since >= quiet:
                return
            time.sleep(0.1)
        else:
            empty_since = None
    raise TimeoutError("turn queue did not stay empty")


def expect_control_event(server, action, expected_type, timeout=30):
    box = []

    def _wait():
        box.append(server.request("GET", "/control/wait", timeout=timeout))

    thread = threading.Thread(target=_wait)
    thread.start()
    time.sleep(0.4)
    action()
    thread.join(timeout=timeout)
    assert not thread.is_alive(), f"GET /control/wait still pending for {expected_type}"
    status, body = box[0]
    payload = assert_json(status, body, 200)
    actual = payload.get("event") or payload.get("type")
    assert actual == expected_type, payload
    return payload


def with_server(fn, extra_env=None):
    server = start_server(GAME_DIR, extra_env=extra_env)
    try:
        status, body = server.start_session()
        assert status == 200, (status, body)
        wait_ready(server)
        fn(server)
    finally:
        server.close()


def test_acquire_release_steal(server):
    status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
    acquired = assert_json(status, body, 200)
    assert acquired["controller"]["kind"] == "agent"
    assert acquired["turnsAdvanced"] >= 0
    assert "turn" in acquired

    status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
    again = assert_json(status, body, 200)
    assert again["controller"]["kind"] == "agent"

    status, body = server.request("POST", "/control/acquire", {"token": OTHER})
    rejected = assert_json(status, body, 409)
    assert rejected["error"] == "CONTROL_HELD"

    status, body = server.request("POST", "/control/acquire")
    stolen = assert_json(status, body, 200)
    assert stolen["controller"]["kind"] == "user"

    status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
    held = assert_json(status, body, 409)
    assert held["error"] == "CONTROL_HELD_BY_USER"

    status, body = server.request("POST", "/control/release", {"token": TOKEN})
    assert_json(status, body, 409)

    status, body = server.request("GET", "/control")
    control = assert_json(status, body, 200)
    assert control["state"] == "held"
    assert control["controller"]["kind"] == "user"

    status, body = server.request("POST", "/control/release")
    released = assert_json(status, body, 200)
    assert released["released"] is True
    assert released["state"] == "idle"


def test_input_gate_table(server):
    status, body = server.post_input("0")
    assert_json(status, body, 200)

    status, body = server.request("POST", "/load-game", {"gameDir": GAME_DIR})
    assert_json(status, body, 200)
    wait_ready(server)

    status, body = server.request("POST", "/input", {"value": "0", "token": TOKEN})
    assert_json(status, body, 200)

    status, body = server.request("POST", "/load-game", {"gameDir": GAME_DIR})
    assert_json(status, body, 200)
    wait_ready(server)

    status, body = server.request("POST", "/control/acquire")
    assert_json(status, body, 200)
    status, body = server.request("POST", "/input", {"value": "0", "token": TOKEN})
    denied = assert_json(status, body, 409)
    assert denied["reason"] == "CONTROL_HELD_BY_USER"
    assert "acquire" in denied["hint"]
    status, body = server.post_input("0")
    assert_json(status, body, 200)

    status, body = server.request("POST", "/load-game", {"gameDir": GAME_DIR})
    assert_json(status, body, 200)
    wait_ready(server)

    status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
    assert_json(status, body, 200)
    status, body = server.post_input("0")
    denied = assert_json(status, body, 409)
    assert denied["reason"] == "CONTROL_HELD_BY_AGENT"
    assert "acquire" in denied["hint"]
    status, body = server.request("POST", "/input", {"value": "0", "token": OTHER})
    denied = assert_json(status, body, 409)
    assert denied["reason"] == "CONTROL_HELD_BY_AGENT"
    status, body = server.request("POST", "/input", {"value": "0", "token": TOKEN})
    assert_json(status, body, 200)


def test_lifecycle_gate(server):
    status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
    assert_json(status, body, 200)

    status, body = server.request("DELETE", "/session")
    denied = assert_json(status, body, 409)
    assert denied["reason"] in ("CONTROL_HELD_BY_AGENT", "CONTROL_NOT_CONTROLLER")

    status, body = server.request("POST", "/load-game", {"gameDir": GAME_DIR})
    denied = assert_json(status, body, 409)
    assert denied["reason"] in ("CONTROL_HELD_BY_AGENT", "CONTROL_NOT_CONTROLLER")

    status, body = server.request("POST", "/input", {"value": "@QUIT", "token": TOKEN})
    assert_json(status, body, 200)
    wait_ended(server)

    status, body = server.request("POST", "/load-game", {"gameDir": GAME_DIR})
    assert_json(status, body, 200)
    wait_ready(server)

    status, body = server.request("DELETE", "/session")
    removed = assert_json(status, body, 200)
    assert removed.get("removed") is True


def test_control_wait_events(server):
    expect_control_event(
        server,
        lambda: server.request("POST", "/control/acquire", {"token": TOKEN}),
        "acquired",
    )
    expect_control_event(
        server,
        lambda: server.request("POST", "/control/release", {"token": TOKEN}),
        "released",
    )
    server.request("POST", "/control/acquire", {"token": TOKEN})
    expect_control_event(
        server,
        lambda: server.request("POST", "/control/acquire"),
        "stolen",
    )
    expect_control_event(
        server,
        lambda: server.request("POST", "/load-game", {"gameDir": GAME_DIR}),
        "session_replaced",
    )
    wait_ready(server)
    server.request("POST", "/control/acquire", {"token": TOKEN})
    expect_control_event(
        server,
        lambda: server.request("POST", "/input", {"value": "@QUIT", "token": TOKEN}),
        "game_ended",
    )


def test_acquire_drain(server):
    deadline = time.time() + 10
    user = None
    while time.time() < deadline:
        status, body = server.request("POST", "/control/acquire")
        user = assert_json(status, body, 200)
        if user.get("turnsAdvanced", 0) >= 1 and user.get("turn") is not None:
            break
        time.sleep(0.1)
    assert user is not None and user["turnsAdvanced"] >= 1, user
    assert user["turn"] is not None, user

    status, body = server.post_input("0")
    assert_json(status, body, 200)
    wait_snapshot_contains(server, "Select again")
    status, body = server.request("POST", "/control/release")
    assert_json(status, body, 200)

    status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
    taken = assert_json(status, body, 200)
    assert taken["turnsAdvanced"] >= 1, taken
    assert taken["turn"] is not None, taken

    status, body = server.request("POST", "/input", {"value": "0", "token": TOKEN})
    assert_json(status, body, 200)
    status, body = server.request("GET", f"/turn?token={TOKEN}", timeout=10)
    own = assert_json(status, body, 200)
    # v12（issue 09）：turn 顶层 state 退场，唯一契约字段是 awaiting——等待语义由其表达。
    assert "awaiting" in own, own
    assert turn_state(own) is not None, own


def test_inflight_turn_lost_on_steal(server):
    status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
    assert_json(status, body, 200)
    drain_until_quiet(server, TOKEN)

    box = []

    def _wait_turn():
        box.append(server.request("GET", f"/turn?token={TOKEN}", timeout=30))

    thread = threading.Thread(target=_wait_turn)
    thread.start()
    time.sleep(0.4)
    started = time.time()
    status, body = server.request("POST", "/control/acquire")
    assert_json(status, body, 200)
    thread.join(timeout=10)
    assert not thread.is_alive(), "in-flight GET /turn did not return after steal"
    assert time.time() - started < 5
    status, body = box[0]
    lost = assert_json(status, body, 409)
    assert lost["error"] == "CONTROL_LOST"
    assert "reason" in lost
    assert "at" in lost


def test_lease_expiry():
    def _run(server):
        status, body = server.request("POST", "/control/acquire", {"token": TOKEN})
        assert_json(status, body, 200)
        status, body = server.request("GET", "/control/wait", timeout=10)
        expired = assert_json(status, body, 200)
        assert (expired.get("event") or expired.get("type")) == "lease_expired"
        status, body = server.request("GET", "/control")
        control = assert_json(status, body, 200)
        assert control["state"] == "idle"
        status, body = server.request("POST", "/control/acquire")
        assert_json(status, body, 200)

    with_server(_run, extra_env={"EMUERA_CONTROL_LEASE_SECONDS": "1"})


def main():
    cases = [
        ("acquire/release/steal", test_acquire_release_steal),
        ("input gate table", test_input_gate_table),
        ("lifecycle gate", test_lifecycle_gate),
        ("control wait events", test_control_wait_events),
        ("acquire drain", test_acquire_drain),
        ("in-flight turn 409", test_inflight_turn_lost_on_steal),
    ]
    failed = []
    for name, fn in cases:
        try:
            with_server(fn)
            print(f"  PASS: {name}")
        except Exception as exc:
            failed.append((name, exc))
            print(f"  FAIL: {name}: {exc!r}")
            traceback.print_exc()

    try:
        test_lease_expiry()
        print("  PASS: lease expiry")
    except Exception as exc:
        failed.append(("lease expiry", exc))
        print(f"  FAIL: lease expiry: {exc!r}")
        traceback.print_exc()

    if failed:
        print(f"control handoff: FAIL ({len(failed)})")
        return 1
    print("control handoff: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
