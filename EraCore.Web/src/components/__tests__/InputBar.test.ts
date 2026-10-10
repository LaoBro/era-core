// @vitest-environment happy-dom
import { describe, it, expect, beforeEach, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import InputBar from '../InputBar.vue';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import type { AwaitInfo } from '../../types/protocol';

/**
 * InputBar 验收（issue 08）：整条分支链按 v12 契约 `awaiting` 分派。
 *
 * 覆盖：
 * - `awaiting.input` 非空 ⇒ 按 `input.type` 渲染 5 种输入 UI（含值输入禁止空提交）
 * - `awaiting.ended` 非空 ⇒ Quit / Error 终态行（错误文案取 `error`）
 * - `awaiting` 为空 ⇒ 版本兼容期兜底（等待连接）
 *
 * 🔒 关键闸门：旧「脚本正在执行」（Running）分支**已删除**——定时等待 / 让路不再渲染
 * 任何「游戏运行中」加载行（帧只在脚本停稳时产出，该态在 v12 契约里没有落点）。
 */

function mountInputBar() {
  const pinia = createPinia();
  setActivePinia(pinia);
  const game = useGameStore();
  const conn = useConnectionStore();
  conn.status = 'connected';
  conn.sendInput = vi.fn();
  const wrapper = mount(InputBar, { global: { plugins: [pinia] } });
  return { wrapper, game, conn };
}

/** 直接写 awaiting（绕过协议解析——本组件只读派生量）。 */
function setAwaiting(game: ReturnType<typeof useGameStore>, awaiting: AwaitInfo): void {
  game.displayState = { ...game.displayState, awaiting };
}

/** 造一个 v12 帧（走完整协议解析路径：用于终态 + error 文案）。 */
function v12Frame(extra: Record<string, unknown>): string {
  return JSON.stringify({ generation: 1, protocolVersion: 12, diff: null, ...extra });
}

describe('InputBar: awaiting.input ⇒ 输入 UI 分派', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('IntValue → 输入框 + 「发送」；禁止空提交（发送按钮禁用）', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, { input: { type: 'IntValue' } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.input-row').exists()).toBe(true);
    expect(wrapper.find('.text-input').attributes('placeholder')).toBe('输入数字…');
    expect(wrapper.find('.submit-btn').text()).toBe('发送');
    // 空值 + 值输入类型 ⇒ 禁用（blockEmpty）
    expect(wrapper.find('.submit-btn').attributes('disabled')).toBeDefined();
  });

  it('AnyValue → 允许空提交（发送按钮可用）', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, { input: { type: 'AnyValue' } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.text-input').attributes('placeholder')).toBe('输入文本…');
    expect(wrapper.find('.submit-btn').text()).toBe('发送');
    expect(wrapper.find('.submit-btn').attributes('disabled')).toBeUndefined();
  });

  it('AnyKey → 状态行「按任意键继续」+「继续」，不渲染文本输入框', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, { input: { type: 'AnyKey' } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.text-input').exists()).toBe(false);
    expect(wrapper.find('.key-prompt').text()).toContain('按任意键继续');
    expect(wrapper.find('.submit-btn').text()).toBe('继续');
  });

  it('EnterKey → 状态行「按回车继续」+「继续」', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, { input: { type: 'EnterKey' } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.key-prompt').text()).toContain('按回车继续');
    expect(wrapper.find('.submit-btn').text()).toBe('继续');
  });

  it('未知 input.type → 兜底提示行（不静默丢弃）', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, { input: { type: 'IntButton' } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.unknown-input-type').text()).toContain('IntButton');
  });
});

describe('InputBar: awaiting.ended ⇒ 终态行', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('terminal=Quit → 「游戏结束」', async () => {
    const { wrapper, game } = mountInputBar();
    game.applyTurn(v12Frame({ awaiting: { ended: { terminal: 'Quit' } } }));
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.state-quit').text()).toContain('游戏结束');
    expect(wrapper.find('.input-row').exists()).toBe(false);

    game.reset();
  });

  it('terminal=Error → 「错误：<error>」（文案取帧的 error 字段）', async () => {
    const { wrapper, game } = mountInputBar();
    game.applyTurn(v12Frame({ error: '脚本崩了', awaiting: { ended: { terminal: 'Error' } } }));
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.state-error').text()).toContain('错误：脚本崩了');

    game.reset();
  });

  it('terminal=Error 无 error 文案 → 兜底「未知错误」', async () => {
    const { wrapper, game } = mountInputBar();
    game.applyTurn(v12Frame({ awaiting: { ended: { terminal: 'Error' } } }));
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.state-error').text()).toContain('未知错误');

    game.reset();
  });
});

describe('InputBar: 两路之外不渲染（兼容兜底已随收缩删除）+ 死分支不存在', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('awaiting 为空 → 不渲染任何内容（挂载条件保证；「等待连接」兜底已在 09 删除）', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, {});
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.state-idle').exists()).toBe(false);
    expect(wrapper.find('.input-row').exists()).toBe(false);
    expect(wrapper.find('.key-prompt').exists()).toBe(false);
  });

  it('🔒 定时等待（只有 wait）→ 不渲染「游戏运行中」加载行（该分支与 DisplayState.state 均已删除）', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, { wait: { durationMs: 8000 } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.state-running').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('游戏运行中');
    // 也不是输入态
    expect(wrapper.find('.input-row').exists()).toBe(false);
    expect(wrapper.find('.key-prompt').exists()).toBe(false);
  });

  it('🔒 零时长让路（durationMs=0）→ 同样无加载行、无输入 UI', async () => {
    const { wrapper, game } = mountInputBar();
    setAwaiting(game, { wait: { durationMs: 0 } });
    await wrapper.vm.$nextTick();

    expect(wrapper.find('.state-running').exists()).toBe(false);
    expect(wrapper.find('.input-row').exists()).toBe(false);
    expect(wrapper.find('.key-prompt').exists()).toBe(false);
  });
});
