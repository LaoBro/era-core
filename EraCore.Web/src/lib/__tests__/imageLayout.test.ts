import { describe, it, expect } from 'vitest';
import {
  clickToImagePixel,
  readPixel,
  readSrcmPixelFromContext,
  sortBgImages,
} from '../imageLayout';
import type { BgImageState } from '../../types/protocol';

/**
 * imageLayout 纯函数单测（issue 04，spec Q6 渲染模型 / Q7 seam ④）。
 * node 环境直测——点击偏移数学、像素读取、背景层排序全部无 DOM 依赖。
 *
 * canvas 胶水（loadImageAndReadPixel）不在此测——组件测试（happy-dom）mock 它。
 */

function imageDataOf(pixels: number[][], width: number, height: number) {
  // pixels: RGBA 行主序数组；简化构造：每像素 [r,g,b,a]
  const data = new Uint8ClampedArray(width * height * 4);
  pixels.forEach((px, i) => {
    data[i * 4] = px[0];
    data[i * 4 + 1] = px[1];
    data[i * 4 + 2] = px[2];
    data[i * 4 + 3] = px[3] ?? 255;
  });
  return { data, width, height };
}

// ---------- clickToImagePixel：点击偏移数学 ----------

describe('clickToImagePixel', () => {
  // 显示 100×50，自然像素 200×100——2x 缩放
  const rect = { left: 10, top: 20, width: 100, height: 50 };
  const natural = { width: 200, height: 100 };

  it('点击中点映射到自然像素中点', () => {
    expect(clickToImagePixel(rect, natural, 60, 45)).toEqual({ x: 100, y: 50 });
  });

  it('点击左上角映射到 (0,0)', () => {
    expect(clickToImagePixel(rect, natural, 10, 20)).toEqual({ x: 0, y: 0 });
  });

  it('点击右下角附近映射到自然尺寸右下（clamp 到最后一像素）', () => {
    // clientX=109.99 → offset=99.99 → x=floor(99.99/100*200)=199
    expect(clickToImagePixel(rect, natural, 109.99, 69.99)).toEqual({ x: 199, y: 99 });
  });

  it('点击在图片外（左/上/右/下）返回 null', () => {
    expect(clickToImagePixel(rect, natural, 9, 30)).toBeNull(); // 左
    expect(clickToImagePixel(rect, natural, 30, 19)).toBeNull(); // 上
    expect(clickToImagePixel(rect, natural, 110, 30)).toBeNull(); // 右（>= width）
    expect(clickToImagePixel(rect, natural, 30, 70)).toBeNull(); // 下（>= height）
  });

  it('零尺寸 rect 或 naturalSize 返回 null（防御）', () => {
    expect(clickToImagePixel({ left: 0, top: 0, width: 0, height: 0 }, natural, 1, 1)).toBeNull();
    expect(clickToImagePixel(rect, { width: 0, height: 0 }, 60, 45)).toBeNull();
  });
});

// ---------- readPixel：ImageData 像素读取 ----------

describe('readPixel', () => {
  // 2×2：TL=红 TR=绿 BL=蓝 BR=黄
  const img = imageDataOf(
    [
      [255, 0, 0, 255], [0, 255, 0, 255],
      [0, 0, 255, 255], [255, 255, 0, 255],
    ],
    2, 2,
  );

  it('读取像素 → 0xRRGGBB（忽略 alpha）', () => {
    expect(readPixel(img, 0, 0)).toBe(0xff0000);
    expect(readPixel(img, 1, 0)).toBe(0x00ff00);
    expect(readPixel(img, 0, 1)).toBe(0x0000ff);
    expect(readPixel(img, 1, 1)).toBe(0xffff00);
  });

  it('越界坐标返回 null', () => {
    expect(readPixel(img, -1, 0)).toBeNull();
    expect(readPixel(img, 0, -1)).toBeNull();
    expect(readPixel(img, 2, 0)).toBeNull();
    expect(readPixel(img, 0, 2)).toBeNull();
  });
});

// ---------- readSrcmPixelFromContext：canvas 胶水纯部分 ----------

describe('readSrcmPixelFromContext', () => {
  it('假 ctx 读取 1×1 区域 → 0xRRGGBB', () => {
    const fakeCtx = {
      getImageData: (_x: number, _y: number, _w: number, _h: number) =>
        imageDataOf([[12, 34, 56, 255]], 1, 1),
    } as unknown as CanvasRenderingContext2D;
    expect(readSrcmPixelFromContext(fakeCtx, 0, 0)).toBe(0x0c2238);
  });

  it('ctx 为 null 返回 null（组件防御分支）', () => {
    expect(readSrcmPixelFromContext(null, 0, 0)).toBeNull();
  });

  it('canvas 被污染（getImageData 抛 SecurityError）返回 null', () => {
    const polluted = {
      getImageData: () => {
        throw new DOMException('tainted', 'SecurityError');
      },
    } as unknown as CanvasRenderingContext2D;
    expect(readSrcmPixelFromContext(polluted, 0, 0)).toBeNull();
  });
});

// ---------- sortBgImages：背景层 depth 排序 ----------

describe('sortBgImages', () => {
  const bg = (src: string, depth: number): BgImageState => ({ src, depth, opacity: 1 });

  it('按 depth 降序排列——DOM 前者先画、在底层（WinForms：大 depth 先烘焙）', () => {
    const sorted = sortBgImages([bg('c', 5), bg('a', 0), bg('b', 2)]);
    expect(sorted.map((b) => b.depth)).toEqual([5, 2, 0]);
  });

  it('不修改入参数组', () => {
    const input = [bg('a', 3), bg('b', 1)];
    const sorted = sortBgImages(input);
    expect(sorted).not.toBe(input);
    expect(input.map((b) => b.depth)).toEqual([3, 1]); // 入参原序
  });

  it('空数组返回空数组', () => {
    expect(sortBgImages([])).toEqual([]);
  });
});
