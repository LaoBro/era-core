# PRD: WebSocket 传输实现

> 对应 [架构评估报告.md](../2026.7.4.跨平台前置重构/架构评估报告.md) P0-2(Kestrel 替换)
> 中为 WebSocket "铺路" 的后续落地项。本 PRD 严格遵循 grill-me 阶段达成的共识
> （2026-07-08 设计审查，5 分支全收口，用户按推荐走）。

## Problem Statement

作为 Emuera.Headless 的未来跨平台 Web 前端开发者，我在对接 server 模式时只能用
HTTP 长轮询：

1. **实时性靠轮询**：`GET /turn` 是长轮询，Agent 层以 50ms 间隔 + 30s 超时做空
   轮询（见架构评估报告 2.5）。游戏长时间 `WAIT` 时持续产生无意义空轮询，延迟与
   CPU 开销都来自轮询模型本身。
2. **非双向**：输入走独立的 `POST /input`，输出走 `GET /turn`——两条单向 HTTP
   通道，客户端无法在一个持久连接上同时收发。
3. **Kestrel 已铺路但 WS 未施工**：P0-2 已完成（`HttpGameServer.cs` 内类已重写为
   `KestrelGameServer`，`UseKestrel()` + `Microsoft.AspNetCore.App` 已就位），但全
   仓库 `UseWebSockets`/`WebSocket` 零命中，WebSocket 能力仍是"已铺路未施工"。
4. **报告预设已证伪**：架构评估报告 2.3 称"新增 WebSocket 传输只需实现 `SessionIO`
   的 4 个方法"，但 `Session` 硬耦合 `HttpSessionIO` 且直接调用其专有方法
   `ReadOutputAsync`/`EnqueueInput`，抽象 `SessionIO` 上并无这些成员——平行写一个
   `WebSocketSessionIO` 无法零改动接入。

我需要的是一条**持久、双向、实时的传输**，且**不触动已稳定的协议层与游戏引擎层**。

## Solution

在现有 Kestrel server 上新增 `GET /ws` WebSocket 端点，与现有 HTTP 长轮询
`/turn`+`/input` **并存**、**共享同一输出流**：

- 新增 `OutputHub`（广播中枢）：游戏循环 `WriteLine(turn)` 时，既推给 HTTP 长轮询
  消费的 Channel，也广播给所有已连接的 WS 客户端。
- `HttpSessionIO` 仍是协议层用的那个 `SessionIO`（`Session` / `AgentJsonlProtocol`
  **一行都不动**）。`HttpSessionIO.WriteLine` 在写自身 `_output` Channel 的同时
  调用 `OutputHub.Publish(turn)`。
- WS 端点是挂在 `OutputHub` 上的**旁路传输**：连上后从 hub 订阅输出（每产生一个
  turn 推一帧），并把收到的输入帧通过 `HttpSessionIO.EnqueueInput` 喂进同一条输入
  Channel。WS 不是另一个 `SessionIO`。
- 线协议采用**裸 JSON 文本帧**：每个 turn 直接发一帧，内容就是现有
  `TurnRecord` v2 JSON（`ops[]` 操作序列模型，initial turn 带 `protocolVersion:2`，
  见 [PRD-T5 富 Turn 升级](../2026.7.4.跨平台前置重构/PRD-T5-富Turn升级.md) 与
  ADR-0002），与 `GET /turn` 响应体
  **字节一致**；输入帧复用 `{"type":"input","value":"..."}`。不包 envelope。

## User Stories

1. 作为 Web 前端开发者，我希望建立一个持久的 WebSocket 连接，这样 turn 能由服务端
   主动推送，我无需以 50ms 间隔轮询 `GET /turn`。
2. 作为 Web 前端开发者，我希望 WS 收到的 turn 帧与 `GET /turn` 的响应体是**同一份**
   `TurnRecord` v2 JSON（`ops[]` 模型），这样我可以直接复用为 HTTP 模式写的 op
   渲染器，无需为 WS 另写一套解析。
3. 作为 Web 前端开发者，我希望通过 WS 发送 `{"type":"input","value":"..."}` 帧即可
   提交输入，这样输入走与 HTTP `/input` 完全相同的格式与输入 Channel。
4. 作为观察者（围观者），我希望连上 `/ws` 后也能实时收到每一个 turn（输出扇出），
   即使我不是提交输入的控制者——结构上为多观察者预留，v1 允许任意 WS 客户端既收
   也发。
5. 作为 Headless 维护者，我希望 `Session` / `AgentJsonlProtocol` / 游戏引擎层**完全
   不被触碰**，这样 WS 传输是纯增量、回归风险集中在传输层。
6. 作为 Headless 维护者，我希望 HTTP 长轮询端点（`/turn` `/input` `/state`
   `/session`）继续工作，这样现有 HTTP 客户端与测试在 WS 上线后不 break。
7. 作为测试开发者，我希望有 WS 集成测试复用现有 HTTP 集成测试 seam，断言帧格式与
   扇出行为，使回归能验证 wire format。
8. 作为未来读者，我希望 PRD 记录"为何用 Hub 旁路而非平行 SessionIO"、"为何裸帧不
   包 envelope"、"为何 WS 与 HTTP 并存"——对应下方 Implementation Decisions 与
   [ADR]（待本 PRD 落地后补 ADR-0003）。

## Implementation Decisions

### 范围与模型决策

- **观察者模型 = 单控制器 + 输出可扇出**：一个 WS 连接充当唯一控制器提交输入；
  输出侧经 `OutputHub` 可扇出给多个观察者。v1 不区分"控制器/观察者"角色——任意
  WS 客户端既可收 turn 也可发 input（游戏回合制下输入自然串行化）。多观察者的真正
  "角色权限"留作以后。
- **与 HTTP 并存**：保留 `/turn`+`/input`，新增 `/ws`，两者共享同一 `OutputHub`
  输出流。HTTP 长轮询客户端本身也视为一个观察者。不删除、不替换 HTTP 端点。
- **接入方式 = Hub 旁路**（最小侵入）：`Session` 仍持有 `HttpSessionIO` 作为协议层
  `SessionIO`；`AgentJsonlProtocol` 仍通过 `_io.WriteLine` / `_io.ReadLineAsync`
  工作。**不**把 `Session` 改成依赖抽象 `SessionIO`，也**不**把 `ReadOutputAsync` /
  `EnqueueInput` 提到抽象层（那会波及 `WaitForTurnAsync` 与 `Session.IO` 返回类型，
  违背"协议/引擎不动"）。WS 是旁路：从 hub 读输出、向 `EnqueueInput` 写输入。
- **线协议 = 裸 JSON 文本帧**：每 turn 一帧，内容 = `TurnRecord` v2 JSON；输入帧 =
  `{"type":"input","value":"..."}`。不引入 envelope（`{"kind":...}` 包装）。理由：
  主流前端框架无需 envelope；版本协商已由 initial turn 的 `protocolVersion` 承载
  （ADR-0001），WS 客户端连上即拿到。

### 模块与接口变化

- **新增 `OutputHub`**：广播中枢。
  - `Publish(string turn)`：向所有订阅者推送一个 turn。
  - `Subscribe()`：返回一个 `ChannelReader<string>` 供新观察者消费（HTTP 长轮询的
    `_output` 订阅者 + 每个 WS 连接各一个订阅者）。
  - 取消订阅（WS 断开 / session 结束）从订阅者集合移除对应 reader。
  - `WriteLine` 写出的 turn 字符串即 `TurnRecord` v2 JSON（已是合法 JSON）。
- **修改 `HttpSessionIO`**：注入 `OutputHub`；`WriteLine(text)` 在原有
  `_output.Writer.TryWrite(text)` 之后追加 `_hub.Publish(text)`。`ReadLineAsync` /
  `Close` / `IsConnected` / `EnqueueInput` 不变。`Close()` 时通知 hub 移除其订阅者。
- **修改 `KestrelGameServer`**：
  - `builder` 构建后调用 `app.UseWebSockets()`。
  - 新增 `MapGet("/ws", handler)`：接受 WS 升级；从 `OutputHub` `Subscribe()` 取
    reader；启动**发送循环**（从 reader 读 turn → `WebSocket.SendAsync` 发一帧文本）
    与**接收循环**（读文本帧 → `JsonSerializer.Deserialize` 校验 `type=="input"` →
    `EnqueueInput`）。任一侧结束 / WS 关闭 → 取消另一循环并取消订阅。
  - server 需持有当前 session 的 `OutputHub` 引用供 `/ws` 访问（与现有 `_session`
    生命周期一致）。
- **`Session` / `AgentJsonlProtocol` / 游戏引擎**：**零改动**。

### Wire Format（契约）

#### 输出帧（server → client，每 turn 一帧，文本）

与 `GET /turn` 响应体字节一致，即 `TurnRecord` v2 JSON：

```json
{
  "state": "WaitInput",
  "inputType": "Int",
  "needValue": true,
  "protocolVersion": 2,
  "ops": [
    {"type":"print","segments":[{"text":"Hello ","color":"#FF0000","bold":true}],"button":{"value":1,"isInteger":true}},
    {"type":"newline","align":"center"}
  ]
}
```

Fatal turn：`ops: []` + `error`（与 PRD-T5 fatal 路径一致）。Session 结束：以 WS
**关闭帧**通知（无额外 payload）。

#### 输入帧（client → server，文本）

```json
{"type":"input","value":"1"}
```

与 HTTP `POST /input` 内部 `EnqueueInput` 的同一种 JSON；协议循环校验 `type=="input"`
（见 `AgentJsonlProtocol.RunLoopAsync`）。

#### 生命周期

- 客户端先 `POST /session`（与 HTTP 对称，启动游戏循环、拿到 `201`），再 `GET /ws`
  附着到该单会话。
- 单会话模型下 URL **不带 `?sessionId=`**（留多会话扩展口，但 v1 不实现）。
- 若无活跃 session，`/ws` 以关闭帧（建议 `WebSocketCloseStatus` 自定义 4004 或
  `InternalServerError`）退回，客户端应提示"请先创建会话"。
- **晚加入观察者只收"连接之后"的新 turn，不回放历史**（v1 最简）：`Subscribe()`
  返回全新 Channel，之前的 op 自然不可见。历史回放（需 op 日志/截断缓冲）留作以后。

## Testing Decisions

延续 PRD-T5 "只测 wire format，不测内部实现" 的哲学。

### 测试 Seam

复用现有**最高层 HTTP 集成测试 seam**（`tests/test_jsonl.py` 的启动 server + 游戏
目录模式），并新增**一个** WS 集成测试 seam（同进程内启动 `Emuera.Headless`，用 WS
客户端连 `ws://localhost:<port>/ws`）。不新增更低层 seam。

### 断言要点（新增 `tests/test_ws.py`）

- **连接前置**：未 `POST /session` 直接连 `/ws` → 连接被关闭（断言 close 状态）。
- **初始帧**：`POST /session` 后连 `/ws`，收到的首帧为 `TurnRecord` v2 JSON：
  `protocolVersion == 2`、`ops` 为 list 且非空、`"text" not in` / `"buttons" not in`。
- **帧格式一致**：同一局的 WS 首帧与 `GET /turn` 首帧**语义等价**（同样断言 `ops[]`
  结构），验证"WS 帧 = HTTP 响应体"。
- **输入往返**：发送 `{"type":"input","value":"..."}` 帧 → 收到下一 turn 帧（step
  turn 不带 `protocolVersion`）。
- **扇出**：同时开 2 个 WS 连接，提交一次输入后，**两个**连接都收到同一下一个 turn
  帧（验证 `OutputHub` 广播）。
- **HTTP/WS 并存**：一个 HTTP 长轮询客户端 + 一个 WS 客户端同时消费同一 session，
  两者均收到一致的 turn 流（验证共享 `OutputHub`）。
- **晚加入**：第二个 WS 在首个 turn 之后才连接 → 只收到后续 turn，不重复收到历史。
- **协议/引擎回归**：`tests/run_all.py` 全套通过，确认 WS 增量未破坏 server 单会话、
  TINPUT timeout、`test_jsonl.py` 等既有场景。

### 参考

- `tests/test_jsonl.py`（HTTP 集成 seam、v2 `ops[]` 断言写法）
- `tests/test_server_single_session.py`（单会话生命周期）
- `tests/run_all.py`（回归套件入口）

## Out of Scope

- **多会话 / 多游戏实例**：`GlobalStatic` 单会话硬约束不在本 PRD 解除（报告 2.3#1）。
  `?sessionId=` 仅留扩展口，不实现。
- **观察者角色 / 权限**：v1 不区分控制器与观察者，任意 WS 可收发。角色声明（如
  envelope 才能承载的 `role` 字段）不在范围。
- **历史回放缓冲**：晚加入者不回放，需 op 日志/截断时再立项。
- **envelope 包装**：不引入 `{"kind":...}` 层。
- **CLI 模式改动**：WS 仅 server 模式；CLI（`--protocol cli`）不受影响。
- **v1 降级路径**：v1 `text`/`buttons` 已由 PRD-T5 删除，WS 直接消费 v2，无降级。
- **LLM 兼容层**：同 PRD-T5，由前端层中转。
- **`HttpGameServer.cs` 文件名 → `KestrelGameServer.cs` 对齐**：属命名清理小债
  （类已重写但文件名未改），与工作量大、风险无关，记为独立 follow-up，不在本 PRD
  实施范围。

## Further Notes

### 与既有文档关系

- [架构评估报告.md](../2026.7.4.跨平台前置重构/架构评估报告.md) **P0-2**：本 PRD 是
  Kestrel "为 WebSocket 支持铺路" 的落地项；报告 2.3 的"只需实现 `SessionIO` 4 个
  方法"预设已被本设计证伪（改为 Hub 旁路）。
- [PRD-T5 富 Turn 升级](../2026.7.4.跨平台前置重构/PRD-T5-富Turn升级.md) /
  ADR-0002：WS 帧直接复用其
  `TurnRecord` v2 `ops[]` 模型——WS 是传输封装层，不重新定义载荷。
- ADR-0001：`protocolVersion` 仅
  initial turn 的约束，WS 客户端连上即获得，无需 WS 专属版本协商。
- 落地后建议补 **ADR-0003**：记录"Hub 旁路 vs 平行 SessionIO""裸帧 vs envelope"
  "WS/HTTP 并存" 三项决策与被拒绝的替代方案。已落地，见
  ADR-0003。

### 与 grill-me 设计审查关系

本 PRD 所有决策来自 2026-07-08 的 grill-me 五分支审查（观察者模型 B / 并存 A /
Hub 旁路 A / 裸帧 A / 生命周期 5a+5b 按推荐），是审查结论的结构化契约，不含新信息。

### 实施前验证

实施者应在落地代码后、运行测试前验证：

1. `dotnet build Emuera.Headless/Emuera.Headless.csproj -c Debug` 成功，无新增 error。
2. `python tests/test_ws.py --binary <path> --game-dir test_game` 全部通过。
3. `python tests/run_all.py --binary <path> --game-dir test_game` 全套通过。
