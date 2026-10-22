## TODO 列表

> 维护规则：TODO 列表只保留未实现或仍需推进的目标；已完成目标应移动到 `docs/DONE.md`（补充实际实现方案与验收结论），避免把完成项继续当作待办.
>
> **维护范围**：本项目**只维护 `Emuera.Headless`**（无头运行器）。`Emuera/` WinForms 项目仅留作功能参考，不再维护。实际使用入口为 CLI 交互模式与 Server 模式；CLI/JSONL 管道模式已由 T-024 废弃。
>
> 当前目标筛选：最终目标按模式拆分——
>
> - CLI 模式：只实现文字相关 timer。
> - server 模式：未来应为前端提供足够数据以实现接近 WinForms 窗口的功能；本次暂不实现 server 相关 timer 功能。

### T-007：CLI 模式 REDRAW 输出抑制与强制刷新

- 状态：未实现
- 范围：`EmueraConsole.SetRedraw()`、`WriteAlignedLine()`、`AgentCliProtocol`
- 说明：`REDRAW 0` 抑制画面刷新，`REDRAW 2` 强制刷新。当前 CLI 模式完全忽略 REDRAW——`WriteAlignedLine()` 无条件写入 `_agentBuffer`，`FlushBuffer()` 无条件输出到终端。`REDRAW 0` 期间的中间输出也会立即显示。
- 纳入范围：
  - `WriteAlignedLine()` 中检查 `redraw == ConsoleRedraw.None`，跳过终端输出（仍写入 `displayLineList` 内存）。
  - `REDRAW 2` 时设置 `_needFullRefresh`，触发终端清屏 + 重绘 `displayLineList` 中累积的行。
- 不纳入范围：
  - `REDRAW 0` 期间 `SKIPDISP` 的交互（`SKIPDISP` 在 Process 层跳过整个指令，不进入 `addDisplayLine`，两者不冲突）。
- 验收：
  - `REDRAW 0` 期间终端不输出中间内容。
  - `REDRAW 2` 后终端一次性显示最终内容。
  - `REDRAW 1`（默认）行为不变。

### T-008：CLI 模式 SKIPDISP / NOSKIP 输出抑制

- 状态：未实现
- 范围：`Process.ScriptProc`、`WriteAlignedLine()`
- 说明：`SKIPDISP 1` 设置 `skipPrint = true`，Process 层跳过所有 `IsPrint()` 指令，不进入 `addDisplayLine`。`NOSKIP` 临时恢复输出，`ENDNOSKIP` 恢复抑制。当前 CLI 下 `SKIPDISP` 在 Process 层已正确跳过输出，但 `SKIPDISP 0` 恢复输出时不会触发终端刷新——如果 `SKIPDISP` 期间 `displayLineList` 被 `CLEARLINE` 等修改，终端不会同步。
- 纳入范围：
  - `SKIPDISP 0`（恢复输出）时检查 `displayLineList` 是否与终端一致，不一致则设置 `_needFullRefresh`。
  - 或更简单：`SKIPDISP` 状态变化时始终设置 `_needFullRefresh`。
- 不纳入范围：
  - `SKIPDISP` 核心逻辑（Process 层已正确实现）。
- 验收：
  - `SKIPDISP 1` 期间终端不输出。
  - `SKIPDISP 0` 恢复后终端显示与 `displayLineList` 一致。

### T-004a：server TINPUT timer metadata（ADR-0016）

- 状态：已落地（ADR-0016，2026-07-18 落地，T-004a 范围）
- 范围：`TurnRecord`、`DisplaySnapshot`、`AgentJsonlProtocol.BuildTurn`、`DisplayState.BuildSnapshot`、`SubmitTimeoutAsync`（`_pendingTimeoutFlag`）
- 说明：`InputRequest` 早已持有 `Timelimit` / `DisplayTime` / `TimeUpMes`，`ConsoleTimerManager` 已算好剩余时间，但 `TurnRecord` / `DisplaySnapshot` 不携带，导致 issue 04 前端 TINPUT 实时倒计时只能降级为启发式检测。本项把 timer 数据以结构化字段暴露给前端。
- 设计（ADR-0016）：
  - `TurnRecord` 新增 `timeLimit?: long`(ms) / `displayTime?: bool` / `timeUpMessage?: string` / `timedOut: bool`（默认 false）
  - `DisplaySnapshot` 新增 `timeLimit?` / `displayTime?` / `timeUpMessage?`（无 `timedOut`）
  - `protocolVersion` bump v5 → v6
  - 前端移除 `userInputSinceLastTurn` 启发式 + `markUserInput`，`timeoutNotice` 改为 `turn.timedOut` 派生
  - 前端倒计时静态 + 本地钟表（server 不周期 push tick）
- 验收（落地后）：
  - WaitInput turn 携带 `timeLimit > 0` + `displayTime == true`
  - 超时后下一帧携带 `timedOut == true` + `timeUpMessage`
  - `InputBar.vue` 显示 `<progress>` 实时倒计时
  - 前端测试覆盖 `parseTurnRecord` 4 字段 + `game` `timedOut` 派生；C# 补 `AgentJsonlProtocolTests` / `DisplayStateTests`

### T-004b：server 动画 timer + 事件驱动 HTTP（继续暂缓）

- 状态：暂缓，不纳入本次实现
- 范围：`SETANIMETIMER` / `SpriteAnime` 前端同步；`HttpGameServer` / `Session` 层事件驱动化（周期 push / 动画 tick）
- 说明：T-004 原暂缓项的剩余子项（(b) 动画帧数据 + (c) 事件驱动 HTTP）。依赖 sprite 加载（ADR-0013 决策五排除，未来 sprite 加载落地后扩展）与 server worker thread 边界重构。
- 当前不实现：
  - server 端图形动画 timer 数据。
  - `SETANIMETIMER` / `SpriteAnime` 前端同步。
  - server HTTP 层进一步事件驱动化。
- 后续再议：
  - 定义前端刷新间隔或动画帧数据。
  - 保持 server worker thread 与游戏步进边界清晰。

### T-015：CLI CLEARLINE 擦除行避免整行空格触发换行

- 状态：未实现，需修复
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：`EraseTerminalRows()` 当前使用 `new string(' ', Console.WindowWidth)` 覆盖旧行。Windows 终端写入整行宽度空格可能触发自动换行，导致终端内容下移，和 `docs/LESSONS/terminal-windows.md` 中记录的风险一致。
- 纳入范围：
  - ANSI 可用时优先使用 `\x1b[2K` 清除当前行。
  - fallback 使用 `SetCursorPosition` 定位后写入 `Math.Max(Console.WindowWidth - 1, 1)` 个空格。
  - 保持 `FlushBuffer()` 中先擦除旧行、再输出新内容的顺序。
- 验收：
  - `CLEARLINE` / `deleteLine()` 后终端不额外下移一行。
  - `changeLastLine()` 仍能原地替换最后一行。
  - ANSI 不可用的 Windows fallback 不残留旧文本。

### T-017：CLI 终端清行 fallback 边界防御

- 状态：未实现，需修复
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：`ClearButtonPrompt()` 的非 ANSI fallback 使用 `Console.WindowWidth - 1`，未处理窗口宽度为 0 或 1 的极端情况。`EraseTerminalRows()` 同样依赖终端宽度，应在 fallback 路径做最小宽度防御。
- 纳入范围：
  - 所有 `new string(' ', ...)` 的长度使用 `Math.Max(width, 1)` 或等价保护。
  - 对 `Console.WindowWidth` 抛异常的场景继续使用 80 作为 fallback。
- 验收：
  - 极小终端窗口下清除按钮提示行不抛异常。
  - ANSI 不可用时按钮提示行能被清空且光标回到原位置。

### T-020：CLI 鼠标悬浮高亮

- 状态：未实现（后续增强）
- 范围：`Emuera.Headless/Agent/AgentCliProtocol.cs`
- 说明：开启 XTerm 1002（按钮事件追踪）或 1003（任意事件追踪），接收鼠标移动事件。当鼠标悬浮在按钮区域上时，用 ANSI 样式（反色/下划线）高亮该按钮。鼠标离开时恢复。
- 前置：T-019。

### T-022：I-12 阶段 2 — Nullable enable + TreatWarningsAsErrors

- 状态：未实现（中期，T-023 已解锁）
- 范围：`Emuera.Headless/Emuera.Headless.csproj`、仓库根 `.editorconfig`
- 说明：I-12 阶段 1（2026-07-01）已落地按路径分级的质量护栏：
  - `Emuera.Headless.csproj` 启用 `<EnableNETAnalyzers>true</EnableNETAnalyzers>` + `<AnalysisMode>Minimum</AnalysisMode>` + `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
  - 仓库根 `.editorconfig` 对 `[Emuera.Headless/Shared/**]` 路径下的历史共享源码抑制 22 个 CA 规则与 CS8981/CS0472/CS0649/CS0162/CS0164 等编译器警告，避免 Headless 构建被历史代码噪音淹没（T-023 已完成路径规则迁移）
  - `Emuera.Headless/**` 自有源码警告保持可见（当前基线 132 条，主要是 CS8632 nullable 注解、CA1822 static 成员、CA1416 平台兼容性）
- 中期目标（T-023 已完成物理迁移共享源码到 `Emuera.Headless/Shared/`，前置已满足）：
  - T-023 落地后，`Emuera.Headless.csproj` 不再通过 csproj glob 共享 `Emuera/` 源码，分析器只扫描 Headless 自有源码 + `Shared/` 历史源码
  - 此时撤销根 `.editorconfig` 中 `[Emuera/**]` 段（迁移为 `[Emuera.Headless/Shared/**]`）的所有 `dotnet_diagnostic.CAxxxx.severity = none`，让 CA 规则回归到 `Shared/` 自己的责任范围
  - 把 `Emuera.Headless.csproj` 的 `<Nullable>` 从 `disable` 改为 `enable`，清零 CS8632/CS8600/CS8602 等 nullable 警告
  - 启用 `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + `<WarningsAsErrors>nullable</WarningsAsErrors>`，让 Headless 构建在引入新 warning 时直接失败
- 不纳入范围（短期）：
  - 在 T-023 完成前不启用 `TreatWarningsAsErrors`——会因 `Emuera/` 共享源码警告导致 Headless 构建失败，且修复会污染 `Shared/` 历史源码
  - 不在 `Emuera/**` 段（迁移后为 `Emuera.Headless/Shared/**`）加 `<TreatWarningsAsErrors>`，避免影响历史源码
- 验收：
  - `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 0 warning 0 error
  - 故意在 Headless 自有源码引入 `string? x = null;`（未启用 nullable 上下文）等典型问题，构建应失败
  - 根 `.editorconfig` 不再包含 `[Emuera/**]` 或 `[Emuera.Headless/Shared/**]` 段的 CA 抑制
- 参考：
  - 复评报告 [I-12 — csproj 质量护栏缺失](2026.6.30.架构健壮性重构/Emuera.Headless%20架构健壮性复评报告.md)
  - 评估报告 [4.4 性能与发布配置（I-12, I-13）](2026.6.30.架构健壮性重构/Emuera.Headless%20架构健壮性评估报告.md)

### T-025：server 模式空闲启动（无预绑游戏目录）

- 状态：已实现（spec: `.scratch/web-frontend/spec-t025-idle-start.md`）
- 范围：`KestrelGameServer`、`Program.Main`、`GamePaths`、`App.vue`、`GamePicker.vue`、`game.ts`
- 说明：让 server 支持空闲启动——不带 `--ExeDir`（或默认目录非法）时，server 正常监听、浏览器可打开游戏选择器；真正的游戏加载推迟到玩家在选择器里 `POST /load-game`。CLI 模式目录非法时改为打印提示 + 等回车再退出（不再静默关窗）。
- 设计决策（推翻旧设计纪要，详见 `spec-t025-idle-start.md` 决策树 D1–D17）：
  - `Program.Main` 中 `Validate()` 失败按模式分流：server 模式降级 warn 继续（空闲启动）；CLI 模式打印提示 + `Console.ReadLine()` 等回车再退出（D1/D2/D17）
  - **不引入** `_gameLoaded` 标志——空闲/已加载判定复用 `KestrelGameServer` 现有 `_session == null` 不变量（D4/D10）
  - `GamePaths.Current` 保持默认目录、`/load-game` 成功时覆盖（D3/D12，推翻旧「允许 null + 读取点加守卫」）——**已被 issue-009 取代**：进程级单例删除，根改为会话作用域
  - 空闲态 `GET /state` 的 `gameDir` 显式置 `null`（D5/D15）——前端据此可靠判 idle 并展示选择器
  - 空闲态 `POST /session` → 503 `{"error":"No game loaded"}`（D4/D16），早于「已有活跃会话 409」判断
  - 前端 `App.vue` 挂载改为：先 `GET /state`，若 `gameDir == null` / `state == "Idle"` → 展示选择器 + 预填 localStorage 上次目录，**不自动 loadGame**（D9 rev，推翻旧「刷新后自动加载」）
  - 前端新增「快速重开」按钮（D14）：header 区域，游戏运行/结束（`state` 非 Idle）时显示，一键 `DELETE /session` + `POST /load-game` 同目录；失败回退到空路径选择器（清空预填）
- 不纳入范围：
  - 多游戏列表式「游戏选择界面」（样式未定，仅 picker + 预填）
  - C# 端持久化 last-used dir（由前端 localStorage 承担）
  - 安卓 MAUI 原生 SAF 目录选择器（issue 05 stub 保留）
- 验收：
  - `Emuera.Headless.exe --server`（无 --ExeDir）正常启动，`GET /state` 返 `gameDir==null` / `state=="Idle"`，浏览器可打开 picker
  - 空闲态 `POST /session` → 503 `No game loaded`
  - picker 选目录后 `POST /load-game` 正常加载游戏，`GET /state` 变有效
  - `DELETE /session` 后 server 回到空闲态（`gameDir==null`、`POST /session` 再次 503）
  - 刷新页面：空闲/已结束 → 展示选择器 + 预填上次目录（不自动加载）；游戏运行中 → 重连当前局
  - 快速重开：同目录一键重载成功；失败回退到空路径选择器
  - CLI 模式目录非法时打印提示 + 等回车再退出（双击不闪退）
- 测试：`tests/test_idle_start.py`（35 断言，Python e2e）、`Emuera.Headless.Tests/KestrelGameServerIdleTests.cs`（3 用例，C# 单测）、`src/stores/__tests__/gameQuickRestart.test.ts`（8 用例，前端 Vitest）
