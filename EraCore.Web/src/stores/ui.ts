import { defineStore } from 'pinia';
import { ref } from 'vue';

/**
 * useUiStore — UI 视图状态。
 *
 * v1 只有 'terminal' 和 'debug' 两个视图（spec.md：「不用 Vue Router，单页面 + Pinia view state」）。
 * Issue 05 起增加 platform 字段，区分 web / android，决定 GamePicker 渲染哪个组件。
 */

export type UiView = 'terminal' | 'debug' | 'settings';
/**
 * 运行平台——issue 05 起 GamePicker 按平台渲染不同组件：
 * - 'web'：桌面浏览器，用路径文本输入框（浏览器安全限制不暴露真实 FS 路径）
 * - 'android'：MAUI WebView 壳，调 C# `POST /native/pick-directory` 触发原生 SAF 选择器
 *
 * 检测策略（spec.md L228）：MAUI 壳会在 window 注入 `window.__EMUERA_PLATFORM__='android'`；
 * 浏览器环境无此全局变量，回落 'web'。同时检查 userAgent 含 'Android' 作 fallback
 * （开发期用 Chrome DevTools 模拟移动端调试时也走 android 路径）。
 */
export type Platform = 'web' | 'android';

/**
 * 触控排版下终端按钮的目标高度（px）——与 `styles/theme.css` 的 `--touch-target` 一致。
 *
 * 含有效按钮的那一行会被撑到这个高度，按钮盒高度取同一值，于是点击盒 = 整行。
 * 导出给 `TerminalDisplay` 用，避免同一常量在两处漂移。
 */
export const TOUCH_TARGET_MIN_PX = 48;

/**
 * 触控排版的横向阈值（列）——唯一的旋钮，见下方 `touchTargetCols`。
 *
 * 以**列**为单位而非像素：列几何直接来自快照的 `ButtonRef.col`/`width`（零 DOM 测量），
 * 注入样式时用 `ch` 表达（等宽终端下 1ch = 1 个 ASCII 字符 = 1 个游戏列）。代价是实际
 * 像素会随字体在 ch 的真实宽度上浮动（浏览器 monospace 约 0.5–0.6em）——即以「字符」
 * 而非绝对像素为触控尺度。目标高度（纵向）仍是像素，见 `TOUCH_TARGET_MIN_PX`。
 *
 * 曾经还有一个「按钮最小间隔」（相邻热区之间强制留几列净空）的旋钮，已撤掉：约束只需
 * 「两个热区不重叠」，两侧平分空隙即可，刻意留净空只会让热区变小而且几乎看不出来
 * （多数游戏一行一个按钮或按钮本就够宽，那个旋钮根本不被触发）。
 */
export const TOUCH_TARGET_COLS_DEFAULT = 5;
/** 目标触控宽度的候选档位（列）——同时充当读档时的合法值白名单。 */
export const TOUCH_TARGET_COLS_CHOICES = [3, 4, 5, 6, 7] as const;

/**
 * 检测当前运行平台——纯函数，便于单测。
 *
 * MAUI 壳启动时在 WebView 注入：
 *   window.__EMUERA_PLATFORM__ = 'android'
 * 浏览器（含 dev server / 桌面 Chrome / Firefox）无此全局变量，回落 'web'。
 * userAgent 含 'Android' 但无 MAUI 注入时也走 'android'（开发期模拟器 / 第三方壳兼容路径）。
 *
 * 入参显式传入，便于测试 mock；生产代码调 detectPlatform() 不传参，从浏览器环境读。
 */
export function detectPlatform(
  globalObj: any = typeof globalThis !== 'undefined' ? globalThis : {},
  userAgent: string = typeof navigator !== 'undefined' ? navigator.userAgent : '',
): Platform {
  // 优先读 MAUI 壳注入的全局变量
  if (globalObj?.__EMUERA_PLATFORM__ === 'android') return 'android';
  // Fallback：userAgent 匹配（DevTools 模拟 / 第三方壳兼容）
  if (/Android/i.test(userAgent)) return 'android';
  return 'web';
}

export const useUiStore = defineStore('ui', () => {
  // Issue 03 起 Terminal 视图正式可用——默认展示用户视角。
  // Debug 视图仍保留供协议调试（顶部 view-switch 切换）。
  const currentView = ref<UiView>('terminal');

  // Issue 05：平台检测——store 初始化时同步检测一次。
  // 平台在 app 生命周期内不会变化（不会从浏览器变成 MAUI），故 ref 不需要 setter。
  const platform = ref<Platform>(detectPlatform());

  /**
   * 虚拟滚动 Stick to bottom 标志——同时控制自动跟随与点击推进守卫。
   *
   * 初始 true（未滚动过视为在底部）；用户向上滚过后变 false，新输出不再自动拉回；
   * 滚回底部后变 true，自动跟随恢复。同一标志还用作 AnyKey/EnterKey 模式的"点击推进"
   * 守卫——`isStickyToBottom=false` 时 `onTerminalClick`/`onGlobalClick` 拒绝推进，
   * 让用户安心翻看历史。
   *
   * 跨组件共享：`useVirtualScroll` composable 内部维护同一含义的 ref，
   * 通过 `onStickyChange` 回调同步到此 store；`InputBar` 读此 store 实现全局 click 守卫。
   */
  const isStickyToBottom = ref<boolean>(true);

  /**
   * 手动输入框可见性——`⌨` 悬浮钮切换。任何 WaitInput 下都能唤出数字/文本输入，
   * 覆盖"按钮之外只能键入数字才能选择的隐藏选项"。与自动弹出（无按钮时才弹）正交。
   */
  const manualInputVisible = ref<boolean>(false);

  function switchView(view: UiView): void {
    currentView.value = view;
  }

  /** 切换手动输入框显隐（⌨ 悬浮钮）。 */
  function toggleManualInput(): void {
    manualInputVisible.value = !manualInputVisible.value;
  }

  /**
   * 设置 isStickyToBottom——由 `useVirtualScroll` 的 onStickyChange 回调写入，
   * 或由 `clear_screen` 等需要强制回到底部的事件主动置 true。
   */
  function setStickyToBottom(v: boolean): void {
    isStickyToBottom.value = v;
  }

  /** 主题：'dark' | 'light'，从 localStorage 读取，默认 'dark'。 */
  const THEME_STORAGE_KEY = 'app.theme';
  function readThemeFromStorage(): 'dark' | 'light' {
    try {
      const v = localStorage.getItem(THEME_STORAGE_KEY);
      if (v === 'light' || v === 'dark') return v;
    } catch { /* 静默 */ }
    return 'dark';
  }
  function writeThemeToStorage(t: 'dark' | 'light'): void {
    try { localStorage.setItem(THEME_STORAGE_KEY, t); } catch { /* 静默 */ }
  }
  const theme = ref<'dark' | 'light'>(readThemeFromStorage());

  /** 切换主题并持久化。 */
  function toggleTheme(): void {
    theme.value = theme.value === 'dark' ? 'light' : 'dark';
    writeThemeToStorage(theme.value);
  }

  /**
   * 菜单首次打开优化（空闲期预热探针）开关：localStorage 读 'on'/'off'，默认开。
   *
   * 与 theme 同类：**纯前端本地设置**，不进 ADR-0030 全局设置——预热是 WebView 渲染行为
   * （把菜单材质的 GPU shader 编译提前到空闲期），C#/HTTP/CLI 侧没有任何消费者，
   * 硬塞进 canonical 键表只是给它找个持久化容器。见 .scratch/menu-first-open-jank/spec.md。
   *
   * 关 = 回到优化前基线（首次打开可能丢帧、看不到完整出场动画），供低端设备与对比复验用。
   */
  const MENU_PREWARM_STORAGE_KEY = 'app.menuPrewarm';
  function readMenuPrewarmFromStorage(): boolean {
    try {
      const v = localStorage.getItem(MENU_PREWARM_STORAGE_KEY);
      if (v === 'on') return true;
      if (v === 'off') return false;
      // 未设置 / 非法值 → 回落默认（含旧版本升级上来第一次运行）
    } catch { /* 静默：存储被禁或非浏览器环境（vitest 用 node 环境） */ }
    return true;
  }
  function writeMenuPrewarmToStorage(on: boolean): void {
    try { localStorage.setItem(MENU_PREWARM_STORAGE_KEY, on ? 'on' : 'off'); } catch { /* 静默 */ }
  }
  const menuPrewarm = ref<boolean>(readMenuPrewarmFromStorage());

  /** 设置预热开关并持久化——App 据此决定要不要调 prewarmMenuShadow()。 */
  function setMenuPrewarm(on: boolean): void {
    menuPrewarm.value = on;
    writeMenuPrewarmToStorage(on);
  }

  /** 切换预热开关并持久化（设置页 toggle 用）。 */
  function toggleMenuPrewarm(): void {
    setMenuPrewarm(!menuPrewarm.value);
  }

  /**
   * 触控排版开关——把含「有效按钮」的行整体撑到 ≥ TOUCH_TARGET_MIN_PX，让小按钮在触屏上点得准。
   *
   * 只撑有按钮的行，不动满屏文本行：手机竖屏的信息密度是最稀缺的资源，
   * 为几十行历史文本统一抬高行距纯属浪费。代价是行高不再均匀，由 `useVirtualScroll`
   * 的稀疏额外高度表（`lineExtras`）吸收——见 composable 内的说明。
   *
   * 「有效按钮」判定沿用 `inputRouting.isLiveButton`：当前回合 generation 且不在
   * EnterKey/AnyKey 惰性态。历史回合残留的失效按钮不参与排版，避免白白占屏。
   *
   * 纯前端本地设置（与 theme / menuPrewarm 同类）：C# / HTTP / CLI 侧没有消费者。
   * Android 默认开（触屏是主要输入方式），Web 默认关（鼠标点击精度足够）。
   */
  const TOUCH_LAYOUT_STORAGE_KEY = 'app.touchLayout';
  function readTouchLayoutFromStorage(): boolean {
    try {
      const v = localStorage.getItem(TOUCH_LAYOUT_STORAGE_KEY);
      if (v === 'on') return true;
      if (v === 'off') return false;
      // 未设置 / 非法值 → 回落平台默认值（含旧版本升级上来第一次运行）
    } catch { /* 静默：存储被禁或非浏览器环境（vitest 用 node 环境） */ }
    return platform.value === 'android';
  }
  function writeTouchLayoutToStorage(on: boolean): void {
    try { localStorage.setItem(TOUCH_LAYOUT_STORAGE_KEY, on ? 'on' : 'off'); } catch { /* 静默 */ }
  }
  const touchLayout = ref<boolean>(readTouchLayoutFromStorage());

  /** 设置触控排版开关并持久化。 */
  function setTouchLayout(on: boolean): void {
    touchLayout.value = on;
    writeTouchLayoutToStorage(on);
  }

  /** 切换触控排版开关并持久化（抽屉菜单 toggle 用）。 */
  function toggleTouchLayout(): void {
    setTouchLayout(!touchLayout.value);
  }

  /**
   * 触控排版的横向阈值——以列为单位，可在设置页「外观 · 触控排版」调整。
   *
   * 为什么做成可配置：合适的触控尺度取决于手指粗细与屏幕大小，没有普适值，把取舍交给
   * 玩家比我们替他定死更合理。改动即时生效——渲染按列几何重算，不需要重开游戏或重连
   * （但设置页会盖住终端，返回游戏页才看得到效果）。
   *
   * 与 `touchLayout` 同类：纯前端本地设置，不进 ADR-0030 全局设置（C#/HTTP/CLI 无消费者）。
   * 取值限定在候选档位内（`..._CHOICES`），越界/非法一律回落默认值——与其钳到一个
   * 玩家没选过的值，不如回到我们验证过的默认。
   */
  const TOUCH_TARGET_COLS_STORAGE_KEY = 'app.touchLayout.targetCols';

  /** 从 localStorage 读一个「档位值」——只接受候选表内的整数，否则回落 fallback。 */
  function readChoiceFromStorage(
    key: string,
    choices: readonly number[],
    fallback: number,
  ): number {
    try {
      const raw = localStorage.getItem(key);
      if (raw !== null) {
        const n = Number(raw);
        if (Number.isInteger(n) && choices.includes(n)) return n;
      }
      // 未设置 / 非法值 / 越界 → 回落默认（含旧版本升级上来第一次运行）
    } catch { /* 静默：存储被禁或非浏览器环境（vitest 用 node 环境） */ }
    return fallback;
  }
  function writeChoiceToStorage(key: string, value: number): void {
    try { localStorage.setItem(key, String(value)); } catch { /* 静默 */ }
  }

  const touchTargetCols = ref<number>(
    readChoiceFromStorage(TOUCH_TARGET_COLS_STORAGE_KEY, TOUCH_TARGET_COLS_CHOICES, TOUCH_TARGET_COLS_DEFAULT),
  );

  /** 设置目标触控宽度（列）并持久化。非候选档位直接忽略（UI 只提供候选档位）。 */
  function setTouchTargetCols(cols: number): void {
    if (!(TOUCH_TARGET_COLS_CHOICES as readonly number[]).includes(cols)) return;
    touchTargetCols.value = cols;
    writeChoiceToStorage(TOUCH_TARGET_COLS_STORAGE_KEY, cols);
  }

  return {
    currentView,
    platform,
    switchView,
    isStickyToBottom,
    setStickyToBottom,
    manualInputVisible,
    toggleManualInput,
    theme,
    toggleTheme,
    menuPrewarm,
    setMenuPrewarm,
    toggleMenuPrewarm,
    touchLayout,
    setTouchLayout,
    toggleTouchLayout,
    touchTargetCols,
    setTouchTargetCols,
  };
});
