/**
 * imageCover — 跨行图片（`PrintSegment.image`）相对**锚定行顶部**的纵向覆盖量（纯函数）。
 *
 * 背景：ERB 图片段挂在锚定行的 `.term-line` 节点内，靠 `overflow: visible` 把超出 1 行高的
 * 部分画到相邻行上（见 `SegmentRenderer.vue` 的 maskStyle / imageStyle）。虚拟滚动只渲染
 * `lines.slice(start, end)`（见 `useVirtualScroll`）——锚定行一旦离开渲染窗口，图片节点就被
 * 卸载，**即使图片箱体仍与视口相交也会整体消失**。
 *
 * 修法：把「图片向上/向下盖住多少」当作**渲染窗口**的扩展依据——窗口两端各多渲染若干行，
 * 保证锚定行落在窗口内。**不动布局**：图片行仍是 1 行高，「后行盖先行」的重叠语义不变。
 *
 * 本模块只做几何换算，把覆盖量解成 **px**（不用行数）：行数依赖 `rowHeight`，px 与之解耦，
 * 由调用方按**未缩放**的 LineHeight 现场换算——见 `neighborCover` 的「缩放无关」说明。
 *
 * 覆盖量以锚定行顶部为原点，图片纵向占 `[ypos, ypos + height]`（均为协议里已解析的 px，
 * 裁剪图 crop 的可见几何同样是 ypos + height，语义一致）：
 * - `belowPx = max(0, ypos + height)`：图片底部越过锚定行顶部往下的量。
 *   锚定行在窗口**上方**滚出时（窗口已滚到锚定行下方），靠它把锚定行拉回窗口。
 * - `abovePx = max(0, -ypos)`：图片顶部越过锚定行顶部往上的量。
 *   锚定行在窗口**下方**时（窗口还停在锚定行上方），靠它把锚定行拉回窗口。
 *
 * 同一行多张图片取各自方向的最大值；无 image 段 → `{0, 0}`。
 */

import type { DisplayLine } from '../types/protocol';

/** 图片相对锚定行顶部的纵向覆盖量（px）。`abovePx` 向上、`belowPx` 向下。 */
export interface ImageCover {
  abovePx: number;
  belowPx: number;
}

/** 零覆盖（无图行 / 空行）——共享常量，避免反复分配。调用方只读，勿改。 */
const NO_COVER: ImageCover = { abovePx: 0, belowPx: 0 };

/**
 * 单行图片覆盖量——遍历该行所有 entry 的所有 segment，对 image 段取各方向最大值。
 * 无 image 段返回共享的 `{0, 0}`（调用方只读，勿改）。
 */
export function lineImageCover(line: DisplayLine): ImageCover {
  let abovePx = 0;
  let belowPx = 0;
  for (const entry of line.entries) {
    for (const seg of entry.segments) {
      const img = seg.image;
      if (!img) continue;
      const down = img.ypos + img.height;
      if (down > belowPx) belowPx = down;
      const up = -img.ypos;
      if (up > abovePx) abovePx = up;
    }
  }
  if (abovePx === 0 && belowPx === 0) return NO_COVER;
  return { abovePx, belowPx };
}

// ---------------------------------------------------------------------------
// 邻域扩展量
// ---------------------------------------------------------------------------

/**
 * `neighborCover` 的扫描半径下限（**游戏 px**）——默认 4096。
 *
 * 含义：图高 ≤ 本值的图片，其锚定行即使从未进入过渲染窗口，也一定会被邻域扫描捕获。
 * 4096 px ≈ 216 行（@LineHeight 19），远高于 ERB 图片资源的常见高度；成本是线性的，
 * 每帧仍在 0.02ms 量级（实测 5000 行全量扫描才 0.26ms），可按游戏资源上限调大。
 */
export const DEFAULT_BASE_RADIUS_PX = 4096;

/** 渲染窗口两端各需扩展的行数。 */
export interface NeighborSpan {
  /** 向上扩展的行数——把**向下**挂的图的锚定行拉回窗口。 */
  backRows: number;
  /** 向下扩展的行数——把**向上**挂的图的锚定行拉回窗口。 */
  fwdRows: number;
}

export interface NeighborCoverOptions {
  /** 渲染窗口起始行（含 overscan）——`useVirtualScroll.startIndex`。 */
  start: number;
  /** 渲染窗口结束行（不含，含 overscan）——`useVirtualScroll.endIndex`。 */
  end: number;
  /**
   * **未缩放**的游戏行高（px，= `ConfigCode.LineHeight`）——把覆盖量 px 换算成行数。
   *
   * 必须传未缩放值：图片几何与行高同乘 `effectiveScale`，换算出的**行数与缩放无关**；
   * 用缩放后的行高去换算会在 scale>1 时少算行数（症状复现）、scale<1 时多算行数。
   */
  rowHeightPx: number;
  /** 缩放系数——把覆盖量（游戏 px）换算到与 `offsetOf` 同源的屏幕 px。 */
  scale: number;
  /** 第 i 行顶部的屏幕 px 偏移（含触控加高）——`useVirtualScroll.offsetOf`。 */
  offsetOf: (i: number) => number;
  /** 扫描半径下限（游戏 px）——兜住「锚定行从未进入过窗口」的场景。默认见常量。 */
  baseRadiusPx?: number;
}

/** 零扩展——共享常量，避免反复分配。调用方只读，勿改。 */
const NO_SPAN: NeighborSpan = { backRows: 0, fwdRows: 0 };

/**
 * 渲染窗口两端各需扩展多少行——**只看视口邻域**，不扫全量行。
 *
 * 为什么不用「全局最大覆盖量」：全局口径下，5000 行之外的一张大图会**恒定**把每一帧的窗口
 * 两端各撑开 R 行（R = 最大图高 / 行高），即使视口附近一张图都没有——这是每帧都在付的
 * DOM 成本（实测窗口从 ~42 行涨到 ~120 行）；而且维持这个全局值要求每回合重扫全量行
 * （O(N)/回合，实测 5000 行 0.26ms、20000 行 2.25ms）。改邻域口径后：无图邻域**零扩展**，
 * 且 O(N)/回合 归零——改成每帧 O(邻域行数)（约 0.02ms）。
 *
 * 判定区间用**渲染窗口** `[offsetOf(start), offsetOf(end)]` 而不是视口：窗口 ⊇ 视口
 * （start 含 overscan 上扩、end 含 overscan 下扩），以窗口为界只会多扩不会漏；顺带省掉
 * 向 `useVirtualScroll` 索取 scrollTop / clientHeight 的新接口。
 *
 * 完备性：锚定行 a < start 的图要被看到，其箱底须越过窗口上沿，即
 * `offsetOf(start) - offsetOf(a) < belowPx × scale`；又因每行至少占 `rowHeightPx × scale`，
 * 得 `start - a < belowPx / rowHeightPx`——**只有距窗口小于「覆盖量行数」那么远的锚定行
 * 才可能相关**。故扫描 `[start - R, start)`（R ≥ 覆盖量对应的行数）即完备。R 取
 * `max(baseRows, 窗口内已见图覆盖量的行数)`：
 * - `baseRows`：常数下限，兜住首帧 / 快照 / 跳跃滚动等「锚定行从未进入过窗口」的场景。
 * - 窗口内已见图：连续滚动时，超大图的锚定行必然**先进入窗口再滚出**，进入期间把 R 抬到
 *   它自己的覆盖量行数，于是滚出后的整个覆盖范围内都仍被保护。
 *
 * 两侧独立取半径——向下挂的图只影响上扩、向上挂的图只影响下扩，互不牵连。
 *
 * @returns 扩展行数；无相关图片时返回共享的 `{0, 0}`（调用方只读，勿改）。
 */
export function neighborCover(
  lines: readonly DisplayLine[],
  opts: NeighborCoverOptions,
): NeighborSpan {
  const { rowHeightPx: rh, scale, offsetOf } = opts;
  if (rh <= 0 || lines.length === 0) return NO_SPAN;

  // 钳制到 [0, lines.length]——调用方的 start/end 已在该区间内，这里是防御性兜底。
  const lo = Math.max(0, Math.min(opts.start, lines.length));
  const hi = Math.max(lo, Math.min(opts.end, lines.length));
  if (hi <= lo) return NO_SPAN;

  const winTop = offsetOf(lo);
  const winBottom = offsetOf(hi);

  // 1) 窗口内覆盖量——把半径自适应抬到「当前窗口里最大那张图」的覆盖量。
  //    绝大多数行无图段，扫描很快被筛掉；窗口约 40 行，成本可忽略。
  let winBelow = 0;
  let winAbove = 0;
  for (let i = lo; i < hi; i++) {
    const cover = lineImageCover(lines[i]);
    if (cover.belowPx > winBelow) winBelow = cover.belowPx;
    if (cover.abovePx > winAbove) winAbove = cover.abovePx;
  }

  const baseRows = Math.ceil((opts.baseRadiusPx ?? DEFAULT_BASE_RADIUS_PX) / rh);
  const backR = Math.max(baseRows, Math.ceil(winBelow / rh));
  const fwdR = Math.max(baseRows, Math.ceil(winAbove / rh));

  // 2) 上邻域带：图片向下挂进窗口 → 锚定行必须留在窗口内。
  let backRows = 0;
  for (let a = lo - 1; a >= Math.max(0, lo - backR); a--) {
    const cover = lineImageCover(lines[a]);
    if (cover.belowPx <= 0) continue;
    if (offsetOf(a) + cover.belowPx * scale > winTop) {
      const need = lo - a;
      if (need > backRows) backRows = need;
    }
  }

  // 3) 下邻域带：图片向上挂进窗口 → 锚定行必须留在窗口内。
  let fwdRows = 0;
  const fEnd = Math.min(lines.length - 1, hi + fwdR);
  for (let a = hi; a <= fEnd; a++) {
    const cover = lineImageCover(lines[a]);
    if (cover.abovePx <= 0) continue;
    if (offsetOf(a) - cover.abovePx * scale < winBottom) {
      const need = a - hi + 1;
      if (need > fwdRows) fwdRows = need;
    }
  }

  if (backRows === 0 && fwdRows === 0) return NO_SPAN;
  return { backRows, fwdRows };
}
