import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';
import { useConnectionStore } from '../connection';

/**
 * T-025 D14：useGameStore.quickRestart 单测——快速重开同目录。
 *
 * 测试矩阵：
 * - 成功：GET /state → DELETE /session → POST /load-game 200 → connect；gameDir 更新 + 持久化
 * - load-game 400 失败：清空 gameDir + localStorage，回退到空路径选择器
 * - load-game 500 失败：同上
 * - GET /state 网络错误：清空 gameDir + localStorage，回退到空路径选择器
 * - GET /state gameDir=null（无活跃游戏）：清空 gameDir，回退到空路径选择器
 * - 二次进入保护：reload 进行中拒绝
 * - serverState 在成功/失败时正确更新
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

describe('useGameStore.quickRestart', () => {
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

  /**
   * 配置 fetch mock 序列——按调用顺序返回指定响应。
   * quickRestart 的 fetch 调用顺序：GET /state → DELETE /session → POST /load-game → [GET /state (layout)]
   */
  function mockFetchSequence(responses: Array<{ status: number; body: unknown }>): void {
    for (const resp of responses) {
      fetchMock.mockResolvedValueOnce({
        status: resp.status,
        json: async () => resp.body,
        text: async () => JSON.stringify(resp.body),
      } as Response);
    }
  }

  it('成功：GET /state → DELETE → POST /load-game 200 → connect；gameDir 更新 + 持久化', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // GET /state (gameDir) → DELETE /session → POST /load-game 200 → GET /state (layout)
    mockFetchSequence([
      { status: 200, body: { state: 'WaitInput', gameDir: 'D:/games/mygame' } },
      { status: 200, body: { removed: true } },
      { status: 200, body: { sessionId: 'abc', state: 'Loading', gameDir: 'D:/games/mygame' } },
      { status: 200, body: { state: 'Loading', gameDir: 'D:/games/mygame', windowWidth: 760, fontSize: 18, lineHeight: 19, gameColumns: 84, fontName: 'ＭＳ ゴシック' } },
    ]);

    await game.quickRestart();

    expect(game.gameDir).toBe('D:/games/mygame');
    expect(game.reloadStatus).toBe('idle');
    expect(game.loadGameError).toBeNull();
    expect(localStorage.getItem('app.gameDir')).toBe('D:/games/mygame');
    expect(connectSpy).toHaveBeenCalledOnce();
    // serverState 应从 GET /state 或 load-game 响应更新（非 Idle）
    expect(game.serverState).not.toBe('Idle');
  });

  it('load-game 400 失败 → 清空 gameDir + localStorage + serverState=Idle', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // 预设 localStorage 有值
    localStorage.setItem('app.gameDir', 'D:/old/game');
    game.gameDir = 'D:/old/game';

    // GET /state → DELETE → POST /load-game 400
    mockFetchSequence([
      { status: 200, body: { state: 'WaitInput', gameDir: 'D:/old/game' } },
      { status: 200, body: { removed: true } },
      { status: 400, body: { error: { code: 'DIR_NOT_FOUND', message: '目录不存在' } } },
    ]);

    await game.quickRestart();

    expect(game.reloadStatus).toBe('idle');
    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('DIR_NOT_FOUND');
    // D14：失败时清空 gameDir + localStorage
    expect(game.gameDir).toBeNull();
    expect(localStorage.getItem('app.gameDir')).toBeNull();
    expect(game.serverState).toBe('Idle');
  });

  it('load-game 500 失败 → 清空 gameDir + localStorage', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    mockFetchSequence([
      { status: 200, body: { state: 'WaitInput', gameDir: 'D:/broken/game' } },
      { status: 200, body: { removed: true } },
      { status: 500, body: { error: { code: 'LOAD_FAILED', message: 'ERB syntax error' } } },
    ]);

    await game.quickRestart();

    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('LOAD_FAILED');
    expect(game.gameDir).toBeNull();
    expect(localStorage.getItem('app.gameDir')).toBeNull();
    expect(game.serverState).toBe('Idle');
  });

  it('GET /state 网络错误 → 清空 gameDir + localStorage + serverState=Idle', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    localStorage.setItem('app.gameDir', 'D:/old/game');
    game.gameDir = 'D:/old/game';

    // 第一次 fetch（GET /state）就抛错
    fetchMock.mockRejectedValueOnce(new Error('Network error'));

    await game.quickRestart();

    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('LOAD_FAILED');
    expect(game.gameDir).toBeNull();
    expect(localStorage.getItem('app.gameDir')).toBeNull();
    expect(game.serverState).toBe('Idle');
  });

  it('GET /state gameDir=null（无活跃游戏）→ 清空 gameDir，不调 load-game', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    localStorage.setItem('app.gameDir', 'D:/old/game');
    game.gameDir = 'D:/old/game';

    // GET /state 返回 idle（gameDir=null）
    mockFetchSequence([
      { status: 200, body: { state: 'Idle', gameDir: null } },
    ]);

    await game.quickRestart();

    // 无活跃游戏 → 清空 gameDir，不调 load-game / DELETE / connect
    expect(game.gameDir).toBeNull();
    expect(localStorage.getItem('app.gameDir')).toBeNull();
    expect(game.serverState).toBe('Idle');
    expect(game.loadGameError).toBeNull();
    // fetch 只被调用一次（GET /state），没有 DELETE / load-game
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(connectSpy).not.toHaveBeenCalled();
  });

  it('二次进入保护：reload 进行中拒绝', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // 第一个 quickRestart 的 GET /state 不立即 resolve
    let resolveFirst!: (v: unknown) => void;
    fetchMock.mockReturnValueOnce(new Promise((r) => { resolveFirst = r; }));

    const p1 = game.quickRestart();
    expect(game.reloadStatus).toBe('loading');

    // 第二个 quickRestart 应被拒绝
    await game.quickRestart();
    // fetch 只被调用一次（第二个被拒绝）
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // 释放第一个——返 idle state（gameDir=null），quickRestart 清空 gameDir
    resolveFirst({
      status: 200,
      json: async () => ({ state: 'Idle', gameDir: null }),
      text: async () => '{}',
    });
    await p1;

    expect(game.reloadStatus).toBe('idle');
  });

  it('reset 时 serverState 回 Idle', () => {
    const game = useGameStore();
    // 模拟 WS 帧推送更新 serverState（v12：由 awaiting 派生）
    game.applyTurn(
      JSON.stringify({
        awaiting: { input: { type: 'AnyKey' } },
        generation: 0,
        diff: null,
      }),
    );
    expect(game.serverState).toBe('WaitInput');

    game.reset();
    expect(game.serverState).toBe('Idle');
  });

  it('applyTurn 由 awaiting 派生 serverState（非终态 → WaitInput；终态 → terminal）', () => {
    const game = useGameStore();
    expect(game.serverState).toBe('Idle');

    game.applyTurn(
      JSON.stringify({
        awaiting: { wait: { durationMs: 0 } },
        generation: 0,
        diff: null,
      }),
    );
    expect(game.serverState).toBe('WaitInput');

    game.applyTurn(
      JSON.stringify({
        awaiting: { ended: { terminal: 'Quit' } },
        generation: 0,
        diff: null,
      }),
    );
    expect(game.serverState).toBe('Quit');
  });
});
