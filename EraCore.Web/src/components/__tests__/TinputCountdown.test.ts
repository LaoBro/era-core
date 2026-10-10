// @vitest-environment happy-dom
import { describe, it, expect } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import TinputCountdown from '../TinputCountdown.vue';
import { useGameStore } from '../../stores/game';
import type { AwaitInfo } from '../../types/protocol';

/**
 * TinputCountdown 验收（issue 07 的倒计时部分；issue 10 撤回「跳过」后的复审）。
 *
 * 判据来自 v12 契约 `awaiting.wait`：
 * - `showTinputCountdown`（带上界的等待 + displayTime=true）→ 进度条 + 剩余秒数
 * - 🔴 **任何帧都不再有「跳过」按钮** —— `wait.interruptible` 连同服务端
 *   `InputRequest.Skippable` 一并撤回（spec §10.1：定时等待一律不可打断）。
 *   实测它在 eraTW `@BADAPPLE` 上逐帧闪烁，是撤回的直接动因。
 *
 * 🔒 关键闸门：**零时长让路**（`durationMs: 0`）不给任何覆盖层——它不是「需要等的东西」，
 * 而是「下一帧立刻到」。
 */

function mountCountdown() {
  const pinia = createPinia();
  setActivePinia(pinia);
  const game = useGameStore();
  const wrapper = mount(TinputCountdown, { global: { plugins: [pinia] } });
  return { wrapper, game };
}

/** 直接写 awaiting（绕过协议解析——本组件只读派生量）。 */
function setAwaiting(game: ReturnType<typeof useGameStore>, awaiting: AwaitInfo): void {
  game.displayState = { ...game.displayState, awaiting };
}

/** 造一个 v12 帧（用于需要 store 走完整解析路径的倒计时用例）。 */
function v12Frame(awaiting: unknown): string {
  return JSON.stringify({ generation: 1, protocolVersion: 12, diff: null, awaiting });
}

describe('TinputCountdown: 定时等待不再有「跳过」affordance（issue 10 撤回）', () => {
  it('AWAIT t（wait{durationMs:8000}）→ 不渲染跳过按钮，也不渲染整个覆盖层', async () => {
    const { wrapper, game } = mountCountdown();
    setAwaiting(game, { wait: { durationMs: 8000 } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tinput-skip').exists()).toBe(false);
    expect(wrapper.find('.tinput-countdown').exists()).toBe(false);
  });

  it('TWAIT t,1（同形）→ 同样不渲染跳过按钮', async () => {
    const { wrapper, game } = mountCountdown();
    setAwaiting(game, { wait: { durationMs: 8000 } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tinput-skip').exists()).toBe(false);
  });

  it('零时长让路（durationMs=0）→ 整个覆盖层不渲染', async () => {
    const { wrapper, game } = mountCountdown();
    setAwaiting(game, { wait: { durationMs: 0 } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tinput-countdown').exists()).toBe(false);
    expect(wrapper.find('.tinput-skip').exists()).toBe(false);
  });

  it('🔒 逐帧抖动不再可能：定时等待帧与让路帧交替时，覆盖层不出现/消失', async () => {
    const { wrapper, game } = mountCountdown();

    // @BADAPPLE 的两种帧形态交替：wait{durationMs:28}（领先）与 wait{durationMs:0}（追赶）。
    // 撤回前「跳过」按钮只在前一形态出现 ⇒ 以 ~30fps 反复挂载/卸载 ⇒ 顶部闪烁。
    for (const awaiting of [
      { wait: { durationMs: 28 } } as AwaitInfo,
      { wait: { durationMs: 0 } } as AwaitInfo,
      { wait: { durationMs: 28 } } as AwaitInfo,
      { wait: { durationMs: 0 } } as AwaitInfo,
    ]) {
      setAwaiting(game, awaiting);
      await wrapper.vm.$nextTick();
      expect(wrapper.find('.tinput-skip').exists()).toBe(false);
      expect(wrapper.find('.tinput-countdown').exists()).toBe(false);
    }
  });
});

describe('TinputCountdown: 倒计时（TINPUT）', () => {
  it('带上界的等待 + displayTime=true → 渲染进度条与剩余秒数，且无跳过按钮', async () => {
    const { wrapper, game } = mountCountdown();
    game.applyTurn(v12Frame({
      input: { type: 'IntValue' },
      wait: { durationMs: 3000, displayTime: true },
    }));
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tinput-progress').exists()).toBe(true);
    expect(wrapper.find('.tinput-countdown-text').text()).toContain('⏱');
    expect(wrapper.find('.tinput-skip').exists()).toBe(false);

    game.reset();
  });

  it('displayTime 缺省 → 只推进计时器不渲染倒计时（ERB 要求不显示）', async () => {
    const { wrapper, game } = mountCountdown();
    game.applyTurn(v12Frame({
      input: { type: 'IntValue' },
      wait: { durationMs: 3000 },
    }));
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tinput-countdown').exists()).toBe(false);
    // 内部计时器仍启动（timeoutNotice 仍可派生）
    expect(game.tinputStartedAt).not.toBeNull();

    game.reset();
  });
});
