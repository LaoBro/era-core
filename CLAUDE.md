# CLAUDE.md

本文件只保留进入项目时必须知道的范围、命令、约束和文档入口。详细架构见 [`ARCHITECTURE.md`](ARCHITECTURE.md)，专题内容不要复制到本文件。

## 维护范围

当前维护核心：

- `EraCore.Core`
- `EraCore.Server`
- `EraCore.Cli`
- `EraCore.Maui`
- `EraCore.Web`

`Emuera/` 是已停止维护的 WinForms 参考源码；`EmueraPluginExample/` 和 `experiments/` 不属于产品主路径。

控制权模型：同一活跃会话同时最多一个 Controller（`kind: agent|user`）。输入与活跃会话的生命周期操作只对 Controller 放行；旁观者只读。术语见根 `CONTEXT.md`「控制权交接」；agent 操控礼仪见 [`.agents/skills/eracore-playtesting/SKILL.md`](.agents/skills/eracore-playtesting/SKILL.md)。

项目结构、依赖边界和运行时数据流见 [`ARCHITECTURE.md`](ARCHITECTURE.md)。

## 前置依赖

- .NET 10 SDK 或更高版本
- Python 3.10+
- Node.js 18+
- Windows CLI/ConPTY 测试：`pip install pywinpty`
- WebSocket 测试：`pip install websockets`

## 常用命令

### 构建 CLI/Server

```bash
dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug
```

### 运行交互式 CLI

需要真实 TTY：

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --ExeDir test_game --protocol cli
```

### 运行 HTTP Server

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --ExeDir test_game --server --port 8080
```

### 构建/运行 MAUI Windows

```bash
dotnet build EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
dotnet run --project EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

### C# 单元测试

```bash
dotnet test EraCore.Tests/EraCore.Tests.csproj

# 真大小写敏感环境（WSL2 Ubuntu ext4；SDK 装在 ~/.dotnet）——碰访问器/通配/分隔符的改动必跑
# 把 /mnt/d/<repo> 换成仓库在 WSL 下的路径；命令里不要出现 $（外层 shell 会先展开 $PATH）
wsl -d Ubuntu -- bash -lc "cd /mnt/d/<repo> && ~/.dotnet/dotnet test EraCore.Tests/EraCore.Tests.csproj"
```

Windows 测不出「文件名大小写 / 通配 pattern / 路径分隔符」类缺陷。WSL 下**基线固定 5 个既有失败 + 时序/并发类偶发 flaky**，只有不在名单里、且单跑也不通过的失败才算回归；触发清单与判读口径见 [`docs/LESSONS/testing-and-ci.md`](docs/LESSONS/testing-and-ci.md) #12。

### 全量回归

```bash
python tests/run_all.py
```

测试夹具、套件和单项运行方式见 [`tests/README.md`](tests/README.md)。

### Web 前端

```bash
cd EraCore.Web && npm install
npm run dev
npm run typecheck
npm test
npm run build
```

前端文件导航和常见修改入口见 [`EraCore.Web/README.md`](EraCore.Web/README.md)。

### eracore_agent CLI

一次调用 = 一个回合。stdout 只输出 turn JSON，错误走 stderr + 非零退出码。`start` 自己拉起 Headless server，或复用已在跑的实例（默认 `localhost:8080`，或读 `.eracore-server.json`）。

```bash
python -m eracore_agent start --game-dir test_game
python -m eracore_agent acquire
python -m eracore_agent step --value 0
python -m eracore_agent release
python -m eracore_agent status
python -m eracore_agent watch
python -m eracore_agent stop
```

安装 console 入口后也可用 `eracore_agent <subcommand>`。路径预设写在 `.eracore-agent.json`，运行时 server 记录（端口 / pid / token / gameDir）写在 `.eracore-server.json`，两者都不要提交。操控礼仪见 [`.agents/skills/eracore-playtesting/SKILL.md`](.agents/skills/eracore-playtesting/SKILL.md)。

## 必须遵守的约束

- Core/Server/Cli/Maui 开启 `TreatWarningsAsErrors`；新增 warning 必须修复，不能随意增加 `NoWarn`。
- T-024 后 stdin pipe 和 JSONL stdin/stdout 模式已删除；自动化统一使用 `--server`。
- 升级 Agent 协议版本时，必须同步更新 C# `TurnRecord.CurrentProtocolVersion` 和 Python `tests/emuera_server.py:PROTOCOL_VERSION`。
- MAUI 只依赖 Core，不引入 Server/Cli 的 ASP.NET Core 依赖。
- 修改前端结构、组件或状态时，先查看 [`EraCore.Web/README.md`](EraCore.Web/README.md)。
- NativeAOT 结论以 [`docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md`](docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md) 为准，不在本文件重复论证。
- `IGameDirAccessor` 的两个通配重载**禁止把 `searchPattern` 透传给 `Directory.GetFiles/GetDirectories`**——其大小写语义随 OS 变（Windows 不敏感 / Unix 敏感），必须走托管侧 `Wildcard.Matches` 过滤，保证真实路径与 SAF 语义一致。详见 [`docs/LESSONS/testing-and-ci.md`](docs/LESSONS/testing-and-ci.md) #12 与 [`saf-file-access.md`](docs/LESSONS/saf-file-access.md) #24。

## 环境陷阱

- 在 Windows/TRAE 环境中，不要使用 `Start-Process -RedirectStandardOutput ... -Wait` 捕获全量 `dotnet build` 输出。重定向管道可能因缓冲区填满而死锁。直接运行 `dotnet build`，或使用不会阻塞输出读取的方式。
- 当前 Windows 沙箱中直接运行 `dotnet build` / `dotnet test` 可能在并行 MSBuild 项目引用解析阶段以“0 错误”但退出码 1 静默失败。遇到时加 `-m:1` 单节点构建/测试，例如：
  ```bash
  dotnet test EraCore.Tests/EraCore.Tests.csproj --no-restore -m:1
  ```
- 在受限沙箱中运行 `dotnet test` 时，testhost 可能因无法打开进程句柄而报 `Win32Exception (5): 拒绝访问`（`System.Diagnostics.Process.EnableRaisingEvents` 路径）。需要给测试运行授予完整进程访问权限（例如沙箱的 `danger-full-access`）才能执行测试。
- 在 WSL 中直接运行 Windows `dotnet.exe` 并通过管道读取输出可能卡住/超时。可将 stdout/stderr 重定向到文件再查看，例如：
  ```bash
  '/mnt/c/Program Files/dotnet/dotnet.exe' test EraCore.Tests/EraCore.Tests.csproj > /tmp/dotnet-test.log 2>&1
  ```
- 从 WSL 启动 Windows 二进制做 Python e2e 时，传入 `/mnt/d/...` 这类 WSL 路径可能导致 server 起不来。应使用 Windows Python（如 `/mnt/c/Python314/python.exe`）运行 `tests/` 脚本，让路径自动变成 `D:\...`。
- `tests/run_all.py` 在 WSL/Windows 混合环境下可能出现偶发性能护栏抖动、409 时序问题或文件锁；单项测试通常可稳定通过。若 `dotnet build` 报 DLL 被占用，先结束残留的 `EraCore.Cli` 进程（如 `taskkill.exe /PID <pid> /F`)再重试。

## 文档索引

- 架构总览：[`ARCHITECTURE.md`](ARCHITECTURE.md)
- 控制权术语：根目录 `CONTEXT.md`「控制权交接」
- Web 前端：[`EraCore.Web/README.md`](EraCore.Web/README.md)
- 测试：[`tests/README.md`](tests/README.md)
- 终端行为教训：[`docs/LESSONS/terminal-windows.md`](docs/LESSONS/terminal-windows.md)
- 全部教训索引：[`docs/LESSONS/README.md`](docs/LESSONS/README.md)
- Issue tracker：[`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md)
- Triage labels：[`docs/agents/triage-labels.md`](docs/agents/triage-labels.md)
- Domain docs：[`docs/agents/domain.md`](docs/agents/domain.md)

## Agent skills

Issues live under `.scratch/<feature>/`. 使用仓库中 `.agents/skills/` 提供的专业流程；需要规划、实现、测试、审查或文档维护时，先加载匹配的 skill。操控游戏走 [`eracore-playtesting`](.agents/skills/eracore-playtesting/SKILL.md)。
