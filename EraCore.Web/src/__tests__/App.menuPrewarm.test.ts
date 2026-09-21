// @vitest-environment happy-dom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { nextTick } from 'vue';
import App from '../App.vue';
import { useUiStore } from '../stores/ui';
import { useGameStore } from '../stores/game';

/**
 * 菜单预热开关（ui.menuPrewarm）在 App 层的行为回归。
 *
 * 守住的不变量：
 * 1. 存储未设置（= 默认开）→ 挂载即调一次 prewarmMenuShadow（既有行为不变）；
 * 2. 存储为 'off' → 挂载**不**预热（真回到优化前基线，供低端设备 / 对比复验）；
 * 3. 启动关、之后在设置页拨到「开」→ 当次会话补一次预热（不必重开应用）。
 *
 * 这里把 prewarmMenuShadow 整体打桩：真实实现要往 document.body 插探针、等
 * requestIdleCallback + 两帧 rAF，与本测试要断言的「有没有被调用」无关且更脆。
 * 预热机制本身的取舍见 .scratch/menu-first-open-jank/spec.md。
 */
const { prewarmSpy } = vi.hoisted(() => ({ prewarmSpy: vi.fn() }));
vi.mock('../lib/prewarmMenuShadow', () => ({ prewarmMenuShadow: prewarmSpy }));

// App 挂载期的其它副作用与本测试无关（同 App.viewSwitch.test.ts 的打桩口径）
vi.mock('../lib/mauiBridge', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../lib/mauiBridge')>()),
  isMauiEnvironment: () => false,
}));
vi.mock('../composables/useAppInit', () => ({ initAppState: vi.fn() }));
vi.mock('../composables/useGameStatusMonitor', () => ({ startGameStatusMonitor: vi.fn() }));
vi.mock('../lib/resourceResolver', () => ({ resolveResource: (path: string) => `/assets/${path}` }));
vi.mock('../composables/usePinchZoom', () => ({ usePinchZoom: () => {} }));

describe('App: 菜单预热开关', () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    // 游戏运行中（非 Idle）→ 渲染 game-view 分支而非两个游戏列表
    useGameStore().serverState = 'WaitInput';
    prewarmSpy.mockClear();
  });

  it('存储未设置（默认开）→ 挂载即预热一次', () => {
    const wrapper = mount(App);
    expect(prewarmSpy).toHaveBeenCalledTimes(1);
    wrapper.unmount();
  });

  it("存储为 'off' → 挂载不预热", () => {
    localStorage.setItem('app.menuPrewarm', 'off');
    const wrapper = mount(App);
    expect(prewarmSpy).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('启动时关、之后拨到「开」→ 当次会话补一次预热', async () => {
    localStorage.setItem('app.menuPrewarm', 'off');
    const ui = useUiStore();
    const wrapper = mount(App);
    expect(prewarmSpy).not.toHaveBeenCalled();

    ui.setMenuPrewarm(true);
    await nextTick();
    expect(prewarmSpy).toHaveBeenCalledTimes(1);
    expect(localStorage.getItem('app.menuPrewarm')).toBe('on');
    wrapper.unmount();
  });
});
