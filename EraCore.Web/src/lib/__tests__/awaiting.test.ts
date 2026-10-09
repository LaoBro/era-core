import { describe, it, expect } from 'vitest';
import { parseTurnRecord, ParseTurnRecordError } from '../parseTurnRecord';
import { awaitingFromLegacyFields, isValueInputType } from '../awaiting';

/**
 * v12 `awaiting` 解析验收（issue 06）。
 *
 * 契约权威：C# `EraCore.Core/Agent/AwaitInfo.cs` + `docs/guides/turn-protocol.md` §2。
 * 结构 =「两个正交维度（input / wait）+ 一个互斥终态键（ended）」，故本组用例把规格里
 * 列出的**全部 7 种形态**逐一钉住（含零时长让路的 `durationMs:0` 与瞬态 `{}`）。
 *
 * 为什么要在 TS 侧重新钉一遍（而不是「C# 已测过」）：前端是**独立消费方**——
 * `AwaitInfo` 落到 JS 后字段可选性语义（`WhenWritingNull`）与键出现与否，都只由解析器保证。
 */

/** 造一个 v12 帧：`awaiting` 为必填，其余旧顶层字段故意留空以证明它们不再被依赖。 */
function v12Frame(awaiting: unknown, extra: Record<string, unknown> = {}): string {
  return JSON.stringify({
    generation: 7,
    protocolVersion: 12,
    diff: null,
    awaiting,
    ...extra,
  });
}

describe('parseTurnRecord - v12 awaiting 形态', () => {
  it('① 纯输入等待（WAIT / INPUT）：input.type，无 wait', () => {
    const turn = parseTurnRecord(v12Frame({ input: { type: 'EnterKey' } }));
    expect(turn.awaiting).toEqual({ input: { type: 'EnterKey' } });
    expect(turn.awaiting.wait).toBeUndefined();
    expect(turn.awaiting.ended).toBeUndefined();
  });

  it('② TINPUT：两个维度同现（既等输入、又有期限）+ 倒计时元数据', () => {
    const turn = parseTurnRecord(v12Frame({
      input: { type: 'IntValue' },
      wait: {
        durationMs: 3000,
        displayTime: true,
        timeUpMessage: '時間切れ',
        timedOut: true,
        interruptible: true,
      },
    }));
    expect(turn.awaiting.input).toEqual({ type: 'IntValue' });
    expect(turn.awaiting.wait).toEqual({
      durationMs: 3000,
      interruptible: true,
      displayTime: true,
      timeUpMessage: '時間切れ',
      timedOut: true,
    });
  });

  it('③ AWAIT 8000：纯定时等待 + interruptible=true（可打断 ⇒ 给「跳过」affordance）', () => {
    const turn = parseTurnRecord(v12Frame({ wait: { durationMs: 8000, interruptible: true } }));
    expect(turn.awaiting.input).toBeUndefined();
    expect(turn.awaiting.wait!.durationMs).toBe(8000);
    expect(turn.awaiting.wait!.interruptible).toBe(true);
  });

  it('④ TWAIT 8000,1：不可打断 ⇒ interruptible 键**不出现**（不是字面量 false）', () => {
    const turn = parseTurnRecord(v12Frame({ wait: { durationMs: 8000 } }));
    expect(turn.awaiting.wait!.interruptible ?? null).toBeNull();
    expect(turn.awaiting.wait!.interruptible).not.toBe(false);
  });

  it('⑤ 零时长让路（无参 AWAIT / AWAIT 0）：wait.durationMs=0，wire 上最简形态', () => {
    const turn = parseTurnRecord(v12Frame({ wait: { durationMs: 0 } }));
    expect(turn.awaiting.wait).toEqual({
      durationMs: 0,
      interruptible: null,
      displayTime: null,
      timeUpMessage: null,
      timedOut: null,
    });
  });

  it('⑥ 终态：ended.terminal，与 input / wait 互斥', () => {
    const turn = parseTurnRecord(v12Frame({ ended: { terminal: 'Quit' } }));
    expect(turn.awaiting).toEqual({ ended: { terminal: 'Quit' } });
    expect(turn.awaiting.input).toBeUndefined();
    expect(turn.awaiting.wait).toBeUndefined();
  });

  it('⑦ 瞬态 `{}`：两维皆无（脚本还在跑时取到快照的情形），由 state 区分', () => {
    const turn = parseTurnRecord(v12Frame({}, { state: 'Running' }));
    expect(turn.awaiting).toEqual({});
  });

  it('awaiting 为空对象以外的非对象值抛 ParseTurnRecordError', () => {
    expect(() => parseTurnRecord(v12Frame([]))).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord(v12Frame('input'))).toThrow(ParseTurnRecordError);
  });

  it('形状校验：input 缺 type / wait 缺 durationMs / ended 缺 terminal 各自抛错', () => {
    expect(() => parseTurnRecord(v12Frame({ input: {} }))).toThrow(/awaiting\.input\.type/);
    expect(() => parseTurnRecord(v12Frame({ wait: { interruptible: true } }))).toThrow(/awaiting\.wait\.durationMs/);
    expect(() => parseTurnRecord(v12Frame({ ended: {} }))).toThrow(/awaiting\.ended\.terminal/);
  });

  it('wait.timedOut 挂在 wait 上（不在顶层）——超时续跑是一次性旗标', () => {
    const turn = parseTurnRecord(v12Frame({ wait: { durationMs: 5000, timedOut: true } }, { timedOut: true }));
    expect(turn.awaiting.wait!.timedOut).toBe(true);
  });
});

describe('parseTurnRecord - errorInfo（issue 06 补齐的半接线头）', () => {
  it('解析结构化异常：type + message + 嵌套 inner', () => {
    const json = JSON.stringify({
      generation: 0,
      protocolVersion: 12,
      awaiting: { ended: { terminal: 'Error' } },
      error: 'StepAsync fatal: boom',
      errorInfo: {
        type: 'System.InvalidOperationException',
        message: 'boom',
        inner: { type: 'System.IO.IOException', message: 'disk' },
      },
    });
    const turn = parseTurnRecord(json);
    expect(turn.errorInfo).toEqual({
      type: 'System.InvalidOperationException',
      message: 'boom',
      inner: { type: 'System.IO.IOException', message: 'disk' },
    });
  });

  it('非异常帧不带 errorInfo ⇒ null；形状不合法则抛（不静默丢弃）', () => {
    expect(parseTurnRecord(v12Frame({ input: { type: 'EnterKey' } })).errorInfo).toBeNull();
    expect(() => parseTurnRecord(v12Frame({}, { errorInfo: { type: 'X' } }))).toThrow(/errorInfo\.message/);
  });
});

describe('awaitingFromLegacyFields / isValueInputType（v11 兼容垫片）', () => {
  it('isValueInputType：只有 IntValue / StrValue 需要值输入', () => {
    expect(isValueInputType('IntValue')).toBe(true);
    expect(isValueInputType('StrValue')).toBe(true);
    expect(isValueInputType('AnyKey')).toBe(false);
    expect(isValueInputType('EnterKey')).toBe(false);
    expect(isValueInputType(null)).toBe(false);
    expect(isValueInputType(undefined)).toBe(false);
  });

  it('终态：Quit / Error ⇒ ended，且不合成 input / wait', () => {
    expect(awaitingFromLegacyFields({ state: 'Quit', inputType: 'IntValue', timeLimit: 5000 }))
      .toEqual({ ended: { terminal: 'Quit' } });
    expect(awaitingFromLegacyFields({ state: 'Error' })).toEqual({ ended: { terminal: 'Error' } });
  });

  it('TINPUT：input + wait（displayTime / timeUpMessage / timedOut 仅在为真时出现）', () => {
    expect(awaitingFromLegacyFields({
      state: 'WaitInput',
      inputType: 'IntValue',
      timeLimit: 3000,
      displayTime: true,
      timeUpMessage: '時間切れ',
      timedOut: true,
    })).toEqual({
      input: { type: 'IntValue' },
      wait: { durationMs: 3000, displayTime: true, timeUpMessage: '時間切れ', timedOut: true },
    });
  });

  it('inputType="Void" 不产 input 维度（AWAIT / TWAIT 在旧 wire 上只能这样表达）', () => {
    expect(awaitingFromLegacyFields({ state: 'WaitInput', inputType: 'Void', timeLimit: 8000 }))
      .toEqual({ wait: { durationMs: 8000 } });
  });

  it('两维皆无（Running / 空闲）⇒ {}；旧 wire 的零时长让路不可表达（固有损失）', () => {
    expect(awaitingFromLegacyFields({ state: 'Running', inputType: null })).toEqual({});
    // 旧 wire 只在 Timelimit>0 时下发 timeLimit ⇒ timeLimit=null 合成不出 durationMs:0
    expect(awaitingFromLegacyFields({ state: 'WaitInput', inputType: 'Void', timeLimit: null })).toEqual({});
  });
});
