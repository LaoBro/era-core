/**
 * touchLayout — 触控排版的热区外扩几何（纯函数）。
 *
 * 为什么需要它：Emuera 的按钮是行内文本，默认行高（LineHeight≈19px）远低于 48px
 * 触控目标，竖屏下点不准。v0 的对策是把含按钮的行整体撑高、并给按钮加**固定**左右
 * 内边距——后者无论按钮多宽都往两侧挤 10px，把同行后续字符往右推（`white-space: pre`
 * 下没有回旋余地），长按钮白付代价、列对齐也会被顶歪。
 *
 * 本模块算的是「每个有效按钮左右各能外扩多少列」，让调用方按需外扩：
 * 按钮自身已够宽就零外扩；两侧余量不足就少扩甚至不扩；只有真的有余量才扩。
 *
 * 几何来源是快照里**已有**的 `ButtonRef.col` / `width`（行内 0-based 列，全角占 2 列，
 * 由 C# `ConsolePrintManager.BuildPrintOpsForLine` 统一计算、快照与增量 ops 一致透传）——
 * 因此这里不需要任何 DOM 测量，也不依赖字体度量。调用方把列数注入样式时用 `ch`：
 * 等宽终端下 1ch 即 1 个 ASCII 字符推进宽，与「列」一一对应。
 *
 * 无 store 依赖：有效按钮判定由调用方以谓词注入（见 `inputRouting.isLiveButton`），
 * 本模块只做几何，便于单测与复用。
 */

import type { DisplayEntry, DisplayLine } from '../types/protocol';

/** 一个按钮左右两侧各自的外扩列数（列；1 列 = 1 个 ASCII 字符 = 1ch）。 */
export interface HitExpansion {
  left: number;
  right: number;
}

export interface LineHitExpansionOptions {
  /**
   * 目标触控宽度（列）——热区总宽达到它即止。按钮自身列数已达（或超过）它时零外扩：
   * 这正是「最小宽高」语义在横向的落地，也是「长按钮不必再挤开左右文本」的实现。
   */
  targetCols: number;
  /**
   * 相邻两个按钮的热区之间**至少要保留**的列数。余量不足时宁可热区小一点也不挤——
   * 外扩的收益（更容易点中）必须大于代价（版面被推动 / 误点隔壁），这条是判据。
   */
  minGapCols: number;
  /**
   * 判定某 entry 的按钮当前是否「有效」（当前回合 generation、非 EnterKey/AnyKey 惰性态）。
   * 只有有效按钮会被外扩——历史回合残留的失效按钮不该白占版面与热区。
   */
  isLive: (entry: DisplayEntry) => boolean;
}

/** 行内某个带几何的按钮 entry。 */
interface PlacedButton {
  /** entry 在 `line.entries` 里的下标——返回值以此为键。 */
  index: number;
  /** 行内起始列（0-based）。 */
  col: number;
  /** 显示宽度（列，全角占 2 列）。 */
  width: number;
}

/** 无外扩时的共享空表——调用方只读。 */
const NO_EXPANSION: ReadonlyMap<number, HitExpansion> = new Map<number, HitExpansion>();

/** 列数保留两位小数并**向下取整**——向上取整会突破由空隙推出的上限。 */
function floor2(value: number): number {
  return Math.floor(value * 100) / 100;
}

/**
 * 某侧可用外扩列数——由「到该侧最近按钮障碍物的空隙」与最小间隔推出。
 * 该侧没有障碍物时视为无约束（返回 Infinity），由 `targetCols` 自己封顶。
 */
function sideCap(gapCols: number, minGapCols: number): number {
  if (gapCols === Infinity) return Infinity;
  return Math.max(0, (gapCols - minGapCols) / 2);
}

/**
 * 算出一行里每个有效按钮左右两侧可外扩的列数。
 *
 * 语义（每一条都对应一个已定决策）：
 * - **只有有效按钮会被外扩**；**任何**带几何的按钮 entry（含失效按钮）都是障碍物——
 *   比只算存活按钮更保守，避免热区与提示矩形压在一个「看起来还在」的失效按钮上。
 * - 障碍物缺 `col` / `width` 时**整体忽略**它（既不外扩也不当障碍）：无法定位就不猜，
 *   保守方向是「只损失放大，不产生误点」。
 * - 左右两侧独立计算。两个相邻按钮各自按同一空隙取一半，故两者热区之间恰好剩下
 *   `minGapCols` 列，不会重叠。
 * - 返回表只含**至少一侧有正外扩**的 entry；调用方对缺席者零处理。
 *
 * 边界：行首 / 行尾按钮的对外一侧无约束，仍由 `targetCols` 封顶（不因处在边界而特殊对待）。
 * 行对齐（`align`）不参与计算——列差与对齐无关。
 */
export function computeLineHitExpansions(
  line: DisplayLine,
  opts: LineHitExpansionOptions,
): ReadonlyMap<number, HitExpansion> {
  const placed: PlacedButton[] = [];
  for (let i = 0; i < line.entries.length; i++) {
    const button = line.entries[i].button;
    if (!button) continue;
    const { col, width } = button;
    if (typeof col !== 'number' || typeof width !== 'number') continue;
    placed.push({ index: i, col, width });
  }
  if (placed.length === 0) return NO_EXPANSION;

  // 按列排序（col 相同则按行内顺序）——「前一个 / 后一个障碍物」必须按列相邻才算得对。
  const byCol = [...placed].sort((a, b) => a.col - b.col || a.index - b.index);

  const expansions = new Map<number, HitExpansion>();
  for (let k = 0; k < byCol.length; k++) {
    const self = byCol[k];
    if (!opts.isLive(line.entries[self.index])) continue;

    // 到左/右最近障碍物的列距；无邻居则该侧无约束。
    const prev = k > 0 ? byCol[k - 1] : null;
    const next = k < byCol.length - 1 ? byCol[k + 1] : null;
    const gapLeft = prev ? self.col - (prev.col + prev.width) : Infinity;
    const gapRight = next ? next.col - (self.col + self.width) : Infinity;

    // 自身已够宽 → 目标外扩为 0 → 两侧都不动，绝不因为「反正有余量」而白扩。
    const wanted = Math.max(0, (opts.targetCols - self.width) / 2);
    const left = floor2(Math.min(wanted, sideCap(gapLeft, opts.minGapCols)));
    const right = floor2(Math.min(wanted, sideCap(gapRight, opts.minGapCols)));

    if (left > 0 || right > 0) expansions.set(self.index, { left, right });
  }

  return expansions;
}
