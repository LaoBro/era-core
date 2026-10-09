import { describe, it, expect } from 'vitest';
import {
  isAnyKeyInput,
  isLiveButton,
  shouldSubmitButtonValue,
  shouldAdvanceOnTerminalClick,
  type ButtonClickContext,
  type TerminalClickContext,
} from '../inputRouting';
import type { AwaitInfo } from '../../types/protocol';

/**
 * inputRouting 单测——"点击→行为"路由判定（spec.md 用户故事 9 / 输入守卫）。
 *
 * 回归锚点：EnterKey/AnyKey（任意键/回车等待）下，点击已存在按钮——
 * - 不应提交按钮 value（shouldSubmitButtonValue=false）→ 游戏不会多回显一行输入；
 * - 且应推进游戏（shouldAdvanceOnTerminalClick=true）→ 点击按钮区域与空白区行为一致，
 *   同时修复过期按钮死区。
 * 与 CLI / WinForms 行为一致：该态下按钮是惰性的。
 *
 * issue 07：判据从「引擎状态 + 输入种类」改为直接读契约 `awaiting`——
 * 本节所有上下文都用 `awaiting` 构造，`state` 不再参与判定（新增 `inputAwait` 夹具）。
 */

/** 造一个「期待客户端输入」的 awaiting（v12 `input` 维度）。 */
function inputAwait(type: string): AwaitInfo {
  return { input: { type } };
}

/** 无任何维度（空闲 / 瞬态）——旧模型里对应 state 既不是 WaitInput 也不是终态。 */
const NO_AWAIT: AwaitInfo = {};

/** 终态（Quit / Error）——旧模型里对应 state === 'Quit' / 'Error'。 */
const ENDED: AwaitInfo = { ended: { terminal: 'Quit' } };

describe('isAnyKeyInput——任意键/回车等待态判定', () => {
  it('EnterKey / AnyKey → true', () => {
    expect(isAnyKeyInput('EnterKey')).toBe(true);
    expect(isAnyKeyInput('AnyKey')).toBe(true);
  });

  it('IntValue/StrValue/AnyValue/IntButton/StrButton → false', () => {
    expect(isAnyKeyInput('IntValue')).toBe(false);
    expect(isAnyKeyInput('StrValue')).toBe(false);
    expect(isAnyKeyInput('AnyValue')).toBe(false);
    expect(isAnyKeyInput('IntButton')).toBe(false);
    expect(isAnyKeyInput('StrButton')).toBe(false);
  });

  it('null / undefined → false', () => {
    expect(isAnyKeyInput(null)).toBe(false);
    expect(isAnyKeyInput(undefined)).toBe(false);
  });
});

describe('isLiveButton——按钮是否存活（排版是否为其撑高）', () => {
  it('当前回合 + 非惰性输入态 → 存活', () => {
    expect(isLiveButton({ awaiting: inputAwait('IntButton'), buttonGeneration: 3, currentTurnGeneration: 3 })).toBe(true);
  });

  it('过期按钮（generation 落后）→ 不存活', () => {
    expect(isLiveButton({ awaiting: inputAwait('IntButton'), buttonGeneration: 2, currentTurnGeneration: 3 })).toBe(false);
  });

  it('惰性态（EnterKey / AnyKey）→ 不存活', () => {
    expect(isLiveButton({ awaiting: inputAwait('EnterKey'), buttonGeneration: 3, currentTurnGeneration: 3 })).toBe(false);
    expect(isLiveButton({ awaiting: inputAwait('AnyKey'), buttonGeneration: 3, currentTurnGeneration: 3 })).toBe(false);
  });

  it('服务端不期待输入（定时等待 / 让路 / 终态）→ 不存活', () => {
    for (const awaiting of [NO_AWAIT, ENDED, { wait: { durationMs: 8000 } } as AwaitInfo, { wait: { durationMs: 0 } } as AwaitInfo]) {
      expect(isLiveButton({ awaiting, buttonGeneration: 3, currentTurnGeneration: 3 })).toBe(false);
    }
  });
});

describe('shouldSubmitButtonValue——点击按钮是否提交 value', () => {
  const base: ButtonClickContext = {
    connected: true,
    awaiting: inputAwait('IntButton'),
    buttonGeneration: 1,
    currentTurnGeneration: 1,
    inputInFlight: false,
  };

  it('正常按钮输入态（IntButton）→ 提交', () => {
    expect(shouldSubmitButtonValue(base)).toBe(true);
    expect(shouldSubmitButtonValue({ ...base, awaiting: inputAwait('StrButton') })).toBe(true);
  });

  it('EnterKey 等待态 + 当前代按钮 → 不提交（回归锚点：避免多回显一行输入）', () => {
    expect(shouldSubmitButtonValue({ ...base, awaiting: inputAwait('EnterKey') })).toBe(false);
  });

  it('AnyKey 等待态 + 当前代按钮 → 不提交（回归锚点）', () => {
    expect(shouldSubmitButtonValue({ ...base, awaiting: inputAwait('AnyKey') })).toBe(false);
  });

  it('EnterKey 等待态 + 过期按钮 → 不提交（按钮本就惰性，不再额外区分 generation）', () => {
    expect(shouldSubmitButtonValue({
      ...base,
      awaiting: inputAwait('EnterKey'),
      buttonGeneration: 0,
      currentTurnGeneration: 1,
    })).toBe(false);
  });

  it('过期按钮（IntButton，generation 不匹配）→ 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, buttonGeneration: 0, currentTurnGeneration: 1 })).toBe(false);
  });

  it('服务端不期待输入（定时等待 / 让路 / 终态 / 空闲）→ 不提交', () => {
    // issue 07 语义修正：旧判据是 state !== 'WaitInput'；新判据直接读 awaiting.input，
    // 于是 WaitInput + Void（AWAIT / TWAIT）这类「其实不收输入」的停稳点也不再放行按钮。
    for (const awaiting of [NO_AWAIT, ENDED, { wait: { durationMs: 8000 } } as AwaitInfo, { wait: { durationMs: 0 } } as AwaitInfo]) {
      expect(shouldSubmitButtonValue({ ...base, awaiting })).toBe(false);
    }
  });

  it('未连接 → 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, connected: false })).toBe(false);
  });

  it('inputInFlight（乐观锁）→ 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, inputInFlight: true })).toBe(false);
  });

  it('旁观中（canInput=false）→ 不提交', () => {
    expect(shouldSubmitButtonValue({ ...base, canInput: false })).toBe(false);
  });
});

describe('shouldAdvanceOnTerminalClick——点击终端是否推进', () => {
  const base: TerminalClickContext = {
    isButton: false,
    isStickyToBottom: true,
    awaiting: inputAwait('EnterKey'),
    connected: true,
    inputInFlight: false,
  };

  it('空白区 + EnterKey → 推进', () => {
    expect(shouldAdvanceOnTerminalClick(base)).toBe(true);
  });

  it('空白区 + AnyKey → 推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, awaiting: inputAwait('AnyKey') })).toBe(true);
  });

  it('按钮区 + EnterKey → 推进（回归锚点：任意键态按钮点击同样推进，不成为死区）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true })).toBe(true);
  });

  it('按钮区 + AnyKey → 推进（回归锚点）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, awaiting: inputAwait('AnyKey') })).toBe(true);
  });

  it('按钮区 + 非任意键态（IntButton/IntValue）→ 不推进（按钮有独立 @click，终端跳过）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, awaiting: inputAwait('IntButton') })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, awaiting: inputAwait('IntValue') })).toBe(false);
  });

  it('空白区 + 非任意键态（IntButton/IntValue/AnyValue）→ 不推进（输入由按钮/输入栏承担）', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, awaiting: inputAwait('IntButton') })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, awaiting: inputAwait('IntValue') })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, awaiting: inputAwait('AnyValue') })).toBe(false);
  });

  it('服务端不期待输入（定时等待 / 让路 / 终态 / 空闲）→ 不推进', () => {
    for (const awaiting of [NO_AWAIT, ENDED, { wait: { durationMs: 8000, interruptible: true } } as AwaitInfo, { wait: { durationMs: 0 } } as AwaitInfo]) {
      expect(shouldAdvanceOnTerminalClick({ ...base, awaiting })).toBe(false);
      expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, awaiting })).toBe(false);
    }
  });

  it('isStickyToBottom=false（翻看历史）→ 不推进，按钮区同样不推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, isStickyToBottom: false })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, isButton: true, isStickyToBottom: false })).toBe(false);
  });

  it('未连接 / inputInFlight → 不推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, connected: false })).toBe(false);
    expect(shouldAdvanceOnTerminalClick({ ...base, inputInFlight: true })).toBe(false);
  });

  it('旁观中（canInput=false）→ 不推进', () => {
    expect(shouldAdvanceOnTerminalClick({ ...base, canInput: false })).toBe(false);
  });
});

describe('EnterKey/AnyKey 态点击路由契约——两个判定协同', () => {
  it('任意键态点按钮：不提交 value（false）+ 推进（true）——行为统一为"惰性按钮 + 推进"', () => {
    for (const type of ['EnterKey', 'AnyKey'] as const) {
      const awaiting = inputAwait(type);
      // 按钮 @click：不提交
      expect(shouldSubmitButtonValue({
        connected: true,
        awaiting,
        buttonGeneration: 5,
        currentTurnGeneration: 5,
        inputInFlight: false,
      })).toBe(false);
      // 终端 @click（冒泡）：推进
      expect(shouldAdvanceOnTerminalClick({
        isButton: true,
        isStickyToBottom: true,
        awaiting,
        connected: true,
        inputInFlight: false,
      })).toBe(true);
    }
  });
});
