import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { initAppState } from '../useAppInit';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';

/**
 * T-025 D9 rev：initAppState 单测——App.vue onMounted 提取的逻辑。
 *
 * 测试矩阵（spec L122）：
 * - 空闲态（gameDir==null / state=="Idle"）→ 展示选择器 + 预填 localStorage，不自动 loadGame / connect
 * - 有活跃 session（state 非 Idle + gameDir 非 null）→ conn.connect() 重连
 * - server 未启动（fetch 抛错）→ 不自动连
 *
 * 推翻 issue 05「异则 loadGame」自动加载语义——空闲态不再自动 loadGame(localStorage value)。
 */

function makeLocalStorage(): Storage {
  const map = new Map<string, string>();
  return {
    getItem: (k: string) => (map.has(k) ? map.get(k)! : null),
    setItem: (k: string, v: string) => {
      map.set(k, v);
    },
    removeItem: (k: string) => {
      map.delete(k);
    },
    clear: () => {
      map.clear();
    },
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    get length() {
      return map.size;
    },
  };
}

describe('initAppState (T-025 D9 rev)', () => {
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  /** 模拟 GET /state 响应。fetchAndApplyStateLayout 内部 fetch /state。 */
  function mockStateResponse(status: number, body: unknown): void {
    fetchMock.mockResolvedValueOnce({
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response);
  }

  /** 模拟 GET /config 响应。fetchConfig 内部 fetch /config。 */
  function mockConfigResponse(status: number, body: unknown): void {
    fetchMock.mockResolvedValueOnce({
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response);
  }

  it('空闲态（gameDir==null + state=="Idle"）→ 不调 connect，展示选择器', async () => {
    // localStorage 有上次目录——选择器应预填，但不自动 loadGame
    // 必须在 useGameStore() 之前设置（store 初始化时读 localStorage）
    localStorage.setItem('app.gameDir', 'D:/old/game');

    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // game.gameDir 从 localStorage 初始化——确认预填源
    expect(game.gameDir).toBe('D:/old/game');

    mockStateResponse(200, { state: 'Idle', gameDir: null });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    // D9 rev：空闲态不自动 loadGame / connect
    expect(connectSpy).not.toHaveBeenCalled();
    // gameDir 保留 localStorage 值（选择器预填源）
    expect(game.gameDir).toBe('D:/old/game');
    // serverState 应被 fetchAndApplyStateLayout 写入为 Idle
    expect(game.serverState).toBe('Idle');
  });

  it('有活跃 session（state=="WaitInput" + gameDir 非 null）→ conn.connect() 重连', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    mockStateResponse(200, {
      state: 'WaitInput',
      gameDir: 'D:/current/game',
      windowWidth: 760,
      fontSize: 18,
      lineHeight: 19,
      gameColumns: 84,
      fontName: 'ＭＳ ゴシック',
    });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    expect(connectSpy).toHaveBeenCalledOnce();
    expect(game.serverState).toBe('WaitInput');
    expect(game.maxLog).toBe(5000);
  });

  it('server 未启动（fetch 抛错）→ 不调 connect', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    fetchMock.mockRejectedValueOnce(new Error('Network error'));

    await initAppState();

    expect(connectSpy).not.toHaveBeenCalled();
    // serverState 保持初始 Idle（fetchAndApplyStateLayout catch 后不写 serverState）
    expect(game.serverState).toBe('Idle');
  });

  it('空闲态不自动 loadGame——推翻 issue 05 自动加载语义', async () => {
    // issue 05 旧行为：localStorage gameDir 与 server gameDir 不同 → 自动 loadGame(localStorage)
    // T-025 D9 rev：空闲态展示选择器 + 预填，不自动 loadGame
    // 必须在 useGameStore() 之前设置（store 初始化时读 localStorage）
    localStorage.setItem('app.gameDir', 'D:/old/game');

    const game = useGameStore();
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // localStorage 有值，server 返 idle（gameDir=null）——旧行为会 loadGame('D:/old')
    expect(game.gameDir).toBe('D:/old/game');

    mockStateResponse(200, { state: 'Idle', gameDir: null });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    // 不调 connect，不调 loadGame（fetch 调两次 = GET /state + GET /config，无 POST /load-game）
    expect(connectSpy).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    // gameDir 保留——选择器预填
    expect(game.gameDir).toBe('D:/old/game');
    expect(game.maxLog).toBe(5000);
  });

  it('GET /state 非 200 → 不调 connect（state==null 视为无法连接）', async () => {
    const conn = useConnectionStore();
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    mockStateResponse(500, { error: 'Internal Server Error' });

    await initAppState();

    expect(connectSpy).not.toHaveBeenCalled();
  });

  it('有活跃 session 时写入窗口布局元信息', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    mockStateResponse(200, {
      state: 'WaitInput',
      gameDir: 'D:/game',
      windowWidth: 1000,
      fontSize: 20,
      lineHeight: 22,
      gameColumns: 99,
      fontName: 'CustomFont',
    });
    mockConfigResponse(200, { maxLog: 5000 });

    await initAppState();

    expect(game.windowWidth).toBe(1000);
    expect(game.fontSize).toBe(20);
    expect(game.lineHeight).toBe(22);
    expect(game.gameColumns).toBe(99);
    expect(game.fontName).toBe('CustomFont');
    expect(game.maxLog).toBe(5000);
  });
});

// ---------- handleMauiMessage：游戏线程存活探测回复 ----------

describe('handleMauiMessage - gameThreadStatus', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('alive=true → gameStatusHint=running（游戏运行中提示）', async () => {
    // 模拟 MAUI unpackaged window（app.local 虚拟主机）+ chrome.webview 让 postInput 可用
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    expect(typeof handler).toBe('function');

    handler!({ type: 'gameThreadStatus', alive: true });

    expect(game.gameStatusHint).toBe('running');
  });

  it('alive=false → gameStatusHint=stopped（游戏已停止提示）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'gameThreadStatus', alive: false });

    expect(game.gameStatusHint).toBe('stopped');
  });
});

// ---------- handleMauiMessage：config 消息（A0 文件日志开关）----------

describe('handleMauiMessage - config agentLogEnabled (A0)', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('config 消息含 agentLogEnabled=true → 写入 store（设置页开关渲染依据）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    expect(game.agentLogEnabled).toBe(false); // 默认 false（Android 默认关闭）

    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    expect(typeof handler).toBe('function');

    handler!({ type: 'config', maxLog: 1000, agentLogEnabled: true });
    expect(game.agentLogEnabled).toBe(true);
    expect(game.maxLog).toBe(1000);

    // 再推 false → 同步关闭
    handler!({ type: 'config', agentLogEnabled: false });
    expect(game.agentLogEnabled).toBe(false);
  });

  it('config 消息缺 agentLogEnabled 字段 → 保持默认值不变', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', maxLog: 500 });
    expect(game.agentLogEnabled).toBe(false);
    expect(game.maxLog).toBe(500);
  });
});

// ---------- handleMauiMessage：config 消息（启动日志覆盖三态）----------

describe('handleMauiMessage - config displayReportOverride', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('config 消息含 displayReportOverride → 写入 store（设置页分段控件渲染依据）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    expect(game.displayReportOverride).toBe('off'); // 默认 off（抑制启动日志，与历史默认一致）

    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', displayReportOverride: 'auto' });
    expect(game.displayReportOverride).toBe('auto');

    // 再推 on / off → 逐个同步
    handler!({ type: 'config', displayReportOverride: 'on' });
    expect(game.displayReportOverride).toBe('on');
    handler!({ type: 'config', displayReportOverride: 'off' });
    expect(game.displayReportOverride).toBe('off');
  });

  it('config 消息含非法 displayReportOverride 值 → 忽略不写 store', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', displayReportOverride: 'bogus' });
    expect(game.displayReportOverride).toBe('off'); // 保持原值
  });
});

// ---------- handleMauiMessage：config 消息（ADR-0026 诊断旋钮一：平台门策略）----------

describe('handleMauiMessage - config sweepGatePolicy (ADR-0026 诊断分组)', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('config 消息含 sweepGatePolicy → 写入 store（诊断分段控件渲染依据）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    expect(game.sweepGatePolicy).toBe('auto'); // 默认 auto（跟随平台门，与旧版本逐位一致）

    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', sweepGatePolicy: 'forceon' });
    expect(game.sweepGatePolicy).toBe('forceon');

    // 再推 forceoff / auto → 逐个同步
    handler!({ type: 'config', sweepGatePolicy: 'forceoff' });
    expect(game.sweepGatePolicy).toBe('forceoff');
    handler!({ type: 'config', sweepGatePolicy: 'auto' });
    expect(game.sweepGatePolicy).toBe('auto');
  });

  it('config 消息含非法 sweepGatePolicy 值 → 忽略不写 store', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', sweepGatePolicy: 'on' }); // 旧三态词表不是本旋钮词表
    expect(game.sweepGatePolicy).toBe('auto'); // 保持原值
  });
});

// ---------- handleMauiMessage：config 消息（ADR-0026 诊断旋钮二：SAF 读取并发度）----------

describe('handleMauiMessage - config safReadDegree (ADR-0026 诊断分组)', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('config 消息含 safReadDegree → 写入 store（诊断分段控件渲染依据）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    expect(game.safReadDegree).toBe('auto'); // 默认 auto（每树 1 并发，与旧版本逐位一致）

    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', safReadDegree: '2' });
    expect(game.safReadDegree).toBe('2');

    // 再推 3 / 16 / auto → 逐个同步（自由数值词表：任意十进制数值字符串合法）
    handler!({ type: 'config', safReadDegree: '3' });
    expect(game.safReadDegree).toBe('3');
    handler!({ type: 'config', safReadDegree: '16' });
    expect(game.safReadDegree).toBe('16');
    handler!({ type: 'config', safReadDegree: 'auto' });
    expect(game.safReadDegree).toBe('auto');
  });

  it('config 消息含非法 safReadDegree 值 → 忽略不写 store', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', safReadDegree: 'on' }); // 旧三态词表不是本旋钮词表
    expect(game.safReadDegree).toBe('auto'); // 保持原值
    handler!({ type: 'config', safReadDegree: 'abc' }); // 非数值非 auto
    expect(game.safReadDegree).toBe('auto'); // 保持原值
  });
});

describe('handleMauiMessage - config maxParallelErbFiles/dimLockPolicy (ADR-0026 06-K1/K2 诊断分组)', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubWindow(): void {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });
  }

  it('config 消息含 maxParallelErbFiles → 写入 store（诊断分段控件渲染依据）', async () => {
    stubWindow();

    await initAppState();

    const game = useGameStore();
    expect(game.maxParallelErbFiles).toBe('auto'); // 默认 auto（min(核数,8)，与现状逐位一致）

    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', maxParallelErbFiles: '16' });
    expect(game.maxParallelErbFiles).toBe('16');

    // 再推 4 / 8 / auto → 逐个同步
    handler!({ type: 'config', maxParallelErbFiles: '4' });
    expect(game.maxParallelErbFiles).toBe('4');
    handler!({ type: 'config', maxParallelErbFiles: '8' });
    expect(game.maxParallelErbFiles).toBe('8');
    handler!({ type: 'config', maxParallelErbFiles: 'auto' });
    expect(game.maxParallelErbFiles).toBe('auto');
  });

  it('config 消息含非法 maxParallelErbFiles 值 → 忽略不写 store', async () => {
    stubWindow();

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', maxParallelErbFiles: '2' }); // 闭集词表只有 auto/4/8/16
    expect(game.maxParallelErbFiles).toBe('auto'); // 保持原值
    handler!({ type: 'config', maxParallelErbFiles: '08' }); // 闭集精确匹配，前导零不算
    expect(game.maxParallelErbFiles).toBe('auto'); // 保持原值
  });

  it('config 消息含 dimLockPolicy → 写入 store（诊断分段控件渲染依据）', async () => {
    stubWindow();

    await initAppState();

    const game = useGameStore();
    expect(game.dimLockPolicy).toBe('auto'); // 默认 auto（恒加锁，与止血逐位一致）

    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', dimLockPolicy: 'nolock' });
    expect(game.dimLockPolicy).toBe('nolock');
    handler!({ type: 'config', dimLockPolicy: 'auto' });
    expect(game.dimLockPolicy).toBe('auto');
  });

  it('config 消息含非法 dimLockPolicy 值 → 忽略不写 store', async () => {
    stubWindow();

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;

    handler!({ type: 'config', dimLockPolicy: 'forceoff' }); // 其他旋钮词表不是本旋钮词表
    expect(game.dimLockPolicy).toBe('auto'); // 保持原值
    handler!({ type: 'config', dimLockPolicy: 'NOLOCK' }); // 词表精确匹配，大写不算
    expect(game.dimLockPolicy).toBe('auto'); // 保持原值
  });
});

// ---------- handleMauiMessage：agentLog 消息（A0 补充：app 内日志查看器）----------

describe('handleMauiMessage - agentLog (A0 日志查看器)', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('agentLog 消息 → 写入 store（设置页日志展示区渲染）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    expect(game.agentLogContent).toBe('');
    expect(game.agentLogTruncated).toBe(false);

    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    handler!({ type: 'agentLog', content: '17:20:00.123 [saf] Query ok ms=45', truncated: false });
    expect(game.agentLogContent).toBe('17:20:00.123 [saf] Query ok ms=45');
    expect(game.agentLogTruncated).toBe(false);
  });

  it('agentLog 消息 truncated=true → 写入截断标记', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    handler!({ type: 'agentLog', content: 'x'.repeat(10), truncated: true });
    expect(game.agentLogTruncated).toBe(true);
  });

  it('agentLog 消息缺 content 字段 → 置为空串', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const game = useGameStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    handler!({ type: 'agentLog' });
    expect(game.agentLogContent).toBe('');
    expect(game.agentLogTruncated).toBe(false);
  });
});

// ---------- handleMauiMessage：controlStatus（issue 05 MAUI 托管控制状态同步）----------

describe('handleMauiMessage - controlStatus (issue 05)', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('agent 持有控制权 → controller=agent + isSpectator=true（输入栏禁用 + 旁观横幅）', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const conn = useConnectionStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    handler!({
      type: 'controlStatus',
      controller: { kind: 'agent', leaseExpiresAt: '2026-08-15T10:00:00Z' },
      state: 'held',
    });

    expect(conn.controller?.kind).toBe('agent');
    expect(conn.isSpectator).toBe(true);
    expect(conn.canInput).toBe(false);
    expect(conn.controlState).toBe('held');
  });

  it('无控制权（空闲）→ controller=null + 可操作', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const conn = useConnectionStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    handler!({ type: 'controlStatus', controller: null, state: 'idle' });

    expect(conn.controller).toBeNull();
    expect(conn.canInput).toBe(true);
    expect(conn.controlState).toBe('idle');
  });

  it('用户释放/接管后 controller=user → 可操作且非旁观', async () => {
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
      chrome: { webview: { postMessage: vi.fn() } },
    });

    await initAppState();

    const conn = useConnectionStore();
    const handler = (window as any).__onMessage as ((msg: unknown) => void) | undefined;
    handler!({
      type: 'controlStatus',
      controller: { kind: 'user', leaseExpiresAt: null },
      state: 'held',
    });

    expect(conn.controller?.kind).toBe('user');
    expect(conn.isSpectator).toBe(false);
    expect(conn.canInput).toBe(true);
  });
});
