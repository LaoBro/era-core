import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useUiStore, detectPlatform, TOUCH_TARGET_COLS_DEFAULT } from '../ui';

/**
 * useUiStore / detectPlatform 单测——issue 05 平台检测。
 *
 * detectPlatform 纯函数（spec L228）：
 * - MAUI 壳注入 `window.__EMUERA_PLATFORM__='android'` → 'android'
 * - 浏览器无注入 + userAgent 不含 Android → 'web'
 * - userAgent 含 Android 但无 MAUI 注入（DevTools 模拟 / 第三方壳）→ 'android'
 * - 优先级：MAUI 注入 > userAgent
 *
 * useUiStore.platform：
 * - 默认 = detectPlatform() 结果（浏览器环境通常 'web'）
 * - 平台在 app 生命周期内不变（无 setter）
 */
describe('detectPlatform 纯函数', () => {
  it('MAUI 壳注入 __EMUERA_PLATFORM__=android → android', () => {
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'android' }, 'Mozilla/5.0')).toBe('android');
  });

  it('浏览器无注入 + userAgent 不含 Android → web', () => {
    expect(detectPlatform({}, 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)')).toBe('web');
    expect(detectPlatform({}, 'Mozilla/5.0 (Macintosh)')).toBe('web');
    expect(detectPlatform({}, 'Mozilla/5.0 (X11; Linux x86_64)')).toBe('web');
  });

  it('userAgent 含 Android 但无 MAUI 注入 → android（DevTools 模拟 / 第三方壳）', () => {
    expect(detectPlatform({}, 'Mozilla/5.0 (Linux; Android 13)')).toBe('android');
    expect(detectPlatform({}, 'Mozilla/5.0 (Linux; U; Android 11)')).toBe('android');
  });

  it('MAUI 注入优先于 userAgent（iOS 模拟 Android 边界场景）', () => {
    // 即使 userAgent 不是 Android，MAUI 注入仍优先
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'android' }, 'Mozilla/5.0 (iPhone)')).toBe('android');
  });

  it('MAUI 注入非 android 值 → 忽略，回落到 userAgent 检测', () => {
    // 防御性：MAUI 壳误注入 'ios' 等非支持值时，回落到 userAgent
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'ios' }, 'Mozilla/5.0 (Linux; Android 13)')).toBe('android');
    expect(detectPlatform({ __EMUERA_PLATFORM__: 'ios' }, 'Mozilla/5.0 (Windows)')).toBe('web');
  });

  it('空 globalObj / 空 userAgent → web', () => {
    expect(detectPlatform({}, '')).toBe('web');
    expect(detectPlatform(null, '')).toBe('web');
    expect(detectPlatform(undefined, undefined)).toBe('web');
  });

  it('__EMUERA_PLATFORM__ 为非 string 值（如 number）→ 忽略，回落到 userAgent', () => {
    expect(detectPlatform({ __EMUERA_PLATFORM__: 1 }, 'Mozilla/5.0 (Windows)')).toBe('web');
    expect(detectPlatform({ __EMUERA_PLATFORM__: true }, 'Mozilla/5.0 (Linux; Android 13)')).toBe('android');
  });
});

describe('useUiStore platform', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('store 初始化时调用 detectPlatform——浏览器环境默认 web', () => {
    // jsdom 环境 navigator.userAgent 不含 Android，也无 MAUI 注入
    const ui = useUiStore();
    expect(ui.platform).toBe('web');
  });

  it('currentView 默认 terminal', () => {
    const ui = useUiStore();
    expect(ui.currentView).toBe('terminal');
  });

  it('switchView 切换到 debug', () => {
    const ui = useUiStore();
    ui.switchView('debug');
    expect(ui.currentView).toBe('debug');
    ui.switchView('terminal');
    expect(ui.currentView).toBe('terminal');
  });

  it('switchView 切换到 settings', () => {
    const ui = useUiStore();
    ui.switchView('settings');
    expect(ui.currentView).toBe('settings');
    ui.switchView('terminal');
    expect(ui.currentView).toBe('terminal');
  });
});

// ---------- isStickyToBottom（虚拟滚动 + sticky 守卫） ----------
//
// spec.md 决策四：isStickyToBottom 跨组件共享——composable 内部维护，
// 通过 onStickyChange 回调同步到 useUiStore；InputBar 读 store 实现全局 click 守卫。
describe('useUiStore isStickyToBottom', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('初始值为 true（未滚动过视为在底部）', () => {
    const ui = useUiStore();
    expect(ui.isStickyToBottom).toBe(true);
  });

  it('setStickyToBottom(false) 翻看历史', () => {
    const ui = useUiStore();
    ui.setStickyToBottom(false);
    expect(ui.isStickyToBottom).toBe(false);
  });

  it('setStickyToBottom(true) 滚回底部', () => {
    const ui = useUiStore();
    ui.setStickyToBottom(false);
    expect(ui.isStickyToBottom).toBe(false);
    ui.setStickyToBottom(true);
    expect(ui.isStickyToBottom).toBe(true);
  });

  it('setStickyToBottom 幂等——重复设同值不报错', () => {
    const ui = useUiStore();
    ui.setStickyToBottom(true);
    ui.setStickyToBottom(true);
    expect(ui.isStickyToBottom).toBe(true);
    ui.setStickyToBottom(false);
    ui.setStickyToBottom(false);
    expect(ui.isStickyToBottom).toBe(false);
  });
});

// ---------- manualInputVisible（⌨ 手动输入开关） ----------
describe('useUiStore manualInputVisible', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('初始隐藏（false）——自动弹出是唯一默认通道', () => {
    const ui = useUiStore();
    expect(ui.manualInputVisible).toBe(false);
  });

  it('toggleManualInput 切换显隐', () => {
    const ui = useUiStore();
    ui.toggleManualInput();
    expect(ui.manualInputVisible).toBe(true);
    ui.toggleManualInput();
    expect(ui.manualInputVisible).toBe(false);
  });
});

// ---------- menuPrewarm（菜单首次打开优化的预热开关） ----------
//
// 纯前端本地设置（同 theme）：localStorage `app.menuPrewarm` 存 'on'/'off'，默认开。
// store 每次新建时读一次 → 用「重建 pinia + 重建 store」验证持久化真的落到了存储。
describe('useUiStore menuPrewarm', () => {
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

  beforeEach(() => setActivePinia(createPinia()));
  afterEach(() => vi.unstubAllGlobals());

  it('未设置 → 默认开（保持既有优化行为）', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    expect(useUiStore().menuPrewarm).toBe(true);
  });

  it("存储值 'off' → 关；'on' → 开", () => {
    const storage = makeLocalStorage();
    storage.setItem('app.menuPrewarm', 'off');
    vi.stubGlobal('localStorage', storage);
    expect(useUiStore().menuPrewarm).toBe(false);

    setActivePinia(createPinia());
    storage.setItem('app.menuPrewarm', 'on');
    expect(useUiStore().menuPrewarm).toBe(true);
  });

  it('非法值 → 回落默认开（不当成关）', () => {
    const storage = makeLocalStorage();
    storage.setItem('app.menuPrewarm', 'true'); // 非 'on'/'off' 词表，忽略
    vi.stubGlobal('localStorage', storage);
    expect(useUiStore().menuPrewarm).toBe(true);
  });

  it('setMenuPrewarm(false) 写入存储——新建 store 后仍为关', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    useUiStore().setMenuPrewarm(false);
    expect(localStorage.getItem('app.menuPrewarm')).toBe('off');

    setActivePinia(createPinia()); // 模拟重开应用：store 重建 → 从存储回填
    expect(useUiStore().menuPrewarm).toBe(false);
  });

  it('toggleMenuPrewarm 翻转并持久化', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    const ui = useUiStore();
    expect(ui.menuPrewarm).toBe(true);
    ui.toggleMenuPrewarm();
    expect(ui.menuPrewarm).toBe(false);
    expect(localStorage.getItem('app.menuPrewarm')).toBe('off');
    ui.toggleMenuPrewarm();
    expect(ui.menuPrewarm).toBe(true);
    expect(localStorage.getItem('app.menuPrewarm')).toBe('on');
  });

  it('存储不可用（读写皆抛）→ 默认开，且切换不抛异常', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
    });
    const ui = useUiStore();
    expect(ui.menuPrewarm).toBe(true);
    ui.setMenuPrewarm(false);
    expect(ui.menuPrewarm).toBe(false); // 未持久化，但内存态仍可切
  });
});

// ---------- touchLayout（触控排版：撑高含有效按钮的行） ----------
//
// localStorage `app.touchLayout` 存 'on'/'off'；默认值跟随平台——Android 默认开，
// Web 默认关（鼠标点击精度足够，不必付出排挤版面的代价）。
describe('useUiStore touchLayout', () => {
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

  beforeEach(() => setActivePinia(createPinia()));
  afterEach(() => vi.unstubAllGlobals());

  it('Web 平台未设置 → 默认关（不改既有桌面视觉）', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    const ui = useUiStore();
    expect(ui.platform).toBe('web');
    expect(ui.touchLayout).toBe(false);
  });

  it("存储值 'on' 优先于平台默认", () => {
    const storage = makeLocalStorage();
    storage.setItem('app.touchLayout', 'on');
    vi.stubGlobal('localStorage', storage);
    expect(useUiStore().touchLayout).toBe(true);
  });

  it("存储值 'off' → 关", () => {
    const storage = makeLocalStorage();
    storage.setItem('app.touchLayout', 'off');
    vi.stubGlobal('localStorage', storage);
    expect(useUiStore().touchLayout).toBe(false);
  });

  it('非法值 → 回落平台默认', () => {
    const storage = makeLocalStorage();
    storage.setItem('app.touchLayout', 'yes');
    vi.stubGlobal('localStorage', storage);
    expect(useUiStore().touchLayout).toBe(false);
  });

  it('toggleTouchLayout 翻转并持久化——重开应用后保留', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    const ui = useUiStore();
    ui.toggleTouchLayout();
    expect(ui.touchLayout).toBe(true);
    expect(localStorage.getItem('app.touchLayout')).toBe('on');

    setActivePinia(createPinia());
    expect(useUiStore().touchLayout).toBe(true);

    useUiStore().toggleTouchLayout();
    expect(localStorage.getItem('app.touchLayout')).toBe('off');
  });

  it('存储不可用（读写皆抛）→ 不抛异常，内存态仍可切', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
    });
    const ui = useUiStore();
    expect(ui.touchLayout).toBe(false);
    ui.setTouchLayout(true);
    expect(ui.touchLayout).toBe(true);
  });
});

describe('useUiStore 触控排版阈值', () => {
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

  beforeEach(() => setActivePinia(createPinia()));
  afterEach(() => vi.unstubAllGlobals());

  it('未设置 → 用默认档位（目标宽度 5 列）', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    expect(useUiStore().touchTargetCols).toBe(TOUCH_TARGET_COLS_DEFAULT);
  });

  it('存储值优先于默认值', () => {
    const storage = makeLocalStorage();
    storage.setItem('app.touchLayout.targetCols', '6');
    vi.stubGlobal('localStorage', storage);
    expect(useUiStore().touchTargetCols).toBe(6);
  });

  it('非法值一律回落默认——越界 / 非数字 / 非整数都不接受', () => {
    for (const bad of ['9', '1', 'abc', '2.5', '', 'null']) {
      const storage = makeLocalStorage();
      storage.setItem('app.touchLayout.targetCols', bad);
      vi.stubGlobal('localStorage', storage);
      setActivePinia(createPinia());
      expect(useUiStore().touchTargetCols).toBe(TOUCH_TARGET_COLS_DEFAULT);
    }
  });

  it('setter 只接受候选档位——越界值被忽略，且不落盘', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    const ui = useUiStore();
    ui.setTouchTargetCols(6);
    expect(ui.touchTargetCols).toBe(6);

    ui.setTouchTargetCols(99);
    expect(ui.touchTargetCols).toBe(6);
    expect(localStorage.getItem('app.touchLayout.targetCols')).toBe('6');
  });

  it('改动持久化——重建 store 后仍是玩家选的值', () => {
    vi.stubGlobal('localStorage', makeLocalStorage());
    useUiStore().setTouchTargetCols(7);

    setActivePinia(createPinia());
    expect(useUiStore().touchTargetCols).toBe(7);
  });

  it('存储不可用（读写皆抛）→ 不抛异常，内存态仍可设', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      },
    });
    const ui = useUiStore();
    expect(ui.touchTargetCols).toBe(TOUCH_TARGET_COLS_DEFAULT);
    ui.setTouchTargetCols(6);
    expect(ui.touchTargetCols).toBe(6);
  });
});
