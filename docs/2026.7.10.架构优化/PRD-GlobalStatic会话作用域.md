# PRD — GlobalStatic 会话作用域深化（候选 1）

**关联**：ADR-0008 · [架构深化候选报告](架构深化候选报告.md) · 仓库 [LaoBro/Emuera.MCP](https://github.com/LaoBro/Emuera.MCP)

## Problem Statement

作为 Emuera.Headless 的维护者，我即将开展**单元测试、跨平台构建、跨平台 Web 前端**三项新开发。但当前 `GlobalStatic` 是**进程级 `static class`**：9 个核心字段（Console / Process / GameBaseData / ConstantData / VariableData / VEvaluator / IdentifierDictionary / EMediator / LabelDictionary）以 `static` property + lock + 单赋值断言保护，`Reset()` 在 Session finally/Dispose 幂等清理。

这造成三重阻塞：

1. **C# 单测无从落地**：任何测试要么完整初始化整条链（Config→Process→EmueraConsole→字体/csv/erb 校验），要么污染进程状态。测试基建至今为零。
2. **跨平台 Web 前端无法建可测会话工厂**：Web 前端要的"开一个会话、跑、关"的能力，被进程级单例锁死——同一进程无法隔离地构造第二个会话上下文。
3. **遗留 Shared/ 代码臃肿解构无从下手**：候选 2（Config static 上帝类）、候选 3（Process god object）都依赖 GlobalStatic 先解除进程级绑定，否则 static 之间双向耦合无从拆。

## Solution

把 `GlobalStatic` 从"进程级 static + Reset 幂等"深化为"**会话作用域实例 + ambient `Current` + scope RAII**"。Shared/ ERB 运行时（280 处读取）**零代码改动**——通过 static `Current` 转发属性保留 `GlobalStatic.VEvaluator` 调用语法。Headless 两个入口（Server 的 `Session.GameLoopAsync`、CLI 的 `HeadlessRunner.RunAsyncCore`）各自用 `using var scope = GlobalStatic.OpenScope()` 包住游戏执行块，scope 在 finally 自动释放。`Reset()` 与其幂等 hack 全部删除——scope 的 RAII 单一归属替代。

落地后：C# 单测可开隔离 scope 验证 Session 不污染进程；并行测试各自 scope 互不干扰；候选 2/3 的前置就位。

## User Stories

1. 作为 Emuera.Headless 维护者，我希望 `GlobalStatic` 不再是进程级单例，这样我能在单个测试进程里构造多个隔离的会话上下文。
2. 作为测试作者，我希望调用 `GlobalStatic.OpenScope()` 拿到一个 `IDisposable` scope，这样我能用 `using` 块限定会话状态的生命周期。
3. 作为测试作者，我希望 scope Dispose 后 `GlobalStatic.Current` 回到 `null`，这样我能在测试的 finally 后断言进程状态未被污染。
4. 作为测试作者，我希望两个并行测试各自开 scope、各自写 `ForceQuitAndRestart`，互不污染，这样我能放心并行跑测试套件。
5. 作为测试作者，我希望在同一 scope 内二次 set 同一字段（如重复 `Initialize`）抛 `InvalidOperationException`，这样双初始化 bug 能在测试里被捕获而非静默吞掉。
6. 作为 Emuera.Headless 维护者，我希望 Shared/ ERB 运行时代码（Instraction.Child / VariableEvaluator / Creator.Method 等 280 处读取）**完全不需要改动**，这样候选 1 的回归面被压到最小。
7. 作为 Emuera.Headless 维护者，我希望 `GlobalStatic.VEvaluator` 这类调用语法保留不变，这样 Shared/ 的 280 处读取不会因重命名引入拼写/语义回归。
8. 作为 Server 模式维护者，我希望 HTTP 请求线程（/turn、/input、/session DELETE）从不直接触碰 `GlobalStatic.Current`，这样跨请求的 AsyncLocal 隔离无需额外论证。
9. 作为 Server 模式维护者，我希望 `Session.GameLoopAsync` 在顶部开 scope、finally 自动关，这样游戏循环跑在自带 scope 的 async 上下文里，会话结束即清理。
10. 作为 CLI 模式维护者，我希望 `HeadlessRunner` 的 Initialize+RunLoop 块同样被 scope 包住，这样 CLI 与 Server 共用同一套会话作用域语义。
11. 作为 Emuera.Headless 维护者，我希望 `Reset()` 方法及其 `_resetCalled` 幂等标志、backing-field 绕过 setter 的 trick 全部删除，这样会话清理从"双重 Reset 调用 + 幂等 hack"简化为单一 scope RAII。
12. 作为 Emuera.Headless 维护者，我希望 `Session.Dispose` 仍先 join 游戏任务再释放 scope，这样游戏循环一定先退出，scope 释放时无 dangling 读取。
13. 作为测试作者，我希望有一个新建的 `Emuera.Headless.Tests/` 项目（xUnit），这样 C# 单测有正式归宿而非塞进主项目。
14. 作为测试作者，我希望测试项目用 `InternalsVisibleTo` 访问主项目 `internal` 成员，这样 scope API 的内部细节可被直测。
15. 作为测试作者，我希望测试项目 `TreatWarningsAsErrors=false`，这样测试代码的宽松警告不阻塞构建。
16. 作为 Emuera.Headless 维护者，我希望首个测试集只验 scope 机制本身（生命周期 / 并行隔离 / 单赋值断言），不依赖真实游戏/ERB/字体，这样候选 1 的杠杆能被快速、确定性地验证。
17. 作为 Emuera.Headless 维护者，我希望既有 Python 端到端回归测试（server_single_session / tinput_timeout / force_quit_survival / jsonl）保持全绿，这样深化不破坏现有 Server/CLI 行为。
18. 作为即将开发跨平台 Web 前端的工程师，我希望会话状态已是可实例化、可隔离的对象，这样 Web 前端的"会话工厂"能建立在真实可测的 scope 之上而非绕开 GlobalStatic。
19. 作为即将开发单元测试的工程师，我希望 GlobalStatic 进程级阻塞已解除，这样我能为 EmueraConsole / Console*Managers / Agent 协议逐步补单测（候选 3 切 Process seam 之后）。
20. 作为即将推进候选 2（Config static 上帝类）的工程师，我希望 GlobalStatic 的实例化机制已就位，这样 Config 能复用同一 scope/注入模式。
21. 作为文档读者，我希望 `CONTEXT.md` 收录 "SessionScope / ambient Current" 术语，这样后续架构讨论有统一词汇。

## Implementation Decisions

- **两区策略**：Headless 自有 ~40 处读取暂不强制 ctor 注入（候选 1 阶段经 scope 可测即可）；Shared/ ERB 运行时 ~280 处保留 ambient 访问、零改动。深化点在"生命周期与可替换性"，不在"消除全局访问"——ERB 指令求值访问当前会话的 VariableData/EMediator 语义本身正确。
- **`GlobalStatic` 升实例 + static `Current` 转发**：`static class` → `class`，9 字段变实例成员；新增 `static GlobalStatic Current`（`AsyncLocal<GlobalStatic>` 承载）；原 static 属性改一行转发 `=> Current._field`。Shared/ 280 处 0 改动、0 重命名。
- **承载选 `AsyncLocal<GlobalStatic>`**：游戏循环 async 链流转正确；并行测试隔离；生产单会话开销可忽略。
- **scope RAII（O1）**：`Session.GameLoopAsync` 首行 `using var scope = GlobalStatic.OpenScope()`，finally 自动关；`HeadlessRunner.RunAsyncCore` 同理包住 Initialize+RunLoop。Current 仅存于游戏 task 的 async 上下文，HTTP 线程零介入。
- **实例形状**：单赋值断言保留（锁从 static 改实例字段，防双 Initialize）；辅助字段 `tempDic` / `ForceQuitAndRestart` / `Pfc` / `ctrlZ` / `StackList(DEBUG)` 全部进实例；`Pfc.Dispose()` 进 scope.Dispose。
- **删除 `Reset()`** + 两处调用（Session finally + Dispose）+ `_resetCalled` + backing-field bypass。`Session.Dispose` 的 join 游戏任务保留。
- **初始化顺序不变**：9 字段全部在 `console.Initialize()` 内被 set（ConsoleStateManager 设 Console+Process，Process.Initialize 设其余 6，VariableEvaluator 设 VariableData）。scope 在 Initialize 之前开，字段在 scope 内被 set，断言自然生效。
- **测试基建**：新建 `Emuera.Headless.Tests/`（仓库根，平级主项目，加入 `Emuera.sln`），`net10.0`，xUnit，`InternalsVisibleTo("Emuera.Headless.Tests")`，`TreatWarningsAsErrors=false`。
- **垂直切片**：prefactor（测试项目脚手架）先行；GlobalStatic 深化作为原子 tracer-bullet 跟进（static→instance + 两入口接线 + 删 Reset 必须同提交，否则 build 断）。

详细决策与拒绝的替代方案见 ADR-0008。

## Testing Decisions

**什么是好测试**：只测外部可观察行为，不测实现细节。scope API 测"开/关/隔离/断言"的行为契约，不测 AsyncLocal 内部、不测锁字段名。

**测试模块与 seam（三层）**：

1. **主 seam — `GlobalStatic` scope API**（新建 C# 直测，零游戏依赖）：
   - T-a：`OpenScope` 后 `Current != null`，Dispose 后 `Current == null`（生命周期契约）。
   - T-b：两并行 task 各自开 scope、各设 `ForceQuitAndRestart` 不同值，断言互不污染（AsyncLocal 隔离契约）。
   - T-c：scope 内二次 set 同字段抛 `InvalidOperationException`（单赋值断言契约）。
2. **次 seam — `Session` 烟测**（轻量，不跑完整 ERB）：在 scope 内构造 Session、Start、断言测试线程 `Current == null`（隔离不泄漏）。
3. **回归 seam — 既有 Python 端到端**（不改）：`server_single_session` / `tinput_timeout` / `force_quit_survival` / `jsonl`，确保 Server/CLI 行为不变。

**既有先例**：项目至今 C# 单测为零，本 PRD 首建。Python 端到端测试是既有的高层安全网先例（见 `tests/README.md`）。

## Out of Scope

- **候选 2（Config static 上帝类深化）**：依赖本 PRD 落地后推进，独立 ADR。
- **候选 3（Process god object 切 seam）**：Shared/ ERB 运行时内部组织重构、EmueraConsole/Console\*Managers 的 ctor 注入（T2/T3），均待候选 3。
- **fake globals 注入（O2 升级）**：调用方开 scope + Session 收 GlobalStatic 实例，留给候选 3+ 评估，本 PRD 不预留该抽象（遵循 ADR-0005 避免预期式抽象）。
- **Shared/ 280 处读取的任何重命名或重写**：本 PRD 明确零改动。
- **Image / PluginSystem 死代码清理（候选 5）**：独立进行。
- **Web 前端接缝（候选 4）**：依赖本 PRD，独立 PRD。
- **并行多会话**（同一进程并发多个 Session）：当前仍是单会话，scope 机制为测试隔离而非生产多会话。

## Further Notes

- 本 PRD 由 2026-07-10 grilling 会话产出，决策树：两区策略(A) → ambient Current 转发(A1) → T1 测试性目标 → scope RAII(O1) → 实例形状确认 → xUnit 测试基建。完整记录见 ADR-0008。
- AsyncLocal Current 仅在游戏 task 上下文存活：`Task.Run(GameLoopAsync)` 从调用 `Start` 的上下文继承 Current，游戏循环及其 async 续延可见；HTTP 请求线程（独立请求上下文）零介入。
- `Session.cs` L171 TODO#6 注释（Reset 为"让紧随其后的 new Session 拿到干净状态"）随 Reset 删除而失效——新会话是新 scope 新实例，天然干净。
- 实施时关注 `Emuera.Headless/**` 下文件新引入的 CA/CS 警告（按根 `.editorconfig` 路径分级护栏），`Shared/` 历史警告已全局抑制。
