# PRD: Turn 协议版本化与 Schema 定义

> 对应 [架构评估报告.md](./架构评估报告.md) 任务 3、ADR-0001。
> 本 PRD 严格遵循 grilling 阶段达成的共识。

## Problem Statement

作为 Emuera.Headless 的前端开发者（MCP 网关、未来跨平台 Web/TUI 前端），
我对接服务端 turn 协议时，无法在协议演进时感知版本变更——当 `BuildTurn()` 输出
的 JSON 字段发生增减或语义变化时，前端只能静默失败。同时，作为后续维护者，我
发现 turn 的 JSON 结构由 `BuildTurn()` 内的匿名对象序列化而成，缺乏类型契约，
增改字段时无编译期保障。

## Solution

把 `BuildTurn()` 的匿名对象重构为 `TurnRecord` + `ButtonEntry` 两个 record，
并在 initial turn 顶层加 `protocolVersion: 1` 字段。客户端通过 initial turn
感知协议版本，后续 turn 不重复声明。**不引入握手协议**——版本字段是单向声明，
不是双向协商。

关键约束：除 initial turn 多 `protocolVersion` 字段外，wire format 字节级不变。
现有 Python 客户端、`test_jsonl.py` 断言除新增字段外不需要任何修改。

## User Stories

1. 作为 MCP 网关开发者，我希望 initial turn 携带 `protocolVersion` 字段，这样
   我可以在协议升级时感知版本并选择降级或断开。
2. 作为 MCP 网关开发者，我希望 step turn / final turn 不携带 `protocolVersion`
   字段，这样协议帧不会因版本号重复声明而冗余。
3. 作为 MCP 网关开发者，我希望 input 方向（`POST /input` body）保持
   `{"type":"input","value":"..."}` 不变，这样我的客户端代码不需要协同升级。
4. 作为 Emuera.Headless 维护者，我希望 turn JSON 结构由 `TurnRecord` record 定义，
   这样我在增改字段时有编译期类型检查。
5. 作为 Emuera.Headless 维护者，我希望 button 元素由 `ButtonEntry` record 定义，
   这样我在 button 结构上增改字段时有一处集中修改点。
6. 作为 Emuera.Headless 维护者，我希望 fatal turn 路径与 step turn 路径分离构造，
   这样 fatal 时不会因为 `TakeAgentBuffer()` 返回半截 buffer 而泄漏不可信数据。
7. 作为 Emuera.Headless 维护者，我希望 `protocolVersion` 的当前值通过常量
   `CurrentProtocolVersion` 定义，这样未来升级版本时有一处显式修改点。
8. 作为 Emuera.Headless 维护者，我希望 wire format 在重构后字节级不变（除
   initial turn 新增 `protocolVersion` 字段），这样我不需要协同更新 Python
   客户端和测试断言。
9. 作为测试开发者，我希望 `test_jsonl.py` 扩展断言 `protocolVersion` 字段，这样
   回归测试能验证版本字段的正确位置与值。
10. 作为测试开发者，我希望 fatal turn 路径有结构化断言覆盖（即使本次 PRD 不交付），
    这样 fatal 路径的 wire format 不会因未来重构而漂移——此项见 [TODO.md](./TODO.md)。
11. 作为未来读者，我希望有 ADR 记录"为何不引入握手"、"为何 fatal 路径不调
    `BuildTurn()`"、"为何版本字段仅出现在 initial turn"——这些决策点对应
    ADR-0001。
12. 作为未来读者，我希望 `CONTEXT.md` 提供 turn 协议术语表（Turn / Initial Turn /
    Step Turn / Final Turn / Fatal Turn / protocolVersion / Input Rejection /
    Fatal Exception），这样我能在统一词汇下阅读代码与文档。

## Implementation Decisions

### 范围决策

- **采用 A + B 范围**：record 重构（A）+ initial turn 加 `protocolVersion` 字段（B）。
- **明确排除握手协议（C）**：不引入 `POST /session` 后的版本协商往返。理由见
  ADR-0001 Rejected Alternatives。
- **input 方向不加版本字段**：`JsonlCommand` record 保持
  `(string type, string value)` 不变。

### Turn 结构定义

单一 `TurnRecord` record 承载所有 turn 路径（initial / step / final / fatal），
通过可选字段区分：

```csharp
internal record TurnRecord(
    string text,
    string state,
    string? inputType,
    bool needValue,
    List<ButtonEntry> buttons,
    string? error = null,
    int? protocolVersion = null  // 仅 initial turn 填 1
);

internal record ButtonEntry(string label, object value);
```

- `ButtonEntry.value` 为 `object`：序列化时按运行时类型发 `int` 或 `string`，
  wire format 与当前匿名对象一致。
- `protocolVersion` 为 `int?`，依赖 System.Text.Json 默认 `WhenWritingNull`
  行为：非 initial turn 序列化时字段不存在。

### BuildTurn 签名

`BuildTurn(bool isInitial = false)` —— `isInitial: true` 时填
`protocolVersion = CurrentProtocolVersion`，否则填 `null`。

```csharp
private const int CurrentProtocolVersion = 1;

private string BuildTurn(bool isInitial = false)
{
    var req = console.CurrentRequest;
    return JsonSerializer.Serialize(new TurnRecord(
        text: console.TakeAgentBuffer(),
        state: console.State.ToString(),
        inputType: req?.InputType.ToString(),
        needValue: req?.NeedValue ?? false,
        buttons: CollectVisibleButtons(),
        error: _pendingRejectReason,
        protocolVersion: isInitial ? CurrentProtocolVersion : null
    ));
}
```

`CurrentProtocolVersion` 为 `private const int` 定义在 `AgentJsonlProtocol`
上。**不提升到 `AgentProtocolBase`**——CLI 协议走 stdin/stdout，无 turn 概念，
不会复用此常量。

### CollectVisibleButtons 返回类型变更

`CollectVisibleButtons()` 返回类型从 `List<object>` 改为 `List<ButtonEntry>`，
构造点改为：

```csharp
buttons.Add(new ButtonEntry(
    label: btn.ToString(),
    value: btn.IsInteger ? (object)btn.Input : (object)btn.Inputs
));
```

### Fatal 路径独立构造

`AgentJsonlProtocol.StepAsync` 的 catch 块**不调用 `BuildTurn()`**，内联构造
`TurnRecord`：

```csharp
catch (Exception ex)
{
    AgentLog.Instance.Write("step fatal: " + ex);
    var errorTurn = new TurnRecord(
        text: "",
        state: console.State.ToString(),
        inputType: null,
        needValue: false,
        buttons: new List<ButtonEntry>(),
        error: ex.Message
    );
    Stop();
    return JsonSerializer.Serialize(errorTurn);
}
```

理由：fatal 时游戏内部状态（指令指针/栈帧/变量表）已被破坏，
`TakeAgentBuffer()` 返回的半截 buffer 与 `CollectVisibleButtons()` 返回的
可能失效按钮不可信。强制 `text=""`、`buttons=[]` 是有意安全策略。
`error` 字段填 `ex.Message`，与 `_pendingRejectReason`（Input Rejection 语义）
显式分离。

### GetInitialTurnAsync 调用点

```csharp
internal override async Task<string?> GetInitialTurnAsync()
{
    if (!await WaitForInputAsync())
        return null;
    return BuildTurn(isInitial: true);
}
```

其他调用点（`StepAsync` / `SubmitTimeoutAsync` / `BuildFinalTurn`）保持
默认参数 `isInitial: false`。

### 文件位置

- 新增 `Emuera.Headless/Agent/TurnRecord.cs`：包含 `TurnRecord` + `ButtonEntry`
  两个 record。
- 命名空间 `MinorShift.Emuera.GameView`，与 `AgentJsonlProtocol`、
  `JsonlCommand` 一致。
- 可见性 `internal`，与同目录其他类型一致。
- `JsonlCommand.cs` **不动**。

### 涉及文件清单

| 文件 | 改动类型 |
|------|----------|
| `Emuera.Headless/Agent/TurnRecord.cs` | 新增 |
| `Emuera.Headless/Agent/AgentJsonlProtocol.cs` | 修改（`BuildTurn` 重构 + fatal 路径内联 record + `CollectVisibleButtons` 返回类型 + `CurrentProtocolVersion` 常量） |
| `tests/test_jsonl.py` | 修改（新增 `protocolVersion` 断言） |
| `CONTEXT.md`（仓库根） | 已落地（grilling 阶段） |
| `docs/adr/0001-turn-protocol-versioning.md` | 已落地（grilling 阶段） |

## Testing Decisions

### 测试哲学

仅测外部行为（HTTP 接口暴露的 turn JSON 结构），不测内部实现细节（C# record
类型、私有方法、字段布局）。如果未来把 `TurnRecord` 改回匿名对象、或拆成多个
record，只要 wire format 不变，测试不应失败。

### 测试 Seam

复用现有 `test_jsonl.py` 的 HTTP 集成测试 seam——这是覆盖 turn 协议的最高且
唯一 seam。**不新增测试 seam。**

理由：
- HTTP 接口是客户端实际观察到的协议，是正确的观察点。
- 新增 C# 单测层验证 `TurnRecord` 字段会锁定内部类型结构，与"wire format
  字节级不变"的核心约束相悖。
- 现有 seam 已覆盖 initial / step / final 三种 turn 路径，扩展成本低。

### 扩展断言

在 `test_jsonl.py` 的 Turn 1（initial turn）断言中新增：

- `turn1.get("protocolVersion") == 1`（initial turn 声明版本）

在 Turn 2 / Turn 3（step / final turn）断言中新增：

- `"protocolVersion" not in turn2`（step turn 不带版本字段）
- `"protocolVersion" not in turn3`（final turn 不带版本字段）

现有断言（`text`/`state`/`inputType`/`needValue`/`buttons` 结构）全部保留——
它们验证 wire format 字节级不变这一核心约束。

### 未覆盖项

fatal turn 路径的结构断言本次不交付，记入 [TODO.md](./TODO.md) 作为后续工作。
理由：fatal 路径的 wire format **未改变**——本次决策明确"保留原样"。测试未改变
的行为属于覆盖延伸，不属于本 PRD 核心交付。`test_game/erb/TEST.ERB` 当前不抛
异常，覆盖 fatal 路径需要复制 `test_game` 并注入 ERB 异常逻辑，应作为独立
测试夹具工作。

### Prior Art

- [test_jsonl.py](../../tests/test_jsonl.py)：JSONL turn schema 断言
  （buttons label/value、generation 过滤、stale button 排除）。
- [test_server_single_session.py](../../tests/test_server_single_session.py)：
  HTTP 单会话生命周期断言。
- [test_tinput_timeout.py](../../tests/test_tinput_timeout.py)：
  复制 `test_game` + 覆盖 ERB 构造特定场景的模式——未来的 fatal turn 测试
  应参照此模式。

## Out of Scope

- **握手协议**：不引入 `POST /session` 后的版本协商往返。未来若需多前端并发或
  WebSocket 双向协商，单独立项。
- **input 方向版本字段**：`JsonlCommand` 不加 `protocolVersion` 字段。
- **多个 record 多态**：不引入 `InitialTurnRecord`/`StepTurnRecord`/
  `ErrorTurnRecord` 等多 record 结构。
- **fatal 路径统一走 `BuildTurn`**：fatal 路径保持独立构造，不与 step 路径合并。
- **fatal turn 测试覆盖**：本次不交付，记入 TODO.md。
- **turn schema Source Generator**：架构评估报告提到"考虑引入 Source Generator
  生成序列化代码"——本次不引入。当前 `JsonSerializer.Serialize<TurnRecord>`
  反射开销可接受，过早引入 SG 会增加构建复杂度。
- **协议版本协商客户端逻辑**：MCP 网关侧不实现"读到不认识的版本则断开"的降级
  逻辑——当前 `protocolVersion = 1` 是唯一版本，无降级路径可走。未来 v2 出现
  时再实现。
- **JSONL turn 的 WebSocket 传输**：P0-2（Kestrel 替换）已为 WebSocket 铺路，
  但 WebSocket 协议适配不在本 PRD 范围。

## Further Notes

### 与既有 ADR 的关系

ADR-0001 已在 grilling 阶段落地，
记录"A + B 范围、排除握手"的核心决策与 4 条被拒绝的替代方案。本 PRD 是该 ADR
的实现规格。

### 与 `CONTEXT.md` 的关系

grilling 阶段已建立仓库根 `CONTEXT.md`，定义 Turn / Initial Turn / Step Turn /
Final Turn / Fatal Turn / protocolVersion / Input Rejection / Fatal Exception
等 8 个领域术语。本 PRD 全程使用该词汇表，避免 `error turn`（与 Input Rejection
的 `error` 字段混淆）等模糊说法。

### 与架构评估报告其他任务的关系

本 PRD 是 [架构评估报告.md](./架构评估报告.md) P1 优先级任务 3 的独立交付。
P1 任务 4（IConsoleUI.Invoke 调度器抽象）是独立的演进路径，不与本 PRD 交叉。
P0 任务 1（终端抽象层）和 P0 任务 2（Kestrel 替换）已完成——Kestrel 替换把
HTTP 路由从 `/sessions/{id}/...` 改成 `/session`/`/turn`/`/input`，本 PRD 在
此 HTTP 接口上扩展 `protocolVersion` 字段，不再叠加 API 路由破坏。

### 实施前验证

实施者应在落地代码后、运行测试前验证：

1. `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 成功，无
   新增 error / warning（`Shared/` 子树的历史警告不在关注范围）。
2. `python tests/test_jsonl.py --binary <path> --game-dir test_game` 全部通过。
3. `python tests/run_all.py --binary <path> --game-dir test_game` 全套通过——
   验证 `protocolVersion` 字段引入未破坏 server 单会话、TINPUT timeout、
   force quit survival 等场景。
