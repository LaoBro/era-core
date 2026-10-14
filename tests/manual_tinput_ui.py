"""手动测试脚本：ADR-0016 TINPUT timer metadata UI 验证。

一键启动 C# headless server（8080）+ Vite dev server（5173），注入覆盖所有
ADR-0016 测试场景的 ERB，并自动打开浏览器。开发者按 ERB 菜单选择测试场景，
肉眼验证 UI 行为。

用法
----
1. 构建 Debug exe（若尚未构建）：

       dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug

2. 安装前端依赖（仅首次运行）：

       cd EraCore.Web && npm install

3. 一键启动（C# server + Vite + 浏览器）：

       python tests/manual_tinput_ui.py

4. 浏览器加载后，按 ERB 菜单选择测试场景。

   也可直接访问 http://localhost:8080/snapshot 查看原始 JSON，
   或用 curl GET /turn 验证 WS / HTTP 帧中的 4 个 timer 字段。

5. 验证完毕后按 Enter 关闭两个 server。

测试场景（ERB 内置菜单）
------------------------
- [0] 场景 A：5s 显示倒计时 + 超时
      验证 InputBar <progress> 倒计时 + timeoutNotice 横幅（timeUpMessage）
- [1] 场景 B：5s 显示倒计时 + 手动输入
      验证手动输入后下一帧无 timeoutNotice 横幅
- [2] 场景 C：3s 隐藏倒计时（disp=0）+ 超时
      验证 displayTime=false 时不显示 <progress>，但超时仍触发 timeUpMessage
- [3] 场景 D：30s 长倒计时（晚加入者测试）
      验证 GET /snapshot 携带 timer 字段，重连浏览器后倒计时继续显示
- [9] 退出游戏

每个场景结束后返回菜单，可重复测试。
"""
import shutil
import subprocess
import sys
import time
import webbrowser
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WEB_DIR = ROOT / "EraCore.Web"
sys.path.insert(0, str(ROOT / "tests"))

from emuera_server import PROTOCOL_VERSION, copy_test_game_with_erb, start_server, wait_for_port


# 注入到临时游戏目录的 ERB。覆盖 ADR-0016 全部测试场景。
# TINPUT 语法：TINPUT time, def, disp, timeout, mouse
#   time    超时毫秒（>0）
#   def     超时后 RESULT 的默认值
#   disp    是否显示倒计时（1=显示，0=隐藏）
#   timeout 超时提示文案
#   mouse   是否允许鼠标输入（0=禁用）
ERB = """@SYSTEM_TITLE
$LOOP
PRINTL === ADR-0016 TINPUT 手动测试 ===
PRINTL
PRINTL 请选择测试场景：
PRINTL  [0] 场景 A：5s 显示倒计时 + 超时
PRINTL  [1] 场景 B：5s 显示倒计时 + 手动输入
PRINTL  [2] 场景 C：3s 隐藏倒计时 + 超时（disp=0）
PRINTL  [3] 场景 D：30s 长倒计时（晚加入者测试）
PRINTL  [9] 退出游戏
PRINTL
INPUT
IF RESULT == 0
\tPRINTL === 场景 A：5s 显示倒计时 + 超时 ===
\tPRINTL 期望：InputBar 显示倒计时（5.0s → 0s）；5s 后显示 timeUpMessage 横幅
\tPRINTL --- TINPUT 开始（不输入，等待超时）---
\tTINPUT 5000, 100, 1, "A 超时，默认值 100", 0
\tPRINTFORML RESULT = {RESULT}（期望：100）
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 1
\tPRINTL === 场景 B：5s 显示倒计时 + 手动输入 ===
\tPRINTL 期望：InputBar 显示倒计时；手动输入数字后无 timeUpMessage 横幅
\tPRINTL --- TINPUT 开始（请输入数字，如 42）---
\tTINPUT 5000, 0, 1, "B 超时（不应出现此横幅）", 0
\tPRINTFORML RESULT = {RESULT}
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 2
\tPRINTL === 场景 C：3s 隐藏倒计时 + 超时 ===
\tPRINTL 期望：InputBar 不显示倒计时（displayTime=false）；3s 后仍显示 timeUpMessage 横幅
\tPRINTL --- TINPUT 开始（不输入，等待超时）---
\tTINPUT 3000, 200, 0, "C 隐藏倒计时也超时", 0
\tPRINTFORML RESULT = {RESULT}（期望：200）
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 3
\tPRINTL === 场景 D：30s 长倒计时（晚加入者测试）===
\tPRINTL 期望：InputBar 显示倒计时；可刷新浏览器验证 GET /snapshot 携带 timer 字段
\tPRINTL --- TINPUT 开始（30s 超时或输入数字提前结束）---
\tTINPUT 30000, 0, 1, "D 30s 长倒计时结束", 0
\tPRINTFORML RESULT = {RESULT}
\tPRINTL [0] 返回菜单
\tINPUT
ELSEIF RESULT == 9
\tQUIT
ELSE
\tPRINTL 无效输入，请重试
ENDIF
GOTO LOOP
"""


def start_vite_dev(web_dir: Path, port: int = 5173, timeout: float = 30.0):
    """启动 Vite dev server（在 EraCore.Web 目录运行 `npm run dev`）。

    - shell=True 让 Windows 找到 npm.cmd
    - stdout/stderr 走 DEVNULL，避免管道缓冲被填满导致 npm 阻塞
      （Vite 启动后持续输出日志，若 Popen 用 PIPE 且不读会死锁）
    - --strictPort 强制端口，被占用时直接报错而不是漂移到 5174
    """
    if not (web_dir / "node_modules").is_dir():
        raise FileNotFoundError(
            f"{web_dir} 下没有 node_modules，请先运行：cd EraCore.Web && npm install"
        )

    proc = subprocess.Popen(
        ["npm", "run", "dev", "--", "--port", str(port), "--strictPort"],
        cwd=str(web_dir),
        shell=True,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    try:
        wait_for_port(port, timeout=timeout)
    except Exception:
        stop_process_tree(proc)
        raise
    return proc


def stop_process_tree(proc):
    """杀掉进程树。Windows 用 taskkill /T 一并杀掉 npm.cmd 派生的 vite 子进程，
    避免单独 kill npm 后 vite 仍监听 5173。
    """
    if proc.poll() is not None:
        return
    if sys.platform == "win32":
        subprocess.run(
            ["taskkill", "/F", "/T", "/PID", str(proc.pid)],
            capture_output=True,
        )
    else:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


def main():
    temp_dir = None
    server = None
    vite_proc = None
    try:
        temp_dir, game_dir = copy_test_game_with_erb(ERB)
        # 固定 8080 端口：vite.config.ts 的 /ws /snapshot /session 代理都指向 8080
        server = start_server(game_dir, port=8080)

        # 注意：这里不调 server.create_session()——session 由浏览器加载后
        # 通过 POST /session 创建。若脚本抢先创建 session，server 立即跑
        # 游戏循环 → 几毫秒内写出 initialTurn 到 Hub → 此时浏览器还没
        # WS 订阅，Hub 不回放历史 → initialTurn 被丢弃 → 浏览器永远收不到
        # 首帧 → ERB 在 INPUT 处等待输入，不产生新 turn → WS 卡死。
        # 交给浏览器创建 session 才能保证 WS 订阅在前、initialTurn 在后。

        vite_url = "http://localhost:5173"
        try:
            vite_proc = start_vite_dev(WEB_DIR, port=5173)
            vite_ready = True
        except Exception as e:
            print(f"WARNING: 启动 Vite dev server 失败：{e}")
            print(f"         可手动在另一终端运行：cd EraCore.Web && npm run dev")
            vite_ready = False

        print()
        print("=" * 64)
        print("ADR-0016 TINPUT 手动测试环境已启动")
        print("=" * 64)
        print()
        print(f"  C# server      : {server.base_url}")
        if vite_ready:
            print(f"  Vite dev server: {vite_url}")
        print(f"  临时游戏目录   : {game_dir}")
        print()
        if vite_ready:
            print(f"3 秒后自动打开浏览器：{vite_url}")
            time.sleep(3)
            try:
                webbrowser.open(vite_url)
            except Exception:
                pass
        else:
            print("协议层直接验证（无前端）：")
            print(f"  - curl {server.base_url}/snapshot  # 应含 timeLimit/displayTime/timeUpMessage")
            print(f"  - curl {server.base_url}/turn       # WaitInput 帧应含 4 个 timer 字段")
        print()
        print("ERB 内置测试场景：")
        print("  [0] 场景 A：5s 显示倒计时 + 超时      → 验证 timeUpMessage 横幅")
        print("  [1] 场景 B：5s 显示倒计时 + 手动输入  → 验证无横幅")
        print("  [2] 场景 C：3s 隐藏倒计时 + 超时      → 验证 displayTime=false")
        print("  [3] 场景 D：30s 长倒计时              → 验证晚加入者 GET /snapshot")
        print("  [9] 退出游戏")
        print()
        print("验证项清单：")
        print("  [ ] InputBar <progress> 倒计时平滑递减（awaiting.wait.displayTime=true 时）")
        print("  [ ] awaiting.wait.displayTime=false 时不显示 <progress>")
        print("  [ ] 超时后下一帧显示 timeoutNotice 横幅（awaiting.wait.timeUpMessage）")
        print("  [ ] 手动输入后无 timeoutNotice 横幅")
        print("  [ ] v12：TINPUT 计时元数据只在 awaiting.wait（durationMs/displayTime/timeUpMessage/timedOut），顶层无旧四字段")
        print("  [ ] 超时续跑帧 awaiting.wait.timedOut=true（一次性，非超时帧不出现）")
        print(f"  [ ] 协议版本 protocolVersion={PROTOCOL_VERSION}（v12：wire 只剩 diff/awaiting/error/generation）")
        print("  [ ] 晚加入者 GET /snapshot 携带 awaiting.wait timer 字段")
        print()
        print("按 Enter 关闭两个 server（或 Ctrl+C 强制退出）...")
        try:
            input()
        except KeyboardInterrupt:
            pass
        return 0
    finally:
        if vite_proc is not None:
            stop_process_tree(vite_proc)
        if server is not None:
            try:
                server.delete_session()
            except Exception:
                pass
            server.close()
        if temp_dir is not None:
            shutil.rmtree(temp_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
