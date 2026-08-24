"""Run the Emuera.Headless full test suite with one command.

This entry point keeps the existing focused test scripts intact and adds a
single command for routine regression runs. It runs the .NET xUnit unit
tests (Emuera.Headless.Tests) first, then the Python end-to-end suites:

    python tests/run_all.py
    python tests/run_all.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe
    python tests/run_all.py --game-dir test_game
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

TESTS_DIR = Path(__file__).resolve().parent
ROOT_DIR = TESTS_DIR.parent

sys.path.insert(0, str(TESTS_DIR))
from emuera_server import find_binary


def _resolve_path(value, base_dir):
    path = Path(value)
    if path.is_absolute():
        return path
    return (base_dir / path).resolve()


def _binary_command(binary_path):
    path = Path(binary_path)
    if path.suffix.lower() == ".dll":
        dotnet = shutil.which("dotnet")
        if dotnet is None:
            raise RuntimeError("dotnet was not found in PATH")
        return [dotnet, "exec", str(path)]
    return [str(path)]


def _child_env(binary_path=None):
    env = os.environ.copy()
    if binary_path:
        env["EMUERA_BINARY"] = str(binary_path)
    return env


def _safe_print_line(line):
    # 直接 print：Windows 控制台 Python 走 WinConsoleIO（UTF-16 直接写，不经代码页），
    # 任意 Unicode 正常显示。旧实现 encode/decode(errors="replace") 双重编码在 GBK
    # 代码页（PowerShell 默认 936）下把中文替换成 U+FFFD �——显示乱码，且断言层无关。
    # 若被重定向到管道/文件，print 用 stdout.encoding（UTF-8/gbk 均支持中文），同样安全。
    print(line)


def _print_key_lines(stdout):
    for line in stdout.splitlines():
        if (
            line.startswith("===")
            or line.startswith("  FAIL:")
            or line.startswith("  WARN:")
            or line.startswith("[EmueraServer]")
        ):
            _safe_print_line(line)


def _run_script(name, args, env=None, timeout=None):
    print(f"\n=== {name} ===")
    # 子进程 stdout 是管道（非控制台）：Python 管道模式默认用 locale 编码（Windows=GBK）
    # 写中文，父进程按 UTF-8 解码即乱码（�）。强制子进程 UTF-8 输出，与下方
    # encoding="utf-8" 解码对齐；PYTHONIOENCODING 是 Python 专用，不影响其他子进程类型。
    child_env = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    if env:
        child_env.update(env)
    completed = subprocess.run(
        args,
        cwd=str(ROOT_DIR),
        env=child_env,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )
    _print_key_lines(completed.stdout)
    if completed.returncode != 0 and completed.stderr.strip():
        for line in completed.stderr.strip().splitlines():
            _safe_print_line(f"  [stderr] {line}")
    # Brief pause between test suites on Windows to avoid file-lock races
    # (the .NET single-file host may briefly hold the exe after process exit)
    if sys.platform == "win32":
        time.sleep(1)
    return completed.returncode == 0, completed.returncode, False


def _run_dotnet_test(timeout=None):
    """Run the .NET xUnit unit tests via `dotnet test`.

    Returns (passed, returncode, skipped). Skipped when `dotnet` is not on
    PATH so the suite still runs on machines without the .NET SDK.
    """
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        print("\n=== .NET unit tests (xUnit) ===")
        print("SKIPPED: dotnet not found in PATH")
        return False, None, True
    print("\n=== .NET unit tests (xUnit) ===")
    completed = subprocess.run(
        [dotnet, "test", "EraCore.Tests/EraCore.Tests.csproj", "--nologo"],
        cwd=str(ROOT_DIR),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=timeout,
    )
    for line in completed.stdout.splitlines():
        stripped = line.strip()
        if any(kw in stripped for kw in ("失败", "通过", "已通过", "error", "Error", "FAILED", "Test Run")):
            _safe_print_line(line)
    if completed.returncode != 0 and completed.stderr.strip():
        for line in completed.stderr.strip().splitlines():
            _safe_print_line(f"  [stderr] {line}")
    # Brief pause on Windows to release any file lock from the build step.
    if sys.platform == "win32":
        time.sleep(1)
    return completed.returncode == 0, completed.returncode, False





def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to Emuera.Headless binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="Path to game directory")
    args = parser.parse_args()

    binary_path = _resolve_path(args.binary, Path.cwd()) if args.binary else find_binary(str(ROOT_DIR))[0]
    game_dir = _resolve_path(args.game_dir, ROOT_DIR)
    env = _child_env(binary_path)

    results = []

    results.append(
        (
            ".NET unit tests (xUnit)",
            _run_dotnet_test(timeout=300),
        )
    )

    results.append(
        (
            "JSONL + buttons",
            _run_script(
                "JSONL + buttons",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_jsonl.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=120,
            ),
        )
    )

    results.append(
        (
            "server single-session",
            _run_script(
                "server single-session",
                [sys.executable, str(TESTS_DIR / "test_server_single_session.py")],
                env=env,
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "control handoff",
            _run_script(
                "control handoff",
                [sys.executable, str(TESTS_DIR / "test_control_handoff.py")],
                env=env,
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "eracore_agent CLI",
            _run_script(
                "eracore_agent CLI",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_eracore_agent.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                env=env,
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "TINPUT timeout",
            _run_script(
                "TINPUT timeout",
                [sys.executable, str(TESTS_DIR / "test_tinput_timeout.py")],
                env=env,
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "fatal turn",
            _run_script(
                "fatal turn",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_fatal_turn.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=120,
            ),
        )
    )

    results.append(
        (
            "preload readback",
            _run_script(
                "preload readback",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_preload_readback.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "I-11 exit survival",
            _run_script(
                "I-11 exit survival",
                [sys.executable, str(TESTS_DIR / "test_force_quit_survival.py")],
                env=env,
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "SELECTCASE loading",
            _run_script(
                "SELECTCASE loading",
                [sys.executable, str(TESTS_DIR / "test_selectcase_loading.py")],
                env=env,
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "CLI basic",
            _run_script(
                "CLI basic",
                [sys.executable, str(TESTS_DIR / "test_cli_basic.py"), "--binary", str(binary_path), "--game-dir", str(game_dir)],
                timeout=60,
            ),
        )
    )

    results.append(
        (
            "CLI scroll (ConPTY)",
            _run_script(
                "CLI scroll (ConPTY)",
                [sys.executable, str(TESTS_DIR / "test_cli_scroll.py"), "--binary", str(binary_path)],
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "CLI clearline/printn (ConPTY)",
            _run_script(
                "CLI clearline/printn (ConPTY)",
                [sys.executable, str(TESTS_DIR / "test_clearline_reprint.py"), "--binary", str(binary_path)],
                timeout=120,
            ),
        )
    )

    results.append(
        (
            "VT-only fatal exit",
            _run_script(
                "VT-only fatal exit",
                [sys.executable, str(TESTS_DIR / "test_vt_only.py")],
                env=env,
                timeout=60,
            ),
        )
    )

    results.append(
        (
            "WebSocket transport",
            _run_script(
                "WebSocket transport",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_ws.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "GET /snapshot endpoint",
            _run_script(
                "GET /snapshot endpoint",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_snapshot.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=120,
            ),
        )
    )

    results.append(
        (
            "load-game endpoints",
            _run_script(
                "load-game endpoints",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_load_game.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "/assets resource channel",
            _run_script(
                "/assets resource channel",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_assets.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=180,
            ),
        )
    )

    results.append(
        (
            "ERB parallel/serial equivalence",
            _run_script(
                "ERB parallel/serial equivalence",
                [
                    sys.executable,
                    str(TESTS_DIR / "test_erbloader_equivalence.py"),
                    "--binary",
                    str(binary_path),
                    "--game-dir",
                    str(game_dir),
                ],
                timeout=180,
            ),
        )
    )

    print("\n=== Summary ===")
    all_passed = True
    for name, (passed, code, skipped) in results:
        if skipped:
            print(f"SKIP: {name}")
            continue
        status = "PASS" if passed else f"FAIL (exit {code})"
        print(f"{status}: {name}")
        all_passed = all_passed and passed

    return 0 if all_passed else 1


if __name__ == "__main__":
    sys.exit(main())
