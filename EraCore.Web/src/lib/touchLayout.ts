/**
 * touchLayout — 触控排版的热区几何（纯函数）。
 *
 * 为什么需要它：Emuera 的按钮是行内文本，默认行高（LineHeight≈19px）远低于 48px
 * 触控目标，竖屏下点不准。v0 的对策是把含按钮的行整体撑高、并给按钮加**固定**左右
 * 内边距——后者无论按钮多宽都往两侧挤 10px，把同行后续字符往右推（`white-space: pre`
 * 下没有回旋余地），长按钮白付代价、列对齐也会被顶歪。
 *
 * 本模块有两件事，对应横向与纵向：
 *
 * 1. `computeLineHitExpansions`——每个有效按钮**左右**各能外扩多少列：按钮自身已够宽
 *    就零外扩；两侧余量不足就少扩甚至不扩；只有真的有余量才扩。
 * 2. `computeLineExtras`——哪些行需要**纵向**被撑高、各撑高多少。按钮盒恒为一个触控目标
 *    高；撑高只是为了撑开槽位让上下两张盒不相撞，且只撑到几何上必需的那一点——不动
 *    文字位置，也不把非按钮文本挤开。
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
 * 该侧可用外扩列数——与邻居**平分**空隙，故最多只能拿到一半。
 *
 * 只要求「两个热区不重叠」，不额外留净空：平分空闲空隙后，两个热区最多**相切**。
 * 不需要刻意留缝——提示矩形是内描边画法，相切时两条描边各自贴在自己盒子的内侧，
 * 视觉上本来就有 2px 的缝，而 0 净空的误触风险只落在 1px 的边界线上，不值得为此
 * 牺牲热区面积（原设计里那个「最小间隔」旋钮就是这么来的，现已撤掉）。
 *
 * 该侧没有邻居障碍物时无约束（返回 Infinity），由 `targetCols` 自己封顶。
 */
function sideCap(gapCols: number): number {
  if (gapCols === Infinity) return Infinity;
  return Math.max(0, gapCols / 2);
}

/**
 * 算出一行里每个有效按钮左右两侧可外扩的列数。
 *
 * 语义（每一条都对应一个已定决策）：
 * - **只有有效按钮会被外扩**；**任何**带几何的按钮 entry（含失效按钮）都是障碍物——
 *   比只算存活按钮更保守，避免热区与提示矩形压在一个「看起来还在」的失效按钮上。
 * - 障碍物缺 `col` / `width` 时**整体忽略**它（既不外扩也不当障碍）：无法定位就不猜，
 *   保守方向是「只损失放大，不产生误点」。
 * - 左右两侧独立计算。两个相邻按钮按同一空隙各取一半，故两者热区最多**相切**、不会重叠。
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
    const left = floor2(Math.min(wanted, sideCap(gapLeft)));
    const right = floor2(Math.min(wanted, sideCap(gapRight)));

    if (left > 0 || right > 0) expansions.set(self.index, { left, right });
  }

  return expansions;
}

// ---------- 纵向：哪些行需要被撑高 ----------

export interface LineExtrasOptions {
  /** 基准行高（px，含缩放）——游戏自身的 LineHeight × effectiveScale。 */
  rowHeightPx: number;
  /** 触控目标行高（px，含缩放）——`max(LineHeight, 48)` × effectiveScale。 */
  targetRowPx: number;
  /**
   * 判定某 entry 的按钮当前是否「有效」（当前回合 generation、非 EnterKey/AnyKey 惰性态）。
   * 只有有效按钮参与判定：失效按钮没有放大热区（不套 `.term-btn-touch`），
   * 它的行高就是普通行距，不构成纵向重叠风险。
   */
  isLive: (entry: DisplayEntry) => boolean;
}

/** 带存活按钮的一行：行号 + 该行存活按钮的列区间。 */
interface LiveLine {
  index: number;
  /** 列区间，`[起始列, 结束列)`（列 = 半角字符宽，全角占 2 列）。 */
  spans: Array<[number, number]>;
  /** 有存活按钮缺 `col`/`width`（脏数据）——无法定位，见下方判定语义。 */
  unbounded: boolean;
}

/**
 * 纵向判定窗口（行数）——相隔**几行**之内的两个按钮盒才可能上下重叠。
 *
 * 盒高固定等于目标行高 H，行距为 r，则第 i 行与第 i+k 行的槽起点相差 `k·r`，
 * 两盒重叠 ⟺ `k·r < H`。故窗口 `W = ceil(H / r) - 1`：
 * - 默认（LineHeight 19、H 48）→ `ceil(2.53) - 1 = 2`——**不是 1**：隔 2 行时两槽
 *   只差 38px，两张 48px 盒仍有 10px 重叠（Chromium 实测），漏判会留下一条
 *   掉进去就点错的窄带。
 * - 行距 ≥ 48px → W = 0（此时 extra 也是 0，本来就不用抬）。
 *
 * 用**一致行距** r 推算（而非抬高后的实际位置）：抬高只会让两行离得更远，所以这是
 * 保守方向——可能多抬一行，绝不会漏判。
 */
function conflictWindowRows(targetRowPx: number, rowHeightPx: number): number {
  if (!(rowHeightPx > 0) || !(targetRowPx > 0)) return 0;
  // 减去极小量：H/r 恰为整数时（如 48/16=3）第 k=H/r 行只是**相切**不算重叠，
  // 不减会让它多抬一行。
  return Math.max(0, Math.ceil(targetRowPx / rowHeightPx - 1e-9) - 1);
}

/** 两行是否有列区间重叠。任一行含「无几何」的存活按钮 → 无法排除重叠，按重叠处理。 */
function linesOverlap(a: LiveLine, b: LiveLine): boolean {
  if (a.unbounded || b.unbounded) return true;
  for (const [aStart, aEnd] of a.spans) {
    for (const [bStart, bEnd] of b.spans) {
      if (aStart < bEnd && bStart < aEnd) return true;
    }
  }
  return false;
}

/**
 * 算出哪些行需要在基准行高之上额外撑高，以及各撑高多少（px）——`行号 → extra`。
 *
 * **盒高恒等于目标行高**、以文字行为中心（见 `.term-btn-touch`），所以两行的盒是否相撞
 * 只看它们的槽位距离。而**撑高只会把它后面的行往下推，帮不到它与上方行之间的距离**——
 * 因此每条冲突的约束都由**上方那一行**负责，撑高量取满足约束的最小值：
 *
 *     相邻行（k = 1）→ `e ≥ 2g`（满额；g = (目标行高 - 基准行高) / 2）
 *     隔 k-1 行      → `e ≥ max(0, 2g - (k-1)·r)`
 *
 * 推导：上方行 i 与下方行 j = i+k 冲突时，两盒不相交 ⟺ 槽距 ≥ r + 2g（各自让出 g）。
 * 而槽距 = (r + e_i) + (k-1)·r（中间那些行的撑高只会再加），故 `e_i ≥ 2g - (k-1)·r`。
 *
 * 相邻行必须满额撑高，这是唯一真正的取舍：不撑则两盒必然互相覆盖（默认行距 19px 时
 * 叠 29px，48px 里有一半点下去落到隔壁按钮）；而隔 1 行只需 10px、隔 2 行及以上不需要
 * 任何处置——`k > 窗口` 时 `2g - (k-1)·r ≤ 0` 自动成立。这让「中间隔了空行的两个按钮行」
 * 只多出几何上必需的那一点空白，而不是原来那样把两行各撑 29px。
 *
 * **不做「往小里收热区」**：那会让同一屏出现两种高度的矩形（收窄的上下只剩 9.5px、看着
 * 紧贴文本），而且「下方行已撑高」时反而漏判重叠——这是量过才敢下的结论，见
 * `.scratch/touch-layout-polish/spec.md` 的修订节。
 *
 * 其余语义：
 * - **只有存活按钮参与**：失效按钮不套触控盒，没有重叠风险。这与横向「任何带几何的
 *   按钮 entry 都算障碍物」不同——那里保守是为了别拿提示矩形压住「看起来还在」的
 *   按钮文字（视觉理由），这里则是纯粹的命中几何。
 * - 存活按钮缺 `col`/`width`：视为占满整行 → 与窗口内任何存活按钮都判为冲突
 *   （多留白的保守方向，符合「脏数据只导致没放大、不导致点错」）。
 * - 判定窗口见 conflictWindowRows；超出窗口的配对 `2g - (k-1)·r ≤ 0`，无需处置。
 * - 返回表只含需要撑高的行；无人需要撑高时返回 `null`（调用方据此走定高快路径）。
 */
export function computeLineExtras(
  lines: readonly DisplayLine[],
  opts: LineExtrasOptions,
): Map<number, number> | null {
  const grow = Math.max(0, opts.targetRowPx - opts.rowHeightPx);
  // 游戏自身行距已达目标（grow = 0）→ 盒本来就在行内，纵向什么都不用做。
  if (grow <= 0) return null;

  const windowRows = conflictWindowRows(opts.targetRowPx, opts.rowHeightPx);
  if (windowRows <= 0) return null;

  // 先收「哪些行有存活按钮」及其列区间——绝大多数行没有按钮，这一步就把它们筛掉。
  const live: LiveLine[] = [];
  for (let i = 0; i < lines.length; i++) {
    const entries = lines[i].entries;
    let spans: Array<[number, number]> | null = null;
    let unbounded = false;
    for (const entry of entries) {
      if (!opts.isLive(entry)) continue;
      if (!spans) spans = [];
      const button = entry.button;
      const col = button?.col;
      const width = button?.width;
      if (typeof col !== 'number' || typeof width !== 'number') {
        unbounded = true;
        continue;
      }
      spans.push([col, col + width]);
    }
    if (spans) live.push({ index: i, spans, unbounded });
  }
  if (live.length === 0) return null;

  // 每行只对**它下方**窗口内的冲突负责。live 按行号升序，故内层一旦越界即可跳出。
  const r = opts.rowHeightPx;
  const extras = new Map<number, number>();
  for (const self of live) {
    let extra = 0;
    for (const other of live) {
      const k = other.index - self.index;
      if (k <= 0) continue;
      if (k > windowRows) break;
      if (!linesOverlap(self, other)) continue;
      extra = Math.max(extra, grow - (k - 1) * r);
    }
    if (extra > 0) extras.set(self.index, extra);
  }

  return extras.size === 0 ? null : extras;
}
