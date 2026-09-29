# TODO — 跨平台前置重构后续工作

## 已完成

### P0-1 终端抽象层提取

详见 [架构评估报告.md](./架构评估报告.md) 第四章 P0-1。
对应 git commits：`440b6f3` / `5fffdc6` / `746671e` / `35521e4` / `f574445` / `34d7045`。

完成范围：
- 新建 `Emuera.Headless/Terminal/Platform/` 目录
- 提取 `ITerminalSetup` 接口（`TryEnableAnsi` / `IsAnsiEnabled` / `TrySetConsoleSize` /
  `DetectFont` / `TryPrepareVtInput`）
- 提取 `ITerminalInput` 接口（`HasInputAvailable` / `ReadByte` / `EnableSgrMouse` /
  `DisableSgrMouse`，`IDisposable`）
- Windows 实现：`WindowsTerminalSetup`（P/Invoke `kernel32`：`GetStdHandle` /
  `SetConsoleMode` / `GetCurrentConsoleFontEx` 等）+ `WindowsTerminalInput`
  （VT 输入模式 + `ReadFile` + `WaitForSingleObject`）
- Posix 实现：`PosixTerminalSetup`（`tcgetattr` / `tcsetattr` / `cfmakeraw`
  raw mode + 退出恢复 hook）+ `PosixTerminalInput`（`poll` + `read` 非阻塞 I/O）
- `NullTerminalSetup` 空实现（Server 模式用）
- `VtInputHandler` 改为依赖 `ITerminalInput`
- `Program` 按 `OperatingSystem.IsWindows()` 选择 `WindowsTerminalInput` /
  `PosixTerminalInput` 与 `WindowsTerminalSetup` / `PosixTerminalSetup`
- `EmueraConsole` / `AgentCliProtocol` / `HeadlessRunner` / `Session` /
  `KestrelGameServer` 构造函数改为依赖 `ITerminalSetup` / `ITerminalInput`
- 删除 `WindowsConsoleHelper.cs` + `Win32ConsoleInterop.cs`（功能并入平台实现）

实现差异（vs 原方案）：
- 原方案提议单个 `ITerminalPlatform` 接口，实际拆分为 `ITerminalSetup` +
  `ITerminalInput` 两个接口（职责分离更清晰）
- `TerminalDisplayWidth.DetectCharWidths()` 仍用 `Console.SetCursorPosition`
  直接探测（`Console` 类在 Unix 通过 VT 转义实现，跨平台可用，未抽象到平台接口）

预期达成：CLI 模式可在 Linux/macOS 终端工作。

### P0-2 HTTP 服务器替换为 Kestrel

详见 [架构评估报告.md](./架构评估报告.md) 第四章 P0-2。
对应 git commit：`591a02c`。

完成范围：
- `Emuera.Headless/Server/HttpGameServer.cs` 内部类重写为
  `KestrelGameServer`（基于 `WebApplication` + `UseKestrel()` + `UseUrls`）
- 路由通过 `_app.MapPost/MapGet/MapDelete` 注册
  （`/session` POST/DELETE、`/turn` GET、`/input` POST、`/state` GET）
- `ServerRunner.cs` 改用 `KestrelGameServer`
- `Emuera.Headless.csproj` 添加
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
- `Session` / `SessionIO` / `HttpSessionIO` 层不变（只替换传输层）
- 删除 `HttpListener` 依赖（代码中已无引用）

预期达成：Server 模式在 Linux/macOS 上开箱可用；为 WebSocket 铺路。

### P1-3 Turn 协议版本化（v1）

详见 [PRD-T3-Turn协议版本化.md](./PRD-T3-Turn协议版本化.md)、
ADR-0001。

- `TurnRecord` + `ButtonEntry` record 落地
- `protocolVersion: 1` 仅出现在 initial turn
- wire format 字节级不变（除 initial turn 多 `protocolVersion` 字段）
- `test_jsonl.py` 扩展断言 `protocolVersion`

### P1-4 Fatal turn 结构断言测试

详见 [PRD-T4-FatalTurn测试.md](./PRD-T4-FatalTurn测试.md)。
对应 issue #3（已关闭）。

- `test_fatal_turn.py` 落地：复制 `test_game` 注入抛异常 ERB，断言 fatal turn 结构
- v2 升级时（PRD-T5）已同步更新断言为 `ops == []`

### P1-5 富 Turn 升级（v2 操作序列模型）

详见 [PRD-T5-富Turn升级.md](./PRD-T5-富Turn升级.md)、
ADR-0002。
对应 issue #4 / #5 / #6（均已关闭）。

完成范围：
- `TurnRecord` 重写：删除 `text`/`buttons`/`ButtonEntry`，新增 `TurnOp` 层次
- `TurnOp`/`PrintOp`/`NewLineOp`/`ClearLineOp`/`ClearOp`/`SetBgOp`/`PrintSegment`/`ButtonRef`
- `EmuColor.ToHex()` 扩展方法
- `ConsoleStateData._pendingOps` 队列
- `ConsolePrintManager.AddDisplayLine` emit print+newline ops
- `ConsolePrintManager.DeleteLine` 新增 `suppressOp` 参数，emit clearline op
- `ConsolePrintManager.ClearDisplay` emit clear op
- `ConsolePrintManager.SetBgColor` emit set_bg op
- `AgentJsonlProtocol.BuildTurn` 改为读 `TakePendingOps`
- `CurrentProtocolVersion = 2`
- `CollectVisibleButtons()` 删除
- `test_jsonl.py` 重写为断言 `ops[]` 结构
- `test_fatal_turn.py` / `test_server_single_session.py` / `test_tinput_timeout.py` / `test_selectcase_loading.py` 同步更新
- 所有回归测试通过

### PRD-T6 CLI 双写技术债消除（displayLineList delta）

详见 [PRD-T6-CLI双写消除.md](./PRD-T6-CLI双写消除.md)、
ADR-0003。
对应 issue #7 / #8 / #9（均已关闭）。

完成范围：
- `TerminalRenderer.FlushBuffer` 改为读 `displayLineList` delta：持有
  `_lastRenderedLineNo` + `_lastRenderedLastLine`（reference），三分支逻辑
  （`>` 新增行 / `<` 删除行 / `==` && `!ReferenceEquals` merge 替换）
- 新增 `DrainPendingOpsForCli(Action<TurnOp>)` 回调式 drain，CLI 仅动作于
  `ClearOp` / `SetBgOp`，其余 op drain 但不动作
- `AddDisplayLine` 删除 `WriteAlignedLine` 调用（消除双写）
- Input echo 改直接 `Console.Write`（`WriteOutput` 移到 `AgentCliProtocol`，
  删除 `AppendToAgentBuffer`）
- `FullRefresh` 末尾同步 `_lastRenderedLineNo` / `_lastRenderedLastLine`
- VT 模式获得 `set_bg` 能力（`ESC[48;2;r;g;bm`，新 feature 红利）
- 删除双写状态：`_agentBuffer` / `_agentBufferLineCount` /
  `TakeAgentBuffer` / `WriteToAgentBuffer` / `WriteToAgentBufferNoNewline` /
  `AppendToAgentBuffer` / `RemoveLastLineFromAgentBuffer` /
  `_pendingEraseRows` / `ConsumePendingEraseRows` / `WriteAlignedLine`
- 接口瘦身：`IConsoleStateView` 仅保留 `ConsumeNeedFullRefresh`
- `test_cli_basic.py` 扩展为 ConPTY smoke test 覆盖
  clearline/clear/setbg/merge/alignment 路径
- VT 模式 set_bg ConPTY smoke test 落地

已知风险接受：
- `LineNo` wraparound（`int.MaxValue` 回 0）极罕见但会导致 delta 失步，
  失步后 `FullRefresh` 可恢复
- `FullRefresh` 忘同步 delta tracking state 会让后续 `FlushBuffer` 全错——
  实现已处理（`FullRefresh` 末尾同步）

#### P2-5：EmueraConsole 门面瘦身（按 ADR-0004 收缩路线已完成）

详见 [架构评估报告.md](./架构评估报告.md) 第四章 P2-5、ADR-0004。

- 原方向（`IConsoleState`/`IConsoleInput`/`IConsolePrint` 接口提取）被 ADR-0004
  supersede，改为"先收缩后评估"
- 完成范围（代码已落地）：
  - `EmueraConsole` 改为 `internal sealed`，公开成员仅剩 `ConsumeNeedFullRefresh`
    （`IConsoleStateView` 实现）+ `Dispose`
  - Print 20+ 方法由 `public` 降为 `internal`
  - 删除 WinForms 死代码：`CBG_*` / `rikaichan` / `CBProc` / `GetLinePointY`
  - 终端格式方法 `FormatLineForTerminal` / `FormatLineWithAnsi` / `BuildTerminalLine` /
    `GetGameColumnWidth` 外移至 `Terminal/TerminalLineFormatter.cs`（纯函数、零依赖）
  - Agent 层仅依赖窄接口 `IConsoleStateView`，不依赖整个 facade
- 预期达成：EmueraConsole 公开表面从约 50 成员降至约 2，门面瘦身目标达成

## 已取消

### P1-4：IConsoleUI.Invoke 调度器抽象

原计划：`IConsoleUI` 添加 `Task InvokeAsync(Func<Task>)` 方法，让未来 Web 前端
可注入 `SynchronizationContext` 或 `Dispatcher`（详见
[架构评估报告.md](./架构评估报告.md) 第四章 P1-4）。

**取消理由**：原前提"未来 Web 前端实现可注入 `SynchronizationContext`"假设前端
与 Headless 同进程。实际架构采用 HTTP/JSON 协议解耦（参见 `KestrelGameServer` +
`HttpSessionIO`）：Web 前端是独立进程，通过 `/turn` / `/input` 端点与 Headless
通信，不直接调用 `IConsoleUI`。`IConsoleUI` 在 Headless 模式下永远是
`HeadlessConsole` 空实现，游戏引擎仍单线程跑，同步 `Invoke(Action)` 已满足需求。
前端自身的异步 UI 调度由前端框架处理，与 Headless 无关。

代码现状佐证：
- `IConsoleUI.Invoke(Action)` 仍是同步方法，未添加 `InvokeAsync`
- `HeadlessConsole.Invoke` 直接同步执行 `action?.Invoke()`
- 4 个调用方（`AgentJsonlProtocol` / `ConsoleRefreshHandler` /
  `ConsoleTimerManager`）全部同步使用

### P2-7：AgentCliProtocol 拆分

原计划：`AgentCliProtocol.cs`（报告评估时描述 441 行）拆分为可维护小组件，
`LoopStrategy` 子类提取为独立文件，VT 模式管理提取为 `VtSessionManager`
（详见 [架构评估报告.md](./架构评估报告.md) 第四章 P2-7）。

**取消理由**：报告评估时（2026.7.4）的描述基于预 ADR-0005 状态。ADR-0005
（VT-only 重构，2026.7.7）已删除 `LoopStrategy`/`VtLoopStrategy`/
`ConsoleKeyLoopStrategy` 策略类并将主循环内联，同时将渲染/按钮/倒计时逻辑提取至
`TerminalRenderer`/`ButtonSelectionMode`/`CountdownRenderer`，VT 屏幕/输入生命周期
外移至 `AgentCliVtScreen`/`VtInputHandler`。当前 `AgentCliProtocol.cs` 约 382 行，
原 P2-7 目标已被 ADR-0005 实质吞并。ADR-0005 明确拒绝"保留 LoopStrategy 抽象以备
扩展"并立下"避免预期式抽象"原则，继续按原描述拆分等于为拆而拆、踩中该原则。

代码现状佐证：
- `AgentCliProtocol.cs` 无嵌套策略类；注释 `// ADR-0005：原 LoopStrategy 策略模式已删除，主循环逻辑直接内联（VT-only）`
- `TerminalRenderer`/`ButtonSelectionMode`/`CountdownRenderer`/`AgentCliVtScreen`/`VtInputHandler` 均为独立文件

## 后续工作

### 富 Turn 升级排除项（独立立项）

PRD-T5 grilling 阶段明确排除以下三项，待条件成熟后独立立项：

- **R-06 按钮区域**（`PointX` / `Width` / `row` / `col`）：立项为 PRD-T6，待 v2
  落地后评估是否还需要服务端坐标。v2 的 `print.button` 已足够让前端自行做命中
  测试。
- **R-09 图片元数据**：`PrintImg` / `PrintShape` 在 v2 降级为
  `print(node.ToString())`。真正的图片暴露等 Headless 实现 sprite 加载
  （`AppContents.GetSprite` 非空实现）后再立项。
- **LLM 兼容层**：v2 不保留 `text` 字段作 LLM 降级。LLM 交互由前端层中转——
  前端把 op 序列渲染成纯文本再喂给 LLM。

### 架构评估报告待办

详见 [架构评估报告.md](./架构评估报告.md) 第四章。以下任务独立于 Turn 协议演进，
按优先级排列：

#### P2-6：Shared 子树债务清零（渐进）

- **目标**：逐步移除 `.editorconfig` 对 `Shared/**` 的警告抑制
- **方向**：按 T-022 计划逐步修复 nullable 警告，最终全项目
  `TreatWarningsAsErrors` 无抑制
- **候选 5 关联（2026-07-13）**：架构深化候选报告原将 `Shared/UI/Game/Image/`、`Shared/Runtime/Utils/PluginSystem/`、`HtmlManager.cs` 列为"死代码"。静态验证结论——三者均非死代码，从本 P2-6 "噪音/可删"清单移除：
  - `Image/` 的 `System.Drawing` 渲染实现已在 `#else`/`#if !HEADLESS` 编译期排除；HEADLESS 仅编译 no-op 存根（`GraphicsImage`/`CroppedImage`/`ConstImage`/`AppContents`），由 ERB `G*` 图形指令与状态切换调用，运行时无渲染路径——属有意保留的 HEADLESS 后端，非可删债务。
  - `IImageContext`/`IBitmapImage`/`IBrush` + `Headless/HeadlessImageContext.cs` 为候选 4 未来图像支持的跨平台抽象 seam，有意保留。
  - `PluginSystem` 经 `LoadEngineData`+`@CALLSHARP` 活可达；`HtmlManager` 经 `PRINT`/`ConsolePrintManager` 活可达——均非死代码。
  - 候选 5 以 no-code-change 关闭；上述项的剩余 nullable 警告仍按本 P2-6 方向逐步修复，但不作为"可删死代码"追踪。

## 基础文档索引

- [PRD-T3](./PRD-T3-Turn协议版本化.md)：v1 版本化
- [PRD-T4](./PRD-T4-FatalTurn测试.md)：fatal turn 测试
- [PRD-T5](./PRD-T5-富Turn升级.md)：v2 操作序列模型
- [PRD-T6](./PRD-T6-CLI双写消除.md)：CLI 双写技术债消除（displayLineList delta）
- ADR-0001：v1 版本字段策略
- ADR-0002：v2 操作序列模型决策
- ADR-0003：CLI 渲染层 displayLineList delta 决策
- `CONTEXT.md`：领域术语表（含 Operation Sequence Model 子章节）
- [架构评估报告.md](./架构评估报告.md)：P0/P1/P2 全局任务清单
