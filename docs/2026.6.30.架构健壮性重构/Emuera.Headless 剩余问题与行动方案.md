# Emuera.Headless 剩余问题与行动方案

> 基准：[Emuera.Headless 架构健壮性复评报告.md](Emuera.Headless%20架构健壮性复评报告.md)（2026-07-01）
> 复核日期：2026-07-02
> 定位：本文档汇总**尚未解决或仅部分解决**的问题与对应行动方案，作为后续重构的执行清单。已解决问题不在此列。

## 一、现实约束

经与维护者确认，项目维护范围收窄：

1. **只维护 Emuera.Headless**：Emuera（WinForms）项目功能数年前已稳定，无维护必要，仅留作功能参考。
2. **CLI 交互模式与 Server 模式**是实际使用入口；**CLI 管道模式（stdin pipe）与 JSONL 管道模式（stdin/stdout）无实际用途**。

### 已解决项概览（仅记录，不在下文展开）

I-03、I-04、I-05、I-06、I-07、I-08、I-13、I-15（JSONL + HTTP 500）、I-16、I-18；新增问题 N-01（Reset try-catch）、N-02（HeadlessRunner async 化）、N-03（ServerRunner 优雅关闭）、N-04（脚本死循环，由 T-021 `#if HEADLESS` + `GameExitException` 解决）均已落地。

***

## 二、剩余问题清单

### 🟡 部分解决

#### I-02 — GlobalStatic god-object

**状态**：部分解决（重大进展，仍是单会话约束）

**已做**：[Emuera.Headless/GlobalStatic.cs](../../EraCore.Core/GlobalStatic.cs) 是 Headless 专用实现，核心字段改为 `property + internal set`，setter 加锁 + 严格断言；新增幂等 `Reset()` 方法，由 `Session.GameLoopAsync` finally 块调用。

**未做**：字段仍是进程级 `static`，**无法支持并发多会话**。原报告建议的"`IGameRuntime` 上下文接口"未实施——**单会话契约已确认**（server 模式只支持单会话，见 [CLAUDE.md](../../CLAUDE.md) 与 [SPEC_SINGLE_THREAD.md §5.2](../old/3.single-thread/SPEC_SINGLE_THREAD.md)），现有硬约束（`HttpGameServer` 单 `_session` + 409、`GlobalStatic` 锁 + 断言 + 幂等 `Reset`）足以防误用，无需再引入上下文接口。

#### I-10 — AgentLog 单例

**状态**：部分解决

**已做**：[AgentLog.cs](../../EraCore.Core/Agent/AgentLog.cs) 默认开启，加 `lock` 保护写入，注册 `ProcessExit` 钩子确保 flush。

**未做**：仍是 `Lazy<>` 单例 + 文件路径依赖 `Program.ExeDir` 静态。未引入 `Microsoft.Extensions.Logging` 抽象。Server 多会话共享同一文件句柄（与单会话约束叠加，目前不构成实际问题）。

#### I-17（部分）— EmueraConsole.AgentBridge 合并

**状态**：部分解决

**已做**：`EmueraConsole.AgentBridge.cs` 已合并入 [EmueraConsole.cs](../../EraCore.Core/UI/Game/EmueraConsole.cs)。

**未做**：`partial` 修饰符仍保留（[EmueraConsole.cs:20](../../EraCore.Core/UI/Game/EmueraConsole.cs#L20)），且 `Emuera/Emuera.sln` 仍存在。详见下文 I-17 未解决部分。

***

### ✅ 已解决

#### I-01 — 共享源码 glob（结构性风险）

**状态**：已解决（T-023 物理迁移完成）

`Emuera.Headless.csproj` 不再有 `<Compile Include="..\Emuera\...">` glob。T-023 已将共享源码物理迁移到 `Emuera.Headless/Shared/`，由 SDK 默认 glob 自动包含。

#### I-11 — HeadlessConsole.Environment.Exit 绕过 Dispose

**状态**：已解决

[HeadlessConsole.cs](../../EraCore.Core/Headless/HeadlessConsole.cs) 的 `Close()` / `ExitApplication()` 已改为抛 `GameExitException`。`GameExitException` 类已创建，异常处理链完整：
- `HeadlessRunner.cs:71-74`：catch 后静默退出
- `Session.cs:52`：Server 模式正确 catch
- `Process.cs:351-395`：脚本层特殊处理，避免被当作错误

#### I-12（阶段 2）— csproj 质量护栏

**状态**：已解决

`Emuera.Headless.csproj` 已启用：
- `<Nullable>enable</Nullable>`（第 9 行）
- `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`（第 10 行）
- `.editorconfig` 按路径分级抑制历史警告，Headless 自有源码警告可见

#### I-17 — 两份 .sln + partial 修饰符多余

**状态**：已解决

- `Emuera/Emuera.sln` 已删除，只保留根目录 `Emuera.sln`
- [EmueraConsole.cs:20](../../EraCore.Core/UI/Game/EmueraConsole.cs#L20) `partial` 修饰符已移除

### ❌ 未解决

#### I-09 — 零 C# 单元测试

**状态**：未解决

`tests/` 目录全是 Python 端到端测试，**没有任何 xUnit/NUnit 项目**。`AgentJsonlProtocol.BuildTurn`、`HttpSessionIO` 队列语义、`ButtonRegionTracker` 坐标计算等纯逻辑无法隔离测试，每次 C# 改动仍需 `dotnet build` + Python 才能验证。

#### I-14 — IConsoleUI 抽象泄漏 System.Drawing

**状态**：部分解决（阶段 A 已完成）

**已做**：
- `ToolTipDrawEventArgs.Graphics` 已移除（Headless 模式下 Draw 事件从未触发）
- `ToolTipPopupEventArgs.Size` 已移除（Popup 事件从未触发）
- 自定义值类型已创建：`EmuPoint`/`EmuRectangle`/`EmuColor`/`EmuSize`（`Primitives/` 目录）
- 隐式转换兼容现有代码，无需修改任何使用处

**未做**（阶段 B/C）：
- 渐进式移除 37 个文件的 `using System.Drawing`（可逐文件进行）
- 从 csproj 移除 `System.Drawing.Common` 依赖

详见 [I-14阶段2-自定义值类型实施方案.md](I-14阶段2-自定义值类型实施方案.md)

***

## 三、行动方案

### <a id="t023"></a>T-023：I-01 简化 — 物理迁移共享源码到 Emuera.Headless

**核心思路**：用 `git mv` 把 `Emuera/` 下被共享的源码物理迁移到 `Emuera.Headless/` 下，删除 csproj glob，WinForms 专用文件留在 `Emuera/` 原处作只读参考。

| 步骤 | 操作 |
|---|---|
| 1 | `git mv Emuera/Runtime/ Emuera.Headless/Runtime/`（排除 `Sound.WMP.cs`/`Sound.NAudio.cs`/`NAudio_LoopStream.cs`/`WinInput.cs`/`Clipboard.cs`，留在 `Emuera/` 原处） |
| 2 | `git mv` `Emuera/UI/Game/` 下被共享的文件到 `Emuera.Headless/UI/Game/`（**不可覆盖**已有的 [EmueraConsole.cs](../../EraCore.Core/UI/Game/EmueraConsole.cs)；`EmueraConsole.Print.cs` 不迁移） |
| 3 | `git mv Emuera/UI/FontFactory.cs Emuera.Headless/UI/FontFactory.cs` |
| 4 | `git mv Emuera/Properties/lang/ Emuera.Headless/Properties/lang/` |
| 5 | 简化 `Emuera.Headless.csproj`：删除所有 `<Compile Include="..\Emuera\...">` glob 与 `<Compile Remove="...">` 排除项，改由 SDK 默认 glob 包含；`<EmbeddedResource>` 路径同步更新 |
| 6 | 删除 `Emuera/Emuera.csproj`、`Emuera/Emuera.sln`；仓库根 `Emuera.sln` 移除 Emuera 项目引用，只保留 `Emuera.Headless` |

**解锁**：T-022（I-12 阶段 2）、I-11、I-14、I-17

**验收**：

- `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 error（warning 基线不变）
- `Emuera.Headless.csproj` 中不再出现 `..\Emuera\` 路径引用
- `run_all.py` 全部回归测试通过
- `Emuera/` 目录下不再有 `.csproj`/`.sln`，残留文件不被任何项目引用

### T-024：废弃 CLI/JSONL 管道模式入口

**核心思路**：移除 stdin 管道入口与 `ConsoleOutIO`，收窄维护与测试矩阵。

| 步骤 | 操作 |
|---|---|
| 1 | 删除 `ConsoleOutIO.cs`（stdin/stdout 封装，仅管道模式使用） |
| 2 | [HeadlessOptions.cs](../../EraCore.Cli/HeadlessOptions.cs) / [HeadlessRunner.cs](../../EraCore.Cli/HeadlessRunner.cs) 移除 `--protocol cli`/`--protocol jsonl` 的 stdin 管道分支；`--protocol` 参数保留以兼容交互 CLI |
| 3 | [AgentCliProtocol](../../EraCore.Core/Agent/AgentCliProtocol.cs) 改为只支持交互式终端（移除 stdin pipe 读路径） |
| 4 | [AgentJsonlProtocol](../../EraCore.Core/Agent/AgentJsonlProtocol.cs) **保留**（Server 模式通过 `HttpSessionIO` 仍依赖它，不改动） |
| 5 | 精简 `tests/test_cli.py`、`tests/test_jsonl.py` 中针对 stdin 管道的用例；更新 [tests/README.md](../../tests/README.md) 测试矩阵与 [CLAUDE.md](../../CLAUDE.md) 运行命令 |

**验收**：

- `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 error
- 交互 CLI 模式与 Server 模式回归测试全绿
- `ConsoleOutIO.cs` 已删除，代码中无 stdin 管道引用

***

## 四、优先级路线图

### P1 — 新功能开发期间应完成

1. ~~**T-023**：物理迁移共享源码到 `Emuera.Headless/`~~ —— ✅ 已完成，顺带解决 I-01、I-17
2. **T-024**：废弃 CLI/JSONL 管道模式入口 —— 收窄维护面
3. **I-09**：建立 C# xUnit 测试项目 —— 保护 Channel/Reset/WaitForTurnAsync 等纯逻辑
4. ~~**T-022（I-12 阶段 2）**：`Nullable enable` + `TreatWarningsAsErrors`~~ —— ✅ 已完成
5. ~~**I-02 收尾**：评估 `IGameRuntime` 上下文接口~~ —— ✅ 已关闭：单会话契约已确认，无需引入上下文接口

### P2 — 持续改进

1. ~~**I-11**：HeadlessConsole 改抛 `GameExitException`~~ —— ✅ 已完成
2. **I-14**：`IConsoleUI` 抽象去 `System.Drawing` —— 🟡 阶段 A 完成（自定义类型已创建，隐式转换兼容）
3. **I-10**：可注入 `ILogger`（Server 多会话前置）

### 关键路径依赖

```
T-023 (物理迁移) ──┬─→ ✅ T-022 (Nullable + WarningsAsErrors) 已完成
                   ├─→ ✅ I-11 (HeadlessConsole Exit) 已完成
                   ├─→ 🟡 I-14 (IConsoleUI 去 System.Drawing) 阶段 A 完成
                   └─→ ✅ I-17 (sln/partial 清理) 已完成

T-024 (管道废弃) ──→ 收窄测试矩阵，为 I-09 单测项目减负
```

**当前状态**：T-023 已完成，解锁了 I-11、I-17、I-12 阶段 2（均已落地）。I-14 阶段 A 已完成（自定义类型 + 隐式转换），阶段 B/C 可根据需要逐步推进。下一步建议推进 T-024（废弃管道模式）与 I-09（建立 C# 单测项目）。
