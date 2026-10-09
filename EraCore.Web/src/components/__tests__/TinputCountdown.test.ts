// @vitest-environment happy-dom
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import TinputCountdown from '../TinputCountdown.vue';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import type { AwaitInfo } from '../../types/protocol';

/**
 * TinputCountdown 验收（issue 07）：定时等待覆盖层的两种呈现 —— 倒计时 + 「跳过」提示。
 *
 * 判据全部来自 v12 契约 `awaiting.wait`：
 * - `showTinputCountdown`（带上界的等待 + displayTime=true）→ 进度条 + 剩余秒数
 * - `canSkipWait`（`interruptible === true` 且 `durationMs > 0`）→ 「跳过」按钮，点击提交空输入
 *
 * 🔒 关键闸门：**零时长让路**（`durationMs: 0`）两者都不给——它不是「需要等的东西」，
 * 而是「下一帧立刻到」。
 */

function mountCountdown() {
  const pinia = createPinia();
  setActivePinia(pinia);
  const game = useGameStore();
  const conn = useConnectionStore();
  conn.status = 'connected';
  conn.sendInput = vi.fn();
  const wrapper = mount(TinputCountdown, { global: { plugins: [pinia] } });
  return { wrapper, game, conn };
}

/** 直接写 awaiting（绕过协议解析——本组件只读派生量）。 */
function setAwaiting(game: ReturnType<typeof useGameStore>, awaiting: AwaitInfo): void {
  game.displayState = { ...game.displayState, awaiting };
}

/** 造一个 v12 帧（用于需要 store 走完整解析路径的倒计时用例）。 */
function v12Frame(awaiting: unknown): string {
  return JSON.stringify({ generation: 1, protocolVersion: 12, diff: null, awaiting });
}

describe('TinputCountdown: 可打断的定时等待 → 「跳过」提示', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('interruptible=true（AWAIT t）→ 渲染跳过按钮；点击提交空输入提前结束等待', async () => {
    const { wrapper, game, conn } = mountCountdown();
    setAwaiting(game, { wait: { durationMs: 8000, interruptible: true } });
    await wrapper.vm.$nextTick();

    const btn = wrapper.find('.tinput-skip');
    expect(btn.exists()).toBe(true);
    expect(btn.text()).toBe('跳过');

    await btn.trigger('click');
    expect(conn.sendInput).toHaveBeenCalledWith('');
    // 乐观锁：提交后按钮禁用，防连点
    expect(game.inputInFlight).toBe(true);
    expect(wrapper.find('.tinput-skip').attributes('disabled')).toBeDefined();
  });

  it('interruptible 缺省（TWAIT t,1）→ 不给跳过按钮（该键不出现即不可打断）', async () => {
    const { wrapper, game } = mountCountdown();
    setAwaiting(game, { wait: { durationMs: 8000 } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tinput-skip').exists()).toBe(false);
  });

  it('旁观（canInput=false）→ 跳过按钮禁用，点击不提交', async () => {
    const { wrapper, game, conn } = mountCountdown();
    conn.controller = { kind: 'agent' };
    setAwaiting(game, { wait: { durationMs: 8000, interruptible: true } });
    await wrapper.vm.$nextTick();

    const btn = wrapper.find('.tinput-skip');
    expect(btn.attributes('disabled')).toBeDefined();
    await btn.trigger('click');
    expect(conn.sendInput).not.toHaveBeenCalled();
  });

  it('零时长让路（durationMs=0）→ 既无倒计时也无跳过按钮（整个覆盖层不渲染）', async () => {
    const { wrapper, game } = mountCountdown();
    setAwaiting(game, { wait: { durationMs: 0 } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.tinput-countdown').exists()).toBe(false);
  });
});

describe('TinputCountdown: 倒计时（TINPUT）', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('带上界的等待 + displayTime=true → 渲染进度条与剩余秒数，且无跳过按钮（TINPUT 不可打断）', async () => {
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
