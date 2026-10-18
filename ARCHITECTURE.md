# EraCore Architecture

本文档是当前项目架构的总览入口。它描述当前有效的项目边界、依赖关系和主要运行时流程；历史迁移过程和具体任务决策仍以 `docs/` 下的专题文档为准。

## 维护边界

当前维护范围：

- `EraCore.Core`：无头运行器核心。
- `EraCore.Server`：HTTP/WebSocket 服务器组件。
- `EraCore.Cli`：CLI 和 Server 模式入口。
- `EraCore.Maui`：Windows/Android MAUI 应用壳和 C# 桥接。
- `EraCore.Web`：Vue 3 前端。

不在当前维护范围：

- `Emuera/`：旧 WinForms 源码，仅作行为参考。
- `EmueraPluginExample/`：旧插件示例，不属于当前解决方案主路径。
- `experiments/`：一次性实验项目，不作为产品架构依赖。

## 项目关系

```text
                         +----------------------+
                         |   EraCore.Web (Vue)   |
                         +----------+-----------+
                                    |
                    +---------------+----------------+
                    |                                |
              HTTP/WebSocket                    WebView JS 桥
                    |                                |
       +------------v-------------+       +----------v----------+
       | EraCore.Server   |       |    EraCore.Maui    |
       |  (Kestrel 宿主壳)         |       |  (BridgeHost +      |
       +------------+-------------+       |   HttpListenerHost) |
                    |                      +----------+----------+
                    |   HTTP/WS (agent)  ────────────┘
                    +---------------+----------------+
                                    |
                         +----------v-----------+
                         | EraCore.Core |
                         |  (共享层 Server/:      |
                         |   GameServerProtocol /|
                         |   WsRelay / Session   |
                         |   / Controller ...)   |
                         +----------------------+

       EraCore.Cli -> Core + Server
       EraCore.Tests -> Core + Server
       EraCore.Maui.Tests -> Maui
```

依赖边界：

- `Core` 不依赖 ASP.NET Core 或 MAUI。
- `Server` 依赖 `Core`，提供 Kestrel 宿主壳（路由 + 静态资源 + WS 升级），业务协议委托给 Core 共享层 `Server/`。
- `Cli` 依赖 `Core` 和 `Server`；Android RID 下通过条件编译排除 Server。
- `Maui` 只依赖 `Core`，经 `BridgeHost` 桥接 WebView 渲染共享层会话，并用 `HttpListenerHost`（BCL，Core 内）托管 Server 供 agent 经 HTTP/WS 连接同一会话。
- `Web` 通过 HTTP/WS 或 MAUI JavaScript bridge 消费同一套回合/显示语义。

## Core

`EraCore.Core/` 是唯一维护中的引擎核心，负责：

- 游戏路径、配置、预加载和运行时初始化。
- `EmueraConsole`、显示状态、输入请求和按钮状态。
- Agent 协议、CLI 终端循环和终端渲染。
- **共享层 `Server/`**（issue 05「抽共享层 + 双宿主」）：回合协议（`GameServerProtocol`）、控制状态机（`Controller`）、会话（`Session` / `SessionRegistry` / `HttpSessionIO` / `OutputHub` / `WsRelay`）与 `HttpListenerHost`（BCL 托管宿主）——不依赖 ASP.NET Core，供 Kestrel 与 MAUI 双宿主复用。
- 从旧 WinForms 源码迁移而来的共享运行时，位于 `Shared/`。

> Core 内部的三条能力轴（输入 / 输出 / 文件）抽象、实现映射与不变量清单，见 [`EraCore.Core/ARCHITECTURE.md`](EraCore.Core/ARCHITECTURE.md)。

主要区域：

| 路径 | 职责 |
|---|---|
| `Agent/` | Agent 协议、回合生成和按钮逻辑 |
| `Terminal/` | CLI/VT 输入、终端解析、渲染和滚动 |
| `Server/` | 共享层：回合协议 + 控制状态机 + 会话 + WS 旁路 + `HttpListenerHost`（MAUI 托管宿主），双宿主复用的传输无关逻辑 |
| `UI/Game/` | `EmueraConsole` 和游戏控制台状态 |
| `Shared/` | 从旧 WinForms 迁移的共享运行时和 UI 代码 |
| `Headless/` | 无头替换实现，如声音、剪贴板和字符串测量 |

`EmueraConsole` 是核心游戏控制台状态对象，不属于 Server 或 Web 层。Server、MAUI 和 Agent 都通过它或其桥接状态消费游戏输出。

## 方言兼容层

era-core **不 fork 核心**来兼容其他 Emuera 分支。上游 fork 的语义键（如 Skia 的 `TINPUTNF`、`SQL_*`）作为**方言模块**登记在内置注册表中，默认对普通游戏隐藏；会话绑定 profile 后，才在解析期进入可见面。

```text
descriptor（Dialect = Baseline | Snake | Eratw）
  -> FunctionIdentifier.funcDic / FunctionMethodCreator.methodList
  -> DialectRegistry：模块 -> profile 闭包 -> 不可变 surface（按 id 缓存）
  -> GlobalStatic.OpenScope(configData, profileId)
  -> IdentifierDictionary 构造期一次性过滤 instructionDic / methodDic -> nameDic
  -> 解析器 / 执行器只读过滤后的表（零 profile 分支）
```

| profile | 模块闭包 | 用途 |
|---|---|---|
| `v24pure`（默认） | `baseline` | 普通 Emuera 游戏；与移植前行为一致 |
| `snake` | `baseline + snake` | Skia 语义键（17 指令 + 49 函数） |
| `eratw` | `baseline + snake + eratw` | eraTW 画蛇添足版；当前 `eratw` 模块为空 |

约定：

- 可见面 = 所选模块声明键的并集；**未声明即隐藏**；显式 Hide 胜出；模块顺序不参与键裁决；
- 同键多 owner 必须显式 `override`/`priority`，否则构造期报错，禁止静默合并；
- 新增一条指令/函数只改一个 descriptor 声明点，见 [`EraCore.Core/Shared/Runtime/Script/Statements/FunctionIdentifier.Registry.cs`](EraCore.Core/Shared/Runtime/Script/Statements/FunctionIdentifier.Registry.cs) 与 [`Creator.Registry.cs`](EraCore.Core/Shared/Runtime/Script/Statements/Function/Creator.Registry.cs)；
- `--profile` 在 CLI 启动期 fail-fast；MAUI 用设置页 `app.dialectProfile`，重载游戏生效；agent 透传 `--profile` 并守卫复用 server 的一致性。

SQL 方言键引入 Core 的**唯一原生依赖**：`Microsoft.Data.Sqlite` → `SQLitePCLRaw.bundle_e_sqlite3` → `e_sqlite3.dll`。所有 SQLite 类型收口在 `SqlManager.cs` 单文件；CLI 分发时需保留 `runtimes/win-x64/native/e_sqlite3.dll`（或平铺到 exe 同目录），MAUI Windows 已内置。

详见 [方言移植指南](docs/guides/dialect-porting.md)、[eraTW 运行手册](docs/guides/running-eratw.md) 与 [66 键清单](docs/reference/dialect-surface.md)。

## Server

`EraCore.Server/` 提供单进程、单活跃会话的 HTTP 服务（**Kestrel 宿主壳**——路由、静态资源、body/token 解析与 HttpResult→IResult 映射，业务协议委托给 Core 共享层）：

- `KestrelGameServer`：Kestrel 构建 + 路由注册 + 静态资源，端点委托 `GameServerProtocol`。
- 共享层会话与传输（在 `EraCore.Core/Server/`）：`Session`（一次游戏会话的 console/protocol/IO）、`HttpSessionIO`（HTTP 输入/输出队列）、`OutputHub`（向 WS 客户端旁路广播）、`WsRelay`（WS 旁路循环）、`SessionRegistry`（会话生命周期 + /load-game 原子序列）、`GameServerProtocol`（传输无关回合协议 + 控制状态机）、`Controller`（控制权状态机）。
- `HttpListenerHost`（Core 内，BCL `System.Net.HttpListener`）：MAUI 托管宿主，与 Kestrel 共用同一 `GameServerProtocol`/`WsRelay`，wire 契约逐字节一致。

服务器会话约束：

- 同一时间只有一个活跃 `Session`，同时最多一个 Controller（`kind: agent|user`）。
- `POST /load-game` 校验目录、重建运行时并创建新会话；已有活跃会话时返回冲突。
- `DELETE /session` 结束当前会话并回到空闲态。空闲时 `POST /session` 返回 503，不经 `load-game` 不能建局。
- `GET /ws` 与 HTTP 长轮询共用同一会话输入通道；旁观者只读 WS。
- 控制权：`POST /control/acquire`、`POST /control/release`、`GET /control`、`GET /control/wait`。输入与活跃会话的生命周期操作只对 Controller 放行。

## CLI

`EraCore.Cli/` 是统一可执行入口：

- `HeadlessEntry`：解析命令行选项并选择运行模式。
- `HeadlessRunner`：交互式 CLI 模式。
- `ServerRunner`：启动 `KestrelGameServer`。
- `HeadlessOptions`：`--ExeDir`、`--server`、`--port`、`--profile` 和协议选项。

T-024 后已删除 stdin pipe 和 JSONL stdin/stdout 模式。脚本、自动化和 `eracore_agent` CLI 统一使用 Server 模式。

## MAUI

`EraCore.Maui/` 是 Windows/Android 应用壳：

- `MainPage` 承载 WebView。
- `BridgeHost` 编排托管会话（issue 05）：起 `HttpListenerHost`、建共享层 `Session`、订阅 `OutputHub` turn 转发 WebView、`HttpSessionIO` 入输入、`ControlPumpAsync` 推控制状态、写 agent 发现记录。
- `HttpListenerHost`（Core 内，BCL `System.Net.HttpListener`）：MAUI 托管 Server，起 HTTP+WS，agent 经 localhost 连接**同一会话**（单实例共享）。
- `JsBridge/` 提供 Windows/Android 平台桥接实现。
- `MauiProgram` 负责 MAUI 依赖注入和应用启动。
- 方言 profile 由设置项 `app.dialectProfile` 决定（`BridgeHost` 在建会话前读取并按会话传入），重载游戏生效。

MAUI 启动和游戏选择流程：

```text
MainPage / MauiProgram
  -> 初始化 BridgeHost，但不启动托管 server
  -> Vue 注册 window.__onTurn / window.__onMessage
  -> MauiGameList 扫描游戏目录
  -> 用户选择游戏
  -> loadGameFromPath(fullPath)
  -> C# 重建运行时 → BridgeHost.Start()
  -> 起 HttpListenerHost(动态端口) → CreateMauiSessionAsync 建会话
  -> TurnPumpAsync(OutputHub) → BridgeHost.PostTurn()
  -> Vue game store.applyTurn() → TerminalDisplay 渲染
  -> 写 %LOCALAPPDATA%\Emuera\emuera-maui-server.json（agent 发现）
```

MAUI 不加载 `EraCore.Server` 或 `EraCore.Cli`——`HttpListenerHost` 在 Core 内用 BCL，避免 Android 目标引入 ASP.NET Core 依赖。托管 server 生命周期随游戏会话（退出/关闭即 Dispose 并删发现记录）。

## Web

`EraCore.Web/` 是 Vue 3 SPA，详细页面、组件、Store 和修改入口见 [`EraCore.Web/README.md`](EraCore.Web/README.md)。

前端有两条传输路径：

```text
HTTP 模式:
Web -> WebSocket/HTTP -> Headless.Server (Kestrel 宿主) -> Core 共享层

MAUI 模式:
WebView -> IJsBridge/BridgeHost -> 共享层会话 (OutputHub 订阅 / session.IO)
agent   -> HTTP/WS -> HttpListenerHost (同一共享层会话)
```

两种模式共享：

- 回合状态和显示状态语义。
- 输入类型、按钮值和终端推进语义。
- 图片、图形和背景的结构化显示模型。

差异只在传输层：HTTP 模式走网络端点；MAUI 模式 WebView 用进程内桥接渲染、同时托管 Server 供 agent 连同一会话。控制状态（谁在操控）在 MAUI 端经 `controlStatus` 桥接消息同步（对应 HTTP 模式 `/control` 轮询）。

## 协议和状态流

核心回合流程：

```text
游戏脚本
  -> EmueraConsole / GameLoop
  -> AgentJsonlProtocol
  -> TurnRecord
  -> HTTP/WS (Kestrel) 或 WebView 桥接 (MAUI, 经 OutputHub 订阅)
  -> game store
  -> DisplayState / TerminalDisplay
```

常见状态：

- `Idle`：没有活跃游戏会话。
- `Loading`：正在加载或重建游戏。
- `WaitInput`：游戏等待用户输入。
- `Quit`：游戏正常结束。
- `Error`：游戏或协议发生致命错误。

协议版本变更时，必须同步更新 C# `TurnRecord.CurrentProtocolVersion` 和 Python 侧 `tests/emuera_server.py:PROTOCOL_VERSION`。

## 构建边界

- `EraCore.Cli` 的 Vue 构建通过 `build/VueBuild.targets` 集成。
- CLI 使用 `EraCore.Web/dist/` 和默认 Vite base。
- MAUI 使用独立的 `dist-maui/` 和相对 Vite base，避免与 CLI 构建互相覆盖。
- CLI 发布时 `wwwroot/` 是随 exe 分发的外部静态文件目录，不嵌入单文件 exe。
- MAUI 不支持 `PublishSingleFile`；应使用框架依赖或独立发布。
- CLI 的 SQLite 原生库位于 `runtimes/win-x64/native/e_sqlite3.dll`；拷贝分发时必须带 `runtimes/` 整树或把 dll 平铺到 exe 同目录。MAUI Windows 已由 RID 发布压平。

## 相关文档

- Core 抽象层：[`EraCore.Core/ARCHITECTURE.md`](EraCore.Core/ARCHITECTURE.md)（输入/输出/文件三条能力轴的层图与不变量）
- 前端导航：[`EraCore.Web/README.md`](EraCore.Web/README.md)
- 测试说明：[`tests/README.md`](tests/README.md)
- 文件迁移历史：[`docs/2026.6.30.架构健壮性重构/T-023前置-文件结构整理方案.md`](docs/2026.6.30.架构健壮性重构/T-023前置-文件结构整理方案.md)
- NativeAOT 验证：[`docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md`](docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md)
- 终端行为教训：[`docs/LESSONS/terminal-windows.md`](docs/LESSONS/terminal-windows.md)
