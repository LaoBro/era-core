# PRD: 富 Turn 升级（v2 操作序列模型）

> 对应 [TODO.md](./TODO.md) "富 turn 升级整体推迟" 章节、
> ADR-0002。
> 本 PRD 严格遵循 grilling 阶段达成的共识。

## Problem Statement

作为 Emuera.Headless 的未来跨平台 Web 前端开发者，我对接 v1 turn 协议时无法
还原 Emuera 的完整显示语义：

1. **per-segment 样式丢失**：v1 `text` 是纯字符串，`TakeAgentBuffer()` 把
   `ConsoleButtonString.strArray[]` 中每个 segment 的 `StringStyle`（color /
   bold / italic / fontname）全部丢弃。前端无法还原彩色、粗体、斜体排版。
2. **行级对齐丢失**：`ConsoleDisplayLine.align`（LEFT / CENTER / RIGHT）不进入
   turn，前端无法还原 `PrintC` / `PrintButtonC` 的居中/右对齐。
3. **行编辑不可表达**：`CLEARLINE` 删 N 行、`ReuseLastLine`、临时行替换、
   `PrintFlush` 后的行合并——这些操作在 v1 通过 `RemoveLastLineFromAgentBuffer`
   （字符串级 hack）和 `_pendingEraseRows` 间接表达，前端无法精确还原显示状态。
4. **按钮区域丢失**：`ConsoleButtonString.PointX` / `Width` 不进入 turn，前端
   无法做鼠标命中测试（此项 deferred 到 PRD-T6，不在本 PRD 范围）。

v1 turn 的根本问题是**快照语义**——`text` 是增量字符串、`buttons` 是当前快照，
两者混合且都无法表达行编辑历史。Web 前端需要的是"像终端一样可向上滚动看完整
历史、又能被服务端结构化操控"的模型。

## Solution

v2 turn 删除 `text` 与 `buttons` 顶层字段，新增 `ops[]` 数组——**操作序列模型**。
服务端在 `ConsolePrintManager` 的 `AddDisplayLine` / `DeleteLine` / `ClearDisplay`
等方法中 emit 结构化 op 到 `ConsoleStateData._pendingOps` 队列；`BuildTurn` 序列化
队列并清空。前端按序 apply ops 维护 Display State，行为类似扩展功能的终端。

`protocolVersion` 升到 `2`。**不保留 v1 字段作降级**——v2 是干净替换。

op 种类与 wire format 见下文"Op Schema"章节。术语定义见
CONTEXT.md Operation Sequence Model。

## User Stories

1. 作为 Web 前端开发者，我希望 turn 携带 `ops[]` 而非 `text`，这样我能还原
   per-segment 样式（color / bold / italic / fontname）。
2. 作为 Web 前端开发者，我希望 `newline` op 携带 `align` 字段，这样我能还原
   居中/右对齐排版。
3. 作为 Web 前端开发者，我希望 `clearline` op 携带 `n` 字段，这样我能精确还原
   `CLEARLINE` 删除 N 行的语义，而非靠字符串 hack。
4. 作为 Web 前端开发者，我希望 `print` op 的 `button` 字段内联按钮值，这样按钮
   与显示文本的绑定关系明确，无需另查 `buttons[]` 列表匹配。
5. 作为 Web 前端开发者，我希望 initial turn 的 `ops[]` 包含从会话开始到第一个
   WaitInput 的完整 op 序列，这样我从空 Display State apply 后得到正确初始显示。
6. 作为 Web 前端开发者，我希望 turn 边界隐式标记前一代按钮过期，这样我无需
   手动管理 Generation——当前 turn 的 `print.button` 即可点击，历史 print 中的
   button 仅展示不可点击。
7. 作为 Emuera.Headless 维护者，我希望 op emit 集中在 `AddDisplayLine` 层而非
   `Print` 层，这样 `PrintFlush` 完成词法换行后的正确行结构能被捕获。
8. 作为 Emuera.Headless 维护者，我希望行合并时用 `suppressOp: true` 抑制
   spurious `clearline` op，这样前端不会看到"删除一个它从未见过的行"的噪音。
9. 作为 Emuera.Headless 维护者，我希望 fatal turn 路径保持 `ops: []` + `error`，
   这样游戏状态破坏时不泄漏不可信 buffer 残留。
10. 作为测试开发者，我希望 `test_jsonl.py` 重写为断言 `ops[]` 结构，这样回归
    测试能验证 v2 wire format 的正确性。
11. 作为未来读者，我希望有 ADR 记录"为何用 op 序列而非快照"、"为何不保留 v1
    降级"、"为何 CLI 双写 `_agentBuffer`"——这些决策点对应
    ADR-0002。
12. 作为未来读者，我希望 `CONTEXT.md` 提供 Op / Print Op /
    NewLine Op / ClearLine Op / Clear Op / Set BG Op / Display State /
    Operation Sequence / Generation 等术语，这样我能在统一词汇下阅读代码与文档。

## Implementation Decisions

### 范围决策

- **本 PRD 范围**：R-05（per-segment 样式）+ align（行级对齐）+ R-07（操作序列）。
  三项统一为 v2 op 序列模型——R-05/align 是 op payload 的字段，R-07 是 op 序列
  本身。原 TODO 把 R-07 列为"非采集遗漏类、独立立项"，grilling 阶段确认三项应
  合并：op 序列是承载 R-05/align 的容器，三者不可分。
- **明确排除 R-06（按钮区域）**：`PointX` / `Width` 字段不进入 v2 turn。理由：
  - R-06 是"采集遗漏"性质，数据已存在于 `ConsoleButtonString`，但前端真正的命中
    测试需要 `ButtonRegionTracker` 的 East-Asian-Width 感知坐标计算，server 路径
    当前不引入 `ButtonRegionTracker`。
  - v2 的 `print.button` 已足够让前端知道"这段是按钮、值是什么"，前端可自行
    用 CSS / DOM 事件做命中测试，无需服务端坐标。
  - R-06 立项为 PRD-T6，待 v2 落地后评估是否还需要服务端坐标。
- **明确排除 R-09（图片元数据）**：`PrintImg` / `PrintShape` 在 v2 降级为
  `print(node.ToString())`（HTML 字符串占位）。真正的图片暴露等 Headless 实现
  sprite 加载（`AppContents.GetSprite` 非空实现）后再立项。
- **明确排除 LLM 兼容层**：v2 不保留 `text` 字段作 LLM 降级。LLM 交互由前端层
  中转——前端把 op 序列渲染成纯文本再喂给 LLM。服务端不承担 LLM 兼容。

### v2 Turn 结构

`TurnRecord` 重写，删除 `text` / `buttons`，新增 `ops`：

```csharp
internal record TurnRecord(
    string state,
    string? inputType,
    bool needValue,
    List<TurnOp> ops,
    string? error = null,
    int? protocolVersion = null  // 仅 initial turn 填 2
);

internal abstract record TurnOp(string type);

internal record PrintOp(
    List<PrintSegment> segments,
    ButtonRef? button
) : TurnOp("print");

internal record NewLineOp(
    string? align  // "left" | "center" | "right" | null
) : TurnOp("newline");

internal record ClearLineOp(int n) : TurnOp("clearline");

internal record ClearOp : TurnOp("clear");

internal record SetBgOp(string color) : TurnOp("set_bg");

internal record PrintSegment(
    string text,
    string? color,        // "#RRGGBB" 或 null（默认色）
    bool? bold,           // true/false 或 null
    bool? italic,         // true/false 或 null
    string? fontname      // 字体名或 null
);

internal record ButtonRef(
    object value,         // int 或 string，按运行时类型序列化
    bool isInteger        // 标记 value 是 int 还是 string
);

internal record ButtonEntry(string label, object value);  // 删除
```

- `TurnOp` 为 abstract record，`type` 字段作 discriminator。System.Text.Json
  默认序列化 derived record 时会带上所有字段，`type` 由基类构造参数固定。
- `PrintSegment.color` 为 `#RRGGBB` hex 字符串（drop alpha，CSS-friendly）。
  `EmuColor` 转换为 hex 由 `EmuColor.ToHex()` 扩展方法提供（新增）。
- `PrintSegment.bold` / `italic` 为 `bool?`：`null` 表示默认（不输出字段），
  `true` / `false` 显式输出。`StringStyle.FontStyle` 是 `EmuFontStyle` flags enum
  （Regular / Bold / Italic / BoldItalic），转换逻辑：
  - `bold = (style.FontStyle & Bold) != 0`
  - `italic = (style.FontStyle & Italic) != 0`
  - Regular 时两者均为 `null`（默认）。
- `PrintSegment.fontname` 为 `string?`：`null` 表示默认字体，非空时输出字体名。
- `ButtonRef.value` 为 `object`，序列化时按运行时类型发 `int` 或 `string`，与 v1
  `ButtonEntry.value` 行为一致。`isInteger` 字段帮助前端判断 value 类型（避免
  JSON 数字 vs 字符串的歧义，如 `"123"` vs `123`）。
- `ButtonEntry` record 删除——`CollectVisibleButtons()` 整个方法删除。

### Op Schema（wire format）

#### `print` op

```json
{
  "type": "print",
  "segments": [
    {"text": "Hello ", "color": "#FF0000", "bold": true},
    {"text": "World", "color": null, "italic": true}
  ],
  "button": {"value": 1, "isInteger": true}
}
```

- `segments[]` 至少 1 个元素。每个 segment 的 `text` 不可为 `null`（空字符串允许）。
- `button` 可选。`button.value` 为 `int` 或 `string`。`button.isInteger` 标记类型。
- 一个 `print` op 最多一个 `button`——若 `ConsoleButtonString` 跨多个 segment，
  整个 button 的值由 `btn.Input` / `btn.Inputs` 决定，segments 共享同一 button。
- `color` / `bold` / `italic` / `fontname` 为 `null` 时序列化时字段不存在
  （`WhenWritingNull` 行为）。

#### `newline` op

```json
{"type": "newline", "align": "center"}
{"type": "newline"}
```

- `align` 可选：`"left"` / `"center"` / `"right"` / 不存在。
- `align` 来自 `ConsoleDisplayLine.align`，对应 `DisplayLineAlignment` enum。
- `Print` 后未遇到 `newline` 时，后续 `print` 在同一行追加（line merge 语义）。

#### `clearline` op

```json
{"type": "clearline", "n": 3}
```

- `n` 必填，正整数。从 Display State 末尾删除 N 行。
- 对应 ERB `CLEARLINE n`。

#### `clear` op

```json
{"type": "clear"}
```

- 无 payload。清空整个 Display State。
- 对应 ERB `CLEAR` / `CLEARDRAWLINE`。

#### `set_bg` op

```json
{"type": "set_bg", "color": "#0000FF"}
```

- `color` 必填，`#RRGGBB` hex。
- 对应 ERB `SETBG`。`SETBGIMAGE` 在 Headless 为空实现，不 emit op。

### Op Emit 策略

op emit 集中在 `ConsolePrintManager.AddDisplayLine` 层，**不在 `Print` 层**。
理由：`Print(str)` 只是追加到 `printBuffer`，真正的行结构在 `PrintFlush` 完成
词法换行后才确定。在 `Print` 层 emit 会产生大量中间 op（每个 `Print` 调用一个
op），且无法表达"多个 Print 合并为一行"的语义。

#### `AddDisplayLine` emit 逻辑

```csharp
internal void AddDisplayLine(ConsoleDisplayLine line, bool force_LEFT)
{
    // ... 既有逻辑（temporary 替换、error 扫描、alignment 设置）...

    if (_state.displayLineList.Count != 0 && !_state.displayLineList[^1].IsLineEnd)
    {
        // 行合并：前一行 IsLineEnd==false，删除并拼接
        var lastline = _state.displayLineList[^1];
        DeleteLine(1, suppressOp: true);  // 抑制 clearline op
        line.ShiftPositionX(lastline.Buttons[^1].PointX + lastline.Buttons[^1].Width);
        line.ChangeStr([.. lastline.Buttons, .. line.Buttons]);
    }
    _state.displayLineList.Add(line);

    // === 新增：emit ops ===
    EmitPrintOps(line);        // 按 button 分段 emit print op
    if (line.IsLineEnd)
        EmitNewLineOp(line.align);

    _console.WriteAlignedLine(line);  // 保留：双写 _agentBuffer 给 CLI

    // ... 既有逻辑（lineNo++、logicalLineCount、MaxLog 截断）...
}
```

- `EmitPrintOps(line)` 遍历 `line.Buttons`（即 `ConsoleButtonString[]`，每个
  是一个 display chunk），为每个 chunk 构造 `PrintOp`：
  - `segments[]` 来自 `chunk.StrArray`（`AConsoleDisplayNode[]`），每个 node
    转成 `PrintSegment(text, color, bold, italic, fontname)`。
  - `button` 来自 `chunk.IsButton`：若 true，构造 `ButtonRef(value, isInteger)`；
    若 false，`button: null`。
- `EmitNewLineOp(align)` 仅在 `line.IsLineEnd == true` 时调用。`align` 来自
  `line.align` 字段（已通过 `SetAlignment` 设置）。

#### `DeleteLine` emit 逻辑

```csharp
public void DeleteLine(int argNum, bool suppressOp = false)
{
    // ... 既有删除逻辑 ...

    if (!suppressOp)
        _state._pendingOps.Add(new ClearLineOp(argNum));

    // 保留：双写 _agentBuffer 给 CLI
    if (!_console.RemoveLastLineFromAgentBuffer())
        _state._pendingEraseRows++;
}
```

- `suppressOp: true` 仅在 `AddDisplayLine` 的行合并分支调用——前端未见过该行的
  `newline`，新 `print` 自然追加，无需 `clearline`。
- 其他调用点（`CLEARLINE` ERB 指令、temporary 行替换）保持 `suppressOp: false`。

#### `ClearDisplay` emit 逻辑

```csharp
public void ClearDisplay()
{
    _state._pendingOps.Add(new ClearOp());
    // ... 既有清空逻辑 ...
}
```

#### `PrintTemporaryLine` emit 逻辑

`PrintTemporaryLine` 调用 `PrintSingleLine(str, temporary: true)`，最终走
`AddDisplayLine`。emit 路径与普通行一致：`print` + `newline`。
**下一个 `AddDisplayLine` 的 temporary 替换分支**（`if (LastLineIsTemporary)
DeleteLine(1)`）会 emit `clearline(1)`——不抑制，前端需要这个 op 清除临时行。

#### `SETBG` emit 逻辑

`SETBG` ERB 指令通过 `ConsolePrintManager` 的 `SetBg` / `ResetBg` 方法（若存在）
或 `EmueraConsole.AddBackgroundImage`（当前为空 stub）触发。v2 在此处 emit
`SetBgOp`。**若当前代码库无 `SetBg` 方法**，需新增——`SETBG` ERB 指令的
`Instruction` 调用 `_console.PrintManager.SetBg(color)`，emit `SetBgOp`。

> 实施时需先确认 `SETBG` 的当前代码路径，可能需要补全 Headless 下的 stub。

#### `PrintHtml` / `PrintImg` / `PrintShape` emit 逻辑

- `PrintHtml(str)`：解析 HTML 字符串，对每个文本节点 emit `print` op、`<br>`
  emit `newline` op。图片标签 `<img>` 降级为 `print(node.ToString())`
  （HTML 字符串占位）。
- `PrintImg(...)`：降级为 `print(node.ToString())`，不 emit 图片 op。
- `PrintShape(...)`：降级为 `print(node.ToString())`，不 emit 形状 op。

### Op 队列管理

`ConsoleStateData` 新增 op 队列：

```csharp
internal readonly List<TurnOp> _pendingOps = new();
```

- `BuildTurn` 序列化 `_pendingOps` 并 `Clear()`：
  ```csharp
  private string BuildTurn(bool isInitial = false)
  {
      var req = console.CurrentRequest;
      string? error = _pendingRejectReason;
      _pendingRejectReason = null;
      var ops = console.TakePendingOps();  // 返回 _pendingOps 并 Clear
      return JsonSerializer.Serialize(new TurnRecord(
          state: console.State.ToString(),
          inputType: req?.InputType.ToString(),
          needValue: req?.NeedValue ?? false,
          ops: ops,
          error: error,
          protocolVersion: isInitial ? CurrentProtocolVersion : null
      ), TurnJsonOptions);
  }
  ```
- `EmueraConsole.TakePendingOps()` 封装 `_state._pendingOps` 的取出并清空。
- Initial turn：直接序列化 `_pendingOps`——队列从会话开始累积，包含所有 op，
  apply 后得到正确初始 Display State。**不重放 `displayLineList`**——重放会丢失
  op 序列中的 `clearline` / `clear` 等行编辑历史。
- Fatal turn：`ops: new List<TurnOp>()`（空数组），不读 `_pendingOps`。

### CLI 共存策略（双写 `_agentBuffer`）

v2 保留 `_agentBuffer` 供 CLI 使用，新增 op 队列供 server 使用。`AddDisplayLine`
同时写两者：

- `_console.WriteAlignedLine(line)` 保留——写 `_agentBuffer`，CLI 的
  `TerminalRenderer.FlushBuffer()` 继续读。
- `EmitPrintOps(line)` / `EmitNewLineOp(align)` 新增——写 `_pendingOps`，
  server 的 `BuildTurn` 读。
- `DeleteLine` 保留 `RemoveLastLineFromAgentBuffer` + `_pendingEraseRows` 路径
  给 CLI，新增 `ClearLineOp` 给 server。
- `ClearDisplay` 保留 `_state.displayLineList.Clear()` 给 CLI 的 `FullRefresh`，
  新增 `ClearOp` 给 server。

**技术债**：`_agentBuffer` 沦为 CLI 专属路径，与 op 队列语义重叠。未来应让 CLI
也消费 op 队列（`TerminalRenderer.FlushBuffer` 改为 apply ops 到终端状态），
消除 `_agentBuffer` / `_pendingEraseRows` / `WriteAlignedLine` / `FormatLineForTerminal`
等 CLI 专属渲染路径。此项记入 [TODO.md](./TODO.md) 作为后续工作，不在本 PRD 范围。

### Fatal Turn 路径

`AgentJsonlProtocol.StepAsync` 的 catch 块保持独立构造，**不调 `BuildTurn()`**：

```csharp
catch (Exception ex)
{
    AgentLog.Instance.Write("step fatal: " + ex);
    var errorTurn = JsonSerializer.Serialize(new TurnRecord(
        state: console.State.ToString(),
        inputType: null,
        needValue: false,
        ops: new List<TurnOp>(),
        error: ex.Message
    ), TurnJsonOptions);
    Stop();
    return errorTurn;
}
```

理由与 v1 一致：fatal 时游戏内部状态已被破坏，`_pendingOps` 中的残留 op 不可信。
强制 `ops: []` 是有意安全策略。

### `CurrentProtocolVersion` 升级

```csharp
private const int CurrentProtocolVersion = 2;
```

定义位置不变（`AgentJsonlProtocol`）。

### 颜色 wire format

`EmuColor` 新增 `ToHex()` 扩展方法：

```csharp
public static class EmuColorExtensions
{
    public static string ToHex(this EmuColor c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
```

- Drop alpha（`A` 字段）——CSS 无 alpha hex 的对应概念，且 Emuera 颜色均为不透明。
- `PrintSegment.color` 为 `string?`，默认色（`ColorChanged == false`）时为 `null`，
  序列化时字段不存在。

### Generation 语义

v2 不在 turn 中输出 `generation` 字段。**turn 边界隐式标记前一代按钮过期**：

- `BuildTurn` 序列化当前 `_pendingOps` 后 `Clear()`——下一 turn 的 `print.button`
  即新代按钮。
- 前端 apply 当前 turn 的 ops 后，Display State 中所有 `print.button` 即可点击；
  上一 turn 的 `print.button` 仍可见（在历史行中）但已过期，前端应标记为不可点击。
- 服务端 `DispatchInput` 仍校验 `btn.Generation == LastButtonGeneration`——前端
  提交过期按钮值会被 `OnInputRejected` 拒绝（v1 既有逻辑保留）。

### 涉及文件清单

| 文件 | 改动类型 |
|------|----------|
| `Emuera.Headless/Agent/TurnRecord.cs` | 重写（删除 `text`/`buttons`/`ButtonEntry`，新增 `TurnOp` 层次） |
| `Emuera.Headless/Agent/AgentJsonlProtocol.cs` | 修改（`BuildTurn` 改读 `TakePendingOps`、`CurrentProtocolVersion=2`、fatal 路径 `ops:[]`、删除 `CollectVisibleButtons`） |
| `Emuera.Headless/UI/Game/Console/ConsoleStateData.cs` | 修改（新增 `_pendingOps` 队列） |
| `Emuera.Headless/UI/Game/Console/ConsolePrintManager.cs` | 修改（`AddDisplayLine` emit ops、`DeleteLine` 加 `suppressOp` 参数、`ClearDisplay` emit `ClearOp`） |
| `Emuera.Headless/UI/Game/EmueraConsole.cs` | 修改（新增 `TakePendingOps()`、保留 `TakeAgentBuffer` 给 CLI） |
| `Emuera.Headless/Primitives/EmuColor.cs` | 修改（新增 `ToHex()` 扩展方法或实例方法） |
| `tests/test_jsonl.py` | 重写（断言 `ops[]` 结构，删除 `text`/`buttons` 断言） |
| `CONTEXT.md`（仓库根） | 已落地（grilling 阶段，新增 Op 相关术语） |
| `docs/adr/0002-turn-v2-operation-sequence.md` | 已落地（grilling 阶段） |

## Testing Decisions

### 测试哲学

延续 [PRD-T3](./PRD-T3-Turn协议版本化.md) 的"只测 wire format，不测内部实现"。
如果未来把 `TurnOp` 改回匿名对象、或拆成多个 record，只要 wire format 不变，测试
不应失败。

### 测试 Seam

复用现有 `test_jsonl.py` 的 HTTP 集成测试 seam——这是覆盖 turn 协议的最高且
唯一 seam。**不新增测试 seam。**

### 重写断言

`test_jsonl.py` 重写为断言 `ops[]` 结构：

- **Initial turn**：
  - `turn1.get("protocolVersion") == 2`
  - `"text" not in turn1`（v2 删除 text 字段）
  - `"buttons" not in turn1`（v2 删除 buttons 字段）
  - `isinstance(turn1["ops"], list)` 且 `len(turn1["ops"]) > 0`
  - 遍历 `ops[]`，每个 op 有 `type` 字段，值为 `"print"` / `"newline"` /
    `"clearline"` / `"clear"` / `"set_bg"` 之一
  - `print` op 有 `segments[]`，每个 segment 有 `text` 字段（字符串）
  - `print` op 可选 `button` 字段，若有则含 `value` 与 `isInteger`
  - `newline` op 可选 `align` 字段
  - `clearline` op 有 `n` 字段（正整数）
  - `set_bg` op 有 `color` 字段（`#RRGGBB` 格式）

- **Step turn**：
  - `"protocolVersion" not in turn2`（step turn 不带版本字段）
  - `"text" not in turn2`
  - `"buttons" not in turn2`
  - `ops[]` 结构同上

- **Final turn**：
  - `"protocolVersion" not in turn3`
  - `ops == []` 或 `ops` 字段存在（final turn 可能无 op）

### 未覆盖项

- **Fatal turn 的 `ops: []` 断言**：本次不交付，记入
  [PRD-T4](./PRD-T4-FatalTurn测试.md) 的 fatal turn 测试夹具工作——PRD-T4 的
  fatal turn 断言需同步更新为 v2 结构（`ops: []` 而非 `text: ""` / `buttons: []`）。
  PRD-T4 的实施应在 PRD-T5 落地后进行。
- **op 序列的语义正确性**（如"clearline 后 print 是否正确追加"）：本 PRD 只测
  wire format 结构，不测前端 apply 后的 Display State——前端 apply 逻辑是前端
  的实现细节，不在服务端测试范围。

### Prior Art

- [test_jsonl.py](../../tests/test_jsonl.py)：v1 turn schema 断言，重写为 v2。
- [test_server_single_session.py](../../tests/test_server_single_session.py)：
  HTTP 单会话生命周期断言，应同步检查是否依赖 v1 字段。
- [test_tinput_timeout.py](../../tests/test_tinput_timeout.py)：TINPUT 超时
  场景，应同步检查是否依赖 v1 字段。
- [run_all.py](../../tests/run_all.py)：回归套件入口。

## Out of Scope

- **R-06 按钮区域**（`PointX` / `Width` / `row` / `col`）：立项为 PRD-T6，
  待 v2 落地后评估。
- **R-09 图片元数据**：`PrintImg` / `PrintShape` 降级为 `print(node.ToString())`。
  真正的图片暴露等 Headless 实现 sprite 加载后再立项。
- **LLM 兼容层**：v2 不保留 `text` 字段作 LLM 降级。LLM 交互由前端层中转。
- **CLI 迁移到 op 队列**：`_agentBuffer` 双写是技术债，未来让 CLI 也消费 op
  队列以消除重复。记入 [TODO.md](./TODO.md)。
- **v1 降级路径**：v2 是干净替换，不保留 v1 字段。客户端要么支持 v2 要么断开。
- **多 record 多态**：`TurnOp` 为 abstract record + derived records，但 wire format
  通过 `type` 字段 discriminator，不依赖 JSON polymorphism。
- **WebSocket 传输**：P0-2（Kestrel 替换）已为 WebSocket 铺路，但 WebSocket
  协议适配不在本 PRD 范围。
- **op 序列的压缩 / 增量编码**：本 PRD 不引入 op diff / op compaction。每个 turn
  的 `ops[]` 是自上一个 turn 以来的完整增量，前端 apply 后丢弃。

## Further Notes

### 与既有 ADR 的关系

ADR-0002 已在 grilling 阶段落地，
记录"v2 采用 op 序列模型、不保留 v1 降级、CLI 双写 `_agentBuffer`"的核心决策
与 3 条被拒绝的替代方案（v1+v2 双字段、2× 窗口快照、op 序列+text 降级）。
ADR-0001 的"wire format 字节级不变"
约束被 v2 显式 supersede，但 ADR-0001 的其他决策（版本字段仅在 initial turn、
fatal 路径独立构造、版本常量位置）仍然有效。

### 与 `CONTEXT.md` 的关系

grilling 阶段已更新仓库根 `CONTEXT.md`：
- 更新 Turn / Fatal Turn / protocolVersion 为版本无关描述。
- 新增 Generation 术语。
- 新增 "Operation Sequence Model (v2)" 子章节，定义 Op / Print Op / NewLine Op /
  ClearLine Op / Clear Op / Set BG Op / Display State / Operation Sequence 等
  8 个术语。

### 与架构评估报告其他任务的关系

本 PRD 是 [架构评估报告.md](./架构评估报告.md) P1 优先级任务 3 的 v2 演进。
[PRD-T3](./PRD-T3-Turn协议版本化.md) 是 v1 的交付，本 PRD 是 v2 的交付。两者
共享 `TurnRecord` / `protocolVersion` / fatal 路径独立构造等基础决策，v2 在此
基础上替换显示数据模型。

### 与 [PRD-T4](./PRD-T4-FatalTurn测试.md) 的关系

PRD-T4 的 fatal turn 测试夹具应在 PRD-T5 落地后实施，断言需同步更新为 v2
结构（`ops: []` 而非 `text: ""` / `buttons: []`）。PRD-T4 文档中的 v1 断言
描述应标记为"待 PRD-T5 落地后更新"。

### 实施前验证

实施者应在落地代码后、运行测试前验证：

1. `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 成功，无
   新增 error（`Shared/` 子树的历史警告不在关注范围）。
2. `python tests/test_jsonl.py --binary <path> --game-dir test_game` 全部通过——
   v2 断言结构正确。
3. `python tests/run_all.py --binary <path> --game-dir test_game` 全套通过——
   验证 server 单会话、TINPUT timeout、force quit survival 等场景未因 v2 破坏。
4. 手动验证 CLI 模式仍正常工作（`--protocol cli`）——`_agentBuffer` 双写路径
   未破坏 CLI 渲染。
5. 同步检查 `test_server_single_session.py` / `test_tinput_timeout.py` 是否
   依赖 v1 字段（`text` / `buttons`），若有则同步更新。
