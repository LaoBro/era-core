import { describe, it, expect } from 'vitest';
import { parseTurnRecord, ParseTurnRecordError } from '../parseTurnRecord';

/**
 * parseTurnRecord 单测——WS 帧原始 JSON 字符串解析为 TurnRecord。
 *
 * C# 端 `KestrelGameServer` 把 turn 序列化为 JSON 文本帧，前端必须能正确反序列化。
 * 此处验证：
 * - 合法 JSON → 结构化 TurnRecord（含 diff / lineOps 嵌套）
 * - 缺必填字段 → 抛 ParseTurnRecordError
 * - 未知 op type → 抛 ParseTurnRecordError（与 C# TestAdapter.ApplyDiff 抛 InvalidOperationException 对称）
 * - 非 JSON 字符串 → 抛 ParseTurnRecordError
 */
describe('parseTurnRecord', () => {
  it('解析最简 TurnRecord（v12 两个必填键：generation + awaiting）', () => {
    const json = JSON.stringify({ generation: 0, awaiting: { input: { type: 'EnterKey' } } });
    const turn = parseTurnRecord(json);
    expect(turn.awaiting).toEqual({ input: { type: 'EnterKey' } });
    expect(turn.diff).toBeNull();
    expect(turn.error).toBeNull();
    expect(turn.errorInfo).toBeNull();
    expect(turn.protocolVersion).toBeNull();
  });

  it('解析完整 TurnRecord（含 input+wait 两维 / diff / protocolVersion）', () => {
    const json = JSON.stringify({
      awaiting: {
        input: { type: 'IntValue' },
        wait: { durationMs: 3000, displayTime: true, timeUpMessage: '時間切れ' },
      },
      protocolVersion: 12,
      generation: 0,
      diff: {
        lineOps: [
          { type: 'append', newLines: [{ entries: [{ segments: [{ text: 'Hello' }] }], isLineEnd: true }] },
          { type: 'clear_line_diff', clearCount: 2 },
          { type: 'clear_screen' },
        ],
        bgColor: '#FF0000',
      },
    });
    const turn = parseTurnRecord(json);
    expect(turn.awaiting.input).toEqual({ type: 'IntValue' });
    expect(turn.awaiting.wait!.durationMs).toBe(3000);
    expect(turn.awaiting.wait!.displayTime).toBe(true);
    expect(turn.awaiting.wait!.timeUpMessage).toBe('時間切れ');
    expect(turn.protocolVersion).toBe(12);
    expect(turn.diff).not.toBeNull();
    expect(turn.diff!.bgColor).toBe('#FF0000');
    expect(turn.diff!.lineOps).toHaveLength(3);

    // lineOps[0]: append + 1 行 + 1 entry + 1 segment
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('append');
    if (op0.type !== 'append') throw new Error('unreachable');
    expect(op0.newLines).toHaveLength(1);
    expect(op0.newLines[0].entries).toHaveLength(1);
    expect(op0.newLines[0].entries[0].segments).toHaveLength(1);
    expect(op0.newLines[0].entries[0].segments[0].text).toBe('Hello');
    expect(op0.newLines[0].isLineEnd).toBe(true);

    // lineOps[1]: clear_line_diff + clearCount=2
    const op1 = turn.diff!.lineOps[1];
    expect(op1.type).toBe('clear_line_diff');
    if (op1.type !== 'clear_line_diff') throw new Error('unreachable');
    expect(op1.clearCount).toBe(2);

    // lineOps[2]: clear_screen（无其他字段）
    expect(turn.diff!.lineOps[2].type).toBe('clear_screen');
  });

  it('解析 error 帧（终态 awaiting + error 文案）', () => {
    const json = JSON.stringify({
      awaiting: { ended: { terminal: 'Error' } },
      generation: 0,
      error: 'EraBrowser load failed',
    });
    const turn = parseTurnRecord(json);
    expect(turn.awaiting.ended!.terminal).toBe('Error');
    expect(turn.error).toBe('EraBrowser load failed');
  });

  it('解析带 ButtonRef 的 entry（value 为 number/string 两种）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [
                  {
                    segments: [{ text: '[OK]' }],
                    button: { value: 1, isInteger: true, col: 0, width: 4, generation: 0 },
                  },
                  {
                    segments: [{ text: '[Cancel]' }],
                    button: { value: 'cancel', isInteger: false, col: 4, width: 8, generation: 0 },
                  },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('append');
    if (op0.type !== 'append') throw new Error('unreachable');
    const entry0 = op0.newLines[0].entries[0];
    expect(entry0.button!.value).toBe(1);
    expect(entry0.button!.isInteger).toBe(true);
    expect(entry0.button!.col).toBe(0);
    expect(entry0.button!.width).toBe(4);

    const entry1 = op0.newLines[0].entries[1];
    expect(entry1.button!.value).toBe('cancel');
    expect(entry1.button!.isInteger).toBe(false);
    expect(entry1.button!.col).toBe(4);
    expect(entry1.button!.width).toBe(8);
  });

  it('解析 align 字段（left/center/right/null）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              { entries: [{ segments: [{ text: 'left' }] }], align: 'left', isLineEnd: true },
              { entries: [{ segments: [{ text: 'center' }] }], align: 'center', isLineEnd: true },
              { entries: [{ segments: [{ text: 'right' }] }], align: 'right', isLineEnd: true },
              { entries: [{ segments: [{ text: 'default' }] }], isLineEnd: true },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('append');
    if (op0.type !== 'append') throw new Error('unreachable');
    const lines = op0.newLines;
    expect(lines[0].align).toBe('left');
    expect(lines[1].align).toBe('center');
    expect(lines[2].align).toBe('right');
    expect(lines[3].align).toBeNull();
  });

  it('非 JSON 字符串抛 ParseTurnRecordError', () => {
    expect(() => parseTurnRecord('not a json')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('{')).toThrow(ParseTurnRecordError);
  });

  it('顶层非对象抛 ParseTurnRecordError', () => {
    expect(() => parseTurnRecord('123')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('"string"')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('null')).toThrow(ParseTurnRecordError);
    expect(() => parseTurnRecord('[]')).toThrow(ParseTurnRecordError);
  });

  it('缺 generation 字段抛 ParseTurnRecordError（v7 起每帧必带，不接受降级）', () => {
    expect(() => parseTurnRecord(JSON.stringify({ awaiting: { input: { type: 'EnterKey' } } }))).toThrow(ParseTurnRecordError);
  });

  it('缺 awaiting 抛 ParseTurnRecordError（v12 唯一契约字段，缺失即抛；不再有版本双轨）', () => {
    const json = JSON.stringify({
      generation: 0,
      // 旧顶层字段即使全都在，也合成不出 awaiting
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      protocolVersion: 12,
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  it('未知 LineOp type 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      generation: 0,
      awaiting: { input: { type: 'EnterKey' } },
      diff: {
        lineOps: [{ type: 'unknown_op', foo: 'bar' }],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  it('lineOps 非数组抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      generation: 0,
      awaiting: { input: { type: 'EnterKey' } },
      diff: { lineOps: 'not_an_array' },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  it('ButtonRef.value 既非 number 也非 string 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      generation: 0,
      awaiting: { input: { type: 'EnterKey' } },
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [{ segments: [{ text: 'x' }], button: { value: true, isInteger: false } }],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });
});

// ---------- ADR-0016：TINPUT 计时元数据（v12 起挂在 awaiting.wait 上）----------
//
// 旧顶层四件套（timeLimit / displayTime / timeUpMessage / timedOut）已随 v12 收缩退场；
// 同一份数据现在只出现在 `awaiting.wait` 的四个可选键上，语义是 C# `WhenWritingNull`
// （仅 true / 有值时出现）。与 C# AgentJsonlProtocolTests 对称：覆盖填充、缺省、形状校验。

describe('parseTurnRecord - TINPUT 计时元数据（awaiting.wait）', () => {
  it('TINPUT 帧：durationMs + displayTime + timeUpMessage 正确解析', () => {
    const json = JSON.stringify({
      awaiting: {
        input: { type: 'IntValue' },
        wait: { durationMs: 5000, displayTime: true, timeUpMessage: '时间到！' },
      },
      protocolVersion: 12,
      generation: 0,
      diff: null,
    });
    const turn = parseTurnRecord(json);
    expect(turn.awaiting.wait!.durationMs).toBe(5000);
    expect(turn.awaiting.wait!.displayTime).toBe(true);
    expect(turn.awaiting.wait!.timeUpMessage).toBe('时间到！');
    // 尚未超时 ⇒ 一次性旗标键缺省（不是 false）
    expect(turn.awaiting.wait!.timedOut).toBeNull();
  });

  it('超时续跑帧：wait.timedOut=true 透传（一次性旗标）', () => {
    const json = JSON.stringify({
      awaiting: {
        input: { type: 'IntValue' },
        wait: { durationMs: 5000, timedOut: true, timeUpMessage: 'Time up, continue with default' },
      },
      generation: 0,
      diff: null,
    });
    const turn = parseTurnRecord(json);
    expect(turn.awaiting.wait!.timedOut).toBe(true);
    expect(turn.awaiting.wait!.timeUpMessage).toBe('Time up, continue with default');
  });

  it('零时长让路帧：只有 durationMs=0，四个可选键全缺省', () => {
    const json = JSON.stringify({
      awaiting: { wait: { durationMs: 0 } },
      generation: 0,
      diff: null,
    });
    const turn = parseTurnRecord(json);
    expect(turn.awaiting.wait).toEqual({
      durationMs: 0,
      displayTime: null,
      timeUpMessage: null,
      timedOut: null,
    });
  });

  it('wait.durationMs 非 int 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      awaiting: { wait: { durationMs: '5000' } },
      generation: 0,
    });
    expect(() => parseTurnRecord(json)).toThrow(/awaiting\.wait\.durationMs/);
  });

  it('wait.displayTime 非 boolean 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      awaiting: { wait: { durationMs: 3000, displayTime: 'yes' } },
      generation: 0,
    });
    expect(() => parseTurnRecord(json)).toThrow(/awaiting\.wait\.displayTime/);
  });

  it('wait.timedOut 非 boolean 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      awaiting: { wait: { durationMs: 3000, timedOut: 1 } },
      generation: 0,
    });
    expect(() => parseTurnRecord(json)).toThrow(/awaiting\.wait\.timedOut/);
  });
});

// ---------- shift_head LineOp ----------
//
// 与 C# DisplayDiffTests.ShiftHeadTests 系列（Emuera.Headless.Tests/DisplayDiffTests.cs）
// 对称——验证前端解析器能正确反序列化 C# 端产出的 shift_head op。
describe('parseTurnRecord - shift_head LineOp', () => {
  it('解析 shift_head op（count=1，MaxLog 滚动场景）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          { type: 'shift_head', count: 1 },
          { type: 'append', newLines: [{ entries: [{ segments: [{ text: 'new' }] }], isLineEnd: true }] },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    expect(turn.diff).not.toBeNull();
    expect(turn.diff!.lineOps).toHaveLength(2);

    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('shift_head');
    if (op0.type !== 'shift_head') throw new Error('unreachable');
    expect(op0.count).toBe(1);
  });

  it('解析 shift_head op（count>1，多行截断）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [{ type: 'shift_head', count: 5 }],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('shift_head');
    if (op0.type !== 'shift_head') throw new Error('unreachable');
    expect(op0.count).toBe(5);
  });

  it('shift_head count 缺失抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [{ type: 'shift_head' }],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  it('shift_head count 非 int 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [{ type: 'shift_head', count: '1' }],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  it('shift_head count=0 合法（无操作，防御性）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [{ type: 'shift_head', count: 0 }],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    expect(op0.type).toBe('shift_head');
    if (op0.type !== 'shift_head') throw new Error('unreachable');
    expect(op0.count).toBe(0);
  });
});

// ---------- v8：image / shape / bgImages 字段 ----------
//
// 与 C# ProtocolV8SegmentTests / AdapterV8Tests 对称——验证前端解析器能正确反序列化
// C# 端产出的 v8 新字段。2026-08-06 修复：此前 parsePrintSegment/parseDiff 只按 v7 字段
// 解析，image/shape/bgImages 在 JSON→对象转换时被丢弃——SegmentRenderer 的 v-if="seg.image"
// 恒 false，图片/背景「降级为文本」（渲染 AltText 字符串）。本组用例锁住透传行为。
describe('parseTurnRecord - v8 image/shape/bgImages', () => {
  it('image segment：src/srcb/srcm/width/height/ypos 全字段透传', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [{ segments: [{ text: '<img>', image: { src: 'img/test.png', srcb: 'img/hover.png', srcm: 'img/map.png', width: 36, height: 18, ypos: 3 } }] }],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];
    expect(seg.image).not.toBeUndefined();
    expect(seg.image!.src).toBe('img/test.png');
    expect(seg.image!.srcb).toBe('img/hover.png');
    expect(seg.image!.srcm).toBe('img/map.png');
    expect(seg.image!.width).toBe(36);
    expect(seg.image!.height).toBe(18);
    expect(seg.image!.ypos).toBe(3);
  });

  it('image segment：srcb/srcm 省略时解析为 null（WhenWritingNull 对称）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              { entries: [{ segments: [{ text: '<img>', image: { src: 'img/x.png', width: 10, height: 10, ypos: 0 } }] }], isLineEnd: true },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];
    expect(seg.image!.srcb).toBeNull();
    expect(seg.image!.srcm).toBeNull();
  });

  it('shape segment：type/x/y/width/height/color 全字段透传', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [{ segments: [{ text: '', shape: { type: 'rect', x: 13, y: 0, width: 18, height: 18, color: '#FF0000' } }] }],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];
    expect(seg.shape).not.toBeUndefined();
    expect(seg.shape!.type).toBe('rect');
    expect(seg.shape!.x).toBe(13);
    expect(seg.shape!.y).toBe(0);
    expect(seg.shape!.width).toBe(18);
    expect(seg.shape!.height).toBe(18);
    expect(seg.shape!.color).toBe('#FF0000');
  });

  it('underline 文本样式透传（PRINT_SLIDER 横线）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              { entries: [{ segments: [{ text: '  ', color: '#40C040', underline: true }] }], isLineEnd: true },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];

    expect(seg.underline).toBe(true);
  });

  it('strikeout 文本样式透传（PRINT_SLIDER 轨道，ERB 位4=删除线）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              { entries: [{ segments: [{ text: '  ', color: '#40C040', strikeout: true }] }], isLineEnd: true },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];

    expect(seg.strikeout).toBe(true);
    expect(seg.underline).toBeFalsy();
  });

  it('diff.bgImages：src/depth/opacity 数组透传', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [],
        bgImages: [
          { src: 'bg/forest.png', depth: 0, opacity: 1 },
          { src: 'bg/overlay.png', depth: 1, opacity: 0.5 },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    expect(turn.diff!.bgImages).not.toBeUndefined();
    expect(turn.diff!.bgImages).toHaveLength(2);
    expect(turn.diff!.bgImages![0].src).toBe('bg/forest.png');
    expect(turn.diff!.bgImages![0].depth).toBe(0);
    expect(turn.diff!.bgImages![0].opacity).toBe(1);
    expect(turn.diff!.bgImages![1].src).toBe('bg/overlay.png');
    expect(turn.diff!.bgImages![1].depth).toBe(1);
    expect(turn.diff!.bgImages![1].opacity).toBe(0.5);
  });

  it('纯文本段：image/shape 为 undefined（v7 行为锁定）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              { entries: [{ segments: [{ text: 'Hello' }] }], isLineEnd: true },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];
    expect(seg.image).toBeUndefined();
    expect(seg.shape).toBeUndefined();
  });

  it('image 缺 src 抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              { entries: [{ segments: [{ text: '<img>', image: { width: 36, height: 18, ypos: 0 } }] }], isLineEnd: true },
            ],
          },
        ],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });

  // ---------- v9（issue 07）：crop 裁切字段 ----------

  it('image crop：x/y/imgWidth/imgHeight 全字段透传', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [{
                  segments: [{
                    text: '<img>',
                    image: { src: 'Face_2', width: 36, height: 18, ypos: 0, crop: { x: -36, y: 0, imgWidth: 72, imgHeight: 36 } },
                  }],
                }],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];
    expect(seg.image!.crop).toEqual({ x: -36, y: 0, imgWidth: 72, imgHeight: 36 });
    // width/height 仍是裁切后显示尺寸（容器尺寸），与 crop.imgWidth/imgHeight 语义区分
    expect(seg.image!.width).toBe(36);
    expect(seg.image!.height).toBe(18);
  });

  it('image 无 crop 字段时解析为 undefined（老客户端帧兼容）', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [{ segments: [{ text: '<img>', image: { src: 'img/test.png', width: 36, height: 18, ypos: 0 } }] }],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    const turn = parseTurnRecord(json);
    const op0 = turn.diff!.lineOps[0];
    if (op0.type !== 'append') throw new Error('unreachable');
    const seg = op0.newLines[0].entries[0].segments[0];
    expect(seg.image!.crop).toBeUndefined();
  });

  it('image crop 缺字段（非 int）抛 ParseTurnRecordError', () => {
    const json = JSON.stringify({
      awaiting: { input: { type: 'EnterKey' } },
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [{
                  segments: [{
                    text: '<img>',
                    image: { src: 'Face_2', width: 36, height: 18, ypos: 0, crop: { x: -36, y: 0, imgWidth: 72 } },
                  }],
                }],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    expect(() => parseTurnRecord(json)).toThrow(ParseTurnRecordError);
  });
});
