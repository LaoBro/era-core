import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import { mauiGameLibrarySource, httpGameLibrarySource } from '../gameLibrary';
import { consumeDirectoryMessage } from '../mauiBridge';

/**
 * GameLibrarySource adapter 契约测试——GameLibraryView 依赖的传输层抽象。
 *
 * 覆盖：
 * - 两个 source 的能力标志（kind / supportsNativePicker）
 * - maui source.scan：标记扫描中 + 触发 bridge scanGames 消息
 * - http source.scan：委托 store.scanGamesHttp（mock fetch）
 * - http source.pickGame：委托 store.loadGame（mock fetch + connection spies）
 * - http source.listDirs：委托 store.browseDirectoryHttp
 * - issues/15 S2：maui source.listDirs / storageRoots——投递桥接消息 + 消费回执 + 超时兜底
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

  it('maui source.listDirs：投递 listDirectories 并消费 directoriesListed 回执（issues/15 S2）', async () => {
    const pending = mauiGameLibrarySource.listDirs('D:/emuera');
    const json = JSON.parse(postMessageSpy.mock.calls[0][0]);
    expect(json.type).toBe('listDirectories');
    expect(json.dirPath).toBe('D:/emuera');

    // C# 回执到达（wire 是 subDirectories，适配层映射为 dirs）
    expect(
      consumeDirectoryMessage({
        type: 'directoriesListed',
        currentPath: 'D:/emuera',
        parentPath: 'D:',
        subDirectories: ['a', 'b'],
      }),
    ).toBe(true);

    await expect(pending).resolves.toEqual({
      currentPath: 'D:/emuera',
      parentPath: 'D:',
      dirs: ['a', 'b'],
    });
  });

  it('maui source.storageRoots：投递 getStorageRoots 并消费 storageRoots 回执（issues/15 S2）', async () => {
    const pending = mauiGameLibrarySource.storageRoots!();
    const json = JSON.parse(postMessageSpy.mock.calls[0][0]);
    expect(json.type).toBe('getStorageRoots');

    expect(
      consumeDirectoryMessage({
        type: 'storageRoots',
        roots: [
          { path: '/storage/emulated/0', label: '内部存储', primary: true, removable: false },
          { path: '/storage/1234-5678', label: '可移动存储', primary: false, removable: true },
        ],
      }),
    ).toBe(true);

    await expect(pending).resolves.toEqual([
      { path: '/storage/emulated/0', label: '内部存储', primary: true, removable: false },
      { path: '/storage/1234-5678', label: '可移动存储', primary: false, removable: true },
    ]);
  });

  it('maui source.listDirs：C# 无回执时超时返回 null（不挂住对话框）', async () => {
    vi.useFakeTimers();
    const pending = mauiGameLibrarySource.listDirs('/storage/emulated/0');
    vi.advanceTimersByTime(10_000);
    await expect(pending).resolves.toBeNull();
  });

  it('consumeDirectoryMessage：非目录消息不消费（交回调用方分发）', () => {
    expect(consumeDirectoryMessage({ type: 'gamesScanned' })).toBe(false);
    expect(consumeDirectoryMessage({ type: 'permissionStatus', granted: true })).toBe(false);
  });

  it('http source.storageRoots：无挂载点概念，返回 null', async () => {
    expect(await httpGameLibrarySource.storageRoots!()).toBeNull();
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

    const result = await httpGameLibrarySource.listDirs('D:/emuera');

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
