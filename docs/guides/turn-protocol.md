# Turn 协议规格（wire 契约）

> **本文是协议的权威规格。** 字段以本文为准；C# 端 `EraCore.Core/Agent/TurnRecord.cs`、
> `EraCore.Core/Agent/DisplayState.cs` 是它的实现，前端 `EraCore.Web/src/types/protocol.ts`
> 是它的镜像。三者不一致时以本文为准，并订正另两处。
>
> 当前协议版本：**`protocolVersion = 12`**（定义在 `TurnRecord.CurrentProtocolVersion`）。
>
> 本文回答"**线上长什么样**"。为什么这么设计见
> [`docs/adr/`](../adr/)；怎么调用见 [agent-integration.md](agent-integration.md)。

---

## 1. 一帧 = 一个 `TurnRecord`

游戏引擎的输出与客户端的输入都是**回合制**的：每一帧是一次"画面 + 输入需求"的完整快照，
不是增量操作序列（增量在帧内以 `diff` 承载，见 §4）。

```
客户端                          服务端
  │                                │
  │── POST /input {"value":"1"} ──▶│  引擎推进 → DisplayState.ComputeDiff()
  │                                │  AgentJsonlProtocol.BuildTurn() 打包成 TurnRecord
  │◀── GET /turn  200 TurnRecord ──│  HttpSessionIO.WriteLine() ─┐
  │                                │                             ├─▶ OutputHub 扇出
  │◀── GET /ws Text 帧（同一份）───│                             ┘
```

**同一份 turn 字符串同时喂给 HTTP 长轮询与所有 WS 观察者**（`HttpSessionIO.WriteLine`
写 `_output` Channel 的同时调 `_broadcaster.Publish`）。这意味着 `/turn` 的响应体与
`/ws` 的帧内容**逐字节一致**——客户端一套解析器即可复用两种传输。

---

## 2. `TurnRecord` 字段表

`TurnRecord` 只有 **六个键**：唯一的契约字段 `awaiting`，加上帧内增量、诊断与协议元信息。

序列化用 `JsonSerializerOptions.DefaultIgnoreCondition = WhenWritingNull`——
**nullable 字段为 null 时字段整个不出现**（不是写 `"x": null`）。下表"省略"列即为此意。

| 字段 | 类型 | 必现 | 说明 |
|---|---|---|---|
| `awaiting` | object | **是** | **唯一契约字段**：服务端此刻在等什么。见下 |
| `diff` | object | 否 | 行级增量。见 §4。null/省略 = **画面完全未变**（输入被拒，或空转帧被抑制） |
| `error` | string | 否 | 语义收窄为一句话：**非空 ⇒ 客户端上一次输入未被接受，画面与等待状态均未推进**（见 §7） |
| `errorInfo` | object | 否 | 结构化异常（`type`/`message`/`inner?`，`inner` 最深 4 层）。见 §7 |
| `protocolVersion` | number | **仅首帧** | 协议版本。仅 initial turn 携带，后续帧省略（见 §6） |
| `generation` | number | **是** | 按钮代号，用于按钮过期检测（见 §5）。非 nullable，每帧都出现 |

> `generation` 是唯一"非 nullable 且每帧必现"的字段——客户端无需处理 `undefined`。

🔒 **v12 起不存在** `state` / `inputType` / `needValue` / `timeLimit` / `displayTime` /
`timeUpMessage` / `timedOut` 这些顶层字段：它们承载的信息全部并入 `awaiting`。

| 旧顶层字段 | v12 去向 |
|---|---|
| `state` | 帧内不再有"引擎状态"——"在等什么"读 `awaiting`；会话级状态见 §3 |
| `inputType` | `awaiting.input.type` |
| `needValue` | 不占字段：客户端按 `input.type` 推出（§3 的输入类型表） |
| `timeLimit` | `awaiting.wait.durationMs` |
| `displayTime` / `timeUpMessage` / `timedOut` | `awaiting.wait` 的同名键（语义不变：仅真/有值时出现） |

### `awaiting` —— 唯一契约字段

`awaiting` 每帧必现（非 nullable），回答**服务端此刻在等什么**。它是「两个正交维度 +
一个互斥终态键」：

| 键 | 性质 | 出现条件 | 载荷 |
|---|---|---|---|
| `input` | 正交维度 | 非终态，且**期待客户端发输入**（`inputType != Void`） | `type`（`InputType` 字符串，见 §3） |
| `wait` | 正交维度 | 非终态，且这段等待**有上界**（`Timelimit >= 0`） | `durationMs`，可选 `interruptible` / `displayTime` / `timeUpMessage` / `timedOut` |
| `ended` | **互斥键** | 终态（`Quit` / `Error`） | `terminal`（状态名）；出现时前两者**必不出现** |

🔒 两条硬规则：

1. `ended` 出现 ⇒ `input` / `wait` **必不出现**；
2. `ended` 未出现 ⇒ `input` / `wait` **至少其一必现**（两者都空 = 这个等待既不等输入也不等
   定时器，没有任何东西能推进它）。

**三个键写死，`wait` 的三态也写死（不得再加第四个取值）：**

| 形态 | 含义 | 客户端表现 |
|---|---|---|
| `wait` 缺省（`Timelimit < 0`，即 `InputRequest.Timelimit` 的默认值 -1） | **无限期**：没有定时器 | 无倒计时、无超时文案，`timedOut` 不出现 |
| `wait.durationMs == 0` | **零时长让路点**（无参 `AWAIT` / `AWAIT 0`） | 无 UI 表现；语义是"**下一帧立刻到**" |
| `wait.durationMs > 0` | **定时等待** | 倒计时；`interruptible` 为真时给"跳过"affordance |

`interruptible` / `displayTime` / `timedOut` **仅在 `true` 时出现**（省略即 false），故零时长
让路点在 wire 上就是最简形态 `{"wait":{"durationMs":0}}`——比定时等待只少一个数字。

常见形态：

```jsonc
{"awaiting":{"input":{"type":"EnterKey"}}}                                  // WAIT：纯输入等待（无 wait）
{"awaiting":{"input":{"type":"IntValue"},
             "wait":{"durationMs":3000,"displayTime":true}}}                // TINPUT：两维同现 + 倒计时
{"awaiting":{"wait":{"durationMs":8000,"interruptible":true}}}              // AWAIT 8000：可打断的定时等待
{"awaiting":{"wait":{"durationMs":8000}}}                                   // TWAIT 8000,1：不可打断（该键不出现）
{"awaiting":{"wait":{"durationMs":0}}}                                      // 无参 AWAIT：零时长让路点
{"awaiting":{"ended":{"terminal":"Quit"}}}                                  // 终态：与 input / wait 互斥
```

> ⚠️ **客户端遇到未知键必须回退取快照，不得静默忽略**：若 `awaiting` 里出现本文未描述的
> 第三个维度（或 `wait` 的未知选项），客户端应重新 `GET /snapshot` 并把它当作
> 「服务端在等一件我不认识的事」处理（保守：不推进、不显示可交互 affordance）。
> 静默忽略会把「在等一件我不知道的事」误当成「无事可做」。未知的
> `awaiting.ended.terminal` 取值按**终态**处理（停止交互），不是空闲。
>
> ⚠️ `GET /snapshot` 也带 `awaiting` 且**形状相同**（见 §6）；但快照可能在"脚本还在跑"的
> 瞬态被取到 —— 那时两维都无值（`awaiting:{}`）。流上的帧只在**停稳点**产出，故"至少一维
> 必现"只约束**流上的帧**（§8 说明产出时机）。
>
> 致命错误帧（MAUI `BridgeHost.ShowError`，不经过脚本）同样是终态形状
> `{"awaiting":{"ended":{"terminal":"Error"}}}`，见 §7。

### 示例：initial turn

```json
{
  "awaiting": { "input": { "type": "IntValue" } },
  "diff": null,
  "protocolVersion": 12,
  "generation": 0
}
```

### 示例：带画面增量的 step turn

```json
{
  "awaiting": { "input": { "type": "AnyKey" } },
  "diff": {
    "lineOps": [
      { "type": "append", "newLines": [ /* DisplayLine[]，见 §4 */ ] }
    ],
    "bgColor": "#101010",
    "bgImages": null
  },
  "generation": 3
}
```

---

## 3. 状态与输入类型

### `state`（`ConsoleState`）—— 只在 `GET /state` 出现

**帧里没有 `state`**（v12 起删除）。会话级状态由 `GET /state` 的 `state` 字段给出，用于 UI
判断"是否有活跃游戏"（`Idle` = 无活跃 session）。它不是"服务端此刻在等什么"——那件事读
`awaiting`。

| 值 | 含义（`GET /state` 语义） |
|---|---|
| `Idle` | 无活跃 session |
| `Loading` | 会话已建、游戏循环未启动完 |
| `Running` | 脚本执行中 |
| `WaitInput` | 停在某个等待上（等输入 / 定时等待 / 让路都归此类——**帧里不区分**） |
| `Quit` / `Error` | 会话终结 |

> ⚠️ `Running` **不会出现在帧里**：帧只在脚本停稳点产出（§8），wire 上不存在"脚本正在跑"。
> 历史上的 `Sleep`（=21）已删除——它曾承载 `AWAIT`，现已改走停稳点（§8）。

### `inputType`（`InputType`）—— 即 `awaiting.input.type`

| 值 | 含义 | 需要输入 |
|---|---|---|
| `EnterKey` | 回车或点击 | 是（可空） |
| `AnyKey` | 任意键 | 是（可空） |
| `IntValue` | 整数 | 是（必须填） |
| `StrValue` | 字符串 | 是（可空） |
| `AnyValue` | 整数或字符串 | 是（必须填） |
| `IntButton` | 整数按钮 | 是（按钮热区） |
| `StrButton` | 字符串按钮 | 是（按钮热区） |
| `PrimitiveMouseKey` | 等待原始鼠标/键盘事件 | 走事件通道，非文本输入 |
| `Void` | **不接受输入，只能等** | **否** |

**`Void` 是本协议里最容易被误读的取值**，务必注意——它同时承担两种语义：

| 场景 | `Timelimit` | `Skippable` | 客户端能否用输入打断 |
|---|---|---|---|
| `TWAIT t,1` —— 参考实现的定时等待 | > 0 | false | **否**（输入被吞掉，保持原语义） |
| `AWAIT t` —— 回合内动画/渐变等待 | > 0 | true | **是**（任意输入提前结束等待） |

`Skippable` 自 v12 起**已上 wire**——就是 `awaiting.wait.interruptible`（见 §2）。
客户端**不需要**再反向推断：`interruptible` 为真即可启用"跳过"affordance，
缺省（不可打断）时不启用。那个旧顶层字段 `state`/`inputType`/`timeLimit` 的组合推断方式
已随七个顶层字段在 v12 删除。

---

## 4. `diff`（`DisplayDiff`）—— 帧内增量

`TurnRecord` 顶层只带 `diff`，不带 `ops[]`（v5 起 `ops` 已废弃移除）。

```json
"diff": {
  "lineOps": [ ... ],
  "bgColor": "#RRGGBB",
  "bgImages": [ ... ]
}
```

| 字段 | 说明 |
|---|---|
| `lineOps` | 行级操作数组，**必须按顺序应用**。空数组 = 无行变化 |
| `bgColor` | 背景色。null/省略 = 未变 |
| `bgImages` | 背景图**整体替换**语义：省略/null = 未变，`[]` = 清空，非空 = 全量新状态 |

### `lineOps` 四种操作

Emuera 的显示模型是**追加式**的，故只需四类操作覆盖全部情形：

| `type` | 载荷 | 语义 |
|---|---|---|
| `append` | `newLines: DisplayLine[]` | 追加到末尾 |
| `clear_line_diff` | `clearCount: number` | 从**末尾**删除 n 行（CLEARLINE）。其后的 `append` 即重印 |
| `clear_screen` | 无 | 全清。其后的 `append` 即重印全量 |
| `shift_head` | `count: number` | 从**头部**删除 n 行（MaxLog 滚动截断） |

**应用顺序**：`shift_head` → `clear_line_diff` → `clear_screen` → `append`。

- `shift_head` 与 `clear_screen` 互斥（同回合不会既头部截断又全清）。
- `shift_head` 可与 `clear_line_diff` + `append` 共存。
- 无换行的 `PRINT` 落到最后一行 = `clear_line_diff(1)` + `append(1)`。

### `DisplayLine`（`append` 的载荷）

```json
{
  "entries": [ { "segments": [ PrintSegment ], "button": null } ],
  "align": "left",
  "isLineEnd": true
}
```

`entries` 中每个元素对应服务端一个 `ConsoleButtonString`——**可能是按钮，也可能是普通
文本**，靠 `button` 是否为 null 区分。`align` 取 `"left"`/`"center"`/`"right"`/null。

### `PrintSegment`

一段带样式的字符序列：

```json
{
  "text": "HP",
  "color": "#FF0000",
  "bold": true,
  "italic": null,
  "fontname": null,
  "underline": null,
  "strikeout": null,
  "image": null,
  "shape": null
}
```

| 字段 | 补充 |
|---|---|
| `text` | 必现。有 `image`/`shape` 时它只是调试标记，前端忽略 |
| `color` | **只在与游戏默认色不同时才出现**。null/省略 = 用默认文字色（取 `GET /state` 的 `foreColor`） |
| `bold`/`italic`/`underline`/`strikeout` | 布尔样式。null/省略 = 不加该样式 |
| `fontname` | null/省略 = 用默认字体（`GET /state` 的 `fontName`） |
| `image` | `SegmentImage`，见下 |
| `shape` | `SegmentShape`，见下 |

**几何一律由服务端算好，客户端零布局数学**——这是协议的核心设计承诺。

`SegmentImage`：`src`（游戏根相对路径）/ `srcb`（悬停态）/ `srcm`（映射图，点击时读
像素色作为按钮值）/ `width`/`height`/`ypos`（已解析 px）/ `crop`（v9，图集裁切：`x`/`y`
负偏移 + `imgWidth`/`imgHeight` 渲染尺寸）。

`SegmentShape`：`type` 恒为 `"rect"` + `x`/`y`/`width`/`height`/`color`。

> 图片路径经 `GET /assets/{path}` 取，服务端只放行图片扩展名白名单并做路径消毒。

---

## 5. `generation` 与按钮过期

按钮失效是**按代**（generation）而非按内容比对的：

- 服务端每次按钮集合结构性变更，`generation` 递增。
- 客户端提交按钮时带上点击时看到的 `generation`。
- 服务端比对当前代；不符则该输入无效——**避免用户点击陈旧画面导致的误操作**。

客户端必须做到：**渲染时记住 `generation`，点击时回传同一个值**。`generation` 每帧携带，
与 `ButtonRef.generation` 冗余，用于免去逐个按钮查找。

---

## 6. `protocolVersion` 与版本演进

**只有 initial turn 携带 `protocolVersion`**，后续帧省略。这是单向声明而非双向协商
（[ADR-0001](../adr/0001-turn-protocol-versioning.md)）——客户端连上后从首帧即知版本，
无需握手往返。

演进策略是**纯增量字段 + 破坏性取值变更才升版本**：新增 nullable 字段不升版本号（旧客户端
忽略未知字段自然降级）；但给取值集合加新值、或给 `awaiting` 加新维度键，**必须升**（§10）。

| 版本 | 变更 |
|---|---|
| v1 | `TurnRecord` record 化，initial turn 加 `protocolVersion` |
| v4 | diff 模式——`ops[]` 移除，仅 `diff` |
| v5 | 清空信号自描述——`TruncateLinesOp`/`ReplaceAllOp` → `clear_line_diff`/`clear_screen` |
| v6 | TINPUT 计时字段：`timeLimit`/`displayTime`/`timeUpMessage`/`timedOut` |
| v7 | 按钮 `generation` 过期检测 |
| v8 | 图片与形状：`PrintSegment.image`/`.shape`、`bgImages`、`set_bg_image` 族 |
| v9 | `SegmentImage.crop`——图集 sprite 裁切 |
| v10 | `PrintSegment.underline` |
| v11 | `PrintSegment.strikeout`——按 ERB `FONTSTYLE` 位契约对称补齐（位4=删除线、位8=下划线） |
| v12 | `awaiting` 容器（`input` / `wait` 两个正交维度 + `ended` 互斥终态）成为**唯一契约字段**，"用 `state`+`inputType`+`timeLimit` 反向推断"退场；七个旧顶层字段（`state`/`inputType`/`needValue`/`timeLimit`/`displayTime`/`timeUpMessage`/`timedOut`）**全部删除**；`wait.interruptible` 让 `Skippable` 首次上 wire |

> v2/v3 属重构期过渡态，未在生产协议中留存。

### `DisplaySnapshot`（`GET /snapshot`）

全量快照给**晚加入者**用（WS 连上时错过的帧不可回放——ADR-0003 明确"晚加入者不回放历史"）。

```json
{
  "lines": [ DisplayLine ],
  "bgColor": "#101010",
  "bgImages": [ BgImageState ],
  "awaiting": { "input": { "type": "AnyKey" } },
  "protocolVersion": 12,
  "generation": 3
}
```

与 `TurnRecord` 的差异：带 `lines` 全量而非 `diff`；**不带 `timedOut`**（快照表达"当前
状态"而非"如何到达此状态"，晚加入者不关心上一帧是否刚超时——这条不对称同样适用于
`awaiting.wait.timedOut`：快照里它恒不出现）。`awaiting` 与流上帧**同形**
（同一个工厂、同一个 `req`/`state`），故晚加入者拿到的第一张画面不是另一套契约形状。

**晚加入流程**：`GET /snapshot` 建初始画面 → 订阅 `/ws` → 逐帧应用 `diff`。

---

## 7. `error` 与 `errorInfo`

两者**语义分离**，不要混用：

| 字段 | 含义 | 会话是否终结 |
|---|---|---|
| `error` | 字符串。**输入拒绝**（"当前需要整数输入，请重试"）或脚本异常的完整串 | 否（输入拒绝）/ 是（异常） |
| `errorInfo` | 结构化异常 `{type, message, inner?}`，`inner` 最深 4 层 | 同上 |

`errorInfo` 是纯增量 nullable 字段——非异常帧不出现，**不升协议版本**。它的存在是为了让
agent 一次定位崩溃，而不必在长堆栈里手工挖类型与信息。

> 初始化阶段的异常与游戏循环回调的异常都**不经过脚本**，走的是
> `AgentJsonlProtocol.BuildErrorTurn`——终态形状 `awaiting.ended{terminal:"Error"}` +
> `error`（完整串）+ `errorInfo`（结构化）。见 `Session.DeliverInitStageFailure`。

---

## 8. 帧的产出时机 = 轴 A（停稳点形态）× 轴 B（驱动）

🔒 **不变量（唯一必要条件）：服务端只在脚本已停稳时才发帧。**

「产出时机」曾含两类信息，拆开后各归其位：

| 轴 | 回答 | 取值 | 决定什么 |
|---|---|---|---|
| **A · 脚本侧** | 停稳点**是什么** | 等输入 / 定时等待 / 让路 / 终态 | `awaiting` 的内容（§2） |
| **B · 驱动侧** | **谁**把脚本推进到这个停稳点 | 会话启动 / 客户端输入 / 定时器到期 / 让路 drain / 致命错误 | **可否抑制** |

> 🔑 **「收到客户端输入」是轴 B 的一项，不是产出时机本身。** 代码上是清楚的——`StepAsync`
> 先等当前停稳点，再 `DispatchInput`，而 `ui.Invoke` 是**同步内联**的（见本节的宿主契约）⇒
> `DispatchInput` 返回时脚本已经跑到下一个停稳点，帧是在那儿产出的。

### 轴 A：停稳点的四种形态

| 形态 | 判据 | `awaiting` |
|---|---|---|
| **等输入** | `inputType != Void` | `input{type}`（可同时有 `wait`） |
| **定时等待** | `inputType == Void && Timelimit > 0` | `wait{durationMs>0}` |
| **让路** | 无参 `AWAIT` / `AWAIT 0` | `wait{durationMs:0}` |
| **终态** | `state ∈ {Quit, Error}` | `ended{terminal}` |

> 「这帧产自哪条指令 / 哪个时机」**根本不进 wire**（`InputRequest.IsYield` 只是服务端的
> drain 判据，永不序列化）。客户端在意的是「下一帧立刻到」（`durationMs:0`），不是那个
> 旗标叫什么。

### 轴 B：五种驱动

| # | 驱动 | 触发 | 可否抑制 | 理由 |
|---|---|---|---|---|
| **B1** | 会话启动 | 引擎首次到达停稳点 | 否 | 客户端还没见过任何帧，且要带 `protocolVersion` |
| **B2** | **客户端输入** | 收到 `{"type":"input",...}` | **否** | 必须回应（含被拒，见下） |
| **B3** | 定时器到期 | `InputTimeoutMs` 到期（超时续跑） | 是 | 引擎自己推进的；带 `wait.timedOut:true` |
| **B4** | 让路 drain | 宿主识别出让路点（`wait.durationMs == 0`） | 是 | 画面没变就不落 wire |
| **B5** | 致命错误 | MAUI fatal（**不经过脚本**） | 否 | 第二产出者，终态 |

一句话记住：**客户端发起的推进（B1/B2）必须回应；引擎自己发起的推进（B3/B4）可抑制；
终态（B5）永不抑制。**

> **B5 是轴 A 的例外**：它不经过脚本、不对应任何停稳点，直接构造
> `ended{terminal:"Error"}`。会话终结（`Quit`/`Error` 经脚本）则由 `BuildFinalTurn`
> 在 B1/B2/B3 任一驱动的帧里体现为 `ended`。

抑制判据（`BuildTurn`）：

```
产帧 ⟺ diff 非空
     ∨ awaiting 的【形状】与上一已产出帧不同（哪个键出现：input / wait / ended）
     ∨ input 的【性质】（type）与上一帧不同
     ∨ wait 的【性质】（durationMs + interruptible + displayTime + timeUpMessage）与上一帧不同
```

- 🔒 **比较「性质」不得比较「实例」**——每次等待都是新实例，比较实例会让抑制形同虚设。
- 🔒 **B1 / B2 / B5 永不抑制**：输入被拒（`error`）的帧画面恒为空、等待契约与上一帧完全
  相同，一旦让 B2 参与抑制，`error` 就永远送不到客户端（见下条）。

### 🔴 唯一例外：输入被拒时**没有新停稳点，帧仍必须发**

输入未被接受时（`OnInputRejected`）**不调 `RunEmueraProgram`**——`State` 仍是 `WaitInput`、
`CurrentRequest` 还是原来那个。于是这一帧：

- 画面未变（`diff` 为空）、`awaiting` 未变、**没有新停稳点**；
- 但它是「客户端发了一次输入」的唯一回音载体（`error` 非空）。

⇒ **「产出时机 = 到达停稳点」严格说不完备**：B2 的被拒分支是**驱动侧单独强制**的交付。
这是全表唯一的例外。客户端读到非空 `error` 时应理解为"这一次输入没被接受，画面与等待都没变"。

### 🔒 宿主契约：`IConsoleUI.Invoke` 必须同步执行完毕

```csharp
// EraCore.Core/Headless/HeadlessConsole.cs
public void Invoke(Action action) => action?.Invoke();
```

⇒ 脚本执行与帧生成**永远在同一个线程、同一个调用栈上**：让路 drain 期间没有任何人并发改动
画面，`BuildTurn` 的 `ComputeDiff` 不存在撕裂风险。**这与「脚本跑在自己线程上、外部观察」
相比，省掉了一整套锁与快照队列。**

⚠️ **这条契约是本节多处结论的前提**（两轴模型、让路 drain 的即时交付）。若未来某个宿主把
`Invoke` 改成异步投递，则 `BuildTurn` 可能取到撕裂画面——届时必须改走真线程 + 快照队列，
本节结论随之作废。实现 `IConsoleUI` 时必须保持同步语义。

> 该不变量对 **CLI 同样成立**：`CliGameLoop.RunLoop` 是同步轮询，脚本由 `DispatchInput` /
> `HandleTimeout` 在同一线程上推进，不存在第二个观察线程。

---

## 9. HTTP 接口与终止态

### 端点

| 端点 | 用途 |
|---|---|
| `POST /session` | 建会话，返回 `{sessionId, createdAt, state}` |
| `POST /control/acquire` | 取控制权。响应含 drain 后的末位 turn + `turnsAdvanced` |
| `POST /control/release` | 放控制权 |
| `GET /control` | 当前控制权状态 |
| `GET /control/wait` | 长轮询等控制事件（25s 超时 → 204） |
| `GET /turn` | 长轮询等一个 turn |
| `POST /input` | 提交输入，body `{"type":"input","value":"..."}` |
| `GET /state` | 会话状态 + 窗口布局元信息（`foreColor`/`fontName`/尺寸…） |
| `GET /snapshot` | 全量显示快照（晚加入者入口） |
| `GET /ws` | WebSocket，每帧一个裸 JSON 文本帧 |
| `DELETE /session` | 销毁会话 |
| `GET /config` | `maxLog`（前端履历容量） |

身份：空 token = 用户；非空 token = agent（token 即身份）。读取方式三选一——
`X-Control-Token` 头 / `Authorization: Bearer` 头 / `?token=` 查询参数。

### `GET /turn` 的终止态

长轮询可能以**四种**方式结束，客户端必须分别处理：

| 状态码 | 含义 | 客户端应对 |
|---|---|---|
| 200 | 拿到一个 turn | 应用帧 |
| 204 | 25s 超时，无新帧 | **继续轮询**（不是错误） |
| 404 | 会话已结束（`final turn` 已交付，或 Channel 关闭） | 停止轮询 |
| 409 | `CONTROL_LOST`——等待期间控制权易主 | 重新 acquire |

**404 的判定是"终局"的**：final turn 交付过一次后，后续所有 `/turn` 一律 404。
客户端收到 404 即认定会话终结。

控制事件的 `type` 取值：`acquired` / `released` / `stolen` / `lease_expired` /
`game_ended` / `session_replaced`。

### 单会话约束

进程内同时只有一个会话（`GlobalStatic` 隔离约束未解除）。`?sessionId=` 只是多会话
的预留扩展位，**v1 不实现**。

### `GET /ws` 契约

**上行**（客户端 → 服务端）：文本帧，`{"type":"input","value":"..."}`——**与
`POST /input` 的 body 完全同源**（`GameServerProtocol.BuildInputJsonl` 是两者共用的
构造函数）。`type` 非 `"input"` 的帧被静默忽略。输入前会过控制权门禁
（`Controller.CheckInput`），无权则**静默丢弃**——不回错误帧。

**下行**（服务端 → 客户端）：每回合一个裸 JSON 文本帧，无 envelope 包装
（[ADR-0003](../adr/0003-websocket-hub-bypass.md) 决策二——主流前端框架无需 envelope，
且让"WS 帧 = HTTP 响应体"这一可复用性成立）。

关闭码：

| 关闭码 | 含义 |
|---|---|
| `4004` | 无活跃 session，升级被拒（自定义码，借用"应用层错误"语义） |
| `1000` | 正常关闭——含 session 结束（`OutputHub.Complete`）的情形 |

**晚加入者不回放历史**：`Subscribe()` 返回全新 Channel，连接之前已产生的帧不可见。
必须先 `GET /snapshot` 重建画面（见 §6）。

---

## 10. 一致性要求

改动协议时，以下三处必须同步，否则会出现静默不一致：

1. **C# 实现**——`TurnRecord.cs` / `DisplayState.cs` 的 record 定义
2. **前端类型**——`EraCore.Web/src/types/protocol.ts`（手动维护）
3. **本文**——字段表与版本演进表

已知的现存偏差：**无**（v12 收缩后三处已对齐）。

另注：`docs/guides/agent-integration.md` 的 JSONL 示例是 v1 形态（`text`/`buttons` 字段早已被
`diff` 取代），已订正并指向本文。

**新增字段的规矩**：

- **纯增量 nullable 字段**（如 `errorInfo`）：`WhenWritingNull`（省略而非 null），**不升版本号**，
  旧客户端忽略即可。
- 🔒 **新增枚举取值必须升版本号**：给 `awaiting.ended.terminal`、`InputType` 之类**取值集合**
  加新值，或给 `awaiting` 加第三个维度键，都是**破坏性扩展**——客户端不认识这个取值时会走错
  分支（把终态当空闲、把新等待当无事可做），必须升 `protocolVersion` 并在本文版本演进表里
  加一行说明改了什么。
- 客户端遇到不认识的键 / 取值：**回退 `GET /snapshot`**，不得静默忽略（§2）。
