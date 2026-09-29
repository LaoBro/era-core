# PRD: CLI 双写技术债消除（displayLineList delta）

> 对应 [TODO.md](./TODO.md) "CLI 双写技术债消除（PRD-T6，待立项）" 章节、
> ADR-0003。
> 本 PRD 严格遵循 grilling 阶段达成的共识（Q1-Q7）。

## Problem Statement

作为 Emuera.Headless 的维护者，我在 v2 操作序列模型落地后（[PRD-T5](./PRD-T5-富Turn升级.md)）
仍背负 CLI 渲染层与 server 渲染层并行维护的代价：

1. **双写成本**：`ConsolePrintManager.AddDisplayLine` 同时调用 `EmitPrintOps(line)`（写
   `_pendingOps` 给 server）和 `WriteAlignedLine(line)`（写 `_agentBuffer` 给 CLI）。
   两条数据流语义重叠，merge/clearline 路径必须双份维护——`DeleteLine` 要同时发
   `ClearLineOp` 和调 `RemoveLastLineFromAgentBuffer` + 累加 `_pendingEraseRows`。

2. **CLI 专属渲染路径冗余**：`WriteAlignedLine` 与 `FormatLineForTerminal` 是同一段
   对齐+ANSI 渲染逻辑的两个拷贝（仅末尾输出目标不同）。`FullRefresh` 走 `FormatLineForTerminal`，
   `FlushBuffer` 走 `WriteAlignedLine`→`TakeAgentBuffer`——同一行被渲染两次的隐患点。

3. **`IConsoleStateView` 接口虚胖**：接口含 `ConsumeNeedFullRefresh` / `ConsumePendingEraseRows` /
   `AppendToAgentBuffer` 三方法，后两个仅为 `_agentBuffer` 链路服务。未来 P0-1 终端抽象想 mock
   这个接口做单元测试，虚胖的接口会让 mock 负担无谓增大。

4. **`WriteOutput` 误放基类**：`AgentProtocolBase.WriteOutput` 调 `console.AppendToAgentBuffer`，
   把 input echo 这种"非显示数据"塞进 display buffer。echo 本该直接 `Console.Write`——它和
   `displayLineList` 是两个时序层。`WriteOutput` 现在是 CLI-only 行为却放在 server 也继承的
   基类里，调用面被无谓放大。

5. **VT 模式无背景色能力**：`SetBgOp` 已在 v2 落地，但 CLI 路径完全不消费它——VT 终端有
   `ESC48;2;r;g;bm` 能力却没被使用，是 v2 留下的能力红利未兑现。

## Solution

CLI 的 `TerminalRenderer.FlushBuffer` 改为读 `displayLineList` delta（canonical state diff），
**不消费 op 流做渲染**。op 队列仍被排空（避免内存增长），但仅 `ClearOp` 和 `SetBgOp` 被
acted on（它们没有 `displayLineList` 等价物）。`PrintOp` / `NewLineOp` / `ClearLineOp` 的
语义全部由 `displayLineList` delta 捕获。

详见 ADR-0003。术语定义沿用
[CONTEXT.md Operation Sequence Model。

## User Stories

1. 作为 Emuera.Headless 维护者，我希望 `AddDisplayLine` 只写一条数据流
   （`_pendingOps`），`_agentBuffer` 链路彻底删除，这样 merge/clearline 路径只维护一份。

2. 作为 Emuera.Headless 维护者，我希望 `FlushBuffer` 直接读 `displayLineList` delta
   而非 `_agentBuffer`，这样 `WriteAlignedLine` 这条 CLI 专属渲染拷贝可以删除，
   与 `FullRefresh` 共用 `FormatLineForTerminal`。

3. 作为 Emuera.Headless 维护者，我希望 `IConsoleStateView` 瘦身为只剩
   `ConsumeNeedFullRefresh`，这样未来 P0-1 终端抽象时 mock 接口更轻。

4. 作为 Emuera.Headless 维护者，我希望 `WriteOutput` 从 `AgentProtocolBase` 移到
   `AgentCliProtocol`，input echo 改为直接 `Console.Write`，这样 echo 不再污染
   display buffer，且 server 路径不再继承这个无意义的方法。

5. 作为 CLI 用户，我希望 VT 模式渲染背景色（`SETBG` 生效），这样 CLI 的视觉表现
   更接近 winforms 版。

6. 作为测试开发者，我希望 `test_cli_basic.py` 扩展为覆盖 clearline/clear/setbg/merge/
   alignment 路径的 ConPTY smoke test，这样 PRD-T6 的回归有黑盒保障。

7. 作为未来读者，我希望有 ADR-0003 记录"为何 CLI 用 displayLineList delta 而非
   op 消费"、"为何 `_agentBuffer` 链路被删除而非迁移到 op 消费"——这些决策点对应
   ADR-0003。

8. 作为未来 P0-1 实施者，我希望 `FlushBuffer` 已是稳定的 `displayLineList` delta
   结构，这样提取 `ITerminalOutput` 是机械替换，不会再次重写渲染逻辑。

## Implementation Decisions

### 范围决策

- **本 PRD 范围**：消除 `_agentBuffer` 双写链路 + `FlushBuffer` 改用 displayLineList
  delta + `WriteOutput` 迁移 + VT 模式获得 `set_bg` 能力 + ConPTY smoke test 扩展。
- **明确排除 `ITerminalOutput` 提取**：Q2 决定不做单元测试 seam。`FlushBuffer` 内
  仍直接调 `Console.Write` / `AgentCliVtScreen`，留给 P0-1 时再抽接口。理由：当前
  无单元测试需求，提取接口是无的放矢的预先抽象。
- **明确排除 `LineNo` 改 `long`**：Q3 决定接受 `int.MaxValue` wraparound 风险。
  `AddDisplayLine` 内已有 wraparound 处理（`if (_state.lineNo == int.MaxValue)`），
  失步后 `FullRefresh` 可恢复——`FullRefresh` 会重置 `_lastRenderedLineNo`。
  改 `long` 涉及 `ConsoleStateData.lineNo` 字段类型变更 + `ConsoleDisplayLine.LineNo`
  类型变更，影响面远大于本 PRD 价值。

### displayLineList delta 机制

`TerminalRenderer` 持有两个新字段：

```csharp
internal sealed class TerminalRenderer
{
    private int _lastRenderedLineNo = -1;          // -1 表示"未渲染过"或"需 FullRefresh"
    private ConsoleDisplayLine? _lastRenderedLastLine;  // reference，用于 merge 检测
    // ...
}
```

#### `FlushBuffer` 流程

```csharp
internal void FlushBuffer()
{
    // 1. 排空 _pendingOps：仅 ClearOp / SetBgOp 动作，其余 drain
    var ops = _console.TakePendingOpsForCli();  // 新方法：取出并清空（CLI 专用）
    bool cleared = false;
    foreach (var op in ops)
    {
        switch (op)
        {
            case ClearOp:
                Console.Clear();  // 或 _cursor.ClearScreen() + screen.ClearScreen()
                _lastRenderedLineNo = -1;
                _lastRenderedLastLine = null;
                cleared = true;
                break;
            case SetBgOp bg:
                if (_getScreen() != null && _ansiEnabled)
                    Console.Write($"\x1b[48;2;{ParseR(bg.color)};{ParseG(bg.color)};{ParseB(bg.color)}m");
                break;
            default:
                break;  // PrintOp / NewLineOp / ClearLineOp 由 displayLineList delta 捕获
        }
    }
    if (cleared) return;  // ClearOp 后 displayLineList 也被清，无 delta 可写

    // 2. displayLineList delta 计算
    var lines = _console.DisplayLineList;
    if (lines.Count == 0)
    {
        _lastRenderedLineNo = -1;
        _lastRenderedLastLine = null;
        return;
    }

    var lastLine = lines[^1];
    int currentLineNo = lastLine.LineNo;

    if (_lastRenderedLineNo < 0)
    {
        // 首次渲染或 ClearOp 后：FullRefresh 接管
        // （理论上应被 NeedFullRefresh 标记触发，这里是兜底）
        FullRefresh();
        return;
    }

    if (currentLineNo > _lastRenderedLineNo)
    {
        // 新增行（最常见路径）：写新行
        // 注意：可能跨越多行（极少见，如 AddRangeDisplayLine 后只调一次 FlushBuffer）
        WriteNewLinesSince(lines, _lastRenderedLineNo, _lastRenderedLastLine);
    }
    else if (currentLineNo < _lastRenderedLineNo)
    {
        // ClearLine 删除：擦除 delta 行
        int delta = _lastRenderedLineNo - currentLineNo;
        EraseTerminalRows(delta);
        // 删除后 lastLine 是新的尾行，若与 _lastRenderedLastLine 不同对象则需重写
        if (!ReferenceEquals(_lastRenderedLastLine, lastLine))
        {
            EraseTerminalRows(1);
            WriteDisplayLine(lastLine);
        }
    }
    else
    {
        // LineNo 相等：检查 merge（不同对象）或 temporary 替换
        if (!ReferenceEquals(_lastRenderedLastLine, lastLine))
        {
            EraseTerminalRows(1);
            WriteDisplayLine(lastLine);
        }
        // 否则无变化，不输出
    }

    _lastRenderedLineNo = currentLineNo;
    _lastRenderedLastLine = lastLine;
}
```

#### `WriteDisplayLine` / `WriteNewLinesSince`

```csharp
private void WriteDisplayLine(ConsoleDisplayLine line)
{
    string text = _console.FormatLineForTerminal(line);
    if (line.IsLineEnd)
        Console.WriteLine(text);
    else
        Console.Write(text);
}

private void WriteNewLinesSince(
    List<ConsoleDisplayLine> lines,
    int lastRenderedLineNo,
    ConsoleDisplayLine? lastRenderedLastLine)
{
    // 从 lastRenderedLineNo 对应的行之后开始写到末尾
    // 找到第一个 LineNo > lastRenderedLineNo 的索引
    int startIdx = -1;
    for (int i = lines.Count - 1; i >= 0; i--)
    {
        if (lines[i].LineNo <= lastRenderedLineNo)
        {
            startIdx = i + 1;
            break;
        }
    }
    if (startIdx < 0) startIdx = 0;  // 全部是新行

    // 若上一渲染的尾行 IsLineEnd==false（行未结束），新行需 merge 到当前行
    // —— 但 merge 已由 AddDisplayLine 在 displayLineList 层完成（line.ChangeStr），
    //    且 _lastRenderedLastLine 已被 ReplaceLastLineFromMerge 处理。
    //    实际上：若 lastRenderedLastLine.IsLineEnd==false，说明上次写的是无换行的
    //    增量，现在 displayLineList[^1] 是 merge 后的新行，需要先擦尾行再重写。
    if (lastRenderedLastLine != null
        && !lastRenderedLastLine.IsLineEnd
        && startIdx > 0)
    {
        EraseTerminalRows(1);
        startIdx--;  // 重写上一行（已被 merge 替换）
    }

    for (int i = startIdx; i < lines.Count; i++)
        WriteDisplayLine(lines[i]);
}
```

#### `EraseTerminalRows` 重构

`EraseTerminalRows` 现有签名（无参，读 `ConsumePendingEraseRows`）改为接受参数：

```csharp
internal void EraseTerminalRows(int rows)
{
    if (rows <= 0) return;
    // ... 现有 VT / 非 VT 擦除逻辑不变，rows 来源从 console 改为参数 ...
}
```

#### `FullRefresh` 末尾同步

**关键约束**：`FullRefresh` 末尾必须同步 `_lastRenderedLineNo` / `_lastRenderedLastLine`，
否则后续 `FlushBuffer` delta 全错：

```csharp
internal void FullRefresh()
{
    var lines = _console.DisplayLineList;
    var screen = _getScreen();

    // ... 现有渲染逻辑不变 ...

    // === 新增：同步 delta tracking state ===
    if (lines.Count > 0)
    {
        _lastRenderedLineNo = lines[^1].LineNo;
        _lastRenderedLastLine = lines[^1];
    }
    else
    {
        _lastRenderedLineNo = -1;
        _lastRenderedLastLine = null;
    }
}
```

### 配套删除：`_agentBuffer` 全链路

以下代码全部删除：

**`ConsoleStateData`**（[ConsoleStateData.cs L127-L131](../../EraCore.Core/UI/Game/Console/ConsoleStateData.cs#L127-L131)）：
```csharp
// 删除：
internal readonly StringBuilder _agentBuffer = new();
internal int _agentBufferLineCount;
internal int _pendingEraseRows;
// 保留：
internal readonly List<TurnOp> _pendingOps = [];
internal bool _needFullRefresh;
```

**`EmueraConsole`**（[EmueraConsole.cs L441-L497](../../EraCore.Core/UI/Game/EmueraConsole.cs#L441-L497)）：
```csharp
// 删除：
public int ConsumePendingEraseRows() { ... }
public void AppendToAgentBuffer(string text, bool newLine) { ... }
internal void WriteToAgentBuffer(string text) { ... }
internal void WriteToAgentBufferNoNewline(string text) { ... }
internal string TakeAgentBuffer() { ... }
internal bool RemoveLastLineFromAgentBuffer() { ... }
internal void WriteAlignedLine(ConsoleDisplayLine line) { ... }

// 保留：
public bool ConsumeNeedFullRefresh() { ... }
internal List<TurnOp> TakePendingOps() { ... }  // server 用，CLI 不再调
internal string FormatLineForTerminal(ConsoleDisplayLine line) { ... }  // FullRefresh + delta 共用
private static string BuildTerminalLine(...) { ... }  // FormatLineForTerminal 内部用
```

**`ConsolePrintManager.AddDisplayLine`**（[ConsolePrintManager.cs L151](../../EraCore.Core/UI/Game/Console/ConsolePrintManager.cs#L151)）：
```csharp
// 删除这一行：
_console.WriteAlignedLine(line);
```

**`ConsolePrintManager.DeleteLine`**（[ConsolePrintManager.cs L219-L220](../../EraCore.Core/UI/Game/Console/ConsolePrintManager.cs#L219-L220)）：
```csharp
// 删除这两行：
if (!_console.RemoveLastLineFromAgentBuffer())
    _state._pendingEraseRows++;
```

**`IConsoleStateView`**（[IConsoleStateView.cs](../../EraCore.Core/Agent/IConsoleStateView.cs)）：
```csharp
internal interface IConsoleStateView
{
    bool ConsumeNeedFullRefresh();
    // 删除：ConsumePendingEraseRows() / AppendToAgentBuffer(...)
}
```

### `WriteOutput` 迁移：input echo 改直接 `Console.Write`

**`AgentProtocolBase`**（[AgentProtocolBase.cs L50-L53](../../EraCore.Core/Agent/AgentProtocolBase.cs#L50-L53)）：
```csharp
// 删除：
internal virtual void WriteOutput(string text, bool newLine = true)
{
    console.AppendToAgentBuffer(text, newLine);
}
```

**`AgentCliProtocol`** 新增 CLI-only `WriteOutput`：
```csharp
private void WriteOutput(string text, bool newLine = true)
{
    if (newLine) Console.WriteLine(text);
    else Console.Write(text);
}
```

调用点不变（[AgentCliProtocol.cs L351, L362, L436](../../EraCore.Core/Agent/AgentCliProtocol.cs)），
从基类虚方法降级为本类私有方法。`EraseInputLine` / `ProcessChar` 内的 `WriteOutput` 调用
语义保持——echo 与 display line 时序分析见下方"Input echo 时序安全性"。

### Input echo 时序安全性

`ProcessChar` 在 `RunAgentLoop` 的 `Poll` 阶段被调用（VT 路径：`vtInput.Feed` →
`ProcessKeyFromVt` → `ProcessKey` → `ProcessChar`；非 VT 路径：`ProcessKey` → `ProcessChar`）。
`DispatchInput` 在 `ProcessChar('\r')` 内同步执行——会触发游戏运行 + display line 产出。
`FlushBuffer` 在 `Poll` 之后调用。

时序保证：
1. echo（`WriteOutput(ch, false)`）发生在 `ProcessChar` 内，`DispatchInput` 之前。
2. `DispatchInput` 同步运行游戏，display line 在此期间累积到 `displayLineList`。
3. `FlushBuffer` 在 `Poll` 之后读取 `displayLineList` delta。

echo 写的是"当前输入行的字符"，display line 写的是"游戏输出行"。两者在终端物理上
不交错——echo 写在光标位置（输入行），display line 写在新行（`WriteLine` 后光标换行）。
唯一需要小心的是 `EraseInputLine`：它用 `\r` + 空格擦除当前输入行——此时光标在输入行，
不冲突。`FlushBuffer` 写新行时光标已被 `EraseInputLine` 重置或 `ProcessChar('\r')` 的
`EraseInputLine()` 调用重置。

### VT 模式 `set_bg` 实现

VT 模式下 `FlushBuffer` 遇到 `SetBgOp` 时发 VT escape：

```csharp
case SetBgOp bg:
    if (_getScreen() != null && _ansiEnabled)
    {
        // bg.color 格式为 "#RRGGBB"
        string hex = bg.color.TrimStart('#');
        int r = Convert.ToInt32(hex.Substring(0, 2), 16);
        int g = Convert.ToInt32(hex.Substring(2, 2), 16);
        int b = Convert.ToInt32(hex.Substring(4, 2), 16);
        Console.Write($"\x1b[48;2;{r};{g};{b}m");
    }
    break;
```

非 VT 模式忽略 `SetBgOp`（无能力，与现状一致）。VT 备用屏会立即应用背景色到整个屏。
注意：`SETBG` 后续 `print` 的背景色由终端维护，不需要每个 `print` 重发 escape。

### `ClearOp` 处理

`ClearOp` 在 `FlushBuffer` 内触发 `Console.Clear()`（或 VT `_screen.ClearScreen()` +
非 VT `_cursor.ClearScreen()`），并重置 `_lastRenderedLineNo = -1` / `_lastRenderedLastLine = null`。
重置后下次 `FlushBuffer` 会触发 `FullRefresh` 兜底（若 `displayLineList` 已被 `ClearDisplay`
清空，则 lines.Count == 0 路径直接返回）。

`ClearDisplay` 既有实现已清空 `displayLineList` 并设 `_needFullRefresh = true`——
`RunAgentLoop` 下一轮 `ConsumeNeedFullRefresh` 触发 `FullRefresh`。`FlushBuffer` 内的
`ClearOp` 处理是冗余安全网：若 `ClearDisplay` 在 `Poll` 与 `FlushBuffer` 之间被调用，
`displayLineList` 已空，`FlushBuffer` 走 lines.Count == 0 路径，ClearOp 不再需要动作。
但若 `ClearDisplay` 在游戏运行中调用而 `FlushBuffer` 在其后立即被调用（如 countdown
超时路径），ClearOp 处理确保终端立即清屏。

### `TakePendingOpsForCli` 新方法

CLI 不应通过 `TakePendingOps`（server 专用，返回 `List<TurnOp>`）排空队列——这会
让 CLI 与 server 共享同一取出路径，语义混乱。新增 CLI 专用方法：

```csharp
// EmueraConsole 内
internal void DrainPendingOpsForCli(Action<TurnOp> action)
{
    foreach (var op in _state._pendingOps)
        action(op);
    _state._pendingOps.Clear();
}
```

`FlushBuffer` 内：
```csharp
_console.DrainPendingOpsForCli(op =>
{
    switch (op)
    {
        case ClearOp: /* Console.Clear + reset tracking */ break;
        case SetBgOp bg: /* emit VT escape */ break;
        default: break;
    }
});
```

理由：`TakePendingOps` 返回 `List<TurnOp>` 拷贝（`.ToList()`），CLI 不需要这个拷贝——
CLI 只是要 drain 队列并对 `ClearOp` / `SetBgOp` 做动作。callback 形式更直接。

### 涉及文件清单

| 文件 | 改动类型 |
|------|----------|
| `Emuera.Headless/Terminal/TerminalRenderer.cs` | 重写 `FlushBuffer`、重构 `EraseTerminalRows(int)`、`FullRefresh` 末尾同步、新增 `WriteDisplayLine` / `WriteNewLinesSince` |
| `Emuera.Headless/UI/Game/Console/ConsoleStateData.cs` | 删除 `_agentBuffer` / `_agentBufferLineCount` / `_pendingEraseRows` 字段 |
| `Emuera.Headless/UI/Game/Console/ConsolePrintManager.cs` | `AddDisplayLine` 删 `WriteAlignedLine` 调用、`DeleteLine` 删 `RemoveLastLineFromAgentBuffer` + `_pendingEraseRows` 累加 |
| `Emuera.Headless/UI/Game/EmueraConsole.cs` | 删除 `ConsumePendingEraseRows` / `AppendToAgentBuffer` / `WriteToAgentBuffer` / `WriteToAgentBufferNoNewline` / `TakeAgentBuffer` / `RemoveLastLineFromAgentBuffer` / `WriteAlignedLine`；新增 `DrainPendingOpsForCli` |
| `Emuera.Headless/Agent/IConsoleStateView.cs` | 删除 `ConsumePendingEraseRows` / `AppendToAgentBuffer`，瘦身为单方法接口 |
| `Emuera.Headless/Agent/AgentProtocolBase.cs` | 删除 `WriteOutput` 虚方法 |
| `Emuera.Headless/Agent/AgentCliProtocol.cs` | 新增 CLI-only `WriteOutput`（直接 `Console.Write`） |
| `tests/test_cli_basic.py` | 扩展为 ConPTY smoke test 覆盖 clearline/clear/setbg/merge/alignment |

## Testing Decisions

### 测试哲学

延续 [PRD-T3](./PRD-T3-Turn协议版本化.md) / [PRD-T5](./PRD-T5-富Turn升级.md) 的
"不新增测试 seam"原则。Q2 决定不提取 `ITerminalOutput`，故无单元测试 seam——
`TerminalRenderer` 仍直接调 `Console.Write` / `AgentCliVtScreen`，无法 mock。

测试策略：扩展 `test_cli_basic.py` 为 ConPTY smoke test，黑盒覆盖关键路径。

### 测试 Seam

复用 `test_cli_basic.py` 的 pywinpty ConPTY seam——这是覆盖 CLI 渲染的最高且唯一 seam。
**不新增测试 seam。**

### 扩展断言

`test_cli_basic.py` 当前只覆盖 happy path（Hello → World → Exit）。扩展为多个独立
测试函数，每个覆盖一条关键路径：

1. **`test_cli_basic`**（既有，保留）：Hello/World/Exit happy path。
2. **`test_cli_clearline`**：注入 ERB 用 `CLEARLINE 1` 删行，ConPTY 验证屏幕上
   不再出现被删行的内容。夹具：复制 `test_game` 到临时目录，注入 ERB 文件。
3. **`test_cli_clear`**：注入 ERB 用 `CLEAR` 清屏，ConPTY 验证屏幕清空后只显示
   `CLEAR` 之后的输出。
4. **`test_cli_setbg`**：注入 ERB 用 `SETBG` 设背景色，ConPTY 验证 VT escape
   `\x1b[48;2;r;g;bm` 出现在输出字节流中（非 VT 模式下跳过此测试）。
5. **`test_cli_merge`**：注入 ERB 用两次 `Print`（无 `NewLine`）触发行合并，
   ConPTY 验证屏幕上只显示合并后的一行（不重复）。
6. **`test_cli_alignment`**：注入 ERB 用 `PrintC` 触发居中对齐，ConPTY 验证
   屏幕上文本前有前导空格（居中 padding）。

每个测试复制 `test_game` 到临时目录（参考 `test_tinput_timeout.py` 的做法），
注入 ERB，运行 CLI，验证屏幕输出。

### 未覆盖项

- **`LineNo` wraparound**：极罕见场景（`int.MaxValue` 行后回 0），不做测试。
  失步后 `FullRefresh` 可恢复，接受此风险。
- **`FullRefresh` 同步 delta tracking state 的回归**：黑盒测试难以直接验证
  内部状态。依赖 `test_cli_clearline`（ClearLine 后 resize 触发 FullRefresh，
  再 print 新行）间接覆盖——若 `FullRefresh` 忘同步，后续 print 会重复输出。
- **VT 与非 VT 路径差异**：ConPTY 默认是 VT 路径。非 VT 路径（`ConsoleKeyLoopStrategy`）
  难以在 ConPTY 下触发——非 VT 是降级路径，留给手动验证。
- **Input echo 与 display line 时序交错**：黑盒测试难以构造交错场景。时序正确性
  依赖代码审查 + 既有 happy path 测试间接覆盖。

### Prior Art

- [test_cli_basic.py](../../tests/test_cli_basic.py)：既有 ConPTY smoke test，
  扩展为多路径覆盖。
- [test_tinput_timeout.py](../../tests/test_tinput_timeout.py)：复制 `test_game`
  到临时目录注入 ERB 的夹具模式，本次扩展复用此模式。
- [tests/emuera_server.py](../../tests/emuera_server.py)：`find_binary` 工具函数，
  CLI 测试复用。
- [tests/README.md](../../tests/README.md)：测试权威文档，本次扩展应同步更新。

## Rollout

**Big bang 单 PR**（Q5 决定）。理由：双写消除无中间形态——要么保留 `_agentBuffer`，
要么删除。无法分阶段。`TerminalRenderer.FlushBuffer` 重写 + `_agentBuffer` 链路删除
必须同 PR 落地，否则编译失败（`AddDisplayLine` 调 `WriteAlignedLine` 但 `WriteAlignedLine`
已删）。

PR 顺序：
1. 实现 `TerminalRenderer` 重写（`FlushBuffer` / `EraseTerminalRows(int)` / `FullRefresh` 同步）。
2. 删除 `_agentBuffer` 全链路（`ConsoleStateData` / `EmueraConsole` / `ConsolePrintManager` / `IConsoleStateView`）。
3. 迁移 `WriteOutput`（`AgentProtocolBase` → `AgentCliProtocol`）。
4. 扩展 `test_cli_basic.py` 为多路径 ConPTY smoke test。
5. 运行全套回归测试。
6. 同步更新 [TODO.md](./TODO.md) 标记 PRD-T6 完成、[tests/README.md](../../tests/README.md)
   新增测试条目。

## Out of Scope

- **`ITerminalOutput` 接口提取**：留给 P0-1（终端抽象层）。本 PRD 不引入新接口，
  `FlushBuffer` 内仍直接调 `Console.Write` / `AgentCliVtScreen`。
- **`LineNo` 改 `long`**：影响面过大（`ConsoleStateData.lineNo` + `ConsoleDisplayLine.LineNo`
  类型变更 + 所有比较逻辑），且 `int.MaxValue` wraparound 极罕见。接受此风险。
- **非 VT 路径的 `set_bg` 能力**：非 VT 终端无背景色能力，`SetBgOp` 在非 VT 路径
  被忽略。这是能力缺失而非回归——非 VT 模式从未支持背景色。
- **CLI 单元测试**：Q2 决定不提取 `ITerminalOutput`，无 mock 接口。CLI 测试仅靠
  ConPTY smoke test。
- **`AgentCliProtocol` 拆分**：P2-7 任务，独立于本 PRD。
- **op 序列的压缩 / 增量编码**：op 队列仍按 v2 语义排空，不引入 diff / compaction。
- **server 路径调整**：`BuildTurn` / `TakePendingOps` 完全不变。server 是 op 流的
  唯一消费者，CLI 改用 displayLineList delta 不影响 server。

## Further Notes

### 与既有 ADR 的关系

ADR-0003 已在 grilling 阶段落地，
记录"CLI 用 displayLineList delta 而非 op 消费"的核心决策与 3 条被拒绝的替代方案
（逐行缓冲消费 ops / 混合 ops+displayLineList / TODO 原方向"消费 ops"）。
ADR-0002 的"CLI 双写技术债" consequence
条目被 ADR-0003 显式 supersede。

### 与 [PRD-T5](./PRD-T5-富Turn升级.md) 的关系

PRD-T5 落地 v2 操作序列模型时有意保留 `_agentBuffer` 双写（PRD-T5 的"CLI 共存策略"
章节明确标记为技术债，记入 TODO）。本 PRD 是 PRD-T5 留下的债的清偿——不修改 v2 协议、
不修改 `TurnRecord` 结构、不修改 server 路径。仅修改 CLI 渲染层 + 删除 v1 遗留 buffer。

### 与 [架构评估报告.md](./架构评估报告.md) 其他任务的关系

本 PRD 是 [架构评估报告.md](./架构评估报告.md) P0-1（终端抽象层提取）的前置：
- P0-1 需要提取 `ITerminalOutput`，本 PRD 让 `FlushBuffer` 成为稳定的
  `displayLineList` delta 结构，P0-1 时机械替换 `Console.Write` / `AgentCliVtScreen`
  为接口调用即可，不需要再次重写渲染逻辑。
- 本 PRD 让 `IConsoleStateView` 瘦身为单方法接口，P0-1 时 mock 接口负担减轻。

### 实施前验证

实施者应在落地代码后、运行测试前验证：

1. `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 成功，无新增 error
   （`Shared/` 子树的历史警告不在关注范围）。
2. 手动验证 CLI 模式正常工作（`--protocol cli` 运行 `test_game`）——
   happy path 渲染、输入回显、退出无回归。
3. 手动验证 resize 行为：CLI 运行中改变终端窗口尺寸，`FullRefresh` 触发后
   后续 `FlushBuffer` 不重复/丢失行（验证 `_lastRenderedLineNo` 同步正确）。
4. `python tests/test_cli_basic.py --binary <path> --game-dir test_game` 全部通过——
   扩展后的 ConPTY smoke test 覆盖 clearline/clear/setbg/merge/alignment。
5. `python tests/run_all.py --binary <path> --game-dir test_game` 全套通过——
   验证 server 单会话、TINPUT timeout、force quit survival 等场景未因 CLI 改动破坏
   （server 路径不应受影响，但需验证 `_pendingOps` 队列在 CLI 不再调 `TakePendingOps` 后
   仍正常工作）。
6. 同步检查 `test_server_single_session.py` / `test_tinput_timeout.py` 是否依赖
   被 deleted 的 API（`TakeAgentBuffer` / `ConsumePendingEraseRows`），若有则同步更新。

### 已知风险

1. **`LineNo` wraparound**：`int.MaxValue` 回 0（`AddDisplayLine` 内有处理）极罕见，
   但 delta 比较会失步。失步表现为 `FlushBuffer` 误判"新增行"或"删除行"，输出错乱。
   `FullRefresh`（resize 或 NeedFullRefresh 触发）可恢复。本次接受此风险——
   Q3 决定不改 `long`。
2. **`FullRefresh` 忘同步 delta tracking state**：实现时的关键约束。若忘同步，
   `_lastRenderedLineNo` 保持旧值，后续 `FlushBuffer` delta 全错。代码审查 + 
   `test_cli_clearline` 间接覆盖（resize 后 print 验证不重复）。
3. **`WriteNewLinesSince` 的多行跨越**：`AddRangeDisplayLine` 后只调一次 `FlushBuffer`
   时，需写多行。此路径在 `test_game` 中罕见（`PrintFlush` 通常逐行），但 ERB 的
   `PrintHtml` 可能触发。ConPTY smoke test 难以构造此场景，依赖代码审查。
4. **merge case 的 `ReferenceEquals` 检测**：merge 时 `AddDisplayLine` 创建新
   `ConsoleDisplayLine` 对象（`line.ChangeStr([.. lastline.Buttons, .. line.Buttons])`），
   `ReferenceEquals(_lastRenderedLastLine, lastLine)` 为 false 触发重写。若 `ChangeStr`
   实现是 in-place 修改（不创建新对象），`ReferenceEquals` 会误判为"无变化"——
   实施时需验证 `ChangeStr` 的语义（应返回新对象或 in-place 修改后 `LineNo` 不变）。
