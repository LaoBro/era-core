# Issues: 移除 CLI 非 VT 降级路径

**Parent PRD**: [PRD.md](./PRD.md)
**Parent ADR**: ADR-0005
**Triage Label**: `ready-for-agent`

按依赖顺序发布 4 个垂直切片。每个 issue 完成后可独立验证。

---

## Issue 1: VT 初始化失败改为 fatal 退出

### What to build

引入 `HeadlessFatalException` 异常类型，表示 CLI 协议层不可恢复的环境问题。
`AgentCliProtocol.RunCliLoop` 在 `TryPrepareVtInput()` 返回 false 时抛出此异常
（而非走降级路径）。`HeadlessRunner.RunAsync` 捕获后：写 stderr 提示（原因 +
解决方案）+ 写 `AgentLog`，然后 `Environment.Exit(1)`。

此步**不删除降级代码**——`ConsoleKeyLoopStrategy` 和降级渲染分支保留，仅变为
不可达。错误模型先于代码清理落地。

新增 stdin 重定向测试：用 `subprocess.Popen` + `stdin=PIPE` 构造 stdin 重定向
环境，运行 `--protocol cli`，验证非零退出码 + stderr 含错误提示。

### Acceptance criteria

- [ ] 新增 `HeadlessFatalException` 异常类型
- [ ] `RunCliLoop` 在 `TryPrepareVtInput()` 返回 false 时抛 `HeadlessFatalException`
- [ ] `HeadlessRunner.RunAsync` 捕获 `HeadlessFatalException`，输出 stderr 提示
      （含原因 + 解决方案建议）
- [ ] `HeadlessRunner.RunAsync` 写 `AgentLog`
- [ ] `HeadlessRunner.RunAsync` 以非零退出码退出
- [ ] 新增 stdin 重定向测试，验证 `--protocol cli` + stdin 重定向 → 非零退出码
- [ ] 现有 ConPTY 测试（`tests/run_all.py`）全部通过
- [ ] 降级路径代码保留但变为不可达（不删除）

### Blocked by

None — 可立即开始

### User stories covered

1, 2, 3, 4, 5（错误体验），23（stdin 重定向测试）

---

## Issue 2: 删除 LoopStrategy 抽象，主循环内联

### What to build

删除 `LoopStrategy` 抽象基类、`VtLoopStrategy`、`ConsoleKeyLoopStrategy` 三个
策略类。VT 主循环逻辑直接内联到 `RunCliLoop`，包括：VT 输入读取、超时检查、
countdown 更新、resize 检测、按钮区域同步、末尾刷新。

`HandleTimeout` 移除 `VtInputHandler? vtInput` 可空参数，直接用实例字段
`_vtInput`（VT-only 下必非 null）。

此步是纯代码重构，CLI 在 VT 路径下行为不变。

### Acceptance criteria

- [ ] 删除 `LoopStrategy` 抽象基类
- [ ] 删除 `VtLoopStrategy` 类
- [ ] 删除 `ConsoleKeyLoopStrategy` 类
- [ ] `RunCliLoop` 内联完整 VT 主循环逻辑
- [ ] `HandleTimeout` 移除可空参数，直接用 `_vtInput`
- [ ] `RunCliLoop` 的 `catch (Exception ex)` 块保留（脚本运行期异常仍走 fatal）
- [ ] 现有 ConPTY 测试（`tests/run_all.py`）全部通过
- [ ] VT 路径下 CLI 行为不变（备用屏、鼠标、渲染均正常）

### Blocked by

- Issue 1（需要 fatal 路径就位，删除降级策略后才不会破坏错误处理）

### User stories covered

13（LoopStrategy 删除），16（HandleTimeout 简化）

---

## Issue 3: 删除 ANSI 字段分支 + TerminalCursor 简化

### What to build

删除所有 ANSI 字段及非 ANSI 分支：

- `TerminalCursor._ansi` 字段删除，`Set`/`ClearLine`/`SaveAnsi`/`RestoreAnsi`
  只保留 ANSI 路径
- `TerminalCursor.ClearScreen` 的 `Console.Clear()` + `===` 分隔线 fallback 删除
- `_ansiEnabled` 字段从 `AgentCliProtocol`/`TerminalRenderer`/`CountdownRenderer`/
  `ButtonSelectionMode` 全部删除，调用方直接假设 ANSI 可用
- `TerminalLineFormatter.FormatLineForTerminal` 的 `ansiEnabled` 参数**保留**
  （它是输出格式开关，不属于路径判定），调用方传 `true`
- 删除 `NullTerminalSetup.cs`（未被 `Program.CreateTerminalSetup` 使用的死代码）

此步是纯代码清理，CLI 在 VT 路径下行为不变。

### Acceptance criteria

- [ ] `TerminalCursor._ansi` 字段及其分支删除
- [ ] `TerminalCursor.ClearScreen` 的 `Console.Clear()` + `===` fallback 删除
- [ ] `_ansiEnabled` 字段从 `AgentCliProtocol` 删除
- [ ] `_ansiEnabled` 字段从 `TerminalRenderer` 删除
- [ ] `_ansiEnabled` 字段从 `CountdownRenderer` 删除
- [ ] `_ansiEnabled` 字段从 `ButtonSelectionMode` 删除
- [ ] `TerminalLineFormatter.FormatLineForTerminal` 的 `ansiEnabled` 参数保留
- [ ] `NullTerminalSetup.cs` 删除
- [ ] 现有 ConPTY 测试（`tests/run_all.py`）全部通过
- [ ] VT 路径下 CLI 行为不变

### Blocked by

- Issue 2（主循环已内联，ANSI 清理更安全）

### User stories covered

12（_ansiEnabled 删除），17（NullTerminalSetup 删除），30（FormatLineForTerminal
参数保留）

---

## Issue 4: 删除降级渲染分支 + 测试收紧

### What to build

删除所有降级渲染分支（`_screen == null` / `vtInput == null` 检查）：

- `TerminalRenderer` 的 4 处 `_screen == null` 分支删除：`ClearOp`、`SetBgOp`、
  `FullRefresh`、`EraseTerminalRows`
- `CountdownRenderer.Overwrite` 的 else 分支删除
- `ButtonSelectionMode` 的 5+ 处 `_getScreen() != null` / `vtInput == null`
  检查删除
- `_screen` 在 VT 路径下必非 null，相关 null 检查移除

更新 `test_cli_basic.py`：

- `detect_vt_path` 保留，但语义改变：
  - 探测到 `VT_PATH_MARKER` → 正常（现有断言）
  - 探测到 `FALLBACK_PATH_MARKER` → **FAIL**（不再是 WARN）
  - 未识别到路径标志 → **FAIL**（日志缺失视为回归）
- `warn`/`warned` 机制保留用于 ConPTY 24-bit color SGR 限制场景

### Acceptance criteria

- [ ] `TerminalRenderer.FlushBuffer` 的 ClearOp 分支删除 `_screen == null` 检查
- [ ] `TerminalRenderer.FlushBuffer` 的 SetBgOp 分支删除 `_screen == null` 检查
- [ ] `TerminalRenderer.FullRefresh` 删除 `_screen == null` 分支
- [ ] `TerminalRenderer.EraseTerminalRows` 删除 `_screen == null` 分支
- [ ] `CountdownRenderer.Overwrite` 删除 else 分支
- [ ] `ButtonSelectionMode` 删除 5+ 处 `_getScreen() != null` / `vtInput == null`
      检查
- [ ] `test_cli_basic.py` 中 `detect_vt_path` 探测到降级标记时 FAIL
- [ ] `test_cli_basic.py` 中 `detect_vt_path` 未识别到标记时 FAIL
- [ ] `warn`/`warned` 机制保留用于 ConPTY 24-bit color SGR 限制场景
- [ ] 现有 ConPTY 测试（`tests/run_all.py`）全部通过
- [ ] VT 路径下 CLI 行为不变

### Blocked by

- Issue 3（ANSI 字段已清理，渲染分支删除更干净）

### User stories covered

14（TerminalRenderer 分支），15（ButtonSelectionMode 分支），22（降级 = FAIL），
24（warn 保留），25（只测外部行为）

---

## 依赖关系图

```
Issue 1 (fatal 退出)
    ↓
Issue 2 (删 LoopStrategy)
    ↓
Issue 3 (删 ANSI 分支)
    ↓
Issue 4 (删渲染分支 + 测试)
```

## 发布到 GitHub Issues

GitHub auth token 当前失效。恢复后可用以下命令发布：

```bash
gh auth refresh -h github.com
# 然后对每个 issue 执行：
gh issue create --title "Issue N: ..." --body-file docs/2026.7.7.remove-non-vt-path/issues.md --label ready-for-agent
```

或逐个 issue 拆分为独立文件后发布。
