# EraCore.Core 架构：抽象层与不变量

本文是 `EraCore.Core` 的**层图与边界**：模块对外有哪些接触面、谁在实现、谁在消费、哪些规则必须守住。
目标是一张能五分钟看懂的地图，让接手的贡献者知道该去哪里继续读——**不代替代码，也不代替 ADR**。

**信息分层**（防止本文重新变胖）：本文只管**结构**——「长什么样、边界在哪」（不写历史、不写行号、不抄清单）；
**为什么**归 [`../docs/adr/`](../docs/adr/)（本文只点名主 ADR）；**怎么实现**与例外清单归类型 remarks 和守卫测试（机读真值唯一）。

> 本文是根 [`../ARCHITECTURE.md`](../ARCHITECTURE.md) `## Core` 一节的深化。
> 维护约定：改动任一轴的主接口时同步 §1 / §5。最后核对日期：**2026-10-22**。

---

## 1. 三条能力轴总览

Core 把「引擎与世界的全部接触面」收敛为三条轴，它们**方向不完全对称**：

- **输入 / 文件**是**依赖倒置面**：接口定义在 Core，**宿主实现**，引擎只读。
- **输出**是**产出面**：**引擎产出**（快照 / 广播），宿主消费。

```text
     ┌──────────── 依赖倒置面（宿主实现 → 引擎消费）────────────┐
     输入：InputPort（宿主门面）→ IInputHost / IVtHost / ITerminalInput  →  引擎只读
     文件：GameTree（宿主门面）→ IGameDirAccessor          →  引擎的全部游戏树 IO
     └──────────────────────────────────────────────────────────┘

     ┌──────────── 产出面（引擎产出 → 宿主消费）───────────────┐
     输出：OutputPort（快照 + 增量）→ DisplayState 快照 / OutputHub 广播
     └──────────────────────────────────────────────────────────┘
```

> **方言层不是第四条能力轴。** 它是 `Shared/Runtime/Script/Statements/` 内注册表的会话级投影
> （profile → 可见面），不增加输入/输出/文件的新接触面；结构、闭包与不变量见
> [`../docs/guides/dialect-porting.md`](../docs/guides/dialect-porting.md)。

三轴的**宿主门面**（`InputPort` / `OutputPort` / `GameTree`）与组装根 `CoreSession` 是 `public`：
宿主只认这些类型。门面背后的接口与实现（`IInputHost` / `DisplayState` / `OutputHub` / `GameLoopComposer` /
`SessionRegistry` 等）保持 `internal`；`EraCore.Core.csproj` 的 `InternalsVisibleTo`
（`EraCore.Server` / `EraCore.Cli` / `EraCore.Maui` / `EraCore.Tests`）只服务于本仓宿主接入与测试。
`IGameDirAccessor` 仍是唯一需要外部实现的 `public` 接口。

**会话不是第四条能力轴**，而是三轴的**汇合点**：`CoreSession` 持有 `Input` / `Output` / `Files` 三个门面，
负责 `StartAsync`（路径校验 → 运行时初始化 → 游戏循环）与 `StopAsync`（有界拆除）。
宿主从 `StartAsync` 到 `StopAsync` 之间只接触三轴；HTTP / JSONL / WS 协议仍属于 Server，不进组装根。

---

## 2. 输入轴

**`IInputHost`** 是统一输入宿主：**宿主实现，引擎只读**。引擎侧经 `IConsoleUI.Input` 拿它，读到的
`InputSnapshot` 是**不可变快照**，宿主只能以 `Volatile` 整体换引用。宿主能做什么由静态能力位
`InputCapabilities`（`Geometry` / `Hover` / `KeyboardState` / `Focus`）在构造期声明——运行期不变。

与这个 pull 面**分居两侧**的是 push 面：上行输入先落成 **`InputStateEvent`**（两个 wire 入口 —— WS 协议帧 /
HTTP body —— 的共同终点），再由 **`InputStateApplier`** 翻译成宿主状态并统一做能力位门卫。

视口（`IViewportProvider`）是**渲染侧概念**，不并入输入宿主；`IVtHost` / `ITerminalInput` / `ITerminalSetup` /
`IWindowSize` 是 CLI 侧的平台裸终端能力。这些类型的成员语义见 `Shared/UI/Game/` 与 `Terminal/` 下各自的 remarks。

### 2.3 不变量

| # | 规则 | 违反后果 |
|---|---|---|
| I-1 | 引擎只读 `InputSnapshot`，宿主**整体换引用**，不得逐字段写 | 同一次 `WaitInput` 内高频轮询读到撕裂的坐标/按键 |
| I-3 | `ConsumeTrigger` **严格照抄原版** `(s < 0) && (toggle != keytoggle[vk])`，不做时间窗口、不做延时失效 | 脚本可观测行为偏离原版（曾被放宽后回退） |
| I-5 | **core 不模拟、不补偿、不放宽**：宿主能力缺失时由**宿主**自选降级策略，core 只忠实表达 | 为一个宿主污染全生态的输入语义 |

> 其余输入侧约束（能力位静态、`ConsumeTrigger` 是唯一有副作用成员、`ResetState` 时机、视口不并入 `IInputHost`）落在各类型 remarks 与 ADR-0031。

### 2.4 主决策

- [ADR-0031 统一输入抽象层](../docs/adr/0031-unified-input-abstraction-layer.md) —— proposed，本轴主决策。
  相关：`ADR-0010`（`IVtHost` 雏形）、`ADR-0009`（Scroll 抽出）、`ADR-0021`（几何通道落地时的重开条件）。
  完整索引见 [`../docs/adr/`](../docs/adr/)。

---

## 3. 输出轴

输出有两条**结构不同**的通道，共用同一 turn 串：

- **拉取式快照**（`OutputPort.Snapshot`）：引擎持有显示状态，宿主按需 `TryUpdate()` / `Current`。
- **推送式广播**（`OutputHub`）：turn 写出即广播给所有旁路订阅者（WS），是**唯一**旁路出口，**不设接口层**。

一次 turn 走过的关键角色：**`TurnFrames`**（帧形状的唯一权威，纯静态、不持运行期状态）→
**`HttpSessionIO`**（写同时入 HTTP 队列与广播）→ **`TurnDelivery`**（长轮询 + 控制权抢占 + final-turn-once + 超时）
→ **`WsRelay`**（WS 传输，Core 共享、BCL only）。帧的 wire 契约与差分操作见 `Agent/TurnRecord.cs`；
会话相位与拆除顺序由 `CoreSession` / `SessionRegistry` / `TurnDelivery` 持有（ADR-0049）；
宿主经 `CoreSession.Output` 拿到 `OutputPort`，看不到 `DisplayState` / `OutputHub`。

### 3.3 不变量

| # | 规则 | 违反后果 |
|---|---|---|
| O-1 | `OutputHub` **纯转发**：不解析 turn、不持业务状态、不依赖 `CoreSession` / 协议层 / 引擎层 | 旁路耦合进业务 |
| O-3 | `OutputHub` 是**唯一**旁路广播出口且**不设接口层**（理由见该类型 remarks）。若真出现第二个投递目标，须在此重开讨论后再引入抽象 | 抽象面 1 处 vs 具体面 4 处，「已解耦」的假象复活却换不来任何测试能力 |
| O-4 | `WriteLine` 必须**同时**入 HTTP 队列与旁路广播，两侧**同序、同一字符串实例**，不得在任一侧再加工 —— 行为锁 `HttpSessionIoDualWriteTests` | HTTP 与 WS 输出分叉（症状：WS 下按钮不刷新而 HTTP 下正常，最难归因） |
| O-6 | 协议版本变更须**同步**更新 `TurnRecord.CurrentProtocolVersion` 与 Python 侧 `tests/emuera_server.py:PROTOCOL_VERSION` | 前后端版本错配 |
| O-7 | 生产代码**只能**经 `TurnFrames`（`Ok` / `Quit` / `Fatal` / `Error`）构造帧；终态帧只能经 `Quit` / `Fatal` / `Error` 三个语义入口 | 「Quit 帧带 diff」「错误帧照抄 `console.State`」等非法形态复活 |

> 其余输出侧约束（订阅者独占 reader 且不回放历史、`CoreSession` 只做组装不参与取水、`TurnFrames` 不持运行期状态）落在 `OutputHub` / `TurnDelivery` / `TurnFrames` 的 remarks 与 ADR-0032。

### 3.4 主决策

- [ADR-0032 回合帧形状收口：TurnFrames 作为唯一形状权威](../docs/adr/0032-turn-frame-shape-authority.md) —— implemented，本轴形状权威。
  相关：`ADR-0001`（turn 协议版本化）、`ADR-0002`（v2 操作序列）、`ADR-0013` / `ADR-0014`（DisplayState 快照路由与统一真相源）、
  `ADR-0003`（WS Hub 旁路）、`ADR-0020`（控制权交接）、`ADR-0049`（会话相位与拆除顺序）。
  完整索引见 [`../docs/adr/`](../docs/adr/)。

---

## 4. 文件轴

`IGameDirAccessor` 是**唯一**的游戏目录访问抽象，也是 Core 里唯一 `public` 的能力轴接口，接口头部硬性声明：

> 「所有文件 IO 必须通过此接口，禁止直接调用 `File.*` / `Directory.*`。」

两个实现：`FileSystemGameDirAccessor`（本地文件系统）与 `SafGameDirAccessor`（Android SAF `content://`，MAUI 侧），
二者必须**契约一致**（精确名读取大小写不敏感，对齐 SAF 语义）。主要消费者：`GamePaths`（解析/校验游戏根）、
`GameLibrary`（扫描）、`DirectoryLister`。宿主经 `GameTree`（绑定游戏根 + 访问实现的相对路径门面）访问游戏目录；
目录选择器属于 UI 关注点，不在 IO 契约内。

应用私有域是唯一的**豁免域**：由 `AppDataPaths` 界定、经 `AppDataIo` 这一个窄口子 IO（方法只收「相对私有根的名字」，
越域在签名上不可表达）。

**游戏根由根环境承载**（issue-007）：`GamePaths.Current` 不再是可写的进程级单例字段，而是
`GameRootEnvironment` 的只读视图——**作用域层**（`AsyncLocal`）由组装根 `CoreSession` 在游戏循环流内建立、
循环退出即销毁；**装配期视图**（进程级槽位）由 `GamePaths.Resolve` / `SetCurrent` 提交，只服务尚未迁移的读者
（HTTP 流上的 `/assets`、MAUI UI 线程、既有测试脚手架）。读序：作用域层 → 装配期视图。
issue-008 把引擎调用点迁到显式根，issue-009 删掉装配期视图。

### 4.3 不变量

| # | 规则 | 违反后果 |
|---|---|---|
| F-1 | **游戏树**内全部文件 IO 必须经 `IGameDirAccessor`；禁止直接 `File.*` / `Directory.*` | Android SAF 下静默失败、平台差异泄漏进引擎 |
| F-4 | 应用私有域是**唯一豁免域** —— domain 由 `AppDataPaths`（+ `AppDataIo` 窄口子）显式界定，经 `AppDataPaths.IsUnder` 机检 | 豁免边界回到「靠开发者记得」的隐式状态 |
| F-5 | 例外清单的**唯一真值是守卫测试 `GameDirAccessorIoPolicyTests` 的 `KnownDirectIo`**（逐文件、逐 Cap 盯住，并检测陈旧登记）；新增 IO 立即判「未登记」转红。**本文不复制该清单** | 例外蔓延成事实上的第二套 IO |
| F-6 | 游戏根**只能经 `GameRootEnvironment` 写入**：`Establish` 的生产调用点唯一（`Sessions/CoreSession.cs`，由守卫测试机检），`GamePaths.Current` 无 setter、`GamePaths` 内不得再有根单例字段 | 拆除期间的旧循环读到新根，去新目录找 ERB/CSV（issue-001 的故障站点） |

> GameTree 域已归零；剩余豁免均为平台 / 私有域性质（访问器底层实现、accessor 上任之前的探测、进程工作目录），逐条理由写在上述测试的登记里。收敛过程见 ADR-0019 及后续各 ADR 与 Git 历史。

### 4.4 主决策

- [ADR-0019 Android SAF 文件访问替代 MANAGE_EXTERNAL_STORAGE](../docs/adr/0019-android-saf-file-access.md) —— accepted，本轴主决策。
  相关：`ADR-0017`（MAUI 桥接传输分层，含 `GameLibrary` / `DirectoryLister`）。
  完整索引见 [`../docs/adr/`](../docs/adr/)。

---

## 5. 宿主侧接触点

| 宿主 | 声明的输入能力位 | 输入 | 输出 | 文件 | 方言 profile |
|---|---|---|---|---|---|
| **Cli** | `Geometry \| KeyboardState` | 经 `CoreSession.Input`（`CreateForCli` 组装根；VT `AgentCliProtocol` 同步派发） | 经 `CoreSession.Output`（`AgentCliProtocol` + `TerminalRenderer`；视口 `CliViewport`） | 经 `CoreSession.Files`（`FileSystemGameDirAccessor` + `GamePaths`）；入口 `Resolve` 提交根，会话在循环流内建立作用域 | `--profile`（启动期 fail-fast；缺省 `v24pure`） |
| **Server / HTTP** | `KeyboardState \| Hover \| Focus` | 经 `CoreSession.Input`（会话持有的 `DefaultInputHost`；value 由门面编码成 input JSONL 入队） | Kestrel 壳路由 + `WsConnectionHandler` → `WsRelay`；`CoreSession.Output` + `HttpSessionIO` 双写 | 请求流无会话作用域：`/assets` 经活动会话的 `CoreSession.Files`（`DirAccessor` + `Root`），`/state` 的 `gameDir` 同源（issue-008） | 同 Cli（`ServerRunner` 透传） |
| **Maui** | 复用 Server 同一 `DefaultInputHost` | 同 Server（经 `CoreSession.Input`） | `BridgeHost.TurnPumpAsync` 订阅 `OutputHub`（会话由 `CoreSession` 持有） | `BridgeHost.ResolveDirAccessor`：`content://` → `SafGameDirAccessor.Instance`，否则 `FileSystemGameDirAccessor`；UI 线程读装配期视图（issue-009 迁移点：宿主改持显式根） | 设置项 `app.dialectProfile`（建会话前读，重载游戏生效） |

---

## 6. 落地状态快照（截至 2026-10-22）

| 轴 | 主接口 | 状态 |
|---|---|---|
| 输入 | `InputPort` / `IInputHost` | ADR-0031 proposed；`InputPort` 门面与 `InputFrame` 已落地并接入 CLI/server；接口 / `DefaultInputHost` / 几何与键盘通道已落地，触屏 E-min 与 `IConsoleUI` 瘦身未落地 |
| 输出 | `OutputPort` / `OutputHub` | ADR-0003 / 0013 / 0014 accepted 且已落地；帧形状由 `TurnFrames` 收口（ADR-0032），宿主经 `OutputPort` 读快照/增量 |
| 文件 | `GameTree` / `IGameDirAccessor` | ADR-0019 accepted 且已落地（双实现）；`GameTree` 相对路径门面已落地，选择器移出 IO 契约；剩余豁免由守卫测试机检 |
| 会话 | `CoreSession` / `CoreSessionOptions` | issue-006 落地：公共组装根（`StartAsync` / `StopAsync` + 三轴）；CLI/server/MAUI 复用同一实现；公共面白名单守卫 `SessionsPublicSurfaceTests` |
| 游戏根 | `GameRootEnvironment`（internal，非宿主面） | issue-007 expand + issue-008 migrate 已落地：会话作用域根环境（`CoreSession` 建立 / 循环退出销毁）+ 装配期视图；引擎与服务端已无 `GamePaths.Current` 代码引用（守卫机检），`Program.*` 是唯一兼容转发垫片。装配期视图、`GamePaths.Current` 与 MAUI 宿主/测试读点由 issue-009 删除 |

> 本表随里程碑推进而改，不视为稳定契约。

---

## 7. 相关文档

- 仓库总览：[`../ARCHITECTURE.md`](../ARCHITECTURE.md)
- 方言移植指南：[`../docs/guides/dialect-porting.md`](../docs/guides/dialect-porting.md)
- 方言键清单：[`../docs/reference/dialect-surface.md`](../docs/reference/dialect-surface.md)
- 架构决策记录：[`../docs/adr/`](../docs/adr/)
- 前端导航：[`../EraCore.Web/README.md`](../EraCore.Web/README.md)
- 测试说明：[`../tests/README.md`](../tests/README.md)
- 终端行为教训：[`../docs/LESSONS/terminal-windows.md`](../docs/LESSONS/terminal-windows.md)
