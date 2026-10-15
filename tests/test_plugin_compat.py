"""Test: 已编译的 legacy emuera.em 插件可被 EraCore 直接加载（不重编译）。

复现 eraTW 场景：随游戏分发的插件 DLL 编译目标是程序集
`Emuera, Version=1.824.0.0, PublicKeyToken=null`（非强命名）。EraCore 通过
`EraCore.Compat.Emuera`（AssemblyName=Emuera，纯 TypeForwardedTo）提供程序集身份，
使插件 IL 中的 `MinorShift.Emuera.Runtime.Utils.PluginSystem.*` 类型解析到 EraCore.Core；
并由 PluginManager 在 Plugins 目录装 `AssemblyLoadContext.Default.Resolving` 处理器，
解析插件自带的兄弟依赖（.NET Core LoadFrom 不探测自身目录）。

自建夹具 tests/fixtures/legacy_plugin：
  - EmueraStub      : 定义型 `Emuera` 桩（1.824.0.0），仅编译期用，绝不部署
  - RefLegacyDep    : 兄弟依赖，只放进游戏 `plugins/`，验证解析处理器
  - RefLegacyPlugin : 引用上述两者 -> AssemblyRef = Emuera,1.824（与真实插件一致）

刻意使用小写 `plugins/` 目录（同 eraTW）验证大小写不敏感解析；
刻意不创建 pluginsAware.txt，验证安全门禁默认关闭（配置项 PluginsAwareRequired 默认 false）。

Usage:
    python test_plugin_compat.py
    python test_plugin_compat.py --binary path/to/EraCore.Cli.exe
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

_project_dir = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(_project_dir))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from emuera_server import find_binary, snapshot_text, start_server, copy_test_game_with_erb  # noqa: E402

FIXTURE_DIR = _project_dir / "tests" / "fixtures" / "legacy_plugin"
PLUGIN_PROJ = FIXTURE_DIR / "RefLegacyPlugin" / "RefLegacyPlugin.csproj"
EXPECTED_MARKER = "[COMPAT_OK:hello|SIDE_DEP_OK]"

ERB = """@SYSTEM_TITLE
#DIMS COMPAT_OUT = ""
PRINTL Agent Plugin Compat Start
CALLSHARP CompatProbe("hello", COMPAT_OUT)
PRINTFORML OUT=%COMPAT_OUT%
PRINTL Agent Plugin Compat End
PRINTL [0] Quit
INPUT
QUIT
"""


def check(condition, message, passed, failed):
    if condition:
        passed[0] += 1
        print(f"  PASS: {message}")
    else:
        failed[0] += 1
        print(f"  FAIL: {message}")


def build_fixture():
    """构建参照插件 + 兄弟依赖，返回 (plugin_dll, dep_dll)。失败抛 CalledProcessError。"""
    subprocess.run(
        ["dotnet", "build", str(PLUGIN_PROJ), "-c", "Debug", "-m:1"],
        check=True,
        cwd=str(_project_dir),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        # 子进程是 dotnet/MSBuild：输出为 UTF-8，而 Windows 管道模式默认按 locale
        # （GBK）解码 → UnicodeDecodeError（中文「正在确定要还原的项目…」）。与
        # run_all.py / emuera_server.py 等处同一处理。build 输出内容未被使用，
        # 只需保证不炸。
        encoding="utf-8",
        errors="replace",
    )
    out = PLUGIN_PROJ.parent / "bin" / "Debug" / "net10.0"
    plugin_dll = out / "RefLegacyPlugin.dll"
    dep_dll = out / "RefLegacyDep.dll"
    if not plugin_dll.is_file() or not dep_dll.is_file():
        raise FileNotFoundError(f"fixture build output missing under {out}")
    return plugin_dll, dep_dll


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--binary", help="Path to EraCore binary (exe or dll)")
    parser.add_argument("--game-dir", default="test_game", help="(unused) kept for run_all convention")
    args = parser.parse_args()

    passed = [0]
    failed = [0]

    if shutil.which("dotnet") is None:
        print("SKIP: dotnet 不在 PATH，跳过 legacy plugin compat 套件")
        return 0

    temp_dir = None
    server = None
    try:
        plugin_dll, dep_dll = build_fixture()

        temp_dir, game_dir = copy_test_game_with_erb(ERB)
        # 小写 plugins/（同 eraTW）；不放 pluginsAware.txt（验证门禁默认关）。
        plugins_dir = Path(game_dir) / "plugins"
        plugins_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(plugin_dll, plugins_dir / plugin_dll.name)
        shutil.copy2(dep_dll, plugins_dir / dep_dll.name)
        check(not (Path(game_dir) / "pluginsAware.txt").exists(),
              "fixture has no pluginsAware.txt (gate default off)", passed, failed)

        binary = args.binary if args.binary else find_binary(str(_project_dir))[0]
        server = start_server(game_dir, binary=binary)

        status, body = server.load_game(game_dir)
        check(status == 200, f"POST /load-game returns 200, got {status}", passed, failed)

        status, body = server.get_turn(timeout=20)
        check(status == 200, f"GET /turn returns 200, got {status}", passed, failed)

        status, snap_body = server.get_snapshot(timeout=10)
        check(status == 200, f"GET /snapshot returns 200, got {status}", passed, failed)
        text = snapshot_text(json.loads(snap_body)) if status == 200 else ""
        print(f"snapshot text: {text!r}")
        check("Agent Plugin Compat Start" in text,
              "game advanced past CALLSHARP (script not aborted by plugin load)", passed, failed)
        check(EXPECTED_MARKER in text,
              f"CALLSHARP result carries dependency-derived marker {EXPECTED_MARKER}", passed, failed)
    except Exception as exc:  # noqa: BLE001 —— 用例失败也要汇总输出
        check(False, f"unexpected error: {exc!r}", passed, failed)
    finally:
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()
        if temp_dir is not None:
            shutil.rmtree(temp_dir, ignore_errors=True)

    print(f"\n=== legacy plugin compat: {passed[0]} passed, {failed[0]} failed ===")
    return 0 if failed[0] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())