import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import {
  isMauiEnvironment,
  registerTurnHandler,
  registerMessageHandler,
  scanGames as scanGamesBridge,
} from '../lib/mauiBridge';

/**
 * T-025 D9 rev：App 挂载初始化逻辑——从 App.vue onMounted 提取以便单测。
 *
 * 流程（HTTP 模式）：
 * 1. GET /state（经 fetchAndApplyStateLayout）→ 拿 server 当前 state + gameDir + 窗口布局元信息
 * 2. server 未启动（state==null）→ 不自动连，让用户用 ConnectionPanel 手动连
 * 3. 空闲态（gameDir==null 或 state=="Idle"）→ 展示选择器并预填 localStorage 上次目录，
 *    **不自动 loadGame**——推翻 issue 05「异则 loadGame」的自动加载语义。
 *    选择器预填靠 game.gameDir（已从 localStorage 初始化），此处只需 return 不 connect。
 * 4. 有活跃 session（游戏运行中/已结束）→ conn.connect() 重连，不放弃当前局
 *
 * MAUI 模式分支（issue 07 / spec ID7 + issue 09 文件选择器 + 用户反馈修复）：
 * - `window.location.protocol` 判断为 MAUI（`ms-appx-web:` / `file:` / `https:app.local`）时：
 *   1. 注册 `window.__onTurn`——C# PostTurn 调此函数，参数为 turn 对象，JSON.stringify 后调 game.applyTurn
 *   2. 注册 `window.__onMessage`——C# PostMessage 调此函数，按 type 分发非 turn 事件
 *      （game-library spec ID9：`folderPicked` → `setMainGameDir` + `scanGames` 更改主目录并重扫；
 *      旧 issue 09 的「folderPicked → loadGameFromPath」语义已废弃）
 *   3. 标记 conn.status='connected'——让 sendInput / UI 组件认为已连接（MAUI 无 WS 但语义等价）
 *   4. **不**发 `sendReady()`——用户反馈：启动时不应自动加载游戏（即便内置 test_game 也不行）。
 *      首次启动仅注册回调 + 设 connected 状态，等用户在列表页点选游戏后通过 `loadGameFromPath`
 *      （在 `MauiGameList.vue` 内）触发 C# `OnReloadGame` → `RecreateHost` + `Start`
 *      （Start 内部设 `_readyReceived=true` 跳过 ready 检查）。
 *      MainPage 构造时创建的占位 BridgeHost 永远等不到 ready 信号，不会启动游戏循环。
 *   5. return——不走 HTTP/WS 路径
 *
 * 提取原因：onMounted 回调无法直接单测，提取为纯函数后可在 Vitest 中 mock fetch +
 * connection store 验证空闲态/活跃态/未启动/MAUI 四条分支。
 */
export async function initAppState(): Promise<void> {
  const game = useGameStore();
  const conn = useConnectionStore();

  // Issue 07 / spec ID7：MAUI 环境分支——不走 HTTP/WS，用 JS interop 桥接
  if (isMauiEnvironment()) {
    console.log('[useAppInit] MAUI environment detected, initializing bridge');
    // 1. 注册 C# → JS turn 回调——C# PostTurn 调 window.__onTurn(turnJson)，
    //    turnJson 是 JS 字面量（JSON ⊂ JS），Vue 端 JSON.stringify 还原为字符串后复用 game.applyTurn
    registerTurnHandler((rawJson) => game.applyTurn(rawJson));
    // 2. issue 09 / game-library spec ID9：注册 C# → JS 非 turn 消息回调——C# PostMessage 调 window.__onMessage(msg)，
    //    按 type 分发：folderPicked → setMainGameDir + scanGames（更改主目录 + 重扫，详见 handleMauiMessage）
    registerMessageHandler((msg) => handleMauiMessage(msg, game));
    // 3. 标记已连接——MAUI 无 WS 但 sendInput / UI 组件按 status='connected' 判定可用
    conn.status = 'connected';
    // 4. 用户反馈修复：不发 sendReady()——首次启动不自动加载游戏，等用户主动选目录。
    //    占位 BridgeHost 永远等不到 ready 信号，游戏循环不启动。
    //    用户选目录后 loadGameFromPath → C# OnReloadGame → Start（跳过 ready 检查）
    // ADR-0019：SAF 替代了 MANAGE_EXTERNAL_STORAGE，Android 不再需要检查存储权限。
    // 直接扫描主目录，与 Windows 行为一致。
    game.scanStatus = 'scanning';
    scanGamesBridge(game.mainGameDir);
    // 6. 不走 HTTP/WS 路径
    return;
  }
  // 防御性 window 检查——与 isMauiEnvironment() 同模式，让 node 测试环境（vitest environment='node'）下
  // 不抛 ReferenceError。生产浏览器环境 window 总存在，无行为变化。
  if (typeof window !== 'undefined') {
    console.log('[useAppInit] HTTP environment, protocol=', window.location.protocol, 'hostname=', window.location.hostname);
  }

  const httpBase = conn.deriveHttpBase(conn.serverUrl);

  // fetchAndApplyStateLayout 内部已 catch 网络错误——state==null 表示无法连接 server
  const { gameDir: serverGameDir, state: serverState } =
    await game.fetchAndApplyStateLayout(httpBase);

  // 读取 emuera.config 值（MaxLog 等）。失败不阻塞——保持 fallback。
  await game.fetchConfig(httpBase);

  // server 未启动——不自动连
  if (serverState === null) return;

  // 空闲态 → 展示选择器 + 预填 localStorage，不自动 loadGame（D9 rev）
  if (serverGameDir === null || serverState === 'Idle') return;

  // 有活跃 session → 重连
  await conn.connect();
}

/**
 * 解析 C# 桥接推来的 controller 节点（{kind,leaseExpiresAt}|null）——与 connection store
 * 内部 parseController 同构，供 controlStatus 消息应用控制状态（issue 05 MAUI 托管）。
 */
function parseBridgeController(raw: unknown): { kind: 'agent' | 'user'; leaseExpiresAt: string | null } | null {
  if (!raw || typeof raw !== 'object') return null;
  const rec = raw as { kind?: unknown; leaseExpiresAt?: unknown };
  if (rec.kind !== 'agent' && rec.kind !== 'user') return null;
  return {
    kind: rec.kind,
    leaseExpiresAt: typeof rec.leaseExpiresAt === 'string' ? rec.leaseExpiresAt : null,
  };
}

/**
 * issue 09 文件选择器 + game-library spec ID3 / ID9：处理 C# `PostMessage` 推来的非 turn 消息——
 * `registerMessageHandler` 注册的回调，按 `type` 字段分发。
 *
 * 消息契约（C# `BridgeHost.Handle*` 推送）：
 * - `{"type":"folderPicked","path":"..."}`——用户选中目录（Windows FolderPicker），
 *   game-library spec ID9 后语义改为「更改主目录」——保存为 mainGameDir + 重新 scanGames
 * - `{"type":"folderPicked","error":"..."}`——文件选择器失败，写 `mauiError` 让 UI 展示
 * - `{"type":"gamesScanned","games":[{name,fullPath}],"rootDir":...}`——scanGames 回复，写入 store
 * - `{"type":"gameExited"}`——exitGame 处理完成，清状态 + 自动重新 scanGames
 * - `{"type":"gameThreadStatus","alive":true/false}`——前端静默监控的线程存活探测回复，
 *   写入 gameStatusHint（'running'/'stopped'），App.vue 据此显示半透明状态提示
 *
 * 收到 `folderPicked` 带 path 时的处理（game-library spec ID9 修订）：
 * 1. `game.setMainGameDir(path)`——更新 mainGameDir ref + 持久化到 localStorage
 * 2. `game.scanStatus = 'scanning'`——让列表页展示 loading 占位
 * 3. `scanGamesBridge(path)`——投递 `{"type":"scanGames","rootDir":...}`，
 *    C# `HandleScanGames` 收到后更新 `_mainGameDir` + 写 Preferences + 扫描 + 回复 gamesScanned
 *
 * **为何不再调 `loadGameFromPath(path)`**：game-library spec 后 `pickFolder` 仅用于「更改主目录」，
 * 不再用于「选游戏目录加载」（后者改由点击列表项触发 `loadGameFromPath(fullPath)`）。
 * 旧 issue 09 的「folderPicked → loadGameFromPath」语义已废弃。
 *
 * 收到 `gamesScanned` 时的处理（game-library spec ID7）：
 * - `game.setScannedGames(games, rootDir)`——写入 store，列表页据此渲染
 * - **不**自动加载游戏——列表页等用户点击项再触发
 *
 * 收到 `gameExited` 时的处理（game-library spec ID10 / ID12）：
 * 1. `game.completeExitGame()`——清 gameDir + displayState + serverState + exitStatus，
 *    并把视图重置回 terminal（退出重开不残留 debug/settings 视图）
 * 2. `scanGamesBridge()`——自动重新扫描主目录，列表页填新数据
 *
 * C# 侧 reload 失败时（`GamePaths.Validate` 抛 `GamePathValidationException` 或
 * `EmueraRuntimeInitializer.Initialize` 抛异常）由 `MainPage.OnReloadGame` 调
 * `_host.ShowError(...)` 推 error turn——Vue 端 `applyTurn` 渲染 error 字段，无需此处处理。
 *
 * @param msg C# 推来的消息对象（已解析的 JS 对象）
 * @param game game store 实例
 */
function handleMauiMessage(msg: unknown, game: ReturnType<typeof useGameStore>): void {
  if (!msg || typeof msg !== 'object') return;
  const m = msg as Record<string, unknown>;
  const type = m.type;

  if (type === 'layout') {
    game.setGameLayout({
      windowWidth: typeof m.windowWidth === 'number' ? m.windowWidth : null,
      fontSize: typeof m.fontSize === 'number' ? m.fontSize : null,
      lineHeight: typeof m.lineHeight === 'number' ? m.lineHeight : null,
      gameColumns: typeof m.gameColumns === 'number' ? m.gameColumns : null,
      fontName: typeof m.fontName === 'string' ? m.fontName : null,
    });
    // layout 消息携带 state——同步 serverState，避免首帧到达前误显"请点击快速重开"
    if (typeof m.state === 'string' && m.state !== 'Idle') {
      game.applyServerState(m.state);
    }
    return;
  }

  if (type === 'config') {
    if (typeof m.maxLog === 'number') game.maxLog = m.maxLog;
    // A0：文件日志开关的 C# 权威状态——设置页据此渲染开关初始值
    if (typeof m.agentLogEnabled === 'boolean') game.agentLogEnabled = m.agentLogEnabled;
    // 启动日志覆盖三态的 C# 权威状态——设置页据此渲染分段控件初始值
    if (m.displayReportOverride === 'auto' || m.displayReportOverride === 'off' || m.displayReportOverride === 'on') {
      game.displayReportOverride = m.displayReportOverride;
    }
    // 性能（启动加载）覆盖三态的 C# 权威状态——设置页据此渲染分段控件初始值
    for (const perfKey of ['lazyFunctionLoad', 'irDiskCacheLoad', 'parallelErbLoad', 'parallelParseScript'] as const) {
      const value = m[perfKey];
      if (value === 'auto' || value === 'off' || value === 'on') {
        game.perfOverrides[perfKey] = value;
      }
    }
    // ADR-0026 诊断旋钮一（平台门策略）的 C# 权威状态——设置页「诊断」分段控件据此渲染初始值
    if (m.sweepGatePolicy === 'auto' || m.sweepGatePolicy === 'forceon' || m.sweepGatePolicy === 'forceoff') {
      game.sweepGatePolicy = m.sweepGatePolicy;
    }
    // ADR-0026 诊断旋钮二（SAF 读取并发度）的 C# 权威状态（"auto" 或钳制后的数值字符串）
    // ——设置页「诊断」输入框据此渲染
    if (m.safReadDegree === 'auto' || (typeof m.safReadDegree === 'string' && /^\d+$/.test(m.safReadDegree))) {
      game.safReadDegree = m.safReadDegree;
    }
    // ADR-0026 诊断旋钮三（并行生成度上限，06-K1）的 C# 权威状态——设置页「诊断」分段控件据此渲染初始值
    if (m.maxParallelErbFiles === 'auto' || m.maxParallelErbFiles === '4' || m.maxParallelErbFiles === '8' || m.maxParallelErbFiles === '16') {
      game.maxParallelErbFiles = m.maxParallelErbFiles;
    }
    // ADR-0026 诊断旋钮四（privateDimList 锁策略，06-K2）的 C# 权威状态——设置页「诊断」分段控件据此渲染初始值
    if (m.dimLockPolicy === 'auto' || m.dimLockPolicy === 'nolock') {
      game.dimLockPolicy = m.dimLockPolicy;
    }
    return;
  }

  // issue 05（MAUI 托管）：控制状态同步——C# BridgeHost.ControlPumpAsync 在控制事件发生时
  // 推 {"type":"controlStatus","controller":{kind,leaseExpiresAt}|null,"state":"idle"|"held"}，
  // 前端据此切 旁观/可操作（输入栏启用/禁用 + 接管按钮）。对应 HTTP 模式的 /control 轮询语义。
  if (type === 'controlStatus') {
    const conn = useConnectionStore();
    const ctrl = parseBridgeController(m.controller);
    conn.applyControlStatus({
      controller: ctrl,
      state: typeof m.state === 'string' ? m.state : (ctrl ? 'held' : 'idle'),
    });
    return;
  }

  // A0 补充（真机无 adb）：日志查看器回复——写入 store，设置页渲染日志内容
  if (type === 'agentLog') {
    game.agentLogContent = typeof m.content === 'string' ? m.content : '';
    game.agentLogTruncated = m.truncated === true;
    return;
  }

  // game-library spec ID3 / ID7：scanGames 回复——写入 store 让列表页渲染
  if (type === 'gamesScanned') {
    const rawGames = Array.isArray(m.games) ? m.games : [];
    type RawGame = {
      name: string;
      fullPath: string;
      irCached?: unknown;
      irCacheBytes?: unknown;
      irCacheBuiltUtc?: unknown;
    };
    const games = rawGames
      .filter((g): g is RawGame =>
        !!g &&
        typeof g === 'object' &&
        typeof (g as Record<string, unknown>).name === 'string' &&
        typeof (g as Record<string, unknown>).fullPath === 'string',
      )
      .map((g) => ({
        name: g.name,
        fullPath: g.fullPath,
        // issues/05：IR 缓存存在性提示（可选——HTTP /game/scan 响应不含）
        irCached: g.irCached === true,
        irCacheBytes: typeof g.irCacheBytes === 'number' ? g.irCacheBytes : 0,
        irCacheBuiltUtc: typeof g.irCacheBuiltUtc === 'string' ? g.irCacheBuiltUtc : null,
      }));
    const rootDir = typeof m.rootDir === 'string' ? m.rootDir : null;
    // game-library spec ID13：主目录是否存在（C# HandleScanGames 通过 IGameDirAccessor 检查）
    const rootDirExists = typeof m.rootDirExists === 'boolean' ? m.rootDirExists : null;
    console.log(
      `[useAppInit] gamesScanned: ${games.length} games, rootDir=${rootDir}, rootDirExists=${rootDirExists}`,
    );
    game.setScannedGames(games, rootDir, rootDirExists);
    return;
  }

  // game-library spec ID3 / ID10 / ID12：exitGame 处理完成——清状态 + 自动重新扫描
  if (type === 'gameExited') {
    console.log('[useAppInit] gameExited: completing exit and rescanning');
    game.completeExitGame();
    // 自动重新扫描主目录——spec ID12 退出后回列表自动 rescan
    scanGamesBridge(game.mainGameDir);
    return;
  }

  // ADR-0019：Android SAF 目录选择结果
  if (type === 'safDirectoryPicked') {
    if (m.cancelled === true) {
      console.log('[useAppInit] safDirectoryPicked: user cancelled');
      return;
    }
    if (typeof m.error === 'string') {
      console.error('[useAppInit] safDirectoryPicked error:', m.error);
      game.mauiError = `选择目录失败：${m.error}`;
      return;
    }
    const path = m.path;
    if (typeof path !== 'string' || !path.trim()) {
      console.warn('[useAppInit] safDirectoryPicked: missing path');
      return;
    }
    console.log('[useAppInit] safDirectoryPicked:', path);
    game.setMainGameDir(path);
    game.scanStatus = 'scanning';
    scanGamesBridge(path);
    return;
  }

  // game-library spec ID10：Android 物理返回键——触发退出确认对话框
  if (type === 'backButtonPressed') {
    console.log('[useAppInit] backButtonPressed: incrementing trigger for exit confirm');
    // 自增计数器——App.vue watch 此值后弹出退出确认对话框
    game.backButtonPressedTick++;
    return;
  }

  // 游戏线程存活探测回复——前端静默监控据此区分「运行中」（慢计算/死循环）
  // 与「已停止」（线程假死兜底，原无限循环检测的直接杀游戏改为被动提示后
  // 由 useGameStatusMonitor 发起探测，C# BridgeHost.HandleGetGameThreadStatus 回复）。
  if (type === 'gameThreadStatus') {
    const alive = m.alive === true;
    console.log(`[useAppInit] gameThreadStatus: alive=${alive}`);
    game.setGameStatusHint(alive ? 'running' : 'stopped');
    return;
  }

  if (type !== 'folderPicked') {
    console.warn('[useAppInit] handleMauiMessage: unknown message type:', type);
    return;
  }
  // folderPicked 带 error → 文件选择器失败
  if (typeof m.error === 'string') {
    console.error('[useAppInit] folderPicked error:', m.error);
    game.mauiError = `选择目录失败：${m.error}`;
    return;
  }
  // folderPicked 带 path → game-library spec ID9：更改主目录 + 重新扫描
  // （旧 issue 09 的「folderPicked → loadGameFromPath」语义已废弃——pickFolder 现仅用于更改主目录）
  const path = m.path;
  if (typeof path !== 'string' || !path.trim()) {
    console.warn('[useAppInit] folderPicked: missing or non-string path');
    return;
  }
  console.log('[useAppInit] folderPicked, updating mainGameDir + rescanning:', path);
  // 1. 更新 mainGameDir + 持久化到 localStorage
  game.setMainGameDir(path);
  // 2. 标记扫描中——列表页展示 loading 占位
  game.scanStatus = 'scanning';
  // 3. 投递 scanGames 让 C# 扫描新主目录——C# HandleScanGames 会同步更新 _mainGameDir + 写 Preferences
  scanGamesBridge(path);
}
