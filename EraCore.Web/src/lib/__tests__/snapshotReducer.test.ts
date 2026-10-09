import { describe, it, expect } from 'vitest';
import { applySnapshot } from '../snapshotReducer';
import { EMPTY_DISPLAY_STATE } from '../../types/protocol';
import type { DisplaySnapshot, DisplayLine, DisplayEntry, ButtonRef, PrintSegment } from '../../types/protocol';

/**
 * applySnapshot 单测——与 C# `TestAdapterTests.T_snapshot_reconstructs_displayLineList_field_by_field`
 * （Emuera.Headless.Tests/TestAdapterTests.cs:32）对称。
 *
 * 测试矩阵（issue 02）：
 * - 空状态：snapshot.lines=[] → 新状态也为空
 * - 有行：snapshot.lines 含多行 → 全部复制到新状态
 * - 有按钮：snapshot.lines[].entries[].button → 几何 col/width 透传
 * - 有背景色：snapshot.bgColor → 新状态 bgColor
 * - 有 awaiting（v12 唯一契约字段）：snapshot.awaiting → 新状态 awaiting
 *
 * 函数式纯度验证：applySnapshot 不修改入参 state 与 snapshot（深拷贝隔离）。
 */

// ---------- 测试夹具 ----------

function seg(text: string): PrintSegment {
  return { text };
}

function entry(text: string, button?: ButtonRef | null): DisplayEntry {
  return button === undefined ? { segments: [seg(text)] } : { segments: [seg(text)], button };
}

function button(value: number | string, isInteger: boolean, col: number, width: number, generation: number): ButtonRef {
  return { value, isInteger, generation, col, width };
}

function line(entries: DisplayEntry[], align?: DisplayLine['align'], isLineEnd = true): DisplayLine {
  return align === undefined ? { entries, isLineEnd } : { entries, align, isLineEnd };
}

function snapshot(partial: Partial<DisplaySnapshot> & Pick<DisplaySnapshot, 'awaiting'>): DisplaySnapshot {
  return {
    lines: partial.lines ?? [],
    bgColor: partial.bgColor ?? null,
    awaiting: partial.awaiting,
    protocolVersion: partial.protocolVersion ?? 12,
    generation: partial.generation ?? 0,
  };
}

/** 最常见的夹具：等一个「任意键」输入的停稳点。 */
const INPUT_WAIT = { input: { type: 'EnterKey' } };

// ---------- 测试 ----------

describe('applySnapshot', () => {
  it('空快照：lines=[] → 新状态也为空', () => {
    const snap = snapshot({ awaiting: INPUT_WAIT });
    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(newState.lines).toEqual([]);
    expect(newState.bgColor).toBeNull();
    expect(newState.awaiting).toEqual(INPUT_WAIT);
  });

  it('有行：snapshot.lines 含 2 行 → 全部复制到新状态', () => {
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      lines: [
        line([entry('Hello')]),
        line([entry('World')]),
      ],
    });
    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(newState.lines).toHaveLength(2);
    expect(newState.lines[0].entries[0].segments[0].text).toBe('Hello');
    expect(newState.lines[1].entries[0].segments[0].text).toBe('World');
    expect(newState.lines[0].isLineEnd).toBe(true);
  });

  it('有按钮：ButtonRef 几何 col/width 透传', () => {
    const btn = button(1, true, 0, 4, 0);
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      lines: [line([entry('[OK]', btn)])],
    });
    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    const got = newState.lines[0].entries[0].button;
    expect(got).not.toBeNull();
    expect(got!.value).toBe(1);
    expect(got!.isInteger).toBe(true);
    expect(got!.generation).toBe(0);
    expect(got!.col).toBe(0);
    expect(got!.width).toBe(4);
  });

  it('有背景色：snapshot.bgColor → 新状态 bgColor', () => {
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      bgColor: '#FF0000',
    });
    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(newState.bgColor).toBe('#FF0000');
  });

  it('有 awaiting：唯一契约字段原样覆盖到新状态（两维同现的 TINPUT 形态）', () => {
    const awaiting = {
      input: { type: 'IntValue' },
      wait: { durationMs: 3000, displayTime: true },
    };
    const snap = snapshot({ awaiting });
    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(newState.awaiting).toEqual(awaiting);
    expect(newState.awaiting.input!.type).toBe('IntValue');
    expect(newState.awaiting.wait!.durationMs).toBe(3000);
  });

  it('T_snapshot 对称：多行多 entry（含按钮 + 非按钮 + 按钮）按字段重建', () => {
    // 与 C# T_snapshot_reconstructs_displayLineList_field_by_field 对称：
    // 第一行单按钮 [OK]（col=0, width=4）
    // 第二行 3 entries：[Yes]（col=0, width=5）/ " or " 非按钮 / [No]（col=9, width=4）
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      bgColor: '#0A141E',
      lines: [
        line([entry('[OK]', button(1, true, 0, 4, 0))], 'left', true),
        line([
          entry('[Yes]', button(10, true, 0, 5, 0)),
          entry(' or ', null),
          entry('[No]', button(20, true, 9, 4, 0)),
        ]),
      ],
    });

    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);

    // 顶层字段
    expect(newState.lines).toHaveLength(2);
    expect(newState.bgColor).toBe('#0A141E');
    expect(newState.awaiting).toEqual(INPUT_WAIT);

    // 第一行：单按钮
    const l1 = newState.lines[0];
    expect(l1.entries).toHaveLength(1);
    expect(l1.isLineEnd).toBe(true);
    expect(l1.align).toBe('left');

    const e1 = l1.entries[0];
    expect(e1.segments[0].text).toBe('[OK]');
    expect(e1.button).not.toBeNull();
    expect(e1.button!.col).toBe(0);
    expect(e1.button!.width).toBe(4);
    expect(e1.button!.value).toBe(1);
    expect(e1.button!.isInteger).toBe(true);

    // 第二行：3 entries，col 累加（几何透传）
    const l2 = newState.lines[1];
    expect(l2.entries).toHaveLength(3);

    expect(l2.entries[0].button!.col).toBe(0);
    expect(l2.entries[0].button!.width).toBe(5);

    expect(l2.entries[1].button).toBeNull();

    expect(l2.entries[2].button!.col).toBe(9); // 5 + 4 = 9
    expect(l2.entries[2].button!.width).toBe(4);
  });

  it('深拷贝隔离：修改新状态不影响原 snapshot', () => {
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      lines: [line([entry('orig')])],
    });
    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);

    // 修改新状态
    newState.lines[0].entries[0].segments[0].text = 'mutated';
    newState.lines.push(line([entry('extra')]));

    // 原 snapshot 未受影响
    expect(snap.lines).toHaveLength(1);
    expect(snap.lines[0].entries[0].segments[0].text).toBe('orig');
  });

  it('多按钮 col 累加（T_multi_button_line 对称）：4 按钮一行 col=0/3/6/9', () => {
    // 与 C# T_multi_button_line_cols_accumulate_by_preceding_widths 对称
    // "[0]"(3) + "[1]"(3) + "[2]"(3) + "[Cancel]"(8) — col 累加 0/3/6/9
    // 注意：col 累加是 C# BuildSnapshot 计算的，TS 端只验证透传
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      lines: [line([
        entry('[0]', button(0, true, 0, 3, 0)),
        entry('[1]', button(1, true, 3, 3, 0)),
        entry('[2]', button(2, true, 6, 3, 0)),
        entry('[Cancel]', button(3, true, 9, 8, 0)),
      ])],
    });

    const newState = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    const entries = newState.lines[0].entries;

    expect(entries[0].button!.col).toBe(0);
    expect(entries[0].button!.width).toBe(3);

    expect(entries[1].button!.col).toBe(3);
    expect(entries[1].button!.width).toBe(3);

    expect(entries[2].button!.col).toBe(6);
    expect(entries[2].button!.width).toBe(3);

    expect(entries[3].button!.col).toBe(9);
    expect(entries[3].button!.width).toBe(8);
  });
});
