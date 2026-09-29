# Emuera.Headless 架构健壮性复评报告

> 复评基准：[Emuera.Headless 架构健壮性评估报告.md](Emuera.Headless%20架构健壮性评估报告.md)（2026-06-30）
> 复评日期：2026-07-01
> 复评范围：`Emuera.Headless/`、`Emuera/UI/Game/`、`docs/TODO.md`、`docs/DONE.md`

## 一、复评结论概览

本轮重构针对原报告 P0/P1 路线图取得了**显著进展**：P0 三项全部落地，P1 中 I-02/I-03/I-05/I-06/I-08 已完成核心改造，I-16 顺带完成。**协议层与 Server 模式的并发模型已脱胎换骨**，从"裸 Thread + sync-over-async + Thread.Sleep 忙等"演变为"Task + Channel + async/await"。

| 类别      | 数量 | 编号                                                 |
| ------- | -- | -------------------------------------------------- |
| ✅ 已解决   | 8  | I-03、I-04、I-05、I-06、I-07、I-08、I-16、I-15（JSONL 部分）  |
| 🟡 部分解决 | 4  | I-02、I-10、I-15（HTTP 500 部分）、I-17（AgentBridge 合并部分） |
| ❌ 未解决   | 6  | I-01、I-09、I-11、I-12、I-13、I-14、I-18                 |
| 🆕 新增问题 | 4  | N-01、N-02、N-03、N-04                                |

> 旧报告 I-17 包含"两份 .sln"与"partial 多余"两条，本报告拆分记录。

***

## 二、原问题逐项复核

### ✅ 已解决

#### I-03 — HttpGameServer 单会话契约澄清

**状态**：已解决

`_ioMap`（ConcurrentDictionary）已删除，`HttpGameServer.cs:13-19` 仅保留 `_session` + `_sessionLock`，实现与"单会话"文档契约一致。`POST /session` 在 `HasEnded` 后自动覆盖旧会话，运行中返回 409。

#### I-04 — 空 catch 大幅清理

**状态**：已解决

从 21 处降到 **3 处**，且每一处都有清晰注释或合理理由：

| 位置                                                                                                                      | 理由                                       |
| ----------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| [AgentCliProtocol.cs:71](../../EraCore.Core/Agent/AgentCliProtocol.cs#L71)                    | fatal 已写日志后 `Console.Error.WriteLine` 兜底 |
| [TerminalDisplayWidth.cs:165-168](../../EraCore.Core/Terminal/TerminalDisplayWidth.cs#L165-L168) | 探测失败保持默认，注释清晰                            |
| [VtParser.cs:184-187](../../EraCore.Core/Terminal/VtParser.cs#L184-L187)                         | UTF-8 解码失败丢弃字节，注释清晰                      |

其余原"空 catch"位置已替换为 `AgentLog.Instance.Write(ex)` 或具体异常处理（如 `HttpGameServer.cs:185-189` 返回 400）。

#### I-05 — Session async 化（Server 模式）

**状态**：已解决（Server 模式完全修复；Headless 模式残留 sync-over-async 见 N-02）

[Session.cs:42-50](../../EraCore.Core/Server/Session.cs#L42-L50) 用 `Task.Run(GameLoopAsync)` + `await _console.Initialize()` + `await _protocol.RunLoopAsync(...)`，彻底消除 Server 模式下的 `.Wait()` sync-over-async。

#### I-06 — 长轮询忙等消除

**状态**：已解决

`HttpGameServer.cs:145` 改为 `await session.WaitForTurnAsync(...)`；[Session.cs:97-133](../../EraCore.Core/Server/Session.cs#L97-L133) 通过 `Channel<string>.ReadAsync(ct)` + `CancellationTokenSource.CreateLinkedTokenSource + CancelAfter` 实现纯异步等待。原 `Thread.Sleep(50)` 忙等 25 秒的代码已删除。

#### I-07 — CLI 游戏结束空转 BUG（T-014）

**状态**：已解决

[AgentCliProtocol.cs:120](../../EraCore.Core/Agent/AgentCliProtocol.cs#L120) `while (!token.IsCancellationRequested && !IsGameExited())`，[AgentCliProtocol.cs:148-149](../../EraCore.Core/Agent/AgentCliProtocol.cs#L148-L149) `IsGameExited()` 检查 `ConsoleState.Quit or ConsoleState.Error`。已迁入 `docs/DONE.md`。

#### I-08 — 协议异常后半运行态 turn（T-016）

**状态**：已解决

- [AgentJsonlProtocol.cs:55-72](../../EraCore.Core/Agent/AgentJsonlProtocol.cs#L55-L72) `StepAsync` 捕获异常后调用 `Stop()`，让 `RunLoopAsync` 下一轮退出。
- [AgentJsonlProtocol.cs:83-89](../../EraCore.Core/Agent/AgentJsonlProtocol.cs#L83-L89) `SubmitTimeoutAsync` 改为 `async Task<string?>`，先 `console.SubmitTimeout()` 再 `await WaitForInputAsync()` 等待稳定状态后 `BuildTurn()`，不再返回半运行态 turn。
- [Session.cs:56-85](../../EraCore.Core/Server/Session.cs#L56-L85) `GameLoopAsync` 的 `finally` 块负责 `BuildFinalTurn` + `GlobalStatic.Reset`，确保 fatal 后 server 端状态可恢复。

#### I-15（JSONL 部分）— JSONL 畸形输入响应

**状态**：已解决

`HttpGameServer.cs:185-189` JSON 反序列化失败返回 `400 {"error":"Invalid JSON, expected {\"value\":\"...\"}"}`；[AgentJsonlProtocol.cs:172](../../EraCore.Core/Agent/AgentJsonlProtocol.cs#L172) JSONL 输入解析失败写日志后 `continue`（管道模式无法回写错误，行为合理）。

#### I-16 — Program.cs 拆分

**状态**：已解决

`Program.cs` 从 \~500 行缩减到 **80 行**，P/Invoke 与业务流彻底分离：

- [Program.cs](../../EraCore.Core/Program.cs) — 仅入口编排
- [HeadlessRunner.cs](../../EraCore.Cli/HeadlessRunner.cs) — 无头模式运行
- [ServerRunner.cs](../../EraCore.Cli/ServerRunner.cs) — 服务器模式运行
- [HeadlessOptions.cs](../../EraCore.Cli/HeadlessOptions.cs) — System.CommandLine 解析
- [GamePaths.cs](../../EraCore.Core/GamePaths.cs) — 路径解析与校验
- `UI/WindowsConsoleHelper.cs` + `Agent/Win32ConsoleInterop.cs` — P/Invoke 集中

***

### 🟡 部分解决

#### I-02 — GlobalStatic god-object

**状态**：部分解决（重大进展，但仍是单会话约束）

**已做**：[Emuera.Headless/GlobalStatic.cs](../../EraCore.Core/GlobalStatic.cs) 是 **Headless 专用实现**，不再通过 csproj glob 共享 `Emuera/GlobalStatic.cs`（csproj:59-60 明确注释）。

- 核心 9 字段改为 `property + internal set`，setter 内加锁 + 严格断言：非 null 时拒绝覆盖，强制调用 `Reset()`（[GlobalStatic.cs:34-185](../../EraCore.Core/GlobalStatic.cs#L34-L185)）
- 新增幂等 `Reset()` 方法（[GlobalStatic.cs:211-239](../../EraCore.Core/GlobalStatic.cs#L211-L239)），由 `Session.GameLoopAsync` finally 块调用清理全局状态
- 显式文档化"单会话不可变全局"硬约束

**未做**：字段仍是进程级 `static`，**无法支持并发多会话**。原报告建议的"构造注入或 `IGameRuntime` 上下文接口"未实施——这是合理的权衡，因为多会话需求尚不明确，先做硬约束防误用。

#### I-10 — AgentLog 单例

**状态**：部分解决

**已做**：[AgentLog.cs](../../EraCore.Core/Agent/AgentLog.cs) 改为默认开启（`EMUERA_AGENT_LOG` 未设或 `1`/`true` 即开启），加 `lock` 保护写入，注册 `ProcessExit` 钩子确保 flush。原"只在环境变量开启时才写文件"的隐患已消除。

**未做**：仍是 `Lazy<>` 单例 + 文件路径依赖 `Program.ExeDir` 静态。Server 多会话共享同一文件句柄（与 I-03 单会话约束叠加，目前不构成实际问题）。未引入 `Microsoft.Extensions.Logging` 抽象。

#### I-15（HTTP 500 部分）— 500 错误信息泄露

**状态**：未解决

`HttpGameServer.cs:80` 仍 `new { error = ex.Message }` 直接返回异常消息。本地开发场景风险可控，但若 server 部署到非 localhost 会泄露内部类型/路径信息。建议改为固定 `"internal error"` + 日志。

#### I-17（部分）— EmueraConsole.AgentBridge 合并

**状态**：部分解决

`EmueraConsole.AgentBridge.cs` 已合并入 [EmueraConsole.cs](../../EraCore.Core/UI/Game/EmueraConsole.cs)（注释 [418/428/450](../../EraCore.Core/UI/Game/EmueraConsole.cs#L418) 暗示历史 partial 来源），但 `partial` 修饰符仍保留（[EmueraConsole.cs:20](../../EraCore.Core/UI/Game/EmueraConsole.cs#L20)），且 `Emuera/Emuera.sln` 仍存在（见 I-17 未解决部分）。

***

### ❌ 未解决

#### I-01 — 共享源码 glob（结构性风险未消除）

**状态**：未解决

`Emuera.Headless.csproj:22-51` 仍用 `<Compile Include="..\Emuera\...">` glob 共享 Runtime/UI/Game 源码。Emuera 主项目重命名/移动文件仍会静默破坏 Headless 构建。**这是后续新功能开发前最该做的一次性投资**（原报告 P1 第 1 项）。

#### I-09 — 零 C# 单元测试

**状态**：未解决

`tests/` 目录仍全是 Python 端到端测试（test\_cli/test\_jsonl/test\_server\_single\_session/test\_tinput\_timeout/test\_mcp/test\_mcp\_interactive），**没有任何 xUnit/NUnit 项目**。`AgentJsonlProtocol.BuildTurn`、`HttpSessionIO` 队列语义、`ButtonRegionTracker` 坐标计算等纯逻辑无法隔离测试，每次 C# 改动仍需 `dotnet build` + Python 才能验证。

#### I-11 — HeadlessConsole.Environment.Exit 绕过 Dispose

**状态**：未解决

[HeadlessConsole.cs:26,43](../../EraCore.Core/Headless/HeadlessConsole.cs#L26-L43) 仍 `Close() => Environment.Exit(0)` / `ExitApplication() => Environment.Exit(0)`。该文件由 Emuera 主项目共享，改动会同时影响 WinForms——这是未修复的合理顾虑，但应至少在 Headless 路径加运行时分支（如 `#if HEADLESS` 抛 `GameExitException`）。

#### I-12 — csproj 质量护栏缺失

**状态**：未解决

`Emuera.Headless.csproj:1-13` 仍 `<Nullable>disable</Nullable>`，无 `<EnableNETAnalyzers>`、无 `<TreatWarningsAsErrors>`。`CLAUDE.md` 中"项目构建会产生大量已有 CS 警告，忽略 warning"的约定暗示这是有意为之，但长期看无静态分析护栏会让新代码质量问题持续累积。

#### I-13 — 依赖版本陈旧

**状态**：未解决

`Emuera.Headless.csproj:16-18`：

- `System.Drawing.Common` 锁定 `9.0.0`（TargetFramework 是 `net10.0`）
- `System.CommandLine` 仍是 `2.0.0-beta4.22272.1`（2022 年 beta，至今未 GA）
- `Enums.NET` `4.0.2` 版本未审计

#### I-14 — IConsoleUI 抽象泄漏 System.Drawing

**状态**：未解决

[IConsoleUI.cs:86](../../EraCore.Core/Shared/UI/Game/IConsoleUI.cs#L86) `ToolTipDrawEventArgs.Graphics` 仍暴露 `System.Drawing.Graphics`。HeadlessConsole 实现仍需引用 System.Drawing.Common（虽然 Headless 路径不触发 ToolTip Draw 事件）。`IConsoleUI` 也仍用 `System.Drawing.Point/Rectangle/Color`。

#### I-17 — 两份 .sln + partial 修饰符多余

**状态**：未解决

- `Emuera/Emuera.sln` 仍存在（仅含 Emuera 单项目），与根 `Emuera.sln` 易混淆
- [EmueraConsole.cs:20](../../EraCore.Core/UI/Game/EmueraConsole.cs#L20) `internal sealed partial class` 但全项目只有这一个文件，`partial` 多余

#### I-18 — csproj.user 未 gitignore

**状态**：未解决

[.gitignore](../../.gitignore) 第 10-11 行只忽略 `Emuera/Emuera.csproj.user` 与 `*.pubxml.user`，未忽略 `Emuera.Headless/Emuera.Headless.csproj.user`。`Emuera.Headless.csproj.user:4` 仍被跟踪，含 `_LastSelectedProfileId`。

***

## 三、新增问题

### 🆕 N-01 — Session.GameLoopAsync finally 中 Reset() 无 try-catch

**严重度**：🟡 中低

[Session.cs:83](../../EraCore.Core/Server/Session.cs#L83) `GlobalStatic.Reset()` 在 `finally` 块末尾，**未包裹 try-catch**。虽然 `Reset()` 是幂等的且只操作 backing field（理论上不抛异常），但其中 `_io.Close()`、`Pfc.Dispose()` 等步骤若抛异常，会传播到 `Task.UnobservedTaskException`，掩盖前面的游戏循环异常。

**建议**：把 `Reset()` 包入 try-catch，写日志后吞掉。

### 🆕 N-02 — HeadlessRunner 仍 sync-over-async

**严重度**：🟡 中低

[HeadlessRunner.cs:60](../../EraCore.Cli/HeadlessRunner.cs#L60) `console.Initialize().Wait()` 与 [HeadlessRunner.cs:63](../../EraCore.Cli/HeadlessRunner.cs#L63) `jsonl.RunLoopAsync(...).GetAwaiter().GetResult()` 仍是 sync-over-async。

**缓解因素**：HeadlessRunner 是顶层入口，`Main` 方法 `[STAThread]` + 默认无 SynchronizationContext，`.Wait()` 不会触发 `InvalidOperationException` 死锁。

**建议**：升级到 .NET 10 后可考虑 `Main` 改为 `async Task Main`，统一 async 模型；或保留现状但加注释说明"无 SynchronizationContext 风险"。

### 🆕 N-03 — ServerRunner 主线程 Console.ReadLine 阻塞

**严重度**：🟡 中低

[ServerRunner.cs:18](../../EraCore.Cli/ServerRunner.cs#L18) `Console.ReadLine()` 阻塞主线程等待 Enter。Ctrl+C 触发 `Console.CancelKeyPress` 默认终止进程，但其他关闭信号（如 POSIX SIGTERM）无处理。

**建议**：用 `Console.CancelKeyPress` 钩子调用 `server.Dispose()` 优雅关闭，避免 HTTP listener 与 Session Task 异常中止。

### 🆕 N-04 — 脚本死循环保护缺失（T-021）

**严重度**：🟠 中高

[docs/TODO.md:87-106](../TODO.md#L87-L106) 已记录 T-021：`Process.DoScript()` 内部指令循环遇到 ERB 死循环（`WHILE 1 \n WEND`）永不返回，所有调用 `RunEmueraProgram` 的路径都会卡死。本轮 I-08 修复的 `WaitForInputAsync` 30s 超时**救不了死循环**——`SubmitTimeoutAsync` 第一行 `console.SubmitTimeout()` 同步调 `RunEmueraProgram` 就卡住了，根本到不了 `await`。

**当前状态**：依赖 I-01 抽取 `Emuera.Core` 类库后才能给 `Process.DoScript` 加 `CancellationToken` 参数，目前标记"中期目标"。

***

## 四、本轮重构的整体评估

### 4.1 协议层演进

```
旧：裸 Thread → .Wait() sync-over-async → Thread.Sleep(50) 忙等 25s
新：Task.Run(GameLoopAsync) → await → Channel.ReadAsync(ct) + CancelAfter
```

`Session.WaitForTurnAsync` 的实现是本轮重构的**最佳实践样板**（[Session.cs:97-133](../../EraCore.Core/Server/Session.cs#L97-L133)）：

- 链式 `CancellationTokenSource` 同时处理外部取消与超时
- `Channel<string>` 多 reader 安全，`_finalTurnDelivered` 标志保证 finalTurn 一次性交付
- 注释清晰说明每种 `TurnWaitStatus` 的 HTTP 语义

### 4.2 全局状态管理

GlobalStatic 从 Emuera 共享改为 **Headless 专用 + Reset 机制**，是本轮最关键的结构性改动。这让"启动新 session"从"进程必须重启"变为"调用 Reset 后可复用进程"，为未来潜在的 server 模式热重启或会话切换打下基础。

但 Reset 的设计引入了**隐性约束**：调用顺序必须严格遵循"Session finally → Reset → 下一个 Session 构造"。目前仅靠 `_resetCalled` 标志防重复，**没有跨进程的契约测试**（因 I-09 未做）。

### 4.3 错误处理与可观测性

- AgentLog 默认开启是重要改进——之前空 catch 的"假装成功"问题在默认配置下不再掩盖异常
- AgentCliProtocol / AgentJsonlProtocol 的 fatal 路径都加了 `Stop()` + 日志
- 但 `AgentLog` 仍是单例，未来若引入多会话需重构

### 4.4 测试反馈链

`tests/run_all.py` 的 4 套件（JSONL+buttons 30/30、CLI 8/8、server single-session 24/24、TINPUT timeout 14/14）继续全绿，但这仍是端到端测试。**没有任何 C# 单元测试**意味着：

- 改 `BuildTurn` 序列化结构要起整个 server 才能验证
- 改 `ButtonRegionTracker` 坐标计算要构造鼠标点击 fixture
- 改 `Channel` 语义要靠 HTTP 集成测试反向验证

***

## 五、更新后的优先级路线图

### P0 — 已完成 ✅

1. ✅ I-07（T-014）：CLI 游戏结束空转
2. ✅ I-08（T-016）：协议异常后半运行态
3. ✅ I-04：空 catch 加日志

### P1 — 新功能开发期间应完成（更新）

1. **I-01**：抽取 `Emuera.Core` 类库 —— **仍是最高优先级结构性投资**，也是 N-04（T-021 脚本死循环保护）的前置依赖
2. **I-09**：建立 C# xUnit 测试项目 —— 本轮重构引入了 `Channel`/`Reset`/`WaitForTurnAsync` 等纯逻辑，急需单测保护
3. **N-04 / T-021**：脚本死循环保护（依赖 I-01）—— 任何长脚本场景的稳定性前提
4. **I-02 收尾**：评估是否需要 `IGameRuntime` 上下文接口 —— 当前单会话契约足够，但应在 I-01 抽 Core 时一并考虑接口边界

### P2 — 持续改进（更新）

1. **I-15 HTTP 500**：固定文案 + 日志（半小时改动）
2. **I-11**：HeadlessConsole 用 `#if HEADLESS` 分支抛 `GameExitException`
3. **I-12, I-13, I-14**：质量护栏、依赖升级、System.Drawing 抽象（可与 I-01 协同推进）
4. **I-10**：可注入 `ILogger`（Server 多会话前置）
5. **I-17, I-18, N-01, N-02, N-03**：清理 partial/sln/csproj.user、Reset 加 try-catch、ServerRunner 优雅关闭

***

## 六、结论

本轮健壮性重构在 **Server 模式并发模型**与**全局状态生命周期**上取得了**实质性突破**：

1. **协议层 async 化**（I-05/I-06/I-08）让 Server 模式从"勉强能跑"变为"符合 .NET 异步最佳实践"
2. **GlobalStatic Headless 专用实现 + Reset**（I-02）虽未消除单会话约束，但让约束变得**显式且可观测**
3. **空 catch 清理 + AgentLog 默认开启**（I-04/I-10）恢复了错误可见性
4. **CLI 空转 BUG 修复**（I-07）解决了所有交互式使用的最常见痛点

**结构性风险仍未消除**：

1. **共享源码 glob（I-01）** 仍是最大的耦合源——本轮靠"Headless 专用 GlobalStatic.cs + 显式 csproj 注释"绕开了一部分问题，但 Runtime/UI/Game 源码改动仍会同时影响两个项目
2. **零 C# 单测（I-09）** 让本轮引入的 Channel/Reset/WaitForTurnAsync 等纯逻辑缺乏回归保护，重构收益无法被量化验证
3. **脚本死循环（N-04/T-021）** 是隐藏的灾难场景，依赖 I-01 才能根治

**建议下一步**：在启动任何新功能前，先完成 I-01（抽 Core 类库）+ I-09（xUnit 项目）的组合拳。这两项一旦落地，N-04、I-02 收尾、I-14 等剩余项都能在更稳固的基础上推进。
