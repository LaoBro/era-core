// @vitest-environment happy-dom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';
import App from '../App.vue';
import { useUiStore } from '../stores/ui';
import { useGameStore } from '../stores/game';

/**
 * App 视图切换的结构回归测试（2026-09-16）。
 *
 * 守住的不变量：**terminal ↔ debug/settings 切换不得卸载 TerminalView**。
 *
 * 违反它的症状（改造前）：进 Debug/Settings 会卸载 TerminalView → TerminalDisplay 一并销毁 →
 * useVirtualScroll 的 scrollTop/isStickyToBottom 与 .terminal 滚动容器全部丢失，返回时是新元素、
 * scrollTop 从 0 开始（"自动跳回最上方"），且重建后 vs.isStickyToBottom 回到初值 true、
 * 与 ui.isStickyToBottom（InputBar 点击推进守卫）失同步。
 *
 * 断言方式：用 **DOM 节点同一性**（`wrapper.find('.terminal-view').element` 跨切换后仍 === 首次
 * 拿到的引用）证明"未卸载重建"——比对 class 更严格：即使有人把实现换回 v-if，只要节点被重建，
 * 引用就不相等。
 */

// mauiBridge 只改 isMauiEnvironment（其余导出用真实实现，避免 App/子组件的其它导入变 undefined）
vi.mock('../lib/mauiBridge', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/mauiBridge')>()),
  isMauiEnvironment: () => false,
}));

// App 挂载期的副作用与本测试无关：初始化与静默监控整体打桩
vi.mock('../composables/useAppInit', () => ({ initAppState: vi.fn() }));
vi.mock('../composables/useGameStatusMonitor', () => ({ startGameStatusMonitor: vi.fn() }));
vi.mock('../lib/resourceResolver', () => ({ resolveResource: (path: string) => `/assets/${path}` }));
vi.mock('../composables/usePinchZoom', () => ({ usePinchZoom: () => {} }));

describe('App: 视图切换不卸载 TerminalView（滚动位置不丢）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    // 游戏运行中（非 Idle）→ 渲染 game-view（terminal/debug/settings）分支而非两个游戏列表
    useGameStore().serverState = 'WaitInput';
  });

  it('terminal → debug → settings → terminal：终端节点始终同一、且被隐藏而非移除', async () => {
    const ui = useUiStore();
    const wrapper = mount(App);

    const terminalNode = wrapper.find('.terminal-view').element;
    expect(terminalNode).toBeTruthy();
    expect(wrapper.find('.terminal-view').classes()).not.toContain('view-hidden');
    expect(wrapper.find('.debug-view').exists()).toBe(false);

    // → debug：Debug 是覆盖层；终端仍在 DOM（隐藏），且是同一个节点
    ui.switchView('debug');
    await nextTick();
    expect(wrapper.find('.debug-view').classes()).toContain('view-overlay');
    expect(wrapper.find('.terminal-view').exists()).toBe(true);
    expect(wrapper.find('.terminal-view').classes()).toContain('view-hidden');
    expect(wrapper.find('.terminal-view').element).toBe(terminalNode);

    // → settings：ADR-0030 起 Settings 是全局覆盖层（settings-overlay，盖在列表页/游戏页之上）
    ui.switchView('settings');
    await nextTick();
    expect(wrapper.find('.debug-view').exists()).toBe(false);
    expect(wrapper.find('.settings-view').classes()).toContain('settings-overlay');
    expect(wrapper.find('.terminal-view').element).toBe(terminalNode);
    expect(wrapper.find('.terminal-view').classes()).toContain('view-hidden');

    // → terminal：去掉隐藏类，节点仍是同一个（未重建 → scrollTop 与虚拟滚动状态天然保留）
    ui.switchView('terminal');
    await nextTick();
    expect(wrapper.find('.view-overlay').exists()).toBe(false);
    expect(wrapper.find('.terminal-view').classes()).not.toContain('view-hidden');
    expect(wrapper.find('.terminal-view').element).toBe(terminalNode);
  });

  it('.view-hidden 的样式契约必须是 visibility:hidden（禁止 display:none——会销毁布局盒）', () => {
    // 组件样式不注入测试 DOM（SFC style 在测试中不生效），故按源码契约断言：
    // 换成 display:none 会让 scrollTop/scrollHeight 归零、被覆盖期间新回合把位置写成 0。
    const src = readFileSync(`${process.cwd()}/src/App.vue`, 'utf8');
    const rule = /\.view-hidden\s*\{([^}]*)\}/.exec(src);
    expect(rule, '.view-hidden 规则缺失').toBeTruthy();
    const body = rule![1];
    expect(body).toContain('visibility: hidden');
    expect(body).not.toContain('display: none');
  });

  it('ADR-0030：从列表页打开设置后游戏被加载（serverState Idle→非 Idle）自动关设置页', async () => {
    const game = useGameStore();
    const ui = useUiStore();
    // 覆盖 beforeEach 的 'WaitInput'——本用例需要「列表页态」开局（Idle）
    game.serverState = 'Idle';
    const wrapper = mount(App);

    // 列表页（无游戏）打开设置
    ui.switchView('settings');
    await nextTick();
    expect(wrapper.find('.settings-view').exists()).toBe(true);

    // agent/CLI 加载游戏 → MAUI 加载流只改 serverState（layout 消息）→ 自动关设置页回到游戏画面
    game.serverState = 'WaitInput';
    await nextTick();
    expect(ui.currentView).toBe('terminal');
    expect(wrapper.find('.settings-view').exists()).toBe(false);
  });
});
