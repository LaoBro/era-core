import { describe, it, expect, beforeEach, vi, afterEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import {
  useGameStore,
  readMainGameDirFromStorage,
  readLastPlayedGameFromStorage,
  formatMainGameDirForDisplay,
  type GameEntry,
} from '../game';
import { useUiStore } from '../ui';

/**
 * game-library spec Seam 3：useGameStore 主目录 / 上次玩过 / scanGames / unloadGame 单测。
 *
 * 测试矩阵：
 * - readMainGameDirFromStorage / readLastPlayedGameFromStorage 纯函数
 *   - 有值 / null / 空 / 异常 / 无 storage
 * - setMainGameDir：更新 ref + 持久化 localStorage
 * - setLastPlayedGame：更新 ref + 持久化 localStorage
 * - setScannedGames：写入 scannedGames + scanRootDir + scanStatus='idle' + 同步 mainGameDir
 * - scanRootDirDisplay：SAF content URI 的可读主目录（与 rootDir 成对校验，错配即失效）
 * - beginExitGame：成功 / 二次进入保护
 * - completeExitGame：清 gameDir + displayState + serverState='Idle' + exitStatus='idle'，
 *   不清 lastPlayedGame（列表高亮仍需）
 * - reset 时 exitStatus 回 'idle'
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

describe('readMainGameDirFromStorage 纯函数', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  it('有值 → 返回值', () => {
    localStorage.setItem('app.mainGameDir', 'D:/games/emuera');
    expect(readMainGameDirFromStorage()).toBe('D:/games/emuera');
  });

  it('null / 未设置 → null', () => {
    expect(readMainGameDirFromStorage()).toBeNull();
  });

  it('空字符串 → null', () => {
    localStorage.setItem('app.mainGameDir', '');
    expect(readMainGameDirFromStorage()).toBeNull();
  });

  it('storage=null → null', () => {
    expect(readMainGameDirFromStorage(null)).toBeNull();
  });
});

describe('readLastPlayedGameFromStorage 纯函数', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  it('有值 → 返回值', () => {
    localStorage.setItem('app.lastPlayedGame', '战勇RPG');
    expect(readLastPlayedGameFromStorage()).toBe('战勇RPG');
  });

  it('null / 未设置 → null', () => {
    expect(readLastPlayedGameFromStorage()).toBeNull();
  });

  it('空字符串 → null', () => {
    localStorage.setItem('app.lastPlayedGame', '');
    expect(readLastPlayedGameFromStorage()).toBeNull();
  });

  it('storage=null → null', () => {
    expect(readLastPlayedGameFromStorage(null)).toBeNull();
  });
});

describe('formatMainGameDirForDisplay 纯函数（SAF 兜底文案）', () => {
  it('没有可读名可依时折叠成常量，避免把 provider URL 展示给用户', () => {
    expect(formatMainGameDirForDisplay(
      'content://com.android.externalstorage.documents/tree/primary%3Aemuera',
    )).toBe('Android 存储目录');
  });

  it('保留传统文件系统路径', () => {
    expect(formatMainGameDirForDisplay('D:/games/emuera')).toBe('D:/games/emuera');
  });

  it('空路径显示未设置', () => {
    expect(formatMainGameDirForDisplay(null)).toBe('(未设置)');
    expect(formatMainGameDirForDisplay('   ')).toBe('(未设置)');
  });
});

/**
 * issues/15：SAF content URI 的可读主目录（C# `SafPath.TryGetDisplayPath` → ``displayPath``）。
 *
 * 展示名与它对应的 rootDir 成对存——`mauiGameLibrarySource.scan` 会立刻改写 scanRootDir，
 * 而回执要等 C# 扫完才到；只存展示串会让这段窗口出现「旧目录的名字 + 新目录的模式徽章」。
 */
describe('scanRootDirDisplay——可读主目录', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const SAF_DIR =
    'content://com.android.externalstorage.documents/tree/primary%3Agames%2Feratoho';
  const SAF_DISPLAY = '内部存储 / games / eratoho';

  it('回执带回 displayPath 时生效——SAF 目录名不再折叠成常量', () => {
    const game = useGameStore();
    game.setScannedGames([], SAF_DIR, true, SAF_DISPLAY);

    expect(game.scanRootDirDisplay).toBe(SAF_DISPLAY);
  });

  it('无 displayPath（Windows / Web / 旧 C# 消息）→ null，渲染方回退折叠文案', () => {
    const game = useGameStore();
    game.setScannedGames([], 'D:/emuera', true);

    expect(game.scanRootDirDisplay).toBeNull();
  });

  it('主目录已换、回执未到 → 立刻失效，不拿旧目录的名字配新目录', () => {
    const game = useGameStore();
    game.setScannedGames([], SAF_DIR, true, SAF_DISPLAY);

    // mauiGameLibrarySource.scan 收到新目录时会同步改写 scanRootDir
    game.scanRootDir =
      'content://com.android.externalstorage.documents/tree/primary%3Aother';

    expect(game.scanRootDirDisplay).toBeNull();
  });

  it('同一路径重扫 → 仍生效（路径校验按值比较，不按引用）', () => {
    const game = useGameStore();
    game.setScannedGames([], SAF_DIR, true, SAF_DISPLAY);
    game.setScannedGames([], SAF_DIR, true, SAF_DISPLAY);

    expect(game.scanRootDirDisplay).toBe(SAF_DISPLAY);
  });

  it('rootDir 为 null 时不留存展示名', () => {
    const game = useGameStore();
    game.setScannedGames([], null, null, SAF_DISPLAY);

    expect(game.scanRootDirDisplay).toBeNull();
  });
});

describe('useGameStore game-library 状态与动作', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  describe('初始状态', () => {
    it('mainGameDir 从 localStorage 初始化', () => {
      localStorage.setItem('app.mainGameDir', 'D:/games/emuera');
      const game = useGameStore();
      expect(game.mainGameDir).toBe('D:/games/emuera');
    });

    it('mainGameDir localStorage 无值时为 null', () => {
      const game = useGameStore();
      expect(game.mainGameDir).toBeNull();
    });

    it('lastPlayedGame 从 localStorage 初始化', () => {
      localStorage.setItem('app.lastPlayedGame', '战勇RPG');
      const game = useGameStore();
      expect(game.lastPlayedGame).toBe('战勇RPG');
    });

    it('lastPlayedGame localStorage 无值时为 null', () => {
      const game = useGameStore();
      expect(game.lastPlayedGame).toBeNull();
    });

    it('scannedGames / scanRootDir / scanStatus 初始为空/idle', () => {
      const game = useGameStore();
      expect(game.scannedGames).toEqual([]);
      expect(game.scanRootDir).toBeNull();
      expect(game.scanStatus).toBe('idle');
    });

    it('exitStatus 初始为 idle', () => {
      const game = useGameStore();
      expect(game.exitStatus).toBe('idle');
    });
  });

  describe('setMainGameDir', () => {
    it('更新 ref + 持久化 localStorage', () => {
      const game = useGameStore();
      game.setMainGameDir('D:/new/emuera');
      expect(game.mainGameDir).toBe('D:/new/emuera');
      expect(localStorage.getItem('app.mainGameDir')).toBe('D:/new/emuera');
    });

    it('trim 空格——前后空格被去除', () => {
      const game = useGameStore();
      game.setMainGameDir('  D:/trim/emuera  ');
      expect(game.mainGameDir).toBe('D:/trim/emuera');
      expect(localStorage.getItem('app.mainGameDir')).toBe('D:/trim/emuera');
    });

    it('空字符串不更新', () => {
      const game = useGameStore();
      game.setMainGameDir('D:/old/emuera');
      game.setMainGameDir('   ');
      expect(game.mainGameDir).toBe('D:/old/emuera');
    });
  });

  describe('setLastPlayedGame', () => {
    it('更新 ref + 持久化 localStorage', () => {
      const game = useGameStore();
      game.setLastPlayedGame('兰斯');
      expect(game.lastPlayedGame).toBe('兰斯');
      expect(localStorage.getItem('app.lastPlayedGame')).toBe('兰斯');
    });

    it('trim 空格', () => {
      const game = useGameStore();
      game.setLastPlayedGame('  战勇  ');
      expect(game.lastPlayedGame).toBe('战勇');
      expect(localStorage.getItem('app.lastPlayedGame')).toBe('战勇');
    });

    it('空字符串不更新', () => {
      const game = useGameStore();
      game.setLastPlayedGame('old');
      game.setLastPlayedGame('   ');
      expect(game.lastPlayedGame).toBe('old');
    });
  });

  describe('setScannedGames', () => {
    it('写入 scannedGames + scanRootDir + scanStatus=idle', () => {
      const game = useGameStore();
      const games: GameEntry[] = [
        { name: 'game1', fullPath: 'D:/emuera/game1' },
        { name: 'game2', fullPath: 'D:/emuera/game2' },
      ];
      game.scanStatus = 'scanning';
      game.setScannedGames(games, 'D:/emuera');

      expect(game.scannedGames).toEqual(games);
      expect(game.scanRootDir).toBe('D:/emuera');
      expect(game.scanStatus).toBe('idle');
    });

    it('rootDir 非空时同步 mainGameDir + localStorage', () => {
      const game = useGameStore();
      const games: GameEntry[] = [];
      game.setScannedGames(games, 'D:/new/main');

      expect(game.mainGameDir).toBe('D:/new/main');
      expect(localStorage.getItem('app.mainGameDir')).toBe('D:/new/main');
    });

    it('rootDir 与当前 mainGameDir 相同时不重复写 localStorage', () => {
      const game = useGameStore();
      game.setMainGameDir('D:/same/emuera');
      const writeSpy = vi.spyOn(localStorage, 'setItem');

      game.setScannedGames([], 'D:/same/emuera');

      // setScannedGames 不应再写 app.mainGameDir（值未变）
      // 注意：setMainGameDir 之前已写过一次，此处只校验 setScannedGames 调用后无新写入
      const mainDirWrites = writeSpy.mock.calls.filter(
        (c) => c[0] === 'app.mainGameDir',
      );
      expect(mainDirWrites).toHaveLength(0);
    });

    it('rootDir 为 null 时不清空 mainGameDir', () => {
      const game = useGameStore();
      game.setMainGameDir('D:/keep/emuera');
      game.setScannedGames([], null);

      expect(game.mainGameDir).toBe('D:/keep/emuera');
      expect(game.scanRootDir).toBeNull();
    });

    it('空游戏列表也接受——列表页据此展示「未找到游戏」空状态', () => {
      const game = useGameStore();
      game.setScannedGames([], 'D:/empty/emuera');

      expect(game.scannedGames).toEqual([]);
      expect(game.scanRootDir).toBe('D:/empty/emuera');
    });
  });

  describe('beginExitGame', () => {
    it('首次调用 → exitStatus=exiting + 返 true', () => {
      const game = useGameStore();
      const result = game.beginExitGame();

      expect(result).toBe(true);
      expect(game.exitStatus).toBe('exiting');
    });

    it('二次进入保护——exiting 时返 false 不再变更', () => {
      const game = useGameStore();
      game.beginExitGame();
      const result = game.beginExitGame();

      expect(result).toBe(false);
      expect(game.exitStatus).toBe('exiting');
    });
  });

  describe('completeExitGame', () => {
    it('清空 gameDir + localStorage', () => {
      const game = useGameStore();
      game.setGameDir('D:/emuera/game1');
      expect(localStorage.getItem('app.gameDir')).toBe('D:/emuera/game1');

      game.completeExitGame();

      expect(game.gameDir).toBeNull();
      expect(localStorage.getItem('app.gameDir')).toBeNull();
    });

    it('重置 serverState 为 Idle', () => {
      const game = useGameStore();
      game.applyServerState('WaitInput');
      expect(game.serverState).toBe('WaitInput');

      game.completeExitGame();

      expect(game.serverState).toBe('Idle');
    });

    it('重置 exitStatus 为 idle', () => {
      const game = useGameStore();
      game.beginExitGame();
      expect(game.exitStatus).toBe('exiting');

      game.completeExitGame();

      expect(game.exitStatus).toBe('idle');
    });

    it('清空 displayState + lastTurn', () => {
      const game = useGameStore();
      // 模拟 WS 帧推送
      game.applyTurn(
        JSON.stringify({
          state: 'WaitInput',
          needValue: false,
          generation: 1,
          diff: null,
        }),
      );
      expect(game.lastTurn).not.toBeNull();
      expect(game.displayState.state).toBe('WaitInput');

      game.completeExitGame();

      expect(game.lastTurn).toBeNull();
      expect(game.displayState.state).toBe('');
    });

    it('视图拨回 terminal——退出前停留 debug/settings，重开不残留', () => {
      const game = useGameStore();
      const ui = useUiStore();
      ui.switchView('debug');
      expect(ui.currentView).toBe('debug');

      game.completeExitGame();

      expect(ui.currentView).toBe('terminal');

      // settings 同理
      ui.switchView('settings');
      game.completeExitGame();
      expect(ui.currentView).toBe('terminal');
    });

    it('不清空 lastPlayedGame——列表高亮仍需', () => {
      const game = useGameStore();
      game.setLastPlayedGame('战勇RPG');

      game.completeExitGame();

      expect(game.lastPlayedGame).toBe('战勇RPG');
      expect(localStorage.getItem('app.lastPlayedGame')).toBe('战勇RPG');
    });

    it('不清空 mainGameDir——列表页底部「主目录」展示仍需', () => {
      const game = useGameStore();
      game.setMainGameDir('D:/emuera');

      game.completeExitGame();

      expect(game.mainGameDir).toBe('D:/emuera');
      expect(localStorage.getItem('app.mainGameDir')).toBe('D:/emuera');
    });

    it('不清空 scannedGames——列表页继续展示原列表，等 scanGames 回复后覆盖', () => {
      const game = useGameStore();
      const games: GameEntry[] = [{ name: 'game1', fullPath: 'D:/emuera/game1' }];
      game.setScannedGames(games, 'D:/emuera');

      game.completeExitGame();

      expect(game.scannedGames).toEqual(games);
      expect(game.scanRootDir).toBe('D:/emuera');
    });
  });

  describe('reset 时 exitStatus', () => {
    it('reset 时 exitStatus 回 idle', () => {
      const game = useGameStore();
      game.beginExitGame();
      expect(game.exitStatus).toBe('exiting');

      game.reset();

      expect(game.exitStatus).toBe('idle');
    });
  });

  describe('完整退出流程模拟', () => {
    it('beginExitGame → completeExitGame 后状态正确，可再次开始', () => {
      const game = useGameStore();
      game.setMainGameDir('D:/emuera');
      game.setLastPlayedGame('game1');
      game.setGameDir('D:/emuera/game1');
      game.applyServerState('WaitInput');
      game.applyTurn(
        JSON.stringify({
          state: 'WaitInput',
          needValue: false,
          generation: 1,
          diff: null,
        }),
      );

      // 用户点退出
      expect(game.beginExitGame()).toBe(true);
      expect(game.exitStatus).toBe('exiting');

      // C# 回复 gameExited 后
      game.completeExitGame();

      expect(game.exitStatus).toBe('idle');
      expect(game.gameDir).toBeNull();
      expect(game.serverState).toBe('Idle');
      expect(game.lastTurn).toBeNull();
      // mainGameDir / lastPlayedGame / scannedGames 保留
      expect(game.mainGameDir).toBe('D:/emuera');
      expect(game.lastPlayedGame).toBe('game1');
      expect(game.scannedGames).toEqual([]);

      // 再次 beginExitGame 应允许
      expect(game.beginExitGame()).toBe(true);
    });
  });
});

/**
 * issues/15（方案 B）：模式降级告知——「所有文件访问」被系统收回后主目录自动退回兼容轨。
 *
 * 与 mauiError 是**两条独立通道**：降级不是错误（游戏照样能玩），只是告知；
 * 混进错误条会让用户以为坏了，还会被错误条的 5 秒定时器与关闭逻辑搅在一起。
 */
describe('modeNotice——模式降级告知', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('初始为 null', () => {
    expect(useGameStore().modeNotice).toBeNull();
  });

  it('写入后可在 store 读到，清空后回 null', () => {
    const game = useGameStore();

    game.setModeNotice('「所有文件访问」权限已失效，已退回兼容模式，加载会慢一些。');
    expect(game.modeNotice).toContain('已退回兼容模式');

    game.clearModeNotice();
    expect(game.modeNotice).toBeNull();
  });

  it('与 mauiError 互不影响——降级不该被当成错误', () => {
    const game = useGameStore();

    game.setModeNotice('已退回兼容模式');
    expect(game.mauiError).toBeNull();

    game.mauiError = '选择目录失败';
    expect(game.modeNotice).toBe('已退回兼容模式');
  });
});
