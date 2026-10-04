// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { lineImageCover, neighborCover, type NeighborCoverOptions } from '../imageCover';
import type { DisplayLine, PrintSegment } from '../../types/protocol';

/**
 * imageCover 单测——跨行图片相对锚定行顶部的纵向覆盖量 + 渲染窗口的邻域扩展量（纯函数，无 DOM）。
 *
 * 覆盖矩阵：
 * - ypos > 0（图片整体在行下方）→ belowPx = ypos + height，abovePx = 0
 * - ypos < 0（图片向上溢出）→ abovePx = -ypos，belowPx = ypos + height
 * - 同一行多张图片 → 各方向取 max
 * - 无 image 段 / 空行 → {0, 0}
 * - ypos 极负（图片整体在行上方）→ belowPx 钳到 0
 * - neighborCover：上扩 / 下扩 / 零扩展 / 缩放无关 / 远处大图不撑开 / 半径下限边界
 */

function imgSeg(image: Partial<PrintSegment['image']>): PrintSegment {
  return {
    text: '<img>',
    image: {
      src: 'img/x.png',
      srcb: null,
      srcm: null,
      width: 36,
      height: 18,
      ypos: 0,
      ...image,
    },
  };
}

function line(segments: PrintSegment[]): DisplayLine {
  return { entries: [{ segments }], align: 'left', isLineEnd: true };
}

function textSeg(text: string): PrintSegment {
  return { text };
}

describe('lineImageCover', () => {
  it('ypos > 0：图片整体在行下方 → belowPx = ypos + height，abovePx = 0', () => {
    expect(lineImageCover(line([imgSeg({ ypos: 5, height: 18 })]))).toEqual({
      abovePx: 0,
      belowPx: 23,
    });
  });

  it('ypos < 0：图片向上溢出 → abovePx = -ypos，belowPx = ypos + height', () => {
    expect(lineImageCover(line([imgSeg({ ypos: -8, height: 18 })]))).toEqual({
      abovePx: 8,
      belowPx: 10,
    });
  });

  it('ypos 极负：图片整体在行上方 → belowPx 钳到 0', () => {
    expect(lineImageCover(line([imgSeg({ ypos: -30, height: 10 })]))).toEqual({
      abovePx: 30,
      belowPx: 0,
    });
  });

  it('同一行多张图片（含多 entry）→ 各方向取 max', () => {
    const l: DisplayLine = {
      entries: [
        { segments: [imgSeg({ ypos: 0, height: 20 }), imgSeg({ ypos: -5, height: 10 })] },
        { segments: [textSeg('中间文本'), imgSeg({ ypos: -100, height: 10 })] },
      ],
      align: 'left',
      isLineEnd: true,
    };
    expect(lineImageCover(l)).toEqual({ abovePx: 100, belowPx: 20 });
  });

  it('无 image 段 → {0, 0}', () => {
    expect(lineImageCover(line([textSeg('hello')]))).toEqual({ abovePx: 0, belowPx: 0 });
  });

  it('空行（无 entries）→ {0, 0}', () => {
    expect(lineImageCover({ entries: [], align: null, isLineEnd: true })).toEqual({
      abovePx: 0,
      belowPx: 0,
    });
  });
});

describe('neighborCover', () => {
  /** ConfigCode.LineHeight 默认值——覆盖量 px 换算成行数用它（未缩放）。 */
  const RH = 19;

  /** 造 `n` 行文本，`imageAt` 行换成一张图（缺省即无图）。 */
  function linesWith(
    n: number,
    imageAt: number,
    image: Partial<PrintSegment['image']> = {},
  ): DisplayLine[] {
    return Array.from({ length: n }, (_, i) =>
      i === imageAt ? line([imgSeg(image)]) : line([textSeg(`L${i}`)]),
    );
  }

  /**
   * 邻域选项——`offsetOf` 按「行号 × 行高 × scale」给屏幕 px 偏移（与
   * useVirtualScroll 的定高快路径一致，无触控加高时精确）。
   */
  function opts(
    partial: Partial<NeighborCoverOptions> & Pick<NeighborCoverOptions, 'start' | 'end'>,
  ): NeighborCoverOptions {
    const scale = partial.scale ?? 1;
    return {
      rowHeightPx: RH,
      scale,
      offsetOf: (i: number) => i * RH * scale,
      ...partial,
    };
  }

  it('窗口与邻域都无图 → 零扩展', () => {
    expect(neighborCover(linesWith(100, -1), opts({ start: 45, end: 87 }))).toEqual({
      backRows: 0,
      fwdRows: 0,
    });
  });

  it('图在窗口内 → 不需要为它扩展', () => {
    // 锚定行 60 落在窗口 [45, 87) 内——它本来就被渲染，两端都不必扩。
    expect(
      neighborCover(linesWith(100, 60, { height: 400 }), opts({ start: 45, end: 87 })),
    ).toEqual({ backRows: 0, fwdRows: 0 });
  });

  it('锚定行在窗口上方、箱底越过窗口上沿 → 上扩到锚定行', () => {
    // 锚定行 40（顶 760）+ 高 400 → 箱底 1160 > 窗口上沿 855，需把第 40 行拉回窗口。
    expect(
      neighborCover(linesWith(100, 40, { height: 400 }), opts({ start: 45, end: 87 })),
    ).toEqual({ backRows: 5, fwdRows: 0 });
  });

  it('锚定行在窗口上方、但箱底没越过窗口上沿 → 不上扩', () => {
    // 箱底 760 + 50 = 810 < 855——图已完全滚出窗口，不必再为它渲染。
    expect(
      neighborCover(linesWith(100, 40, { height: 50 }), opts({ start: 45, end: 87 })),
    ).toEqual({ backRows: 0, fwdRows: 0 });
  });

  it('锚定行在窗口下方、向上挂进窗口 → 下扩到锚定行', () => {
    // 锚定行 90（顶 1710）向上溢出 400 → 箱顶 1310 < 窗口下沿 1653，需把第 90 行拉回窗口。
    expect(
      neighborCover(
        linesWith(100, 90, { ypos: -400, height: 10 }),
        opts({ start: 45, end: 87 }),
      ),
    ).toEqual({ backRows: 0, fwdRows: 4 });
  });

  it('扩展行数与缩放无关——scale 1 与 scale 2 结果相同（回归：用缩放后行高换算会少算）', () => {
    const lines = linesWith(100, 40, { height: 400 });
    // 窗口上移到 58：需上扩 18 行才能留住锚定行 40（旧实现在 scale=2 只给 ceil(400/38)=11 行）。
    const at1 = neighborCover(lines, opts({ start: 58, end: 100, scale: 1 }));
    const at2 = neighborCover(lines, opts({ start: 58, end: 100, scale: 2 }));
    expect(at1).toEqual({ backRows: 18, fwdRows: 0 });
    expect(at2).toEqual(at1);
  });

  it('远处的大图不撑开窗口（邻域口径 vs 全局口径）', () => {
    // 图在 100 行、窗口在 [495, 537)——相距 395 行，远超扫描半径 → 零扩展。
    // 全局口径会因为它恒定上扩 ceil(400/19)=22 行。
    expect(
      neighborCover(linesWith(1000, 100, { height: 400 }), opts({ start: 495, end: 537 })),
    ).toEqual({ backRows: 0, fwdRows: 0 });
  });

  it('半径下限决定保护范围——超出半径的锚定行不计入（调大 baseRadiusPx 即可覆盖）', () => {
    const lines = linesWith(100, 30, { height: 400 });
    // baseRadiusPx=100 → 半径 6 行；锚定行 30 距窗口起点 15 行 → 不在带内。
    expect(
      neighborCover(lines, opts({ start: 45, end: 87, baseRadiusPx: 100 })),
    ).toEqual({ backRows: 0, fwdRows: 0 });
    // 默认半径（4096px ≈ 216 行）覆盖到 → 上扩 15 行。
    expect(neighborCover(lines, opts({ start: 45, end: 87 }))).toEqual({
      backRows: 15,
      fwdRows: 0,
    });
  });

  it('空行列表 / 空窗口 → 零扩展', () => {
    expect(neighborCover([], opts({ start: 0, end: 0 }))).toEqual({
      backRows: 0,
      fwdRows: 0,
    });
    expect(neighborCover(linesWith(10, 3, { height: 100 }), opts({ start: 5, end: 5 }))).toEqual(
      { backRows: 0, fwdRows: 0 },
    );
  });
});
