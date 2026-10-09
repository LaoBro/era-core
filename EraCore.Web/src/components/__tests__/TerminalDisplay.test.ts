// @vitest-environment happy-dom
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import TerminalDisplay from '../TerminalDisplay.vue';
import { useGameStore } from '../../stores/game';
import { useConnectionStore } from '../../stores/connection';
import { useUiStore } from '../../stores/ui';
import { loadImageAndReadPixel } from '../../lib/imageLayout';
import type { DisplaySnapshot, PrintSegment, BgImageState } from '../../types/protocol';

vi.mock('../../lib/mauiBridge', () => ({
  isMauiEnvironment: () => true,
}));

// TerminalDisplay 的空状态测试需要 MAUI 环境；资源 URL 断言仍沿用 Web 端测试路径。
vi.mock('../../lib/resourceResolver', () => ({
  resolveResource: (path: string) => `/assets/${path}`,
}));

/**
 * TerminalDisplay 组件测试（issue 04，spec Q7 seam ④——首个 happy-dom 组件测试）。
 *
 * 覆盖 Q6 渲染模型的模板层行为：
 * - 掩膜渲染：image segment → 掩膜 span + img（src 经 resolveResource 拼 /assets）
 * - 溢出：img margin-top = ypos（负值向上溢出）
 * - rect 渲染色块
 * - srcb 悬停切换 <img src>
 * - srcm 热区：点击 → canvas 读像素（mock）→ 提交十进制色值 + 阻断按钮冒泡
 * - 非 srcm 图片在按钮内：点击冒泡走既有按钮路径
 * - 背景层：bgImages 渲染 + depth 排序 + opacity
 *
 * 虚拟滚动/捏合缩放 mock（模板层的行切片与手势与此测试无关）；
 * canvas 胶水 loadImageAndReadPixel mock（浏览器像素读取不在组件测试驱动）。
 */

vi.mock('../../composables/useVirtualScroll', () => ({
  useVirtualScroll: () => ({
    startIndex: { value: 0 },
    endIndex: { value: 200 },
    totalHeight: { value: 1900 },
    offsetY: { value: 0 },
    // 渲染窗口扩展后窗口位移改用 offsetOf(新 start)——mock 按 rowHeight 19 线性给出即可。
    offsetOf: (i: number) => i * 19,
    isStickyToBottom: { value: true },
    adjustScrollTop: vi.fn(),
    scrollToBottom: vi.fn(),
  }),
}));

vi.mock('../../composables/usePinchZoom', () => ({
  usePinchZoom: () => {},
}));

vi.mock('../../lib/imageLayout', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../lib/imageLayout')>();
  return { ...actual, loadImageAndReadPixel: vi.fn() };
});

function imgSeg(partial: Partial<PrintSegment['image']> = {}): PrintSegment {
  return {
    text: '<img>',
    image: {
      src: 'img/test.png',
      srcb: null,
      srcm: null,
      width: 36,
      height: 18,
      ypos: 0,
      ...partial,
    },
  };
}

function rectSeg(): PrintSegment {
  return { text: '<rect>', shape: { type: 'rect', x: 0, y: 0, width: 18, height: 18, color: '#FF0000' } };
}

function line(segments: PrintSegment[]): DisplaySnapshot['lines'][number] {
  return { entries: [{ segments }], align: 'left', isLineEnd: true };
}

async function mountWith(lines: DisplaySnapshot['lines'][number][], bgImages: BgImageState[] = [], scale?: number) {
  const pinia = createPinia();
  setActivePinia(pinia);
  const game = useGameStore();
  // issue 09：可选缩放——SegmentRenderer 从 store 读 effectiveScale 计算图片几何
  if (scale !== undefined) game.setScale(scale);
  const conn = useConnectionStore();
  conn.status = 'connected';
  conn.sendInput = vi.fn();
  // Pinia setup store 自动解包 ref——displayState 是值不是 Ref，直接整体赋值。
  // issue 07：路由判据改读 awaiting ⇒ 夹具必须给出 input 维度（否则按钮一律视为不可提交）。
  game.displayState = {
    ...game.displayState,
    lines,
    bgImages,
    awaiting: { input: { type: 'IntValue' } },
  };
  game.currentTurnGeneration = 0;
  const wrapper = mount(TerminalDisplay, { global: { plugins: [pinia] } });
  return { wrapper, game, conn };
}

describe('TerminalDisplay: image 掩膜渲染（Q6）', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('image segment 渲染掩膜 span + img（src 经 resolveResource 拼 /assets）', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    const mask = wrapper.find('.term-mask');
    expect(mask.exists()).toBe(true);
    const img = wrapper.find('.term-img');
    expect(img.attributes('src')).toBe('/assets/img/test.png');
    // issue 09：img 尺寸在 style（× effectiveScale），不再用 width/height attribute
    expect(img.attributes('width')).toBeUndefined();
    expect(img.attributes('style')).toContain('width: 36px');
    expect(img.attributes('style')).toContain('height: 18px');
  });

  it('掩膜高度钉死=行高（CSS 变量），overflow 可见，宽度×缩放', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    const mask = wrapper.find('.term-mask');
    const style = mask.attributes('style') ?? '';
    expect(style).toContain('var(--term-line-min-height');
    expect(style).toContain('overflow: visible');
    expect(style).toContain('vertical-align: top');
    // issue 09：掩膜宽度也吃缩放（此前写死 px 不随缩放）
    expect(style).toContain('width: 36px');
  });

  it('ypos 溢出：img margin-top = ypos×缩放（可为负）', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ ypos: -8 })])]);
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('margin-top: -8px');
  });

  // ---------- issue 07（协议 v9）：crop 裁切渲染 ----------

  it('crop 裁切：掩膜内嵌 overflow:hidden 定尺寸容器 + img 负偏移', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ crop: { x: -36, y: 0, imgWidth: 72, imgHeight: 36 } })])]);
    // 外层掩膜保持 overflow:visible（"后行盖先行"语义不破坏）
    const mask = wrapper.find('.term-mask');
    expect(mask.attributes('style')).toContain('overflow: visible');
    // 容器：定尺寸（裁切后显示尺寸，×缩放）+ overflow:hidden
    const crop = wrapper.find('.term-crop');
    expect(crop.exists()).toBe(true);
    const style = crop.attributes('style') ?? '';
    expect(style).toContain('overflow: hidden');
    expect(style).toContain('width: 36px');
    expect(style).toContain('height: 18px');
    // img：元素尺寸 = 整图×缩放（72×36，再×缩放），负偏移 margin-left
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('width: 72px');
    expect(img.attributes('style')).toContain('height: 36px');
    expect(img.attributes('style')).toContain('margin-left: -36px');
    expect(img.attributes('style')).toContain('margin-top: 0px');
  });

  it('crop 裁切：ypos 转移到容器（img 不再带 margin-top）', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ ypos: -8, crop: { x: 0, y: -4, imgWidth: 72, imgHeight: 36 } })])]);
    const crop = wrapper.find('.term-crop');
    expect(crop.attributes('style')).toContain('margin-top: -8px');
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('margin-top: -4px'); // crop.y 在 img 上
    expect(img.attributes('style')).not.toContain('margin-top: -8px');
  });

  it('无 crop 图片不渲染 term-crop 容器（v8 行为锁定）', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    expect(wrapper.find('.term-crop').exists()).toBe(false);
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('width: 36px');
    expect(img.attributes('style')).toContain('height: 18px');
  });

  it('rect segment 渲染掩膜 + 色块（background 颜色，尺寸×缩放）', async () => {
    const { wrapper } = await mountWith([line([rectSeg()])]);
    const rect = wrapper.find('.term-rect');
    expect(rect.exists()).toBe(true);
    expect(rect.attributes('style')).toContain('background-color: #FF0000');
    expect(rect.attributes('style')).toContain('width: 18px');
  });

  it('rect segment 保留 x 偏移并为完整几何预留流宽', async () => {
    const { wrapper } = await mountWith([line([{
      text: '<rect>',
      shape: { type: 'rect', x: 6, y: 0, width: 18, height: 2, color: '#00FF00' },
    }])]);
    const mask = wrapper.find('.term-mask');
    const rect = wrapper.find('.term-rect');

    expect(mask.attributes('style')).toContain('width: 24px');
    expect(rect.attributes('style')).toContain('left: 6px');
  });

  it('underline 空格段使用 text-decoration（Chromium/WebView2 实测可靠）', async () => {
    const { wrapper } = await mountWith([line([{
      text: '  ',
      color: '#40C040',
      underline: true,
    }])]);
    const segment = wrapper.find('.term-seg');
    const style = segment.attributes('style') ?? '';

    expect(style).toContain('text-decoration: underline');
    expect(style).not.toContain('background-image');
    expect(style).not.toContain('display: inline-block');
  });

  it('underline 普通文本使用 CSS 下划线', async () => {
    const { wrapper } = await mountWith([line([{
      text: 'text',
      underline: true,
    }])]);

    expect(wrapper.find('.term-seg').attributes('style')).toContain('text-decoration: underline');
  });

  it('strikeout 空格段使用 text-decoration: line-through（Chromium 对纯空格可靠）', async () => {
    const { wrapper } = await mountWith([line([{
      text: '  ',
      color: '#40C040',
      strikeout: true,
    }])]);
    const segment = wrapper.find('.term-seg');
    const style = segment.attributes('style') ?? '';

    expect(style).toContain('text-decoration: line-through');
    expect(style).not.toContain('background-image');
    expect(style).not.toContain('display: inline-block');
  });

  it('strikeout 普通文本使用 CSS 删除线', async () => {
    const { wrapper } = await mountWith([line([{
      text: 'text',
      strikeout: true,
    }])]);

    expect(wrapper.find('.term-seg').attributes('style')).toContain('text-decoration: line-through');
  });

  it('underline+strikeout 叠加为 "underline line-through"', async () => {
    const { wrapper } = await mountWith([line([{
      text: '  ',
      underline: true,
      strikeout: true,
    }])]);

    expect(wrapper.find('.term-seg').attributes('style')).toContain('text-decoration: underline line-through');
  });

  // ---------- issue 09：缩放（effectiveScale 作用于图片几何） ----------

  it('缩放 1.5 倍：掩膜/图片/crop 全部几何等比放大', async () => {
    const { wrapper } = await mountWith(
      [line([
        imgSeg({ ypos: -8, crop: { x: -36, y: 0, imgWidth: 72, imgHeight: 36 } }),
        { text: '  ', underline: true },
      ])],
      [],
      1.5,
    );
    // 掩膜宽 36×1.5=54；img 72×1.5=108；crop.y=-36×1.5=-54；ypos -8×1.5=-12
    const mask = wrapper.find('.term-mask');
    expect(mask.attributes('style')).toContain('width: 54px');
    const crop = wrapper.find('.term-crop');
    expect(crop.attributes('style')).toContain('width: 54px');
    expect(crop.attributes('style')).toContain('height: 27px');
    expect(crop.attributes('style')).toContain('margin-top: -12px');
    const img = wrapper.find('.term-img');
    expect(img.attributes('style')).toContain('width: 108px');
    expect(img.attributes('style')).toContain('height: 54px');
    expect(img.attributes('style')).toContain('margin-left: -54px');
    const rail = wrapper.findAll('.term-seg').find((s) =>
      (s.attributes('style') ?? '').includes('text-decoration: underline'),
    );
    expect(rail).toBeDefined();
  });

  // ---------- 掩膜静态定位（不进入定位层，DOM 序"后行盖先行"） ----------

  it('文本段不设 position（静态定位——issue 09 曾加 relative 已回退）', async () => {
    const { wrapper } = await mountWith([
      line([imgSeg()]),
      line([{ text: '后行文本', color: null, bold: null, italic: null, fontname: null }]),
    ]);
    const textSeg = wrapper.findAll('.term-seg').find((s) => s.text() === '后行文本');
    expect(textSeg).toBeDefined();
    // 回退后文本段无任何 style（Vue 不渲染空 style 属性）——不含 position
    expect((textSeg!.attributes('style') ?? '')).not.toContain('position:');
  });

  it('图片/矩形掩膜不设 position（保持静态，不进入定位层盖文本）', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ ypos: -50 })])]);
    const mask = wrapper.find('.term-mask');
    expect(mask.attributes('style')).not.toContain('position:');
  });
});

describe('TerminalDisplay: MAUI 空状态', () => {
  async function mountEmpty(gameDir: string | null, serverState: 'Idle' | 'Loading' | 'WaitInput') {
    const { wrapper, game } = await mountWith([]);
    game.gameDir = gameDir;
    game.serverState = serverState;
    await wrapper.vm.$nextTick();
    return wrapper;
  }

  it('选择游戏后启动期间显示启动提示，不要求点击快速重开', async () => {
    const wrapper = await mountEmpty('D:/games/mygame', 'Idle');
    expect(wrapper.find('.terminal-empty').text()).toContain('游戏正在启动，请稍候…');
    expect(wrapper.find('.terminal-empty').text()).not.toContain('快速重开');
  });

  it('Loading 状态显示加载提示', async () => {
    const wrapper = await mountEmpty('D:/games/mygame', 'Loading');
    expect(wrapper.find('.terminal-empty').text()).toContain('游戏加载中，请稍候…');
  });

  it('未选择游戏时显示选择游戏提示', async () => {
    const wrapper = await mountEmpty(null, 'Idle');
    expect(wrapper.find('.terminal-empty').text()).toContain('请选择一个游戏');
  });
});

describe('TerminalDisplay: srcb 悬停切换', () => {
  it('mouseenter 切换 img src 到 srcb，mouseleave 还原', async () => {
    const { wrapper } = await mountWith([line([imgSeg({ srcb: 'img/hover.png' })])]);
    const img = wrapper.find('.term-img');
    expect(img.attributes('src')).toBe('/assets/img/test.png');

    await wrapper.find('.term-mask').trigger('mouseenter');
    expect(wrapper.find('.term-img').attributes('src')).toBe('/assets/img/hover.png');

    await wrapper.find('.term-mask').trigger('mouseleave');
    expect(wrapper.find('.term-img').attributes('src')).toBe('/assets/img/test.png');
  });

  it('无 srcb 的图片 mouseenter 不切换', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    await wrapper.find('.term-mask').trigger('mouseenter');
    expect(wrapper.find('.term-img').attributes('src')).toBe('/assets/img/test.png');
  });
});

describe('TerminalDisplay: srcm 热区点击', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('srcm 图片点击 → 提交映射色（十进制）且阻断按钮冒泡', async () => {
    vi.mocked(loadImageAndReadPixel).mockResolvedValue(0xff0000);
    // srcm 图在按钮 entry 内，按钮原值 = 999
    const seg = imgSeg({ srcm: 'img/map.png' });
    const snap = {
      lines: [{
        entries: [{ segments: [seg], button: { value: 999, isInteger: true, generation: 0 } }],
        align: 'left' as const,
        isLineEnd: true,
      }],
      bgColor: '#000000',
      bgImages: [],
      awaiting: { input: { type: 'IntValue' } },
      protocolVersion: 12,
      generation: 0,
    };
    const { wrapper, conn } = await mountWith(snap.lines);

    // mock getBoundingClientRect：img 元素（onImageClick 用 img 自身 rect）display 尺寸 36×18 @ (0,0)
    const imgEl = wrapper.find('.term-img');
    imgEl.element.getBoundingClientRect = () => ({
      left: 0, top: 0, width: 36, height: 18, right: 36, bottom: 18,
      x: 0, y: 0, toJSON: () => ({}),
    });
    // naturalWidth/Height 是只读 getter（happy-dom），defineProperty 重定义
    Object.defineProperty(imgEl.element, 'naturalWidth', { value: 36, configurable: true });
    Object.defineProperty(imgEl.element, 'naturalHeight', { value: 18, configurable: true });

    await wrapper.find('.term-mask').trigger('click', { clientX: 18, clientY: 9 });

    expect(loadImageAndReadPixel).toHaveBeenCalledWith('/assets/img/map.png', 18, 9);
    expect(conn.sendInput).toHaveBeenCalledWith('16711680'); // 0xFF0000 十进制
    expect(conn.sendInput).not.toHaveBeenCalledWith('999'); // 未提交按钮原值
  });

  it('srcm 读像素失败（null）→ 不提交、但已阻断按钮冒泡（热区接管语义）', async () => {
    vi.mocked(loadImageAndReadPixel).mockResolvedValue(null);
    const seg = imgSeg({ srcm: 'img/map.png' });
    const snap = {
      lines: [{
        entries: [{ segments: [seg], button: { value: 999, isInteger: true, generation: 0 } }],
        align: 'left' as const,
        isLineEnd: true,
      }],
      bgColor: '#000000',
      bgImages: [],
      awaiting: { input: { type: 'IntValue' } },
      protocolVersion: 12,
      generation: 0,
    };
    const { wrapper, conn } = await mountWith(snap.lines);
    const imgEl = wrapper.find('.term-img');
    imgEl.element.getBoundingClientRect = () => ({
      left: 0, top: 0, width: 36, height: 18, right: 36, bottom: 18,
      x: 0, y: 0, toJSON: () => ({}),
    });
    // 必须 mock natural 尺寸——否则零尺寸守卫让 clickToImagePixel 提前返回 null，
    // 测不到"读像素失败"路径（review 2026-08-06 假用例修复）
    Object.defineProperty(imgEl.element, 'naturalWidth', { value: 36, configurable: true });
    Object.defineProperty(imgEl.element, 'naturalHeight', { value: 18, configurable: true });

    await wrapper.find('.term-mask').trigger('click', { clientX: 18, clientY: 9 });

    expect(loadImageAndReadPixel).toHaveBeenCalled();
    expect(conn.sendInput).not.toHaveBeenCalled(); // 读像素失败不提交
  });

  it('非 srcm 图片在按钮内 → 点击冒泡提交按钮原值', async () => {
    const seg = imgSeg(); // 无 srcm
    const snap = {
      lines: [{
        entries: [{ segments: [seg], button: { value: 42, isInteger: true, generation: 0 } }],
        align: 'left' as const,
        isLineEnd: true,
      }],
      bgColor: '#000000',
      bgImages: [],
      awaiting: { input: { type: 'IntValue' } },
      protocolVersion: 12,
      generation: 0,
    };
    const { wrapper, conn } = await mountWith(snap.lines);

    await wrapper.find('.term-mask').trigger('click', { clientX: 10, clientY: 5 });

    expect(conn.sendInput).toHaveBeenCalledWith('42');
  });
});

describe('TerminalDisplay: 背景图层', () => {
  it('bgImages 渲染 + depth 排序（大 depth 在后/在上）+ opacity', async () => {
    const bgImages: BgImageState[] = [
      { src: 'bg/top.png', depth: 5, opacity: 0.8 },
      { src: 'bg/bottom.png', depth: 0, opacity: 0.4 },
      { src: 'bg/mid.png', depth: 2, opacity: 1 },
    ];
    const { wrapper } = await mountWith([line([imgSeg()])], bgImages);

    const layer = wrapper.find('.term-bg-layer');
    expect(layer.exists()).toBe(true);
    expect(layer.attributes('style')).toContain('position: fixed');
    expect(layer.attributes('style')).toContain('z-index: -1');

    const imgs = wrapper.findAll('.term-bg-img');
    expect(imgs).toHaveLength(3);
    // 排序后：depth 0 → 2 → 5（渲染顺序=升序，大 depth 最后画=视觉在上）
    expect(imgs[0].attributes('src')).toBe('/assets/bg/bottom.png');
    expect(imgs[1].attributes('src')).toBe('/assets/bg/mid.png');
    expect(imgs[2].attributes('src')).toBe('/assets/bg/top.png');
    expect(imgs[0].attributes('style')).toContain('opacity: 0.4');
    expect(imgs[2].attributes('style')).toContain('opacity: 0.8');
  });

  it('无 bgImages 不渲染背景层', async () => {
    const { wrapper } = await mountWith([line([imgSeg()])]);
    expect(wrapper.find('.term-bg-layer').exists()).toBe(false);
  });

  // ---------- issue 记忆-内存优化 spec D：过滤 opacity<=0 的隐藏层 ----------

  it('bgImages 含 opacity=0 时不渲染对应 <img>（隐藏层不请求/解码）', async () => {
    const bgImages: BgImageState[] = [
      { src: 'bg/visible.png', depth: 0, opacity: 1 },
      { src: 'bg/hidden.png', depth: 1, opacity: 0 },
      { src: 'bg/fading.png', depth: 2, opacity: 0.5 },
    ];
    const { wrapper } = await mountWith([line([imgSeg()])], bgImages);

    const imgs = wrapper.findAll('.term-bg-img');
    expect(imgs).toHaveLength(2);
    const srcs = imgs.map((i) => i.attributes('src'));
    expect(srcs).not.toContain('/assets/bg/hidden.png');
    expect(srcs).toContain('/assets/bg/visible.png');
    expect(srcs).toContain('/assets/bg/fading.png');
  });

  it('全 opacity=0 时背景层整体隐藏（v-if="sortedBgImages.length > 0" 生效）', async () => {
    const bgImages: BgImageState[] = [
      { src: 'bg/a.png', depth: 0, opacity: 0 },
      { src: 'bg/b.png', depth: 1, opacity: 0 },
    ];
    const { wrapper } = await mountWith([line([imgSeg()])], bgImages);
    expect(wrapper.find('.term-bg-layer').exists()).toBe(false);
  });
});

// ---------- 默认文字色（ConfigCode.ForeColor） ----------
//
// turn 协议只在「与默认色不同」的 segment 上带 color，普通文字段 color 为 null；
// 渲染端靠 store 的 defaultTextColor（来自 GET /state / MAUI layout 的 foreColor）
// 覆盖 .terminal 的 --term-color-text，否则落回主题默认色（与原版 Emuera/CLI 不符）。
describe('TerminalDisplay: 默认文字色', () => {
  /** 挂载前写入 layout 默认文字色（mountWith 内部自建 store，故此处自行挂载）。 */
  function mountWithForeColor(foreColor: string | null) {
    const pinia = createPinia();
    setActivePinia(pinia);
    const game = useGameStore();
    if (foreColor !== null) game.setGameLayout({ foreColor });
    const conn = useConnectionStore();
    conn.status = 'connected';
    conn.sendInput = vi.fn();
    game.displayState = { ...game.displayState, lines: [line([{ text: 'こんにちは' }])] };
    return mount(TerminalDisplay, { global: { plugins: [pinia] } });
  }

  it('读到默认文字色时覆盖 --term-color-text（.term-btn 也引用该变量）', () => {
    const wrapper = mountWithForeColor('#7F0000');
    const style = wrapper.find('.terminal').attributes('style') ?? '';
    expect(style).toContain('--term-color-text: #7F0000');
  });

  it('未读到默认文字色时不覆盖（保留主题令牌作 fallback）', () => {
    const wrapper = mountWithForeColor(null);
    const style = wrapper.find('.terminal').attributes('style') ?? '';
    expect(style).not.toContain('--term-color-text');
  });
});

// ---------- 触控排版（按钮盒 ≥48px + 横向按需外扩 + 纵向条件抬高） ----------
//
// 判定规则与虚拟滚动加高表同源（inputRouting.isLiveButton）：
// 只有「当前回合 generation 且非 EnterKey/AnyKey 惰性态」的按钮才会套上触控盒。
// 默认开关按平台走（Android 开 / Web 关），测试里显式置位，
// 并在 afterEach 清掉持久化，避免污染同文件后续用例。
describe('TerminalDisplay: 触控排版', () => {
  /** 一个按钮 entry（默认不带 col/width 几何）。 */
  function btnEntry(generation: number) {
    return { segments: [{ text: '[0] 選項' }], button: { value: 0, isInteger: true, generation } };
  }

  function btnLine(generation: number): DisplaySnapshot['lines'][number] {
    return { entries: [btnEntry(generation)], align: 'left', isLineEnd: true };
  }

  /**
   * 按列几何拼一行——省略 `col` 的 part 是普通文本 entry，否则是带 `col`/`width` 的按钮 entry。
   * generation 默认 0（当前回合）；`width` 默认 2（一个全角字）。
   */
  function geoLine(
    parts: Array<{ text: string; col?: number; width?: number; generation?: number }>,
  ): DisplaySnapshot['lines'][number] {
    return {
      entries: parts.map((p) =>
        p.col === undefined
          ? { segments: [{ text: p.text }], button: null }
          : {
              segments: [{ text: p.text }],
              button: { value: 0, isInteger: true, generation: p.generation ?? 0, col: p.col, width: p.width ?? 2 },
            },
      ),
      align: 'left',
      isLineEnd: true,
    };
  }

  /** 按钮内联注入的热区外扩列数（ch）——未注入视为 0。 */
  function hitCols(el: Element, side: 'left' | 'right'): number {
    const inline = el.getAttribute('style') ?? '';
    const m = new RegExp(`--hit-${side[0]}:\\s*(-?[\\d.]+)ch`).exec(inline);
    return m ? Number(m[1]) : 0;
  }

  async function mountTouch(opts: {
    inputType?: string;
    turnGeneration?: number;
    lineGeneration?: number;
    lines?: DisplaySnapshot['lines'][number][];
    targetCols?: number;
    lineHeight?: number;
  } = {}) {
    // scale 显式传 1——game store 会持久化缩放，同文件前面的用例可能留下 1.5
    const lines = opts.lines ?? [
      line([{ text: '普通文本行' }]),
      btnLine(opts.lineGeneration ?? 0),
    ];
    const { wrapper, game } = await mountWith(lines, [], 1);
    // pinia 由 mountWith 创建，store 必须在那之后取（否则拿到的是前一个 pinia 的实例）
    const ui = useUiStore();
    ui.setTouchLayout(true);
    if (opts.targetCols !== undefined) ui.setTouchTargetCols(opts.targetCols);
    if (opts.lineHeight !== undefined) game.setGameLayout({ lineHeight: opts.lineHeight });
    game.currentTurnGeneration = opts.turnGeneration ?? 0;
    // 触控排版判据读 awaiting.input ⇒ 夹具直接把类型喂给契约字段
    const inputType = opts.inputType ?? 'IntValue';
    game.displayState = {
      ...game.displayState,
      awaiting: { input: { type: inputType } },
    };
    await wrapper.vm.$nextTick();
    return wrapper;
  }

  afterEach(() => {
    try {
      localStorage.removeItem('app.touchLayout');
      localStorage.removeItem('app.touchLayout.targetCols');
    } catch { /* 静默 */ }
  });

  it('默认（开关关）按钮不带触控盒类——桌面/关闭态零视觉变化', async () => {
    const { wrapper } = await mountWith([btnLine(0)]);
    expect(wrapper.find('.term-btn').classes()).not.toContain('term-btn-touch');
  });

  it('开关开 + 当前回合按钮 → 该按钮为触控目标', async () => {
    const wrapper = await mountTouch();
    expect(wrapper.find('.term-btn').classes()).toContain('term-btn-touch');
  });

  it('历史回合残留按钮（generation 落后）→ 不撑行，避免白白占屏', async () => {
    const wrapper = await mountTouch({ turnGeneration: 7, lineGeneration: 3 });
    expect(wrapper.find('.term-btn').classes()).not.toContain('term-btn-touch');
  });

  it('EnterKey/AnyKey 惰性态 → 按钮不是触控目标（点击只推进，不提 value）', async () => {
    expect((await mountTouch({ inputType: 'EnterKey' })).find('.term-btn').classes())
      .not.toContain('term-btn-touch');
    expect((await mountTouch({ inputType: 'AnyKey' })).find('.term-btn').classes())
      .not.toContain('term-btn-touch');
  });

  it('孤立按钮的行不被撑高——热区改由 --hit-v 纵向溢出，行距保持游戏自身值', async () => {
    const wrapper = await mountTouch();
    const terminalStyle = wrapper.find('.terminal').attributes('style') ?? '';
    // 19px 是 ConfigCode.LineHeight 默认；触控排版不改变普通行的行距
    expect(terminalStyle).toContain('line-height: 19px');
    // 纵向外扩 (48-19)/2 = 14.5px：盒高 = 内容行高 + 2 × 本值 = 48px（与行是否撑高无关），
    // 由等量负 margin 抵消，行高不变
    expect(terminalStyle).toContain('--hit-v: 14.5px');

    const rows = wrapper.findAll('.term-line');
    expect(rows).toHaveLength(2);
    // 上下没有列区间重叠的其它按钮 → 两行都不钉高（这正是 v2 与 v0 的差别）
    expect(rows[0].attributes('style') ?? '').not.toContain('height:');
    expect(rows[1].attributes('style') ?? '').not.toContain('height:');
  });

  // ---------- 横向按需外扩（v1）----------
  //
  // 外扩量由快照的 col/width 列几何算出（lib/touchLayout.ts），零 DOM 测量。
  // 唯一的阈值是「目标触控宽度」，默认 5 列；与邻居之间只要求不重叠（平分空隙，最多相切）。
  describe('横向外扩', () => {
    it('窄按钮（2 列）在开阔处双侧对称外扩到目标宽度——(5-2)/2 = 1.5 列', async () => {
      const wrapper = await mountTouch({ lines: [geoLine([{ text: '選項', col: 0, width: 2 }])] });
      const btn = wrapper.find('.term-btn').element;
      expect(hitCols(btn, 'left')).toBe(1.5);
      expect(hitCols(btn, 'right')).toBe(1.5);
    });

    it('按钮自身已达目标宽度 → 零外扩，长按钮周围不会多出空隙', async () => {
      const wrapper = await mountTouch({ lines: [geoLine([{ text: '很長的選項文字', col: 0, width: 6 }])] });
      const btn = wrapper.find('.term-btn').element;
      expect(hitCols(btn, 'left')).toBe(0);
      expect(hitCols(btn, 'right')).toBe(0);
    });

    it('目标宽度可调——调到 7 列时同一按钮外扩到 (7-2)/2 = 2.5 列', async () => {
      const wrapper = await mountTouch({
        lines: [geoLine([{ text: '選項', col: 0, width: 2 }])],
        targetCols: 7,
      });
      const btn = wrapper.find('.term-btn').element;
      expect(hitCols(btn, 'left')).toBe(2.5);
      expect(hitCols(btn, 'right')).toBe(2.5);
    });

    it('空隙小于目标需求 → 两侧平分空隙，两个热区正好相切不重叠', async () => {
      // 两个 2 列按钮之间只隔 1 列：目标各要 1.5 列，但空隙只够各拿 0.5 列
      const wrapper = await mountTouch({
        lines: [geoLine([{ text: 'A', col: 0, width: 2 }, { text: 'B', col: 3, width: 2 }])],
      });
      const btns = wrapper.findAll('.term-btn');
      expect(btns).toHaveLength(2);
      expect(hitCols(btns[0].element, 'right')).toBe(0.5);
      expect(hitCols(btns[1].element, 'left')).toBe(0.5);
      // 外侧没有邻居约束，仍由目标宽度封顶——两侧独立取值，各自只朝有余量的方向长
      expect(hitCols(btns[0].element, 'left')).toBe(1.5);
      expect(hitCols(btns[1].element, 'right')).toBe(1.5);
    });

    it('两按钮紧邻（空隙 0 列）→ 朝对方那一侧零外扩，热区绝不重叠', async () => {
      const wrapper = await mountTouch({
        lines: [geoLine([{ text: 'A', col: 0, width: 2 }, { text: 'B', col: 2, width: 2 }])],
      });
      const btns = wrapper.findAll('.term-btn');
      expect(hitCols(btns[0].element, 'right')).toBe(0);
      expect(hitCols(btns[1].element, 'left')).toBe(0);
    });

    it('空隙富余但目标宽度先到顶 → 外扩量取两者较小者（不为填满空隙而白扩）', async () => {
      // 间隔 2 列：余量上限 1 列，目标需要 1.5 列 → 取 1 列
      const wrapper = await mountTouch({
        lines: [geoLine([{ text: 'A', col: 0, width: 2 }, { text: 'B', col: 4, width: 2 }])],
      });
      const btns = wrapper.findAll('.term-btn');
      expect(hitCols(btns[0].element, 'right')).toBe(1);
      expect(hitCols(btns[1].element, 'left')).toBe(1);
      // 对外两侧无邻居约束，仍由目标宽度封顶
      expect(hitCols(btns[0].element, 'left')).toBe(1.5);
      expect(hitCols(btns[1].element, 'right')).toBe(1.5);
    });

    it('非按钮文本不是障碍物——它占的列不挡外扩（「隔了足够空间的文本」正是不该挤的那些）', async () => {
      const wrapper = await mountTouch({
        lines: [geoLine([{ text: '　・' }, { text: '選項', col: 0, width: 2 }])],
      });
      const btn = wrapper.find('.term-btn').element;
      expect(hitCols(btn, 'left')).toBe(1.5);
      expect(hitCols(btn, 'right')).toBe(1.5);
    });

    it('失效按钮仍算障碍物——热区不会压在「看起来还在」的旧回合按钮上', async () => {
      // 同一位置对照：A 右侧有没有「历史回合按钮」决定它能不能扩满
      const alone = await mountTouch({ lines: [geoLine([{ text: 'A', col: 0, width: 2 }])] });
      expect(hitCols(alone.find('.term-btn').element, 'right')).toBe(1.5);

      const withNeighbor = await mountTouch({
        lines: [geoLine([
          { text: 'A', col: 0, width: 2 },
          { text: 'B', col: 4, width: 2, generation: 9 },
        ])],
        turnGeneration: 0,
      });
      const btns = withNeighbor.findAll('.term-btn');
      // 4 列处那个按钮虽已失效（generation 不匹配、自己不外扩），仍把 A 的右侧卡到空隙的一半
      expect(hitCols(btns[0].element, 'right')).toBe(1);
      // 失效按钮自己也不带触控盒类
      expect(btns[1].classes()).not.toContain('term-btn-touch');
    });

    it('缺 col/width 的按钮不参与横向外扩（不猜值：只损失放大，不产生误点）', async () => {
      const wrapper = await mountTouch({ lines: [btnLine(0)] });
      const btn = wrapper.find('.term-btn');
      expect(btn.classes()).toContain('term-btn-touch');
      expect(btn.attributes('style') ?? '').not.toContain('--hit-');
    });

    it('关闭开关 → 既不带触控盒类也不注入任何热区变量（可逆、零痕迹）', async () => {
      const { wrapper } = await mountWith([geoLine([{ text: '選項', col: 0, width: 2 }])]);
      const btn = wrapper.find('.term-btn');
      expect(btn.classes()).not.toContain('term-btn-touch');
      expect(btn.attributes('style') ?? '').not.toContain('--hit-');
    });

    it('内联样式只注入 --hit-l / --hit-r——外扩量与文字的相消留在 CSS 里（padding 与等量负 margin 成对）', async () => {
      const wrapper = await mountTouch({ lines: [geoLine([{ text: '選項', col: 0, width: 2 }])] });
      const inline = wrapper.find('.term-btn').attributes('style') ?? '';
      expect(inline).toContain('--hit-l');
      expect(inline).toContain('--hit-r');
      // 若有人改从内联注入单边 padding/margin，就再也不是「两侧相消」了——锁住这条边界
      expect(inline).not.toMatch(/padding|margin|width:/);
    });
  });

  // ---------- 纵向：按需最小撑高（v2）----------
  //
  // 按钮盒恒为一个触控目标高（默认 48px）、以文字行为中心上下各长 --hit-v，**处处一致**。
  // 两行是否相撞只看槽位距离，而撑高只会把它**后面**的行往下推——所以每条冲突的约束都由
  // **上方那一行**负责，撑高量取几何上必需的最小值：相邻行满额（29px），隔 1 行只要 10px，
  // 隔 2 行及以上根本不用撑。判定窗口 W = ceil(目标行高 / 行距) - 1 = 2（默认行距 19px）。
  describe('纵向最小撑高', () => {
    /** 该行是否被撑高（= 被钉到「基准行高 + extra」）。 */
    function lifted(el: Element): boolean {
      return /(^|;)\s*height:/.test(el.getAttribute('style') ?? '');
    }
    /** 该行自己覆盖的 --hit-v（px）——恒为 null：盒高处处一致，不做按行收窄。 */
    function hitV(el: Element): number | null {
      const m = /--hit-v:\s*(-?[\d.]+)px/.exec(el.getAttribute('style') ?? '');
      return m ? Number(m[1]) : null;
    }
    /** 一个 2 列宽的按钮 part。 */
    function btnAt(col: number, generation?: number) {
      return { text: '選項', col, width: 2, ...(generation === undefined ? {} : { generation }) };
    }

    it('相邻两行同列按钮 → 只有上方那行撑满额，且**不**覆盖 --hit-v（盒与文字位置都不变）', async () => {
      const wrapper = await mountTouch({ lines: [geoLine([btnAt(0)]), geoLine([btnAt(0)])] });
      const rows = wrapper.findAll('.term-line');
      // 撑高把下方行推开，两张 48px 盒正好相切；下方行自己不需要撑
      expect(rows[0].attributes('style')).toContain('height: 48px');
      expect(lifted(rows[1].element)).toBe(false);
      // 回归防线：撑高只加槽位，绝不能顺手把纵向外扩归零——那会让盒被撑满整槽，而
      // Chromium 的 button 布局会把内容在内容盒里垂直居中，按钮文字整体下沉半个增量、
      // 离开游戏给它的位置（真机上表现为文本行与按钮行之间多出空白）。
      expect(hitV(rows[0].element)).toBe(null);
      expect(hitV(rows[1].element)).toBe(null);
    });

    it('相隔 2 行（中间一空行）同列按钮 → 上方行只撑几何上必需的 10px，不是 29px', async () => {
      const wrapper = await mountTouch({
        lines: [geoLine([btnAt(0)]), line([{ text: '中間文本' }]), geoLine([btnAt(0)])],
      });
      const rows = wrapper.findAll('.term-line');
      // 两盒相距 38px、各 48px 高 → 只需挤开 10px。以前这里把两行各撑 29px（间距 38→67px）
      expect(rows[0].attributes('style')).toContain('height: 29px');
      expect(lifted(rows[1].element)).toBe(false);
      expect(lifted(rows[2].element)).toBe(false);
      expect(hitV(rows[0].element)).toBe(null);
    });

    it('下方行因相邻冲突撑高时，上方同列行也必须撑——否则两盒叠 10px（真机回归）', async () => {
      const wrapper = await mountTouch({
        lines: [
          geoLine([btnAt(0)]),
          line([{ text: '中間文本' }]),
          geoLine([btnAt(0)]),
          geoLine([btnAt(0)]),
        ],
      });
      const rows = wrapper.findAll('.term-line');
      // 行 2、3 相邻同列 → 行 2 满额；行 0 与行 2 隔 1 行同列 → 行 0 也得撑 10px。
      // 「下方行已撑高」救不了它与上方行的距离——撑高只推它后面的行。
      expect(rows[2].attributes('style')).toContain('height: 48px');
      expect(rows[0].attributes('style')).toContain('height: 29px');
    });

    it('相隔 3 行（57px ≥ 48px 盒高）→ 两盒本就不撞，什么都不做', async () => {
      const wrapper = await mountTouch({
        lines: [
          geoLine([btnAt(0)]),
          line([{ text: 'a' }]),
          line([{ text: 'b' }]),
          geoLine([btnAt(0)]),
        ],
      });
      const rows = wrapper.findAll('.term-line');
      expect(lifted(rows[0].element)).toBe(false);
      expect(lifted(rows[3].element)).toBe(false);
      // 不覆盖 --hit-v → 沿用 .terminal 上的默认值，热区照常纵向溢出
      expect(hitV(rows[0].element)).toBe(null);
    });

    it('相邻两行但列不重叠 → 不撑高也不收窄（热区纵向重叠无所谓，列不重叠就不会误点）', async () => {
      const wrapper = await mountTouch({ lines: [geoLine([btnAt(0)]), geoLine([btnAt(10)])] });
      const rows = wrapper.findAll('.term-line');
      expect(lifted(rows[0].element)).toBe(false);
      expect(lifted(rows[1].element)).toBe(false);
      expect(hitV(rows[0].element)).toBe(null);
      expect(hitV(rows[1].element)).toBe(null);
    });

    it('相隔 2 行但列不重叠 → 不撑高也不收窄（纵向重叠时列不重叠就不会误点）', async () => {
      const wrapper = await mountTouch({
        lines: [geoLine([btnAt(0)]), line([{ text: '中間文本' }]), geoLine([btnAt(20)])],
      });
      const rows = wrapper.findAll('.term-line');
      expect(lifted(rows[0].element)).toBe(false);
      expect(lifted(rows[2].element)).toBe(false);
      expect(hitV(rows[0].element)).toBe(null);
      expect(hitV(rows[2].element)).toBe(null);
    });

    it('失效按钮不参与纵向判定——同一位置换成历史回合按钮就不再收窄', async () => {
      const lines = (generation: number) => [
        geoLine([btnAt(0)]),
        line([{ text: '中間文本' }]),
        geoLine([btnAt(0, generation)]),
      ];
      const stale = await mountTouch({ lines: lines(9), turnGeneration: 0 });
      const staleRows = stale.findAll('.term-line');
      expect(lifted(staleRows[0].element)).toBe(false);
      expect(hitV(staleRows[0].element)).toBe(null);

      const live = await mountTouch({ lines: lines(0), turnGeneration: 0 });
      const liveRows = live.findAll('.term-line');
      // 同一个位置换成当前回合按钮 → 相隔 2 行同列，上方行撑 10px
      expect(liveRows[0].attributes('style')).toContain('height: 29px');
      expect(hitV(liveRows[0].element)).toBe(null);
    });

    it('缺 col/width 的存活按钮视为占满整行 → 与窗口内任何存活按钮都冲突（脏数据只多让位，不产生误点）', async () => {
      const wrapper = await mountTouch({
        lines: [geoLine([btnAt(0)]), line([{ text: '中間文本' }]), btnLine(0)],
      });
      const rows = wrapper.findAll('.term-line');
      // 相隔 2 行 → 上方行撑 10px（不是按几何无关地满额撑）
      expect(rows[0].attributes('style')).toContain('height: 29px');
      expect(lifted(rows[2].element)).toBe(false);
      expect(hitV(rows[0].element)).toBe(null);
    });

    it('窗口随行距缩放——行距 10px 时 W = ceil(48/10) - 1 = 4：隔 4 行撑 (48-10)-3×10 = 8px，隔 5 行不动', async () => {
      /** 两个按钮行，中间隔 gap-1 行文本——两行的行索引差即 gap。 */
      const withGap = (gap: number) => [
        geoLine([btnAt(0)]),
        ...Array.from({ length: gap - 1 }, (_, i) => line([{ text: `t${i}` }])),
        geoLine([btnAt(0)]),
      ];
      const within = await mountTouch({ lines: withGap(4), lineHeight: 10 });
      const rows4 = within.findAll('.term-line');
      expect(rows4).toHaveLength(5);
      expect(rows4[0].attributes('style')).toContain('height: 18px'); // 基准 10 + extra 8
      expect(lifted(rows4[4].element)).toBe(false);
      expect(hitV(rows4[0].element)).toBe(null);

      const beyond = await mountTouch({ lines: withGap(5), lineHeight: 10 }); // 行索引差 5 → 50px ≥ 48px
      const rows5 = beyond.findAll('.term-line');
      expect(rows5).toHaveLength(6);
      expect(lifted(rows5[0].element)).toBe(false);
      expect(lifted(rows5[5].element)).toBe(false);
    });

    it('游戏自身行距已 ≥ 48px → 不撑高、也不纵向溢出（冷路径：--hit-v 0）', async () => {
      const wrapper = await mountTouch({
        lines: [geoLine([btnAt(0)]), geoLine([btnAt(0)])],
        lineHeight: 60,
      });
      const terminalStyle = wrapper.find('.terminal').attributes('style') ?? '';
      expect(terminalStyle).toContain('--hit-v: 0px');
      for (const row of wrapper.findAll('.term-line')) expect(lifted(row.element)).toBe(false);
    });
  });

  // ---------- 图形按钮（图片 / 色块）整体跳过 ----------
  //
  // 这类按钮的内容由游戏以**像素**画出来（`<img>` / `<rect>`），`ButtonRef.width` 恒为 0
  // （C# 侧 `ConsoleImagePart.Text` 是空串，`BuildPrintOpsForLine` 按文本算显示宽度，图片的
  // 显示宽度另由 px 决定）。拿列几何去处理它会同时错三处：横向按「(目标列数 − 0) / 2」外扩、
  // 被当有效按钮撑高所在行并画圆角矩形、还占一个零宽列区间当障碍物。真机上它们本身就够大，
  // 所以整体跳过——但**照旧可点**（只跳呈现层的放大，不碰可提交性）。
  describe('图形按钮跳过', () => {
    /** 一行：一个由图片或色块画出来的按钮。 */
    function graphicBtnLine(seg: PrintSegment): DisplaySnapshot['lines'][number] {
      return {
        entries: [{ segments: [seg], button: { value: 7, isInteger: true, generation: 0 } }],
        align: 'left',
        isLineEnd: true,
      };
    }
    /** 一行：一个两列宽的纯文字按钮。 */
    function textBtnLine(): DisplaySnapshot['lines'][number] {
      return {
        entries: [{
          segments: [{ text: '選項' }],
          button: { value: 0, isInteger: true, generation: 0, col: 0, width: 2 },
        }],
        align: 'left',
        isLineEnd: true,
      };
    }
    const styleOf = (wrapper: VueWrapper, sel: string) => wrapper.find(sel).attributes('style') ?? '';

    it('图片按钮不套触控盒、不注入热区、所在行也不被撑高', async () => {
      const wrapper = await mountTouch({ lines: [graphicBtnLine(imgSeg())] });
      const btn = wrapper.find('.term-btn');
      expect(btn.classes()).not.toContain('term-btn-touch');
      expect(styleOf(wrapper, '.term-btn')).not.toContain('--hit-');
      expect(styleOf(wrapper, '.term-line')).not.toContain('height:');
    });

    it('色块（rect）按钮同样跳过', async () => {
      const wrapper = await mountTouch({ lines: [graphicBtnLine(rectSeg())] });
      expect(wrapper.find('.term-btn').classes()).not.toContain('term-btn-touch');
      expect(styleOf(wrapper, '.term-line')).not.toContain('height:');
    });

    it('文字与图片混在一个按钮里 → 也算图形按钮，整体跳过', async () => {
      const wrapper = await mountTouch({
        lines: [{
          entries: [{
            segments: [{ text: '選項' }, imgSeg()],
            button: { value: 7, isInteger: true, generation: 0 },
          }],
          align: 'left',
          isLineEnd: true,
        }],
      });
      expect(wrapper.find('.term-btn').classes()).not.toContain('term-btn-touch');
    });

    it('图形按钮不会让相邻的文字按钮行被撑高（它不参与纵向冲突判定）', async () => {
      // 上下相邻、列区间重叠：若图形按钮参与判定，两行都会被钉高
      const wrapper = await mountTouch({ lines: [graphicBtnLine(imgSeg()), textBtnLine()] });
      for (const row of wrapper.findAll('.term-line')) {
        expect(row.attributes('style') ?? '').not.toContain('height:');
      }
    });

    it('开着触控排版时图形按钮照旧可点（只跳呈现层，不碰可提交性）', async () => {
      const { wrapper, conn } = await mountWith([graphicBtnLine(imgSeg())]);
      useUiStore().setTouchLayout(true);
      await wrapper.vm.$nextTick();
      await wrapper.find('.term-mask').trigger('click', { clientX: 10, clientY: 5 });
      expect(conn.sendInput).toHaveBeenCalledWith('7');
    });
  });
});
