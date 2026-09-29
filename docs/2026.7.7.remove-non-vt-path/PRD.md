# PRD: 移除 CLI 非 VT 降级路径

**Triage Label**: `ready-for-agent`
**相关 ADR**: ADR-0005 移除非 VT 路径
**相关上下文**: CONTEXT.md CLI Terminal Path

## Problem Statement

作为 Emuera.Headless 的开发者在向 CLI 协议层添加鼠标滚轮滚动功能时，发现当前
CLI 协议层维护着两条渲染路径——VT 路径与非 VT 降级路径。降级路径在当前架构下
几乎是死代码（入口已被 `DetectProtocol` 堵住、.NET 10 + Windows 10 1809+ 保证
VT 可用），却在 6 个文件中留下约 150-200 行分支代码 + 20+ 处 null 检查，
且零测试覆盖。

即将添加的鼠标滚轮功能依赖 VT 绝对定位、SGR 鼠标事件、备用屏视口缓冲——这些
能力非 VT 路径完全无法实现。继续维护降级路径会导致：每加一个 VT 能力都要在
非 VT 路径写"不支持"分支或退化实现；架构分叉从"能力差异"演变为"架构分叉"；
测试覆盖持续空白；用户遇到 VT 失败时得到的是静默降级到残缺体验（无鼠标、无
背景色、无对齐），而非明确错误提示。

## Solution

从用户视角，移除非 VT 降级路径后：

- **VT 初始化失败时明确报错**：用户在不受支持的终端环境下运行 `--protocol cli`
  时，会看到清晰的错误提示（原因 + 解决方案），进程以非零退出码退出。不再是
  静默降级到残缺体验。
- **现代终端用户体验不变**：在 Windows Terminal / ConPTY / 现代 SSH 等支持
  VT 的终端下，用户体验完全不变——备用屏、SGR 鼠标、24-bit 色仍正常工作。
- **未来鼠标滚轮等功能更快落地**：开发者无需为每个新 VT 能力维护降级分支，
  鼠标滚轮等功能能更早交付。

## User Stories

### 错误体验

1. 作为 CLI 用户，我在不支持 VT 的终端下运行 `--protocol cli` 时，希望看到
   明确的错误提示告诉我原因和解决方案，而不是静默降级到残缺体验。
2. 作为 CLI 用户，我希望 VT 初始化失败时进程以非零退出码退出，以便我的
   脚本能检测到失败并采取相应行动（如切换到 `--server` 模式）。
3. 作为 CLI 用户，我希望错误提示包含具体原因（如"ANSI 未启用"或"stdin 被重定向"），
   以便我诊断环境问题。
4. 作为 CLI 用户，我希望错误提示包含解决方案建议（如"请在真实交互式终端运行"），
   以便我知道下一步该怎么做。
5. 作为 CLI 用户，我希望错误信息同时写到 stderr 和 AgentLog，以便终端用户
   和开发者调试都能获得信息。

### 现代终端用户体验

6. 作为 Windows Terminal 用户，我运行 `--protocol cli` 时希望进入备用屏、
   支持鼠标点击、24-bit 背景色，体验与移除降级路径前完全一致。
7. 作为 ConPTY 自动化测试运行者，我希望 CLI 在 ConPTY 下仍能正常工作，
   所有现有测试持续通过。
8. 作为现代 SSH 客户端用户，我希望在远程会话中使用 CLI 时 VT 能力正常启用。
9. 作为 CLI 用户，我希望 SETBGCOLOR 仍然在 VT 路径下发出 `ESC48;2;R;G;Bm`
   转义序列。
10. 作为 CLI 用户，我希望 CLEARLINE、PRINTN 合并、PRINTC 对齐等渲染行为
    与移除降级路径前一致。

### 开发者体验

11. 作为开发者，我希望移除降级路径后 CLI 协议层代码净减约 200-250 行，
    降低维护成本。
12. 作为开发者，我希望 `_ansiEnabled` / `_ansi` 字段及其分支从 6 个文件中
    消失，调用方直接假设 ANSI 可用。
13. 作为开发者，我希望 `LoopStrategy` 抽象基类、`VtLoopStrategy`、
    `ConsoleKeyLoopStrategy` 三个策略类被删除，主循环逻辑直接内联到
    `RunCliLoop`。
14. 作为开发者，我希望 `TerminalRenderer` 的 4 处 `_screen == null` 分支
    消失，`_screen` 在 VT 路径下必非 null。
15. 作为开发者，我希望 `ButtonSelectionMode` 的 5+ 处 `_getScreen() != null`
    / `vtInput == null` 检查消失。
16. 作为开发者，我希望 `HandleTimeout` 不再需要 `VtInputHandler? vtInput`
    可空参数。
17. 作为开发者，我希望 `NullTerminalSetup.cs` 死代码被删除。
18. 作为开发者，我希望 VT 初始化失败的错误模型结构化（`HeadlessFatalException`），
    便于未来扩展到 Server 模式。
19. 作为开发者，我希望本次清理为未来鼠标滚轮功能扫清道路——单路径实现，
    无需维护降级分支。
20. 作为开发者，我希望本次清理**不预留**任何鼠标滚轮接口——避免预期式抽象，
    未来需求明确时再设计。

### 测试

21. 作为测试维护者，我希望现有 ConPTY 测试（test_cli_basic.py）继续通过，
    VT 路径行为不受影响。
22. 作为测试维护者，我希望 test_cli_basic.py 中的 `detect_vt_path` 保留，
    但探测到降级路径标记时视为 FAIL（而非 WARN）——CI 能捕获 VT 初始化回归。
23. 作为测试维护者，我希望新增一个 stdin 重定向测试，验证 `--protocol cli`
    在 stdin 重定向时以非零退出码退出。
24. 作为测试维护者，我希望 `warn`/`warned` 机制保留用于 ConPTY 24-bit color
    SGR 限制场景（这是环境限制，非产品 bug）。
25. 作为测试维护者，我希望测试只验证外部行为（终端输出、退出码、stderr 提示），
    不验证内部实现细节。

### 兼容性

26. 作为 Server 模式用户，我希望 Server 模式完全不受本次改动影响——不走
    `AgentCliProtocol`，错误处理仍走原 fatal turn 逻辑。
27. 作为 `--protocol auto` 用户，我希望 `DetectProtocol` 的 stdin 重定向检查
    保留，入口仍能拒绝重定向环境。
28. 作为跨平台用户，我希望 `WindowsTerminalInput`/`PosixTerminalInput` 的
    raw 模式设置保留，不受本次改动影响。
29. 作为防御性编程受益者，我希望 `TerminalCursor.TryWrite`/`TrySetCursorPosition`
    的 try-catch 保留（不属于降级路径，是防御性代码）。
30. 作为未来 P0-1 终端抽象的承接者，我希望 `TerminalLineFormatter.FormatLineForTerminal`
    的 `ansiEnabled` 参数保留（它是输出格式开关，不属于路径判定）。

## Implementation Decisions

### 错误模型

- 引入新异常类型 `HeadlessFatalException`，表示 CLI 协议层不可恢复的环境问题
- `RunCliLoop` 在 `TryPrepareVtInput()` 返回 false 时抛出
- `HeadlessRunner.RunAsync` 捕获后：写 stderr 提示（原因 + 解决方案）+ 写
  `AgentLog`，然后 `Environment.Exit(1)`
- 不传播到 `Program.Main`——错误处理集中在 `HeadlessRunner` 层
- Server 模式理论上也可使用此异常类型统一错误模型，但本次不改造 Server 路径

### 主循环重写

- 删除 `LoopStrategy` 抽象基类、`VtLoopStrategy`、`ConsoleKeyLoopStrategy`
- 主循环逻辑直接内联到 `RunCliLoop`，包括：
  - VT 输入读取（`_vtInput.ReadByte` + `Feed`）
  - 超时检查（`HandleTimeout`）
  - countdown 更新
  - resize 检测
  - 按钮区域同步（`RefreshButtonRegions`）
  - 末尾刷新（`FlushBuffer` + `SyncButtonState`）
- `HandleTimeout` 移除 `VtInputHandler? vtInput` 参数，直接用实例字段 `_vtInput`
  （VT-only 下必非 null）

### ANSI 字段清理

- `TerminalCursor._ansi` 字段删除，`Set`/`ClearLine`/`SaveAnsi`/`RestoreAnsi`
  只保留 ANSI 路径
- `TerminalCursor.ClearScreen` 的 `Console.Clear()` + `===` 分隔线 fallback 删除
- `_ansiEnabled` 字段从 `AgentCliProtocol`/`TerminalRenderer`/`CountdownRenderer`/
  `ButtonSelectionMode` 全部删除
- `TerminalLineFormatter.FormatLineForTerminal` 的 `ansiEnabled` 参数保留
  （输出格式开关，调用方传 `true`）

### 降级渲染分支删除

- `TerminalRenderer` 的 4 处 `_screen == null` 分支删除：
  `ClearOp`、`SetBgOp`、`FullRefresh`、`EraseTerminalRows`
- `CountdownRenderer.Overwrite` 的 else 分支删除
- `ButtonSelectionMode` 的 5+ 处 `_getScreen() != null` / `vtInput == null`
  检查删除
- `_screen` 在 VT 路径下必非 null，相关 null 检查移除

### 死代码删除

- `NullTerminalSetup.cs` 删除（未被 `Program.CreateTerminalSetup` 使用）

### 保留项

- `Console.IsInputRedirected` 检查保留（入口保护 + Win32 句柄保护）
- `HeadlessRunner.DetectProtocol` 的 stdin 重定向检查保留
- `WindowsTerminalInput`/`PosixTerminalInput` 的 raw 模式设置保留
- `TerminalCursor.TryWrite`/`TrySetCursorPosition` 的 try-catch 保留
- `TerminalLineFormatter.FormatLineForTerminal` 的 `ansiEnabled` 参数保留

### 鼠标滚轮功能预留

本次清理**不预留**任何鼠标滚轮接口。未来实现时：

- 视口滚动状态由 `AgentCliVtScreen` 持有（与 `_inAltScreen` 一起）
- 滚轮事件解析在 `VtInputHandler` 添加
- 滚轮后按钮命中区重算复用现有 `ButtonSelectionMode.RefreshButtonRegions`
- `displayLineList` MaxLog 保持现状，滚动仅改变视口偏移

避免预期式抽象——未来需求明确时再设计具体接口。

### 提交粒度

分 4 步小提交：

1. 引入 `HeadlessFatalException` + fatal 退出路径（不删降级代码，仅改错误模型）
2. 删 `ConsoleKeyLoopStrategy` + `LoopStrategy` 抽象，重写主循环
3. 删 `_ansi`/`_ansiEnabled` 分支 + `TerminalCursor` 简化 + 删 `NullTerminalSetup.cs`
4. 更新测试 + 清理降级渲染分支

## Testing Decisions

### 测试原则

- 只测试外部行为（终端输出、退出码、stderr 提示），不测试内部实现细节
- 优先复用现有 seam，不新增单元测试（项目 Q2 决定不提取 `ITerminalOutput` 接口）
- 黑盒端到端测试优先于白盒测试

### 测试 Seam

**复用 `tests/test_cli_basic.py` 的 ConPTY 黑盒测试**（最高 seam）：

- 现有 ConPTY 测试继续验证 VT 路径行为（SETBGCOLOR 转义、CLEARLINE、PRINTN 合并、
  PRINTC 对齐、清屏等）
- `detect_vt_path` 路径探测保留，但语义改变：
  - 探测到 `VT_PATH_MARKER` → 正常（现有断言）
  - 探测到 `FALLBACK_PATH_MARKER` → **FAIL**（不再是 WARN）
  - 未识别到路径标志 → **FAIL**（日志缺失视为回归）
- `warn`/`warned` 机制保留用于 ConPTY 24-bit color SGR 限制场景
  （`SETBGCOLOR` 转义被 ConPTY 消费，无法捕获——这是环境限制，非产品 bug）

**新增 stdin 重定向测试**（`tests/test_vt_only.py` 或扩展 `test_cli_basic.py`）：

- 用 `subprocess.Popen` + `stdin=PIPE` 构造 stdin 重定向环境
- 运行 `--protocol cli`，验证：
  - 进程以非零退出码退出
  - stderr 包含 `HeadlessFatalException` 相关提示
  - stderr 包含原因说明（"ANSI 未启用"或"stdin 被重定向"）
  - stderr 包含解决方案建议

### 受测模块

- `AgentCliProtocol.RunCliLoop`（VT 初始化失败路径）
- `HeadlessRunner.RunAsync`（`HeadlessFatalException` 捕获 + 退出）
- `TerminalRenderer`（VT 路径渲染行为不变）
- `TerminalCursor`（ANSI 路径行为不变）
- `ButtonSelectionMode`（VT 鼠标命中区行为不变）

### Prior Art

- `tests/test_cli_basic.py` 的 ConPTY 黑盒测试模式（`PtyProcess.spawn` + 路径探测）
- `tests/test_force_quit_survival.py` 的非零退出码断言模式
- `tests/test_server_single_session.py` 的 stderr 捕获模式

## Out of Scope

- **鼠标滚轮滚动功能实现**：本次仅为扫清道路，不实现滚轮功能本身
- **Server 模式错误模型统一**：`HeadlessFatalException` 理论上可扩展到 Server
  路径，但本次不改造
- **`displayLineList` MaxLog 调整**：保持现状，滚轮范围限制问题留给未来
- **`TerminalLineFormatter` 接口提取**：Q2 决定不提取 `ITerminalOutput`，本次
  不引入新抽象
- **旧文档同步更新**：`PRD-T6-CLI双写消除.md` 和 `tests/README.md` 中关于
  "非 VT 路径"的描述不修改，新 ADR-0005 取代这些表述
- **POSIX 路径单独验证**：`PosixTerminalSetup` 的 `tcgetattr` 失败路径理论
  上会触发 `HeadlessFatalException`，但本次不在 POSIX 环境下验证
- **`--protocol jsonl` 恢复**：T-024 已废弃，本次不涉及

## Further Notes

### 相关 ADR

- [ADR-0005 移除非 VT 路径 —— 本次 PRD 的
  架构决策记录
- ADR-0003 CLI 渲染层消除双写
  —— 确立了 CLI 读 `displayLineList` delta 的方向，本次移除降级路径是该方向
  的延续
- ADR-0004 EmueraConsole 门面收缩
  —— 确立了"先收缩再评估接口"的原则，本次遵循该原则不引入新接口

### 风险与缓解

- **风险**：移除降级路径后，某些边缘环境（如 Windows 10 1809 前的旧终端）
  无法运行 CLI
  **缓解**：.NET 10 本身要求 Windows 10 1809+，`ENABLE_VIRTUAL_TERMINAL_PROCESSING`
  自 1709 起可用，实际无损失
- **风险**：未来鼠标滚轮实现时需要重新设计视口状态管理
  **缓解**：ADR-0005 已记录方向（`AgentCliVtScreen` 持有视口状态），实现时
  按此方向设计即可
- **风险**：`HeadlessFatalException` 引入后 Server 模式错误处理出现两套模型
  **缓解**：本次明确标注"理论上可统一但本次不改"，未来可整合

### 成功标准

- 所有现有测试通过（`tests/run_all.py`）
- 新增 stdin 重定向测试通过
- CLI 协议层代码净减约 200-250 行
- 6 个文件显著简化（`AgentCliProtocol`、`TerminalRenderer`、`TerminalCursor`、
  `CountdownRenderer`、`ButtonSelectionMode`、`HeadlessRunner`）
- VT 初始化失败时用户看到明确错误提示 + 非零退出码
