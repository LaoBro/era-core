# EraCore.Core 架构：抽象层与不变量

本文档是 `EraCore.Core` 的**层图与不变量索引**，回答三个问题：

1. 引擎与外部世界接触的全部接口是什么？
2. 谁在实现它们、谁在消费它们？
3. 哪些规则必须守住（可逐项审计）？

**边界**：

- **决策的动机、备选方案与权衡不在本文** —— 以 [`../docs/adr/`](../docs/adr/) 的 ADR 为准，本文只给结论与指针。
- 仓库级项目边界与运行时流程见根 [`../ARCHITECTURE.md`](../ARCHITECTURE.md)；本文是它 `## Core` 一节的深化。
- 迁移历史见 `docs/` 下的专题文档。

**维护约定**：改动任一接口 / 增删实现 / 变更落地状态时，同步更新本文对应小节与第 6 节状态快照。
最后核对日期：**2026-10-16**。

---

## 1. 三条能力轴总览

Core 把「引擎与世界的全部接触面」收敛为三条能力轴。它们**方向不完全对称**：

- **输入 / 文件**是**依赖倒置面**：接口定义在 Core，**宿主实现**，引擎只读。
- **输出**是**产出面**：**引擎产出**（快照 / 广播），宿主消费。

```text
     ┌──────────── 依赖倒置面（宿主实现 → 引擎消费）────────────┐
     输入：IInputHost / IVtHost / ITerminalInput   →  引擎只读 Current / Capabilities
     文件：IGameDirAccessor                        →  引擎的全部游戏树 IO
     └──────────────────────────────────────────────────────────┘

     ┌──────────── 产出面（引擎产出 → 宿主消费）───────────────┐
     输出：DisplayState 快照（拉取）  /  OutputHub 广播（推送）  →  宿主渲染 / 旁路订阅
     └──────────────────────────────────────────────────────────┘
```

三条轴共同的实现约束：**接口在 Core 内为 `internal`**，宿主程序集经 `EraCore.Core.csproj` 的
`InternalsVisibleTo`（`EraCore.Server` / `EraCore.Cli` / `EraCore.Maui` / `EraCore.Tests`）访问。
唯一例外是 `IGameDirAccessor`（`public`）—— 见第 4 节。

---

## 2. 输入轴

### 2.1 接口与类型

| 类型 | 语义 | 定义 |
|---|---|---|
| `IInputHost` | 统一输入宿主。**宿主实现、引擎只读**，与 `IVtHost` 同构的依赖倒置 | `Shared/UI/Game/IInputHost.cs:138` |
| `InputCapability` / `InputCapabilities` | **静态**能力位（`Geometry` / `Hover` / `KeyboardState` / `Focus`），构造期声明、运行期不变 | `Shared/UI/Game/IInputHost.cs:16,33` |
| `InputSnapshot` | **不可变**输入快照（pull 面），宿主以 `Volatile` 整体换引用 | `Shared/UI/Game/IInputHost.cs:109` |
| `KeyStateSet` | 键盘电平 + 版本号（`GETKEY` / `GETKEYTRIGGERED` 的操作数） | `Shared/UI/Game/IInputHost.cs:61` |
| `IViewportProvider` | 视口状态（**渲染侧概念，与输入分离**） | `Shared/UI/Game/IInputHost.cs:181` |
| `InputStateEvent` | **上行**输入事件（push 面）：两个 wire 入口（`JsonlCommand` 协议帧 / `HttpInput` HTTP body）的共同终点。与 pull 面的 `InputSnapshot` 分居两侧 | `Shared/UI/Game/InputStateEvent.cs` |
| `InputKeyEvent` / `InputGeometry` | 上行状态的 wire 载荷（VK 键码 + down / console 局部坐标），两个 wire 入口共用 | `Shared/UI/Game/InputStateEvent.cs` |
| `InputStateApplier` | 上行输入翻译**深模块**（与下行 `TurnFrames` 对称）。两个适配器 = 真接缝：wire 入口 `Apply` + CLI 指针入口 `ApplyPointerGeometry/ApplyPointerKey`；**能力位门卫在此统一** | `Shared/UI/Game/InputStateApplier.cs` |
| `IVtHost` | VT 输入宿主窄接口（CLI 侧原语分发） | `Terminal/IVtHost.cs:11` |
| `ITerminalInput` / `ITerminalSetup` / `IWindowSize` | 平台裸终端能力（读字节 / ANSI / 尺寸） | `Terminal/Platform/ITerminalInput.cs`、`ITerminalSetup.cs`、`Terminal/IWindowSize.cs` |

引擎侧经 `IConsoleUI.Input`（`Shared/UI/Game/IConsoleUI.cs:61`）与 `IConsoleUI.Viewport`（同文件 `:64`）访问宿主。

### 2.2 实现

| 实现 | 位置 | 用途 |
|---|---|---|
| `DefaultInputHost` | `Shared/UI/Game/IInputHost.cs:246` | 生产默认实现（可变、跨线程安全），ctor 默认能力位 `KeyboardState` |
| `NullInputHost` | `Shared/UI/Game/IInputHost.cs:214` | 无能力单例，构造缺省回落到它 |
| `HeadlessConsole`（`: IConsoleUI, IViewportProvider`） | `Headless/HeadlessConsole.cs:10` | 无头 console；视口经 `AttachViewport` 后置注入 |
| `CliViewport`（`: IViewportProvider`） | `Terminal/CliViewport.cs:22` | CLI 视口，组合 `ScrollController` + `TerminalViewportGeometry` |
| `WindowsTerminalInput` / `PosixTerminalInput` | `Terminal/Platform/` | `ITerminalInput` 平台实现（CLI 构造） |
| `WindowsTerminalSetup` / `PosixTerminalSetup` | `Terminal/Platform/` | `ITerminalSetup` 实现（`EmueraRuntimeInitializer` 选择） |

能力位的**实际声明点**（宿主在装配会话时给出）：

| 宿主 | 能力位 | 位置 |
|---|---|---|
| CLI | `Geometry \| KeyboardState` | `EraCore.Cli/HeadlessRunner.cs:82` |
| Server / HTTP | `KeyboardState \| Hover \| Focus` | `Server/Session.cs:79-80` |
| MAUI | 复用 Server 的同一 `DefaultInputHost`，无独立声明 | — |

### 2.3 不变量

| # | 规则 | 违反后果 |
|---|---|---|
| I-1 | 引擎**只读** `InputSnapshot`，宿主**整体换引用**（`Volatile`），不得逐字段写 | 同一次 `WaitInput` 内高频轮询读到撕裂的坐标/按键 |
| I-2 | `IInputHost.ConsumeTrigger` 是接口上**唯一有副作用**的成员（读到即失效）；**不得**放进不可变快照 | 破坏「快照是纯 pull」语义 |
| I-3 | `ConsumeTrigger` 语义**严格照抄原版** `(s < 0) && (toggle != keytoggle[vk])`，**不做**时间窗口、不做延时失效 | 脚本可观测行为偏离原版（曾被放宽后回退） |
| I-4 | 能力位是**静态**的：构造期声明，运行期不变；需切换须重建会话 | 降级矩阵组合爆炸、不可测 |
| I-5 | **core 不模拟、不补偿、不放宽**：宿主能力缺失时由**宿主**自选降级策略，core 只忠实表达 | 为一个宿主污染全生态的输入语义 |
| I-6 | `ResetState` **不得**在 `WaitInput` 建立时调用（属 era-core 自创语义） | 破坏 `GETKEYTRIGGERED` 边沿检测 |
| I-7 | 视口**不并入** `IInputHost` | 输入层重演 `IConsoleUI` 的臃肿 |

### 2.4 ADR 索引

- [ADR-0031 统一输入抽象层：IInputHost / InputCapabilities / InputSnapshot](../docs/adr/0031-unified-input-abstraction-layer.md) —— **proposed**，本轴主决策（含「core 不模拟不补偿不放宽」与坐标系契约 §8）
- [ADR-0010 VtParser 解耦 + IVtHost 原语分发重构](../docs/adr/0010-vt-parser-decoupling-and-ivthost-primitive.md) —— accepted，`IVtHost` 是输入抽象层的既有雏形
- [ADR-0009 Scroll 状态抽出到 ScrollController](../docs/adr/0009-scroll-controller-extraction.md) —— accepted
- [ADR-0021 按钮可提交性跨客户端判定](../docs/adr/0021-button-submitability-cross-client.md) —— 几何通道落地时的重开条件
- [ADR-0018 按钮 generation 失效](../docs/adr/0018-button-generation-invalidation-v7.md) —— 几何通道兜底依赖

---

## 3. 输出轴

### 3.1 两类产物

输出有两条**结构不同**的通道，共用同一 turn 串：

- **拉取式快照**（`IDisplayState`）：引擎持有显示状态，宿主按需 `TryUpdate()` / `Current`。
- **推送式广播**（`OutputHub` / `IOutputBroadcaster`）：turn 写出即广播给所有旁路订阅者（WS）。

| 类型 | 语义 | 定义 |
|---|---|---|
| `IDisplayState` | 拉取式显示状态窄接口（`TryUpdate` / `Current`） | `Agent/IDisplayState.cs` |
| `DisplayState`（`: IDisplayState`） | 有状态、带变更检测的统一显示模型 | `Agent/DisplayState.cs:96` |
| `DisplaySnapshot` / `DisplayLine` / `DisplayEntry` | 全量快照与行/条目 | `Agent/DisplayState.cs:22,46,81` |
| `TurnRecord` | 增量帧 wire 契约（`diff` / `awaiting` / `error` / `protocolVersion` / `generation`） | `Agent/TurnRecord.cs:9`（版本常量 `:47`） |
| `DisplayDiff` / `LineOp` / `TurnOp` | 帧内操作序列 | `Agent/TurnRecord.cs:93,113,134` |
| `IOutputBroadcaster` | 输出广播窄接口（仅 `Publish` / `Complete`） | `Server/IOutputBroadcaster.cs:14` |
| `ITurnSink` / `IControlSignal` | `TurnDelivery` 消费的窄接缝（读/回滚/排水/写/关闭；控制权快照） | `Server/TurnDelivery.cs:23,12` |

### 3.2 生产实现与消费链

| 角色 | 类型 | 位置 |
|---|---|---|
| 快照产出 | `AgentJsonlProtocol`（持 `DisplayState`，经 `_io.WriteLine` 产帧） | `Agent/AgentJsonlProtocol.cs:14` |
| **帧形状权威** | `TurnFrames`（合法帧形状 + 序列化 + 终态工厂；纯静态、不持运行期状态） | `Agent/TurnFrames.cs:26` |
| 推送中枢 | `OutputHub`（`: IOutputBroadcaster`，唯一生产实现） | `Server/OutputHub.cs:22` |
| 会话 IO | `HttpSessionIO`（`: SessionIO, ITurnSink`；`WriteLine` 同时入队 + 广播） | `Server/HttpSessionIO.cs:14` |
| 长轮询编排 | `TurnDelivery`（长轮询 + 控制权抢占 + final-turn-once + 超时不变式） | `Server/TurnDelivery.cs:38` |
| 控制信号 | `Controller`（`: IControlSignal`） | `Server/Controller.cs:86` |
| WS 传输 | `WsRelay`（Core 共享，BCL only） | `Server/WsRelay.cs:27` |
| 会话装配 | `SessionRegistry.CreateSessionCore`（建 `OutputHub` → `HttpSessionIO` → `Session`；测试可注入会话工厂，接缝是一个 loop `Task`） | `Server/SessionRegistry.cs:546` |
| 会话相位与拆除顺序 | `SessionRegistry` 的 `Live` / `Retiring` 相位（ADR-0049 S-1~S-3）；拆除顺序固定在 `TurnDelivery.Dispose(reason)` 内（置旗标 → `End` → `Close`），registry 不得自行 `Controller.End` | `Server/SessionRegistry.cs`、`Server/TurnDelivery.cs:133` |
| 宿主持有 | Kestrel `WsConnectionHandler` / MAUI `BridgeHost.TurnPumpAsync` / CLI `TerminalRenderer` | 见第 5 节宿主表 |

### 3.3 不变量

| # | 规则 | 违反后果 |
|---|---|---|
| O-1 | `OutputHub` **纯转发**：不解析 turn、不持业务状态、不依赖 `Session` / 协议层 / 引擎层 | 旁路耦合进业务 |
| O-2 | 订阅者为**独占** `ChannelReader`，**晚加入者只收后续 turn**（不回放历史） | 订阅语义歧义 |
| O-3 | `IOutputBroadcaster` **仅为 `HttpSessionIO` 可测性服务**，生产实现只允许 `OutputHub` | 出现第二个生产实现即破坏「唯一广播中枢」 |
| O-4 | `WriteLine` 必须**同时**入 `_output` Channel（HTTP 长轮询）与广播（旁路），二者不可只做其一 | HTTP 与 WS 输出分叉 |
| O-5 | `TurnDelivery` 独占「长轮询 + 控制权抢占 + final-turn-once + 超时」四条不变式，`Session` 只做组装 | 取水路径出现第二读者 / final turn 重复或丢失 |
| O-6 | 协议版本变更须**同步**更新 `TurnRecord.CurrentProtocolVersion` 与 Python 侧 `tests/emuera_server.py:PROTOCOL_VERSION` | 前后端版本错配 |
| O-7 | 生产代码**只能**经 `TurnFrames`（`Ok` / `Quit` / `Fatal` / `Error`）构造帧；终态帧只能经 `Quit` / `Fatal` / `Error` 三个语义入口 | 「Quit 帧带 diff」「错误帧照抄 `console.State`」等非法形态复活 |
| O-8 | `TurnFrames` **不持运行期状态**（产帧时机与空转抑制属 `AgentJsonlProtocol`） | 形状权威与运行时状态纠缠、变胖 |

### 3.4 ADR 索引

- [ADR-0001 Turn 协议版本化](../docs/adr/0001-turn-protocol-versioning.md)
- [ADR-0002 Turn v2 操作序列](../docs/adr/0002-turn-v2-operation-sequence.md)
- [ADR-0032 回合帧形状收口：TurnFrames 作为唯一形状权威](../docs/adr/0032-turn-frame-shape-authority.md) —— implemented，本轴形状权威（含不变量 O-7 / O-8）
- [ADR-0013 DisplayState 快照路由](../docs/adr/0013-displaystate-snapshot-routing.md) —— accepted
- [ADR-0014 DisplayState 统一真相源（CLI 亦消费）](../docs/adr/0014-displaystate-unified-source-cli-consumes.md) —— accepted，取代 ADR-0013 决策四
- [ADR-0022 DisplayState clear 信号累加器](../docs/adr/0022-displaystate-clear-signal-accumulator.md)
- [ADR-0003 WebSocket 传输接入：Hub 旁路](../docs/adr/0003-websocket-hub-bypass.md) —— accepted
- [ADR-0020 Agent 控制权交接模型](../docs/adr/0020-control-handoff-model.md) —— proposed
- [ADR-0021 按钮可提交性跨客户端判定](../docs/adr/0021-button-submitability-cross-client.md)

---

## 4. 文件轴

### 4.1 接口

`IGameDirAccessor`（`IGameDirAccessor.cs:13`）是**唯一**的游戏目录访问抽象，也是 Core 里**唯一 `public`** 的能力轴接口
（宿主实现需跨程序集可见）。接口头部硬性声明：

> 「所有文件 IO 必须通过此接口，禁止直接调用 `File.*` / `Directory.*`。」

覆盖：目录/文件枚举、读取（`ReadAllText` / `ReadAllBytes` / `OpenRead`）、写入（`CreateDirectory` / `OpenWrite` / `Delete` / `HasWriteAccess`）、路径操作（`ResolveSubPath` / `CombinePath` / `GetParentPath` / `GetFileName` / 批量 `CombinePaths`）、目录选择（`PickDirectoryAsync`）。

### 4.2 实现与辅助

| 类型 | 位置 | 说明 |
|---|---|---|
| `FileSystemGameDirAccessor` | `FileSystemGameDirAccessor.cs:16` | 本地文件系统实现；含 issues/20 大小写容错 |
| `SafGameDirAccessor` | `EraCore.Maui/Services/SafGameDirAccessor.cs:18`（`#if ANDROID`） | Android SAF（`content://`）实现 |
| `GamePaths` | `GamePaths.cs:6` | 持 `DirAccessor`，解析 / 校验当前游戏根 |
| `GameScanner` | `GameScanner.cs:11` | 扫描游戏根（**全经 accessor**） |
| `DirectoryLister` | `DirectoryLister.cs:12` | 逐层列目录，供 Vue `DirectoryBrowser`（**全经 accessor**） |
| `DirectoryChildCache` | `DirectoryChildCache.cs:22` | SAF docId → 子项缓存（**不依赖**接口） |
| `Wildcard` | `Wildcard.cs:14` | 通配匹配，被两个实现复用（**不依赖**接口） |
| `AppDataPaths` | `AppDataPaths.cs:8` | 应用私有根（**非游戏树**，**不走** accessor）；`IsUnder` = F-4 的可机检判定 |
| `AppDataIo` | `AppDataIo.cs` | **私有域的唯一 IO 窄口子**：方法收「相对私有根的名字」，越域在签名上不可表达 |

### 4.3 不变量

| # | 规则 | 违反后果 |
|---|---|---|
| F-1 | **游戏树**内全部文件 IO 必须经 `IGameDirAccessor`；禁止直接 `File.*` / `Directory.*` | Android SAF 下静默失败、平台差异泄漏进引擎 |
| F-2 | 两个实现**契约一致**：精确名读取须大小写不敏感解析（对齐 SAF 语义） | 大小写敏感挂载上固定名 CSV 读不出 |
| F-3 | SAF 写路径须按「父目录 + displayName」查找/创建，**不得**假设理论 URI 已在 provider 注册 | 写回存档丢失 |
| F-4 | 应用私有域是**唯一豁免域**——domain 由 `AppDataPaths`（+ `AppDataIo` 窄口子）显式界定，经 `AppDataPaths.IsUnder` 机检 | 豁免边界回到「靠开发者记得」的隐式状态 |
| F-5 | 已知**例外清单**（见下，分「待收敛」与「永久豁免」两段）由守卫测试 `GameDirAccessorIoPolicyTests` 逐文件、逐调用数盯着；新增代码**不得**扩大该清单 | 例外蔓延成事实上的第二套 IO |

**例外清单**（截至 2026-10-18，Core 内裸 `File.*` / `Directory.*`）——**GameTree 域已归零**。

> **两段的区分口径**（2026-10-18 拆分，由守卫测试的 `Domain` 字段机读）：
> **待收敛** = `Domain.AppPrivate`，理论上可收敛，只差一个合适的接缝；
> **永久豁免** = `Domain.Platform`，是访问器的底层实现或发生在 accessor 上任之前，**不打算收敛**。
> 此前两者混装在一张表里，每次盘点都要重新解释"为什么这几行不动"。

> **2026-10-18（ADR-0036，两片）**：GameTree 域里两组**不可编译**站点已随其源码一并删除，非"收敛"——
> ① `AppContents.cs`（3 处，随 `#else` 半支删除）；② `ImgUtils.cs`（整文件，`#if !HEADLESS`）
> 与 `EvilMask/Utils.cs`（1 处，随 `#if !HEADLESS` 区域删除）。
> 依据：Core 恒以 `HEADLESS` 编译（`EraCore.Core.csproj:8`），这些站点从不参与任何构建；其调用方
> （`Process.cs:118` 命中 headless stub、`ConstImage.cs:76`、`Creator.Method.Io.cs:82/290`）亦在各自 `#else` 内。
> 注：`Emuera/Program.cs:183/186` 也引用了被删的 `Utils.LoadImage` / `MakeIconFromBmpFile`，但该目录**无 csproj、
> 不在 `EraCore.sln` 内**，属遗留参考源码，不参与构建。
> **清点清单前应先确认目标是否在 `#else` 内**——清单条目数是源码文本计数，不等于真实 IO 面。
> **守卫测试自此也堵住了同一漏洞**：清单行的实际计数若已归零（文件还在、但已无直连 IO），该行会被判为陈旧而必须删除——清单始终保持为「现役欠账」。
> 2026-10-18 下午（ADR-0037）又收掉三处**可编译**站点：`ConstantData` 的不可达 `File.Exists` 回退、
> `EncodingHandler` 的两个 path 重载、`EraStreamReader` 的「accessor 读空后直连磁盘」最后兜底
> （末项是**刻意的行为变化**：失败显性化；行为锁 = `EraStreamReaderAccessorTests`，其中
> `Open_does_not_fall_back_to_the_local_file_system` 在兜底被加回时立即转红）。
> 同日（ADR-0038）收尾 `ConfigData` 的 sav 挪档：搬移段前有一道 `Dialog.ShowPrompt`，无头实现恒返回 No
> ⇒ 两处 `File.Move` 与依赖它们的三处探测**在任何当前构建里不可达**，随段删除；其余两处改用 accessor。
> **关键结论：原本为它们准备的 `MoveFile` 接口成员已撤销**——不需要了，接口一个符号都没动。
> 行为锁 = `SaveFolderCreationTests`，钉住「建 sav 目录 + 旧存档不搬移」这一刻意保持的行为。

### 待收敛（真欠账，**0 处** —— 已清空）

> **2026-10-18（ADR-0048，14 票批 B/C）**：两个缓存文件的 **18 处全部归零**——
> 文件级 IO 改走 `SafCompat`（本地 / content URI 双形态的单点），目录级孤儿治理改走 `AppDataIo`
> （批 A 补的枚举 / 目录 mtime / 递归删三个幂等原语，语义均为幂等）。
> `ErbIrCache`（10 处）与 `SrcContentCache`（8 处）的行都已从守卫清单删除。
>
> **收敛方向不是塞进 `IGameDirAccessor`**：该接口的语义是「游戏内容目录」，把缓存 IO 挂进去会污染 F-1。
> 实际采用的是**既有两个接缝**（`SafCompat` + `AppDataIo`），未新增任何接口。
>
> 本节此后应为空——新增任何 AppPrivate 直连 IO 都会被守卫判为「未登记」转红。

### 永久豁免（21 处，不打算收敛）

| 文件 | 域 | 处数 | 为什么不收敛 |
|---|---|---|---|
| `GamePaths.cs` | Platform | 2 | 宿主安装目录探测 + 本地路径归一化（`DetectDefault` / `new DirectoryInfo`），**发生在 accessor 上任之前**——此时还没有访问器可用 |
| `Shared/Runtime/Utils/SafCompat.cs` | Platform | 18 | ADR-0019 兼容包装**本体**：它就是访问器在 SAF 下的实现底座，收敛等于让实现调用自己 |
| `Shared/Runtime/Utils/Sys.cs` | Platform | 1 | 进程工作目录，不解析游戏文件 |

> **不计入清单的合规直连调用**（守卫测试显式跳过）：`FileSystemGameDirAccessor.cs`（接口实现本体）、
> `AppDataIo.cs` 与 `AppDataPaths.cs`（F-4 豁免域自身的 IO 口子）。

**2026-10-16 收敛轮**（候选 6「IGameDirAccessor 接缝泄漏」）已移出清单的站点：

- `DirectoryLister.cs` —— `ComputeParentPath` 改走 `dirAccessor.GetParentPath`（补了 `DirectoryListerTests` 行为锁，因两者对尾部分隔符的语义不同）
- `GamePaths.cs` —— `Validate()` 的真实路径分支不再裸调 `Directory.Exists`，与 SAF 分支合并为一条经 accessor 的路径
- `Compat/LegacyPluginCompat.cs` —— 插件兄弟依赖的存在性判定改走注入的 accessor
- `Shared/Runtime/Utils/PluginSystem/PluginManager.cs` —— `pluginsAware.txt` 存在性改走 accessor
- `Shared/UI/Game/Image/ConstImage.cs` —— 诊断日志不再写进程 CWD，改落私有域
- `Agent/AgentLog.cs`、`Shared/Runtime/Config/JSON/JSONConfig.cs` —— 私有域 IO 全部经 `AppDataIo` 窄口子

### 4.4 ADR 索引

- [ADR-0019 Android SAF 文件访问替代 MANAGE_EXTERNAL_STORAGE](../docs/adr/0019-android-saf-file-access.md) —— accepted，本轴主决策
- [ADR-0017 MAUI 桥接传输：SessionIO 分层](../docs/adr/0017-maui-bridge-sessionio.md) —— revised，含 `GameScanner` / `DirectoryLister`

---

## 5. 宿主侧接触点

| 宿主 | 输入 | 输出 | 文件 |
|---|---|---|---|
| **Cli** | 造 `ITerminalInput`；`DefaultInputHost(Geometry\|KeyboardState)`（`HeadlessRunner.cs:30-32,82`） | `AgentCliProtocol` + `TerminalRenderer`；视口 `CliViewport` | `FileSystemGameDirAccessor` + `GamePaths`（`HeadlessEntry.cs:54,60`） |
| **Server** | 经共享层 `Session` 内置 `DefaultInputHost(KeyboardState\|Hover\|Focus)` | Kestrel 壳路由 + `WsConnectionHandler` → `WsRelay` | `GamePaths.Current.DirAccessor`（`KestrelGameServer.cs:298`） |
| **Maui** | `Session` 同一 `DefaultInputHost`；输入经 `s.IO.EnqueueInput`（`BridgeHost.cs:623`） | `BridgeHost.TurnPumpAsync` 订阅 `OutputHub`（`BridgeHost.cs:2065`） | `ResolveDirAccessor`：`content://` → `SafGameDirAccessor.Instance`，否则 `FileSystemGameDirAccessor`（`BridgeHost.cs:880`） |

---

## 6. 落地状态快照（截至 2026-10-15）

| 轴 | 主接口 | 状态 | 未完成 |
|---|---|---|---|
| 输入 | `IInputHost` | ADR-0031 **proposed**；M1/M2/M4 已落地（接口 + `DefaultInputHost` + 几何/键盘通道） | M5–M8：触屏 E-min、primitive 能力位、`IConsoleUI` 瘦身 Phase 2 |
| 输出 | `IDisplayState` / `OutputHub` | ADR-0013/0014/0003 **accepted 且已落地** | —— |
| 文件 | `IGameDirAccessor` | ADR-0019 **accepted 且已落地**（双实现）；2026-10-16 收敛一轮；**2026-10-18 三轮清完**：ADR-0036 删全部不可编译分支（`AppContents` / `ImgUtils` / `EvilMask.Utils`）→ ADR-0037 收最后一条可编译读缝（`ConstantData` / `EncodingHandler` / `EraStreamReader`）→ ADR-0038 收 sav 挪档（`ConfigData`） | **GameTree 域已归零**（Cap 0）；剩余 5 项已按 §4.3 拆分：**待收敛 0 项**（两个缓存文件 18 处已由 ADR-0048 全部归零）与**永久豁免 3 项 / 21 处**（`GamePaths` / `SafCompat` / `Sys`）。新增游戏树直连 IO 会立即被守卫测试判为「未登记」转红 |

> 输入轴仍处推进中：ADR-0031 为 `proposed`，M5–M8 未落地。**本表随里程碑推进而改**，不视为稳定契约。

---

## 7. 相关文档

- 仓库总览：[`../ARCHITECTURE.md`](../ARCHITECTURE.md)
- 架构决策记录：[`../docs/adr/`](../docs/adr/)
- 前端导航：[`../EraCore.Web/README.md`](../EraCore.Web/README.md)
- 测试说明：[`../tests/README.md`](../tests/README.md)
- 终端行为教训：[`../docs/LESSONS/terminal-windows.md`](../docs/LESSONS/terminal-windows.md)
