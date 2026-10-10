import { describe, it, expect, beforeEach } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';
import type { DisplaySnapshot } from '../../types/protocol';

/**
 * 状态层持有 `awaiting`（issue 06；issue 09 收缩后复审）。
 *
 * v12 起 wire 上只剩 `awaiting`——状态层不再承载 `state` / `inputType` / `needValue`，
 * 七个旧顶层字段已退场（连「旧字段与 awaiting 冲突」这种用例都不再可能构造）。
 * 本组用例锁定：状态层把 `awaiting` 原样装进来，`serverState` 由它派生，
 * 倒计时 / 超时通知全部读 `awaiting.wait`，快照与流上帧同形。
 *
 * 🔴 issue 10 撤回：「跳过」派生量 `canSkipWait` 已随 `wait.interruptible` 退场
 * （spec §10.1：定时等待一律不可打断），本组不再有与之相关的用例。
 */

/** 造一个 v12 WS 帧（`awaiting` 必填）。 */
function v12Frame(awaiting: unknown): string {
  return JSON.stringify({ generation: 5, protocolVersion: 12, diff: null, awaiting });
}

describe('useGameStore - v12 awaiting（状态层装载）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('装载 awaiting（唯一契约字段），无任何派生副本', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame({ input: { type: 'StrValue' } }));

    expect(game.awaiting).toEqual({ input: { type: 'StrValue' } });
    expect(game.displayState.awaiting).toEqual({ input: { type: 'StrValue' } });
    // 旧的派生副本字段已随收缩退场
    expect('inputType' in game.displayState).toBe(false);
    expect('needValue' in game.displayState).toBe(false);
    expect('state' in game.displayState).toBe(false);
  });

  it('零时长让路帧：durationMs=0 被原样持有（客户端理解为「下一帧立刻到」）', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame({ wait: { durationMs: 0 } }));

    expect(game.awaiting.wait!.durationMs).toBe(0);
    expect(game.awaiting.input).toBeUndefined();
  });

  it('终态帧：ended.terminal 可读，且两维为空', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame({ ended: { terminal: 'Quit' } }));

    expect(game.awaiting.ended!.terminal).toBe('Quit');
    expect(game.awaiting.input).toBeUndefined();
    expect(game.awaiting.wait).toBeUndefined();
  });

  it('后续帧缺 awaiting 也抛（v12 起无版本双轨，缺失即拒）', () => {
    const game = useGameStore();
    game.applyTurn(v12Frame({ input: { type: 'AnyKey' } }));
    expect(game.protocolVersion).toBe(12);

    // 后续帧（C# 仅首帧带 protocolVersion）
    game.applyTurn(JSON.stringify({ generation: 6, diff: null }));

    expect(game.lastError).toContain('awaiting');
    // 解析失败不改画面
    expect(game.awaiting).toEqual({ input: { type: 'AnyKey' } });
  });

  it('reset()：awaiting 回到空闲形态（三键皆无）', () => {
    const game = useGameStore();
    game.applyTurn(v12Frame({ input: { type: 'AnyKey' } }));
    expect(game.awaiting).toEqual({ input: { type: 'AnyKey' } });

    game.reset();

    expect(game.awaiting).toEqual({});
  });
});

describe('useGameStore - 倒计时判据（读 awaiting.wait）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('wait.durationMs>0 → 启动倒计时（起始时刻/时限/是否显示/超时文案全部来自 awaiting.wait）', () => {
    const game = useGameStore();
    const start = Date.now();

    game.applyTurn(v12Frame({
      input: { type: 'IntValue' },
      wait: { durationMs: 3000, displayTime: true, timeUpMessage: '快选择' },
    }));

    expect(game.tinputStartedAt).not.toBeNull();
    expect(game.tinputStartedAt!).toBeGreaterThanOrEqual(start);
    expect(game.tinputTimeLimit).toBe(3000);
    expect(game.tinputDisplayTime).toBe(true);
    expect(game.tinputTimeUpMessage).toBe('快选择');
    expect(game.showTinputCountdown).toBe(true);
    expect(game.tinputRemainingMs!).toBeGreaterThan(2900);

    game.reset();
  });

  it('零时长让路（durationMs=0）→ 不启动倒计时', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame({ wait: { durationMs: 0 } }));

    expect(game.tinputStartedAt).toBeNull();
    expect(game.tinputTimeLimit).toBeNull();
    expect(game.showTinputCountdown).toBe(false);
  });

  it('🔒 定时等待帧不再携带 interruptible（AWAIT t 与 TWAIT t,1 同形）', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame({ wait: { durationMs: 8000 } }));

    expect(game.awaiting.wait).toEqual({
      durationMs: 8000,
      displayTime: null,
      timeUpMessage: null,
      timedOut: null,
    });
    expect(game.awaiting.wait).not.toHaveProperty('interruptible');
  });

  it('超时帧：wait.timedOut=true → timeoutNotice 用 wait.timeUpMessage（缺省用默认文案）', () => {
    const game = useGameStore();

    game.applyTurn(v12Frame(
      { input: { type: 'IntValue' }, wait: { durationMs: 3000, timedOut: true } },
    ));
    expect(game.timeoutNotice).toBe('TINPUT 超时，游戏以默认值继续');

    game.applyTurn(v12Frame(
      { input: { type: 'IntValue' }, wait: { durationMs: 3000, timedOut: true, timeUpMessage: '時間切れ' } },
    ));
    expect(game.timeoutNotice).toBe('時間切れ');

    game.reset();
  });

  it('定时等待结束（下一帧无 wait）→ 倒计时清空', () => {
    const game = useGameStore();
    game.applyTurn(v12Frame({ wait: { durationMs: 8000, displayTime: true } }));
    expect(game.showTinputCountdown).toBe(true);

    game.applyTurn(v12Frame({ input: { type: 'AnyKey' } }));

    expect(game.showTinputCountdown).toBe(false);
    expect(game.tinputStartedAt).toBeNull();
  });
});

describe('useGameStore.setSnapshot - v12 awaiting（晚加入者与流上帧同形）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('快照的 awaiting 原样进入状态层（与流上帧同一形状、同一工厂产出）', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      bgColor: null,
      awaiting: { input: { type: 'IntValue' }, wait: { durationMs: 3000, displayTime: true } },
      protocolVersion: 12,
      generation: 3,
    };

    game.setSnapshot(snap);

    expect(game.awaiting).toEqual({
      input: { type: 'IntValue' },
      wait: { durationMs: 3000, displayTime: true },
    });
    expect(game.protocolVersion).toBe(12);
  });

  it('契约要求 awaiting 必填：快照类型上已无旧字段可兜底', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      bgColor: null,
      awaiting: { ended: { terminal: 'Quit' } },
      protocolVersion: 12,
      generation: 0,
    };

    game.setSnapshot(snap);

    expect(game.awaiting).toEqual({ ended: { terminal: 'Quit' } });
  });
});
