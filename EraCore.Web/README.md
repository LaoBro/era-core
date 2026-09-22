# EraCore.Web 前端导航

EraCore.Web 是 Vue 3 单页应用，使用 Vite 构建、Pinia 管理状态、Vitest 执行测试。
它支持两种运行模式：

- HTTP 模式：通过 WebSocket 连接 `EraCore.Server`。
- MAUI 模式：通过 `mauiBridge.ts` 与 C# `IJsBridge` 通信，不使用 HTTP/WS。

## 开发命令

```bash
npm install
npm run dev
npm run typecheck
npm test
npm run build
```

开发模式下，Vite 默认运行在 `http://localhost:5173`，并将 `/ws` 和 HTTP 请求代理到 C# 服务的 `8080` 端口。

## 页面入口

| 文件 | 职责 |
|---|---|
| `src/App.vue` | 应用初始化、壳层组合（AppShell/悬浮钮）、HTTP/MAUI 分流、快速重开和退出游戏；游戏页两端共用同一壳层 |
| `src/views/TerminalView.vue` | 终端页面外壳、连接/加载状态展示 |
| `src/views/SettingsView.vue` | 设置页面 |
| `src/views/DebugView.vue` | 原始回合、协议和调试信息展示 |
| `src/main.ts` | Vue、Pinia 和应用入口初始化 |

## 组件导航

| 文件 | 职责 |
|---|---|
| `src/components/TerminalDisplay.vue` | 渲染文本、按钮、图片、背景、虚拟滚动和终端点击推进 |
| `src/components/SegmentRenderer.vue` | 渲染文本、图片和图形 segment |
| `src/components/InputBar.vue` | 手动输入、TINPUT 倒计时和输入提交 |
| `src/components/GameLibraryView.vue` | MAUI/Web **共用**的游戏选择页（单一设计源）：hero/路径行/游戏列表/空状态，经 `GameLibrarySource` 抽象取数 |
| `src/components/ChangeDirDialog.vue` | Web/HTTP 的「更改目录」对话框：手动路径输入 + 页面内目录浏览（`/game/dirs`），确认后经 `source.scan` 扫描（MAUI 不走此对话框，直接原生选择器） |
| `src/components/MauiGameList.vue` | MAUI 游戏选择页 wrapper：右上角 ⋮ 菜单（更改目录→原生 FolderPicker/SAF / 重新扫描 / 主题），传输走 C# 桥接 |
| `src/components/WebGameList.vue` | Web 游戏选择页 wrapper：右上角 ⋮ 菜单与 MAUI 一致（更改目录→手动输入对话框 / 重新扫描 / 主题），传输走 C# server（/game/scan、/game/dirs） |
| `src/components/TinputCountdown.vue` | TINPUT 倒计时显示 |
| `src/components/AppShell.vue` | 根布局壳层：全局背景、主内容与全局对话框挂载点 |
| `src/components/DrawerMenu.vue` | 游戏页更多抽屉菜单（MAUI/Web 共用）：头部游戏标题、快速重开、缩放（含恢复）、视图切换、退出；右滑/点遮罩/Escape 关闭 |
| `src/components/ConfirmDialog.vue` | 退出 / 确认 Alert Dialog（遮罩 + 危险文字按钮，Escape/返回取消） |
| `src/components/StatusBanner.vue` | 顶部可关闭提示条：错误 / 警告 / 成功 / 信息 |
| `src/components/SpectatorBanner.vue` | 旁观横幅（MAUI/Web 共用）：Agent 操控提示 + 接管按钮 |
| `src/components/GameRow.vue` | 游戏选择页列表项（文件管理器式整行）；issues/05 IR 缓存存在性徽章（大小 + 构建时间 tooltip） |

## 状态管理

| 文件 | 职责 |
|---|---|
| `src/stores/game.ts` | 游戏目录、服务器状态、回合数据、显示快照、加载和快速重开 |
| `src/stores/connection.ts` | HTTP/WS 地址、连接生命周期、输入和回合请求、控制权状态（旁观/接管） |
| `src/stores/ui.ts` | 当前页面、平台、缩放和手动输入面板状态、**触控排版开关与两个横向阈值**（含 `TOUCH_TARGET_MIN_PX` 与 `TOUCH_TARGET_COLS_*` / `TOUCH_MIN_GAP_COLS_*` 档位常量） |

关键状态：

- `game.gameDir`：当前加载的具体游戏目录。
- `game.mainGameDir`：MAUI 游戏库的主目录。
- `game.scannedGames`：游戏库列表（`GameEntry`）；issues/05 每条携带 IR 缓存存在性提示 `irCached` / `irCacheBytes` / `irCacheBuiltUtc`（来自 `gamesScanned` 消息，HTTP `/game/scan` 不含 → 缺失 = 无提示）。
- `game.serverState`：`Idle`、`Loading`、`WaitInput`、`Quit`、`Error`。
- `game.displayState`：终端当前显示内容。
- `game.reloadStatus`：游戏加载/快速重开是否正在进行。
- `game.displayReportOverride` / `game.perfOverrides`：启动日志与性能（懒加载/IR/并行两项）覆盖三态。
- `game.sweepGatePolicy` / `game.safReadDegree` / `game.maxParallelErbFiles` / `game.dimLockPolicy`：ADR-0026 诊断旋钮（设置页「诊断」分组，`SettingsView.vue` 渲染，`mauiBridge.ts` 的 set* 消息投递，`useAppInit.ts` 按词表校验 config 消息）。
- `ui.touchLayout`：触控排版开关（localStorage `app.touchLayout`，Android 默认开 / Web 默认关）；入口有**两处**——右上角常驻按钮组的 ◎ 钮（与 ⌨ / ⋮ 并列，第一下就点到）与抽屉菜单「触控排版」项，两处共享同一状态。
- `ui.touchTargetCols` / `ui.touchMinGapCols`：触控排版的横向阈值（列，设置页「外观」可调）。默认 5 列 / 2 列，各自限定在候选档位内（越界或非法值回落默认）。

**触控排版的排版规则**：只把**含有效按钮**的行撑到 ≥48px，历史文本行保持原行距（行高因此不均，由 `useVirtualScroll` 的 `lineExtras` 稀疏加高表吸收）。横向热区**按需外扩**——按钮自身已达目标列数就零外扩，与邻居的余量不足最小间隔列数时朝该侧零外扩；外扩靠 `padding` 与**等量负 `margin`** 相消，因此文字位置一格都不动，只有热区与圆角提示变大（圆角矩形就画在同一个盒子上，故提示范围 == 真实热区）。几何全部来自快照既有的 `ButtonRef.col` / `width`，零 DOM 测量——见 `src/lib/touchLayout.ts`。

## Composable 和通信层

| 文件 | 职责 |
|---|---|
| `src/composables/useAppInit.ts` | 应用启动、MAUI 回调注册、HTTP 状态恢复 |
| `src/composables/useGameStatusMonitor.ts` | MAUI 游戏线程存活状态探测 |
| `src/composables/useVirtualScroll.ts` | 终端虚拟滚动和底部跟随（可选 `lineExtras` 支持稀疏加高行） |
| `src/composables/usePinchZoom.ts` | 终端双指缩放 |
| `src/lib/mauiBridge.ts` | Vue 与 MAUI C# 桥接消息、游戏扫描和加载 |
| `src/lib/gameLibrary.ts` | 游戏库数据源抽象：`GameLibrarySource` 接口 + maui（桥接）/ http（HTTP）两个 adapter |
| `src/lib/inputRouting.ts` | 按钮点击和终端点击的输入路由判定（含 `isLiveButton`——「有效按钮」的单一判据） |
| `src/lib/touchLayout.ts` | 触控排版的热区外扩几何（纯函数，按 `ButtonRef.col`/`width` 列几何算，零 DOM 测量） |
| `src/lib/parseTurnRecord.ts` | 解析 C# 回合协议 |
| `src/lib/opsApplier.ts` | 应用终端增量操作 |
| `src/lib/snapshotReducer.ts` | 应用完整终端快照 |
| `src/lib/resourceResolver.ts` | HTTP/MAUI 游戏资源 URL 解析 |

## 常见修改入口

| 修改内容 | 首先查看 |
|---|---|
| 修改终端空状态、文字、按钮或点击行为 | `components/TerminalDisplay.vue` |
| 修改终端图片、矩形、背景和资源路径 | `components/SegmentRenderer.vue`、`lib/resourceResolver.ts`、`lib/imageLayout.ts` |
| 修改游戏选择和加载流程 | `components/GameLibraryView.vue`、`components/ChangeDirDialog.vue`、`stores/game.ts`、`lib/mauiBridge.ts` |
| 修改快速重开或退出游戏 | `App.vue`、`stores/game.ts`、`lib/mauiBridge.ts` |
| 修改控制权（旁观/接管）状态 | `stores/connection.ts`、`components/SpectatorBanner.vue`、`App.vue` |
| 修改手动输入和按钮输入 | `components/InputBar.vue`、`lib/inputRouting.ts` |
| 修改 TINPUT 倒计时 | `components/TinputCountdown.vue`、`stores/game.ts` |
| 修改终端滚动或缩放 | `composables/useVirtualScroll.ts`、`composables/usePinchZoom.ts`、`TerminalDisplay.vue` |
| 修改按钮点击区域 / 触控排版规则 | `components/TerminalDisplay.vue`（`.term-btn-touch` + 加高表）、`lib/touchLayout.ts`（外扩几何）、`lib/inputRouting.ts`（`isLiveButton`）、`stores/ui.ts`（开关与阈值） |
| 修改 MAUI 启动和 C# 消息处理 | `composables/useAppInit.ts`、`lib/mauiBridge.ts` |
| 修改页面导航、顶部按钮或模式分流 | `App.vue`、`components/DrawerMenu.vue` |
| 修改 UI 主题、颜色、圆角、阴影、动效或字号 | `styles/theme.css`（唯一令牌源，组件一律 `var(--token)`） |
| 修改弹窗、菜单或确认框 | `components/PopupMenu.vue`、`components/DrawerMenu.vue`、`components/ConfirmDialog.vue` |
| 修改菜单首次打开的卡顿优化（或其开关） | `lib/prewarmMenuShadow.ts`、`stores/ui.ts`（`menuPrewarm`，纯前端 localStorage 开关）、`views/SettingsView.vue`；取舍与实测见 `.scratch/menu-first-open-jank/spec.md` |

## 数据流

### HTTP 模式

```text
App.vue
  -> WebGameList（选择页）/ game-shell-controls + DrawerMenu（游戏页）
  -> connection.ts
  -> WebSocket (turns) + GET /control + GET /control/wait (control events)
  -> parseTurnRecord.ts
  -> game.ts
  -> TerminalView
  -> SpectatorBanner / TerminalDisplay / InputBar
  -> POST /control/acquire (接管) / POST /input (用户输入)
```

### MAUI 模式

```text
App.vue
  -> MauiGameList（选择页）/ game-shell-controls + DrawerMenu（游戏页）
  -> mauiBridge.ts
  -> C# BridgeHost / IJsBridge
  -> window.__onTurn / window.__onMessage
  -> useAppInit.ts
  -> game.ts
  -> TerminalView
```

### 游戏选择后的 MAUI 启动流程

```text
MauiGameList.onPickGame()
  -> game.setGameDir()
  -> game.reset()
  -> loadGameFromPath()
  -> C# OnReloadGame
  -> 重建 BridgeHost 并启动游戏循环
  -> PostTurn
  -> game.applyTurn()
  -> TerminalDisplay 渲染输出
```

### 更改目录（两平台右上角 ⋮ 菜单，交互对齐）

```text
MAUI ⋮ 菜单「更改目录」
  -> 直接原生 FolderPicker/SAF（bridge folderPicked → setMainGameDir + scanGames）

Web ⋮ 菜单「更改目录」
  -> ChangeDirDialog（手动路径输入 + 浏览 /game/dirs 填框）
  -> source.scan(path)（/game/scan）
  -> gamesScanned 回流 -> game.setScannedGames -> 列表刷新
```

## 测试导航

组件测试位于对应目录的 `__tests__` 子目录：

| 测试文件 | 覆盖内容 |
|---|---|
| `src/components/__tests__/TerminalDisplay.test.ts` | 终端文本、图片、图形、背景、按钮、MAUI 空状态和触控排版（按钮行的撑高规则 + 横向按需外扩的列几何） |
| `src/stores/__tests__/connectionControl.test.ts` | HTTP 控制权：旁观/接管、事件序列、输入 409 |
| `src/stores/__tests__/game.test.ts` | 游戏状态、回合和目录状态 |
| `src/stores/__tests__/gameLoadGame.test.ts` | HTTP 游戏加载 |
| `src/stores/__tests__/gameQuickRestart.test.ts` | HTTP 快速重开 |
| `src/stores/__tests__/gameExitHttp.test.ts` | HTTP 退出游戏（DELETE /session + 清状态 + 重扫、二次进入保护） |
| `src/composables/__tests__/useAppInit.test.ts` | HTTP/MAUI 初始化分流 |
| `src/lib/__tests__/parseTurnRecord.test.ts` | 回合协议解析 |

修改组件或 Store 后，至少运行：

```bash
npm run typecheck
npm test
```

新增组件、状态字段或通信消息时，应同时更新本导航文档中的对应表格和数据流。
