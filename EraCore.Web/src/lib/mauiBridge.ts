/**
 * MAUI WebView 桥接工具——issue 07 / spec ID7。
 *
 * Vue 端按 `window.location` 判断 MAUI 环境（不注入 `__MAUI__` flag，消除注入 race）：
 * - `ms-appx-web:` 协议 → Windows MAUI packaged（MSIX 包内）
 * - `file:` 协议 → Android MAUI（`file:///android_asset/`）
 * - `https:` 协议 + host `app.local` → Windows MAUI unpackaged（WebView2 虚拟主机映射）
 * - 其他 `http:` / `https:` → HTTP 模式（开发/生产浏览器模式）
 *
 * MAUI 模式下：
 * 1. 注册 `window.__onTurn = (turn) => applyTurn(JSON.stringify(turn))`——
 *    C# 侧 `IJsBridge.PostTurn` 调 `window.__onTurn(turnJson)` 作为 JS 字面量传参（JSON ⊂ JS 字面量），
 *    Vue 端 `JSON.stringify` 还原为字符串后调 `game.applyTurn(rawJson)` 复用 HTTP 模式的协议消费链路。
 * 2. `postInput` 用 feature detection 选平台——
 *    Windows: `window.chrome.webview.postMessage(json)`（CoreWebView2 WebMessageReceived）
 *    Android: `window.appBridge.postMessage(json)`（AddJavascriptInterface 注册的桥接对象）
 * 3. 启动后 `postMessage(JSON.stringify({type:'ready'}))`——
 *    C# 侧 `BridgeHost.OnInputFromJs` 收到后写日志（T07），T08 接入游戏循环后启动 `GameLoopComposer.RunAsync`。
 * 4. issue 09 文件选择器 / game-library spec ID9：`pickGameFolder()` 请求 C# 弹原生 FolderPicker →
 *    C# 经 `PostMessage` 推 `{"type":"folderPicked","path":...}` →
 *    `registerMessageHandler` 注册的 handler 收到后调 `setMainGameDir(path)` + `scanGames(path)`
 *    （game-library spec ID9 修订：原 issue 09 的 `loadGameFromPath(path)` 语义已废弃，
 *    `pickFolder` 现仅用于「更改主目录」，加载游戏改由点击列表项触发 `loadGameFromPath`）。
 */

/**
 * Windows MAUI unpackaged 模式下的虚拟主机名（与 WindowsJsBridge.VirtualHostName 对齐）。
 * WebView2 的 SetVirtualHostNameToFolderMapping 把此主机映射到输出目录 wwwroot/。
 */
const MAUI_WINDOWS_VIRTUAL_HOST = 'app.local';

/**
 * Android MAUI 页面 + 游戏资源共用的虚拟主机名（与 C# `GameAssetConstants.VirtualHostName` 对齐）。
 *
 * 2026-08-08：Android 整页从 `file:///android_asset/` 迁到 `https://game.local/wwwroot/`——
 * file:// 页面里的 https:// 子资源不会进入 `shouldInterceptRequest`（AndroidX WebViewAssetLoader
 * 设计前提），实测为图片直接走真实网络 → ERR_NAME_NOT_RESOLVED → 图片全空。
 * 整页迁到 https 虚拟域后与 Windows 模式（app.local 页面 + game.local 资源）对称。
 *
 * **不要在 resourceResolver.test.ts 里把字面量 'game.local' 替换成 MAUI_GAME_VIRTUAL_HOST**——
 * 测试锁字面量是跨语言契约（C# `GameAssetConstants.VirtualHostName`），漂移时变红而不是跟着变绿。
 */
export const MAUI_GAME_VIRTUAL_HOST = 'game.local';

/**
 * Windows unpackaged 虚拟主机名（`app.local`）——导出供组件按 hostname 区分
 * Windows/Android（MauiGameList 的 isWindowsMaui/isAndroidMaui）。Android 页面迁到
 * game.local 后两平台页面都是 https，必须按 hostname 分流，不能只看 protocol。
 */
export { MAUI_WINDOWS_VIRTUAL_HOST };

/**
 * 判断当前是否运行在 MAUI WebView 内（spec ID7）。
 *
 * <para>检查 <c>window.location</c>：</para>
 * <list type="bullet">
 *   <item><c>protocol === 'ms-appx-web:'</c> → Windows MAUI packaged（MSIX 包内）</item>
 *   <item><c>protocol === 'file:'</c> → 旧版 Android MAUI（<c>file:///android_asset/</c>）——
 *       已废弃，2026-08-08 起 Android 整页迁到 <c>https://game.local/wwwroot/</c></item>
 *   <item><c>protocol === 'https:'</c> + host 是 <c>app.local</c> / <c>game.local</c> →
 *       Windows MAUI unpackaged / Android MAUI（WebViewAssetLoader 拦截 wwwroot + 游戏资源）</item>
 *   <item>其他 → HTTP 模式（浏览器 / Vite dev server）</item>
 * </list>
 *
 * SSR / 非 browser 环境返 <c>false</c>（<c>window</c> 未定义）。
 */
export function isMauiEnvironment(): boolean {
  if (typeof window === 'undefined') return false;
  const { protocol, hostname } = window.location;
  if (protocol === 'ms-appx-web:' || protocol === 'file:') return true;
  // Windows unpackaged 模式：WebView2 虚拟主机映射 https://app.local/
  if (protocol === 'https:' && hostname === MAUI_WINDOWS_VIRTUAL_HOST) return true;
  // Android MAUI：整页走 https://game.local/wwwroot/（WebViewAssetLoader 拦截 wwwroot + 游戏资源）
  if (protocol === 'https:' && hostname === MAUI_GAME_VIRTUAL_HOST) return true;
  return false;
}

/**
 * 向 C# 投递一条消息（JS → C# 方向）。
 *
 * Feature detection 选平台：
 * - `window.chrome.webview` 存在 → Windows，调 `chrome.webview.postMessage(json)`（CoreWebView2 WebMessageReceived）
 * - `window.appBridge` 存在 → Android，调 `appBridge.postMessage(json)`（AddJavascriptInterface 桥接对象）
 * - 两者都不存在 → 静默 no-op（MAUI 桥接未 Attach 时不抛错，避免 Vue 启动期 race）
 *
 * 与 C# `IJsBridge.InputReceived` 对齐——事件在 `BridgeHost.OnInputFromJs` 内识别 `{"type":"ready"}` / `{"type":"input","value":"..."}`。
 *
 * @param json 合法 JSON 字符串（调用方 `JSON.stringify` 后传入）。
 */
export function postInput(json: string): void {
  const w = window as any;
  if (w.chrome?.webview) {
    w.chrome.webview.postMessage(json);
    console.log('[mauiBridge] postInput via chrome.webview:', json);
  } else if (!sendBridgeUrl('post', json)) {
    // 无 DOM 时（SSR/Node 测试）保留旧桥接 fallback；Android WebView
    // 生产环境优先使用上面的 bridge:// URL 通道，避免重复投递消息。
    w.appBridge?.postMessage?.(json);
  }
}

/**
 * 注册 C# → JS 的 turn 回调（spec ID5 / ID7）。
 *
 * C# 侧 `IJsBridge.PostTurn(turnJson)` 执行 `window.__onTurn(turnJson)`，
 * `turnJson` 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量，无需再 `JSON.stringify` 双重转义）。
 * Vue 端 handler 收到的是已解析的 JS 对象——`JSON.stringify` 还原为字符串后调 `applyTurn(rawJson)`，
 * 复用 HTTP 模式的 `parseTurnRecord → applyDiff` 协议消费链路（零改动）。
 *
 * @param handler 收到 turn JSON 字符串时的回调（通常 `(rawJson) => game.applyTurn(rawJson)`）。
 */
export function registerTurnHandler(handler: (turnJson: string) => void): void {
  (window as any).__onTurn = (turn: unknown) => {
    // C# 传 JS 字面量 → turn 已是 JS 对象 → 还原为字符串复用 applyTurn
    const rawJson = typeof turn === 'string' ? turn : JSON.stringify(turn);
    handler(rawJson);
  };
}

/**
 * 向 C# 发送 ready 信号（spec ID7）。
 *
 * Vue 启动后立即调用——C# 侧 `BridgeHost.OnInputFromJs` 收到 `{"type":"ready"}` 后：
 * - T07：写日志确认收到（本票验证项）
 * - T08：调 `BridgeHost.Start()` → `Task.Run(GameLoopComposer.RunAsync)` 启动游戏循环
 *
 * ready 是游戏循环启动的前置条件，保证第一帧 turn 不丢失（Vue 已准备好接收）。
 */
export function sendReady(): void {
  postInput(JSON.stringify({ type: 'ready' }));
}

/**
 * issue 09 文件选择器 / game-library spec ID9——请求 C# 弹出原生文件夹选择器（仅 Windows）。
 *
 * Vue 端调用后，C# `BridgeHost.HandlePickFolder` 调 `IJsBridge.PickFolderAsync`
 * （Windows：WinRT `Windows.Storage.Pickers.FolderPicker`）。
 * 用户选中后，C# 经 `PostMessage` 推 `{"type":"folderPicked","path":...}` 回 Vue，
 * 由 `registerMessageHandler` 注册的 handler 接收——
 * game-library spec ID9 后语义改为「更改主目录」：handler 调 `setMainGameDir(path)` + `scanGames(path)`。
 *
 * Android 不走此路径——Android 改用 `pickSafDirectory`（ADR-0019：SAF 原生目录选择器）。
 *
 * 用户取消时 C# 不推消息——Vue 端无需处理取消（原生 picker 模态结束后自然回到 UI）。
 */
export function pickGameFolder(): void {
  postInput(JSON.stringify({ type: 'pickFolder' }));
}

/**
 * ADR-0019：Android SAF 原生目录选择器。
 * C# BridgeHost 收到后调 SafGameDirAccessor.PickDirectoryAsync()。
 * 用户选完后 C# 推 {"type":"safDirectoryPicked","path":"content://..."} 或 {"type":"safDirectoryPicked","cancelled":true}。
 */
export function pickSafDirectory(): void {
  postInput(JSON.stringify({ type: 'pickSafDirectory' }));
}

/**
 * ADR-0019：通过 bridge:// URL scheme 可靠发送 C# 命令（不依赖 appBridge）。
 * 使用隐藏 iframe 触发 WebViewClient.ShouldOverrideUrlLoading。
 *
 * @param action 动作名（如 "pickSafDirectory"），或 "post" 表示附带 JSON 数据
 * @param data 可选的 JSON 字符串数据（action="post" 时作为 msg 参数 URL 编码后附加）
 */
export function sendBridgeUrl(action: string, data?: string): boolean {
  if (typeof document === 'undefined' || !document.body) return false;

  let url: string;
  if (data) {
    url = `bridge://post?msg=${encodeURIComponent(data)}`;
  } else {
    url = `bridge://${action}`;
  }
  const iframe = document.createElement('iframe');
  iframe.style.display = 'none';
  iframe.src = url;
  document.body.appendChild(iframe);
  setTimeout(() => document.body.removeChild(iframe), 500);
  return true;
}

/**
 * issue 09 / game-library spec ID3——注册 C# → JS 的非 turn 消息回调。
 *
 * C# 侧 `IJsBridge.PostMessage(msgJson)` 执行 `window.__onMessage(msgJson)`，
 * `msgJson` 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量）。
 * Vue 端 handler 收到的是已解析的 JS 对象——按 `type` 字段分发：
 * - `{"type":"folderPicked","path":...}`——文件选择器成功，调 `setMainGameDir(path)` + `scanGames(path)`（spec ID9 修订）
 * - `{"type":"folderPicked","error":...}`——文件选择器失败，展示错误
 * - `{"type":"gamesScanned","games":[...],"rootDir":...,"displayPath":...}`——scanGames 回复，写入 store 渲染列表
 *   （`displayPath` 为 rootDir 的可读形态，issues/15；C# 端由 `SafPath.TryGetDisplayPath` 给出）
 * - `{"type":"safDirectoryPicked",...}`——Android SAF 目录选择结果（ADR-0019）
 * - `{"type":"modeDowngraded","reason":...}`——issues/15（方案 B）：真实路径轨失效，
 *   主目录已自动退回兼容轨（原因码由前端翻成文案）
 * - `{"type":"permissionBlocked"}`——issues/15 后续：授权请求被系统阻断（用户选过「拒绝，不再询问」，
 *   此后 Launch 不再弹窗），前端给一条「去系统设置手动开启」的 info 提示。普通拒绝不发
 * - `{"type":"gameExited"}`——exitGame 完成，清状态 + 自动 rescan
 *
 * 与 `registerTurnHandler` 分流——turn 经 `__onTurn` 推 `applyTurn` 协议消费链路，
 * 非 turn 事件经 `__onMessage` 推此 handler，避免污染 turn 协议。
 *
 * @param handler 收到消息对象时的回调。
 */
export function registerMessageHandler(handler: (msg: unknown) => void): void {
  (window as any).__onMessage = (msg: unknown) => {
    handler(msg);
  };
}

/**
 * issue 09 / game-library spec ID3——请求 C# hot-swap reload 到指定游戏目录。
 *
 * Vue 端在 `MauiGameList.vue` 点击列表项时调此方法（不再由 `folderPicked` 触发——
 * game-library spec ID9 修订后 `folderPicked` 改走 `setMainGameDir` + `scanGames`），
 * C# `BridgeHost.HandleLoadGame` 收到后调 `_onReloadGame(path)` 回调，
 * `MainPage.OnReloadGame` 在后台线程重新初始化运行时
 * （`EmueraRuntimeInitializer.Initialize` + `GamePaths.Validate`），成功后 UI 线程
 * `RecreateHost` + `Start`——Vue 已 ready，无需再等 ready 信号，新游戏循环首帧自然推来。
 *
 * @param path 用户选中的游戏目录绝对路径。
 */
export function loadGameFromPath(path: string): void {
  postInput(JSON.stringify({ type: 'loadGame', path }));
}

/**
 * issues/13 M5：重扫脚本缓存（库卡片 ⋮ 菜单入口）——请求 C# 删该游戏的
 * era_ir.dat + era_ir.list，下次启动即建库轮。C# 回执 `{"type":"irCacheRescanned","ok":bool,"path":...}`。
 * PC 库同款 action 通用（本地布局 = 删 _IRCache 下两文件）。
 */
export function rescanIrCache(path: string): void {
  postInput(JSON.stringify({ type: 'rescanIrCache', path }));
}

// ---------- game-library spec ID3：MAUI 桥接新消息（JS → C#）----------
//
// 两个新消息投递函数：
// - scanGames：扫描主目录下的游戏列表
// - exitGame：退出当前游戏回到列表态
//
// 与 C# `BridgeHost.OnInputFromJs` 内的 type 分发分支对齐：
//   "scanGames" / "exitGame"
//
// 投递后 C# 异步处理并经 `__onMessage` 回复对应消息：
// - scanGames → gamesScanned（含 games 数组 + rootDir）
// - exitGame → gameExited（无 payload）

/**
 * game-library spec ID3 / ID7：请求 C# 扫描主目录下的游戏列表。
 *
 * Vue 端在以下时机调用：
 * - App.vue onMounted：MAUI 模式首启动 + 主目录已知
 * - MauiGameList.vue：用户更改主目录后重新扫描
 * - useAppInit：收到 `gameExited` 后自动重新扫描
 *
 * C# `BridgeHost.HandleScanGames` 收到后：
 * 1. 读 rootDir——若未提供则用 C# 端 `_mainGameDir` 字段
 * 2. 若 rootDir 与 `_mainGameDir` 不同——更新 + 写 Preferences
 * 3. 调 `GameScanner.Scan(rootDir)` 扫描
 * 4. 回复 `{"type":"gamesScanned","games":[{name,fullPath}],"rootDir":...}`
 *
 * @param rootDir 主目录绝对路径——null/undefined/空串时 C# 用已存的主目录
 */
export function scanGames(rootDir?: string | null): void {
  const payload: Record<string, unknown> = { type: 'scanGames' };
  if (rootDir && rootDir.trim()) {
    payload.rootDir = rootDir.trim();
  }
  postInput(JSON.stringify(payload));
}

/**
 * game-library spec ID3 / ID10：请求 C# 退出当前游戏。
 *
 * Vue 端在用户点击「退出」按钮 + 确认对话框后调用。
 *
 * C# `BridgeHost.HandleExitGame` 收到后：
 * 1. 经 `IDispatcher.Dispatch` 异步投递 `{"type":"gameExited"}` 回 Vue（必须先回复再 Dispose）
 * 2. 调 `_onGameExited?.Invoke()`——MainPage 重建 BridgeHost（新 host 不 Start）
 * 3. 若 `_onGameExited` 为 null——直接 Dispose 让游戏循环停止
 *
 * 时序：PostMessage 用 Dispatch 异步派发到 UI 线程队列，此方法返回后 UI 线程会
 * 依次执行：投递 gameExited → 重建 host。Vue 端收到 gameExited 后投递 scanGames，
 * 此时新 host 已就绪可接收。
 *
 * 与 `loadGameFromPath` 的区别：
 * - loadGame：用户选新游戏 → C# hot-swap reload + Start 新游戏循环
 * - exitGame：用户退出当前 → C# 仅 Dispose + 重建空 host（不 Start），等 Vue 触发 scanGames
 */
export function exitGame(): void {
  postInput(JSON.stringify({ type: 'exitGame' }));
}

/**
 * 前端静默监控：请求 C# 探测游戏线程存活状态。
 *
 * 前端在「静默超时」（长时间无 turn 帧且不在等待输入）时调用一次，C#
 * `BridgeHost.HandleGetGameThreadStatus`（JS 桥线程直接查 `_gameTask.IsCompleted`，
 * 不依赖游戏线程配合）回复 `{"type":"gameThreadStatus","alive":true/false}`——
 * alive=true → 半透明「游戏运行中」提示；alive=false → 「游戏已停止」提示。
 */
export function getGameThreadStatus(): void {
  postInput(JSON.stringify({ type: 'getGameThreadStatus' }));
}

/**
 * issues/15：查询 Android「所有文件访问」（MANAGE_EXTERNAL_STORAGE）授权状态。
 *
 * C# `BridgeHost.HandleCheckPermission`（spec ID6）真读 `Environment.IsExternalStorageManager`
 * 回复 `{"type":"permissionStatus","granted":true/false}`——由 useAppInit 的 handleMauiMessage
 * 写入 game store。Windows 恒 true（item 不显示，回复无害）。
 */
export function checkAllFilesAccess(): void {
  postInput(JSON.stringify({ type: 'checkPermission' }));
}

/**
 * issues/15：跳转系统「所有文件访问」专用设置页（只列本应用的开关）。
 *
 * C# 拉起 `Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION`；用户开关后返回 app，
 * 调用方（MauiGameList 菜单）经 visibilitychange 触发 {@link checkAllFilesAccess} 复查。
 * ROM 阉割该页时 C# 回退应用详情页。Windows no-op。
 */
export function requestAllFilesAccess(): void {
  postInput(JSON.stringify({ type: 'requestPermission' }));
}

/**
 * A0（saf-accel 计划）：设置页「文件日志（agent.log）」开关——请求 C# 切换 AgentLog 状态。
 *
 * C# `BridgeHost.HandleSetAgentLogEnabled` 收到后：
 * 1. 写 Preferences（key=`app.agentLogEnabled`——MauiProgram 启动早期读同一 key 决定初值）
 * 2. 运行时切换 `AgentLog.Enabled`（即时生效，无需重启）
 * 3. 推回 `config` 消息（含 agentLogEnabled 字段）同步设置页开关的权威状态
 *
 * @param enabled 是否启用文件日志
 */
export function setAgentLogEnabled(enabled: boolean): void {
  postInput(JSON.stringify({ type: 'setAgentLogEnabled', enabled }));
}

/**
 * 设置页「启动日志覆盖」三态——请求 C# 覆盖游戏 `DisplayReport`。
 *
 * C# `BridgeHost.HandleSetDisplayReportOverride` 收到后：
 * 1. 写 Preferences（key=`app.displayReportOverride`——游戏加载时 OnReloadGame 读同一 key 前置位）
 * 2. 运行时切换 `ConfigData.OverrideDisplayReport`（off/on 直接压值；auto 重读配置恢复游戏自身值）
 * 3. 推回 `config` 消息（含 displayReportOverride 字段）同步设置页的权威状态
 *
 * @param value 'auto'=跟随游戏配置（默认）；'off'=强制关（隐藏启动读取日志）；'on'=强制开（逐文件报告 + time.log 计时）
 */
export type DisplayReportOverride = 'auto' | 'off' | 'on';
export function setDisplayReportOverride(value: DisplayReportOverride): void {
  postInput(JSON.stringify({ type: 'setDisplayReportOverride', value }));
}

/**
 * 性能（启动加载）覆盖三态 key——对应 emuera.config 的启动加载性能开关。
 * - lazyFunctionLoad：懒函数加载（安卓上若感觉懒加载拖慢运行可切 'off' 回 eager）
 * - irDiskCacheLoad：IR 磁盘缓存（引擎侧仅在懒函数加载开启时生效）
 * - irFirstDirList：IR 头存 firstDir 清单（issues/12 M4；off = 命中轮恒走 SAF 目录扫描）
 * - parallelErbLoad：ERB 并行读取
 * - parallelParseScript：未调用函数解析并行化
 */
export type PerfOverrideKey = 'lazyFunctionLoad' | 'irDiskCacheLoad' | 'irFirstDirList' | 'irFileListCache' | 'srcContentCache' | 'parallelErbLoad' | 'parallelParseScript';

/**
 * 设置页「性能（启动加载）」三态——请求 C# 覆盖游戏对应 emuera.config 项。
 *
 * C# `BridgeHost.HandleSetPerfOverride` 收到后：
 * 1. 写 Preferences（key=`app.perf.<key>`——游戏加载时 OnReloadGame → ApplyPerformanceOverrides 读同一 key 压值）
 * 2. 不即时改当前局（这组开关只在 ERB 加载时被读取，真正生效在下次进游戏）
 * 3. 推回 `config` 消息（含 `<key>` 字段）同步设置页分段控件的权威状态
 *
 * @param key 性能选项名
 * @param value 'auto'=跟随游戏配置（默认）；'off'=强制关；'on'=强制开
 */
export function setPerfOverride(key: PerfOverrideKey, value: DisplayReportOverride): void {
  postInput(JSON.stringify({ type: 'setPerfOverride', key, value }));
}

/**
 * ADR-0026 诊断旋钮一：并行未调用遍历平台门策略值——设置页「诊断」分组专用词表
 * （与性能覆盖三态 'auto'/'off'/'on' 刻意区分，防误当性能优化项拨动）。
 */
export type SweepGatePolicyValue = 'auto' | 'forceon' | 'forceoff';

/**
 * 设置页「诊断」分组——并行未调用遍历平台门策略（复现 Android 并行崩溃用，默认保持 auto）。
 *
 * C# `BridgeHost.HandleSetSweepGatePolicy` 收到后：
 * 1. 写 Preferences（key=`app.diag.sweepGatePolicy`——游戏加载时 OnReloadGame → ApplySweepGatePolicy 读同一 key 应用）
 * 2. 不即时改当前局（策略只在 ERB 加载时被读取，真正生效在下次进游戏）
 * 3. 推回 `config` 消息（含 sweepGatePolicy 字段）同步设置页分段控件的权威状态
 *
 * 仅内存覆盖不落盘：游戏随包 emuera.config 无法设置；生效路径见 parsescript_policy 日志 policy= 字段。
 *
 * @param value 'auto'=跟随平台门（默认，与旧版本逐位一致）；'forceon'=Android 上强制并行（取证用）；'forceoff'=强制串行（紧急杀开关）
 */
export function setSweepGatePolicy(value: SweepGatePolicyValue): void {
  postInput(JSON.stringify({ type: 'setSweepGatePolicy', value }));
}

/**
 * ADR-0026 诊断旋钮二：SAF content-URI 读取并发度值——设置页「诊断」分组专用词表。
 * 'auto' = 每树 1 并发（默认，与旧版本逐位一致）；其余为十进制数值字符串（C# 侧钳制 1–64，
 * 如 '2'/'4'/'8'/'16'）——真机崩溃阈值二分取证用（"多少并发才崩"）。
 * 与性能覆盖三态 'auto'/'off'/'on' 刻意区分，防误当性能优化项拨动。
 */
export type SafReadDegreeValue = string;

/**
 * 设置页「诊断」分组——SAF content-URI 读取并发度（真机复测 / 崩溃阈值二分取证用，默认保持 auto）。
 *
 * C# `BridgeHost.HandleSetSafReadDegree` 收到后：
 * 1. 规范化（数值钳制 1–64）并写 Preferences（key=`app.diag.safReadDegree`——游戏加载时
 *    OnReloadGame → ApplySafReadDegree 读同一 key 应用）
 * 2. 不即时改当前局（并发度只在 Preload.Load 时被读取，真正生效在下次进游戏）
 * 3. 推回 `config` 消息（含 safReadDegree 字段，规范化后的权威值）同步设置页
 *
 * 仅内存覆盖不落盘：游戏随包 emuera.config 无法设置；仅 SAF content-URI 读取受影响，
 * 桌面文件系统路径不受限；生效状态见 preload_policy 日志 requested=/effective= 字段。
 *
 * @param value 'auto'=每树 1 并发（默认）；'1'-'64' 数值字符串=每树显式并发度
 */
export function setSafReadDegree(value: SafReadDegreeValue): void {
  postInput(JSON.stringify({ type: 'setSafReadDegree', value }));
}

/**
 * ADR-0026 诊断旋钮三（06-K1）：并行生成度上限值——设置页「诊断」分组专用闭集词表。
 * 'auto' = min(核数, 8)（默认，与现状逐位一致）；'4'/'8'/'16' = 取证档直接生效（与核数解耦，
 * ERB 生成窗口与未调用遍历清扫度同源同拨）。
 * 与性能覆盖三态 'auto'/'off'/'on' 刻意区分，防误当性能优化项拨动。
 */
export type MaxParallelErbFilesValue = 'auto' | '4' | '8' | '16';

/**
 * 设置页「诊断」分组——并行生成度上限（真机并行度/内存峰值取证用，默认保持 auto）。
 *
 * C# `BridgeHost.HandleSetMaxParallelErbFiles` 收到后：
 * 1. 词表校验并写 Preferences（key=`app.diag.maxParallelErbFiles`——游戏加载时
 *    OnReloadGame → ApplyMaxParallelErbFiles 读同一 key 应用）
 * 2. 不即时改当前局（生成度只在 ERB 加载时被读取，真正生效在下次进游戏）
 * 3. 推回 `config` 消息（含 maxParallelErbFiles 字段）同步设置页分段控件
 *
 * 仅内存覆盖不落盘：游戏随包 emuera.config 无法设置；生效状态见 LoadPendingParallel 日志
 * knob=/window= 与 parsescript_policy 日志 sweep_deg= 字段。
 *
 * @param value 'auto'=min(核数,8)（默认）；'4'/'8'/'16'=显式上限（取证档）
 */
export function setMaxParallelErbFiles(value: MaxParallelErbFilesValue): void {
  postInput(JSON.stringify({ type: 'setMaxParallelErbFiles', value }));
}

/**
 * ADR-0026 诊断旋钮四（06-K2）：privateDimList 生成期全局锁策略值——设置页「诊断」分组专用词表。
 * 'auto' = 恒加锁（默认，与止血逐位一致）；'nolock' = 纯取证档（关锁重开 List 并发损坏竞态，
 * 可致状态损坏，issue 04-H1 判别用）——UI 已做显著危险标注。
 */
export type DimLockPolicyValue = 'auto' | 'nolock';

/**
 * 设置页「诊断」分组——privateDimList 锁策略（issue 04-H1 损坏型假警告判别用，默认保持 auto）。
 *
 * C# `BridgeHost.HandleSetDimLockPolicy` 收到后：
 * 1. 词表校验并写 Preferences（key=`app.diag.dimLockPolicy`——游戏加载时
 *    OnReloadGame → ApplyDimLockPolicy 读同一 key 应用）
 * 2. 不即时改当前局（锁策略只在 IdentifierDictionary 构造时被读取，真正生效在下次进游戏）
 * 3. 推回 `config` 消息（含 dimLockPolicy 字段）同步设置页分段控件
 *
 * 仅内存覆盖不落盘：游戏随包 emuera.config 无法设置；生效状态见 parsescript_policy 日志
 * dimlock= 字段。
 *
 * @param value 'auto'=恒加锁（默认）；'nolock'=关锁（危险，取证专用，可致状态损坏）
 */
export function setDimLockPolicy(value: DimLockPolicyValue): void {
  postInput(JSON.stringify({ type: 'setDimLockPolicy', value }));
}

/**
 * ADR-0030：恢复默认——请求 C# 清 Preferences（`settingsReset` 消息 → BridgeHost.HandleSettingsReset）。
 * 全部全局设置回出厂默认（auto/出厂值），下次进游戏生效。无直接回复，UI 由 config 消息回流同步。
 */
export function settingsReset(): void {
  postInput(JSON.stringify({ type: 'settingsReset' }));
}

/**
 * ADR-0030：通知 C# 设置页覆盖层的开关状态——`settingsVisibility` 消息 →
 * BridgeHost.HandleSettingsVisibility。C# 侧据此决定安卓返回键是否转发给 Vue
 * （列表页上设置页打开时，返回键应先关设置页而不是退出 app）。
 */
export function setSettingsOpen(open: boolean): void {
  postInput(JSON.stringify({ type: 'settingsVisibility', open }));
}

/**
 * A0 补充（真机无 adb）：请求 C# 读取 agent.log 内容推给 Vue（设置页日志查看器）。
 *
 * C# `BridgeHost.HandleGetAgentLog` 收到后调 `AgentLog.ReadAllText()`（内部先 flush，
 * 无需退出进程即可拿到最新日志），内容超过 200K 字符时截断为尾部（保留最新），
 * 回复 `{"type":"agentLog","content":...,"truncated":true/false}`。
 * 未启用 / 无文件时 content 为空串。
 */
export function getAgentLog(): void {
  postInput(JSON.stringify({ type: 'getAgentLog' }));
}

/**
 * A0 补充（真机无 adb）：导出 agent.log——请求 C# 用 FileProvider 分享给系统面板。
 *
 * C# `BridgeHost.HandleExportAgentLog` 收到后弹系统分享面板（微信/文件应用等），
 * 用户自行保存/转发——绕开 WebView 剪贴板复制 200K 文字的限制。
 * 无需处理回复（分享面板由系统接管）。
 */
export function exportAgentLog(): void {
  postInput(JSON.stringify({ type: 'exportAgentLog' }));
}

// ---------- game-library spec ID8：Android 目录浏览器 ----------
