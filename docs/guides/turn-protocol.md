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

`TurnRecord` 有 5 类字段：画面状态、输入需求、计时、诊断、协议元信息。

序列化用 `JsonSerializerOptions.DefaultIgnoreCondition = WhenWritingNull`——
**nullable 字段为 null 时字段整个不出现**（不是写 `"x": null`）。下表"省略"列即为此意。

| 字段 | 类型 | 必现 | 说明 |
|---|---|---|---|
| `state` | string | 是 | 引擎状态。见 §3 状态表 |
| `inputType` | string | 否 | 输入需求类型。见 §3 输入类型表。null/省略 = 本帧不请求输入 |
| `needValue` | bool | 是 | 是否需要值输入。为 true 时 `inputType` 必为 `IntValue`/`StrValue`/`AnyValue`/`PrimitiveMouseKey`/`IntButton`/`StrButton` |
| `diff` | object | 否 | 行级增量。见 §4。null/省略 = **画面完全未变**（纯状态切换，或空转帧被抑制） |
| `error` | string | 否 | 输入拒绝原因（如"当前需要整数输入，请重试"）或脚本异常消息 |
| `errorInfo` | object | 否 | 结构化异常（`type`/`message`/`inner?`，`inner` 最深 4 层）。见 §7 |
| `protocolVersion` | number | **仅首帧** | 协议版本。仅 initial turn 携带，后续帧省略（见 §6） |
| `timeLimit` | number | 否 | TINPUT/定时等待的总时长（ms）。仅 `Timelimit > 0` 时出现 |
| `displayTime` | boolean | 否 | 是否向玩家显示倒计时。ERB 可设 false 抑制。省略 = 显示 |
| `timeUpMessage` | string | 否 | 超时提示文案 |
| `timedOut` | boolean | **是** | 本帧是否由超时触发。**非 nullable**，每帧都出现（默认 false） |
| `generation` | number | **是** | 按钮代号，用于按钮过期检测（见 §5） |

> `timedOut` 与 `generation` 是仅有的两个"非 nullable 且非必填语义"的字段——
> 它们每帧都出现，前端无需处理 undefined。

### `awaiting` —— v12 的唯一契约字段（展开阶段：与上表旧字段并存）

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

**`wait` 的三态（写死，不得再加第四个取值）：**

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

> ⚠️ **展开阶段（issue 05）**：`awaiting` 入网时，上表里的 `state`/`inputType`/`needValue`/
> `timeLimit`/`displayTime`/`timeUpMessage`/`timedOut` 七个顶层字段**暂时保留**（现有前端保持全绿），
> 它们与 `awaiting` 同源。收缩阶段（issue 09）删除后，`awaiting` 成为唯一契约字段。
>
> ⚠️ `GET /snapshot` 也带 `awaiting` 且**形状相同**（见 §6）；但快照可能在"脚本还在跑"的瞬态被
> 取到 —— 那时两维都无值（`awaiting:{}`，由仍保留的 `state` 字段区分）。流上的帧只在**停稳点**
> 产出，故"至少一维必现"只约束流上的帧。
>
> 致命错误帧（MAUI `BridgeHost.ShowError`，不经过脚本）同样是终态形状
> `{"awaiting":{"ended":{"terminal":"Error"}}}`，见 §7。

### 示例：initial turn

```json
{
  "state": "WaitInput",
  "inputType": "IntValue",
  "needValue": true,
  "awaiting": { "input": { "type": "IntValue" } },
  "diff": null,
  "protocolVersion": 12,
  "timedOut": false,
  "generation": 0
}
```

### 示例：带画面增量的 step turn

```json
{
  "state": "WaitInput",
  "inputType": "AnyKey",
  "needValue": false,
  "awaiting": { "input": { "type": "AnyKey" } },
  "diff": {
    "lineOps": [
      { "type": "append", "newLines": [ /* DisplayLine[]，见 §4 */ ] }
    ],
    "bgColor": "#101010",
    "bgImages": null
  },
  "timedOut": false,
  "generation": 3
}
```

---

## 3. 状态与输入类型

### `state`（`ConsoleState`）

| 值 | 含义 | 客户端应对 |
|---|---|---|
| `Running` | 脚本执行中 | 不可交互；等待下一帧 |
| `WaitInput` | 停在输入请求上 | 按 `inputType` 决定 UI形态 |
| `Quit` | 脚本 QUIT/EXIT 正常退出 | 会话终结，停止轮询 |
| `Error` | 脚本或引擎异常 | 读 `error`/`errorInfo` 展示；会话终结 |
| `Loading` | 会话已建、游戏循环未启动完 | 仅 `GET /state` 返回，**不出现在 turn 里** |

> `Initializing`（=0）在正常路径下不会成为帧内容。
> 历史上的 `Sleep`（=21）已删除——它曾承载 AWAIT，现已改走 `WaitInput`（见 §8）。

### `inputType`（`InputType`）

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
缺省（不可打断）时不启用。旧顶层字段 `state`/`inputType`/`timeLimit` 的组合推断方式
在收缩阶段（issue 09）删除。

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

演进策略是**纯增量**：新字段一律以 nullable 形式加入，旧客户端忽略未知字段自然降级。
因此每个新增字段都不升版本号，只有**语义变更或破坏性改动**才升。

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
| v12 | `awaiting` 容器（`input` / `wait` 两个正交维度 + `ended` 互斥终态）取代"用 `state`+`inputType`+`timeLimit` 反向推断"；`wait.interruptible` 让 `Skippable` 首次上 wire。**展开阶段**七个旧顶层字段暂时保留，issue 09 删除 |

> v2/v3 属重构期过渡态，未在生产协议中留存。

### `DisplaySnapshot`（`GET /snapshot`）

全量快照给**晚加入者**用（WS 连上时错过的帧不可回放——ADR-0003 明确"晚加入者不回放历史"）。

```json
{
  "lines": [ DisplayLine ],
  "bgColor": "#101010",
  "bgImages": [ BgImageState ],
  "state": "WaitInput",
  "inputType": "AnyKey",
  "needValue": false,
  "awaiting": { "input": { "type": "AnyKey" } },
  "protocolVersion": 12,
  "generation": 3,
  "timeLimit": null
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

> 初始化阶段的异常不走 `BuildTurn`，而是直接写一个手拼的 `JsonObject`
> （仅 `error`/`errorInfo`/`state`），见 `Session.DeliverInitStageFailure`。

---

## 8. 帧的产出时机（当前实现的真相）

这一节记录**现状**，包含一处已知的设计债。

### 四条产出路径

| 路径 | 触发 | 是否可抑制 |
|---|---|---|
| initial turn | 会话建立后引擎首次稳定 | 否（携带 `protocolVersion`） |
| **应答帧** | 客户端发来一次 `POST /input` | **否**——客户端发了输入就必须有回音 |
| **超时帧** | `Timelimit` 到期，走超时续跑 | **可**——若 diff 为空且状态指纹未变则不产出 |
| final turn | 游戏循环退出（Quit/Error） | 否 |

### AWAIT：借道输入通道的绕行

`AWAIT t`（回合内动画/渐变等待）**本不属于上表任何一条路径**。参考实现里它是
`Thread.Sleep` + 休眠态——但休眠态不触发回合边界，等待期间画面完全不外送，
导致 eraTW 的背景色渐变表现为"卡住一阵然后突然跳变"、进度条全程静止。

现有实现的对齐落点是 **`TWAIT t,1`**：`AWAIT` 进入 `WaitInput`（`InputType.Void` +
`Timelimit` + `Skippable`），等待期满后由宿主循环走**既有的超时续跑路径**继续脚本
（server：`RunLoopAsync` 的 `CancelAfter` → `SubmitTimeout`；CLI：挂钟比对）。

参考实现同样是 Sleep，但它在 WinForms 里是**直绘**（`RefreshStrings` +
`ProcessEvents` 同步重绘）。无头移植里"重绘"的等价物就是"交付一个回合"——
所以等待输入态是原语义在此处的正确落点。**这不是随意缝合，而是有依据的等价映射。**

### 由此产生的设计债

1. **帧产出被绑死在"等输入 + 超时"这条为 TINPUT 设计的通路上。** `AWAIT` 想要的是
   "时间到了，该重绘了"，却只能表达成"我假装在等一个永不来的输入，然后超时"。
2. **`InputRequest.Skippable` 是给错误抽象打的补丁**——参考实现里 `Void` 的语义是
   "入力不能、待つしかない"（`PressEnterKey` 对 `Void` 直接 return），所以"天然可接收
   输入"并不成立，需要额外标记才能实现可打断。
3. **`Void` 承载两种语义且 wire 上无法区分**（见 §3）。
4. **`BuildTurn` 的空转帧抑制依赖上述假设**——其注释明确写着空转帧"**只**产自超时续跑
   路径"。协议层已经开始依赖这个实现细节，若新增帧来源需同步复核。

### 升级方向（未实施）

给帧加**显式的类别字段**，让 `AWAIT` 走自己的类别而不是伪装成输入超时：

```
frameKind: "response" | "tick" | "final"
```

- `response`——客户端输入的回音（永不抑制）
- `tick`——脚本自主推进的中间帧（空转可抑制；`AWAIT` 属此类）
- `final`——会话最后一帧

届时 `Skippable` 可还原为 `AWAIT` 自身的属性，`Void` 的双重语义也自然拆开。
**在此之前，`AWAIT` 路径的行为依赖上述实现细节，改动 `BuildTurn` 的抑制逻辑时必须
同步复核这条路径。**

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

已知的现存偏差（本文与实现的差异，以本文为准去订正实现侧注释）：

- `protocol.ts` 的 `DisplaySnapshot` 缺 `errorInfo` 相关说明，且注释中协议版本仍写 "v7"
  （实际 v12）。
- `docs/guides/agent-integration.md` 的 JSONL 示例仍是 v1 形态（`text`/`buttons` 字段
  早已被 `diff` 取代），已订正并指向本文。

**新增字段的规矩**：一律 nullable + `WhenWritingNull`（省略而非 null），不升版本号，
旧客户端忽略即可。只有语义变更或破坏性改动才升 `protocolVersion`，且必须在本文的
版本演进表里加一行说明改了什么。
