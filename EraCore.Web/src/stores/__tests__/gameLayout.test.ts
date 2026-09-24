import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';
import { useConnectionStore } from '../connection';

/**
 * useGameStore 窗口布局元信息单测——issue 12。
 *
 * 测试矩阵：
 * - 初始状态：windowWidth/fontSize/lineHeight/gameColumns/fontName 均为 null
 * - setGameLayout：写入五个字段（同时 / 单独）
 * - setGameLayout：null / undefined 不覆盖已有值
 * - loadGame 成功后：重读 GET /state 并更新 layout 字段
 * - loadGame 网络错误：layout 字段保持原值（不更新）
 */

describe('useGameStore - Issue 12 窗口布局元信息', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('初始状态：windowWidth/fontSize/lineHeight/gameColumns/fontName/defaultTextColor 均为 null', () => {
    const game = useGameStore();
    expect(game.windowWidth).toBeNull();
    expect(game.fontSize).toBeNull();
    expect(game.lineHeight).toBeNull();
    expect(game.gameColumns).toBeNull();
    expect(game.fontName).toBeNull();
    expect(game.defaultTextColor).toBeNull();
  });

  it('setGameLayout：同时写入各字段（含默认文字色 foreColor）', () => {
    const game = useGameStore();
    game.setGameLayout({
      windowWidth: 1000,
      fontSize: 20,
      lineHeight: 22,
      gameColumns: 124,
      fontName: 'ＭＳ ゴシック',
      foreColor: '#C0C0C0',
    });
    expect(game.windowWidth).toBe(1000);
    expect(game.fontSize).toBe(20);
    expect(game.lineHeight).toBe(22);
    expect(game.gameColumns).toBe(124);
    expect(game.fontName).toBe('ＭＳ ゴシック');
    expect(game.defaultTextColor).toBe('#C0C0C0');
  });

  it('setGameLayout：单独写入一个字段不影响其他字段', () => {
    const game = useGameStore();
    game.setGameLayout({
      windowWidth: 760,
      fontSize: 18,
      lineHeight: 19,
      gameColumns: 84,
      fontName: 'ＭＳ ゴシック',
    });

    game.setGameLayout({ windowWidth: 1000 });
    expect(game.windowWidth).toBe(1000);
    expect(game.fontSize).toBe(18);
    expect(game.lineHeight).toBe(19);
    expect(game.gameColumns).toBe(84);
    expect(game.fontName).toBe('ＭＳ ゴシック');
  });

  it('setGameLayout：null / undefined 不覆盖已有值', () => {
    const game = useGameStore();
    game.setGameLayout({
      windowWidth: 1000,
      fontSize: 20,
      lineHeight: 22,
      gameColumns: 124,
      fontName: 'ＭＳ ゴシック',
      foreColor: '#C0C0C0',
    });

    // null 不覆盖
    game.setGameLayout({
      windowWidth: null,
      fontSize: null,
      lineHeight: null,
      gameColumns: null,
      fontName: null,
      foreColor: null,
    });
    expect(game.windowWidth).toBe(1000);
    expect(game.fontSize).toBe(20);
    expect(game.lineHeight).toBe(22);
    expect(game.gameColumns).toBe(124);
    expect(game.fontName).toBe('ＭＳ ゴシック');
    expect(game.defaultTextColor).toBe('#C0C0C0');

    // undefined 不覆盖
    game.setGameLayout({});
    expect(game.windowWidth).toBe(1000);
    expect(game.fontSize).toBe(20);
    expect(game.lineHeight).toBe(22);
    expect(game.gameColumns).toBe(124);
    expect(game.fontName).toBe('ＭＳ ゴシック');
    expect(game.defaultTextColor).toBe('#C0C0C0');
  });
});

// ---------- loadGame 与 layout 字段交互 ----------
//
// Issue 12：loadGame 成功后会重读 GET /state 更新 layout 字段。
// 测试覆盖：
// - 成功路径：fetch /load-game 200 + fetch /state 200 → layout 字段更新
// - /state 失败：layout 字段保持原值（不阻塞游戏切换）
// - 网络错误：layout 字段保持原值

describe('useGameStore.loadGame - Issue 12 layout 字段更新', () => {
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

  /** 内存版 localStorage。 */
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

  /** 配置 fetch mock 多次调用的响应序列。 */
  function mockResponses(...responses: Array<{ status: number; body: unknown }>): void {
    for (const r of responses) {
      fetchMock.mockResolvedValueOnce({
        status: r.status,
        json: async () => r.body,
        text: async () => JSON.stringify(r.body),
      } as Response);
    }
  }

  it('loadGame 成功后重读 GET /state 更新 layout 字段', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // 第一次 fetch: POST /load-game 200
    // 第二次 fetch: GET /state 200（携带 windowWidth=1000 + gameColumns=124 + fontName）
    mockResponses(
      { status: 200, body: { sessionId: 'abc', state: 'Idle', gameDir: 'D:/game1000' } },
      {
        status: 200,
        body: {
          state: 'Idle',
          isRunning: false,
          gameDir: 'D:/game1000',
          windowWidth: 1000,
          fontSize: 20,
          lineHeight: 22,
          gameColumns: 124,
          fontName: 'ＭＳ ゴシック',
          foreColor: '#7F0000',
        },
      },
    );

    expect(game.windowWidth).toBeNull();
    await game.loadGame('D:/game1000');

    // layout 字段被 GET /state 响应更新
    expect(game.windowWidth).toBe(1000);
    expect(game.fontSize).toBe(20);
    expect(game.lineHeight).toBe(22);
    expect(game.gameColumns).toBe(124);
    expect(game.fontName).toBe('ＭＳ ゴシック');
    expect(game.defaultTextColor).toBe('#7F0000');
  });

  it('GET /state 失败：layout 字段保持原值（不阻塞游戏切换）', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    // 先写入一个已知 layout 值
    game.setGameLayout({
      windowWidth: 760,
      fontSize: 18,
      lineHeight: 19,
      gameColumns: 84,
      fontName: 'ＭＳ ゴシック',
    });
    expect(game.windowWidth).toBe(760);

    // /load-game 200，/state 返 500
    mockResponses(
      { status: 200, body: { sessionId: 'abc', state: 'Idle', gameDir: 'D:/game' } },
      { status: 500, body: { error: 'internal' } },
    );

    await game.loadGame('D:/game');

    // layout 字段保持原值
    expect(game.windowWidth).toBe(760);
    expect(game.fontSize).toBe(18);
    expect(game.lineHeight).toBe(19);
    expect(game.gameColumns).toBe(84);
    expect(game.fontName).toBe('ＭＳ ゴシック');
    // 游戏切换仍成功
    expect(game.gameDir).toBe('D:/game');
  });

  it('GET /state 网络错误：layout 字段保持原值', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    game.setGameLayout({
      windowWidth: 800,
      fontSize: 16,
      lineHeight: 18,
      gameColumns: 112,
      fontName: 'メイリオ',
    });

    // /load-game 200，/state fetch 抛错
    fetchMock.mockResolvedValueOnce({
      status: 200,
      json: async () => ({ sessionId: 'abc', state: 'Idle', gameDir: 'D:/game' }),
      text: async () => '',
    } as Response);
    fetchMock.mockRejectedValueOnce(new Error('network error'));

    await game.loadGame('D:/game');

    expect(game.windowWidth).toBe(800);
    expect(game.fontSize).toBe(16);
    expect(game.lineHeight).toBe(18);
    expect(game.gameColumns).toBe(112);
    expect(game.fontName).toBe('メイリオ');
    // 游戏切换仍成功
    expect(game.gameDir).toBe('D:/game');
  });

  it('GET /state 响应缺 windowWidth 字段：layout 字段保持原值', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);

    game.setGameLayout({
      windowWidth: 760,
      fontSize: 18,
      lineHeight: 19,
      gameColumns: 84,
      fontName: 'ＭＳ ゴシック',
    });

    // /load-game 200，/state 响应缺 layout 字段（理论不会发生，作兜底测试）
    mockResponses(
      { status: 200, body: { sessionId: 'abc', state: 'Idle', gameDir: 'D:/game' } },
      { status: 200, body: { state: 'Idle', isRunning: false, gameDir: 'D:/game' } },
    );

    await game.loadGame('D:/game');

    // layout 字段保持原值
    expect(game.windowWidth).toBe(760);
    expect(game.fontSize).toBe(18);
    expect(game.lineHeight).toBe(19);
    expect(game.gameColumns).toBe(84);
    expect(game.fontName).toBe('ＭＳ ゴシック');
  });
});
