// @vitest-environment happy-dom
import { describe, it, expect, vi } from 'vitest';
import { loadImageAndReadPixel } from '../imageLayout';

/**
 * loadImageAndReadPixel 的 canvas 复用单测（spec B：srcm 读像素 canvas 复用）。
 * 默认环境是 node（无 DOM / Image / canvas），此文件用 happy-dom，并把浏览器胶水的最小
 * 面（Image.decode / naturalWidth / canvas.getContext）替换为确定性的假实现，只验证
 * ——复用：宽高匹配时不再 createElement('canvas')；
 * ——重设：宽高变化时重设不抛错、仍读回像素。
 *
 * 注意顺序耦合：模块级缓存（cachedCanvas/cachedCtx）在用例间持久，below 的用例通过
 * 显式不同尺寸来解耦，两个 it 按书写顺序运行。
 */

// 可变自然尺寸——让两个用例各自锚定不同尺寸（避免模块级缓存互相污染）。
let naturalW = 4;
let naturalH = 4;

class FakeImage {
  src = '';
  crossOrigin: string | null = null;
  get naturalWidth(): number {
    return naturalW;
  }
  get naturalHeight(): number {
    return naturalH;
  }
  async decode(): Promise<undefined> {
    return undefined;
  }
}

// 假 2d context：drawImage 无操作，getImageData 固定返回 1 个红色像素。
const fakeCtx = {
  drawImage: () => {},
  getImageData: (_x: number, _y: number, _w: number, _h: number) => ({
    data: new Uint8ClampedArray([255, 0, 0, 255]),
    width: 1,
    height: 1,
  }),
} as unknown as CanvasRenderingContext2D;

// 首读抛 SecurityError（模拟 canvas 被污染，readSrcmPixelFromContext 返回 null），
// 之后恢复正常——用来验证"读像素失败不影响缓存状态"（spec Implementation Decisions）。
const flakyCtx = {
  drawImage: () => {},
  getImageData: () => {
    if (throwOnRead) {
      throwOnRead = false;
      throw new DOMException('tainted', 'SecurityError');
    }
    return { data: new Uint8ClampedArray([255, 0, 0, 255]), width: 1, height: 1 };
  },
} as unknown as CanvasRenderingContext2D;
let throwOnRead = true;

function patchGetContext(provider: () => CanvasRenderingContext2D = () => fakeCtx) {
  const orig = HTMLCanvasElement.prototype.getContext;
  Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
    configurable: true,
    value: () => provider(),
  });
  return () => {
    Object.defineProperty(HTMLCanvasElement.prototype, 'getContext', {
      configurable: true,
      value: orig,
    });
  };
}

function canvasCreateCount(createSpy: { mock: { calls: unknown[][] } }) {
  return createSpy.mock.calls.filter((c) => c[0] === 'canvas').length;
}

describe('loadImageAndReadPixel canvas 复用（spec B）', () => {
  it('相同尺寸连续两次只创建一个 canvas（复用生效）', async () => {
    vi.stubGlobal('Image', FakeImage);
    const restoreGetContext = patchGetContext();
    try {
      naturalW = 4;
      naturalH = 4;
      const createSpy = vi.spyOn(document, 'createElement');
      createSpy.mockClear(); // 同一 spy 跨用例会累计——只统计本用例内的 createElement 调用

      const r1 = await loadImageAndReadPixel('a.png', 0, 0);
      const r2 = await loadImageAndReadPixel('b.png', 0, 0);

      expect(r1).toBe(0xff0000);
      expect(r2).toBe(0xff0000);
      // 首次创建 canvas，第二次宽高匹配复用同一 canvas——createElement('canvas') 只有 1 次
      expect(canvasCreateCount(createSpy)).toBe(1);
    } finally {
      restoreGetContext();
      vi.unstubAllGlobals();
    }
  });

  it('尺寸变化时重设不抛错，且随后同尺寸复用', async () => {
    vi.stubGlobal('Image', FakeImage);
    const restoreGetContext = patchGetContext();
    try {
      // 与上一个用例的缓存尺寸（4）不同 → 强制重设（=重新 createElement）
      naturalW = 8;
      naturalH = 8;
      const createSpy = vi.spyOn(document, 'createElement');
      createSpy.mockClear(); // 同一 spy 跨用例会累计——只统计本用例内的 createElement 调用

      const r1 = await loadImageAndReadPixel('big.png', 0, 0); // 重设：创建新 canvas
      const r2 = await loadImageAndReadPixel('big.png', 0, 0); // 已缓存 8×8 → 复用

      expect(r1).toBe(0xff0000);
      expect(r2).toBe(0xff0000);
      expect(canvasCreateCount(createSpy)).toBe(1);
    } finally {
      restoreGetContext();
      vi.unstubAllGlobals();
    }
  });

  it('读像素失败（canvas 污染）返回 null，但不影响已缓存 canvas 的下次复用', async () => {
    vi.stubGlobal('Image', FakeImage);
    const restoreGetContext = patchGetContext(() => flakyCtx);
    try {
      // 与上一用例缓存尺寸（8）不同 → 首次调用走新建分支，并把 flakyCtx 写入缓存
      naturalW = 16;
      naturalH = 16;
      throwOnRead = true;
      const createSpy = vi.spyOn(document, 'createElement');
      createSpy.mockClear();

      const r1 = await loadImageAndReadPixel('tainted.png', 0, 0); // 读像素抛异常 → null
      const r2 = await loadImageAndReadPixel('ok.png', 0, 0); // 同尺寸复用，读到红色

      expect(r1).toBeNull(); // 失败路径：热区静默失效
      expect(r2).toBe(0xff0000); // 缓存未被失败污染，下次正常复用仍读到像素
      expect(canvasCreateCount(createSpy)).toBe(1);
    } finally {
      restoreGetContext();
      vi.unstubAllGlobals();
    }
  });
});