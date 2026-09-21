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
  };
});
