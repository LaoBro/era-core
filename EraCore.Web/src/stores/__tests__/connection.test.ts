import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import {
  useConnectionStore,
  computeBackoffMs,
  MAX_RECONNECT_ATTEMPTS,
  INITIAL_BACKOFF_MS,
  MAX_BACKOFF_MS,
} from '../connection';

/**
 * useConnectionStore 单测——覆盖 issue 06 的 snapshot 恢复 + 自动重连 + 指数退避。
 *
 * Issue 06 关键变更（在 issue 03 已有的 session 前置 + WS 首帧 snapshot 补救基础上）：
 * 1. **onopen 立即拉 snapshot**——替换 issue 03 的"等首帧 diff=null 再决定"启发式。
 *    覆盖所有晚加入者场景：断线重连、多标签观察、调试工具中途连接、刷新页面
 * 2. **异常断开自动重连**——onclose code !== 1000 → status='reconnecting' → 调度 setTimeout
 * 3. **指数退避**：1s → 2s → 4s → 8s → 16s → 30s（封顶）
 * 4. **重试上限**：MAX_RECONNECT_ATTEMPTS=10 次后 reconnectFailed=true + status='disconnected'
 * 5. **手动重连**：retryConnect() 重置 retryCount + 立即 connect
 *
 * 测试矩阵：
 * - deriveHttpBase（纯函数）：ws:// / wss:// / 不同 host:port / 含路径
 * - ensureSession（mock fetch）：201 / 409 / 500+JSON / 500+text / fetch 异常
 * - fetchSnapshot（mock fetch）：200 / 503 重试 / 503 上限 / 404 / 字段校验 / 网络异常
 * - computeBackoffMs（纯函数）：attempt 0..10，封顶 30s
 * - connect 端到端（mock fetch + fake WebSocket + real timers + mock GET /snapshot）：
 *   - session 成功 → WS onopen → status=connected → 立即 GET /snapshot
 *   - session 创建失败 → status=disconnected（不自动重试）
 *   - WS 帧 diff 非空 → applyTurn 在 snapshot 之上叠加
 *   - refreshSnapshot 失败 → 写 closeReason，不改 status
 *   - disconnect() → status=disconnected，取消 pending 重连
 * - reconnect（mock fetch + fake WebSocket + fake timers）：
 *   - WS onclose 4004 → status=reconnecting + 调度 setTimeout
 *   - 推进时间 → 触发 connectInternal 重试
 *   - 重连成功 → onopen → retryCount 清零 + refreshSnapshot
 *   - 10 次失败后 → reconnectFailed=true + status=disconnected
 *   - retryConnect() → 重置 retryCount + 立即 connect
 *   - disconnect() 取消 pending 重连定时器
 */

// ---------- deriveHttpBase 纯函数测试 ----------

describe('useConnectionStore.deriveHttpBase', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('ws:// URL → http:// 同源', () => {
    const conn = useConnectionStore();
    expect(conn.deriveHttpBase('ws://localhost:5173/ws')).toBe('http://localhost:5173');
    expect(conn.deriveHttpBase('ws://localhost:8080/ws')).toBe('http://localhost:8080');
  });

  it('wss:// URL → https:// 同源', () => {
    const conn = useConnectionStore();
    expect(conn.deriveHttpBase('wss://example.com/ws')).toBe('https://example.com');
    expect(conn.deriveHttpBase('wss://secure.host:443/ws')).toBe('https://secure.host:443');
  });

  it('含路径的 URL：只取 host:port，丢弃路径', () => {
    const conn = useConnectionStore();
    expect(conn.deriveHttpBase('ws://localhost:5173/some/path')).toBe('http://localhost:5173');
  });
});

// ---------- ensureSession mock fetch 测试 ----------

describe('useConnectionStore.ensureSession', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => {
    fetchSpy.mockRestore();
  });

  it('201：会话已创建，resolve', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ sessionId: 'abc', createdAt: '2026-01-01T00:00:00Z', state: 'Idle' }), {
        status: 201,
      }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).resolves.toBeUndefined();
    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/session', { method: 'POST' });
  });

  it('409：已有活跃会话，视为成功 resolve', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'A session is already active' }), { status: 409 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).resolves.toBeUndefined();
  });

  it('500 + JSON error body：throw Error 含 detail', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Internal server error' }), { status: 500 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow(
      /POST \/session 失败：HTTP 500.*Internal server error/,
    );
  });

  it('500 + 非 JSON body：throw Error 含 text', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response('plain text error', { status: 500 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow(
      /POST \/session 失败：HTTP 500.*plain text error/,
    );
  });

  it('fetch 网络异常：throw 原始 Error', async () => {
    fetchSpy.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow('Failed to fetch');
  });

  it('4xx（非 409）：throw Error', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Bad request' }), { status: 400 }),
    );
    const conn = useConnectionStore();
    await expect(conn.ensureSession('http://localhost:5173')).rejects.toThrow(
      /HTTP 400.*Bad request/,
    );
  });
});

// ---------- fetchSnapshot mock fetch 测试 ----------

/**
 * 构造合法 DisplaySnapshot JSON body——只覆盖 fetchSnapshot 校验的必填字段。
 */
function snapshotJson(lines: unknown[] = [], opts: { state?: string; needValue?: boolean; protocolVersion?: number } = {}): string {
  return JSON.stringify({
    lines,
    state: opts.state ?? 'WaitInput',
    inputType: null,
    needValue: opts.needValue ?? false,
    generation: 0,
    protocolVersion: opts.protocolVersion ?? 6,
  });
}

describe('useConnectionStore.fetchSnapshot', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;
  let originalSetTimeout: typeof globalThis.setTimeout;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
    // setTimeout 替换为同步立即执行——503 重试测试不等真实 100ms
    originalSetTimeout = globalThis.setTimeout;
    globalThis.setTimeout = ((cb: () => void) => { cb(); return 0; }) as unknown as typeof globalThis.setTimeout;
  });

  afterEach(() => {
    fetchSpy.mockRestore();
    globalThis.setTimeout = originalSetTimeout;
  });

  it('200 + 合法 JSON：返回 DisplaySnapshot', async () => {
    const body = snapshotJson([{ entries: [{ segments: [{ text: 'Hello' }] }], isLineEnd: true }]);
    fetchSpy.mockResolvedValueOnce(new Response(body, { status: 200 }));

    const conn = useConnectionStore();
    const snapshot = await conn.fetchSnapshot('http://localhost:5173');
    expect(snapshot.lines).toHaveLength(1);
    expect(snapshot.state).toBe('WaitInput');
    expect(snapshot.protocolVersion).toBe(6);
    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/snapshot');
  });

  it('503 后 200：重试成功', async () => {
    fetchSpy.mockResolvedValueOnce(new Response('session not yet initialized', { status: 503 }));
    fetchSpy.mockResolvedValueOnce(new Response(snapshotJson(), { status: 200 }));

    const conn = useConnectionStore();
    const snapshot = await conn.fetchSnapshot('http://localhost:5173');
    expect(snapshot.state).toBe('WaitInput');
    expect(fetchSpy).toHaveBeenCalledTimes(2);
  });

  it('连续 503 直到上限：throw 含重试次数', async () => {
    for (let i = 0; i < 30; i++) {
      fetchSpy.mockResolvedValueOnce(new Response('session not initialized', { status: 503 }));
    }

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /GET \/snapshot 重试 30 次仍 503/,
    );
  });

  it('404（无 session）：立即抛错不重试', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(JSON.stringify({ error: 'No active session' }), { status: 404 }));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /GET \/snapshot 失败：HTTP 404.*No active session/,
    );
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });

  it('200 但 lines 非数组：抛解析失败', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(JSON.stringify({ state: 'WaitInput', needValue: false, lines: 'oops' }), { status: 200 }));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /snapshot\.lines 缺失或非数组/,
    );
  });

  it('200 但 state 非 string：抛解析失败', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(JSON.stringify({ state: 123, needValue: false, lines: [] }), { status: 200 }));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /snapshot\.state 非 string/,
    );
  });

  it('200 但 v12 快照缺 awaiting：抛解析失败（契约字段缺失即抛）', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(
      JSON.stringify({ lines: [], state: 'WaitInput', needValue: false, generation: 0, protocolVersion: 12 }),
      { status: 200 },
    ));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow(
      /snapshot\.awaiting 缺失/,
    );
  });

  it('200 但 v11 快照缺 awaiting：不抛错（兼容期允许降级）', async () => {
    fetchSpy.mockResolvedValueOnce(new Response(
      JSON.stringify({ lines: [], state: 'WaitInput', inputType: 'IntValue', needValue: true, generation: 0, protocolVersion: 11 }),
      { status: 200 },
    ));

    const conn = useConnectionStore();
    const snapshot = await conn.fetchSnapshot('http://localhost:5173');
    expect(snapshot.protocolVersion).toBe(11);
  });

  it('fetch 网络异常：throw 原始 Error', async () => {
    fetchSpy.mockRejectedValueOnce(new TypeError('Failed to fetch'));

    const conn = useConnectionStore();
    await expect(conn.fetchSnapshot('http://localhost:5173')).rejects.toThrow('Failed to fetch');
  });
});

// ---------- computeBackoffMs 纯函数测试 ----------

describe('computeBackoffMs', () => {
  it('attempt=0 → INITIAL_BACKOFF_MS（1s，首次重试）', () => {
    expect(computeBackoffMs(0)).toBe(INITIAL_BACKOFF_MS);
    expect(computeBackoffMs(0)).toBe(1000);
  });

  it('attempt=1 → 2s；attempt=2 → 4s；attempt=3 → 8s', () => {
    expect(computeBackoffMs(1)).toBe(2000);
    expect(computeBackoffMs(2)).toBe(4000);
    expect(computeBackoffMs(3)).toBe(8000);
  });

  it('attempt=4 → 16s（仍未封顶）', () => {
    expect(computeBackoffMs(4)).toBe(16000);
  });

  it('attempt=5 → 32s → 封顶 MAX_BACKOFF_MS（30s）', () => {
    expect(computeBackoffMs(5)).toBe(MAX_BACKOFF_MS);
    expect(computeBackoffMs(5)).toBe(30000);
  });

  it('attempt≥5：始终封顶 30s', () => {
    expect(computeBackoffMs(6)).toBe(30000);
    expect(computeBackoffMs(10)).toBe(30000);
    expect(computeBackoffMs(100)).toBe(30000);
  });
});

// ---------- connect 端到端流程测试（mock fetch + fake WebSocket + real timers） ----------
//
// 注意：此 describe block 用真实 timers + 默认 mock GET /snapshot（onopen 后立即拉）。
// 测试 scheduleReconnect 的 reconnect block 在下方独立使用 vi.useFakeTimers()。

/**
 * 最小化 fake WebSocket——只实现 connect 流程需要的属性/方法。
 *
 * Vitest 默认 node 环境无 WebSocket 全局，需 vi.stubGlobal 注入。
 * 每个 fake 实例暴露 open/close/error/message 触发器，由测试主动调用模拟事件。
 */
class FakeWebSocket {
  static lastInstance: FakeWebSocket | null = null;
  static instances: FakeWebSocket[] = [];

  url: string;
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void | Promise<void>) | null = null;
  onerror: (() => void) | null = null;
  onclose: ((event: { code: number; reason: string }) => void) | null = null;

  constructor(url: string) {
    this.url = url;
    FakeWebSocket.lastInstance = this;
    FakeWebSocket.instances.push(this);
  }

  close(code = 1000, reason = ''): void {
    if (this.onclose) this.onclose({ code, reason });
  }

  send(): void {
    // 测试中不验证发送内容
  }

  // 测试辅助：触发 onopen
  triggerOpen(): void {
    if (this.onopen) this.onopen();
  }
  triggerMessage(data: string): void {
    if (this.onmessage) this.onmessage({ data });
  }
  /**
   * 触发 onmessage 并返回其返回的 Promise——
   * 用于"竞态"测试场景：onmessage 是 async 函数，调用方需 await 才能等到内部 await 完成。
   */
  triggerMessageAsync(data: string): Promise<void> {
    if (this.onmessage) {
      const ret = this.onmessage({ data });
      return ret instanceof Promise ? ret : Promise.resolve();
    }
    return Promise.resolve();
  }
  triggerError(): void {
    if (this.onerror) this.onerror();
  }
  triggerClose(code: number, reason: string): void {
    if (this.onclose) this.onclose({ code, reason });
  }
}

/**
 * 默认 fetch mock——POST /session 返回 201，GET /snapshot 返回 200 空 snapshot。
 * 单个测试可用 mockResolvedValueOnce 覆盖特定调用（mockResolvedValueOnce 优先）。
 */
function defaultFetchImpl(): (url: string | URL | Request, init?: RequestInit) => Promise<Response> {
  return async (url, _init) => {
    const urlStr = typeof url === 'string' ? url : url.toString();
    if (urlStr.endsWith('/session')) {
      return new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 });
    }
    if (urlStr.endsWith('/snapshot')) {
      return new Response(snapshotJson(), { status: 200 });
    }
    if (urlStr.includes('/control/wait')) {
      return new Response(JSON.stringify({ error: 'No active session' }), { status: 404 });
    }
    if (urlStr.endsWith('/control')) {
      return new Response(JSON.stringify({ controller: null, state: 'idle' }), { status: 200 });
    }
    return new Response('not found', { status: 404 });
  };
}

function endControlWaitIfNeeded(urlStr: string): Response | null {
  if (urlStr.includes('/control/wait')) {
    return new Response(JSON.stringify({ error: 'No active session' }), { status: 404 });
  }
  if (urlStr.endsWith('/control')) {
    return new Response(JSON.stringify({ controller: null, state: 'idle' }), { status: 200 });
  }
  return null;
}

describe('useConnectionStore.connect — onopen 立即拉 snapshot', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;
  let originalWebSocket: typeof globalThis.WebSocket | undefined;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockImplementation(defaultFetchImpl());
    originalWebSocket = globalThis.WebSocket;
    FakeWebSocket.lastInstance = null;
    FakeWebSocket.instances = [];
    globalThis.WebSocket = FakeWebSocket as unknown as typeof globalThis.WebSocket;
  });

  afterEach(() => {
    fetchSpy.mockRestore();
    if (originalWebSocket === undefined) {
      delete (globalThis as { WebSocket?: unknown }).WebSocket;
    } else {
      globalThis.WebSocket = originalWebSocket;
    }
  });

  it('session 成功 → WS 升级 → onopen → status=connected + 立即 GET /snapshot', async () => {
    const conn = useConnectionStore();
    const connectPromise = conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('connecting');
    expect(conn.closeReason).toBeNull();

    await connectPromise;
    expect(FakeWebSocket.lastInstance).not.toBeNull();
    expect(FakeWebSocket.lastInstance!.url).toBe('ws://localhost:5173/ws');

    // 触发 onopen → status=connected + refreshSnapshot 调度
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    // issue 06：onopen 立即拉 snapshot——vi.waitFor 等 fire-and-forget 的 refreshSnapshot 完成
    await vi.waitFor(() => {
      expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/snapshot');
    });
  });

  it('session 创建失败（500）→ status=disconnected + closeReason，不创建 WS，不自动重试', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'Server down' }), { status: 500 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('disconnected');
    expect(conn.closeReason).toMatch(/POST \/session 失败：HTTP 500.*Server down/);
    expect(FakeWebSocket.lastInstance).toBeNull();
    // issue 06：用户主动 connect 失败不自动重试——retryCount 保持 0
    expect(conn.retryCount).toBe(0);
    expect(conn.reconnectFailed).toBe(false);
  });

  it('session 创建网络异常 → status=disconnected + closeReason', async () => {
    fetchSpy.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('disconnected');
    expect(conn.closeReason).toBe('Failed to fetch');
    expect(FakeWebSocket.lastInstance).toBeNull();
  });

  it('409（已有活跃会话）→ 视为成功 → 继续 WS 升级（多观察者场景）', async () => {
    fetchSpy.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'A session is already active' }), { status: 409 }),
    );
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');

    expect(conn.status).toBe('connecting');
    expect(FakeWebSocket.lastInstance).not.toBeNull();
  });

  it('WS onclose 1000（正常关闭）→ status=disconnected（不调度重连）', async () => {
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    FakeWebSocket.lastInstance!.triggerClose(1000, '');

    expect(conn.status).toBe('disconnected'); // 正常关闭不进 reconnecting
    expect(conn.retryCount).toBe(0);
    expect(conn.reconnectFailed).toBe(false);
  });

  it('WS 接收到 turn JSON（含 diff）→ game.applyTurn 在 snapshot 之上叠加', async () => {
    // GET /snapshot 返回带 1 行 "Snapshot" 的初始画面
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 });
      }
      if (urlStr.endsWith('/snapshot')) {
        return new Response(
          snapshotJson([{ entries: [{ segments: [{ text: 'Snapshot' }] }], isLineEnd: true }]),
          { status: 200 },
        );
      }
      return new Response('not found', { status: 404 });
    });

    const conn = useConnectionStore();
    const { useGameStore } = await import('../game');
    const game = useGameStore();

    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();

    // 等 refreshSnapshot 完成——snapshot 恢复 1 行 "Snapshot"
    await vi.waitFor(() => {
      expect(game.displayState.lines).toHaveLength(1);
    });
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Snapshot');

    // 模拟 server 推送一帧带 diff 的 turn——在 snapshot 之上叠加新行
    const turnJson = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      generation: 0,
      protocolVersion: 6,
      diff: {
        lineOps: [
          { type: 'append', newLines: [{ entries: [{ segments: [{ text: 'Hello' }] }], isLineEnd: true }] },
        ],
      },
    });
    FakeWebSocket.lastInstance!.triggerMessage(turnJson);

    expect(game.lastTurnJson).toBe(turnJson);
    expect(game.displayState.lines).toHaveLength(2);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Snapshot');
    expect(game.displayState.lines[1].entries[0].segments[0].text).toBe('Hello');
    expect(game.protocolVersion).toBe(6);
    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/snapshot');
    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/control');
  });

  it('竞态：WS delta 帧在 snapshot resolve 之前到达 → await snapshot 后再 applyTurn（保证顺序）', async () => {
    // 用 deferred Promise 控制 GET /snapshot 的 resolve 时机
    let resolveSnapshot: (resp: Response) => void = () => {};
    const snapshotDeferred = new Promise<Response>((resolve) => {
      resolveSnapshot = resolve;
    });
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 });
      }
      if (urlStr.endsWith('/snapshot')) {
        return snapshotDeferred;
      }
      return new Response('not found', { status: 404 });
    });

    const conn = useConnectionStore();
    const { useGameStore } = await import('../game');
    const game = useGameStore();

    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    // snapshot 还 pending——此时 WS delta 帧到达
    // 预期：onmessage await pendingSnapshotPromise，不会立即 applyTurn
    const deltaJson = JSON.stringify({
      state: 'WaitInput',
      needValue: false,
      generation: 0,
      protocolVersion: 6,
      diff: {
        lineOps: [
          { type: 'append', newLines: [{ entries: [{ segments: [{ text: 'Delta' }] }], isLineEnd: true }] },
        ],
      },
    });
    const messagePromise = FakeWebSocket.lastInstance!.triggerMessageAsync(deltaJson);
    // 让 microtask 跑几步——onmessage 应已进入 await pendingSnapshotPromise
    await Promise.resolve();
    await Promise.resolve();
    // snapshot 未 resolve → applyTurn 未调用
    expect(game.lastTurnJson).toBeNull();

    // 现在 resolve snapshot——onmessage 应被唤醒并调用 applyTurn
    resolveSnapshot(
      new Response(
        snapshotJson([{ entries: [{ segments: [{ text: 'Snapshot' }] }], isLineEnd: true }]),
        { status: 200 },
      ),
    );
    await messagePromise; // 等 onmessage 完成

    expect(game.lastTurnJson).toBe(deltaJson);
    // snapshot 先应用（1 行 "Snapshot"），delta 在其上叠加（"Delta"）——顺序正确
    expect(game.displayState.lines).toHaveLength(2);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Snapshot');
    expect(game.displayState.lines[1].entries[0].segments[0].text).toBe('Delta');
  });

  it('refreshSnapshot 失败 → 写 closeReason，不改 status（WS 仍 connected）', async () => {
    // GET /snapshot 返回 500
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 });
      }
      if (urlStr.endsWith('/snapshot')) {
        return new Response(JSON.stringify({ error: 'Snapshot boom' }), { status: 500 });
      }
      return new Response('not found', { status: 404 });
    });

    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');
    expect(conn.closeReason).toBeNull();

    // vi.waitFor 等 fire-and-forget 的 catch 块写完 closeReason
    await vi.waitFor(() => {
      expect(conn.closeReason).toMatch(/GET \/snapshot 失败：HTTP 500.*Snapshot boom/);
    });
    expect(conn.status).toBe('connected'); // 仍连接——snapshot 失败不阻断 WS
  });

  it('disconnect() 主动断开 → status=disconnected + retryCount 清零', async () => {
    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');

    conn.disconnect();
    expect(conn.status).toBe('disconnected');
    expect(conn.retryCount).toBe(0);
    expect(conn.reconnectFailed).toBe(false);
  });

  it('首帧 turn diff=null + snapshot 拿到空 lines → onmessage fallback 再次拉 snapshot', async () => {
    // 模拟 C# 真实场景：POST /session 后 _displayState 已构造但游戏循环还没产出 PRINT。
    // GET /snapshot 首次返回 lines=[]，首帧 turn 的 diff 必为 null（DisplayState.ComputeDiff 第一次返回 null）。
    // 期望：onmessage fallback 再次触发 refreshSnapshot，拿到游戏已产出画面。
    let snapshotCallCount = 0;
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 });
      }
      if (urlStr.endsWith('/snapshot')) {
        snapshotCallCount++;
        if (snapshotCallCount === 1) {
          // 第一次：游戏循环还没产出任何 PRINT——空 lines
          return new Response(snapshotJson([], { state: 'WaitInput' }), { status: 200 });
        }
        // 第二次（fallback 触发）：游戏已产出 1 行 "Title"
        return new Response(
          snapshotJson([{ entries: [{ segments: [{ text: 'Title' }] }], isLineEnd: true }]),
          { status: 200 },
        );
      }
      return new Response('not found', { status: 404 });
    });

    const conn = useConnectionStore();
    const { useGameStore } = await import('../game');
    const game = useGameStore();

    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();

    // 等 onopen 的 refreshSnapshot 完成——snapshot 拿到空 lines
    await vi.waitFor(() => {
      expect(snapshotCallCount).toBeGreaterThanOrEqual(1);
    });
    expect(game.displayState.lines).toHaveLength(0);

    // 模拟 C# 推送首帧 turn（diff 必为 null——ComputeDiff 第一次返回 null）
    const firstTurnJson = JSON.stringify({
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      generation: 0,
      protocolVersion: 6,
      // 注意：没有 diff 字段
    });
    FakeWebSocket.lastInstance!.triggerMessage(firstTurnJson);

    // onmessage 应触发 fallback refreshSnapshot——拿到 1 行 "Title"
    await vi.waitFor(() => {
      expect(snapshotCallCount).toBeGreaterThanOrEqual(2);
      expect(game.displayState.lines).toHaveLength(1);
    });
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Title');
    expect(game.lastTurnJson).toBe(firstTurnJson);
  });
});

// ---------- reconnect 自动重连测试（vi.useFakeTimers + fake WebSocket） ----------
//
// fake timers 让我们能精确控制 setTimeout 推进——验证指数退避时序。
// mock fetch 在 reconnect 路径上：POST /session 失败 → scheduleReconnect 重试。

describe('useConnectionStore — issue 06 自动重连', () => {
  let fetchSpy: ReturnType<typeof vi.spyOn>;
  let originalWebSocket: typeof globalThis.WebSocket | undefined;

  beforeEach(() => {
    setActivePinia(createPinia());
    fetchSpy = vi.spyOn(globalThis, 'fetch');
    fetchSpy.mockImplementation(defaultFetchImpl());
    originalWebSocket = globalThis.WebSocket;
    FakeWebSocket.lastInstance = null;
    FakeWebSocket.instances = [];
    globalThis.WebSocket = FakeWebSocket as unknown as typeof globalThis.WebSocket;
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    fetchSpy.mockRestore();
    if (originalWebSocket === undefined) {
      delete (globalThis as { WebSocket?: unknown }).WebSocket;
    } else {
      globalThis.WebSocket = originalWebSocket;
    }
  });

  /**
   * 辅助：建立一次成功连接（走完 connect → onopen → refreshSnapshot 流程）。
   * 返回 fake WS 实例，调用方可触发 onclose 模拟断线。
   */
  async function establishConnection(url = 'ws://localhost:5173/ws'): Promise<FakeWebSocket> {
    const conn = useConnectionStore();
    await conn.connect(url);
    const socket = FakeWebSocket.lastInstance!;
    socket.triggerOpen();
    // 推进 microtask 让 refreshSnapshot 完成
    await vi.advanceTimersByTimeAsync(0);
    return socket;
  }

  // ---------- onclose 异常 → scheduleReconnect ----------

  it('WS onclose 4004 → status=reconnecting + closeReason + 调度 setTimeout(1s)', async () => {
    const socket = await establishConnection();
    const conn = useConnectionStore();
    expect(conn.status).toBe('connected');

    // 异常断开——scheduleReconnect 触发，retryCount 0 → 1，backoff 1s
    socket.triggerClose(4004, 'No active session');

    expect(conn.status).toBe('reconnecting');
    expect(conn.closeReason).toBe('No active session');
    expect(conn.retryCount).toBe(1);
    expect(conn.reconnectFailed).toBe(false);
    // setTimeout 已调度——vi.advanceTimersByTime(1000) 才会触发 connectInternal
  });

  it('推进 1s → 触发 connectInternal 重试（POST /session + WS 升级）', async () => {
    const socket = await establishConnection();
    const conn = useConnectionStore();
    socket.triggerClose(4004, 'No active session');
    expect(conn.retryCount).toBe(1);

    // 推进 1s → connectInternal 触发 → POST /session 调用一次
    FakeWebSocket.instances.pop(); // 清空上一个 socket 引用，便于断言新实例
    FakeWebSocket.lastInstance = null;
    await vi.advanceTimersByTimeAsync(1000);

    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/session', { method: 'POST' });
    expect(FakeWebSocket.lastInstance).not.toBeNull();
    expect(conn.status).toBe('reconnecting'); // WS 还没 onopen
  });

  it('重连成功 → onopen → status=connected + retryCount 清零 + refreshSnapshot', async () => {
    const socket = await establishConnection();
    const conn = useConnectionStore();
    socket.triggerClose(4004, 'No active session');
    expect(conn.retryCount).toBe(1);

    FakeWebSocket.instances.pop();
    FakeWebSocket.lastInstance = null;
    await vi.advanceTimersByTimeAsync(1000);
    expect(conn.status).toBe('reconnecting'); // 等 onopen

    // 触发 onopen → 成功重连
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');
    expect(conn.retryCount).toBe(0); // 重置退避
    expect(conn.reconnectFailed).toBe(false);

    // onopen 立即拉 snapshot
    await vi.advanceTimersByTimeAsync(0);
    expect(fetchSpy).toHaveBeenCalledWith('http://localhost:5173/snapshot');
  });

  // ---------- 指数退避 ----------

  it('指数退避：第 1 次 1s，第 2 次 2s，第 3 次 4s', async () => {
    // 模拟 POST /session 在 reconnect 路径上始终失败——验证退避间隔
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        // 重连路径 POST /session 失败 → scheduleReconnect
        return new Response(JSON.stringify({ error: 'still down' }), { status: 500 });
      }
      const control = endControlWaitIfNeeded(urlStr);
      if (control) return control;
      return new Response(snapshotJson(), { status: 200 });
    });

    const conn = useConnectionStore();
    // 首次连接成功——建立"已连过"状态
    // 但 fetchSpy 默认对 /session 返回 500，我们临时切换回成功
    fetchSpy.mockImplementationOnce(async () =>
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    fetchSpy.mockImplementationOnce(async () => new Response(snapshotJson(), { status: 200 }));

    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    await vi.advanceTimersByTimeAsync(0);
    expect(conn.status).toBe('connected');

    // 切换到失败模式（覆盖 defaultFetchImpl + 上面的 mockImplementationOnce 已消耗）
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ error: 'still down' }), { status: 500 });
      }
      const control = endControlWaitIfNeeded(urlStr);
      if (control) return control;
      return new Response(snapshotJson(), { status: 200 });
    });

    // 异常断开 → 第 1 次重试调度 1s
    FakeWebSocket.lastInstance!.triggerClose(4006, 'abnormal');
    expect(conn.retryCount).toBe(1);
    expect(conn.status).toBe('reconnecting');

    // 推进 999ms——还没触发
    await vi.advanceTimersByTimeAsync(999);
    expect(conn.retryCount).toBe(1);

    // 推进 1ms（共 1000ms）——第 1 次重试触发，POST /session 失败 → 调度第 2 次（2s）
    await vi.advanceTimersByTimeAsync(1);
    expect(conn.retryCount).toBe(2);
    expect(conn.status).toBe('reconnecting');

    // 推进 1999ms——还没触发第 2 次
    await vi.advanceTimersByTimeAsync(1999);
    expect(conn.retryCount).toBe(2);

    // 推进 1ms（共 2000ms）——第 2 次重试触发，POST /session 失败 → 调度第 3 次（4s）
    await vi.advanceTimersByTimeAsync(1);
    expect(conn.retryCount).toBe(3);

    // 推进 3999ms——还没触发第 3 次
    await vi.advanceTimersByTimeAsync(3999);
    expect(conn.retryCount).toBe(3);

    // 推进 1ms（共 4000ms）——第 3 次重试触发
    await vi.advanceTimersByTimeAsync(1);
    expect(conn.retryCount).toBe(4);
  });

  // ---------- 重试上限 ----------

  it('10 次重试失败 → reconnectFailed=true + status=disconnected', async () => {
    // POST /session 始终失败——初始 connect 也走 reconnect 路径
    // 但初始 connect（isReconnect=false）失败不会自动重试，会 status=disconnected。
    // 这里需要先建立连接，再断开后让 reconnect 路径跑 10 次。
    fetchSpy.mockImplementationOnce(async () =>
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    fetchSpy.mockImplementationOnce(async () => new Response(snapshotJson(), { status: 200 }));
    // 之后的 POST /session 全部失败
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ error: 'server down' }), { status: 500 });
      }
      const control = endControlWaitIfNeeded(urlStr);
      if (control) return control;
      return new Response(snapshotJson(), { status: 200 });
    });

    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    await vi.advanceTimersByTimeAsync(0);
    expect(conn.status).toBe('connected');

    // 异常断开 → 触发 10 次重试
    FakeWebSocket.lastInstance!.triggerClose(4006, 'abnormal');
    expect(conn.retryCount).toBe(1);

    // 依次推进时间触发每次重试：1+2+4+8+16+30+30+30+30+30 = 181s
    // 每次重试 POST /session 失败 → scheduleReconnect → retryCount++
    // 推进足够时间让所有定时器跑完
    await vi.advanceTimersByTimeAsync(200_000);

    expect(conn.retryCount).toBe(MAX_RECONNECT_ATTEMPTS);
    expect(conn.reconnectFailed).toBe(true);
    expect(conn.status).toBe('disconnected');
    expect(conn.closeReason).toMatch(/重连 10 次仍失败/);
  });

  // ---------- 手动重连 ----------

  it('retryConnect() 重置 retryCount + reconnectFailed，立即 connect', async () => {
    // 触发重连耗尽场景
    fetchSpy.mockImplementationOnce(async () =>
      new Response(JSON.stringify({ sessionId: 'abc', state: 'Idle' }), { status: 201 }),
    );
    fetchSpy.mockImplementationOnce(async () => new Response(snapshotJson(), { status: 200 }));
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ error: 'server down' }), { status: 500 });
      }
      const control = endControlWaitIfNeeded(urlStr);
      if (control) return control;
      return new Response(snapshotJson(), { status: 200 });
    });

    const conn = useConnectionStore();
    await conn.connect('ws://localhost:5173/ws');
    FakeWebSocket.lastInstance!.triggerOpen();
    await vi.advanceTimersByTimeAsync(0);
    expect(conn.status).toBe('connected');

    // 异常断开 → 10 次失败重试
    FakeWebSocket.lastInstance!.triggerClose(4006, 'abnormal');
    await vi.advanceTimersByTimeAsync(200_000);
    expect(conn.reconnectFailed).toBe(true);
    expect(conn.status).toBe('disconnected');
    expect(conn.retryCount).toBe(MAX_RECONNECT_ATTEMPTS);

    // 用户点击"重新连接"——重置 retryCount，立即尝试连接
    // 切换 fetch 到成功模式
    fetchSpy.mockImplementation(defaultFetchImpl());

    FakeWebSocket.instances.pop();
    FakeWebSocket.lastInstance = null;
    await conn.retryConnect();

    expect(conn.retryCount).toBe(0);
    expect(conn.reconnectFailed).toBe(false);
    expect(conn.status).toBe('connecting');

    // WS 升级成功
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');
    expect(conn.retryCount).toBe(0); // onopen 重置
  });

  it('reconnecting 期间用户点击"立即重连" → 跳过退避立即重试', async () => {
    const socket = await establishConnection();
    const conn = useConnectionStore();
    socket.triggerClose(4004, 'No active session');
    expect(conn.status).toBe('reconnecting');
    expect(conn.retryCount).toBe(1);

    // 切换 fetch 让重连成功
    fetchSpy.mockImplementation(defaultFetchImpl());

    FakeWebSocket.instances.pop();
    FakeWebSocket.lastInstance = null;
    // 用户主动 retryConnect——跳过 1s 等待
    await conn.retryConnect();

    expect(conn.retryCount).toBe(0); // retryConnect 重置
    expect(conn.status).toBe('connecting');

    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');
  });

  // ---------- disconnect 取消 pending 重连 ----------

  it('disconnect() 期间有 pending 重连定时器 → 取消定时器 + 抑制后续重连', async () => {
    const socket = await establishConnection();
    const conn = useConnectionStore();
    socket.triggerClose(4004, 'No active session');
    expect(conn.status).toBe('reconnecting');
    expect(conn.retryCount).toBe(1);

    // 用户主动 disconnect——应取消 pending 重连
    conn.disconnect();
    expect(conn.status).toBe('disconnected');
    expect(conn.retryCount).toBe(0);
    expect(conn.reconnectFailed).toBe(false);

    // 推进时间——不应触发任何 connectInternal
    const fetchCallCountBefore = fetchSpy.mock.calls.length;
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetchSpy.mock.calls.length).toBe(fetchCallCountBefore); // 没有新的 fetch 调用
  });

  // ---------- POST /session 失败 during reconnect → 继续重试 ----------

  it('reconnect 路径 POST /session 失败 → 继续调度下一次重连（不放弃）', async () => {
    const socket = await establishConnection();
    const conn = useConnectionStore();
    expect(conn.status).toBe('connected');

    // 切换 fetch 到失败模式
    fetchSpy.mockImplementation(async (url: Parameters<typeof fetch>[0]) => {
      const urlStr = typeof url === 'string' ? url : url.toString();
      if (urlStr.endsWith('/session')) {
        return new Response(JSON.stringify({ error: 'temporary failure' }), { status: 503 });
      }
      const control = endControlWaitIfNeeded(urlStr);
      if (control) return control;
      return new Response(snapshotJson(), { status: 200 });
    });

    // 异常断开 → 第 1 次重试调度 1s
    socket.triggerClose(4006, 'abnormal');
    expect(conn.retryCount).toBe(1);

    // 推进 1s → connectInternal 触发 → POST /session 失败（503）→ scheduleReconnect
    // 注意：connectInternal 内部的 ensureSession 抛错被 catch → scheduleReconnect
    // scheduleReconnect 会 retryCount++ → 2，调度 setTimeout(2s)
    await vi.advanceTimersByTimeAsync(1000);
    expect(conn.retryCount).toBe(2);
    expect(conn.status).toBe('reconnecting');

    // 推进 2s → 第 2 次重试 → 又失败 → 调度第 3 次
    await vi.advanceTimersByTimeAsync(2000);
    expect(conn.retryCount).toBe(3);
  });

  // ---------- 重连成功后再次断线 → 退避从 1s 重新开始 ----------

  it('重连成功后再次断线 → retryCount 已重置，退避从 1s 重新开始', async () => {
    // 第 1 次连接 + 断线 + 重连成功（已建立场景）
    let socket = await establishConnection();
    const conn = useConnectionStore();
    socket.triggerClose(4006, 'first disconnect');
    expect(conn.retryCount).toBe(1);

    FakeWebSocket.instances.pop();
    FakeWebSocket.lastInstance = null;
    await vi.advanceTimersByTimeAsync(1000);
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.status).toBe('connected');
    expect(conn.retryCount).toBe(0); // onopen 重置

    // 第 2 次断线——退避应从 1s 重新开始
    socket = FakeWebSocket.lastInstance!;
    socket.triggerClose(4006, 'second disconnect');
    expect(conn.retryCount).toBe(1); // 又从 1 开始

    // 推进 1s → 第 1 次重试
    FakeWebSocket.instances.pop();
    FakeWebSocket.lastInstance = null;
    await vi.advanceTimersByTimeAsync(1000);
    expect(conn.retryCount).toBe(1); // 还没递增——POST /session 还没失败
    FakeWebSocket.lastInstance!.triggerOpen();
    expect(conn.retryCount).toBe(0); // onopen 重置
  });
});
