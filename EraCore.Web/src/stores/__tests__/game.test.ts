import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { setActivePinia, createPinia } from 'pinia';
import { useGameStore } from '../game';
import type { DisplayLine, PrintSegment, DisplaySnapshot } from '../../types/protocol';

/**
 * useGameStore.applyTurn / setSnapshot 单测——集成 issue 02 的 parseTurnRecord + applyDiff。
 *
 * 这是 issue 03 的协议层 wiring 测试（spec.md 决策：Vitest 仅覆盖协议层 seam，
 * 不写 Vue 组件 e2e）。验证：
 * - 合法 WS 帧 → displayState 正确更新（lines / bgColor / awaiting）
 * - 多帧累积：第二帧的 diff 在第一帧的基础上叠加
 * - 非法 JSON → lastError 设置，displayState 保持不变
 * - diff=null 的帧（纯状态切换）→ state 字段更新，lines 不变
 * - protocolVersion 派生：从 lastTurn 正确推导
 * - reset() 复位所有状态
 *
 * ADR-0016（v6；v12 收缩后判据改读 awaiting.wait）：
 * - timeoutNotice 由 `awaiting.wait.timedOut` 派生
 * - TINPUT timer 状态（tinputStartedAt/tinputTimeLimit/displayTime/timeUpMessage）
 *   由 `awaiting.wait.durationMs > 0` 触发；setSnapshot 也填充
 * - showTinputCountdown 仅在 displayTime=true 时为 true
 * - serverState 由 `awaiting.ended.terminal` / 非终态 → 'WaitInput' 派生
 */

// ---------- 测试夹具 ----------

function seg(text: string, color?: string): PrintSegment {
  return color ? { text, color } : { text };
}

function diffLine(text: string, align?: 'left' | 'center' | 'right'): DisplayLine {
  return align
    ? { entries: [{ segments: [seg(text)] }], align, isLineEnd: true }
    : { entries: [{ segments: [seg(text)] }], isLineEnd: true };
}

/** 最常见的夹具：等一个「任意键」输入的停稳点。 */
const INPUT_WAIT = { input: { type: 'AnyKey' } };

/** 构造一个简单的 append diff 帧 JSON（awaiting 默认等任意键输入）。 */
function appendFrameJson(
  texts: string[],
  opts: { awaiting?: unknown; bgColor?: string | null; protocolVersion?: number | null } = {},
): string {
  const awaiting = opts.awaiting ?? INPUT_WAIT;
  const bgColor = opts.bgColor === undefined ? null : opts.bgColor;
  const protocolVersion = opts.protocolVersion === undefined ? null : opts.protocolVersion;
  return JSON.stringify({
    awaiting,
    generation: 0,
    protocolVersion,
    diff: {
      lineOps: [{ type: 'append', newLines: texts.map((t) => diffLine(t)) }],
      bgColor,
    },
  });
}

/** 构造一个 clear_line_diff 帧 */
function clearLineFrameJson(clearCount: number): string {
  return JSON.stringify({
    awaiting: INPUT_WAIT,
    generation: 0,
    diff: {
      lineOps: [{ type: 'clear_line_diff', clearCount }],
      bgColor: null,
    },
  });
}

/** 构造一个 clear_screen + set_bg 帧 */
function clearScreenFrameJson(newBg?: string): string {
  return JSON.stringify({
    awaiting: INPUT_WAIT,
    generation: 0,
    diff: {
      lineOps: [{ type: 'clear_screen' }],
      bgColor: newBg ?? null,
    },
  });
}

/** 构造一个 diff=null 的纯等待帧（v12：等待状态只由 awaiting 表达）。 */
function awaitingOnlyFrameJson(awaiting: unknown): string {
  return JSON.stringify({ awaiting, generation: 0, diff: null });
}

/**
 * ADR-0016：构造 TINPUT 帧 JSON（v12：计时元数据挂在 `awaiting.wait` 上）。
 *
 * 模拟 C# `WhenWritingNull` 行为——false / 空值字段不下发：
 * - `durationMs` 缺省 ⇒ 没有 `wait` 键（= 无限期）
 * - `timedOut` 仅在 true 且有时限时出现（与 `AwaitInfo.Of` 的 `Timelimit > 0 && timedOut` 对齐）
 */
function tinputFrameJson(opts: {
  inputType?: string | null;
  durationMs?: number | null;
  displayTime?: boolean | null;
  timeUpMessage?: string | null;
  timedOut?: boolean;
}): string {
  const awaiting: Record<string, unknown> = {};
  if (opts.inputType) awaiting.input = { type: opts.inputType };
  if (opts.durationMs != null) {
    const wait: Record<string, unknown> = { durationMs: opts.durationMs };
    if (opts.displayTime === true) wait.displayTime = true;
    if (opts.timeUpMessage != null && opts.timeUpMessage !== '') wait.timeUpMessage = opts.timeUpMessage;
    if (opts.timedOut === true) wait.timedOut = true;
    awaiting.wait = wait;
  }
  return JSON.stringify({ awaiting, generation: 0, diff: null });
}

// ---------- 测试 ----------

describe('useGameStore.applyTurn', () => {
  beforeEach(() => {
    // 每个测试用独立的 Pinia 实例，避免状态泄漏
    setActivePinia(createPinia());
  });

  it('初始状态：displayState 空，lastTurn null，protocolVersion null', () => {
    const game = useGameStore();
    expect(game.displayState.lines).toEqual([]);
    expect(game.displayState.bgColor).toBeNull();
    // v12：空闲态 = 三键皆无
    expect(game.awaiting).toEqual({});
    expect(game.lastTurn).toBeNull();
    expect(game.lastTurnJson).toBeNull();
    expect(game.turnHistory).toEqual([]);
    expect(game.lastError).toBeNull();
    expect(game.protocolVersion).toBeNull();
  });

  it('合法 append 帧：displayState 更新 lines + awaiting + serverState 派生', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['Hello', 'World'], {
      awaiting: { input: { type: 'AnyKey' } },
      bgColor: '#FF0000',
      protocolVersion: 12,
    }));

    expect(game.lastError).toBeNull();
    expect(game.displayState.lines).toHaveLength(2);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('Hello');
    expect(game.displayState.lines[1].entries[0].segments[0].text).toBe('World');
    expect(game.displayState.bgColor).toBe('#FF0000');
    expect(game.awaiting.input!.type).toBe('AnyKey');
    expect(game.protocolVersion).toBe(12);
    // serverState 由契约派生：非终态停稳点 → 'WaitInput'
    expect(game.serverState).toBe('WaitInput');
    expect(game.lastTurnJson).not.toBeNull();
    expect(game.turnHistory).toHaveLength(1);
  });

  it('多帧累积：第二帧 diff 在第一帧基础上叠加', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A', 'B']));
    game.applyTurn(appendFrameJson(['C']));

    expect(game.displayState.lines).toHaveLength(3);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('A');
    expect(game.displayState.lines[1].entries[0].segments[0].text).toBe('B');
    expect(game.displayState.lines[2].entries[0].segments[0].text).toBe('C');
    expect(game.turnHistory).toHaveLength(2);
  });

  it('clear_line_diff 帧：从末尾删除 n 行', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A', 'B', 'C']));
    game.applyTurn(clearLineFrameJson(2));

    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('A');
  });

  it('clear_screen + set_bg 帧：清空全部行并更新背景色', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['data'], { bgColor: '#000000' }));
    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.bgColor).toBe('#000000');

    game.applyTurn(clearScreenFrameJson('#FFFFFF'));
    expect(game.displayState.lines).toEqual([]);
    expect(game.displayState.bgColor).toBe('#FFFFFF');
  });

  it('clear_screen 帧：lastDisplaySignal = {type:clear_screen}（TerminalDisplay watch 此信号触发回底部）', () => {
    const game = useGameStore();
    expect(game.lastDisplaySignal).toBeNull();

    game.applyTurn(clearScreenFrameJson());
    expect(game.lastDisplaySignal).toEqual({ type: 'clear_screen' });

    // 再次 clear_screen——每次归约都是新对象，引用变化触发 watch
    const prev = game.lastDisplaySignal;
    game.applyTurn(clearScreenFrameJson());
    expect(game.lastDisplaySignal).toEqual({ type: 'clear_screen' });
    expect(game.lastDisplaySignal).not.toBe(prev);
  });

  it('clear_screen + append 同帧：lastDisplaySignal 仍为 clear_screen（race 降级场景）', () => {
    // spec.md「### 视觉处理」要求 clear_screen（含 race 降级产生的 ClearScreenOp + Append）
    // 重置 isStickyToBottom=true + scrollToBottom。applyDiff 单 tick 内顺序应用 clear_screen + append，
    // watch(lines.length) 检测不到全清——信号必须由归约层在应用 op 时同步产出。
    const game = useGameStore();
    const json = JSON.stringify({
      awaiting: INPUT_WAIT,
      generation: 0,
      diff: {
        lineOps: [
          { type: 'clear_screen' },
          { type: 'append', newLines: [diffLine('after-clear')] },
        ],
        bgColor: null,
      },
    });
    game.applyTurn(json);

    // lines 已追加新行（clear_screen + append 同帧后 length=1），但信号仍是 clear_screen
    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('after-clear');
    expect(game.lastDisplaySignal).toEqual({ type: 'clear_screen' });
  });

  it('shift_head 帧：lastDisplaySignal = {type:shift_head, count}', () => {
    const game = useGameStore();
    // 先铺 3 行
    game.applyTurn(appendFrameJson(['A', 'B', 'C']));
    expect(game.lastDisplaySignal).toBeNull(); // append-only 帧不产信号

    game.applyTurn(JSON.stringify({
      awaiting: INPUT_WAIT,
      generation: 0,
      diff: {
        lineOps: [{ type: 'shift_head', count: 2 }],
        bgColor: null,
      },
    }));
    expect(game.lastDisplaySignal).toEqual({ type: 'shift_head', count: 2 });
    expect(game.displayState.lines).toHaveLength(1); // 头部删 2 行，剩 C
  });

  it('非结构性变化帧（append / clear_line_diff）：lastDisplaySignal 保持 null', () => {
    const game = useGameStore();
    expect(game.lastDisplaySignal).toBeNull();

    game.applyTurn(appendFrameJson(['A']));
    expect(game.lastDisplaySignal).toBeNull();

    game.applyTurn(clearLineFrameJson(1));
    expect(game.lastDisplaySignal).toBeNull();
  });

  it('reset() 清空 lastDisplaySignal（避免重挂载残留触发 watch）', () => {
    const game = useGameStore();
    game.applyTurn(clearScreenFrameJson());
    expect(game.lastDisplaySignal).toEqual({ type: 'clear_screen' });

    game.reset();
    expect(game.lastDisplaySignal).toBeNull();
  });

  it('completeExitGame() 清空 lastDisplaySignal（同 reset——重挂载不残留）', () => {
    const game = useGameStore();
    game.applyTurn(clearScreenFrameJson());
    expect(game.lastDisplaySignal).toEqual({ type: 'clear_screen' });

    game.completeExitGame();
    expect(game.lastDisplaySignal).toBeNull();
  });

  it('diff=null 帧：仅更新 awaiting（让路 → 等输入），lines 不变', () => {
    const game = useGameStore();
    // 零时长让路帧：只有 wait 维度
    game.applyTurn(appendFrameJson(['keep'], { awaiting: { wait: { durationMs: 0 } } }));
    expect(game.displayState.lines).toHaveLength(1);

    game.applyTurn(awaitingOnlyFrameJson({ input: { type: 'IntValue' } }));
    expect(game.displayState.lines).toHaveLength(1); // lines 不变
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('keep');
    expect(game.awaiting.input!.type).toBe('IntValue');
    // 同一份契约也驱动 serverState 派生
    expect(game.serverState).toBe('WaitInput');
  });

  it('非法 JSON：lastError 设置，displayState 保持不变', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['orig']));
    const beforeLines = game.displayState.lines.length;
    const beforeAwaiting = game.awaiting;

    game.applyTurn('not a json');

    expect(game.lastError).not.toBeNull();
    expect(game.lastError).toContain('协议解析失败');
    expect(game.displayState.lines).toHaveLength(beforeLines); // 状态不变
    expect(game.awaiting).toBe(beforeAwaiting);
    expect(game.turnHistory).toHaveLength(2); // 但原文仍入历史
    expect(game.lastTurnJson).toBe('not a json');
  });

  it('缺 awaiting 的帧：lastError 设置，displayState 不变（唯一契约字段不得降级）', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['orig']));

    // 旧顶层字段全在也救不回来——v12 起解析器只认 awaiting
    game.applyTurn(JSON.stringify({
      state: 'WaitInput',
      inputType: 'IntValue',
      needValue: true,
      generation: 0,
      protocolVersion: 12,
    }));

    expect(game.lastError).not.toBeNull();
    expect(game.lastError).toContain('awaiting');
    expect(game.displayState.lines).toHaveLength(1);
    expect(game.displayState.lines[0].entries[0].segments[0].text).toBe('orig');
  });

  it('终态帧：awaiting.ended.terminal 驱动 serverState', () => {
    const game = useGameStore();
    game.applyTurn(awaitingOnlyFrameJson({ ended: { terminal: 'Quit' } }));
    expect(game.serverState).toBe('Quit');

    game.applyTurn(awaitingOnlyFrameJson({ ended: { terminal: 'Error' } }));
    expect(game.serverState).toBe('Error');
  });

  it('protocolVersion sticky：首帧后后续帧省略时保留版本（C# isInitial 行为对称）', () => {
    const game = useGameStore();
    // 第一帧带 protocolVersion=12（C# AgentJsonlProtocol isInitial=true）
    game.applyTurn(appendFrameJson(['A'], { protocolVersion: 12 }));
    expect(game.protocolVersion).toBe(12);

    // 第二帧不带 protocolVersion（C# isInitial=false 时该字段为 null）
    game.applyTurn(appendFrameJson(['B'], { protocolVersion: null }));
    // sticky 行为：版本号保留，不闪烁归 null
    expect(game.protocolVersion).toBe(12);
  });

  it('protocolVersion sticky：后续帧带新版本时更新（罕见场景，协议升级时）', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A'], { protocolVersion: 4 }));
    expect(game.protocolVersion).toBe(4);

    game.applyTurn(appendFrameJson(['B'], { protocolVersion: 5 }));
    expect(game.protocolVersion).toBe(5);
  });

  it('protocolVersion sticky：reset 后清空', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['A'], { protocolVersion: 12 }));
    expect(game.protocolVersion).toBe(12);

    game.reset();
    expect(game.protocolVersion).toBeNull();
  });

  it('reset()：所有状态复位到空', () => {
    const game = useGameStore();
    game.applyTurn(appendFrameJson(['data'], { protocolVersion: 12 }));
    expect(game.displayState.lines).toHaveLength(1);

    game.reset();

    expect(game.displayState.lines).toEqual([]);
    expect(game.displayState.bgColor).toBeNull();
    expect(game.awaiting).toEqual({});
    expect(game.lastTurn).toBeNull();
    expect(game.lastTurnJson).toBeNull();
    expect(game.turnHistory).toEqual([]);
    expect(game.lastError).toBeNull();
    expect(game.protocolVersion).toBeNull();
  });

  it('按钮 entry：value 为 number / string 都正确解析并保留', () => {
    const game = useGameStore();
    const json = JSON.stringify({
      awaiting: INPUT_WAIT,
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [
                  {
                    segments: [seg('[0]')],
                    button: { value: 0, isInteger: true, col: 0, width: 3, generation: 0 },
                  },
                  {
                    segments: [seg('[Cancel]')],
                    button: { value: 'cancel', isInteger: false, col: 3, width: 8, generation: 0 },
                  },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    game.applyTurn(json);

    expect(game.displayState.lines).toHaveLength(1);
    const entries = game.displayState.lines[0].entries;
    expect(entries[0].button!.value).toBe(0);
    expect(entries[0].button!.isInteger).toBe(true);
    expect(entries[1].button!.value).toBe('cancel');
    expect(entries[1].button!.isInteger).toBe(false);
  });

  it('多 entry 行（按钮 + 非按钮 + 按钮）：结构完整保留', () => {
    const game = useGameStore();
    const json = JSON.stringify({
      awaiting: INPUT_WAIT,
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [
                  { segments: [seg('[Yes]')], button: { value: 1, isInteger: true, col: 0, width: 5, generation: 0 } },
                  { segments: [seg(' or ')] },
                  { segments: [seg('[No]')], button: { value: 2, isInteger: true, col: 9, width: 4, generation: 0 } },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    game.applyTurn(json);

    const entries = game.displayState.lines[0].entries;
    expect(entries).toHaveLength(3);
    expect(entries[0].button).not.toBeNull();
    expect(entries[1].button).toBeNull();
    expect(entries[2].button).not.toBeNull();
    expect(entries[0].button!.col).toBe(0);
    expect(entries[2].button!.col).toBe(9);
  });

  it('segment 颜色正确保留（hex 字符串透传）', () => {
    const game = useGameStore();
    const json = JSON.stringify({
      awaiting: INPUT_WAIT,
      generation: 0,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: [
                  { segments: [{ text: 'red', color: '#FF0000', bold: true }] },
                  { segments: [{ text: ' ', color: null }] },
                  { segments: [{ text: 'blue', color: '#0000FF', italic: true }] },
                ],
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
    game.applyTurn(json);

    const entries = game.displayState.lines[0].entries;
    expect(entries[0].segments[0].color).toBe('#FF0000');
    expect(entries[0].segments[0].bold).toBe(true);
    expect(entries[1].segments[0].color).toBeNull();
    expect(entries[2].segments[0].color).toBe('#0000FF');
    expect(entries[2].segments[0].italic).toBe(true);
  });
});

// ---------- ADR-0016：timeoutNotice 派生（issue 04；issue 07 改读 awaiting.wait） ----------
//
// v6 协议携带超时旗标、v12 把它挂在 `awaiting.wait.timedOut` 上——前端不再依赖启发式检测。
// timeoutNotice 改为 computed：`wait.timedOut === true` 时显示文案，否则 null。
// 文案优先级：`wait.timeUpMessage` → 默认「TINPUT 超时，游戏以默认值继续」。
//
// ⚠️ 夹具都带 `durationMs`：真实 C# 只在 `Timelimit > 0` 时才会置 `wait.timedOut`
// （`AwaitInfo.Of` 的 `w.Timelimit > 0 && timedOut`），故「有超时旗标但没有时限」在 wire 上
// 不存在；不带 `wait` 的夹具只会走到「无 wait ⇒ 无通知」。
//
// 行为副作用（已接受）：本通知在**下一帧 turn 到达时**才清空（timedOut 缺省）。
// 比用户点击提交晚 50–200ms——TINPUT 场景下人眼无感。

describe('useGameStore - ADR-0016 timeoutNotice 派生', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('初始状态：timeoutNotice=null', () => {
    const game = useGameStore();
    expect(game.timeoutNotice).toBeNull();
  });

  it('wait.timedOut=true + 无 timeUpMessage → 显示默认文案', () => {
    const game = useGameStore();
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      timedOut: true,
    }));
    expect(game.timeoutNotice).toBe('TINPUT 超时，游戏以默认值继续');
  });

  it('wait.timedOut=true + 自定义 timeUpMessage → 显示自定义文案', () => {
    const game = useGameStore();
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      timedOut: true,
      timeUpMessage: '时间到，使用默认值继续',
    }));
    expect(game.timeoutNotice).toBe('时间到，使用默认值继续');
  });

  it('wait.timedOut=false → timeoutNotice=null（下一帧清空）', () => {
    const game = useGameStore();
    // 帧 1：超时
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      timedOut: true,
      timeUpMessage: '时间到',
    }));
    expect(game.timeoutNotice).toBe('时间到');

    // 帧 2：玩家正常输入推进——没有 wait（非 TINPUT）
    game.applyTurn(tinputFrameJson({ inputType: 'IntValue' }));
    expect(game.timeoutNotice).toBeNull();
  });

  it('reset()：清空 timeoutNotice', () => {
    const game = useGameStore();
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      timedOut: true,
    }));
    expect(game.timeoutNotice).not.toBeNull();

    game.reset();
    expect(game.timeoutNotice).toBeNull();
  });
});

// ---------- ADR-0016：TINPUT timer 状态 ----------
//
// applyTurn 末尾根据 `awaiting.wait` 更新 TINPUT timer 状态：
// - wait.durationMs > 0 → 记 tinputStartedAt=Date.now()，启动 setInterval ticker
// - 无 wait / durationMs === 0（零时长让路）→ 清空状态，停止 ticker
//
// showTinputCountdown 仅在 displayTime=true 时为 true（ERB 可隐藏倒计时）。
// tinputRemainingMs 是 computed，依赖 tinputTick（setInterval 100ms ++）触发响应式更新。
//
// setSnapshot 也填充 TINPUT 状态——晚加入者撞上 TINPUT 期间也能看到倒计时。

describe('useGameStore - ADR-0016 TINPUT timer 状态', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('WaitInput + timeLimit>0 + displayTime=true → 启动 timer + showTinputCountdown=true', () => {
    const game = useGameStore();
    const start = Date.now();
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      displayTime: true,
      timeUpMessage: '时间到',
    }));

    expect(game.tinputStartedAt).not.toBeNull();
    expect(game.tinputStartedAt!).toBeGreaterThanOrEqual(start);
    expect(game.tinputStartedAt!).toBeLessThanOrEqual(Date.now());
    expect(game.tinputTimeLimit).toBe(5000);
    expect(game.tinputDisplayTime).toBe(true);
    expect(game.tinputTimeUpMessage).toBe('时间到');
    expect(game.showTinputCountdown).toBe(true);
    // remaining ≈ 5000ms（刚启动，elapsed 极小）
    expect(game.tinputRemainingMs).not.toBeNull();
    expect(game.tinputRemainingMs!).toBeLessThanOrEqual(5000);
    expect(game.tinputRemainingMs!).toBeGreaterThan(4900); // 容差 100ms

    // 清理 setInterval
    game.reset();
  });

  it('非 WaitInput 状态（如 Quit）→ 清空 timer 状态 + showTinputCountdown=false', () => {
    const game = useGameStore();
    // 先进入 TINPUT
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      displayTime: true,
    }));
    expect(game.tinputStartedAt).not.toBeNull();

    // 切到 Quit（终态）——timer 状态清空
    game.applyTurn(awaitingOnlyFrameJson({ ended: { terminal: 'Quit' } }));
    expect(game.tinputStartedAt).toBeNull();
    expect(game.tinputTimeLimit).toBeNull();
    expect(game.tinputDisplayTime).toBeNull();
    expect(game.tinputTimeUpMessage).toBeNull();
    expect(game.showTinputCountdown).toBe(false);
    expect(game.tinputRemainingMs).toBeNull();
  });

  it('无 wait 的输入等待（如 AnyKey）→ 清空 timer 状态', () => {
    const game = useGameStore();
    // 先进入 TINPUT
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      displayTime: true,
    }));
    expect(game.tinputStartedAt).not.toBeNull();

    // 切到非 TINPUT 的输入等待（AnyKey）——awaiting 里没有 wait 键
    game.applyTurn(tinputFrameJson({ inputType: 'AnyKey' }));
    expect(game.tinputStartedAt).toBeNull();
    expect(game.tinputTimeLimit).toBeNull();
    expect(game.showTinputCountdown).toBe(false);
  });

  it('displayTime=false → tinputStartedAt 仍 set 但 showTinputCountdown=false（ERB 隐藏倒计时）', () => {
    const game = useGameStore();
    game.applyTurn(tinputFrameJson({
      inputType: 'IntValue',
      durationMs: 5000,
      displayTime: false, // ERB 显式禁用倒计时显示
    }));

    // 内部 timer 状态仍填充（timeoutNotice 仍可派生）
    expect(game.tinputStartedAt).not.toBeNull();
    expect(game.tinputTimeLimit).toBe(5000);
    // issue 07：`displayTime=false` 在 v12 wire 上与「键缺省」同形（WhenWritingNull）⇒
    // 落地为 null 而非 false；判定只在 `=== true` 时成立，故渲染行为不变。
    expect(game.tinputDisplayTime).not.toBe(true);
    // 但 UI 不渲染倒计时
    expect(game.showTinputCountdown).toBe(false);

    game.reset();
  });

  it('setSnapshot 也填充 TINPUT 状态（晚加入者撞上 TINPUT 期间）', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      bgColor: null,
      awaiting: {
        input: { type: 'IntValue' },
        wait: { durationMs: 3000, displayTime: true, timeUpMessage: '快选择！' },
      },
      protocolVersion: 12,
      generation: 0,
    };

    const start = Date.now();
    game.setSnapshot(snap);

    expect(game.tinputStartedAt).not.toBeNull();
    expect(game.tinputStartedAt!).toBeGreaterThanOrEqual(start);
    expect(game.tinputTimeLimit).toBe(3000);
    expect(game.tinputDisplayTime).toBe(true);
    expect(game.tinputTimeUpMessage).toBe('快选择！');
    expect(game.showTinputCountdown).toBe(true);

    game.reset();
  });

  it('setSnapshot 非 TINPUT 状态 → 不启动 timer', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      bgColor: null,
      // 终态：没有 wait 键
      awaiting: { ended: { terminal: 'Quit' } },
      protocolVersion: 12,
      generation: 0,
    };
    game.setSnapshot(snap);
    expect(game.tinputStartedAt).toBeNull();
    expect(game.tinputTimeLimit).toBeNull();
    expect(game.showTinputCountdown).toBe(false);
  });

  it('setSnapshot 写入 lastSnapshot——调试视图独立展示用', () => {
    const game = useGameStore();
    expect(game.lastSnapshot).toBeNull();

    const snap: DisplaySnapshot = {
      lines: [
        { entries: [{ segments: [{ text: 'Title' }] }], isLineEnd: true },
      ],
      bgColor: '#000000',
      awaiting: INPUT_WAIT,
      protocolVersion: 12,
      generation: 0,
    };
    game.setSnapshot(snap);

    // Pinia ref 包装后 getter 返回的是 reactive proxy，不是原对象引用——用 toStrictEqual 比深值
    expect(game.lastSnapshot).toStrictEqual(snap);
    expect(game.lastSnapshot!.lines).toHaveLength(1);
    expect(game.lastSnapshot!.lines[0].entries[0].segments[0].text).toBe('Title');
  });

  it('reset() 清空 lastSnapshot', () => {
    const game = useGameStore();
    const snap: DisplaySnapshot = {
      lines: [],
      bgColor: null,
      awaiting: { ended: { terminal: 'Quit' } },
      protocolVersion: 12,
      generation: 0,
    };
    game.setSnapshot(snap);
    expect(game.lastSnapshot).not.toBeNull();

    game.reset();
    expect(game.lastSnapshot).toBeNull();
  });
});

// ---------- MAUI 启动不恢复 gameDir ----------
//
// MAUI 进程重启后游戏循环不会自动恢复（C# 占位 BridgeHost 不 Start），上一进程残留的
// localStorage app.gameDir 是过期状态。gameDir 初始化在 MAUI 模式下跳过 localStorage，
// 避免 App.vue 误判「有活跃游戏」而隐藏游戏列表、直接进空终端画面。

/** 最小 localStorage 实现——node 测试环境无 localStorage。 */
function makeLocalStorage(): Storage {
  const map = new Map<string, string>();
  return {
    getItem: (k: string) => (map.has(k) ? map.get(k)! : null),
    setItem: (k: string, v: string) => {
      map.set(k, v);
    },
    removeItem: (k: string) => {
      map.delete(k);
    },
    clear: () => {
      map.clear();
    },
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    get length() {
      return map.size;
    },
  };
}

/** 模拟 window.location——mauiBridge.isMauiEnvironment 据此判断环境。 */
function mockWindowLocation(protocol: string, hostname: string): void {
  vi.stubGlobal('window', { location: { protocol, hostname } });
}

describe('useGameStore - MAUI 启动不恢复 gameDir', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.stubGlobal('localStorage', makeLocalStorage());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('MAUI（ms-appx-web:）localStorage 有残留 gameDir → 初始为 null', () => {
    localStorage.setItem('app.gameDir', 'D:/stale/game');
    mockWindowLocation('ms-appx-web:', 'app');

    const game = useGameStore();
    expect(game.gameDir).toBeNull();
  });

  it('MAUI（file: Android）localStorage 有残留 gameDir → 初始为 null', () => {
    localStorage.setItem('app.gameDir', '/stale/game');
    mockWindowLocation('file:', '');

    const game = useGameStore();
    expect(game.gameDir).toBeNull();
  });

  it('HTTP 模式 localStorage 有 gameDir → 仍从 localStorage 恢复（重连语义）', () => {
    localStorage.setItem('app.gameDir', 'D:/old/game');
    mockWindowLocation('https:', 'localhost');

    const game = useGameStore();
    expect(game.gameDir).toBe('D:/old/game');
  });

  it('MAUI 模式 setGameDir 不写 localStorage（不留过期键）', () => {
    mockWindowLocation('ms-appx-web:', 'app');

    const game = useGameStore();
    game.setGameDir('D:/current/game');
    expect(game.gameDir).toBe('D:/current/game');
    expect(localStorage.getItem('app.gameDir')).toBeNull();
  });

  it('HTTP 模式 setGameDir 写 localStorage', () => {
    mockWindowLocation('https:', 'localhost');

    const game = useGameStore();
    game.setGameDir('D:/current/game');
    expect(localStorage.getItem('app.gameDir')).toBe('D:/current/game');
  });
});

describe('useGameStore - 游戏静默状态提示（gameStatusHint / lastActivityAt）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it('setGameStatusHint 写入 running/stopped，clearGameStatusHint 清空', () => {
    const game = useGameStore();
    expect(game.gameStatusHint).toBeNull();

    game.setGameStatusHint('running');
    expect(game.gameStatusHint).toBe('running');

    game.setGameStatusHint('stopped');
    expect(game.gameStatusHint).toBe('stopped');

    game.clearGameStatusHint();
    expect(game.gameStatusHint).toBeNull();
  });

  it('applyTurn 更新 lastActivityAt（静默监控的计时依据）', () => {
    const game = useGameStore();
    game.lastActivityAt = Date.now() - 60000;

    game.applyTurn(appendFrameJson(['activity'], { awaiting: { wait: { durationMs: 0 } } }));

    expect(Date.now() - game.lastActivityAt).toBeLessThan(1000);
  });

  it('reset 清空 gameStatusHint', () => {
    const game = useGameStore();
    game.setGameStatusHint('stopped');

    game.reset();

    expect(game.gameStatusHint).toBeNull();
  });

  it('completeExitGame 清空 gameStatusHint', () => {
    const game = useGameStore();
    game.setGameStatusHint('stopped');

    game.completeExitGame();

    expect(game.gameStatusHint).toBeNull();
  });

  it('markInputSubmitted 写入时间戳，applyTurn 清空（提交忙态生命周期）', () => {
    const game = useGameStore();
    expect(game.inputSubmittedAt).toBeNull();

    game.markInputSubmitted();
    expect(game.inputSubmittedAt).not.toBeNull();

    game.applyTurn(appendFrameJson(['activity'], { awaiting: INPUT_WAIT }));

    expect(game.inputSubmittedAt).toBeNull();
  });

  it('markInputSubmitted 幂等——重复提交只刷新时间戳', () => {
    const game = useGameStore();
    game.markInputSubmitted();
    const first = game.inputSubmittedAt;

    game.markInputSubmitted();

    expect(game.inputSubmittedAt).not.toBeNull();
    expect(game.inputSubmittedAt).toBeGreaterThanOrEqual(first!);
  });

  it('reset / completeExitGame 清空 inputSubmittedAt', () => {
    const game = useGameStore();
    game.markInputSubmitted();
    game.reset();
    expect(game.inputSubmittedAt).toBeNull();

    game.markInputSubmitted();
    game.completeExitGame();
    expect(game.inputSubmittedAt).toBeNull();
  });
});

describe('useGameStore - hasActiveButtons（按钮 generation 过滤）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  function frameWithButtons(generation: number, buttonGens: number[]): string {
    return JSON.stringify({
      awaiting: INPUT_WAIT,
      generation,
      diff: {
        lineOps: [
          {
            type: 'append',
            newLines: [
              {
                entries: buttonGens.map((bg, i) => ({
                  segments: [seg(`[btn${i}]`)],
                  button: { value: i, isInteger: true, col: i * 5, width: 4, generation: bg },
                })),
                isLineEnd: true,
              },
            ],
          },
        ],
      },
    });
  }

  it('当前回合有按钮（generation 匹配 currentTurnGeneration）→ true', () => {
    const game = useGameStore();
    game.applyTurn(frameWithButtons(0, [0]));

    expect(game.currentTurnGeneration).toBe(0);
    expect(game.hasActiveButtons).toBe(true);
  });

  it('按钮 generation 与当前回合不匹配（历史旧按钮）→ false', () => {
    const game = useGameStore();
    // 当前回合 generation=0，按钮来自旧回合 generation=1 → 过期按钮，不算可用
    game.applyTurn(frameWithButtons(0, [1]));

    expect(game.hasActiveButtons).toBe(false);
  });

  it('无按钮 entry → false', () => {
    const game = useGameStore();
    game.applyTurn(
      JSON.stringify({
        awaiting: INPUT_WAIT,
        generation: 0,
        diff: { lineOps: [{ type: 'append', newLines: [diffLine('纯文本')] }] },
      }),
    );

    expect(game.hasActiveButtons).toBe(false);
  });

  it('历史旧按钮 + 当前回合按钮混合：只看 generation 匹配的', () => {
    const game = useGameStore();
    // 第一回合：按钮 generation=0
    game.applyTurn(frameWithButtons(0, [0]));
    // 第二回合：generation 升到 1，只追加无按钮行——旧按钮仍在历史行，但已过期
    game.applyTurn(
      JSON.stringify({
        awaiting: INPUT_WAIT,
        generation: 1,
        diff: { lineOps: [{ type: 'append', newLines: [diffLine('新内容')] }] },
      }),
    );

    expect(game.currentTurnGeneration).toBe(1);
    expect(game.hasActiveButtons).toBe(false); // 历史按钮 generation=0 ≠ 当前 1
  });
});

describe('useGameStore - setScale（缩放 clamp + 持久化）', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('合法值——直接应用并持久化', () => {
    const game = useGameStore();
    const store = {} as Record<string, string>;
    vi.stubGlobal('localStorage', {
      getItem: (k: string) => store[k] ?? null,
      setItem: (k: string, v: string) => { store[k] = v; },
    });
    game.setScale(1.3);
    expect(game.effectiveScale).toBe(1.3);
    expect(store['app.scale']).toBe('1.3');
  });

  it('超上限 2.0——截断为 2.0', () => {
    const game = useGameStore();
    vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn() });
    game.setScale(3.5);
    expect(game.effectiveScale).toBe(2.0);
    expect(game.isMaxScale).toBe(true);
  });

  it('超下限 0.5——截断为 0.5', () => {
    const game = useGameStore();
    vi.stubGlobal('localStorage', { getItem: () => null, setItem: vi.fn() });
    game.setScale(0.1);
    expect(game.effectiveScale).toBe(0.5);
    expect(game.isMinScale).toBe(true);
  });

  it('从 localStorage 恢复初始化值（0.5–2.0 范围内）', () => {
    vi.stubGlobal('localStorage', { getItem: () => '1.8', setItem: vi.fn() });
    const game = useGameStore();
    expect(game.effectiveScale).toBe(1.8);
  });

  it('localStorage 值非法（超范围/NaN）——回退 1.0', () => {
    for (const bad of ['3.0', '0.2', 'abc']) {
      vi.stubGlobal('localStorage', { getItem: () => bad, setItem: vi.fn() });
      const game = useGameStore();
      expect(game.effectiveScale).toBe(1.0);
    }
  });
});
