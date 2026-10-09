import { describe, it, expect, beforeEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';
import type { DisplaySnapshot } from '../../types/protocol';

/**
 * 状态层持有 `awaiting`（issue 06）。
 *
 * 本票**不切换任何消费者**（迁移见 issue 07 / 08），但状态层必须先从「反向推断」换成
 * 「直接读契约」：`displayState.awaiting` 恒非 null，`inputType` / `needValue` 由其派生。
 *
 * 最能说明收益的一条：让帧的**旧顶层字段与 awaiting 故意冲突**，断言状态层以 awaiting 为准
 * ——这正是「旧的输入状态字段不再由帧的顶层字段覆盖」的可判定形式。
 */

/** 造一个 v12 WS 帧（`awaiting` 必填）。 */
function v12Frame(awaiting: unknown, extra: Record<string, unknown> = {}): string {
  return JSON.stringify({
    generation: 5,
    protocolVersion: 12,
    diff: null,
    awaiting,
    ...extra,
  });
}

describe('useGameStore - v12 awaiting（状态层装载）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('装载 awaiting，且 inputType / needValue 由 awaiting 派生（旧顶层字段冲突时以 awaiting 为准）', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame(
      { input: { type: 'StrValue' } },
      // 故意与 awaiting 冲突：旧字段说 Void / 不要值
      { state: 'WaitInput', inputType: 'Void', needValue: false },
    ));

    expect(game.awaiting).toEqual({ input: { type: 'StrValue' } });
    expect(game.displayState.awaiting).toEqual({ input: { type: 'StrValue' } });
    // 派生自 awaiting.input.type ⇒ 旧顶层字段的 'Void' / false 不再生效
    expect(game.displayState.inputType).toBe('StrValue');
    expect(game.displayState.needValue).toBe(true);
    // state 例外：awaiting 没有「游戏状态」维度，仍取顶层
    expect(game.displayState.state).toBe('WaitInput');
  });

  it('零时长让路帧：durationMs=0 被原样持有（客户端理解为「下一帧立刻到」）', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame({ wait: { durationMs: 0 } }, { state: 'WaitInput' }));

    expect(game.awaiting.wait!.durationMs).toBe(0);
    expect(game.displayState.inputType).toBeNull();
    expect(game.displayState.needValue).toBe(false);
  });

  it('终态帧：ended.terminal 可读，且两维为空', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame({ ended: { terminal: 'Quit' } }, { state: 'Quit' }));

    expect(game.awaiting.ended!.terminal).toBe('Quit');
    expect(game.awaiting.input).toBeUndefined();
    expect(game.displayState.inputType).toBeNull();
    expect(game.displayState.needValue).toBe(false);
  });

  it('sticky 版本生效：首帧装好 12 后，省略 protocolVersion 的后续帧缺 awaiting 也抛', () => {
    const game = useGameStore();
    game.applyTurn(v12Frame({ input: { type: 'AnyKey' } }, { state: 'WaitInput' }));
    expect(game.protocolVersion).toBe(12);

    // 后续帧：帧自带 protocolVersion 省略（C# 仅首帧带）⇒ 只能靠 sticky 判定为 v12
    game.applyTurn(JSON.stringify({ state: 'WaitInput', generation: 6, diff: null }));

    expect(game.lastError).toContain('awaiting');
    // 解析失败不改画面
    expect(game.awaiting).toEqual({ input: { type: 'AnyKey' } });
  });

  it('v11 兼容帧（无 awaiting）：由旧顶层字段合成，旧消费者读到的值不变', () => {
    const game = useGameStore();

    game.applyTurn(JSON.stringify({
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      generation: 0,
      timeLimit: 5000,
      displayTime: true,
      timedOut: false,
      diff: null,
    }));

    expect(game.awaiting).toEqual({
      input: { type: 'IntValue' },
      wait: { durationMs: 5000, displayTime: true },
    });
    expect(game.displayState.inputType).toBe('IntValue');
    expect(game.displayState.needValue).toBe(true);
  });

  it('reset()：awaiting 回到空闲形态（三键皆无）', () => {
    const game = useGameStore();
    game.applyTurn(v12Frame({ input: { type: 'AnyKey' } }, { state: 'WaitInput' }));
    expect(game.awaiting).toEqual({ input: { type: 'AnyKey' } });

    game.reset();

    expect(game.awaiting).toEqual({});
  });
});

describe('useGameStore.setSnapshot - v12 awaiting（晚加入者与流上帧同形）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('v12 快照：awaiting 原样进入状态层，inputType / needValue 由其派生', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      state: 'WaitInput',
      inputType: null,
      needValue: false,
      protocolVersion: 12,
      generation: 3,
      awaiting: { input: { type: 'IntValue' }, wait: { durationMs: 3000, displayTime: true } },
    };

    game.setSnapshot(snap);

    expect(game.awaiting).toEqual({
      input: { type: 'IntValue' },
      wait: { durationMs: 3000, displayTime: true },
    });
    expect(game.displayState.inputType).toBe('IntValue');
    expect(game.displayState.needValue).toBe(true);
    expect(game.protocolVersion).toBe(12);
  });

  it('v11 快照（无 awaiting）：由旧顶层字段合成同一形状', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      state: 'WaitInput',
      inputType: 'AnyKey',
      needValue: false,
      protocolVersion: 11,
      generation: 3,
    };

    game.setSnapshot(snap);

    expect(game.awaiting).toEqual({ input: { type: 'AnyKey' } });
    expect(game.displayState.inputType).toBe('AnyKey');
    expect(game.displayState.needValue).toBe(false);
  });

  it('v11 终态快照：ended 由 state 合成（旧消费者的 state 判断等价物）', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      state: 'Quit',
      inputType: null,
      needValue: false,
      protocolVersion: 11,
      generation: 0,
    };

    game.setSnapshot(snap);

    expect(game.awaiting).toEqual({ ended: { terminal: 'Quit' } });
  });
});
