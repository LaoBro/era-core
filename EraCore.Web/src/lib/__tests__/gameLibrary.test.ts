import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import { mauiGameLibrarySource, httpGameLibrarySource } from '../gameLibrary';

/**
 * GameLibrarySource adapter 契约测试——GameLibraryView 依赖的传输层抽象。
 *
 * 覆盖：
 * - 两个 source 的能力标志（kind / supportsNativePicker）
 * - maui source.scan：标记扫描中 + 触发 bridge scanGames 消息
 * - http source.scan：委托 store.scanGamesHttp（mock fetch）
 * - http source.pickGame：委托 store.loadGame（mock fetch + connection spies）
 * - http source.listDirs：委托 store.browseDirectoryHttp
 *
 * issues/15 方案 B 起 `listDirs` 只有 http 实现（MAUI 目录入口唯一，页内浏览器为 Web 专用），
 * 故 maui 的 listDirs / storageRoots 用例随之删除。
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

describe('gameLibrary source adapters', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let postMessageSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    // maui source 走 window.chrome.webview.postMessage
    postMessageSpy = vi.fn();
    vi.stubGlobal('window', {
      chrome: { webview: { postMessage: postMessageSpy } },
      location: { protocol: 'ms-appx-web:', hostname: '' },
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it('能力标志：maui 支持原生选择器，http 不支持', () => {
    expect(mauiGameLibrarySource.kind).toBe('maui');
    expect(mauiGameLibrarySource.supportsNativePicker).toBe(true);
    expect(httpGameLibrarySource.kind).toBe('http');
    expect(httpGameLibrarySource.supportsNativePicker).toBe(false);
  });

  it('maui source.scan：标记扫描中 + 触发 bridge scanGames 消息', async () => {
    const game = useGameStore();
    const ok = await mauiGameLibrarySource.scan('D:/emuera');

    expect(ok).toBe(true);
    expect(game.scanStatus).toBe('scanning');
    expect(game.scanRootDir).toBe('D:/emuera');
    // 投递 {"type":"scanGames","rootDir":...}
    expect(postMessageSpy).toHaveBeenCalledTimes(1);
    const json = JSON.parse(postMessageSpy.mock.calls[0][0]);
    expect(json.type).toBe('scanGames');
    expect(json.rootDir).toBe('D:/emuera');
  });

  it('maui source.scan：空目录 → false + 不触发 bridge', async () => {
    const ok = await mauiGameLibrarySource.scan('   ');
    expect(ok).toBe(false);
    expect(postMessageSpy).not.toHaveBeenCalled();
  });

  it('maui source.pickMainDir：触发原生选择器（返回 null，结果经消息回流）', async () => {
    expect(await mauiGameLibrarySource.pickMainDir()).toBeNull();
    const json = JSON.parse(postMessageSpy.mock.calls[0][0]);
    expect(json.type).toBe('pickFolder');
  });

  it('http source.scan：委托 store.scanGamesHttp 更新列表', async () => {
    const game = useGameStore();
    fetchMock.mockResolvedValueOnce({
      status: 200,
      json: async () => ({
        rootDir: 'D:/emuera',
        rootDirExists: true,
        games: [{ name: 'game1', fullPath: 'D:/emuera/game1' }],
      }),
      text: async () => '',
    } as Response);

    const ok = await httpGameLibrarySource.scan('D:/emuera');

    expect(ok).toBe(true);
    expect(game.scannedGames).toEqual([{ name: 'game1', fullPath: 'D:/emuera/game1' }]);
    expect(game.scanRootDir).toBe('D:/emuera');
  });

  it('http source.listDirs：委托 store.browseDirectoryHttp', async () => {
    fetchMock.mockResolvedValueOnce({
      status: 200,
      json: async () => ({ currentPath: 'D:/emuera', parentPath: 'D:', dirs: ['a', 'b'] }),
      text: async () => '',
    } as Response);

    const result = await httpGameLibrarySource.listDirs!('D:/emuera');

    expect(result).toEqual({ currentPath: 'D:/emuera', parentPath: 'D:', dirs: ['a', 'b'] });
  });

  it('http source.pickGame：委托 store.loadGame', async () => {
    const game = useGameStore();
    const conn = useConnectionStore();
    vi.spyOn(conn, 'disconnect').mockImplementation(() => {});
    vi.spyOn(conn, 'connect').mockResolvedValue(undefined);
    fetchMock.mockResolvedValueOnce({
      status: 200,
      json: async () => ({ sessionId: 'x', state: 'Idle', gameDir: 'D:/emuera/game1' }),
      text: async () => '',
    } as Response);

    await httpGameLibrarySource.pickGame('game1', 'D:/emuera/game1');

    expect(game.lastPlayedGame).toBe('game1');
    expect(game.gameDir).toBe('D:/emuera/game1');
    expect(game.loadGameError).toBeNull();
  });
});
