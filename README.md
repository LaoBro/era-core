# EraCore

**Emuera 的现代化重构版** —— 同一个引擎，四种前端（Windows 桌面 / Android / 浏览器 / 终端），外加一条给 AI 的操控通道。

在保留原汁原味 Emuera 体验的同时，完全剥离 WinForms 依赖，走向模块化、可扩展、可自动化、可被 AI 操控的新一代架构。

> 本项目最初 fork 自 **EvilMask 的** **[Emuera.EM](https://gitlab.com/EvilMask/emuera.em)**。

![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Android%20%7C%20Web-0078D4)
![License](https://img.shields.io/badge/license-Emuera-blue)

***

## 为什么选 EraCore

| 能力    | 原版 Emuera          | EraCore                                  |
| ----- | ------------------ | ---------------------------------------- |
| 架构    | UI 与内核强耦合          | 内核完全无头，各功能模块化                            |
| 控制流   | 纯靠 WinForms 事件推进游戏 | 前端与内核异步，互不阻塞                           |
| 支持平台  | 仅 Windows          | 原生跨平台                                    |
| 自动化   | 无                  | 内置 HTTP + WebSocket + JSONL 协议，脚本可直接驱动游戏 |
| AI 操控 | 无                  | `eracore_agent` CLI：一次调用 = 一个回合，支持控制权交接  |
| 接入新前端 | 难                  | 实现协议即可接入                                 |
| 性能    | 启动慢、常驻内存高          | 启动快一个数量级，内存降至约 1/7                         |

## 特性亮点

- **模块化架构，易于扩展** — 引擎（`EraCore.Core`）、服务（`EraCore.Server`）、入口（`EraCore.Cli`）、界面（`EraCore.Maui` / `EraCore.Web`）彻底解耦，内核完全无头。

- **无头运行器 + CLI 模式** — 无需图形界面即可运行游戏，内置 HTTP 服务器与 JSONL 协议。

- **轻量级 Web 前端** — 基于 Vue 3 + TypeScript 的纯浏览器界面，打开即玩，支持 WebSocket 实时旁观与接管。

- **跨平台一套引擎** — Windows 桌面（原生 MAUI）、Android（APK，可选 NativeAOT 加速）、浏览器（Web SPA）多处体验一致。

- **性能大幅优化** — ERB 并行加载 + 未调用函数延迟解析 + IR 缓存，实测启动快一个数量级、内存降至原版约 1/7（见下方[性能](#性能)）。

***

## 截图预览

| MAUI/Web                   | CLI 交互模式                    |
| -------------------------- | --------------------------- |
| ![](docs/preview/MAUI.jpg) | ![](docs/preview/CLI模式.jpg) |

***

## 快速开始

### 最简单的玩法：一键拉起本地服务器，浏览器开玩

**双击** **`run-web.cmd`**（或执行下面的命令）→ 构建 → 启动 server → 自动打开默认浏览器：

```bash
# 构建 CLI 入口
dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug

# 启动 HTTP 服务器并自动打开浏览器（未显式 --port 时自动挑空闲端口）
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --server --open-browser
```

浏览器里先看到游戏选择页（输入/浏览主目录 → 点游戏即进），无需手敲游戏路径。手动指定端口：

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --ExeDir test_game --server --port 8080
```

### 其他运行方式

```bash
# CLI 交互模式（终端直接游玩，需真实 TTY）
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --ExeDir <游戏目录> --protocol cli

# Windows 桌面应用（启动后自动解压内置 test_game/ 到 AppData，WebView 加载 Vue 界面）
dotnet run --project EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug

# Web 前端开发（Vite dev server，HTTP/WS 代理到 C# 服务器 :8080）
cd EraCore.Web && npm install && npm run dev
```

> 脚本/自动化统一走 `--server`；`--protocol jsonl` 在非 server 模式下会报错。

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本

- Python 3.10+（用于 `eracore_agent` CLI 和测试）

- Node.js 18+（用于 Web 前端 `EraCore.Web/`）

***

## 支持平台与运行模式

| 运行模式        | 入口                     | 说明                     |
| ----------- | ---------------------- | ---------------------- |
| CLI 交互模式    | `EraCore.Cli`          | 终端直接游玩，需真实 TTY         |
| HTTP Server | `EraCore.Cli --server` | 本地/局域网 Web 服务，浏览器即玩    |
| Windows 桌面  | `EraCore.Maui`         | 原生窗口体验，内置 WebView      |
| Android APK | `EraCore.Maui`         | 移动端游玩（支持 NativeAOT 加速） |
| AI 代理       | `eracore_agent`        | 无头操控，供自动化与 AI 使用       |

***

## 性能

通过一系列加载优化，EraCore 的启动速度与内存占用相比原版 Emuera / uEmuera 已有数量级差距。同一设备实测：

| 场景                                | 对照                     | EraCore         |
| --------------------------------- | ---------------------- | --------------- |
| Android 打开 eratohoK 1.29.3（同一台真机） | uEmuera **30.57s**     | **1.35s**       |
| Windows 打开 eratohoK 至标题界面（进程内存）   | 原版 Emuera **1072.5MB** | CLI **155.1MB** |

> Android 项走 EraCore 的**快速轨**（真实路径直通 accessor），1.35s 为 **IR 缓存命中轮**耗时——IR 缓存是本项目新增能力，缓存建立于首次扫描，之后每次启动复用。
> 测量条件、优化开关与复测口径详见 [docs/guides/performance-tuning.md](docs/guides/performance-tuning.md)。

***

## Agent 集成

Agent 通过中转 CLI `eracore_agent` 操控游戏（不走 MCP）。三条原则：

- **一次调用 = 一个回合** — stdout 只输出一行 turn JSON，错误走 stderr + 非零退出码。

- **控制权排他** — 同一时刻只有一个 Controller（`agent` 或 `user`）；读取画面需先持有控制权，让权以显式 `release` 为准。

- **`start`** **自动复用** — 已在跑的 server（MAUI 托管实例优先）会被复用，都没有才拉起新实例。

启动序列、回合循环、控制权交接礼仪与错误恢复见自包含的 [eracore-playtesting SKILL](.agents/skills/eracore-playtesting/SKILL.md)；
JSONL 协议字段、server 复用优先级与运行期产物见 [docs/guides/agent-integration.md](docs/guides/agent-integration.md)。

***

## 测试

测试分三层：**C# 单元测试**（xUnit）、**Python 端到端**（CLI/HTTP/WebSocket）、**前端测试**（Vitest）。

```bash
# C# 单元测试
dotnet test EraCore.Tests/EraCore.Tests.csproj

# MAUI 单元测试
dotnet test EraCore.Maui.Tests/EraCore.Maui.Tests.csproj

# 前端测试（Vitest）
cd EraCore.Web && npm test

# 全量回归（先 C# 单测 + 构建，再全量 Python 套件；相对路径基于仓库根）
python tests/run_all.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game
```

碰「文件名大小写 / 通配 pattern / 路径分隔符」的改动，必须额外跑一次真大小写敏感环境（WSL2 Ubuntu）——这类缺陷在 Windows 本地 `dotnet test` 里不可能复现。触发清单、判读口径与套件运行方式见 [tests/README.md](tests/README.md)。

***

## 文档

| 文档                                                                     | 内容                                                        |
| ---------------------------------------------------------------------- | --------------------------------------------------------- |
| [ARCHITECTURE.md](ARCHITECTURE.md)                                     | 架构总览：项目边界、依赖关系、运行时数据流                                     |
| [docs/guides/README.md](docs/guides/README.md)                         | 使用与运维指南索引                                                 |
| [docs/guides/build-and-publish.md](docs/guides/build-and-publish.md)   | 构建与发布：`publish.ps1` 各目标、原生 dotnet 命令、Android NativeAOT 约束 |
| [docs/guides/performance-tuning.md](docs/guides/performance-tuning.md) | 性能与加载开关：ERB 并行加载、延迟解析、相关警告开关与实测数据                         |
| [docs/guides/agent-integration.md](docs/guides/agent-integration.md)   | Agent 集成：JSONL 协议、server 复用优先级、运行期产物                      |
| [EraCore.Web/README.md](EraCore.Web/README.md)                         | Web 前端开发：文件导航与常见修改入口                                      |
| [tests/README.md](tests/README.md)                                     | 测试套件：夹具、套件与单项运行方式                                         |
| [docs/LESSONS/README.md](docs/LESSONS/README.md)                       | 经验教训索引：终端、NativeAOT、测试环境等                                 |
| [docs/LESSONS/web-fonts.md](docs/LESSONS/web-fonts.md)                 | Web 跨平台字体方案：固定网格排版、woff2 回退链、字形补齐                         |

***

## 项目结构

```text
EraCore.Core/       -- 无头核心库（MAUI 可直接引用，不依赖 AspNetCore）
EraCore.Server/     -- HTTP/WebSocket 服务器组件
EraCore.Cli/        -- CLI 与 Server 模式入口（唯一可运行的 Headless 入口）
EraCore.Maui/       -- Windows + Android 应用壳（原生 WebView）
EraCore.Web/        -- Vue 3 + TypeScript 浏览器前端
eracore_agent/      -- Python Agent CLI 与 HTTP 客户端
tests/              -- Python 端到端测试脚本
test_game/          -- 开发用最小 ERB 测试游戏
```

其余目录（`EraCore.Tests/`、只读参考源码 `Emuera/` 等）与完整依赖边界详见 [ARCHITECTURE.md](ARCHITECTURE.md)。

***

## 致谢

- **上游项目**：Emuera（Copyright (C) 2008- MinorShift），以及 fork 来源 **EvilMask 的 Emuera.EM**。

- 社区为 Emuera 生态做出的长期贡献。

***

## 许可证

本项目基于上游 Emuera 的许可证分发。详见 [Readme/License/Emuera.LICENSE.txt](Readme/License/Emuera.LICENSE.txt)。Copyright (C) 2008- MinorShift。社区维护分支。
