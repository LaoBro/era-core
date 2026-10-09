import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import {
  useGameStore,
  mapLoadGameErrorCode,
  readGameDirFromStorage,
  LoadGameError,
  type LoadGameErrorCode,
} from '../game';
import { useConnectionStore } from '../connection';

/**
 * useGameStore.loadGame / 错误码映射 / readGameDirFromStorage 单测——issue 05。
 *
 * 测试矩阵：
 * - mapLoadGameErrorCode（纯函数）：5 个 code 映射到中文文案
 * - readGameDirFromStorage（纯函数）：有值 / null / 空 / 异常 / 无 storage
 * - loadGame（mock fetch + mock connection store + stub localStorage）：
 *   - 成功：disconnect → POST /load-game 200 → connect；gameDir 更新 + 持久化
 *   - 路径级错误（400 DIR_NOT_FOUND）：loadGameError 设置 + 仍 connect 回旧
 *   - 加载级错误（500 LOAD_FAILED）：loadGameError 设置 + 仍 connect
 *   - 网络错误（fetch throw）：loadGameError LOAD_FAILED + 尝试 connect
 *   - 二次进入保护：reload 进行中拒绝
 *   - 空字符串 dir：MISSING_GAME_DIR
 *   - clearLoadGameError 清空
 */

/** 内存版 localStorage——vi.stubGlobal 注入。 */
function makeLocalStorage(): Storage {
  const map = new Map<string, string>();
  return {
    getItem: (k: string) => map.has(k) ? map.get(k)! : null,
    setItem: (k: string, v: string) => { map.set(k, v); },
    removeItem: (k: string) => { map.delete(k); },
    clear: () => { map.clear(); },
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    get length() { return map.size; },
  };
}
describe('mapLoadGameErrorCode 纯函数', () => {
  const cases: Array<[LoadGameErrorCode, string]> = [
    ['DIR_NOT_FOUND', '目录不存在'],
    ['MISSING_CSV', '缺少 csv 子目录'],
    ['MISSING_ERB', '缺少 erb 子目录'],
    ['LOAD_FAILED', '游戏加载失败'],
    ['MISSING_GAME_DIR', '请求格式错误'],
    ['INVALID_JSON', '请求格式错误'],
  ];

  for (const [code, expectedSubstring] of cases) {
    it(`${code} → 包含 "${expectedSubstring}"`, () => {
      const text = mapLoadGameErrorCode(code);
      expect(text).toContain(expectedSubstring);
    });
  }
});

describe('readGameDirFromStorage 纯函数', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  it('有值 → 返回值', () => {
    localStorage.setItem('app.gameDir', 'D:\\games\\mygame');
    expect(readGameDirFromStorage()).toBe('D:\\games\\mygame');
  });

  it('null / 未设置 → null', () => {
    expect(readGameDirFromStorage()).toBeNull();
  });

  it('空字符串 → null', () => {
    localStorage.setItem('app.gameDir', '');
    expect(readGameDirFromStorage()).toBeNull();
  });

  it('storage=null → null', () => {
    expect(readGameDirFromStorage(null)).toBeNull();
  });

  it('storage.getItem 抛错 → null（不抛出）', () => {
    const badStorage = {
      getItem: () => {
        throw new Error('not allowed');
      },
    } as unknown as Storage;
    expect(readGameDirFromStorage(badStorage)).toBeNull();
  });
});

describe('LoadGameError 类', () => {
  it('保留 code / message / httpStatus', () => {
    const err = new LoadGameError('DIR_NOT_FOUND', '目录不存在', 400);
    expect(err.code).toBe('DIR_NOT_FOUND');
    expect(err.message).toBe('目录不存在');
    expect(err.httpStatus).toBe(400);
    expect(err.name).toBe('LoadGameError');
    expect(err instanceof Error).toBe(true);
  });
});

// ---------- loadGame 集成测试 ----------
//
// loadGame 是跨 store 复合动作——需要 connection store 配合。
// 测试策略：
// - mock globalThis.fetch（拦截 POST /load-game 和 GET /state 等）
// - mock connection store 的 disconnect / connect / deriveHttpBase / serverUrl
//   通过 vi.spyOn 替换原型方法
// - 验证 loadGame 内部状态变化 + 调用顺序

describe('useGameStore.loadGame', () => {
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

  /** 配置 fetch mock 返回 /load-game 响应。 */
  function mockLoadGameResponse(status: number, body: unknown): void {
    fetchMock.mockResolvedValueOnce({
      status,
      json: async () => body,
      text: async () => JSON.stringify(body),
    } as Response);
  }

  it('成功：POST /load-game 200 → gameDir 更新 + 持久化 + reloadStatus idle', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    // connection store 真实方法会创建 WebSocket——mock 掉
    const disconnectSpy = vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    // serverUrl 默认 'ws://localhost:5173/ws'，deriveHttpBase 返 'http://localhost:5173'
    mockLoadGameResponse(200, { sessionId: 'abc', state: 'Idle', gameDir: 'D:/games/mygame' });

    await game.loadGame('D:/games/mygame');

    expect(game.gameDir).toBe('D:/games/mygame');
    expect(game.reloadStatus).toBe('idle');
    expect(game.loadGameError).toBeNull();
    expect(localStorage.getItem('app.gameDir')).toBe('D:/games/mygame');
    expect(disconnectSpy).toHaveBeenCalledOnce();
    expect(connectSpy).toHaveBeenCalledOnce();
    // fetch 调用参数校验
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5173/load-game',
      expect.objectContaining({
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ gameDir: 'D:/games/mygame' }),
      }),
    );
  });

  it('路径级错误 400 DIR_NOT_FOUND → loadGameError 设置 + 仍 connect 回旧', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    mockLoadGameResponse(400, {
      error: { code: 'DIR_NOT_FOUND', message: '目录不存在: /bad/path' },
    });

    await game.loadGame('/bad/path');

    expect(game.reloadStatus).toBe('idle');
    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('DIR_NOT_FOUND');
    expect(game.loadGameError!.message).toBe('目录不存在: /bad/path');
    expect(game.loadGameError!.httpStatus).toBe(400);
    // 路径级错误——gameDir 不更新
    expect(game.gameDir).toBeNull();
    // localStorage 也不写
    expect(localStorage.getItem('app.gameDir')).toBeNull();
    // 但仍调 connect（回旧 session）
    expect(connectSpy).toHaveBeenCalledOnce();
  });

  it('加载级错误 500 LOAD_FAILED → loadGameError 设置', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    mockLoadGameResponse(500, {
      error: { code: 'LOAD_FAILED', message: 'Preload.Load failed: ERB syntax error' },
    });

    await game.loadGame('D:/broken/game');

    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('LOAD_FAILED');
    expect(game.loadGameError!.httpStatus).toBe(500);
    expect(game.gameDir).toBeNull();
  });

  it('网络错误 fetch throw → LOAD_FAILED + httpStatus=0', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    fetchMock.mockRejectedValueOnce(new Error('Network error'));

    await game.loadGame('D:/any/game');

    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('LOAD_FAILED');
    expect(game.loadGameError!.httpStatus).toBe(0);
    expect(game.loadGameError!.message).toContain('Network error');
  });

  it('400 错误 body 无 error.code → 默认 LOAD_FAILED', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    mockLoadGameResponse(400, { unexpected: 'shape' });

    await game.loadGame('/some/path');

    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('LOAD_FAILED');
    expect(game.loadGameError!.httpStatus).toBe(400);
  });

  it('二次进入保护：reload 进行中拒绝', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // 第一个 loadGame 不立即 resolve（fetch 挂起）
    let resolveFirst!: (v: unknown) => void;
    fetchMock.mockReturnValueOnce(new Promise((r) => { resolveFirst = r; }));

    const p1 = game.loadGame('D:/game1');
    expect(game.reloadStatus).toBe('loading');

    // 第二个 loadGame 应被拒绝（直接 return）
    await game.loadGame('D:/game2');
    // fetch 只被调用一次（第二次被拒绝）
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // 释放第一个
    resolveFirst({ status: 200, json: async () => ({ sessionId: 'x', state: 'Idle', gameDir: 'D:/game1' }), text: async () => '' });
    await p1;

    expect(game.gameDir).toBe('D:/game1');
  });

  it('空字符串 dir → MISSING_GAME_DIR + 不调 fetch', async () => {
    const game = useGameStore();
    await game.loadGame('   ');
    expect(game.loadGameError).not.toBeNull();
    expect(game.loadGameError!.code).toBe('MISSING_GAME_DIR');
    expect(fetchMock).not.toHaveBeenCalled();
    expect(game.reloadStatus).toBe('idle');
  });

  it('MAUI 环境 → 不调 fetch / 不调 connect + 直接设 gameDir（spec ID11）', async () => {
    // 模拟 MAUI unpackaged 模式（Windows 虚拟主机映射）
    vi.stubGlobal('window', {
      location: { protocol: 'https:', hostname: 'app.local' },
    });
    const game = useGameStore();
    const conn = useConnectionStore();
    const disconnectSpy = vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    const connectSpy = vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    await game.loadGame('D:/any/game');

    // MAUI 模式下不应走 HTTP /load-game / disconnect / connect
    expect(fetchMock).not.toHaveBeenCalled();
    expect(disconnectSpy).not.toHaveBeenCalled();
    expect(connectSpy).not.toHaveBeenCalled();
    expect(game.loadGameError).toBeNull();
    expect(game.reloadStatus).toBe('idle');
    // 仅更新 gameDir（让 UI 不再显示选择器预填默认值）
    expect(game.gameDir).toBe('D:/any/game');
  });

  it('dir 自动 trim——前后空格被去除', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    mockLoadGameResponse(200, { sessionId: 'x', state: 'Idle', gameDir: 'D:/trimmed' });

    await game.loadGame('  D:/trimmed  ');

    expect(game.gameDir).toBe('D:/trimmed');
    expect(localStorage.getItem('app.gameDir')).toBe('D:/trimmed');
    // fetch body 也是 trimmed
    const call = fetchMock.mock.calls[0];
    const body = JSON.parse((call[1] as RequestInit).body as string);
    expect(body.gameDir).toBe('D:/trimmed');
  });

  it('clearLoadGameError → 清空', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    mockLoadGameResponse(400, { error: { code: 'DIR_NOT_FOUND', message: 'not found' } });
    await game.loadGame('/bad');
    expect(game.loadGameError).not.toBeNull();

    game.clearLoadGameError();
    expect(game.loadGameError).toBeNull();
  });

  it('loadGame 前调 reset 清空显示状态', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // 先往 store 灌一些显示数据
    game.applyTurn(JSON.stringify({
      awaiting: { input: { type: 'AnyKey' } },
      generation: 0,
      diff: {
        lineOps: [{ type: 'append', newLines: [{ entries: [{ segments: [{ text: 'old data' }] }], isLineEnd: true }] }],
      },
    }));
    expect(game.displayState.lines).toHaveLength(1);

    mockLoadGameResponse(200, { sessionId: 'x', state: 'Idle', gameDir: 'D:/new' });
    await game.loadGame('D:/new');

    // 显示状态被清空——避免新游戏加载时画面闪烁旧帧
    expect(game.displayState.lines).toEqual([]);
  });
});
