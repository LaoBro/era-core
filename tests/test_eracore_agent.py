"""Process-level smoke for eracore_agent CLI (issue 02 / M2a).

Seam: invoke `python -m eracore_agent <subcommand>` against a real Headless
server. HTTP contract is covered by test_control_handoff.py; this file only
checks the one-call-one-turn CLI loop:

    start → acquire → step → release → stop

Usage:
    python tests/test_eracore_agent.py
    python tests/test_eracore_agent.py --binary path/to/EraCore.Cli.exe --game-dir test_game
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
ROOT_DIR = TESTS_DIR.parent
sys.path.insert(0, str(TESTS_DIR))

from emuera_server import TEST_GAME_DIR, find_binary, free_port

passed = 0
failed = 0


def check(condition, message):
    global passed, failed
    if condition:
        passed += 1
        print(f"  PASS: {message}")
    else:
        failed += 1
        print(f"  FAIL: {message}")


def parse_json_stdout(result):
    text = (result.stdout or "").strip()
    if not text:
        return None
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        return None


def run_agent(args, env, timeout=60):
    return subprocess.run(
        [sys.executable, "-m", "eracore_agent", *args],
        cwd=str(ROOT_DIR),
        env=env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )


def backup_server_file(path):
    if not path.is_file():
        return None
    return path.read_text(encoding="utf-8")


def restore_server_file(path, previous):
    if previous is None:
        if path.is_file():
            path.unlink()
        return
    path.write_text(previous, encoding="utf-8")


def wait_port_closed(port, timeout=10):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            import socket
            with socket.create_connection(("127.0.0.1", port), timeout=0.2):
                time.sleep(0.2)
        except OSError:
            return True
    return False


def main():
    import argparse

    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary")
    parser.add_argument("--game-dir", default=str(TEST_GAME_DIR), help="Path to game directory")
    args = parser.parse_args()

    binary = args.binary or find_binary(str(ROOT_DIR))[0]
    game_dir = Path(args.game_dir)
    if not game_dir.is_absolute():
        game_dir = (ROOT_DIR / game_dir).resolve()

    port = free_port()
    server_file = ROOT_DIR / ".eracore-server.json"
    previous = backup_server_file(server_file)
    if server_file.is_file():
        server_file.unlink()

    env = os.environ.copy()
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONPATH"] = os.pathsep.join(
        [str(ROOT_DIR), env.get("PYTHONPATH", "")]
    ).rstrip(os.pathsep)
    if binary:
        env["EMUERA_BINARY"] = str(binary)

    print(f"=== eracore_agent smoke (port {port}) ===")
    try:
        missing = run_agent(["status"], env, timeout=15)
        check(missing.returncode != 0, f"status without server exits non-zero (got {missing.returncode})")
        check(
            "eracore_agent start" in (missing.stderr or ""),
            "status without server tells user to run eracore_agent start",
        )
        check((missing.stdout or "").strip() == "", "status error does not write to stdout")

        start = run_agent(
            [
                "start",
                "--port",
                str(port),
                "--game-dir",
                str(game_dir),
                "--eracore-path",
                str(binary),
            ],
            env,
            timeout=60,
        )
        start_json = parse_json_stdout(start)
        check(start.returncode == 0, f"start exits 0 (got {start.returncode})")
        check(start_json is not None, "start stdout is JSON")
        if start_json is not None:
            check("state" in start_json, f"start turn has state (keys={list(start_json)})")
        check(server_file.is_file(), ".eracore-server.json written")
        saved = {}
        if server_file.is_file():
            saved = json.loads(server_file.read_text(encoding="utf-8"))
            check(saved.get("port") == port, f"server file port == {port}")
            check(bool(saved.get("token")), "server file has token")
            check(bool(saved.get("pid")), "server file has pid")
            check("gameDir" in saved, "server file has gameDir")

        start_again = run_agent(
            [
                "start",
                "--port",
                str(port),
                "--game-dir",
                str(game_dir),
                "--eracore-path",
                str(binary),
            ],
            env,
            timeout=30,
        )
        check(start_again.returncode == 0, f"second start reuses server (got {start_again.returncode})")
        if server_file.is_file() and saved:
            again = json.loads(server_file.read_text(encoding="utf-8"))
            check(again.get("pid") == saved.get("pid"), "second start does not relaunch")
            check(again.get("token") == saved.get("token"), "second start keeps token")

        acquire = run_agent(["acquire"], env, timeout=30)
        acquire_json = parse_json_stdout(acquire)
        check(acquire.returncode == 0, f"acquire exits 0 (got {acquire.returncode})")
        check(acquire_json is not None, "acquire stdout is JSON")
        if acquire_json is not None:
            check(acquire_json.get("controller", {}).get("kind") == "agent", "acquire controller.kind is agent")
            check("state" in acquire_json, "acquire has state")
            check("turnsAdvanced" in acquire_json, "acquire has turnsAdvanced")
            check("turn" not in acquire_json, "acquire default omits full turn record (low-noise)")

        acquire_full = run_agent(["acquire", "--full"], env, timeout=30)
        acquire_full_json = parse_json_stdout(acquire_full)
        check(acquire_full.returncode == 0, f"acquire --full exits 0 (got {acquire_full.returncode})")
        check(acquire_full_json is not None, "acquire --full stdout is JSON")
        if acquire_full_json is not None:
            check("turn" in acquire_full_json, "acquire --full includes turn record")

        step = run_agent(["step", "--value", "0"], env, timeout=30)
        step_json = parse_json_stdout(step)
        check(step.returncode == 0, f"step exits 0 (got {step.returncode})")
        check(step_json is not None, "step stdout is JSON")
        if step_json is not None:
            check("state" in step_json, "step turn has state")

        advance = run_agent(["advance"], env, timeout=30)
        advance_json = parse_json_stdout(advance)
        check(advance.returncode == 0, f"advance exits 0 (got {advance.returncode})")
        check(advance_json is not None, "advance stdout is JSON")
        if advance_json is not None:
            check("turns" in advance_json, "advance has turns list")
            check("stopped" in advance_json, "advance has stopped")
            check("advancedCount" in advance_json, "advance has advancedCount")
            check("limitReached" in advance_json, "advance has limitReached")

        status = run_agent(["status"], env, timeout=15)
        status_json = parse_json_stdout(status)
        check(status.returncode == 0, f"status exits 0 (got {status.returncode})")
        check(status_json is not None, "status stdout is JSON")
        if status_json is not None:
            check(status_json.get("controller", {}).get("kind") == "agent", "status shows agent controller")
            check("game" in status_json or "state" in status_json, "status includes game state")

        release = run_agent(["release"], env, timeout=15)
        release_json = parse_json_stdout(release)
        check(release.returncode == 0, f"release exits 0 (got {release.returncode})")
        check(release_json is not None, "release stdout is JSON")
        if release_json is not None:
            check(release_json.get("released") is True, "release reports released=true")

        stop = run_agent(["stop"], env, timeout=30)
        check(stop.returncode == 0, f"stop exits 0 (got {stop.returncode})")
        check(not server_file.is_file(), "stop deletes .eracore-server.json")
        check(wait_port_closed(port), f"stop ends the server on port {port}")
    except Exception as exc:
        check(False, f"smoke raised {type(exc).__name__}: {exc}")
        import traceback
        traceback.print_exc()
    finally:
        if server_file.is_file():
            try:
                leftover = json.loads(server_file.read_text(encoding="utf-8"))
                pid = leftover.get("pid")
                if pid:
                    if sys.platform == "win32":
                        subprocess.run(
                            ["taskkill", "/PID", str(pid), "/T", "/F"],
                            capture_output=True,
                            timeout=10,
                        )
                    else:
                        try:
                            os.kill(int(pid), 15)
                        except OSError:
                            pass
            except Exception:
                pass
        restore_server_file(server_file, previous)

    print(f"\n{passed} passed, {failed} failed")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
