# PRD: CLI 模式 TINPUT 超时挂钟计时修复

- **日期**: 2026-07-09
- **关联 ADR**: ADR-0007、ADR-0006
- **关联 Glossary**: `CONTEXT.md` — WaitInputEnteredAt、auto-follow、Scroll Mode

## Problem Statement

CLI 模式下使用 `TINPUT` 指令的脚本超时永不触发。游戏卡在等待输入状态，用户无感知，只能手动输入才能推进游戏。

具体表现：
- ERB 脚本执行 `TINPUT 2500, 7, 0, "TIMEUP_MARKER", 0` 后，2.5 秒后游戏**没有**自动以默认结果 7 继续
- 倒计时显示（若启用 `DisplayTime`）永远停在初始值 2.5s 不递减
- 用户必须手动输入才能推进游戏

此 bug 阻塞了 ADR-0006 Issue 001 的 auto-follow 验收——Scroll Mode 下新输出到达时归零 Scroll Offset 的机制无法通过 PTY 端到端测试验证，因为 CLI 模式下无法通过 TINPUT 超时产生新输出来触发 auto-follow。

## Solution

在 CLI 协议层（`AgentCliProtocol.RunVtLoop`）引入挂钟计时，绕过失效的 `genericTimer` stopwatch 机制：

- 记录进入 `WaitInput` 状态的时刻（`WaitInputEnteredAt`）
- poll 循环每轮检查挂钟 elapsed，超时则调 `SubmitTimeout`
- 倒计时显示改用挂钟 elapsed 计算剩余时间
- auto-follow 路径触发后清空计时上下文

Server 模式不受影响——它已用 `CancellationTokenSource.CancelAfter` 独立计时，不依赖 stopwatch。

## User Stories

1. 作为 CLI 用户，我希望 `TINPUT` 指令的超时能正常触发，这样脚本能在超时后自动推进而无需我手动输入。
2. 作为 CLI 用户，我希望倒计时显示随时间递减，这样我能直观看到剩余等待时间。
3. 作为 CLI 用户，在 Scroll Mode（回看历史）下等待 TINPUT 超时，我希望超时正常触发并自动归零 offset 回到最新输出，这样我不会错过新内容。
4. 作为 CLI 用户，当 TINPUT 超时触发后，我希望游戏以脚本的 `defaultResult` 继续执行，这样超时后的脚本逻辑能正确分支。
5. 作为 CLI 用户，当我手动输入推进游戏（非超时路径）后，我希望计时上下文被清空，这样下一次 TINPUT 不会受上次计时影响。
6. 作为 CLI 用户，当 auto-follow 路径触发（如 ConsumeNeedFullRefresh 或 resize）后，我希望计时上下文被清空，这样不会误触发上一轮的超时。
7. 作为开发者，我希望 CLI 与 server 模式的 TINPUT 超时语义一致（都能触发），这样不需要为两种模式分别编写超时逻辑。
8. 作为开发者，我希望修复范围限于 CLI 路径，server 路径不动，这样降低回归风险。
9. 作为开发者，我希望有 PTY 端到端测试覆盖 TINPUT 超时触发 + auto-follow 归零，这样能验证 ADR-0006 Issue 001 的验收标准。
10. 作为开发者，我希望倒计时显示正确递减，这样不会出现"倒计时静止"的 bug。
11. 作为开发者，我希望 `WaitInputEnteredAt` 的生命周期在所有退出路径（State 离开、SubmitTimeout 后、auto-follow 后）都被正确清空，这样不会出现残留计时导致误触发。
12. 作为开发者，我希望 ADR-0007 记录挂钟计时决策与 A/B/C 扶择，这样未来维护者能理解为什么 CLI 不用 genericTimer stopwatch。

## Implementation Decisions

### 修改范围
仅改 CLI 路径。Server 路径（`AgentJsonlProtocol` + `HttpSessionIO`）不动。

### ConsoleTimerManager（UI/Game/Console 层）
- 新增 `InputTimelimit` 属性：返回当前 `WaitInput` 的超时阈值（毫秒），0 表示无超时。供 CLI `HandleTimeout` 读取阈值，绕过 `InputTimeoutMs` 的 stopwatch 依赖。
- 新增 `BuildCountdownText(long elapsedMs)` 重载：接受外部传入的 elapsed，计算 `remainingMs = Timelimit - elapsedMs`。原无参版（依赖 `_genericTimerStopwatch`）无调用方，已删除。

### AgentCliProtocol（Agent 协议层）
- 新增 `private DateTime? WaitInputEnteredAt` 字段。
- `RunVtLoop` poll 循环维护生命周期：
  - 检测 `console.State == WaitInput && WaitInputEnteredAt == null && InputTimelimit > 0` 时记录 `DateTime.UtcNow`
  - `InputTimeoutMs == null` 时清空（覆盖 State 离开 WaitInput 与 SubmitTimeout 后两种情况，因 SubmitTimeout 改变 State 使 InputTimeoutMs 返回 null）
  - `ConsumeNeedFullRefresh` 与 resize 分支在 `ResetScrollIfActive()` 后显式清空
- `HandleTimeout` 改读 `WaitInputEnteredAt`：
  - `WaitInputEnteredAt == null` → return false
  - `elapsed = DateTime.UtcNow - WaitInputEnteredAt >= InputTimelimit` → 调 SubmitTimeout + 立即清空 WaitInputEnteredAt
  - 保留现有的 `_renderer.FlushBuffer()`（当前轮立即触发 auto-follow）

### CountdownRenderer（Terminal 层）
- `Update` 方法改为接受 `elapsedMs` 参数（或通过回调获取），调用 `BuildCountdownText(elapsedMs)` 重载，使倒计时显示基于挂钟 elapsed 动态递减。
- 调用方（`AgentCliProtocol` poll 循环）传入挂钟 elapsed。

### WindowsTerminalInput（Terminal/Platform 层）
- 改用后台读取线程（`Thread` + `IsBackground=true`）阻塞在 `ReadFile` 上，将字节推入 `ConcurrentQueue<byte>`。
- `HasInputAvailable` 改为检查队列是否非空（非阻塞），`ReadByte` 改为 `TryDequeue`（非阻塞）。
- 根因：原 `HasInputAvailable` 用 `WaitForSingleObject(stdin, 0)`，在 ConPTY 下对 phantom 事件误报 signaled，而 `ReadFile` 在 VT 输入模式下对非 VT 事件阻塞 → poll 循环停滞 → `HandleTimeout` 永不被调用。
- 同时移除 `ENABLE_WINDOW_INPUT | ENABLE_MOUSE_INPUT` legacy 标志：ConPTY 已将鼠标/resize 以 VT 序列投递，无需 legacy 事件生成。
- `Dispose` 时不 `Join` 后台线程（`ReadFile` 可能仍阻塞），进程退出时自动终止。

### AgentCliProtocol poll 循环结构调整
- `HandleTimeout` 调用从 `else`（无输入）分支前置到输入处理之前，确保 poll 每轮都检查挂钟 elapsed，不被连续输入读取饿死。

### 不修改的部分
- `genericTimer` stopwatch 机制：CLI 模式下仍不启动，但不影响功能。`InputTimeoutMs` 仍被 server 模式使用，不改其语义。
- `ConsoleRefreshHandler.RefreshStrings`：不手动消费 `need_settimer`，避免渲染副作用。
- `ConsoleInputHandler.WaitInput` / `PresetTimer`：不修改脚本引擎入口。

## Testing Decisions

### 测试原则
只测外部行为，不测内部实现细节。验证 TINPUT 超时触发的可观察结果，而非 `WaitInputEnteredAt` 字段本身。

### 测试 seam
- **主要 seam**: PTY 端到端测试（`tests/test_cli_scroll.py` 中的 `CliSession`）。spawn CLI 进程，注入 ERB 脚本，读取 PTY 输出验证超时后的可观察行为。这是最高 seam，覆盖完整链路。
- **回归 seam**: `tests/run_all.py` 全量套件，确保不破坏 server 模式的 TINPUT 超时与其他功能。

### 测试用例
1. **test_tinput_timeout_triggers**: TINPUT 超时后 RESULT 取默认值（7），TIMEUP_MARKER 可见。
2. **test_tinput_countdown_decrements**: 倒计时显示随时间递减（非静止）。
3. **test_auto_follow_on_new_output**（恢复）: Scroll Mode 下 TINPUT 超时产生新输出，auto-follow 归零 offset，状态栏消失，新内容可见。
4. **test_scroll_mode_tinput_triggers**: Scroll Mode 下 TINPUT 超时正常触发（覆盖 ADR-0006 Issue 001 验收）。

### Prior art
- `tests/test_tinput_timeout.py`（server 模式 TINPUT 超时测试）——参考其 ERB 脚本与断言模式。
- `tests/test_cli_scroll.py` 中的 `CliSession` / `copy_test_game_with_erb`——PTY spawn 与 ERB 注入基础设施。

### 回归验证
修改后运行 `tests/test_cli_scroll.py`（19 项 PTY 测试）+ `tests/run_all.py`（9 项 server 测试）全量套件，确保无回归。

## Out of Scope

- **Server 模式 TINPUT 超时**：已正常工作（`CancelAfter` 独立计时），不修改。
- **`genericTimer` stopwatch 修复**：不修 `need_settimer` 消费路径，CLI 模式下 stopwatch 仍不启动，但不影响功能。
- **`DisplayTime` 路径的 PresetTimer 副作用**：`PresetTimer` 在 `DisplayTime=true` 时的 `PrintSingleLine` 不处理，test_game 的 TINPUT 不启用 DisplayTime。
- **CLI 与 server 计时机制统一**：不统一两路径，CLI 用挂钟、server 用 CancelAfter，各自独立。
- **CONTEXT.md 完整 glossary 建立**：本次只记录与 TINPUT 修复直接相关的术语，不一次性建立项目全部 glossary。

## Further Notes

### 根因分析（详见 ADR-0007 Background）
1. `ConsoleInputHandler.WaitInput` → `PresetTimer` 设 `need_settimer = true`
2. `need_settimer` 只在 `ConsoleRefreshHandler.RefreshStrings` 消费 → 调 `SetTimer()` 启动 stopwatch
3. `AgentCliProtocol.RunVtLoop` 是 poll 架构，只调 `FlushBuffer()`，**从不调 `RefreshStrings()`**
4. 故 stopwatch 永不启动 → `InputTimeoutMs` 恒为正值 → `HandleTimeout` early-return

### 提交策略
独立 `fix(cli)` 提交，与上一个 `feat(cli)` 提交（ADR-0006 实现）分离，便于回滚与溯源。
