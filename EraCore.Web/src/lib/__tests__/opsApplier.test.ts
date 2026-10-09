import { describe, it, expect } from 'vitest';
import { applyOps, applyDiff } from '../opsApplier';
import { applySnapshot } from '../snapshotReducer';
import { EMPTY_DISPLAY_STATE } from '../../types/protocol';
import type { DisplayState, DisplaySnapshot, DisplayLine, DisplayEntry, ButtonRef, PrintSegment, TurnOp, DisplayDiff } from '../../types/protocol';

/**
 * applyOps + applyDiff 单测——与 C# `TestAdapterTests` 的 T_ops_* + T_diff_* 系列
 * （Emuera.Headless.Tests/TestAdapterTests.cs:96+/389+）对称。
 *
 * 测试矩阵（issue 02）：
 * - print：向当前行追加 entry
 * - newline：终止当前行 / 产生空行
 * - clearline：从末尾删除 n 行（含 n 超量场景）
 * - clear：清空全部行 + 重置 bgColor
 * - set_bg：更新 bgColor
 * - 几何透传：ButtonRef 的 col/width 正确传递
 * - 多按钮同行（连续 PrintOp 不换行）
 * - 全量快照 + 增量 ops roundtrip
 * - awaiting 在 ops 应用后保持不变
 * - 空 ops 不改变状态
 * - 纯度：applyOps 不修改入参 state 与 ops
 *
 * applyDiff（plan C v5 显式清空信号——对照 C# TestAdapter.ApplyDiff）：
 * - T_diff_append / T_diff_clearline / T_diff_clear / T_diff_clearscreen+append / T_diff_setbg
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

function makeState(partial: Partial<DisplayState> = {}): DisplayState {
  return {
    lines: partial.lines ?? [],
    bgColor: partial.bgColor ?? null,
    bgImages: partial.bgImages ?? [],
    // v12：awaiting 恒非 null（空闲态 = 三键皆无）。归约层原样透传，故默认空对象即可。
    awaiting: partial.awaiting ?? {},
  };
}

/**
 * 只取 `applyDiff` 返回的状态——T_diff_* 系列只关心状态，信号单独在
 * 「applyDiff: DisplaySignal」describe 中直接断言。`applyDiff` 自 C3 起
 * 返回 `{ state, signal }`，此辅助避免 27 处调用点逐个加 `.state`。
 */
function applyDiffState(state: DisplayState, diff: DisplayDiff): DisplayState {
  return applyDiff(state, diff).state;
}

// ---------- print + newline ----------

describe('applyOps: print + newline', () => {
  it('T_ops_print 对称：PrintOp 追加 entry，NewLineOp 终止行', () => {
    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('Hello')], button: button(42, true, 0, 5, 0) },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(1);
    const l = newState.lines[0];
    expect(l.isLineEnd).toBe(true);
    expect(l.entries).toHaveLength(1);

    const e = l.entries[0];
    expect(e.segments[0].text).toBe('Hello');
    expect(e.button).not.toBeNull();
    expect(e.button!.col).toBe(0);
    expect(e.button!.width).toBe(5);
    expect(e.button!.value).toBe(42);
    expect(e.button!.isInteger).toBe(true);
  });

  it('连续 PrintOp 不带 newline 留在同一行', () => {
    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('Hello')] },
      { type: 'print', segments: [seg(' ')] },
      { type: 'print', segments: [seg('World')] },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(1);
    expect(newState.lines[0].entries).toHaveLength(3);
    expect(newState.lines[0].isLineEnd).toBe(true);
    expect(newState.lines[0].entries.map((e) => e.segments[0].text).join('')).toBe('Hello World');
  });

  it('NewLineOp 在空状态产生空行', () => {
    const ops: TurnOp[] = [{ type: 'newline', align: 'left' }];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(1);
    expect(newState.lines[0].entries).toEqual([]);
    expect(newState.lines[0].isLineEnd).toBe(true);
    expect(newState.lines[0].align).toBe('left');
  });

  it('连续 NewLineOp 产生多个空行', () => {
    const ops: TurnOp[] = [
      { type: 'newline', align: null },
      { type: 'newline', align: null },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    expect(newState.lines).toHaveLength(3);
    for (const l of newState.lines) {
      expect(l.entries).toEqual([]);
      expect(l.isLineEnd).toBe(true);
    }
  });

  it('NewLineOp 覆盖当前行 align（即使 align=null 也覆盖）', () => {
    // 先建一行 align="left"
    const ops1: TurnOp[] = [
      { type: 'print', segments: [seg('x')] },
      { type: 'newline', align: 'left' },
    ];
    const state1 = applyOps(EMPTY_DISPLAY_STATE, ops1);
    expect(state1.lines[0].align).toBe('left');

    // 再来一行 align=null 的 newline，覆盖现有 align
    const ops2: TurnOp[] = [{ type: 'print', segments: [seg('y')] }, { type: 'newline', align: null }];
    const state2 = applyOps(state1, ops2);
    // 第一行 align 保留 "left"；第二行 align 被 null 覆盖
    expect(state2.lines[0].align).toBe('left');
    expect(state2.lines[1].align).toBeNull();
  });
});

// ---------- clearline ----------

describe('applyOps: clearline', () => {
  it('T_ops_clearline 对称：删除末尾 n 行', () => {
    const setup: TurnOp[] = [
      { type: 'print', segments: [seg('A')] },
      { type: 'newline', align: null },
      { type: 'print', segments: [seg('B')] },
      { type: 'newline', align: null },
      { type: 'print', segments: [seg('C')] },
      { type: 'newline', align: null },
    ];
    const state = applyOps(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(3);

    const cleared = applyOps(state, [{ type: 'clearline', n: 2 }]);
    expect(cleared.lines).toHaveLength(1);
    expect(cleared.lines[0].entries[0].segments[0].text).toBe('A');
  });

  it('T_ops_clearline_exceeding 对称：n 超过总行数时全部清空', () => {
    const setup: TurnOp[] = [
      { type: 'print', segments: [seg('only')] },
      { type: 'newline', align: null },
    ];
    const state = applyOps(EMPTY_DISPLAY_STATE, setup);

    const cleared = applyOps(state, [{ type: 'clearline', n: 5 }]);
    expect(cleared.lines).toEqual([]);
  });

  it('clearline n=0 不改变行', () => {
    const setup: TurnOp[] = [
      { type: 'print', segments: [seg('A')] },
      { type: 'newline', align: null },
    ];
    const state = applyOps(EMPTY_DISPLAY_STATE, setup);

    const cleared = applyOps(state, [{ type: 'clearline', n: 0 }]);
    expect(cleared.lines).toHaveLength(1);
  });
});

// ---------- clear ----------

describe('applyOps: clear', () => {
  it('T_ops_clear 对称：清空全部行 + 重置 bgColor', () => {
    // 先用 snapshot 设置初始状态（含 bgColor）
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      bgColor: '#FF0000',
      lines: [{ entries: [entry('data')], isLineEnd: true }],
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(state.lines).toHaveLength(1);
    expect(state.bgColor).toBe('#FF0000');

    const cleared = applyOps(state, [{ type: 'clear' }]);
    expect(cleared.lines).toEqual([]);
    expect(cleared.bgColor).toBeNull();
  });
});

// ---------- set_bg ----------

describe('applyOps: set_bg', () => {
  it('T_ops_setbg 对称：更新 bgColor', () => {
    const state = applyOps(EMPTY_DISPLAY_STATE, [{ type: 'set_bg', color: '#FF0000' }]);
    expect(state.bgColor).toBe('#FF0000');

    const state2 = applyOps(state, [{ type: 'set_bg', color: '#00FF00' }]);
    expect(state2.bgColor).toBe('#00FF00');
  });
});

// ---------- v8：背景图 ops ----------

describe('applyOps: bg images (v8)', () => {
  it('set_bg_image 追加背景图（WinForms 追加语义）', () => {
    const state = applyOps(EMPTY_DISPLAY_STATE, [
      { type: 'set_bg_image', src: 'bg/forest.png', depth: 0, opacity: 1 },
    ]);
    expect(state.bgImages).toEqual([{ src: 'bg/forest.png', depth: 0, opacity: 1 }]);
  });

  it('连续 set_bg_image 按序追加（depth 不排序——排序是渲染层职责）', () => {
    const state = applyOps(EMPTY_DISPLAY_STATE, [
      { type: 'set_bg_image', src: 'bg/a.png', depth: 2, opacity: 1 },
      { type: 'set_bg_image', src: 'bg/b.png', depth: 1, opacity: 0.5 },
    ]);
    expect(state.bgImages.map((b) => b.src)).toEqual(['bg/a.png', 'bg/b.png']);
  });

  it('remove_bg_image 移除首个同名（WinForms 语义）', () => {
    const state = applyOps(EMPTY_DISPLAY_STATE, [
      { type: 'set_bg_image', src: 'bg/forest.png', depth: 0, opacity: 1 },
      { type: 'set_bg_image', src: 'bg/forest.png', depth: 2, opacity: 1 },
      { type: 'set_bg_image', src: 'bg/sea.png', depth: 1, opacity: 1 },
      { type: 'remove_bg_image', src: 'bg/forest.png' },
    ]);
    expect(state.bgImages.map((b) => b.depth)).toEqual([2, 1]);
  });

  it('remove_bg_image 不存在时 no-op', () => {
    const state = applyOps(EMPTY_DISPLAY_STATE, [
      { type: 'set_bg_image', src: 'bg/a.png', depth: 0, opacity: 1 },
      { type: 'remove_bg_image', src: 'bg/nope.png' },
    ]);
    expect(state.bgImages).toHaveLength(1);
  });

  it('clear_bg_image 清空全部', () => {
    const state = applyOps(EMPTY_DISPLAY_STATE, [
      { type: 'set_bg_image', src: 'bg/a.png', depth: 0, opacity: 1 },
      { type: 'clear_bg_image' },
    ]);
    expect(state.bgImages).toEqual([]);
  });

  it('纯度：applyOps 不修改入参 bgImages 数组', () => {
    const st = makeState({ bgImages: [{ src: 'bg/a.png', depth: 0, opacity: 1 }] });
    const state = applyOps(st, [{ type: 'clear_bg_image' }]);
    expect(state.bgImages).toEqual([]);
    expect(st.bgImages).toHaveLength(1); // 入参未被修改
  });
});

// ---------- 几何透传 ----------

describe('applyOps: 几何透传', () => {
  it('T_ops_geometry 对称：ButtonRef col=5, width=4 不重算直接透传', () => {
    const ops: TurnOp[] = [
      {
        type: 'print',
        segments: [seg('btn')],
        button: button('click', false, 5, 4, 0),
      },
      { type: 'newline', align: null },
    ];
    const newState = applyOps(EMPTY_DISPLAY_STATE, ops);

    const e = newState.lines[0].entries[0];
    expect(e.button).not.toBeNull();
    expect(e.button!.col).toBe(5);
    expect(e.button!.width).toBe(4);
    expect(e.button!.value).toBe('click');
    expect(e.button!.isInteger).toBe(false);
  });
});

// ---------- roundtrip ----------

describe('applyOps: roundtrip', () => {
  it('T_full_roundtrip 对称：snapshot 初始化 + ops 增量（clear + set_bg + print + newline）', () => {
    // 初始快照：1 行 [Start] 按钮
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      bgColor: '#000000',
      lines: [lineFromButton('[Start]', 1)],
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);
    expect(state.lines).toHaveLength(1);
    expect(state.lines[0].entries[0].segments[0].text).toBe('[Start]');

    // 增量 ops：清屏 + 新按钮 + 换背景
    const ops: TurnOp[] = [
      { type: 'clear' },
      { type: 'set_bg', color: '#0000FF' },
      {
        type: 'print',
        segments: [seg('[Next]')],
        button: button(2, true, 0, 6, 0),
      },
      { type: 'newline', align: null },
    ];

    const final = applyOps(state, ops);

    expect(final.lines).toHaveLength(1);
    expect(final.lines[0].entries[0].segments[0].text).toBe('[Next]');
    expect(final.lines[0].entries[0].button!.col).toBe(0);
    expect(final.lines[0].entries[0].button!.width).toBe(6);
    expect(final.bgColor).toBe('#0000FF');
  });

  it('T_full_roundtrip_preserves_awaiting 对称：ops 不改变 awaiting', () => {
    const snap = snapshot({
      awaiting: { input: { type: 'IntValue' }, wait: { durationMs: 3000, displayTime: true } },
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);

    expect(state.awaiting.input!.type).toBe('IntValue');
    expect(state.awaiting.wait!.durationMs).toBe(3000);

    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('x')] },
      { type: 'newline', align: null },
    ];
    const final = applyOps(state, ops);

    expect(final.awaiting).toEqual({
      input: { type: 'IntValue' },
      wait: { durationMs: 3000, displayTime: true },
    });
  });

  it('空 ops 不改变状态', () => {
    const snap = snapshot({
      awaiting: INPUT_WAIT,
      bgColor: '#FF0000',
      lines: [lineFromButton('[OK]', 1)],
    });
    const state = applySnapshot(EMPTY_DISPLAY_STATE, snap);

    const final = applyOps(state, []);
    expect(final.lines).toHaveLength(1);
    expect(final.bgColor).toBe('#FF0000');
    expect(final.awaiting).toEqual(INPUT_WAIT);
  });
});

// ---------- 纯度验证 ----------

describe('applyOps: 纯度', () => {
  it('applyOps 不修改入参 state', () => {
    const ops: TurnOp[] = [
      { type: 'print', segments: [seg('A')] },
      { type: 'newline', align: null },
      { type: 'set_bg', color: '#FF0000' },
    ];
    const state = makeState({ bgColor: '#000000', lines: [{ entries: [entry('orig')], isLineEnd: true }] });
    const originalLinesLen = state.lines.length;
    const originalBg = state.bgColor;
    const originalFirstText = state.lines[0].entries[0].segments[0].text;

    applyOps(state, ops);

    expect(state.lines).toHaveLength(originalLinesLen);
    expect(state.bgColor).toBe(originalBg);
    expect(state.lines[0].entries[0].segments[0].text).toBe(originalFirstText);
  });

  it('applyOps 不与入参 ops 共享 segment 引用（深拷贝隔离）', () => {
    // C# TestAdapter 的 segments.ToList() 浅拷贝 List 但元素共享——C# PrintSegment 是 record
    // （init-only 不可变），共享安全。TS 对象默认可变，故 applyOps 深拷贝每个 segment。
    const op: TurnOp = {
      type: 'print',
      segments: [seg('orig')],
    };
    const state = applyOps(EMPTY_DISPLAY_STATE, [op, { type: 'newline', align: null }]);

    // 修改原 op.segments[0].text——不应影响已应用的状态
    op.segments[0].text = 'mutated';

    expect(state.lines[0].entries[0].segments[0].text).toBe('orig');
  });

  it('未知 op 类型抛 Error（与 C# InvalidOperationException 对称）', () => {
    const bogus = { type: 'unknown_op' } as unknown as TurnOp;
    expect(() => applyOps(EMPTY_DISPLAY_STATE, [bogus])).toThrow(/Unknown op type/);
  });
});

// ---------- applyDiff：消费 DisplayDiff（v5 协议增量） ----------

/**
 * 与 C# TestAdapterTests.T_diff_* 系列（Emuera.Headless.Tests/TestAdapterTests.cs:389+）对称。
 * WS 帧 TurnRecord.diff.lineOps 即 LineOp[]——前端实际消费 WS 流靠 applyDiff。
 */
describe('applyDiff', () => {
  // 辅助：构造只含一段 text 的 DisplayLine（用于 append 测试）
  function diffLine(text: string): DisplayLine {
    return { entries: [entry(text)], align: 'left', isLineEnd: true };
  }

  function diff(partial: Partial<DisplayDiff>): DisplayDiff {
    return {
      lineOps: partial.lineOps ?? [],
      bgColor: partial.bgColor ?? null,
      bgImages: partial.bgImages ?? undefined,
    };
  }

  it('T_diff_append 对称：追加新行到末尾', () => {
    const d = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, d);

    expect(state.lines).toHaveLength(2);
    expect(state.lines[0].entries[0].segments[0].text).toBe('a');
    expect(state.lines[1].entries[0].segments[0].text).toBe('b');
  });

  it('T_diff_clearline 对称：从末尾删除 n 行', () => {
    // 先 append 3 行
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(3);

    // 再 clear_line_diff 删除 2 行
    const cleared = applyDiffState(state, diff({ lineOps: [{ type: 'clear_line_diff', clearCount: 2 }] }));
    expect(cleared.lines).toHaveLength(1);
    expect(cleared.lines[0].entries[0].segments[0].text).toBe('a');
  });

  it('T_diff_clear 对称：清空全部行 + 更新 bgColor', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('data')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);

    const cleared = applyDiffState(state, diff({
      lineOps: [{ type: 'clear_screen' }],
      bgColor: '#FF0000',
    }));
    expect(cleared.lines).toEqual([]);
    expect(cleared.bgColor).toBe('#FF0000');
  });

  it('T_diff_clearscreen_then_append 对称：全清后重印重建状态', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('old')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);

    // CLEAR + 重印新行：等价于引擎 CLEAR 后打印
    const rebuilt = applyDiffState(state, diff({
      lineOps: [
        { type: 'clear_screen' },
        { type: 'append', newLines: [diffLine('new')] },
      ],
    }));
    expect(rebuilt.lines).toHaveLength(1);
    expect(rebuilt.lines[0].entries[0].segments[0].text).toBe('new');
  });

  it('T_diff_setbg 对称：仅更新 bgColor 不动 lines', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('keep')] }],
      bgColor: '#00FF00',
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);

    const updated = applyDiffState(state, diff({ lineOps: [], bgColor: '#0000FF' }));
    expect(updated.lines).toHaveLength(1);
    expect(updated.lines[0].entries[0].segments[0].text).toBe('keep');
    expect(updated.bgColor).toBe('#0000FF');
  });

  it('diff.bgColor=null 时保留原 bgColor', () => {
    const state = makeState({ bgColor: '#FF0000', lines: [{ entries: [entry('keep')], isLineEnd: true }] });
    const d = diff({ lineOps: [{ type: 'append', newLines: [diffLine('extra')] }] });
    const updated = applyDiffState(state, d);
    expect(updated.bgColor).toBe('#FF0000');
    expect(updated.lines).toHaveLength(2);
  });

  it('T_diff_bgimages 对称：diff.bgImages 整体替换（与 C# DisplayDiff.bgImages 对称）', () => {
    const state = makeState({ bgImages: [{ src: 'bg/old.png', depth: 0, opacity: 1 }] });
    const updated = applyDiffState(state, diff({
      lineOps: [],
      bgImages: [{ src: 'bg/new.png', depth: 2, opacity: 0.5 }],
    }));
    expect(updated.bgImages).toEqual([{ src: 'bg/new.png', depth: 2, opacity: 0.5 }]);
  });

  it('T_diff_bgimages_clear 对称：[] 表达清空（与 bgColor null=未变区分）', () => {
    const state = makeState({ bgImages: [{ src: 'bg/a.png', depth: 0, opacity: 1 }] });
    const updated = applyDiffState(state, diff({ lineOps: [], bgImages: [] }));
    expect(updated.bgImages).toEqual([]);
  });

  it('T_diff_bgimages_omitted 对称：bgImages 缺省（null）保留原状态', () => {
    const state = makeState({ bgImages: [{ src: 'bg/a.png', depth: 0, opacity: 1 }] });
    const updated = applyDiffState(state, diff({ lineOps: [] }));
    expect(updated.bgImages).toEqual([{ src: 'bg/a.png', depth: 0, opacity: 1 }]);
  });

  it('applySnapshot 重建 bgImages（C# 紧凑归一：省略即空）', () => {
    const snap: DisplaySnapshot = {
      lines: [],
      bgColor: null,
      awaiting: INPUT_WAIT,
      protocolVersion: 8,
      generation: 0,
      bgImages: [{ src: 'bg/a.png', depth: 1, opacity: 0.5 }],
    };
    expect(applySnapshot(EMPTY_DISPLAY_STATE, snap).bgImages).toEqual([{ src: 'bg/a.png', depth: 1, opacity: 0.5 }]);

    const emptySnap: DisplaySnapshot = {
      lines: [],
      bgColor: null,
      awaiting: INPUT_WAIT,
      protocolVersion: 8,
      generation: 0,
    };
    expect(applySnapshot(EMPTY_DISPLAY_STATE, emptySnap).bgImages).toEqual([]);
  });

  it('未知 LineOp 类型抛 Error', () => {
    const bogus = {
      lineOps: [{ type: 'unknown_diff_op' }],
      bgColor: null,
    } as unknown as DisplayDiff;
    expect(() => applyDiffState(EMPTY_DISPLAY_STATE, bogus)).toThrow(/Unknown LineOp type/);
  });

  it('applyDiff 不与入参共享 segments 引用（深拷贝隔离）', () => {
    const newLine: DisplayLine = { entries: [entry('orig')], isLineEnd: true };
    const d = diff({ lineOps: [{ type: 'append', newLines: [newLine] }] });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, d);

    // 修改原 newLine.entries[0].segments[0].text——不应影响已应用的状态
    newLine.entries[0].segments[0].text = 'mutated';

    expect(state.lines[0].entries[0].segments[0].text).toBe('orig');
  });
});

// ---------- applyDiff: shift_head（MaxLog 头部截断） ----------
//
// 与 C# TestAdapterTests.T_diff_shift_head_* 系列（Emuera.Headless.Tests/TestAdapterTests.cs）
// 对称——验证前端消费方与 C# TestAdapter 行为一致。
describe('applyDiff: shift_head', () => {
  function diffLine(text: string): DisplayLine {
    return { entries: [entry(text)], align: 'left', isLineEnd: true };
  }

  function diff(partial: Partial<DisplayDiff>): DisplayDiff {
    return {
      lineOps: partial.lineOps ?? [],
      bgColor: partial.bgColor ?? null,
    };
  }

  it('T_diff_shift_head 对称：从头部删除指定行数', () => {
    // 先 append 3 行 [a, b, c]
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(3);

    // shift_head 删除头部 1 行——剩 [b, c]
    const shifted = applyDiffState(state, diff({ lineOps: [{ type: 'shift_head', count: 1 }] }));
    expect(shifted.lines).toHaveLength(2);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('b');
    expect(shifted.lines[1].entries[0].segments[0].text).toBe('c');
  });

  it('T_diff_shift_head_count_exceeding 对称：count 超过总行数时全部清空', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('only')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);
    expect(state.lines).toHaveLength(1);

    const shifted = applyDiffState(state, diff({ lineOps: [{ type: 'shift_head', count: 5 }] }));
    expect(shifted.lines).toEqual([]);
  });

  it('T_diff_shift_head_zero 对称：count=0 不改变行', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('keep')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);

    const shifted = applyDiffState(state, diff({ lineOps: [{ type: 'shift_head', count: 0 }] }));
    expect(shifted.lines).toHaveLength(1);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('keep');
  });

  it('T_diff_shift_head_then_append 对称：头部截断 + 尾部追加（MaxLog 滚动场景）', () => {
    // 长 game 达 MaxLog 后每新增一行 = ShiftHeadLineOp(1) + AppendLinesOp(1)
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);

    const shifted = applyDiffState(state, diff({
      lineOps: [
        { type: 'shift_head', count: 1 },
        { type: 'append', newLines: [diffLine('c')] },
      ],
    }));
    // 头部删 a + 尾部加 c → [b, c]
    expect(shifted.lines).toHaveLength(2);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('b');
    expect(shifted.lines[1].entries[0].segments[0].text).toBe('c');
  });

  it('T_diff_shift_head_with_clear_line_diff 对称：头部截断 + 尾部清行', () => {
    // 头部截断 + 尾部 CLEARLINE 可同回合发生（spec.md shift_head 与 ClearLineDiffOp 共存）
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c'), diffLine('d')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);

    const shifted = applyDiffState(state, diff({
      lineOps: [
        { type: 'shift_head', count: 1 },          // 删 a → [b, c, d]
        { type: 'clear_line_diff', clearCount: 1 }, // 删 d → [b, c]
      ],
    }));
    expect(shifted.lines).toHaveLength(2);
    expect(shifted.lines[0].entries[0].segments[0].text).toBe('b');
    expect(shifted.lines[1].entries[0].segments[0].text).toBe('c');
  });

  it('shift_head 不与入参共享引用（纯度）', () => {
    const setup = diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    });
    const state = applyDiffState(EMPTY_DISPLAY_STATE, setup);
    const originalLinesLen = state.lines.length;

    applyDiffState(state, diff({ lineOps: [{ type: 'shift_head', count: 1 }] }));

    // applyDiff 不应修改入参 state——state.lines 仍为 2 行
    expect(state.lines).toHaveLength(originalLinesLen);
  });
});

// ---------- applyDiff: DisplaySignal（归约产出的变更信号，C3） ----------
//
// signal 是 TS 侧独有派生物——从 lineOps 分类摘要得出，驱动滚动副作用：
// - shift_head → {type:'shift_head', count}（count 为同帧多个 shift_head op 的累加）
// - clear_screen → {type:'clear_screen'}
// - 无结构性变化（append / clear_line_diff / 纯 bg 变更）→ null
// 确定性优先级：有 clear_screen 即优先（协议保证二者互斥，此为防御）。
describe('applyDiff: DisplaySignal', () => {
  function diffLine(text: string): DisplayLine {
    return { entries: [entry(text)], align: 'left', isLineEnd: true };
  }

  function diff(partial: Partial<DisplayDiff>): DisplayDiff {
    return {
      lineOps: partial.lineOps ?? [],
      bgColor: partial.bgColor ?? null,
      bgImages: partial.bgImages ?? undefined,
    };
  }

  it('shift_head 帧 → {type:shift_head, count}', () => {
    const state = applyDiffState(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    }));
    const { signal } = applyDiff(state, diff({ lineOps: [{ type: 'shift_head', count: 2 }] }));
    expect(signal).toEqual({ type: 'shift_head', count: 2 });
  });

  it('同帧多个 shift_head op（防御）→ count 累加', () => {
    const state = applyDiffState(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c')] }],
    }));
    const { signal } = applyDiff(state, diff({
      lineOps: [
        { type: 'shift_head', count: 1 },
        { type: 'shift_head', count: 1 },
      ],
    }));
    expect(signal).toEqual({ type: 'shift_head', count: 2 });
  });

  it('shift_head + append 同帧（MaxLog 滚动）→ 仍为 shift_head 信号', () => {
    const state = applyDiffState(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    }));
    const { signal } = applyDiff(state, diff({
      lineOps: [
        { type: 'shift_head', count: 1 },
        { type: 'append', newLines: [diffLine('c')] },
      ],
    }));
    expect(signal).toEqual({ type: 'shift_head', count: 1 });
  });

  it('clear_screen 帧 → {type:clear_screen}', () => {
    const state = applyDiffState(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a')] }],
    }));
    const { signal } = applyDiff(state, diff({ lineOps: [{ type: 'clear_screen' }] }));
    expect(signal).toEqual({ type: 'clear_screen' });
  });

  it('clear_screen + append 同帧（race 降级）→ 仍为 clear_screen 信号', () => {
    const state = applyDiffState(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('old')] }],
    }));
    const { signal } = applyDiff(state, diff({
      lineOps: [
        { type: 'clear_screen' },
        { type: 'append', newLines: [diffLine('new')] },
      ],
    }));
    expect(signal).toEqual({ type: 'clear_screen' });
  });

  it('无结构性变化（append / clear_line_diff / 纯 bg 变更）→ signal null', () => {
    // append-only
    const { signal: s1 } = applyDiff(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a')] }],
    }));
    expect(s1).toBeNull();

    const state = applyDiffState(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b'), diffLine('c')] }],
    }));
    // clear_line_diff（尾部截断不影响视口上方内容）
    const { signal: s2 } = applyDiff(state, diff({ lineOps: [{ type: 'clear_line_diff', clearCount: 2 }] }));
    expect(s2).toBeNull();
    // 纯 bg 变更
    const { signal: s3 } = applyDiff(state, diff({ lineOps: [], bgColor: '#FF0000' }));
    expect(s3).toBeNull();
  });

  it('shift_head + clear_screen 同帧（协议不可能，防御）→ clear_screen 优先', () => {
    const state = applyDiffState(EMPTY_DISPLAY_STATE, diff({
      lineOps: [{ type: 'append', newLines: [diffLine('a'), diffLine('b')] }],
    }));
    const { signal } = applyDiff(state, diff({
      lineOps: [
        { type: 'shift_head', count: 1 },
        { type: 'clear_screen' },
      ],
    }));
    expect(signal).toEqual({ type: 'clear_screen' });
  });
});

// ---------- 辅助 ----------

function lineFromButton(text: string, value: number, col = 0, width?: number, generation = 0): DisplayLine {
  const w = width ?? text.length;
  return {
    entries: [entry(text, button(value, true, col, w, generation))],
    isLineEnd: true,
  };
}
