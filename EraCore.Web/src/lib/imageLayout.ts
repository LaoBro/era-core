import type { BgImageState } from '../types/protocol';

/**
 * 图片/背景渲染的纯函数集合（issue 04，spec Q6 渲染模型）。
 * 全部无 DOM 依赖（node 环境直测）——浏览器胶水只有 loadImageAndReadPixel 一个。
 *
 * 职责边界：
 * - clickToImagePixel：点击事件坐标 → srcm 映射图像素坐标（等比缩放，display→natural）
 * - readPixel：ImageData 单像素 → 0xRRGGBB（忽略 alpha，对齐 WinForms GetMappedColor）
 * - readSrcmPixelFromContext：canvas 2d context 读像素（canvas 污染/null 防御）
 * - sortBgImages：背景层 depth 排序（WinForms 降序烘焙语义）
 * - loadImageAndReadPixel：浏览器胶水——加载 srcm 图 → 画离屏 canvas → 读像素
 */

export interface PixelCoord {
  x: number;
  y: number;
}

/**
 * 点击事件坐标 → srcm 映射图的自然像素坐标。
 *
 * `rect` 是图片元素 getBoundingClientRect() 结果（CSS 显示尺寸），`naturalSize` 是
 * srcm 图自然像素尺寸（img.naturalWidth/naturalHeight）。显示尺寸可能被掩膜/缩放改变，
 * 按等比映射回自然像素坐标——与 WinForms 点色查颜色的语义一致。
 *
 * 点击落在图片外（含右/下边缘）返回 null——不触发热区。
 *
 * @returns 自然像素坐标（clamp 到 [0, natural-1]），或 null（图片外/零尺寸）
 */
export function clickToImagePixel(
  rect: { left: number; top: number; width: number; height: number },
  naturalSize: { width: number; height: number },
  clientX: number,
  clientY: number,
): PixelCoord | null {
  if (rect.width <= 0 || rect.height <= 0 || naturalSize.width <= 0 || naturalSize.height <= 0) {
    return null;
  }
  const offsetX = clientX - rect.left;
  const offsetY = clientY - rect.top;
  if (offsetX < 0 || offsetY < 0 || offsetX >= rect.width || offsetY >= rect.height) {
    return null;
  }
  const x = Math.min(naturalSize.width - 1, Math.floor((offsetX / rect.width) * naturalSize.width));
  const y = Math.min(naturalSize.height - 1, Math.floor((offsetY / rect.height) * naturalSize.height));
  return { x, y };
}

/**
 * ImageData 单像素 → 0xRRGGBB（忽略 alpha——WinForms `GetMappedColor` 返回 0xRRGGBB）。
 * 越界返回 null。
 */
export function readPixel(
  imageData: { data: Uint8ClampedArray; width: number; height: number },
  x: number,
  y: number,
): number | null {
  if (x < 0 || y < 0 || x >= imageData.width || y >= imageData.height) {
    return null;
  }
  const i = (y * imageData.width + x) * 4;
  const r = imageData.data[i];
  const g = imageData.data[i + 1];
  const b = imageData.data[i + 2];
  return (r << 16) | (g << 8) | b;
}

/**
 * canvas 2d context 读 (x,y) 像素 → 0xRRGGBB。
 * 防御：ctx 为 null（getContext 失败）或 canvas 被污染（跨域图无 CORS，getImageData 抛
 * SecurityError）→ null，不冒泡异常。
 */
export function readSrcmPixelFromContext(
  ctx: CanvasRenderingContext2D | null,
  x: number,
  y: number,
): number | null {
  if (!ctx) return null;
  let imageData: ImageData;
  try {
    imageData = ctx.getImageData(x, y, 1, 1);
  } catch {
    // canvas 被污染 / 平台不支持——热区静默失效（图片仍可显示）
    return null;
  }
  return readPixel(imageData, 0, 0);
}

/**
 * 背景层排序：按 depth 升序返回（渲染时数组后者在上）。
 * WinForms 是"降序烘焙"——depth 大的图最后绘制、视觉在上；CSS 层叠同 z-index 下
 * DOM 顺序后者在上，故升序数组 + 顺序渲染 = 大 depth 在上，语义对齐。
 * 不修改入参（返回新数组）。
 */
export function sortBgImages(bgImages: BgImageState[]): BgImageState[] {
  return [...bgImages].sort((a, b) => a.depth - b.depth);
}

// 模块级单例离屏 canvas + ctx 缓存（spec B：srcm 读像素 canvas 复用）。
// 连点读像素时复用同一 canvas，只在尺寸不匹配时才重设（重设 = 重新分配，离散发生），
// 避免每次点击分配全尺寸 canvas（大图 8MB+ 级）造成峰值抖动。
let cachedCanvas: HTMLCanvasElement | null = null;
let cachedCtx: CanvasRenderingContext2D | null = null;

/**
 * 浏览器胶水：加载 srcm 图并读取 (x,y) 像素色（0xRRGGBB）。
 * - `crossOrigin='anonymous'`：配合服务端 CORS 头（03 已带 Access-Control-Allow-Origin:*）
 *   使 canvas 可读回像素；缺 CORS 时 getImageData 抛 SecurityError → 返回 null
 * - 离屏 canvas 复用：宽高匹配时不重建（见模块级缓存注释）；读像素失败/加载失败
 *   不影响缓存状态，下一次点击仍走正常路径
 * - 组件调用；组件测试 mock 此函数（canvas 细节不在组件测试里驱动）
 *
 * @returns 0xRRGGBB，或 null（加载失败/零尺寸/读像素失败）
 */
export async function loadImageAndReadPixel(srcmUrl: string, x: number, y: number): Promise<number | null> {
  const img = new Image();
  img.crossOrigin = 'anonymous';
  img.src = srcmUrl;
  try {
    await img.decode();
  } catch {
    return null;
  }
  if (img.naturalWidth <= 0 || img.naturalHeight <= 0) {
    return null;
  }
  // 宽高匹配则复用缓存 canvas，否则重设尺寸（重设即在原尺寸上重新分配）。
  if (cachedCanvas && cachedCtx &&
      cachedCanvas.width === img.naturalWidth && cachedCanvas.height === img.naturalHeight) {
    const ctx = cachedCtx;
    ctx.drawImage(img, 0, 0);
    return readSrcmPixelFromContext(ctx, x, y);
  }
  const canvas = document.createElement('canvas');
  canvas.width = img.naturalWidth;
  canvas.height = img.naturalHeight;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;
  cachedCanvas = canvas;
  cachedCtx = ctx;
  ctx.drawImage(img, 0, 0);
  return readSrcmPixelFromContext(ctx, x, y);
}
