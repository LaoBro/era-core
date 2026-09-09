# EraCore

 **Emuera的现代化重构版本**，完全剥离WinForms依赖，并提供高性能跨平台构建。

> 本项目最初 fork 自 **EvilMask 的 [Emuera.EM](https://gitlab.com/EvilMask/emuera.em)** 项目，致力于在保留原汁原味 Emuera 体验的同时，迈向模块化、可扩展、可自动化与被 AI 操控的新一代架构。

---

## 特性亮点

- **模块化架构，易于扩展** — 引擎（`EraCore.Core`）、服务（`EraCore.Server`）、入口（`EraCore.Cli`）、界面（`EraCore.Maui` / `EraCore.Web`）彻底解耦。让内核完全无头，接不同前端只需实现统一协议。
- **无头运行器 + CLI 模式** — 无需图形界面即可运行游戏。内置 HTTP 服务器与 JSONL 协议，脚本、自动化、**AI 代理（`eracore_agent`）**都能直接操控游戏。
- **轻量级 Web 前端** — 基于 Vue 3 + TypeScript 的纯浏览器界面，任意现代浏览器打开即玩，支持 WebSocket 实时旁观与接管。
- **跨平台** — Windows 桌面（原生 MAUI）、Android（APK，可选 NativeAOT 原生加速）、浏览器（Web SPA）一套引擎多处体验。

---

## 截图预览

| MAUI/Web | CLI 交互模式 |
| --- | --- |
| ![](docs/preview/MAUI.jpg) | ![](docs/preview/CLI模式.jpg) |

---

## 快速开始

### 最简单的玩法：一次性拉起本地服务器，浏览器开玩

**双击 `run-web.cmd`**（或执行下面的命令）→ 构建 → 启动 server → 自动打开默认浏览器：
浏览器里先看到游戏选择页（输入/浏览主目录 → 点游戏即进），无需再手敲游戏路径。

```bash
# 构建 CLI 入口
dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug

# 启动 HTTP 服务器并自动打开浏览器（未显式 --port 时自动挑空闲端口）
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --server --open-browser
```

手动指定端口：

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --ExeDir test_game --server --port 8080
```

浏览器访问 <http://localhost:8080> 即可开始游戏。

### 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 或更高版本
- Python 3.10+（用于 `eracore_agent` CLI 和测试）
- Node.js 18+（用于 Web 前端 `EraCore.Web/`）

---

## 支持平台与运行模式

| 运行模式 | 入口 | 说明 |
| --- | --- | --- |
| CLI 交互模式 | `EraCore.Cli` | 终端直接游玩，需真实 TTY |
| HTTP Server | `EraCore.Cli --server` | 本地/局域网 Web 服务，浏览器即玩 |
| Windows 桌面 | `EraCore.Maui` | 原生窗口体验，内置 WebView |
| Android APK | `EraCore.Maui` | 移动端游玩（支持 NativeAOT 加速） |
| AI 代理 | `eracore_agent` | 无头操控，供自动化与 AI 使用 |

---

## 文档

- **架构总览** → [ARCHITECTURE.md](ARCHITECTURE.md)
- **Web 前端开发** → [EraCore.Web/README.md](EraCore.Web/README.md)
- **测试套件** → [tests/README.md](tests/README.md)
- **终端行为经验** → [docs/LESSONS/terminal-windows.md](docs/LESSONS/terminal-windows.md)

---

## Agent 集成（CLI + Skill）

Agent 通过中转 CLI `eracore_agent` 操控游戏，不走 MCP。原则：一次调用 = 一个回合，stdout 只有一行 JSON，错误走 stderr + 非零退出码；控制权排他（同一时刻一个 Controller），读取画面需先持有控制权，让权以显式 `release` 为准。

```text
agent
  └─ eracore_agent   (python -m eracore_agent <subcommand>
                     或安装后的 eracore_agent)
       └─ HTTP      /load-game /turn /input /state /control/*
            └─ EraCore.Server 或 MAUI 托管的 HttpListenerHost（issue 05，agent 自动侦测复用）
                 ├─ EraCore.Core（共享层 Server/：协议 + 控制状态机 + 会话）
                 └─ GET /ws  →  Web 旁观（MAUI 托管 server 亦暴露同契约 /ws）
```

`start` 复用或拉起 server。复用优先级（自高到低）：
1. **MAUI 托管 server**（用户正在 MAUI 应用里玩的那一局——读 `%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json` 发现记录，端口/token/游戏目录动态）；命中即复用，不 load_game 抢占。
2. **本地记录** `.eracore-server.json`（上次 `start` 拉起的 standalone server）。
3. `--host:--port` 上已在跑的 server；都没有才用 `--eracore-path` 二进制拉起新 server。

具体用法——启动序列、回合循环、紧凑视图的页语义截断、控制权交接礼仪与错误恢复——见 [`.agents/skills/eracore-playtesting/SKILL.md`](.agents/skills/eracore-playtesting/SKILL.md)。该文档自包含，只凭它即可完成操控；回合/按钮字段的权威来源是 `TurnRecord` / `AgentJsonlProtocol`。

运行期产物，不要提交：

| 文件 | 内容 |
|------|------|
| `.eracore-agent.json` | 路径预设（`binaryPath` / `gameDir`） |
| `.eracore-server.json` | 本次运行记录（`host` / `port` / `pid` / `token` / `gameDir` / `startedByAgent` / `mauiHosted`）。`start` 写入，`stop` 删除，`release` 不删 |

---

## JSONL 协议

无头运行器的 JSONL 协议由 server 模式（`--server`）通过 HTTP 暴露，适合脚本和自动化。创建会话后游戏自动输出初始 turn，之后每发送一条输入命令返回一个 turn。

```python
# 游戏自动输出初始 turn（无需发送任何命令）：
{"text": "标题画面...", "state": "WaitInput", "inputType": "IntValue", "needValue": true, "buttons": [...]}

# 客户端发送输入：
{"type": "input", "value": "0"}

# 服务器响应下一 turn：
{"text": "输出文本...", "state": "WaitInput", "inputType": "IntValue", "needValue": true, "buttons": [...]}
```

参考 `tests/emuera_server.py`（server 模式测试 helper）和 `tests/test_jsonl.py`（示例）。

---

## 构建

### 发布（推荐：`scripts/publish.ps1`）

所有可分发产物统一收集到仓库根 `artifacts/`，完成后脚本打印最终产物路径（exe/apk，非 dll）。
MAUI 目标发布前自动确定性重建前端（`npm run build` → dist-maui → 拷贝 wwwroot），规避旧前端滞后问题
（见 `docs/LESSONS/testing-and-ci.md` #11）。

```powershell
.\scripts\publish.ps1                    # 显示用法
.\scripts\publish.ps1 cli                # CLI 普通发布（框架依赖单文件）→ artifacts/cli/
.\scripts\publish.ps1 cli-aot            # CLI NativeAOT win-x64 → artifacts/cli-aot/win-x64/
.\scripts\publish.ps1 maui-win           # MAUI Windows 普通发布 → artifacts/maui-win/
.\scripts\publish.ps1 maui-win-aot       # MAUI Windows NativeAOT → artifacts/maui-win-aot/win-x64/
.\scripts\publish.ps1 maui-android       # MAUI Android 普通 Release → artifacts/maui-android/<rid>/
.\scripts\publish.ps1 maui-android-aot   # MAUI Android NativeAOT → artifacts/maui-android-aot/<rid>/
.\scripts\publish.ps1 all                # 全部
.\scripts\publish.ps1 list               # 列出已有产物
```

常用选项：
- `-Rid arm64|x64|all`：Android RID 短名（默认 arm64，实际传 `android-arm64`/`android-x64`）
- `-SkipFrontend`：跳过 MAUI 发布前的前端确定性重建（不推荐，可能打包旧前端）
- `-Clean`：发布前清空对应 artifacts 子目录
- `-MsvcLib <path>`：`cli-aot` 手动指定 MSVC LIB（分号分隔三段），不传则自动探测

Android 目标需 Android SDK + JDK 17+（`JAVA_HOME`）；`cli-aot` 需 VS「使用 C++ 的桌面开发」工作负载（脚本自动探测 MSVC）。

### 原生 dotnet 命令（参考，自定义参数/排查时用）

以下为不带脚本封装的原始命令。MAUI 目标直接使用时注意前端滞后问题（见下方 Android NativeAOT 约束 3）。

### CLI & Server 模式（共享 `EraCore.Cli`）

```bash
dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug
```

发布单文件：

```bash
dotnet publish EraCore.Cli/EraCore.Cli.csproj -c Release --no-self-contained -o publish/cli
```

### MAUI Windows 桌面应用

```bash
dotnet build EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

MAUI 不支持 `PublishSingleFile`。发布需框架依赖或独立部署：

```bash
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release --self-contained -r win-x64
```

### Android APK

```bash
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-android -c Release
```

需要 Android SDK + JDK 17+（`JAVA_HOME`）环境。

#### Android NativeAOT（PublishAot）

NativeAOT 将托管代码编译为原生 so（APK 内 0 托管 dll，仅 `lib/<abi>/libEmuera.Maui.so`），启动更快、包体更小，但构建更慢、对反射/序列化有限制。实验验证（2026-08-08）：MuMu 模拟器 + 真机 arm64 均可运行。

```powershell
# x64（模拟器，如 MuMu）——单行命令，PowerShell 不认 bash 的 `\` 续行符
# 无需 -p:TreatWarningsAsErrors=false：csproj 自带 TreatWarningsAsErrors=true，
# 且 PublishAot=true 时经 WarningsNotAsErrors 把 XA1040/IL3053 降级为警告（不阻塞）。
# HTTP 请求/响应 JSON 的 IL2026/IL3050 已于 2026-08-27 在根因清零（源生成重载），
# 故新警告会直接变 error 拦截回归——勿再加 false 覆盖。
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-android -r android-x64 -c Release -p:PublishAot=true -p:RunAOTCompilation=false -p:AndroidPackageFormats=apk -p:PublishDir=artifacts/nativeaot/maui-android-x64/ -p:SkipVueBuild=true

# arm64（真机）
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-android -r android-arm64 -c Release -p:PublishAot=true -p:RunAOTCompilation=false -p:AndroidPackageFormats=apk -p:PublishDir=artifacts/nativeaot/maui-android-arm64/ -p:SkipVueBuild=true
```

**关键约束（务必遵守）：**

1. **目录隔离**：`EraCore.Maui/Directory.Build.props` 在 `PublishAot=true` 时自动把中间产物/输出重定向到 `obj-aot/`/`bin-aot/`，并补回 `DefaultItemExcludes`（`obj/**;bin/**`）——**NativeAOT 与普通构建（Mono Full AOT）互不污染**。不要手动传 `-p:BaseIntermediateOutputPath`（全局属性会传染 Core 项目，且破坏 SDK 默认排除导致 CS0579）。
2. **JSON 序列化必须走源生成**：NativeAOT 下 `System.Text.Json` 反射序列化被禁用（`JsonSerializerIsReflectionDisabled` 抛异常）。壳层消息走 `EraCore.Maui/Json/MauiJsonContext.cs`（具名 record + `[JsonSourceGenerationOptions(CamelCase)]`），协议层走 Core 的 `EmueraJsonContext`（显示/回合/配置）与 `ServerJsonContext`（HTTP 请求 body POCO）。**新增壳层消息禁止匿名类型 `JsonSerializer.Serialize(new {...})`**。
3. **`-p:SkipVueBuild=true`**：跳过 Vue 前端构建（使用 `EraCore.Maui/wwwroot/` 现有产物）。若改了 `EraCore.Web/` 前端代码，需先 `npm run build -- --base=./ --outDir=dist-maui` 并拷贝到 `EraCore.Maui/wwwroot` 再构建（`scripts/publish.ps1` 的 MAUI 目标会在发布前自动完成这步）。
4. **`-p:RunAOTCompilation=false`**：避免 Mono Full AOT 与 NativeAOT 双重编译（`PublishAot=true` 时默认 `RunAOTCompilation=true`，需显式关掉）。
5. **ILC 警告治理**：构建会多出 IL2026/IL3050/IL207x 等 AOT 警告。HTTP 请求/响应 JSON 路径（`HttpRouteDispatcher.Parse<T>` / `GameServerProtocol` 的 `JsonArray.Add`）已于 2026-08-27 在根因清零（改源生成重载）；壳层亦已清零。剩余豁免（DataTable 反射、PluginManager 动态加载、`Results.Json` resolver）逐条登记于 `docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md` §5.2 并附实测/理由。新增警告需登记到 §5.2，不许静默 suppress。
6. **已知风险**：`AndroidEnableMarshalMethods=false`（csproj）与 NativeAOT JNI 通道的兼容性未做压力验证；游戏加载链路（loadGame → turn 渲染）在 NativeAOT 下待完整真机验证（2026-08-08 已通过：进入游戏选择界面，0 FATAL）。

> **注意：** I-12 阶段 1 已启用按路径分级的质量护栏。`EraCore.Core/Shared/`（迁移自 `Emuera/`）下的历史警告已全局抑制。修改自有源码时应关注新引入的 CA/CS 警告。`EraCore.Core` / `EraCore.Server` / `EraCore.Cli` / `EraCore.Maui` 均启用 `TreatWarningsAsErrors`（Maui 在 `PublishAot=true` 时经 `WarningsNotAsErrors` 降级 XA1040/IL3053 两条 SDK 信息性警告）。

---

## 运行

### CLI 交互模式（需真实 TTY）

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --ExeDir <游戏目录> --protocol cli
```

### HTTP 服务器模式（浏览器访问 http://localhost:8080）

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll --ExeDir <游戏目录> --server --port 8080
```

> `--protocol jsonl` 在非 server 模式下会报错；脚本/自动化统一走 `--server`。

### MAUI Windows 桌面应用

```bash
dotnet run --project EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

启动后自动解压内置 `test_game/` 到 AppData，WebView 加载 Vue 前端界面。

### Web 前端

前端为 Vue 3 + TypeScript SPA，开发时由 Vite 代理 HTTP/WS 到 C# Kestrel（:8080）。

```bash
cd EraCore.Web && npm install     # 安装依赖
npm run dev                      # Vite dev server → localhost:5173
npm run build                    # 生产构建 → dist/
npm test                         # Vitest 单元测试（12 个文件）
```

### Web 字体方案（跨平台固定网格排版）

游戏终端依赖「ASCII 半角 0.5em / CJK 全角 1.0em」的固定网格，与 C# 侧 GDI 的 `FontSize/2`、`FontSize` 字符宽度计算对齐。Windows 上 `ＭＳ ゴシック` 提供该度量；但 Android 等平台没有该字体，系统 fallback 字体宽度不一致会导致字符画、按钮行、整行横线排版错乱。

**注意**：MS Gothic 的符号区宽度并非均匀——Box/Geometric/Misc 区段内半角（`◢◣`、各类框线 0.5em）与全角（`●` 1.0em）字符并存，引擎 `IsWideChar` 的"整区全角"判定是简化。因此字体补齐以 **MS Gothic 逐字符实测 advance 为准**（`analyze_unifont_widths.py` 实现），而非按区段一刀切。

解决方案是内置两枚 woff2 字体，通过 `TerminalDisplay.vue` 的 font-family 链逐级回退（`游戏字体名 → EmueraMonoJP → EmueraBlock → ui-monospace…`）：

- **`EmueraMonoJP`**（`src/assets/fonts/IPAGothic.woff2`）— 内置 IPA ゴシック，度量与 MS Gothic 兼容。Windows 上 MS Gothic 存在时行为不变；缺失时（Android 等）落到它保证网格一致。
- **`EmueraBlock`**（`src/assets/fonts/EmueraBlock.woff2`）— 补充字体，覆盖 IPAGothic 缺失的字形，避免逐字形回退到宽度随机的系统字体。字形来源**单一：DejaVu Sans(265 字符,无程序化字形)**:
  - **DejaVu Sans 提取**(全部 265 字符):`analyze_dejavu_widths.py` 列出 IPAGothic 缺失 ∩ DejaVu 覆盖的字符,构建脚本按 **MS Gothic 实测 bbox** 对 DejaVu 字形做仿射缩放(源 bbox → 目标 bbox),使字形视觉大小与 MS Gothic 完全一致;advance 强制 MS 实测值(0.5em/1.0em)。四区全覆盖:Box 96、Block 32(含象限 `▖▗▘▙▚▛▜▝▞▟`)、Geometric 57、Misc 80。平滑矢量轮廓,无位图锯齿(╱╲╳ 斜线、╭╮╰╯ 圆角、☢☯☺ 曲线符号、◢◣◤◥ 三角按 MS bbox 缩放)。许可:Bitstream Vera Fonts 版权 + 自由许可(可嵌入、可再分发)。

字体注册见 `src/styles/fonts.css`(`main.ts` 全局引入)。`EmueraBlock` 由 [`scripts/build_emblock_font.py`](EraCore.Web/scripts/build_emblock_font.py) 生成(DejaVu 单一来源,可复现):

```bash
cd EraCore.Web
# 1. 下载 DejaVu Sans 2.37(约 5MB zip;解压出 DejaVuSans.ttf)
curl -L -o scripts/dejavu/dejavu-fonts-ttf-2.37.zip https://github.com/dejavu-fonts/dejavu-fonts/releases/download/version_2_37/dejavu-fonts-ttf-2.37.zip
"C:/Users/95826/miniconda3/python.exe" -c "import zipfile,os; os.makedirs('scripts/dejavu',exist_ok=True); open('scripts/dejavu/DejaVuSans.ttf','wb').write(zipfile.ZipFile('scripts/dejavu/dejavu-fonts-ttf-2.37.zip').read('dejavu-fonts-ttf-2.37/ttf/DejaVuSans.ttf'))"
# 2. 宽度比对(需 Windows + 本机 MS Gothic):生成"MS bbox 为基准"的提取清单
python scripts/analyze_dejavu_widths.py scripts/dejavu/DejaVuSans.ttf --json scripts/dejavu_width_match.json
# 3. 构建:DejaVu 提取全部符号;缺 DejaVu 时构建失败(无程序化兜底)
python scripts/build_emblock_font.py
```

**发布提醒**（改过前端/字体后）：MAUI 的 wwwroot 由 `build/VueBuild.targets` 从 **`dist-maui/`**（不是 `npm run build` 默认的 `dist/`）复制填充；publish 时**不要带 `-p:SkipVueBuild=true`**（README 示例命令默认带它，那是"未改前端"的场景），否则 wwwroot/APK 沿用旧前端。安装前**先卸载旧 APK**——Android WebView 对 file:// 资源有缓存，覆盖安装可能继续用旧字体。产物验证：`dist-maui/assets/` 里 <4KB 的字体（如 EmueraBlock）会被 Vite 内联为 css `data:font` base64，没有独立 woff2 文件是正常现象，别误判"没打包"。

排查字体覆盖用 [`scripts/check_font_coverage.py`](EraCore.Web/scripts/check_font_coverage.py)：输出 IPAGothic 在 Box/Block/Geometric/Misc 各区的缺失字符，并可对照本机 MS Gothic 的 advance width。字形尺寸验证用 [`scripts/check_glyph_bounds.py`](EraCore.Web/scripts/check_glyph_bounds.py)（跨字体提取后确认轮廓缩放正确）。

IPA 字体许可见 `src/assets/fonts/IPA_Font_License_Agreement_v1.0.txt`（IPA Font License v1.0）。DejaVu Sans 许可（Bitstream Vera Fonts 版权 + 自由许可）见 https://dejavu-fonts.github.io/。字形来源可经 `scripts/dejavu/DejaVuSans.ttf` 复现。

---

## 性能与加载开关（erb-loader-parallel + lazy-function-load）

游戏目录下的 `emuera.config`（格式 `名前:値`，支持日文/英文双名）控制引擎启动性能。两项加载优化项目（erb-loader-parallel 阶段一/二 + lazy-function-load B1）合计贡献 **eraTW 活堆 -583.9 MiB / 冷启 Init -1.1s**，对应三个正交的开关，**默认全部 `true` 开启**，AnalysisMode 下一律强制串行/eager。

关闭顺序（排查语义偏差时，逐层回退到原始串行形态）：`LazyFunctionLoad:NO` → `ParallelParseScript:NO` → `ParallelErbLoad:NO`。

| 开关（日文键名） | 英文键名（等价） | 默认值 | 功能 | 设为 NO 时的效果 |
|---|---|---|---|---|
| ERBファイルを並列して読み込む | Load ERB files in parallel | YES | erb-loader-parallel 03：ERB **文件级并行加载**（分词/解析按文件粒度并发，跨文件警告序按文件名回放到串行等价） | 退化为单线程逐文件加载；等价性与加载期警告逐字节一致 |
| 未呼び出し関数の解析を並列して行う | Parse uncalled functions in parallel | YES | erb-loader-parallel 08（ADR-0024）：ParseScript 三步（setArgument / nestCheck / setJumpTo）对**未调用函数集**并行遍历；主循环 BFS 按层仍保持串行 | 三步退化为单线程串行遍历；警告序不变、语义等价 |
| 未呼び出し関数の本体解析を遅延する | Defer uncalled function body parsing | YES | lazy-function-load（ADR-0025）：加载期**不为未调用函数做 ParseScript**，只注册标签桩 + 行号引用；**首调用时单函数重解析物化**（幂等：二次调用直接命中已物化链） | 回退为加载期 eager 全量 ParseScript；与 `ParallelErbLoad:NO` + `ParallelParseScript:NO` 组合即为原始串行 eager 形态 |

### 关联警告与忽略开关

与两项目正交，但常联动使用：

| 开关（日文键名） | 英文键名（等价） | 默认值 | 取值/说明 |
|---|---|---|---|
| 呼び出されなかった関数を無視する | Ignore uncalled functions | YES | 语义上忽略未被任何路径调用到的函数（加载期仍写入 LabelDictionary 保证跳转解析，只是 ParseScript 不再对其执行语义检查）。与 `LazyFunctionLoad` 正交——后者只**延迟**解析，本开关决定**是否**解析。 |
| 関数が見つからない警告の扱い | Function is not found warning | IGNORE | `IGNORE` / `LATER` / `WARN` / `DISPLAY`。`LazyFunctionLoad:YES` 下，未定义函数的报错仍在加载期跳转解析阶段即刻抛出（不延迟到物化）。 |
| 関数が呼び出されなかった警告の扱い | Function not called warning | IGNORE | `IGNORE` / `LATER` / `WARN` / `DISPLAY`。警告发射时机：加载期 eager 全量 ParseScript 时即刻发射；`LazyFunctionLoad:YES` 下按 `Depth==-1` 深差集零解析导出，**与 eager 同集但可能延迟**（定义性行为，不断言逐字节相等）。 |

### 实测参考（eraTW Release CLI，本机 2026-08-24，3 轮稳态后 2 轮）

| 项 | 三开关全关（原始串行 eager） | 三开关全开（出厂默认） | Δ |
|---|---|---|---|
| 活堆（`dumpheap -stat -live`） | 701.7 MiB | **117.8 MiB** | **-583.9 MiB** |
| 冷启 Init:End（Stopwatch 段） | 3449ms | **2315ms** | **-1134ms** |
| 冷启 Proc:Init:ERB 段 | 2647ms | **1554ms** | **-1093ms** |

验收门禁：Init ≤ 3.0s / 活堆 ≤ 350 MiB（lazy 形态下分别为 2.3s / 117.8 MiB，均通过）。**CI 不加入性能/内存断言**：跨机硬件差异大，以上数值仅作本机复测判读基准（沿 erb-loader-parallel 先例）。

详细测量与门禁见 `.scratch/lazy-function-load/spec.md` 复测节，决策见 [ADR-0024](docs/adr/0024-parsescript-parallel-uncalled-sweep.md) 与 [ADR-0025](docs/adr/0025-lazy-function-materialization.md)。erb-loader-parallel 阶段三候选池因 lazy-function-load 落地已**实质归档**，其 P1（单遍遍历融合）重启触发相应改写为「③ **被否决且仍需压启动**」，见 `.scratch/erb-loader-parallel/spec-phase3.md`。

---

## 测试

测试分三层：**C# 单元测试**（xUnit）、**Python 端到端**（CLI/HTTP/WebSocket）、**前端测试**（Vitest）。

```bash
# C# 单元测试（xUnit，304 用例）
dotnet test EraCore.Tests/EraCore.Tests.csproj

# MAUI 单元测试（11 用例）
dotnet test EraCore.Maui.Tests/EraCore.Maui.Tests.csproj

# 前端测试（Vitest，13 个测试文件，224 用例）
cd EraCore.Web && npm test

# Python 端到端（使用 test_game；相对路径基于仓库根，binary 缺省时脚本会自动查找）
python tests/test_jsonl.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game

# 全部回归测试（先 C# 单测+构建，再全量 Python 套件，14 套件）
python tests/run_all.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game
```

`tests/README.md` 是测试的权威文档。

---

## 项目结构

```
EraCore.Cli/    -- CLI 交互 & HTTP 服务器入口（Exe，唯一可运行的 Headless 入口）
EraCore.Core/   -- 无头核心库（Library，无 AspNetCore 依赖，MAUI 可直接引用）
EraCore.Server/ -- HTTP 服务器组件（Library，引 AspNetCore）
EraCore.Maui/   -- MAUI 桌面/移动应用（Windows + Android，原生 WebView 壳）
EraCore.Web/     -- Vue 3 + TypeScript 浏览器前端（Vite + Pinia + Vitest）
EraCore.Tests/  -- C# 单元测试（xUnit，304 用例）
EraCore.Maui.Tests/ -- MAUI 单元测试（xUnit，15 用例）
Emuera/                 -- WinForms 残留源码（不再维护，仅作只读参考，不可独立构建）
eracore_agent/          -- Python eracore_agent CLI 与 HTTP 客户端
tests/                  -- Python 端到端测试脚本
build/                  -- MSBuild targets（VueBuild.targets 共享）
test_game/              -- 开发用最小 ERB 测试游戏
```

---

## 致谢

- **上游项目**：Emuera（Copyright (C) 2008- MinorShift），以及 fork 来源 **EvilMask 的 Emuera.EM**。
- 社区为 Emuera 生态做出的长期贡献。

---

## 许可证

本项目基于上游 Emuera 的许可证分发。详见 [Readme/License/Emuera.LICENSE.txt](Readme/License/Emuera.LICENSE.txt)。Copyright (C) 2008- MinorShift。社区维护分支。