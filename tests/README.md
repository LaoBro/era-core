# Emuera.Headless 测试说明

本目录用于 `Emuera.Headless` 的自动化验收与回归测试。

当前测试覆盖：

- JSONL 协议流程与 buttons schema（经 server 模式驱动，T-024 后 stdin 管道已废弃）。
- server 单会话 HTTP API。
- Server 控制权交接（acquire/release/steal、输入/生命周期门禁、lease、drain、`/control/wait`、in-flight 409）。
- `eracore_agent` CLI 进程级冒烟（start → acquire → step → release → stop）。
- TINPUT timeout server 场景。
- fatal turn 脚本异常路径（THROW）。
- I-11 脚本退出后 server 存活。
- `/assets` 资源通道（issue 03）：路径消毒、扩展名白名单、缓存/CORS 头、协议帧 image segment 几何。
- ERB 加载并行/串行等价性（erb-loader-parallel issue 04）：同一游戏分别以并行（默认）与串行
  fallback 加载，断言 ParseScript 三数统计（非注释行数 / 全函数合计 / 被呼出函数数）与规范化警告
  列表（按文件 + 行号排序）完全一致，并自检「注入标签差异能变红」以证明套件区分能力。
- CLI 渲染布局（贴底）判据：`test_cli_bottom_anchor.py` / `test_cli_mouse_geometry.py` /
  `test_cli_button_click.py`，在 ConPTY 中黑盒验证（详见「CLI 渲染布局判据」一节）。
- **Web 前端布局（贴底）判据**：`EraCore.Web/e2e/bottom-anchor.spec.ts`，Playwright + 系统 Edge
  在**真实浏览器**里量 `boundingBox`（vitest 的 happy-dom 无布局引擎，量不了；详见
  「Web 前端布局验证（Playwright）」一节）。注意它**不在** `run_all.py` 里——`run_all.py` 只编排
  .NET 与 Python 套件，Web 侧需单独跑 `npm run test:e2e`。

## 测试游戏目录

新增或修订回归测试时，应使用仓库根目录的 `test_game` 文件夹作为测试游戏来源：

- 直接运行测试时，使用 `--game-dir test_game`。
- 不建议依赖开发者本机安装的其他 Emuera 游戏资源。
- 如 `test_game` 当前 ERB/CSV 场景不足以覆盖新增行为，应先补充 `test_game` 的测试资源，再扩展测试脚本。
- `test_game` 目前保留在仓库根目录，作为项目级 fixture；`tests/` 内的脚本通过 `PROJECT_DIR / "test_game"` 或 `--game-dir` 引用它。TINPUT timeout 测试会复制一份临时游戏目录，避免修改原始 `test_game`。

当前测试与 `test_game` 的耦合点：

- `test_jsonl.py` 依赖 `test_game/erb/TEST.ERB` 中的菜单文本和按钮值。
- `test_server_single_session.py` 通过 `tests/emuera_server.py` 固定使用根目录 `test_game`。
- `test_tinput_timeout.py` 会从根目录 `test_game` 复制临时副本，再覆盖 `erb/TEST.ERB` 构造 TINPUT 场景。
- `test_force_quit_survival.py` 通过 `tests/emuera_server.py` 启动 server，分别用根目录 `test_game` 与临时副本覆盖 ERB。
- `test_assets.py` 依赖 `test_game/img/test.png`（8×4 RGBA 夹具，四角已知色）与 `TEST.ERB` 中的 `PRINT_IMG` 行。

因此，这些测试确实与 `test_game` 强相关；目前没有必要把整个 `test_game` 移进 `tests/`，更适合保持根目录 fixture，并在测试 helper 中集中管理路径。

## 常用测试

构建后可运行单个测试：

```bash
python tests/test_jsonl.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game
python tests/test_server_single_session.py
python tests/test_tinput_timeout.py
python tests/test_fatal_turn.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game
python tests/test_force_quit_survival.py
python tests/test_assets.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game
python tests/test_cli_basic.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game
python tests/test_cli_basic.py --game-dir test_game  # 自动查找 binary
```

也可以用一个入口运行常规回归：

```bash
python tests/run_all.py --binary EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe --game-dir test_game
```

Web 前端（Vue）的两套测试单独跑，不经过 `run_all.py`：

```bash
cd EraCore.Web
npm test          # vitest：组件行为（706 条，happy-dom，无布局）
npm run test:e2e  # Playwright：真实浏览器布局判据（自动起 vite dev，用系统 Edge）
```

`run_all.py` 会顺序执行：

1. JSONL 流程 + buttons schema 测试（经 server 模式驱动）。
2. server 单会话测试。
3. 控制权交接测试（`test_control_handoff.py`）。
4. `eracore_agent` CLI 进程级冒烟（`test_eracore_agent.py`）。
5. TINPUT timeout 测试。
6. fatal turn 测试（脚本异常路径）。
7. I-11 exit survival 测试。
8. `/assets` 资源通道测试（issue 03）。
9. ERB 加载并行/串行等价性测试（issue 04，`test_erbloader_equivalence.py`）。
10. CLI 交互模式基础测试（happy path + ConPTY smoke tests）与贴底布局判据：
    `test_cli_basic.py`、`test_cli_scroll.py`、`test_cli_clearline_reprint.py`、
    `test_cli_bottom_anchor.py`、`test_cli_mouse_geometry.py`、`test_cli_button_click.py`
    （后三个见「CLI 渲染布局判据（贴底）」一节）。
11. 一致性套件（`test_conformance_kit.py`，用 `test_game` 自检，见下）。

在 Windows 命令行中，如果相对路径启动失败，请使用绝对路径，例如：

```bash
D:/LaoBro/era-core/EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.exe
```

## 方言与面快照测试

方言相关 C# 测试分四层：

| 测试 | 覆盖 |
|---|---|
| `DialectProfileTests` | profile 闭包、可见面并集/隐藏、会话隔离、投影端到端 |
| `DialectSurfaceSnapshotTests` | 三 profile 运行期面与 `tools/dialect-inventory/surface.*.txt` 逐位比对 |
| `DialectCrossForkConflictTests` | 同键多 owner 的 override/priority 裁决 |
| `P0*Tests` | 各 P0 家族的解析 fixture、执行 fixture、flag 反射、profile 可见性 |

```bash
# 全部方言相关
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~Dialect
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~P0
```

新增/删除方言键后，先确认 `v24pure` 面未变化，再显式重写快照：

```powershell
$env:ERACORE_DIALECT_SURFACE_UPDATE=1
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~DialectSurfaceSnapshotTests
Remove-Item Env:ERACORE_DIALECT_SURFACE_UPDATE
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~DialectSurfaceSnapshotTests
```

快照口径与键清单见 [`docs/reference/dialect-surface.md`](../docs/reference/dialect-surface.md)，移植流程见 [`docs/guides/dialect-porting.md`](../docs/guides/dialect-porting.md)。

## 一致性套件 + DoD 单入口（实现票 25）

参数化 `--game-dir <dir> [--profile <id>]` 的可机械执行验收，含三层：**解析 smoke**（零「无法解析的标识符/行」+
零未定义函数警告 + 无未预期 `warnLevel≥2`）、**面快照**（复用票 17 `DialectSurfaceSnapshotTests`）、
**playtest 冒烟**（以目标 profile 加载并进入输入循环）。game→profile 为**显式映射**（`tests/games.json`
的 `matchHints → profile`，不建 resolver）；`anchors` 为内容锚点冒烟（防误配），`knownLv2` 为经参考引擎
判定为「游戏数据缺陷」的已知 Lv2 子串，逐条排除于 DoD。

```bash
# 仓库内自检（test_game → v24pure）
python tests/test_conformance_kit.py --game-dir test_game --no-surface

# DoD 单入口（eraTW 画蛇添足版：自带 build + 面快照）
python tests/test_conformance_kit.py --game-dir D:/era/eratw-sub-modding-develop --build
```

**新增一个游戏只加一条 `tests/games.json` fixture，不改 harness 代码**（`conformance_kit.py` /
`test_conformance_kit.py`）。误配 profile 时 `--profile` 覆盖 fixture 会打印 WARN，且解析 smoke 会因
大量未识别标识符而 RED。

## 大小写敏感关卡（WSL2 Ubuntu）

Windows 的文件系统与 .NET 托管 glob 都不区分大小写，「文件名大小写 / 通配 pattern / 路径分隔符」类缺陷在本地 `dotnet test` 里**不可能复现**（曾出现 Windows 1065/1065 全绿、真机 legacy 一进新游戏就崩）。命中下列任一改动时，必须把同一套单测再跑一次真大小写敏感环境（WSL2 Ubuntu + ext4）：

```bash
# 把 /mnt/d/<repo> 换成仓库在 WSL 下的路径；命令里不要出现 $（外层 shell 会先展开 $PATH）
# SDK 装在 ~/.dotnet
wsl -d Ubuntu -- bash -lc "cd /mnt/d/<repo> && ~/.dotnet/dotnet test EraCore.Tests/EraCore.Tests.csproj"
```

触发清单：

- `FileSystemGameDirAccessor` / `SafGameDirAccessor` / `IGameDirAccessor`
- `Config.GetFiles` 或任何 `GetFiles(..., searchPattern, ...)` 调用点
- `SafPath`（相对名 ↔ 物理路径、分隔符）
- `Wildcard` / `Preload` 树枚举

判读口径：WSL 下**基线固定 5 个既有失败 + 时序/并发类偶发 flaky**，只有不在名单里、且单跑也不通过的失败才算回归。失败名单与成因见 [`docs/LESSONS/testing-and-ci.md`](../docs/LESSONS/testing-and-ci.md) #12。

## JSONL 协议测试

`test_jsonl.py` 通过 `tests/emuera_server.py` 的 `start_server` 启动 server 模式：

```bash
EraCore.Cli.exe --server --port <port> --ExeDir <game-dir>
```

T-024 后 stdin 管道 JSONL 模式已废弃，`AgentJsonlProtocol` 仅由 server 模式的 `Session` 通过 `HttpSessionIO` 驱动。turn 走 `diff` 增量（无顶层 `text`/`ops`）。

**v12（issue 05 / 09）起 wire 契约**：七个旧顶层字段（`state` / `inputType` / `needValue` /
`timeLimit` / `displayTime` / `timeUpMessage` / `timedOut`）已退场，唯一必填契约字段是
`awaiting`（三键：`input` / `wait` / `ended`，互斥终态 `ended` 优先）。测试侧用
`emuera_server.turn_state()` / `awaiting_input_type()` 等 helper 做语义桥接（`state=="WaitInput"`
↔ `awaiting.input`，`state=="Quit"` ↔ `awaiting.ended.terminal`），`PROTOCOL_VERSION` 常量与
`TurnRecord.CurrentProtocolVersion` 同步（当前 12）。

测试流程：

1. `POST /load-game` 建立会话。
2. `GET /turn` 读取初始 turn。
3. 校验 `protocolVersion`、`awaiting`、`diff`；校验旧顶层字段（`state`/`inputType`/…）不出现。
4. 校验按钮包含 `label` 和整数 `value`（append op 内嵌 button，含 `col`/`width` 几何）。
5. `POST /input {"value":"0"}` 推进游戏。
6. 验证第二轮菜单和最终 `Quit` 状态（`awaiting.ended.terminal == "Quit"`）。

## CLI 交互模式（ConPTY smoke test）

T-024 后 stdin 管道 CLI 模式已废弃，`AgentCliProtocol` 仅支持交互式终端（VT 路径或 `ConsoleKey` 降级路径）。交互式 CLI 的终端渲染通过 `test_cli_basic.py` 在 Windows ConPTY（pywinpty）中进行黑盒验证。

`test_cli_basic.py` 通过 `PtyProcess.spawn` 启动 CLI 子进程，捕获原始 ConPTY 输出（含 VT 序列），验证以下路径：

| 测试 | 注入 ERB | 验证点 |
|------|----------|--------|
| happy path | 标准 TEST.ERB（PRINTL/INPUT/QUIT） | 菜单文本、按钮选择、推进到第二屏 |
| `clearline` | PRINTL → CLEARLINE 1 → PRINTL | 被删行内容不在输出中；后续行正常显示 |
| `clear` | 启动时 ClearOp | `\x1b[2J` 转义序列已发出；游戏文本正确显示 |
| `merge` | 连续两次 PRINT（无 NewLine） | "HelloWorld" 作为合并字符串出现 |
| `alignment` | PRINTC "AlignCheck" | 文本前有前导空格（PrintC 对齐填充，默认 25 字符） |
| `setbg` | SETBGCOLOR 0xFF0000 → PRINTL | `\x1b[48;2;255;0;0m` VT 背景色 escape 出现在输出字节流中 |

> **注意：** CLEAR 指令是 C# 内部调用（`ClearDisplay()`），非可用 ERB 命令；`test_cli_clear` 验证的是启动时 `ConsoleStateManager.Initialize()` 发出的系统 ClearOp。`test_cli_setbg` 仅对 VT 路径生效（ConPTY 默认开启 ANSI），非 VT 路径无背景色能力，不覆盖。

非 Windows 或无 pywinpty 时自动跳过。`run_all.py` 已包含 CLI 测试。

## CLI 渲染布局判据（贴底，2026-10-14）

CLI 侧渲染已改为**贴底**（内容末行恒锚 `visibleLines − 1`，与原版 Emuera 一致）。以下三个脚本把
spec §7 判据表的 CLI 列全部机械化，合计 27 条断言（`run_all.py` 已包含）：

| 脚本 | 判据 | 要点 |
|------|------|------|
| `test_cli_bottom_anchor.py`（18） | 内容末行位置 / 上方留白 / 满屏不变 | 29 与 33 行两种终端各跑一遍 |
| `test_cli_mouse_geometry.py`（5） | `MY` 顶行 `−H`（−608）/ 末行 `−LH`（−19）、点击按钮取值 42 | ERB `PRINTFORML MY={MOUSEY()}` 把引擎换算结果打印出来，Python 从重建屏幕抓值 |
| `test_cli_button_click.py`（4） | 点击渲染行提交、点击第 0 行不提交 | 首个真正点击按钮的 E2E；走 `OnMouseEvent → HitTest(SGR 绝对行)` |
| `test_cli_mouse_keystate.py`（7） | 按住左键 ⇒ `GETKEY(1)` 置位 + `GETKEYTRIGGERED(1)` 成立；**60ms 快速单击**同样成立 | 2026-10-15 M4b：VT 鼠标事件接入 `IInputHost` 键状态。**翻掉了下面第 2 条旧限制** |

两个踩坑（已写进代码注释）：

1. **终端高度不受 pywinpty 控制**：`HeadlessRunner` 启动时会用
   `TrySetConsoleSize(gameColumns, gameRows + 4)` 覆盖，`setwinsize` 与 `spawn(dimensions=)`
   **均无效**。实测高度由游戏配置决定：`H = WindowY / LineHeight + 8`（480→33、400→29）。
   故测试改 `emuera.config` 的 `ウィンドウ高さ` 来切换终端尺寸，并断言实测 H 与期望一致
   （环境变化会响，不会静默假绿）。
2. **`{MOUSEY}` 会报错**（`MOUSEY` 是方法）⇒ 用 `{MOUSEY()}`。而 `MOUSEB()` 在回合推进后
   命中区被清空、读到空串，等待期读取又需自定义 `DO/AWAIT` 循环
   ⇒ `MOUSEB` 的判据以「点击按钮行 ⇒ `RESULT` = 42」表达。
   <br>⚠️ 2026-10-15 更正：原文此处还写着「CLI 点击**不走** `GETKEYTRIGGERED`」——
   M4b 后这句**已不成立**（`test_cli_mouse_keystate.py` 就是它的判据）。
   `MOUSEB` 仍用「点击取值」表达，是为了绕开「等待期读值需自建 `DO/AWAIT` 循环」，
   不是因为 `GETKEYTRIGGERED` 不可用。

## Web 前端布局验证（Playwright，真实浏览器）

### 为什么需要它

vitest 用 happy-dom，**没有布局引擎**，且 SFC `<style scoped>` 不注入测试 DOM ⇒
**纯 CSS 的布局在单测里量不出来**。贴底（`.terminal-content` 的
`min-height:100%` + `justify-content:flex-end`）恰恰是纯 CSS 的，所以在 vitest 侧只能做
**源码契约断言**（读 `TerminalDisplay.vue` 源码，锁 `min-height: 100%`、
`justify-content: flex-end`、`.term-virtual-spacer` 的 `flex: none`）—— 它能防误删，
但**证明不了真实布局**。Playwright 补上这一环：在真实浏览器里读 `boundingBox`。

### 怎么跑

```bash
cd EraCore.Web
npm run test:e2e                      # 全部 e2e
npx playwright test e2e/bottom-anchor.spec.ts   # 单个 spec
npx playwright test --headed          # 需要肉眼看时（默认 headless）
```

浏览器用**系统 Edge**（`playwright.config.ts` 的 `channel: 'msedge'`）—— **无需下载
`npx playwright install` 的 ~170MB Chromium**；本机没装 Edge 时可改 `channel: 'chrome'`，
或去掉 `channel` 并先执行 `npx playwright install chromium`。
Playwright 会自己起 vite dev server（`webServer`，端口 5178，`reuseExistingServer: true`）。

### 设施构成

| 路径 | 作用 |
|------|------|
| `EraCore.Web/playwright.config.ts` | `channel:'msedge'` + `webServer`（dev server）+ `testDir: './e2e'` |
| `EraCore.Web/harness/layout.html` | 夹具页：`#app` 定高 600px → flex 列，复刻 App.vue 的高度链（`min-height:100%` 要能解析，父级必须定高） |
| `EraCore.Web/harness/layout.ts` | 挂载真实 `TerminalDisplay`，暴露 `window.__setLines(n)` 切换内容行数（返回 Promise，`page.evaluate` 可直接 await） |
| `EraCore.Web/e2e/bottom-anchor.spec.ts` | 3 条布局判据 |

夹具要点：**不 mock** `useVirtualScroll` —— 真实布局依赖它算出的 `totalHeight`；
而 vitest 组件测试里它是被 mock 的，这也是两者不能互相替代的原因。

### 当前判据（容器 600px / 行高 19px ⇒ 约 31 行可见）

| 用例 | 断言 |
|------|------|
| 空屏 | `.term-virtual-spacer` 高度 == 0（空白可绘制区，与原版 Emuera 启动初期一致） |
| 稀疏（3 行） | spacer **底边** == 内容区底边（贴底），内容高 < 容器高，上方留白 == 容器高 − 内容高 |
| 溢出（60 行） | spacer **顶边** == 内容区顶边（`baseRow = 0`，与改动前逐字节一致） |

断言用 2px 容差（取整与亚像素布局）。

### 踩过的坑

1. **`createApp({ template })` 渲染为空** —— 项目用的 Vue 构建不含运行时模板编译器
   （控制台警告 `runtime compilation is not supported`）。夹具必须改用 `h()` 渲染函数。
2. **溢出用例比错了参照**：溢出时 `.terminal-content` 会随内容长到与 spacer 同高（都是 1140），
   要与**视口** `.terminal`（600px）比，不能与 `.terminal-content` 比。
3. `test-results/` / `playwright-report/` 已加进 `.gitignore`（失败截图与 trace 不入库）。

### 新增布局断言时

- 量的是同一组件 ⇒ 优先扩 `harness/layout.ts` 的开关（如 `__setLines` 之外再加
  `__setScale` / `__setBgImages`），而不是新建夹具页；
- 断言写**几何关系**（底边对齐 / 顶边对齐 / 高度差），不要写死像素值（字体、缩放下会漂）；
- 同步补一条 vitest 源码契约断言（防 CSS 被整段删掉时 e2e 没跑到）。

## TINPUT timeout 测试

`test_tinput_timeout.py` 会从仓库根目录的 `test_game` 复制一份临时游戏目录，并在副本中写入 TINPUT timeout 场景，以避免修改原始 `test_game` 文件。

## Server 测试

`test_server_single_session.py` 使用仓库根目录的 `test_game` 启动 `EraCore.Cli --server`，验证：

- 空闲态 `POST /session` 返回 `503`（须先 `POST /load-game`）。
- `POST /load-game` 建立会话；活跃期间再次 `POST /session` 返回 `409`。
- 输入、turn 拉取、删除会话后回到空闲态（再 `POST /session` 仍为 `503`）。

`test_tinput_timeout.py` 同样通过 server 模式验证 timeout turn 不会吞掉后续输入。
`test_force_quit_survival.py` 验证 `@QUIT` / `FORCE_QUIT` 后 server 存活并能重启 session。
