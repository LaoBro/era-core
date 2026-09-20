import { defineStore } from 'pinia';
import { ref, shallowRef, computed } from 'vue';
import { parseTurnRecord, ParseTurnRecordError } from '../lib/parseTurnRecord';
import { applyDiff, type DisplaySignal } from '../lib/opsApplier';
import { applySnapshot } from '../lib/snapshotReducer';
import { EMPTY_DISPLAY_STATE } from '../types/protocol';
import type { DisplayState, DisplaySnapshot, TurnRecord } from '../types/protocol';
import { useConnectionStore } from './connection';
import { useUiStore } from './ui';
import { isMauiEnvironment, type DimLockPolicyValue, type MaxParallelErbFilesValue, type SafReadDegreeValue, type SweepGatePolicyValue } from '../lib/mauiBridge';
import { TURN_HISTORY_MAX } from '../config/constants';

/**
 * Issue 05：游戏目录持久化 key（localStorage）。
 *
 * App.vue 挂载时读此 key 与 GET /state 的 gameDir 比对：
 * - 同 → 直接 connect()（不重启当前局）
 * - 异 → loadGame(localStorage value) 切换
 */
const GAME_DIR_STORAGE_KEY = 'app.gameDir';

/**
 * game-library spec ID5：主目录持久化 key（localStorage）。
 *
 * MAUI 模式下 Vue 端同步存一份 mainGameDir——
 * - C# 端 `Preferences.Set("app.mainGameDir", ...)` 是权威源（构造 BridgeHost 时读）
 * - Vue 端 localStorage 备份供首帧渲染前知道主目录（如展示在列表页底部）
 *
 * 与 `app.gameDir` 区别：
 * - `app.gameDir`：当前加载的游戏目录（特定游戏）
 * - `app.mainGameDir`：游戏库主目录（包含多个游戏的父目录）
 */
const MAIN_GAME_DIR_STORAGE_KEY = 'app.mainGameDir';

/**
 * game-library spec ID5：上次玩过的游戏名持久化 key（localStorage）。
 *
 * 列表页据此高亮上次玩的游戏——存目录名（不是完整路径），
 * 主目录变更后仍可匹配同名子目录。
 */
const LAST_PLAYED_GAME_STORAGE_KEY = 'app.lastPlayedGame';

/**
 * game-library spec ID1：扫描到的游戏条目（与 C# `GameEntry` record 对称）。
 *
 * issues/05：irCached/irCacheBytes/irCacheBuiltUtc 为 IR 缓存存在性提示（非有效性承诺）——
 * 游戏更新后下次加载自动重建，提示随下次库刷新更新。仅 MAUI 桥接（gamesScanned）携带，
 * HTTP /game/scan 响应不含（可选字段，缺失 = 无缓存提示）。
 */
export interface GameEntry {
  /** 游戏目录名（Path.GetFileName，主目录下的子目录名）。 */
  name: string;
  /** 游戏目录绝对路径。 */
  fullPath: string;
  /** 是否存在 IR 缓存（era_ir.dat 存在且非空）。 */
  irCached?: boolean;
  /** IR 缓存字节数（era_ir.dat 大小）。 */
  irCacheBytes?: number;
  /** IR 缓存构建时间（本地时区 "yyyy-MM-dd HH:mm"）。 */
  irCacheBuiltUtc?: string | null;
}

/**
 * Issue 05：结构化错误码——与 C# `KestrelGameServer.HandleLoadGameAsync` 错误契约对齐。
 *
 * 路径级错误（400）：
 * - DIR_NOT_FOUND：目录不存在
 * - MISSING_CSV：缺 csv 子目录
 * - MISSING_ERB：缺 erb 子目录
 * - MISSING_GAME_DIR / INVALID_JSON：请求体错误（前端构造时不应触发，作兜底）
 *
 * 加载级错误（500）：
 * - LOAD_FAILED：Preload.Load / ERB 解析等失败
 */
export type LoadGameErrorCode =
  | 'DIR_NOT_FOUND'
  | 'MISSING_CSV'
  | 'MISSING_ERB'
  | 'MISSING_GAME_DIR'
  | 'INVALID_JSON'
  | 'LOAD_FAILED';

/** Issue 05：loadGame 抛出的结构化错误——UI 按代码映射提示文案。 */
export class LoadGameError extends Error {
  readonly code: LoadGameErrorCode;
  readonly httpStatus: number;

  constructor(code: LoadGameErrorCode, message: string, httpStatus: number) {
    super(message);
    this.name = 'LoadGameError';
    this.code = code;
    this.httpStatus = httpStatus;
  }
}

/**
 * T-025 D14：server 语义状态——驱动 UI 元素可见性（如「快速重开」按钮）。
 * 'Idle' = 空闲（无活跃 session）；其余值 = 有活跃 session。
 * 与 C# Session.StateString / GET /state state 字段对称。
 */
export type ServerState = 'Idle' | 'Loading' | 'WaitInput' | 'Quit' | 'Error';

/**
 * 错误码 → UI 文案映射——纯函数，便于单测。
 *
 * 与 C# `KestrelGameServer.HandleLoadGameAsync` 的 5 个 code 对称：
 * - DIR_NOT_FOUND：目录不存在
 * - MISSING_CSV：缺 csv 目录
 * - MISSING_ERB：缺 erb 目录
 * - LOAD_FAILED：加载游戏失败（ERB 损坏等）
 * - MISSING_GAME_DIR / INVALID_JSON：请求格式错误（前端 bug，不应让用户看到原文）
 */
export function mapLoadGameErrorCode(code: LoadGameErrorCode): string {
  switch (code) {
    case 'DIR_NOT_FOUND':
      return '目录不存在，请检查路径';
    case 'MISSING_CSV':
      return '缺少 csv 子目录，请确认是 Emuera 游戏目录';
    case 'MISSING_ERB':
      return '缺少 erb 子目录，请确认是 Emuera 游戏目录';
    case 'LOAD_FAILED':
      return '游戏加载失败（ERB 文件可能损坏），请查看服务器日志';
    case 'MISSING_GAME_DIR':
    case 'INVALID_JSON':
      return '请求格式错误，请刷新页面重试';
  }
}

/**
 * 从 localStorage 读 gameDir——纯函数，便于单测。
 *
 * SSR / 旧浏览器无 localStorage 时返 null。读到非 string 值（被外部污染）也返 null。
 */
export function readGameDirFromStorage(
  storage: Storage | null = typeof localStorage !== 'undefined' ? localStorage : null,
): string | null {
  if (!storage) return null;
  try {
    const v = storage.getItem(GAME_DIR_STORAGE_KEY);
    return typeof v === 'string' && v.length > 0 ? v : null;
  } catch {
    // localStorage 访问被禁用（隐私模式 / 跨域）——返 null 不抛
    return null;
  }
}

/** 写 gameDir 到 localStorage——失败时静默（隐私模式 / 容量超限）。 */
function writeGameDirToStorage(dir: string): void {
  try {
    localStorage.setItem(GAME_DIR_STORAGE_KEY, dir);
  } catch {
    // 静默——gameDir ref 仍更新，只是不持久化
  }
}

/** T-025 D14：清空 localStorage gameDir——快速重开失败时调用，回退到空路径选择器。 */
function clearGameDirFromStorage(): void {
  try {
    localStorage.removeItem(GAME_DIR_STORAGE_KEY);
  } catch {
    // 静默
  }
}

/**
 * game-library spec ID5：从 localStorage 读 mainGameDir——纯函数，便于单测。
 * SSR / 旧浏览器无 localStorage 时返 null。
 */
export function readMainGameDirFromStorage(
  storage: Storage | null = typeof localStorage !== 'undefined' ? localStorage : null,
): string | null {
  if (!storage) return null;
  try {
    const v = storage.getItem(MAIN_GAME_DIR_STORAGE_KEY);
    return typeof v === 'string' && v.length > 0 ? v : null;
  } catch {
    return null;
  }
}

/**
 * 将主目录路径转换为用户可读文案。
 * Android SAF 的 content URI 是内部句柄，不应直接展示在页面 header 或空状态中。
 */
export function formatMainGameDirForDisplay(path: string | null | undefined): string {
  const trimmed = typeof path === 'string' ? path.trim() : '';
  if (!trimmed) return '(未设置)';
  if (trimmed.startsWith('content://')) return 'Android 存储目录';
  return trimmed;
}

/** game-library spec ID5：写 mainGameDir 到 localStorage——失败时静默。 */
function writeMainGameDirToStorage(dir: string): void {
  try {
    localStorage.setItem(MAIN_GAME_DIR_STORAGE_KEY, dir);
  } catch {
    // 静默
  }
}

/**
 * game-library spec ID5：从 localStorage 读 lastPlayedGame——纯函数，便于单测。
 */
export function readLastPlayedGameFromStorage(
  storage: Storage | null = typeof localStorage !== 'undefined' ? localStorage : null,
): string | null {
  if (!storage) return null;
  try {
    const v = storage.getItem(LAST_PLAYED_GAME_STORAGE_KEY);
    return typeof v === 'string' && v.length > 0 ? v : null;
  } catch {
    return null;
  }
}

/** game-library spec ID5：写 lastPlayedGame 到 localStorage——失败时静默。 */
function writeLastPlayedGameToStorage(name: string): void {
  try {
    localStorage.setItem(LAST_PLAYED_GAME_STORAGE_KEY, name);
  } catch {
    // 静默
  }
}

/**
 * useGameStore — 游戏帧数据与显示状态（issue 03 升级 / issue 04 ADR-0016 v6 重构）。
 *
 * 协议消费链路（issue 02 引入纯函数，issue 03 在此 wiring，ADR-0016 加 timer 字段）：
 *   WS 帧原始 JSON
 *     → parseTurnRecord(rawJson)  → TurnRecord（含 timeLimit/displayTime/timeUpMessage/timedOut）
 *     → applyDiff(displayState, turn.diff)  → { DisplayState, signal }
 *        signal 非 null 时写入 lastDisplaySignal（归约层对 shift_head/clear_screen 的分类摘要）
 *     → 顶层 state/inputType/needValue 用 turn 字段覆盖
 *
 * 与 C# `Emuera.Headless/Server/OutputHub` + `AgentJsonlProtocol.RunLoopAsync` 对称：
 * 每个 WS Text 帧是一个 TurnRecord v6 JSON，diff 字段携带 LineOp[] 增量，
 * timer 字段携带 TINPUT 元数据（timeLimit/displayTime/timeUpMessage + timedOut 旗标）。
 *
 * ADR-0016 决策三：超时检测改为消费 turn.timedOut 旗标，删除启发式。
 * ADR-0016 决策四：TINPUT 倒计时用「静态 + 本地钟表」——server 只在 input/timeout 时发帧，
 * 平时不主动 push；前端收到 WaitInput+timeLimit>0 帧时记 receivedAt，
 * 用 setInterval(100ms) 算 remaining = timeLimit - (now - receivedAt)。
 *
 * Issue 01 保留的 raw json / history / error 字段继续供 DebugView 调试用。
 */
export const useGameStore = defineStore('game', () => {
  /** 最新 WS 帧的原始 JSON 字符串（未解析），调试视图直接 <pre> 展示。 */
  const lastTurnJson = ref<string | null>(null);
  /** 所有收到的 WS 帧原始 JSON（最新在末尾），调试用。Issue 01 不限制大小，v1 调试面板够用。 */
  const turnHistory = ref<string[]>([]);
  /** 最近一次错误信息（如 WS 帧解析失败）。 */
  const lastError = ref<string | null>(null);
  /** 最新解析后的 TurnRecord（调试用，UI 也可读 inputType 等顶层字段）。 */
  const lastTurn = ref<TurnRecord | null>(null);

  // ---------- Issue 05：游戏目录与重载状态 ----------
  //
  // gameDir 持久化到 localStorage，App.vue 挂载时与 GET /state 比对决定 connect / loadGame。
  // reloadStatus 标记 /load-game 序列进行中——UI 禁用切换按钮、显示 loading。
  // loadGame(dir) 是跨 store 复合动作：disconnect → POST /load-game → connect。
  //   成功：reloadStatus='idle'，gameDir 更新，WS 重连入新游戏
  //   失败：reloadStatus='idle'，loadGameError 写入，UI 按错误码映射提示
  //         路径级错误（400）不拆旧 session——server 端 dispose 旧前拦下，前端 connect() 回旧局
  //         加载级错误（500）旧 session 已被 dispose——connect() 时 server 会建新空 session
  /**
   * 当前游戏目录。null 表示尚未加载过任何游戏。
   *
   * HTTP 模式：持久化到 localStorage（Issue 05 重连语义），App.vue 挂载时与 GET /state 比对决定 connect / loadGame。
   * MAUI 模式：始终为 null 启动——MAUI 进程重启后游戏循环**不会**自动恢复（C# 占位 BridgeHost 不 Start），
   * 上一进程残留的 localStorage gameDir 是过期状态，恢复它只会让 App.vue 误判「有活跃游戏」、
   * 隐藏游戏列表页直接进空终端画面（需手动按「快速重开」）。故 MAUI 启动不读 localStorage。
   */
  const gameDir = ref<string | null>(
    isMauiEnvironment() ? null : readGameDirFromStorage(),
  );

  // ---------- game-library spec ID5 / ID7：主目录 + 游戏列表 + 上次玩过 ----------
  //
  // mainGameDir：游戏库主目录（包含多个游戏的父目录），MAUI 模式下 C# 端 Preferences 是权威源，
  // Vue 端 localStorage 同步备份。首启动时 Vue 端可能未初始化（C# 还没推 layout 消息），
  // 用 readMainGameDirFromStorage 读 localStorage 让 UI 立即展示主目录路径。
  //
  // scannedGames：scanGames 消息回复后写入——列表页据此渲染游戏列表。
  // scanRootDir：最近一次 scanGames 的 rootDir——列表页底部展示「主目录: <path>」用。
  // scanStatus：scanning 期间列表页展示 loading 占位，避免空列表被误认为「无游戏」。
  //
  // lastPlayedGame：用户上次玩的游戏目录名（不是完整路径），列表页据此高亮项。
  // 主目录变更后仍可匹配同名子目录——存名字而非路径。
  /** 游戏库主目录（持久化到 localStorage）。null 表示首启动未设置。 */
  const mainGameDir = ref<string | null>(readMainGameDirFromStorage());
  /** 上次玩过的游戏目录名（持久化到 localStorage）。null 表示未玩过。 */
  const lastPlayedGame = ref<string | null>(readLastPlayedGameFromStorage());
  /** scanGames 消息回复后的游戏列表——列表页据此渲染。 */
  const scannedGames = ref<GameEntry[]>([]);
  /** 最近一次 scanGames 的 rootDir——列表页底部展示「主目录: <path>」用。 */
  const scanRootDir = ref<string | null>(null);
  /**
   * issues/15：扫库回执带回的**可读主目录**（SAF content URI → 「内部存储 / games / eratoho」）。
   *
   * 与 rootDir **成对**存，不单独存展示串：`mauiGameLibrarySource.scan` 会立刻改写 scanRootDir，
   * 而回执要等 C# 扫完才到——只存展示串的话，这段窗口里会拿旧目录的名字去配新目录的模式徽章。
   * 成对存 + 下面的 computed 做路径校验，错配时自然失效、退回折叠文案。
   */
  const scanRootDirDisplayEntry = ref<{ rootDir: string; displayPath: string } | null>(null);
  /**
   * 主目录的展示态可读路径——**仅在它与当前主目录同一路径**时有效，否则 null
   * （渲染方回退 `formatMainGameDirForDisplay`）。Windows / Web 恒为 null：
   * 真实路径本身就是可读的，不需要另给展示名。
   *
   * 注意：此值**只供渲染**。「快速 / 兼容」模式判定必须继续用 `scanRootDir` 原文的
   * `content://` 前缀——展示名不含协议信息，拿它判模式会把 SAF 轨误判成快速轨。
   */
  const scanRootDirDisplay = computed<string | null>(() => {
    const entry = scanRootDirDisplayEntry.value;
    const current = scanRootDir.value ?? mainGameDir.value;
    if (!entry || !current) return null;
    return entry.rootDir === current ? entry.displayPath : null;
  });
  /**
   * issues/15：Android「所有文件访问」（MANAGE_EXTERNAL_STORAGE）授权状态——
   * null = 未知（未查询过）。由 permissionStatus 消息（checkPermission 回复）写入，
   * MauiGameList 菜单项据此渲染「已授权 / 申请」。
   */
  const allFilesAccess = ref<boolean | null>(null);
  /**
   * game-library spec ID13：最近一次 scanGames 的 rootDir 是否存在——
   * C# HandleScanGames 通过 IGameDirAccessor.DirectoryExists(rootDir) 检查后，
   * 经 gamesScanned 消息携带。
   * 列表页空状态据此区分「主目录不存在」vs「主目录存在但无游戏」。
   * null 表示尚未扫描。
   */
  const scanRootDirExists = ref<boolean | null>(null);
  /** scanGames 进行中标志——列表页展示 loading 占位。 */
  const scanStatus = ref<'idle' | 'scanning'>('idle');
  /**
   * 退出游戏进行中标志——unloadGame 投递 exitGame 后置 true，
   * 收到 gameExited 消息后置 false。期间 UI 禁用退出按钮避免重复点击。
   */
  const exitStatus = ref<'idle' | 'exiting'>('idle');
  /**
   * game-library spec ID6：Android 存储权限状态——
   * 'unknown' = 尚未检查（首启动），'granted' = 已授权 MANAGE_EXTERNAL_STORAGE，
   * 'denied' = 未授权（需展示引导页）。
   * T-025 D14：当前 server 状态字符串——驱动 UI 元素可见性（如「快速重开」按钮）。
   *
   * 来源：
   * - App.vue onMounted 从 GET /state 写入
   * - applyTurn 从 turn.state 写入（WS 帧推送）
   * - reset() 时置 'Idle'
   *
   * 'Idle' 表示空闲态（无活跃 session），其他值（'Loading'/'WaitInput'/'Quit'/'Error'）表示有活跃 session。
   * 与 displayState.state 的区别：displayState.state 是 WS 帧的显示状态字段（初始为 ''），
   * serverState 是服务端语义状态（初始为 'Idle'），用于 UI 判断「是否有活跃游戏」。
   */
  const serverState = ref<ServerState>('Idle');
  /** /load-game 进行中标志——UI 禁用切换按钮 + 显示 loading。 */
  const reloadStatus = ref<'idle' | 'loading'>('idle');
  /** 最近一次 loadGame 错误——UI 展示结构化提示。null 表示无错误或已清除。 */
  const loadGameError = ref<LoadGameError | null>(null);
  /**
   * issue 09 文件选择器：MAUI 模式下的错误状态——
   * 文件选择器失败（C# `PickFolderAsync` 抛异常）或 hot-swap reload 失败（`EmueraRuntimeInitializer.Initialize`
   * 或 `GamePaths.Validate` 抛异常）时由 `useAppInit` 消息处理器写入。null 表示无错误。
   *
   * 与 `loadGameError` 区别：`loadGameError` 是 HTTP 模式 `/load-game` 端点的结构化错误（含 code/httpStatus），
   * `mauiError` 是 MAUI 模式原生路径的纯文本错误（无 HTTP 语义）。
   */
  const mauiError = ref<string | null>(null);
  /**
   * 游戏静默状态提示（MAUI）——前端静默监控的结果：
   * - 'running'：游戏线程存活但长时间无输出（慢计算 / 真死循环），半透明「游戏运行中」提示
   * - 'stopped'：游戏线程已死而前端未收到退出/错误帧（后台线程假死兜底），提示「游戏已停止」
   * 任何新 turn / 进入等待输入 / 退出游戏都会清空。null 表示无提示。
   */
  const gameStatusHint = ref<'running' | 'stopped' | null>(null);
  /** 最近一次收到 turn 帧的时间戳（毫秒）——前端静默监控据此判断游戏是否在产出。 */
  const lastActivityAt = ref<number>(Date.now());
  /**
   * 最近一次提交游戏输入的时间戳（毫秒）。null 表示未提交输入或已收到对应 turn。
   *
   * 静默监控据此区分「游戏在等人」（WaitInput 且未提交输入）与「游戏在忙」
   * （已提交输入但长时间无帧——慢回合计算期间前端 state 停留在旧的 WaitInput，
   * 只有提交时刻能标记"游戏开始干活了"）。任何 turn 帧到达即清空。
   */
  const inputSubmittedAt = ref<number | null>(null);
  /**
   * 最近一次 GET /snapshot 拿到的原始快照（调试展示用）。
   *
   * 与 lastTurn/turnHistory 分离——snapshot 不是 WS 帧，不进入 turnHistory；
   * 但调试视图需要独立展示它，便于排查"晚加入者画面"恢复是否符合预期。
   * setSnapshot 调用时更新；reset() 时清空。
   */
  const lastSnapshot = ref<DisplaySnapshot | null>(null);
  /** 当前显示状态——纯函数 applyDiff 累积应用 WS 帧 diff 后的不可变结果。 */
  const displayState = shallowRef<DisplayState>({ ...EMPTY_DISPLAY_STATE });
  /**
   * 当前协议版本（sticky）。
   *
   * C# `AgentJsonlProtocol.BuildTurn` 只在首帧（isInitial=true）带 protocolVersion，
   * 后续帧该字段为 null（见 Emuera.Headless/Agent/AgentJsonlProtocol.cs:233）。
   * 故不能用 `lastTurn.protocolVersion` 直接派生——后续帧会让版本号闪烁归 null。
   *
   * sticky 行为：首帧收到非空版本时写入；后续帧的 null 字段不覆盖。`reset()` 时清空。
   */
  const protocolVersion = ref<number | null>(null);

  // ---------- 显示变更信号（shift_head / clear_screen） ----------
  //
  // C# 端 `ConsolePrintManager.RemoveAt(0)` 触发 MaxLog 头部截断时，
  // `DisplayState.ComputeDiff` 在 lineOps 前置 `ShiftHeadLineOp(count)`；
  // CLEAR/全重置则产出 `ClearScreenOp`。这两类结构性变化会改变滚动位置，
  // 由 `applyDiff` 归约时一并分类为声明式 `DisplaySignal`（见 opsApplier.ts）。
  //
  // 本 ref 只存最近一帧的信号：applyTurn 里 `applyDiff` 返回非 null 信号时才赋值
  // （每次都是新对象，引用变化天然触发 TerminalDisplay 的 watch）；
  // 无结构性变化的帧不触碰本 ref——保持「只在相关帧触发」的语义。
  // reset() / completeExitGame() 置 null，避免重挂载时残留信号触发 watch。
  //
  // 为什么不用 `watch(lines.length)` 检测全清：applyDiff 在单 tick 内顺序应用
  // clear_screen + append，Vue 响应式只观察到最终 length（= append 后行数），
  // length 突变到 0 的中间态不可见——信号必须由归约层在应用 op 时同步产出。
  /** 最近一帧的显示变更信号——TerminalDisplay watch 此 ref 驱动滚动副作用。null 表示无结构性变化。 */
  const lastDisplaySignal = ref<DisplaySignal | null>(null);

  /**
   * game-library spec ID10：Android 物理返回键触发退出确认——计数器。
   * useAppInit 收到 backButtonPressed 消息时自增，App.vue watch 此值弹出确认对话框。
   * 单调递增整数 ref 触发器模式——与已移除的 shift/clear 信号 tick 同理：
   * 避免对同一值重写不触发 watch。
   */
  const backButtonPressedTick = ref<number>(0);

  // ---------- 按钮 generation 失效 + 三重守卫（v6→v7）----------
  //
  // - currentTurnGeneration：当前回合的 generation 号，由 applyTurn/setSnapshot 更新。
  //   前端对比按钮的 generation 是否匹配此值——不匹配说明按钮属于旧回合，禁用。
  // - inputInFlight：同一回合内防重复点击的乐观锁。点击按钮时立即置 true，
  //   下一帧 turn 到达（applyTurn）或快照重建（setSnapshot）时清 false。
  // - inputInFlightTimerId：30s 安全超时——服务端崩溃时前端不会永久锁死。
  /** 当前回合 generation 号。初始 0，与 C# LastButtonGeneration 初始值对称。 */
  const currentTurnGeneration = ref<number>(0);
  /** 输入乐观锁——同一回合内防重复点击。true 时阻止所有按钮/InputBar 提交。 */
  const inputInFlight = ref<boolean>(false);
  /** 安全超时定时器——服务端崩溃兜底，30s 后自动解锁。 */
  let inputInFlightTimerId: ReturnType<typeof setTimeout> | null = null;

  /**
   * 当前回合是否有可用按钮——扫描显示行中 `generation === currentTurnGeneration` 的按钮 entry。
   * 历史滚动行里的旧回合按钮 generation 不匹配，不计入（复用 v7 三重守卫的同一数据源）。
   * 供 InputBar 决定是否自动弹出：有可用按钮时按钮即输入手段，输入栏不自动出现。
   */
  const hasActiveButtons = computed<boolean>(() =>
    displayState.value.lines.some((line) =>
      line.entries.some((entry) => entry.button != null && entry.button.generation === currentTurnGeneration.value),
    ),
  );

  /**
   * 置输入乐观锁 + 30s 安全超时。
   * 幂等：多次调用重置超时（双击时第二击刷新 30s 窗口）。
   */
  function setInputInFlight(): void {
    inputInFlight.value = true;
    if (inputInFlightTimerId !== null) clearTimeout(inputInFlightTimerId);
    inputInFlightTimerId = setTimeout(() => {
      inputInFlight.value = false;
      inputInFlightTimerId = null;
    }, 30000);
  }

  /** 清输入乐观锁 + 清安全超时。applyTurn / setSnapshot / reset 共用。 */
  function clearInputInFlight(): void {
    inputInFlight.value = false;
    if (inputInFlightTimerId !== null) {
      clearTimeout(inputInFlightTimerId);
      inputInFlightTimerId = null;
    }
  }

  /**
   * 设置当前 generation + 清 inputInFlight。
   * applyTurn / setSnapshot / reset 共用的两步操作，消除重复代码。
   */
  function acceptGeneration(n: number): void {
    currentTurnGeneration.value = n;
    clearInputInFlight();
  }

  // ---------- Issue 12：游戏窗口布局元信息 ----------
  //
  // 这五个字段从 C# `GET /state` 响应读取——驱动 TerminalDisplay 的固定宽度布局：
  // - windowWidth：游戏 emuera.config 设定的窗口像素宽度（ConfigCode.WindowX，默认 760）
  // - fontSize：字体像素大小（ConfigCode.FontSize，默认 18）
  // - lineHeight：行距像素（ConfigCode.LineHeight，默认 19；绝对像素，非比例）
  // - gameColumns：游戏可绘制字符列数 = DrawableWidth / (FontSize/2)，与 CLI 模式
  //   TerminalLineFormatter.GetGameColumnWidth() 一致。前端用此值以 CSS ch 单位设置
  //   容器宽度——浏览器 monospace 字符宽度（≈0.6em）与 GDI（FontSize/2=0.5em）不同，
  //   按像素宽度布局会让字符画溢出；按字符列数 × 1ch 布局则字体宽度自适应。
  // - fontName：游戏字体名（ConfigCode.FontName，默认 "ＭＳ ゴシック"）——前端将其作为
  //   font-family 首选，浏览器找不到时再 fallback 到 ui-monospace 链。ASCII 字符画对字体
  //   宽度高度敏感，"ＭＳ ゴシック"（GDI 18px）与 Consolas 等浏览器默认 monospace 字形差异
  //   显著，不读游戏字体名会让字符画视觉走形。
  //
  // null 表示尚未从 server 读取——TerminalDisplay 用默认值 fallback。
  // App.vue onMounted + loadGame 成功后会调 setGameLayout 更新这些字段。
  // 新游戏可能有不同 emuera.config——切换游戏后必须重读 GET /state。
  /** 游戏窗口像素宽度（来自 ConfigCode.WindowX，默认 760）。null 表示尚未读取。 */
  const windowWidth = ref<number | null>(null);
  /** 游戏字体像素大小（来自 ConfigCode.FontSize，默认 18）。null 表示尚未读取。 */
  const fontSize = ref<number | null>(null);
  /** 游戏行距像素（来自 ConfigCode.LineHeight，默认 19）。null 表示尚未读取。 */
  const lineHeight = ref<number | null>(null);
  /** 游戏可绘制字符列数（DrawableWidth / (FontSize/2)，默认按 760/18 算 = 84）。null 表示尚未读取。 */
  const gameColumns = ref<number | null>(null);
  /** 游戏字体名（来自 ConfigCode.FontName，默认 "ＭＳ ゴシック"）。null 表示尚未读取。 */
  const fontName = ref<string | null>(null);
  /** 履歴ログの行数（来自 ConfigCode.MaxLog，默认 5000）。null 表示尚未读取。 */
  const maxLog = ref<number | null>(null);
  /**
   * A0（saf-accel 计划）：文件日志（agent.log）开关——C# 权威状态（来自 config 消息的 agentLogEnabled 字段）。
   * 设置页开关据此渲染初始值；切换时投递 setAgentLogEnabled 请求 C# 切换 + 持久化，
   * C# 推回 config 消息后同步回此值。默认 false（Android 默认关闭）。
   */
  const agentLogEnabled = ref<boolean>(false);
  /**
   * 启动日志覆盖三态——C# 权威状态（来自 config 消息的 displayReportOverride 字段）。
   * 'auto'=跟随游戏配置；'off'=强制关（隐藏启动读取日志）；'on'=强制开（逐文件报告 + time.log 计时）。
   * 设置页分段控件据此渲染初始值；切换时投递 setDisplayReportOverride 请求 C# 切换 + 持久化，
   * C# 推回 config 消息后同步回此值。默认 'off'（抑制启动日志，与历史默认行为一致）。
   */
  const displayReportOverride = ref<'auto' | 'off' | 'on'>('off');
  /**
   * 性能（启动加载）覆盖三态——C# 权威状态（来自 config 消息的 lazyFunctionLoad 等五个字段）。
   * 'auto'=跟随游戏 emuera.config；'off'=强制关；'on'=强制开。
   * 设置页分段控件据此渲染初始值；切换时投递 setPerfOverride 请求 C# 切换 + 持久化，
   * C# 推回 config 消息后同步回此值。仅在下次进游戏时生效，默认 'auto'（不干预游戏配置）。
   */
  const perfOverrides = ref<Record<'lazyFunctionLoad' | 'irDiskCacheLoad' | 'irFirstDirList' | 'irFileListCache' | 'srcContentCache' | 'parallelErbLoad' | 'parallelParseScript', 'auto' | 'off' | 'on'>>({
    lazyFunctionLoad: 'auto',
    irDiskCacheLoad: 'auto',
    irFirstDirList: 'auto',
    irFileListCache: 'auto',
    srcContentCache: 'auto',
    parallelErbLoad: 'auto',
    parallelParseScript: 'auto',
  });
  /**
   * ADR-0026 诊断旋钮一：并行未调用遍历平台门策略——C# 权威状态（来自 config 消息的 sweepGatePolicy 字段）。
   * 'auto'=跟随平台门（默认，与旧版本逐位一致）；'forceon'=Android 强制并行（复现取证用）；
   * 'forceoff'=强制串行（紧急杀开关）。设置页「诊断」分段控件据此渲染初始值；切换时投递
   * setSweepGatePolicy 请求 C# 写 Preferences + 持久化，C# 推回 config 消息后同步回此值。
   * 仅内存覆盖，游戏随包配置无法设置；下次进游戏生效。
   */
  const sweepGatePolicy = ref<SweepGatePolicyValue>('auto');
  /**
   * ADR-0026 诊断旋钮二：SAF content-URI 读取并发度——C# 权威状态（来自 config 消息的 safReadDegree 字段）。
   * 'auto'=每树 1 并发（默认，与旧版本逐位一致）；其余为十进制数值字符串（C# 侧钳制 1–64，
   * 真机崩溃阈值二分取证用）。设置页据此渲染；切换时投递 setSafReadDegree 请求 C# 规范化 +
   * 写 Preferences，C# 推回 config 消息后同步回此值。仅内存覆盖，游戏随包配置无法设置；下次进游戏生效。
   */
  const safReadDegree = ref<SafReadDegreeValue>('auto');
  /**
   * ADR-0026 诊断旋钮三（06-K1）：并行生成度上限——C# 权威状态（来自 config 消息的 maxParallelErbFiles 字段）。
   * 'auto'=min(核数,8)（默认，与现状逐位一致）；'4'/'8'/'16'=取证档直接生效（ERB 生成窗口与
   * 未调用遍历清扫度同源同拨）。设置页据此渲染；切换时投递 setMaxParallelErbFiles 请求 C#
   * 写 Preferences，C# 推回 config 消息后同步回此值。仅内存覆盖，游戏随包配置无法设置；下次进游戏生效。
   */
  const maxParallelErbFiles = ref<MaxParallelErbFilesValue>('auto');
  /**
   * ADR-0026 诊断旋钮四（06-K2）：privateDimList 锁策略——C# 权威状态（来自 config 消息的 dimLockPolicy 字段）。
   * 'auto'=恒加锁（默认，与止血逐位一致）；'nolock'=纯取证档（关锁重开并发损坏竞态，可致状态损坏，
   * UI 已做显著危险标注）。设置页据此渲染；切换时投递 setDimLockPolicy 请求 C# 写 Preferences，
   * C# 推回 config 消息后同步回此值。仅内存覆盖，游戏随包配置无法设置；下次进游戏生效。
   */
  const dimLockPolicy = ref<DimLockPolicyValue>('auto');
  /**
   * A0 补充（真机无 adb）：app 内日志查看器——agent.log 内容（来自 getAgentLog 回复的 agentLog 消息）。
   * 设置页「查看日志」按钮触发读取；truncated 表示内容因超过 200K 字符被截断为尾部。
   */
  const agentLogContent = ref<string>('');
  const agentLogTruncated = ref<boolean>(false);

  // ---------- 缩放比例 ----------
  //
  // 用于 MAUI 移动端/桌面端放大缩小终端画面。范围 0.5–2.0，步长 0.1。
  // 持久化到 localStorage，刷新后保持。
  const SCALE_STORAGE_KEY = 'app.scale';

  const scale = ref<number>(readScaleFromStorage());

  function readScaleFromStorage(): number {
    try {
      const v = localStorage.getItem(SCALE_STORAGE_KEY);
      if (v !== null) {
        const n = parseFloat(v);
        if (!isNaN(n) && n >= 0.5 && n <= 2.0) return n;
      }
    } catch {
      // 静默
    }
    return 1.0;
  }

  function writeScaleToStorage(s: number): void {
    try { localStorage.setItem(SCALE_STORAGE_KEY, String(s)); } catch { /* 静默 */ }
  }

  function setScale(s: number): void {
    const clamped = Math.max(0.5, Math.min(2.0, s));
    scale.value = clamped;
    writeScaleToStorage(clamped);
  }

  const effectiveScale = computed(() => scale.value ?? 1.0);
  const isMinScale = computed(() => effectiveScale.value <= 0.5);
  const isMaxScale = computed(() => effectiveScale.value >= 2.0);

  // ---------- ADR-0016：TINPUT timer 状态（本地钟表）----------
  //
  // server 不周期 push tick 帧——只在 input/timeout 时发帧。前端收到 WaitInput+TINPUT 帧时
  // 记 receivedAt，用 setInterval(100ms) 算 remaining。displayTime=true 才渲染倒计时 UI。
  //
  // 状态分两部分：
  // - 静态 timer 元数据（timeLimit/displayTime/timeUpMessage）：每帧 turn 覆盖
  // - 动态 startedAt：收到 WaitInput+TINPUT 帧时记 Date.now()，下一帧 turn 到达时清空
  // - 动态 remainingMs：computed，依赖 startedAt + 一个 setInterval 推动的 tick ref
  //
  // 后台 tab 节流、setInterval 跳变：回到前台或下一帧 turn 到达后立即修正（tick 立即重算）。

  /** TINPUT 帧接收时刻（Date.now()），null 表示当前不在 TINPUT 期间。 */
  const tinputStartedAt = ref<number | null>(null);
  /** 当前 TINPUT 总时长（毫秒），null 表示非 TINPUT。与 lastTurn.timeLimit 同步但独立 ref 便于响应式。 */
  const tinputTimeLimit = ref<number | null>(null);
  /** 是否向玩家显示倒计时（ERB 可设 false）。与 lastTurn.displayTime 同步。 */
  const tinputDisplayTime = ref<boolean | null>(null);
  /** ERB 超时提示文案。与 lastTurn.timeUpMessage 同步。 */
  const tinputTimeUpMessage = ref<string | null>(null);
  /**
   * 响应式"当前时刻"——由 setInterval(100ms) ++ 触发 tinputRemainingMs computed 重算。
   * 用 ref 的值本身（递增整数）作为依赖触发器，不直接读 Date.now()。
   * 只在 TINPUT 期间运行 setInterval，非 TINPUT 期间 stop 省电。
   */
  const tinputTick = ref<number>(0);
  /** setInterval 句柄——非 TINPUT 期间为 null。 */
  let tinputIntervalId: ReturnType<typeof setInterval> | null = null;

  /**
   * 启动 setInterval(100ms) 推动 tinputTick。
   * 幂等：若已运行则不重复启动。
   */
  function startTinputTicker(): void {
    if (tinputIntervalId !== null) return;
    tinputIntervalId = setInterval(() => {
      tinputTick.value++;
    }, 500);
  }

  /** 停止 setInterval。幂等：若未运行则无操作。 */
  function stopTinputTicker(): void {
    if (tinputIntervalId !== null) {
      clearInterval(tinputIntervalId);
      tinputIntervalId = null;
    }
  }

  /**
   * ADR-0016 决策三：超时通知——纯函数派生 turn.timedOut。
   *
   * - timedOut=true → 显示 turn.timeUpMessage 或默认文案
   * - timedOut=false → null
   *
   * 行为副作用（已接受）：本通知在**下一帧 turn 到达时**才清空。比用户点击提交晚 50–200ms
   * （网络往返 + server BuildTurn）。TINPUT 场景下人眼无感。
   */
  const timeoutNotice = computed<string | null>(() => {
    const turn = lastTurn.value;
    if (!turn || !turn.timedOut) return null;
    return turn.timeUpMessage ?? 'TINPUT 超时，游戏以默认值继续';
  });

  /**
   * ADR-0016 决策四：当前 TINPUT 剩余毫秒（响应式）。
   *
   * - 非 TINPUT 期间（tinputStartedAt=null 或 tinputTimeLimit=null）→ null
   * - TINPUT 期间 → max(0, timeLimit - (now - startedAt))
   *
   * 依赖 tinputTick 触发响应式更新（每 100ms 一次）；后台 tab 节流跳变回到前台立即修正。
   */
  const tinputRemainingMs = computed<number | null>(() => {
    if (tinputStartedAt.value === null || tinputTimeLimit.value === null) return null;
    // 读取 tinputTick 触发响应式依赖——值本身不参与计算，仅用作更新触发器
    void tinputTick.value;
    const elapsed = Date.now() - tinputStartedAt.value;
    const remaining = tinputTimeLimit.value - elapsed;
    return remaining < 0 ? 0 : remaining;
  });

  /** 是否应渲染倒计时 UI——TINPUT 期间 + ERB 脚本允许显示（displayTime=true）。 */
  const showTinputCountdown = computed<boolean>(
    () =>
      tinputStartedAt.value !== null &&
      tinputTimeLimit.value !== null &&
      tinputDisplayTime.value === true,
  );

  /**
   * 接收一个 WS 帧（裸 JSON 字符串）并更新内部状态。
   *
   * 流程：
   * 1. 存入 lastTurnJson + turnHistory（无论解析成功与否，调试视图都要看到原文）
   * 2. 调 parseTurnRecord 解析为 TurnRecord
   *    - 失败（ParseTurnRecordError 或其他）：写 lastError，displayState 不变
   *    - 成功：清 lastError，继续步骤 3
   * 3. 首帧（isInitial=true）带 protocolVersion；后续帧该字段为 null，不覆盖已 sticky 的版本
   * 4. 应用 diff（若存在）。applyDiff 不修改入参——返回新对象
   * 5. 用 turn 顶层字段覆盖 state/inputType/needValue
   * 6. ADR-0016：根据 turn.state + turn.timeLimit 更新 TINPUT timer 状态——
   *    WaitInput+timeLimit>0 → set startedAt/limit/displayTime/timeUpMessage + 启动 setInterval
   *    其他状态 / timeLimit=null → 清空 + 停止 setInterval
   *
   * 注意：v6 协议中 diff=null 表示本回合显示未变（如纯状态切换），
   * 此时仍需更新 state/inputType/needValue（步骤 5），但不调 applyDiff。
   */
  function applyTurn(rawJson: string): void {
    // 任何 turn 帧到达都算游戏活动——静默监控据此重新计时，并清空状态提示
    // （游戏在产出即不在"忙"，提示只存在于静默期间）
    lastActivityAt.value = Date.now();
    gameStatusHint.value = null;
    // turn 到达 = 本次输入的处理已产出结果（或 TINPUT 超时帧）——解除提交忙态
    inputSubmittedAt.value = null;
    lastTurnJson.value = rawJson;
    turnHistory.value.push(rawJson);
    // 上限保护：超出时丢弃最旧帧（FIFO）
    if (turnHistory.value.length > TURN_HISTORY_MAX) {
      turnHistory.value.splice(0, turnHistory.value.length - TURN_HISTORY_MAX);
    }

    let turn: TurnRecord;
    try {
      turn = parseTurnRecord(rawJson);
    } catch (e) {
      lastError.value = e instanceof ParseTurnRecordError
        ? `协议解析失败：${e.message}`
        : `解析失败：${e instanceof Error ? e.message : String(e)}`;
      // 解析失败仍清 inputInFlight——避免锁永久卡住直到 30s 安全超时
      clearInputInFlight();
      return;
    }

    lastError.value = null;
    lastTurn.value = turn;

    // T-025 D14：同步 serverState——WS 帧 state 反映当前 session 状态
    if (turn.state) {
      serverState.value = turn.state as ServerState;
    }

    // 首帧（isInitial=true）带 protocolVersion；后续帧该字段为 null，不覆盖已 sticky 的版本。
    if (turn.protocolVersion != null) {
      protocolVersion.value = turn.protocolVersion;
    }

    // v7：更新 currentTurnGeneration + 清 inputInFlight（新回合到来，解锁）
    acceptGeneration(turn.generation);

    // 应用 diff（若存在）。applyDiff 不修改入参——返回 {state, signal}：
    // - signal 是归约层对 lineOps 结构性变化（shift_head/clear_screen）的分类摘要，
    //   非 null 时写入 lastDisplaySignal，TerminalDisplay watch 据此驱动滚动副作用。
    //   （必须由归约层产出而非事后 watch(lines.length)——applyDiff 单 tick 内顺序
    //   应用 clear_screen + append，Vue 响应式只观察最终 length，突变中间态不可见。）
    let afterDiff: DisplayState;
    if (turn.diff) {
      const { state: nextState, signal } = applyDiff(displayState.value, turn.diff);
      if (signal != null) lastDisplaySignal.value = signal;
      afterDiff = nextState;
    } else {
      afterDiff = displayState.value;
    }

    // 用 turn 顶层字段覆盖 state/inputType/needValue
    displayState.value = {
      ...afterDiff,
      state: turn.state,
      inputType: turn.inputType ?? null,
      needValue: turn.needValue,
    };

    // ADR-0016：更新 TINPUT timer 状态。仅 WaitInput + timeLimit>0 才视为 TINPUT 期间。
    if (turn.state === 'WaitInput' && typeof turn.timeLimit === 'number' && turn.timeLimit > 0) {
      tinputStartedAt.value = Date.now();
      tinputTimeLimit.value = turn.timeLimit;
      tinputDisplayTime.value = turn.displayTime ?? null;
      tinputTimeUpMessage.value = turn.timeUpMessage ?? null;
      startTinputTicker();
    } else {
      tinputStartedAt.value = null;
      tinputTimeLimit.value = null;
      tinputDisplayTime.value = null;
      tinputTimeUpMessage.value = null;
      stopTinputTicker();
    }
  }

  /**
   * 重置游戏状态到空——断开连接 / 切换游戏目录 / 用户主动清屏时调用。
   * Issue 03 暂不接入；issue 06（重连）+ issue 05（game picker）会用到。
   */
  function reset(): void {
    lastTurnJson.value = null;
    turnHistory.value = [];
    lastError.value = null;
    lastTurn.value = null;
    lastSnapshot.value = null;
    displayState.value = { ...EMPTY_DISPLAY_STATE };
    protocolVersion.value = null;
    // 显示变更信号：置空——避免 reset 后 watch 误触发
    lastDisplaySignal.value = null;
    // T-025 D14：reset 时 serverState 回 Idle
    serverState.value = 'Idle';
    tinputStartedAt.value = null;
    tinputTimeLimit.value = null;
    tinputDisplayTime.value = null;
    tinputTimeUpMessage.value = null;
    stopTinputTicker();
    // v7：重置 generation + 清 inputInFlight
    acceptGeneration(0);
    // issue 09：reset 时清空 mauiError——hot-swap reload 前清旧错误
    mauiError.value = null;
    // 清空游戏状态提示——重开/重置后不残留旧提示
    gameStatusHint.value = null;
    // 重置清空提交忙态——无活跃游戏不处于处理中
    inputSubmittedAt.value = null;
    // game-library：reset 时清退出状态——unloadGame 完成或异常后重置
    exitStatus.value = 'idle';
  }

  /**
   * 用全量快照重建 displayState——WS 升级后立即调用，处理晚加入者场景。
   *
   * 与 C# `GET /snapshot` 端点对称：server 不在首帧 diff 重放历史 PRINT 输出，
   * 故前端必须在 WS onopen 之前先调 `GET /snapshot` 拿当前全屏状态。
   *
   * ADR-0016：snapshot 也携带 timer 字段（timeLimit/displayTime/timeUpMessage），
   * 晚加入者在 TINPUT 期间也能看到倒计时——故此处同样更新 TINPUT timer 状态。
   *
   * - 用 lib/snapshotReducer.applySnapshot 深拷贝 snapshot.lines 到 displayState
   * - 同步更新 protocolVersion（snapshot.protocolVersion 与 TurnRecord 一致，v6）
   * - 同步更新 TINPUT timer 状态（snapshot 含 timer 字段时 set + start setInterval）
   * - **不更新 lastTurn / lastTurnJson / turnHistory**——snapshot 不是 WS 帧，
   *   调试视图继续展示真实 WS 帧历史
   *
   * Issue 03 就地修复（spec 盲点）：原计划 issue 06 接入，但 issue 03 手测被
   * 「首帧无 diff」阻塞，提前实现 setSnapshot 部分；issue 06 剩余的断线重连 +
   * 指数退避才真正属于其范围。
   */
  function setSnapshot(snapshot: DisplaySnapshot): void {
    lastSnapshot.value = snapshot;
    displayState.value = applySnapshot(displayState.value, snapshot);
    if (typeof snapshot.protocolVersion === 'number') {
      protocolVersion.value = snapshot.protocolVersion;
    }
    // v7：更新 currentTurnGeneration + 清 inputInFlight（快照重建，解锁）
    acceptGeneration(snapshot.generation);
    // ADR-0016：晚加入者也可能撞上 TINPUT 期间——snapshot 携带 timer 字段时
    // 启动本地钟表。snapshot.state===WaitInput 校验避免 Quit/Error 状态误启。
    if (
      snapshot.state === 'WaitInput' &&
      typeof snapshot.timeLimit === 'number' &&
      snapshot.timeLimit > 0
    ) {
      tinputStartedAt.value = Date.now();
      tinputTimeLimit.value = snapshot.timeLimit;
      tinputDisplayTime.value = snapshot.displayTime ?? null;
      tinputTimeUpMessage.value = snapshot.timeUpMessage ?? null;
      startTinputTicker();
    } else {
      tinputStartedAt.value = null;
      tinputTimeLimit.value = null;
      tinputDisplayTime.value = null;
      tinputTimeUpMessage.value = null;
      stopTinputTicker();
    }
  }

  // ---------- store 卸载时清理 setInterval ----------
  //
  // Pinia store 通常与 app 同生命周期，但测试中 setActivePinia(createPinia()) 频繁切换时
  // 旧 store 会被丢弃。用 scope effect 清理避免 setInterval 泄漏到下一个测试。
  //
  // 注意：Pinia setup store 没有 onUnmounted 生命周期，但 watch + effect scope 可用。
  // 这里用 watch(tinputStartedAt) 间接触发清理——非 TINPUT 期间 stopTinputTicker 已调过。
  // 主卸载时若仍处于 TINPUT，interval 会泄漏——接受这个小问题（生产环境 store 与 app 同寿）。

  // ---------- Issue 05：loadGame 跨 store 复合动作 ----------
  //
  // 延迟解引用 connection store——Pinia 单例模式允许 store 互相 use，
  // 调用 loadGame 时才 useConnectionStore()，避免初始化期循环依赖。
  // spec L221 流程：disconnect → POST /load-game → connect
  //   - disconnect 先断旧 WS（用户视角立即"loading"）
  //   - POST /load-game 让 C# server 原子重载游戏目录（dispose 旧 session + Preload.Clear
  //     + 重建 ConfigData + Preload.Load + 建新 session）
  //   - connect 让 WS 重新升级到新 session（onopen 后 refreshSnapshot 拿新游戏画面）
  //
  // 错误恢复：
  //   - 路径级错误（400 DIR_NOT_FOUND/MISSING_CSV/MISSING_ERB）：server 端拦在 dispose 前，
  //     旧 session 还活着——前端 connect() 回旧局，UI 显示错误提示
  //   - 加载级错误（500 LOAD_FAILED）：server 端已 dispose 旧 session + Preload.Clear，
  //     新 session 未建——前端 connect() 时 server 建新空 session，旧画面丢失（接受）
  //   - 网络错误（fetch 抛错）：视为 LOAD_FAILED 同样处理
  async function loadGame(dir: string): Promise<void> {
    // 二次进入保护——reload 进行中时拒绝（UI 也应禁用按钮，作兜底）
    if (reloadStatus.value === 'loading') return;
    if (!dir || !dir.trim()) {
      loadGameError.value = new LoadGameError('MISSING_GAME_DIR', 'gameDir 为空', 400);
      return;
    }
    // Issue 07 / spec ID11：MAUI 模式下游戏目录由 C# 启动时 GameResourceExtractor.EnsureGameDir 解压就绪，
    // 不走 HTTP /load-game——MAUI 进程内无 HTTP 服务器，fetch 会失败。
    // 用户在 MAUI 模式下不应看到 GamePicker（App.vue 应隐藏），此处 guard 作兜底防御。
    if (isMauiEnvironment()) {
      gameDir.value = dir.trim();
      return;
    }
    const trimmed = dir.trim();
    reloadStatus.value = 'loading';
    loadGameError.value = null;

    // 延迟解引用 connection store——避免 store 初始化期循环依赖
    const conn = useConnectionStore();
    const httpBase = conn.deriveHttpBase(conn.serverUrl);

    // 步骤 1：disconnect 旧 WS——用户视角立即"loading"
    // 注意：server 端旧 session 还在，/load-game 内部会 dispose 它
    conn.disconnect();
    // 清空本地显示状态——避免新游戏加载时画面闪烁旧帧
    reset();

    // 步骤 2：POST /load-game {gameDir}
    try {
      const resp = await fetch(`${httpBase}/load-game`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ gameDir: trimmed }),
      });

      if (resp.status === 200) {
        // 成功——更新 gameDir + 持久化
        gameDir.value = trimmed;
        writeGameDirToStorage(trimmed);
        // Issue 12：重读 GET /state 更新窗口布局元信息 + serverState——新游戏可能有不同 emuera.config
        // （WindowX/FontSize/LineHeight）。失败不阻塞切换——保持原 layout 字段。
        await fetchAndApplyStateLayout(httpBase);
        // 读取 emuera.config 值（MaxLog 等）。失败不阻塞。
        await fetchConfig(httpBase);
        // 步骤 3：connect 新 session（server 已建好新 session 等待 WS 升级）
        await conn.connect(conn.serverUrl);
      } else {
        // 错误——解析 {error:{code,message}} 或控制权 409 {code,controller,state}
        let code: string = 'LOAD_FAILED';
        let message = `HTTP ${resp.status}`;
        let body: {
          error?: { code?: string; message?: string } | string;
          code?: string;
          controller?: { kind?: string; leaseExpiresAt?: string | null };
          state?: string;
        } | null = null;
        try {
          body = await resp.json();
          if (body?.error && typeof body.error === 'object' && body.error.code) {
            code = body.error.code;
            if (body.error.message) message = body.error.message;
          } else if (typeof body?.code === 'string') {
            code = body.code;
          } else if (typeof body?.error === 'string') {
            code = body.error;
          }
        } catch {
          // body 非 JSON——用默认 code/message
        }
        if (code.startsWith('CONTROL_')) {
          conn.applyControlDenied({
            controller: body?.controller,
            state: body?.state,
            reason: code,
            code,
          });
        } else {
          loadGameError.value = new LoadGameError(code as LoadGameErrorCode, message, resp.status);
        }
        // 错误时仍 connect 回旧 session（路径级错误）/ 建空 session（加载级错误）
        await conn.connect(conn.serverUrl);
      }
    } catch (e) {
      // 网络错误——fetch 抛错
      loadGameError.value = new LoadGameError(
        'LOAD_FAILED',
        e instanceof Error ? e.message : String(e),
        0,
      );
      // 尝试重连——可能 server 重启中
      try {
        await conn.connect(conn.serverUrl);
      } catch {
        // 重连也失败——保持 loadGameError 状态，UI 显示"无法连接服务器"
      }
    } finally {
      reloadStatus.value = 'idle';
    }
  }

  /** Issue 05：清空 loadGameError——用户关闭错误提示时调用。 */
  function clearLoadGameError(): void {
    loadGameError.value = null;
  }

  /**
   * issue 09 文件选择器：设置游戏目录（MAUI 模式）——
   * 用户通过原生 FolderPicker 选中目录后，`useAppInit` 消息处理器调此方法更新 `gameDir` + 持久化。
   *
   * 与 HTTP 模式 `loadGame` 的区别：MAUI 模式下 C# 侧直接 hot-swap reload（不经过 HTTP `/load-game`），
   * Vue 端只需更新 `gameDir` ref + localStorage，不触发 fetch / WS 重连。
   *
   * @param dir 用户选中的游戏目录绝对路径。
   */
  function setGameDir(dir: string): void {
    const trimmed = dir.trim();
    if (!trimmed) return;
    gameDir.value = trimmed;
    // MAUI 模式不持久化 gameDir——当前游戏不跨进程恢复（见上方 gameDir 初始化注释），
    // 写入只会留下过期键。HTTP 模式仍需持久化供重启后与 GET /state 比对。
    if (!isMauiEnvironment()) {
      writeGameDirToStorage(trimmed);
    }
  }

  /**
   * game-library spec ID5 / ID7：设置主目录——
   * 用户通过「更改主目录」按钮（Windows FolderPicker / Android SAF 选择器）选中后调用。
   *
   * 更新 `mainGameDir` ref + 持久化到 localStorage。
   * **不**触发 scanGames——调用方（useAppInit）收到 folderPicked / safDirectoryPicked 后
   * 自行投递 scanGames，store 仅管状态。
   *
   * @param dir 用户选中的主目录绝对路径。
   */
  function setMainGameDir(dir: string): void {
    const trimmed = dir.trim();
    if (!trimmed) return;
    mainGameDir.value = trimmed;
    writeMainGameDirToStorage(trimmed);
  }

  /**
   * game-library spec ID5：设置上次玩过的游戏名——
   * 用户点击列表项加载游戏时调用，列表页据此高亮。
   *
   * @param name 游戏目录名（GameEntry.name，不是完整路径）。
   */
  function setLastPlayedGame(name: string): void {
    const trimmed = name.trim();
    if (!trimmed) return;
    lastPlayedGame.value = trimmed;
    writeLastPlayedGameToStorage(trimmed);
  }

  /**
   * game-library spec ID3 / ID7：写入 scanGames 消息回复结果——
   * `useAppInit` 收到 C# 推来的 `gamesScanned` 消息后调此方法。
   *
   * 同步更新 scannedGames / scanRootDir / scanStatus——
   * 列表页 watch scannedGames 重新渲染，scanStatus='idle' 关闭 loading 占位。
   *
   * @param games C# GameScanner.Scan 返回的游戏列表
   * @param rootDir 扫描时使用的主目录路径（C# 回复中携带）
   * @param rootDirExists 主目录是否存在（C# 经 IGameDirAccessor 检查；可选）
   * @param displayPath issues/15：rootDir 的可读形态（SAF content URI → 「内部存储 / games / eratoho」）。
   *   C# `SafPath.TryGetDisplayPath` 给出；省略 / 为 null 时渲染方回退折叠文案。
   *   **仅用于展示**——模式判定仍用 rootDir 原文（见 {@link scanRootDirDisplay}）。
   */
  function setScannedGames(
    games: GameEntry[],
    rootDir: string | null,
    rootDirExists?: boolean | null,
    displayPath?: string | null,
  ): void {
    scannedGames.value = games;
    scanRootDir.value = rootDir;
    scanRootDirExists.value = rootDirExists ?? null;
    scanRootDirDisplayEntry.value =
      rootDir && displayPath ? { rootDir, displayPath } : null;
    scanStatus.value = 'idle';
    // 同步 mainGameDir——C# 端 HandleScanGames 收到非空 rootDir 时已写 Preferences，
    // Vue 端此处同步 localStorage 让首启动 UI 立即正确
    if (rootDir && rootDir !== mainGameDir.value) {
      mainGameDir.value = rootDir;
      writeMainGameDirToStorage(rootDir);
    }
  }

  /**
   * issues/13 M5：重扫回执（irCacheRescanned ok=true）——把该游戏的 IR 缓存徽章清掉。
   * 缓存已删（下次启动建库轮），徽章保持存在会误导「还有缓存」。
   */
  function applyIrCacheRescanned(path: string): void {
    const target = scannedGames.value.find((g) => g.fullPath === path);
    if (target) {
      target.irCached = false;
      target.irCacheBytes = undefined;
      target.irCacheBuiltUtc = null;
    }
  }

  /**
   * Web/HTTP 模式：请求 server 扫描主目录下的游戏（复用 C# `GameScanner.Scan`）。
   *
   * 浏览器沙箱不能枚举本地文件系统（GamePicker 因此只能手动输入路径），但 C# server
   * 运行在本机可访问文件系统——由它代扫，MAUI 同款游戏列表即可在 Web 模式复用。
   *
   * 语义对齐 MAUI 的 `gamesScanned` 消息：成功（HTTP 200）经 `setScannedGames` 写入
   * scannedGames / scanRootDir / scanRootDirExists / scanStatus；rootDir 不存在也是 200
   * （rootDirExists=false，空列表）。网络 / 5xx 错误返回 false（组件据此展示连接错误）。
   *
   * @param rootDir 主目录绝对路径。
   * @returns 请求是否成功（HTTP 200）。
   */
  async function scanGamesHttp(rootDir: string): Promise<boolean> {
    const trimmed = rootDir.trim();
    if (!trimmed) return false;
    const conn = useConnectionStore();
    const httpBase = conn.deriveHttpBase(conn.serverUrl);
    scanStatus.value = 'scanning';
    scanRootDir.value = trimmed;
    try {
      const resp = await fetch(`${httpBase}/game/scan`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ rootDir: trimmed }),
      });
      if (resp.status !== 200) {
        setScannedGames([], trimmed, null);
        return false;
      }
      const body = (await resp.json()) as {
        rootDir?: unknown;
        rootDirExists?: unknown;
        games?: Array<{ name?: unknown; fullPath?: unknown }>;
      } | null;
      const games: GameEntry[] = Array.isArray(body?.games)
        ? body.games
            .filter((g) => typeof g?.name === 'string' && typeof g?.fullPath === 'string')
            .map((g) => ({ name: g.name as string, fullPath: g.fullPath as string }))
        : [];
      setScannedGames(
        games,
        typeof body?.rootDir === 'string' ? body.rootDir : trimmed,
        typeof body?.rootDirExists === 'boolean' ? body.rootDirExists : null,
      );
      return true;
    } catch {
      setScannedGames([], trimmed, null);
      return false;
    }
  }

  /**
   * Web/HTTP 模式：请求 server 列举目录的子目录（复用 C# `DirectoryLister`）。
   *
   * 供 Web 游戏选择页的「目录浏览」逐层定位主目录，替代手动输入绝对路径。
   *
   * @param dir 要浏览的目录绝对路径。
   * @returns {currentPath, parentPath, dirs}；网络错误 / 目录不存在返回 null。
   */
  async function browseDirectoryHttp(dir: string): Promise<{
    currentPath: string;
    parentPath: string | null;
    dirs: string[];
  } | null> {
    const trimmed = dir.trim();
    if (!trimmed) return null;
    const conn = useConnectionStore();
    const httpBase = conn.deriveHttpBase(conn.serverUrl);
    try {
      const resp = await fetch(`${httpBase}/game/dirs`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ dir: trimmed }),
      });
      if (resp.status !== 200) return null;
      const body = (await resp.json()) as {
        currentPath?: unknown;
        parentPath?: unknown;
        dirs?: unknown;
      } | null;
      return {
        currentPath: typeof body?.currentPath === 'string' ? body.currentPath : trimmed,
        parentPath: typeof body?.parentPath === 'string' ? body.parentPath : null,
        dirs: Array.isArray(body?.dirs)
          ? body.dirs.filter((d): d is string => typeof d === 'string')
          : [],
      };
    } catch {
      return null;
    }
  }

  /**
   * game-library spec ID10 / ID12：开始退出游戏流程——
   * 由 App.vue 退出按钮点击处理调用。
   *
   * 与 `loadGame` / `quickRestart` 的区别：
   * - 不投递 HTTP 请求——MAUI 模式靠 C# BridgeHost.HandleExitGame 处理（Dispose + 重建 host）
   * - 不重置 displayState——C# 重建 BridgeHost 后新 host 不 Start，displayState 保持原样
   *   直到 Vue 收到 gameExited 后由调用方再调 reset()
   *
   * 此方法仅置 `exitStatus='exiting'`——实际 exitGame 消息投递由调用方负责
   * （`mauiBridge.exitGame()`，分离 store 状态与桥接投递职责，便于单测）。
   *
   * 二次进入保护：exitStatus='exiting' 时拒绝。
   */
  function beginExitGame(): boolean {
    if (exitStatus.value === 'exiting') return false;
    exitStatus.value = 'exiting';
    return true;
  }

  /**
   * game-library spec ID10：完成退出游戏流程——
   * `useAppInit` 收到 C# 推来的 `gameExited` 消息后调此方法（HTTP 路径为 `exitGameHttp`）。
   *
   * 清空当前游戏相关状态（gameDir / serverState / displayState / lastPlayedGame 不清——
   * 列表页高亮仍需 lastPlayedGame），重置 exitStatus='idle' 让 UI 解锁。
   * 视图重置回 terminal——currentView 是 Pinia 内存态，MAUI 退出只重建 C# host
   * 不重建 WebView，不重置则下次进游戏仍停留在退出前的 debug/settings 视图。
   *
   * **不**投递 scanGames——调用方收到 gameExited 后自行投递 scanGames 重新填充列表。
   */
  function completeExitGame(): void {
    // 清空当前游戏显示状态——回列表态不残留旧画面
    lastTurnJson.value = null;
    turnHistory.value = [];
    lastTurn.value = null;
    lastSnapshot.value = null;
    displayState.value = { ...EMPTY_DISPLAY_STATE };
    protocolVersion.value = null;
    // 显示变更信号：置空——避免重挂载时残留信号触发 watch
    lastDisplaySignal.value = null;
    serverState.value = 'Idle';
    // 清 gameDir——回到列表态无活跃游戏
    gameDir.value = null;
    try {
      localStorage.removeItem(GAME_DIR_STORAGE_KEY);
    } catch {
      // 静默
    }
    exitStatus.value = 'idle';
    // 退出游戏清空状态提示——回列表态不残留
    gameStatusHint.value = null;
    // 退出清空提交忙态——回列表态不处于处理中
    inputSubmittedAt.value = null;
    stopTinputTicker();
    acceptGeneration(0);
    // 视图拨回 terminal——覆盖 MAUI（gameExited）与 HTTP（exitGameHttp）两条退出路径，
    // 用户下次进游戏从 terminal 起步，不残留退出前的 debug/settings 视图。
    useUiStore().switchView('terminal');
  }

  /**
   * HTTP 模式退出游戏——前端与 server 绑定的 Web 游戏页「退出」路径
   * （对齐 MAUI 的 exitGame 桥接语义，T-025 后无 stdin pipe）。
   *
   * 流程：
   * 1. 置 exitStatus='exiting'（二次进入保护，语义对齐 beginExitGame）
   * 2. disconnect 旧 WS
   * 3. DELETE /session（dispose 会话）
   * 4. completeExitGame()——清前端状态回列表态
   * 5. 重新扫描主目录——列表页填充数据
   *
   * 失败不阻断——DELETE 失败也清前端状态回列表态。
   */
  async function exitGameHttp(): Promise<void> {
    if (exitStatus.value === 'exiting') return;
    exitStatus.value = 'exiting';
    const conn = useConnectionStore();
    const httpBase = conn.deriveHttpBase(conn.serverUrl);
    conn.disconnect();
    try {
      await fetch(`${httpBase}/session`, { method: 'DELETE' });
    } catch {
      // DELETE 失败不阻断——仍回列表态
    }
    completeExitGame();
    const rootDir = scanRootDir.value ?? mainGameDir.value;
    if (rootDir) {
      await scanGamesHttp(rootDir);
    }
  }

  /** issue 09：清空 mauiError——用户关闭错误提示时调用。 */
  function clearMauiError(): void {
    mauiError.value = null;
  }

  /** issues/15：写入「所有文件访问」授权状态——permissionStatus 消息（checkPermission 回复）触发。 */
  function setAllFilesAccess(granted: boolean): void {
    allFilesAccess.value = granted;
  }

  /** 设置游戏静默状态提示——前端静默监控收到 gameThreadStatus 回复时调用。 */
  function setGameStatusHint(hint: 'running' | 'stopped'): void {
    gameStatusHint.value = hint;
  }

  /** 清空游戏静默状态提示——新 turn / 等待输入 / 退出游戏时调用。 */
  function clearGameStatusHint(): void {
    gameStatusHint.value = null;
  }

  /**
   * 标记已提交游戏输入——connection store 的 sendInput 唯一入口调用。
   * 静默监控以此区分「提交后等待处理」与「等待玩家输入」，幂等（重复提交只刷新时间戳）。
   */
  function markInputSubmitted(): void {
    inputSubmittedAt.value = Date.now();
  }

  /**
   * T-025 D14：从 GET /state 响应写入 serverState——App.vue onMounted 调用。
   * 封装外部修改，避免组件直接写 store ref。
   */
  function applyServerState(state: string): void {
    serverState.value = state as ServerState;
  }

  /**
   * T-025 D14：快速重开失败时回退到空路径选择器——清空 localStorage 预填 + gameDir + serverState。
   * quickRestart 的 4 个失败分支共用，避免重复代码。
   */
  function resetToIdlePicker(): void {
    clearGameDirFromStorage();
    gameDir.value = null;
    serverState.value = 'Idle';
  }

  /**
   * T-025：GET /state 并写入 serverState + 窗口布局元信息——App.vue onMounted / loadGame / quickRestart 共用。
   *
   * 返回 { gameDir, state } 供调用方决策（如 App.vue 判 idle → 展示选择器）。
   * 失败时（网络错误 / 非 200）返回 { gameDir: null, state: null }，不抛错——
   * 调用方通过 state === null 区分「无法连接」与「空闲态」（state === 'Idle'）。
   */
  async function fetchAndApplyStateLayout(
    httpBase: string,
  ): Promise<{ gameDir: string | null; state: string | null }> {
    try {
      const resp = await fetch(`${httpBase}/state`);
      if (resp.status !== 200) return { gameDir: null, state: null };
      const body = await resp.json();
      const gameDirVal = typeof body?.gameDir === 'string' ? body.gameDir : null;
      const stateVal = typeof body?.state === 'string' ? body.state : null;
      if (stateVal) serverState.value = stateVal as ServerState;
      setGameLayout({
        windowWidth: typeof body?.windowWidth === 'number' ? body.windowWidth : null,
        fontSize: typeof body?.fontSize === 'number' ? body.fontSize : null,
        lineHeight: typeof body?.lineHeight === 'number' ? body.lineHeight : null,
        gameColumns: typeof body?.gameColumns === 'number' ? body.gameColumns : null,
        fontName: typeof body?.fontName === 'string' ? body.fontName : null,
      });
      return { gameDir: gameDirVal, state: stateVal };
    } catch {
      return { gameDir: null, state: null };
    }
  }

  /**
   * T-025 D14：快速重开——同目录一键重载，绕过选择器。
   *
   * 流程：
   * 1. GET /state → 拿当前 gameDir（权威目标，非 localStorage 回退）
   * 2. disconnect 旧 WS + reset 显示状态
   * 3. DELETE /session → 干净替换旧会话
   * 4. POST /load-game {当前 gameDir} → 重载同一目录
   * 5. 成功 → connect WS 入新 session
   * 6. 失败 → 清空 localStorage 预填 + gameDir，回退到空路径选择器，等待玩家手动输入
   *
   * 与 loadGame 的区别：
   * - 目标目录来自 GET /state（当前 session 目录），而非参数传入
   * - 先 DELETE /session 再 /load-game（loadGame 不 DELETE，依赖 /load-game 内部 dispose）
   * - 失败时清空 gameDir（loadGame 失败时保留旧 gameDir）
   *
   * server 端状态备注：快速重开失败后（DELETE 已执行 + /load-game 校验失败），
   * server 端 _session==null（已被 DELETE dispose），GET /state 返 {state:"Idle", gameDir:null}——
   * 前端不感知 Current 残留，看到空选择器。
   */
  async function quickRestart(): Promise<void> {
    if (reloadStatus.value === 'loading') return;

    reloadStatus.value = 'loading';
    loadGameError.value = null;

    const conn = useConnectionStore();
    const httpBase = conn.deriveHttpBase(conn.serverUrl);

    // Step 1: GET /state 拿当前 gameDir（权威目标）
    const stateInfo = await fetchAndApplyStateLayout(httpBase);
    const currentGameDir = stateInfo.gameDir;

    if (stateInfo.state === null) {
      // 网络错误——无法获取当前 gameDir，清空并回退到空选择器
      resetToIdlePicker();
      loadGameError.value = new LoadGameError('LOAD_FAILED', '无法连接服务器', 0);
      reloadStatus.value = 'idle';
      return;
    }

    if (!currentGameDir) {
      // 无活跃游戏或 gameDir 为 null——清空并回退到空选择器
      resetToIdlePicker();
      reloadStatus.value = 'idle';
      return;
    }

    // Step 2: disconnect 旧 WS + reset 显示状态
    conn.disconnect();
    reset();

    try {
      // Step 3: DELETE /session（干净替换旧会话）
      try {
        await fetch(`${httpBase}/session`, { method: 'DELETE' });
      } catch {
        // DELETE 失败不阻断——/load-game 内部会 dispose 旧 session
      }

      // Step 4: POST /load-game {currentGameDir}
      const resp = await fetch(`${httpBase}/load-game`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ gameDir: currentGameDir }),
      });

      if (resp.status === 200) {
        // 成功——更新 gameDir + 持久化
        gameDir.value = currentGameDir;
        writeGameDirToStorage(currentGameDir);
        // Issue 12：重读 GET /state 更新窗口布局元信息 + serverState
        await fetchAndApplyStateLayout(httpBase);
        // Step 5: connect 新 session
        await conn.connect(conn.serverUrl);
      } else {
        // 失败——D14：清空预填，回退到空路径选择器
        let code: string = 'LOAD_FAILED';
        let message = `HTTP ${resp.status}`;
        let body: { error?: { code?: string; message?: string }; code?: string; controller?: unknown; state?: string } | null = null;
        try {
          body = await resp.json();
          if (body?.error?.code) code = body.error.code;
          if (body?.error?.message) message = body.error.message;
          else if (typeof body?.code === 'string') code = body.code;
        } catch {
          // body 非 JSON——用默认 code/message
        }
        if (code.startsWith('CONTROL_')) {
          conn.applyControlDenied({
            controller: body?.controller,
            state: body?.state,
            reason: code,
            code,
          });
        } else {
          loadGameError.value = new LoadGameError(code as LoadGameErrorCode, message, resp.status);
          resetToIdlePicker();
        }
      }
    } catch (e) {
      // 网络错误——fetch 抛错
      loadGameError.value = new LoadGameError(
        'LOAD_FAILED',
        e instanceof Error ? e.message : String(e),
        0,
      );
      resetToIdlePicker();
    } finally {
      reloadStatus.value = 'idle';
    }
  }

  /**
   * Issue 12：写入窗口布局元信息——App.vue onMounted + loadGame 成功后调用。
   *
   * 入参 null / undefined 表示 server 响应缺失该字段——保持原值不动，
   * 让前端 fallback 到上一已知值或默认 760/18/19/84/"ＭＳ ゴシック"。
   */
  function setGameLayout(opts: {
    windowWidth?: number | null;
    fontSize?: number | null;
    lineHeight?: number | null;
    gameColumns?: number | null;
    fontName?: string | null;
  }): void {
    if (typeof opts.windowWidth === 'number') windowWidth.value = opts.windowWidth;
    if (typeof opts.fontSize === 'number') fontSize.value = opts.fontSize;
    if (typeof opts.lineHeight === 'number') lineHeight.value = opts.lineHeight;
    if (typeof opts.gameColumns === 'number') gameColumns.value = opts.gameColumns;
    if (typeof opts.fontName === 'string') fontName.value = opts.fontName;
  }

  async function fetchConfig(httpBase: string): Promise<void> {
    try {
      const resp = await fetch(`${httpBase}/config`);
      if (resp.status !== 200) return;
      const body = await resp.json();
      if (typeof body?.maxLog === 'number') maxLog.value = body.maxLog;
    } catch {
      // 网络错误——保持 fallback 值
    }
  }

  /**
   * 全局设置（ADR-0030）——统一应用权威载荷（GET/PUT/DELETE /settings 响应或 MAUI config 消息）。
   * 字段校验与 useAppInit 的 config 消息分支逐位一致；agentLogEnabled 兼容 bool（MAUI config）
   * 与 "true"/"false" 字符串（HTTP /settings）两种形态。
   */
  function applySettingsUpdate(payload: Record<string, unknown>): void {
    if (typeof payload.maxLog === 'number') maxLog.value = payload.maxLog;
    if (typeof payload.agentLogEnabled === 'boolean') {
      agentLogEnabled.value = payload.agentLogEnabled;
    } else if (payload.agentLogEnabled === 'true' || payload.agentLogEnabled === 'false') {
      agentLogEnabled.value = payload.agentLogEnabled === 'true';
    }
    if (payload.displayReportOverride === 'auto' || payload.displayReportOverride === 'off' || payload.displayReportOverride === 'on') {
      displayReportOverride.value = payload.displayReportOverride;
    }
    for (const perfKey of ['lazyFunctionLoad', 'irDiskCacheLoad', 'irFirstDirList', 'irFileListCache', 'srcContentCache', 'parallelErbLoad', 'parallelParseScript'] as const) {
      const value = payload[perfKey];
      if (value === 'auto' || value === 'off' || value === 'on') {
        perfOverrides.value[perfKey] = value;
      }
    }
    if (payload.sweepGatePolicy === 'auto' || payload.sweepGatePolicy === 'forceon' || payload.sweepGatePolicy === 'forceoff') {
      sweepGatePolicy.value = payload.sweepGatePolicy;
    }
    if (payload.safReadDegree === 'auto' || (typeof payload.safReadDegree === 'string' && /^\d+$/.test(payload.safReadDegree))) {
      safReadDegree.value = payload.safReadDegree;
    }
    if (payload.maxParallelErbFiles === 'auto' || payload.maxParallelErbFiles === '4' || payload.maxParallelErbFiles === '8' || payload.maxParallelErbFiles === '16') {
      maxParallelErbFiles.value = payload.maxParallelErbFiles;
    }
    if (payload.dimLockPolicy === 'auto' || payload.dimLockPolicy === 'nolock') {
      dimLockPolicy.value = payload.dimLockPolicy;
    }
  }

  /** HTTP 模式（ADR-0030）：GET /settings 拉全局设置权威值。失败静默（保持 fallback）。 */
  async function fetchSettings(httpBase: string): Promise<void> {
    try {
      const resp = await fetch(`${httpBase}/settings`);
      if (resp.status !== 200) return;
      applySettingsUpdate((await resp.json()) as Record<string, unknown>);
    } catch {
      // 网络错误——保持 fallback 值
    }
  }

  /** HTTP 模式（ADR-0030）：PUT /settings 单键。成功以服务端响应同步权威状态；失败返回 false。 */
  async function updateSettingsHttp(httpBase: string, key: string, value: string): Promise<boolean> {
    try {
      const resp = await fetch(`${httpBase}/settings`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ [key]: value }),
      });
      if (resp.status !== 200) return false;
      applySettingsUpdate((await resp.json()) as Record<string, unknown>);
      return true;
    } catch {
      return false;
    }
  }

  /** HTTP 模式（ADR-0030）：DELETE /settings 恢复默认。成功以服务端响应同步权威状态；失败返回 false。 */
  async function resetSettingsHttp(httpBase: string): Promise<boolean> {
    try {
      const resp = await fetch(`${httpBase}/settings`, { method: 'DELETE' });
      if (resp.status !== 200) return false;
      applySettingsUpdate((await resp.json()) as Record<string, unknown>);
      return true;
    } catch {
      return false;
    }
  }

  return {
    lastTurnJson,
    turnHistory,
    lastError,
    lastTurn,
    lastSnapshot,
    displayState,
    protocolVersion,
    // v7：按钮 generation 失效 + 三重守卫
    currentTurnGeneration,
    inputInFlight,
    hasActiveButtons,
    setInputInFlight,
    clearInputInFlight,
    // ADR-0016：暴露 TINPUT timer 状态供 UI / 测试访问
    timeoutNotice,
    tinputStartedAt,
    tinputTimeLimit,
    tinputDisplayTime,
    tinputTimeUpMessage,
    tinputRemainingMs,
    showTinputCountdown,
    // 显示变更信号：暴露给 TerminalDisplay watch
    lastDisplaySignal,
    // game-library spec ID10：Android 物理返回键退出确认
    backButtonPressedTick,
    applyTurn,
    setSnapshot,
    reset,
    // Issue 05：游戏目录与重载状态
    gameDir,
    reloadStatus,
    loadGameError,
    loadGame,
    clearLoadGameError,
    // issue 09：MAUI 模式文件选择器状态
    mauiError,
    setGameDir,
    clearMauiError,
    // 游戏静默状态提示（MAUI）——前端静默监控
    gameStatusHint,
    lastActivityAt,
    inputSubmittedAt,
    setGameStatusHint,
    clearGameStatusHint,
    markInputSubmitted,
    // game-library：主目录 + 游戏列表 + 上次玩过 + 退出游戏 + 目录浏览器
    mainGameDir,
    lastPlayedGame,
    scannedGames,
    scanRootDir,
    // issues/15：主目录的展示态可读路径（SAF content URI 解出的「内部存储 / …」）——仅供渲染
    scanRootDirDisplay,
    // issues/15：Android「所有文件访问」授权状态
    allFilesAccess,
    setAllFilesAccess,
    scanRootDirExists,
    scanStatus,
    exitStatus,
    setMainGameDir,
    setLastPlayedGame,
    setScannedGames,
    applyIrCacheRescanned,
    // Web/HTTP 模式：server 代扫 + 目录浏览
    scanGamesHttp,
    browseDirectoryHttp,
    beginExitGame,
    completeExitGame,
    exitGameHttp,
    // T-025 D14：快速重开 + server 状态
    quickRestart,
    serverState,
    applyServerState,
    fetchAndApplyStateLayout,
    // Issue 12：窗口布局元信息
    windowWidth,
    fontSize,
    lineHeight,
    gameColumns,
    fontName,
    maxLog,
    agentLogEnabled,
    displayReportOverride,
    perfOverrides,
    sweepGatePolicy,
    safReadDegree,
    maxParallelErbFiles,
    dimLockPolicy,
    agentLogContent,
    agentLogTruncated,
    fetchConfig,
    applySettingsUpdate,
    fetchSettings,
    updateSettingsHttp,
    resetSettingsHttp,
    setGameLayout,
    // 缩放比例
    scale,
    effectiveScale,
    isMinScale,
    isMaxScale,
    setScale,
  };
});
