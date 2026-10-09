// @vitest-environment happy-dom
import { describe, it, expect, vi } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import TerminalDisplay from '../TerminalDisplay.vue';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import type { DisplaySnapshot, PrintSegment } from '../../types/protocol';

/**
 * 复现用户症状（diagnosing-bugs Phase 1——只建可红的反馈回路，不修复）：
 *
 * MAUI（WebView 内 `EraCore.Web`）中，跨多行的 ERB 图片段排版正确；但当**图片锚定的那一行**
 * 滚出可见窗口时，图片**整体消失**——即使它的箱体（锚定行顶部 + 图片高度，向下溢出）仍然
 * 与可见窗口相交。
 *
 * 关键接缝：图片 DOM 是锚定行 `.term-line` 的子节点，而 `.term-line` 只有在
 * `displayState.lines.slice(startIndex, endIndex)` 里才会被渲染（虚拟滚动）。
 * 因此「图片是否渲染」被绑在**锚定行是否落在窗口内**，而不是**图片箱体是否与窗口相交**。
 *
 * 本用例不 mock `useVirtualScroll`——走真实虚拟滚动路径，只对 happy-dom 缺失的视口量测
 * （clientHeight / scrollTop）做最小注入；资源解析与 MAUI 判定照既有测试风格 mock。
 */

vi.mock('../../lib/mauiBridge', () => ({
  isMauiEnvironment: () => false,
}));

vi.mock('../../lib/resourceResolver', () => ({
  resolveResource: (path: string) => `/assets/${path}`,
}));

/** ConfigCode.LineHeight 默认值——虚拟滚动 rowHeight = lineHeight × effectiveScale(1)。 */
const ROW_HEIGHT = 19;
/** 视口高度（px）——决定 visibleCount = ceil(600/19) = 32。 */
const VIEWPORT_H = 600;

function imgSeg(partial: Partial<PrintSegment['image']> = {}): PrintSegment {
  return {
    text: '<img>',
    image: {
      src: 'img/tall.png',
      srcb: null,
      srcm: null,
      width: 36,
      height: 18,
      ypos: 0,
      ...partial,
    },
  };
}

function textLine(label: string): DisplaySnapshot['lines'][number] {
  return { entries: [{ segments: [{ text: label }] }], align: 'left', isLineEnd: true };
}

describe('TerminalDisplay: 跨行图片的锚定行滚出窗口', () => {
  it('锚定行离开虚拟窗口、但图片箱体仍与视口相交 → 图片 DOM 应仍存在（症状：当前整体消失）', async () => {
    const pinia = createPinia();
    setActivePinia(pinia);
    const game = useGameStore();
    game.setScale(1); // 固定缩放，避免同文件/持久化残留影响 rowHeight
    const conn = useConnectionStore();
    conn.status = 'connected';

    const ANCHOR = 40; // 图片锚定的行索引
    const IMG_H = 400; // 图片高度（约 21 行）——从锚定行向下溢出，跨多行

    const lines: DisplaySnapshot['lines'][number][] = Array.from({ length: 100 }, (_, i) =>
      i === ANCHOR
        ? { entries: [{ segments: [imgSeg({ height: IMG_H })] }], align: 'left', isLineEnd: true }
        : textLine(`L${i}`),
    );
    game.displayState = {
      ...game.displayState,
      lines,
      awaiting: { input: { type: 'IntValue' } },
    };

    const wrapper = mount(TerminalDisplay, { global: { plugins: [pinia] } });
    await wrapper.vm.$nextTick();

    // happy-dom 无真实布局：给 .terminal 注入最小视口量测（clientHeight + 可写 scrollTop）
    const el = wrapper.find('.terminal').element as HTMLElement;
    let scrollTop = 0;
    Object.defineProperty(el, 'clientHeight', { get: () => VIEWPORT_H, configurable: true });
    Object.defineProperty(el, 'scrollTop', {
      get: () => scrollTop,
      set: (v: number) => { scrollTop = Math.max(0, v); },
      configurable: true,
    });

    // 滚到第 50 行顶部：rawStart=50 → 虚拟窗口 [startIndex=45, endIndex=87)
    // 锚定行 40 < 45，落在渲染窗口之外。
    scrollTop = 50 * ROW_HEIGHT; // 950
    el.dispatchEvent(new Event('scroll'));
    await wrapper.vm.$nextTick();

    // load-bearing 几何核对（在测试里显式断言，避免用例变成空转）：
    //   锚定行顶部 = 40 × 19 = 760，已滚出窗口上沿（< scrollTop=950）
    //   图片底   = 760 + 400 = 1160，仍与视口 [950, 1550] 相交 210px
    const anchorTop = ANCHOR * ROW_HEIGHT;
    const viewportTop = scrollTop;
    expect(anchorTop).toBeLessThan(viewportTop);
    expect(anchorTop + IMG_H).toBeGreaterThan(viewportTop);

    // 症状断言：箱体仍在窗口内相交，图片 DOM 就应存在（当前会因锚定行被 slice 掉而整体消失）
    expect(wrapper.find('.term-img').exists()).toBe(true);
  });
});

/**
 * 修复后的回归：渲染窗口两端按图片覆盖量扩展。
 *
 * 复用真实 useVirtualScroll 路径（不 mock），只做最小视口量测注入——
 * 与上方既有用例同款接缝。新增用例不改动既有断言。
 */
describe('TerminalDisplay: 跨行图片的渲染窗口扩展', () => {
  /** 挂载第 `lines` 行 + 注入视口量测 + 滚到 `scrollTop`（px）。`scale` 默认 1。 */
  async function mountAndScroll(
    lines: DisplaySnapshot['lines'][number][],
    scrollTop: number,
    scale = 1,
  ): Promise<VueWrapper> {
    const pinia = createPinia();
    setActivePinia(pinia);
    const game = useGameStore();
    game.setScale(scale); // 固定缩放，避免同文件/持久化残留影响 rowHeight
    const conn = useConnectionStore();
    conn.status = 'connected';
    game.displayState = {
      ...game.displayState,
      lines,
      awaiting: { input: { type: 'IntValue' } },
    };
    const wrapper = mount(TerminalDisplay, { global: { plugins: [pinia] } });
    await wrapper.vm.$nextTick();

    const el = wrapper.find('.terminal').element as HTMLElement;
    let st = 0;
    Object.defineProperty(el, 'clientHeight', { get: () => VIEWPORT_H, configurable: true });
    Object.defineProperty(el, 'scrollTop', {
      get: () => st,
      set: (v: number) => { st = Math.max(0, v); },
      configurable: true,
    });
    st = scrollTop;
    el.dispatchEvent(new Event('scroll'));
    await wrapper.vm.$nextTick();
    return wrapper;
  }

  /** `length` 行文本，其中 `anchor` 行是唯一的图片行。 */
  function linesWithImage(
    length: number,
    anchor: number,
    image: Partial<PrintSegment['image']>,
  ): DisplaySnapshot['lines'][number][] {
    return Array.from({ length }, (_, i) =>
      i === anchor
        ? { entries: [{ segments: [imgSeg(image)] }], align: 'left', isLineEnd: true }
        : textLine(`L${i}`),
    );
  }

  it('对照：锚定行在窗口内、图片向下溢出窗口底 → 图片仍渲染（未改坏正常情况）', async () => {
    const ANCHOR = 60; // 落在虚拟窗口 [45, 87) 内
    const IMG_H = 500; // 锚定行顶 1140 + 500 = 1640 > 视口底 1550 → 向下溢出
    const wrapper = await mountAndScroll(linesWithImage(100, ANCHOR, { height: IMG_H }), 50 * ROW_HEIGHT);

    // 几何核对：锚定行顶部在视口内，图片底越过视口底（向下溢出）
    const viewportTop = 50 * ROW_HEIGHT;
    expect(ANCHOR * ROW_HEIGHT).toBeGreaterThanOrEqual(viewportTop);
    expect(ANCHOR * ROW_HEIGHT + IMG_H).toBeGreaterThan(viewportTop + VIEWPORT_H);

    expect(wrapper.find('.term-img').exists()).toBe(true);
  });

  it('向上溢出：锚定行在窗口下方、图片向上盖住视口 → 图片应渲染', async () => {
    const ANCHOR = 90; // > endIndex=87，落在窗口下方
    const ABOVE = 400; // ypos = -400 → 图片向上溢出
    const wrapper = await mountAndScroll(
      linesWithImage(100, ANCHOR, { ypos: -ABOVE, height: 10 }),
      50 * ROW_HEIGHT,
    );

    // 几何核对：锚定行在视口下方，但图片上沿（锚定行顶 - 上溢量）仍与视口相交
    const viewportBottom = 50 * ROW_HEIGHT + VIEWPORT_H;
    const anchorTop = ANCHOR * ROW_HEIGHT;
    expect(anchorTop).toBeGreaterThan(viewportBottom);
    expect(anchorTop - ABOVE).toBeLessThan(viewportBottom);

    expect(wrapper.find('.term-img').exists()).toBe(true);
  });

  it('位置正确性：窗口上扩后位移 = offsetOf(新 start)，可见行像素位置不变', async () => {
    const ANCHOR = 40;
    const IMG_H = 400;
    const wrapper = await mountAndScroll(linesWithImage(100, ANCHOR, { height: IMG_H }), 50 * ROW_HEIGHT);

    // 邻域口径：只扩到「锚定行刚好回到窗口内」为止——原窗口起点 45，锚定行 40 → back = 5，
    // renderStart = 40。（全局口径会按图高恒定扩 ceil(400/19)=22 行 → renderStart = 23，
    // 多渲染 17 行用不到的 DOM。）
    const renderStart = ANCHOR; // 45 - 5
    // 位移必须按扩展后的起点算（= offsetOf(renderStart)），而不是原起点 45×19=855——
    // 否则多出来的行会把可见行整体下推，可见行像素位置漂移。
    const win = wrapper.find('.term-virtual-window');
    expect(win.attributes('style')).toContain(`translateY(${renderStart * ROW_HEIGHT}px)`);
    // 锚定行（图片行，本身无文本）确实回到了窗口内
    expect(wrapper.find('.term-img').exists()).toBe(true);
    // 上扩出来的第一行进入 DOM，且恰好只扩到锚定行——没有多扩（L39 不该出现）
    expect(wrapper.text()).toContain('L41');
    expect(wrapper.text()).not.toContain('L39');
    expect(wrapper.text()).toContain('L45');
  });

  it('远处的大图不撑开窗口——邻域无图时零扩展（全局口径会恒定扩 22 行）', async () => {
    // 图在第 100 行，窗口滚到 [495, 537)：相距 395 行 > 扫描半径 → 窗口不扩。
    const wrapper = await mountAndScroll(linesWithImage(1000, 100, { height: 400 }), 500 * ROW_HEIGHT);

    const win = wrapper.find('.term-virtual-window');
    // 未扩展：renderStart === vs.startIndex = rawStart(500) - overscan(5) = 495。
    expect(win.attributes('style')).toContain(`translateY(${495 * ROW_HEIGHT}px)`);
    expect(wrapper.text()).not.toContain('L494');
  });

  it('缩放 2 倍：行数换算不受缩放影响 → 图片仍不被卸载（回归 scale 相关的少算行数）', async () => {
    const ANCHOR = 40;
    const IMG_H = 400;
    const SCALE = 2;
    const SCALED_ROW = ROW_HEIGHT * SCALE; // 38
    // 滚到第 58 行：rawStart=58 → startIndex=53。锚定行 40 距窗口起点 13 行。
    // 旧实现把覆盖量 px 除以**缩放后**的行高（ceil(400/38)=11 < 13）→ 锚定行被切掉、图消失；
    // 正确换算（行数与缩放无关）给出 13 行。
    expect(Math.ceil(IMG_H / SCALED_ROW)).toBeLessThan(58 - 5 - ANCHOR);

    const wrapper = await mountAndScroll(
      linesWithImage(100, ANCHOR, { height: IMG_H }),
      58 * SCALED_ROW,
      SCALE,
    );

    // 几何核对：图片箱底（40×38 + 400×2 = 2320）仍越过窗口上沿（53×38 = 2014）。
    expect(ANCHOR * SCALED_ROW + IMG_H * SCALE).toBeGreaterThan(53 * SCALED_ROW);
    expect(wrapper.find('.term-img').exists()).toBe(true);
  });
});
