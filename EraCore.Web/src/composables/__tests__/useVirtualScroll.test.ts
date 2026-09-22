import { describe, it, expect, beforeEach, vi } from 'vitest';
import { ref, nextTick } from 'vue';
import { useVirtualScroll } from '../useVirtualScroll';

/**
 * useVirtualScroll 单测——spec.md 决策一虚拟滚动 composable。
 *
 * 测试矩阵：
 * - startIndex/endIndex/totalHeight/offsetY 在各种 itemCount/rowHeight/scrollTop 组合下的正确性
 * - 边界条件：空列表（itemCount=0）、行数 < 视口（全部渲染）、scrollTop=0、scrollTop 超界
 * - isStickyToBottom 判定：在底部 threshold 内为 true、超出为 false
 * - 缩放补偿：rowHeight 变化后 scrollTop 按比例调整
 * - shift_head adjustScrollTop：补偿后视口位置正确、isStickyToBottom 重判定
 *
 * mock viewport 元素的 scrollTop/clientHeight/scrollHeight——不测真实 DOM 渲染。
 */

interface MockViewport {
  scrollTop: number;
  clientHeight: number;
  scrollHeight: number;
  addEventListener: ReturnType<typeof vi.fn>;
  removeEventListener: ReturnType<typeof vi.fn>;
}

function makeMockViewport(opts: { clientHeight?: number; scrollHeight?: number; scrollTop?: number } = {}): MockViewport {
  return {
    scrollTop: opts.scrollTop ?? 0,
    clientHeight: opts.clientHeight ?? 600,
    scrollHeight: opts.scrollHeight ?? 0,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
}

/**
 * 模拟真实浏览器行为的 mock——scrollTop 赋值时钳制到 [0, scrollHeight - clientHeight]，
 * scrollHeight 变化（模拟 DOM 渲染更新）时同步重钳制 scrollTop。
 * 普通 makeMockViewport 不做钳制，无法复现"写回被旧内容上限钳制"这一真实浏览器行为
 * （缩放补偿 bug 的根源）。
 */
function makeClampingViewport(opts: { clientHeight: number; scrollHeight: number; scrollTop: number }): MockViewport {
  let scrollHeight = opts.scrollHeight;
  let stored = Math.max(0, Math.min(opts.scrollTop, scrollHeight - opts.clientHeight));
  return {
    clientHeight: opts.clientHeight,
    get scrollTop(): number { return stored; },
    set scrollTop(v: number) { stored = Math.max(0, Math.min(v, scrollHeight - opts.clientHeight)); },
    get scrollHeight(): number { return scrollHeight; },
    set scrollHeight(v: number) {
      scrollHeight = v;
      stored = Math.max(0, Math.min(stored, scrollHeight - opts.clientHeight));
    },
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
}

/**
 * 把 mock viewport 注入 composable——composable 用 viewportRef.value 读 scrollTop/clientHeight/scrollHeight。
 * 用 Object.assign 把 mock 字段塞到一个空对象上，cast 为 HTMLElement。
 */
function attachMockViewport(viewportRef: ReturnType<typeof ref<HTMLElement | null>>, mock: MockViewport): void {
  // composable 调用 el.addEventListener / el.removeEventListener / el.scrollTop 等——
  // 把 mock 上这些字段塞到对象上即可。
  const el = mock as unknown as HTMLElement;
  viewportRef.value = el;
}

/**
 * 从 mock viewport 的 addEventListener 调用记录中提取 'scroll' 监听器——
 * 多个测试需要手动触发 onScroll 来更新 sticky 状态。
 */
function getScrollListener(mock: MockViewport): () => void {
  const calls = mock.addEventListener.mock.calls;
  const scrollCall = calls.find((c) => c[0] === 'scroll');
  if (!scrollCall) throw new Error('scroll listener 未注册');
  return scrollCall[1] as () => void;
}

describe('useVirtualScroll: 纯计算逻辑', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  it('空列表（itemCount=0）——startIndex/endIndex/totalHeight 都为 0', () => {
    const itemCount = ref(0);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    expect(vs.startIndex.value).toBe(0);
    expect(vs.endIndex.value).toBe(0);
    expect(vs.totalHeight.value).toBe(0);
    expect(vs.offsetY.value).toBe(0);
  });

  it('行数 < 视口（全部渲染 + overscan）', async () => {
    const itemCount = ref(5);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 5 * 19, scrollTop: 0 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 5 行全部应被渲染——startIndex=0（钳制 rawStart 0 - overscan 5 = -5 → 0），
    // endIndex = min(5, 0 + ceil(600/19) + 5) = min(5, 37) = 5
    expect(vs.startIndex.value).toBe(0);
    expect(vs.endIndex.value).toBe(5);
    expect(vs.totalHeight.value).toBe(95);
    expect(vs.offsetY.value).toBe(0);
  });

  it('scrollTop=0 时 startIndex=0', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef, overscan: 5 });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1000 * 19, scrollTop: 0 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    expect(vs.startIndex.value).toBe(0);
    // endIndex = min(1000, 0 + ceil(600/19) + 5) = min(1000, 32 + 5) = 37
    expect(vs.endIndex.value).toBe(37);
    expect(vs.offsetY.value).toBe(0);
  });

  it('scrollTop 中段——startIndex/endIndex 正确计算', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef, overscan: 5 });

    // scrollTop = 200px → rawStart = floor(200/19) = 10
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1000 * 19, scrollTop: 200 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // startIndex = max(0, 10 - 5) = 5
    expect(vs.startIndex.value).toBe(5);
    // endIndex = min(1000, 10 + 32 + 5) = 47
    expect(vs.endIndex.value).toBe(47);
    expect(vs.offsetY.value).toBe(5 * 19); // 95
  });

  it('scrollTop 超界——钳制到 itemCount-1', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef, overscan: 5 });

    // scrollTop 远超总高度——rawStart 钳制到 99（itemCount-1）
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 100 * 19, scrollTop: 99999 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // startIndex = max(0, 99 - 5) = 94
    expect(vs.startIndex.value).toBe(94);
    // endIndex = min(100, 99 + 32 + 5) = 100
    expect(vs.endIndex.value).toBe(100);
  });

  it('overscan=0——不缓冲，仅渲染视口内', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef, overscan: 0 });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1000 * 19, scrollTop: 200 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // rawStart = 10, overscan = 0 → startIndex = 10
    expect(vs.startIndex.value).toBe(10);
    // endIndex = min(1000, 10 + 32 + 0) = 42
    expect(vs.endIndex.value).toBe(42);
  });

  it('totalHeight = itemCount * rowHeight', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(25);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    expect(vs.totalHeight.value).toBe(2500);

    itemCount.value = 50;
    await nextTick();
    expect(vs.totalHeight.value).toBe(1250);

    itemCount.value = 0;
    await nextTick();
    expect(vs.totalHeight.value).toBe(0);
  });
});

describe('useVirtualScroll: isStickyToBottom', () => {
  it('初始值为 true（未滚动过视为在底部）', () => {
    const itemCount = ref(0);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    expect(vs.isStickyToBottom.value).toBe(true);
  });

  it('onStickyChange 回调在 sticky 变化时调用', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const onStickyChange = vi.fn();
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef, onStickyChange });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1000 * 19, scrollTop: 0 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 初始 true——onStickyChange 不应被调用（无变化）
    expect(onStickyChange).not.toHaveBeenCalled();

    // 模拟用户向上滚动——scrollTop=100，不在底部
    mock.scrollTop = 100;
    mock.scrollHeight = 1000 * 19;
    // 触发 onScroll——mock 不真实触发事件，直接调注册的 scroll listener
    getScrollListener(mock)();

    expect(vs.isStickyToBottom.value).toBe(false);
    expect(onStickyChange).toHaveBeenCalledWith(false);
  });

  it('scrollToBottom() 置 sticky=true', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1000 * 19, scrollTop: 100 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 模拟 scroll 事件——置 sticky=false
    mock.scrollTop = 100;
    getScrollListener(mock)();
    expect(vs.isStickyToBottom.value).toBe(false);

    // scrollToBottom——置 sticky=true + scrollTop=scrollHeight
    vs.scrollToBottom();
    expect(mock.scrollTop).toBe(mock.scrollHeight);
    expect(vs.isStickyToBottom.value).toBe(true);
  });

  it('threshold = 1 行高度——scrollTop 在 threshold 内仍视为 sticky', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    // scrollHeight = 1000 * 19 = 19000
    // clientHeight = 600 → 底部 scrollTop = 19000 - 600 = 18400
    // threshold = 19 → scrollTop >= 18400 - 19 = 18381 视为 sticky
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 18385 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    const onScroll = getScrollListener(mock);
    onScroll();
    // 18385 >= 18381 → sticky=true
    expect(vs.isStickyToBottom.value).toBe(true);

    // 滚出 threshold——scrollTop = 18380 < 18381
    mock.scrollTop = 18380;
    onScroll();
    expect(vs.isStickyToBottom.value).toBe(false);
  });
});

describe('useVirtualScroll: adjustScrollTop（shift_head 补偿）', () => {
  it('adjustScrollTop(delta) 增加 scrollTop——shift_head 后向上补偿', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 5000 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 模拟 scroll——置 sticky=false（用户在查看历史）
    getScrollListener(mock)();
    expect(vs.isStickyToBottom.value).toBe(false);

    // shift_head 删除 1 行——scrollTop 应向上补偿 1 行高度 = -19
    // scrollTop = 5000 - 19 = 4981
    vs.adjustScrollTop(-19);
    expect(mock.scrollTop).toBe(4981);
  });

  it('adjustScrollTop 钳制到 0——delta 让 scrollTop 变负时归零', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 10 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // delta = -100，scrollTop = 10 - 100 = -90 → 钳制到 0
    vs.adjustScrollTop(-100);
    expect(mock.scrollTop).toBe(0);
  });

  it('adjustScrollTop(-count*rowHeight) 与 scrollHeight 同步减少——距底距离不变，sticky 保持 false', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    // 用户原在 18380（距底部 20px，刚好超出 threshold=19），sticky=false
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 18380 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    getScrollListener(mock)();
    expect(vs.isStickyToBottom.value).toBe(false);

    // shift_head 删除 1 行——scrollHeight 减少 19px → 18981
    // adjustScrollTop(-19)：scrollTop = 18380 - 19 = 18361
    // 新距底距离 = 18981 - 18361 - 600 = 20px（与调整前一致）→ sticky 仍为 false
    // 注：adjustScrollTop 的目的就是保持视觉位置不变，距底距离自然也不变
    mock.scrollHeight = 18981;
    vs.adjustScrollTop(-19);
    expect(vs.isStickyToBottom.value).toBe(false);
  });

  it('adjustScrollTop 后重判定 sticky——delta 让用户进入 threshold 内时置 true', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    // 用户原在 18380（距底部 20px，刚好超出 threshold=19），sticky=false
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 18380 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    getScrollListener(mock)();
    expect(vs.isStickyToBottom.value).toBe(false);

    // 假设 scrollHeight 不变，adjustScrollTop(+5) 让 scrollTop=18385
    // 新距底距离 = 19000 - 18385 - 600 = 15px ≤ threshold=19 → sticky=true
    // 此场景验证 adjustScrollTop 后的 sticky 重判定逻辑（非 shift_head 实际路径，
    // 但覆盖 computeSticky 在 adjustScrollTop 末尾的调用）
    vs.adjustScrollTop(5);
    expect(vs.isStickyToBottom.value).toBe(true);
  });
});

describe('useVirtualScroll: 缩放补偿', () => {
  it('rowHeight 变化时按比例调整 scrollTop', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });
    void vs; // composable 内部 watch(rowHeight) 副作用——vs 仅用于激活 watcher

    // 初始 scrollTop = 190（看第 10 行）
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 190 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 行高加倍 19 → 38（scale 1.0 → 2.0）
    // scrollTop 应 = 190 * (38/19) = 380（仍看第 10 行）
    // 写回延迟到渲染完成（watch 内 nextTick）——与 itemCount watch 同模式
    rowHeight.value = 38;
    await nextTick();
    await Promise.resolve();
    await nextTick();
    expect(mock.scrollTop).toBe(380);
  });

  it('rowHeight 不变时不调整 scrollTop', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });
    void vs;

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 200 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    rowHeight.value = 19; // 同值
    await nextTick();
    await Promise.resolve();
    await nextTick();
    expect(mock.scrollTop).toBe(200);
  });

  it('同 tick 连续多次 rowHeight 变化（捏合连续缩放）——批处理为一次，按最终比例补偿', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });
    void vs;

    // 初始 scrollTop = 190（看第 10 行）
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 190 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 同 tick 内 19 → 38 → 57：Vue 把 watcher 批处理为一次（oldH=19, newH=57），
    // 补偿 = 190 × (57/19) = 570——按最终比例换算，而非中间值逐级累计
    rowHeight.value = 38;
    rowHeight.value = 57;
    await nextTick();
    await Promise.resolve();
    await nextTick();
    expect(mock.scrollTop).toBe(570);
  });

  it('贴底放大——补偿目标用缩放前的 scrollTop，渲染后写回不被旧内容上限钳制', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });
    void vs;

    // 贴底：scrollTop = 19000 - 600 = 18400（浏览器钳制模拟）
    const mock = makeClampingViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 18400 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 行高加倍：pre-flush 捕获缩放前 scrollTop=18400 → 目标 36800。
    // 渲染后（scrollHeight 更新为 38000，上限 37400）写回 36800 不被钳制——
    // 旧实现在写回时被旧上限 18400 钳制 → 视口停在旧底 → 画面向上滚。
    rowHeight.value = 38;
    await nextTick();
    mock.scrollHeight = 38000;
    await Promise.resolve();
    await nextTick();
    expect(mock.scrollTop).toBe(36800);
  });

  it('贴底缩小——用渲染前捕获的锚点换算，写回被钳制到新上限（新底部）', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });
    void vs;

    const mock = makeClampingViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 18400 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 行高 19 → 15.2（scale 0.8）：pre-flush 捕获缩放前 scrollTop=18400 → 目标 14720。
    // 渲染缩小后浏览器先把 scrollTop 钳到新上限 14600，再写 14720 → 钳回新底部 14600。
    rowHeight.value = 15.2;
    await nextTick();
    mock.scrollHeight = 15200;
    await Promise.resolve();
    await nextTick();
    expect(mock.scrollTop).toBe(14600);
  });
});

describe('useVirtualScroll: scrollToIndex', () => {
  it('跳转到指定行——scrollTop = i * rowHeight', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 19000, scrollTop: 0 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    vs.scrollToIndex(100);
    expect(mock.scrollTop).toBe(100 * 19);
  });

  it('跳转索引超界——钳制到 itemCount-1', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1900, scrollTop: 0 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    vs.scrollToIndex(9999);
    // 钳制到 99 → scrollTop = 99 * 19 = 1881
    expect(mock.scrollTop).toBe(99 * 19);
  });

  it('跳转负索引——钳制到 0', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1900, scrollTop: 500 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    vs.scrollToIndex(-10);
    expect(mock.scrollTop).toBe(0);
  });
});

describe('useVirtualScroll: itemCount 变化时自动跟随', () => {
  it('isStickyToBottom=true 时新行到达自动滚到底部', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 100 * 19, scrollTop: 100 * 19 - 600 });
    attachMockViewport(viewportRef, mock);
    await nextTick();
    expect(vs.isStickyToBottom.value).toBe(true);

    // 新增 50 行——scrollHeight 增至 150 * 19 = 2850
    mock.scrollHeight = 150 * 19;
    itemCount.value = 150;
    // 等 watch + Promise.resolve().then() 触发
    await nextTick();
    await Promise.resolve();
    await nextTick();

    // scrollTop 应被推到 scrollHeight（底部）
    expect(mock.scrollTop).toBe(mock.scrollHeight);
  });

  it('isStickyToBottom=false 时新行到达不自动滚动', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });

    // 用户在中段——scrollTop = 1000
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 100 * 19, scrollTop: 1000 });
    attachMockViewport(viewportRef, mock);
    await nextTick();

    // 模拟 scroll——sticky=false
    getScrollListener(mock)();
    expect(vs.isStickyToBottom.value).toBe(false);

    const originalScrollTop = mock.scrollTop;
    mock.scrollHeight = 150 * 19;
    itemCount.value = 150;
    await nextTick();
    await nextTick();

    // scrollTop 不应被改变——用户在查看历史
    expect(mock.scrollTop).toBe(originalScrollTop);
  });
});

/**
 * 稀疏加高行（lineExtras）——触控排版专用路径。
 *
 * 行高不均时最容易出的错是"用 floor(scrollTop / rowHeight) 反推行号"：
 * 上方每有一个加高行，行号就往后偏一截，看得越深偏得越多。以下断言锁定三点：
 * 总高含加高量、offsetY 含其上方的加高量、startIndex 按真实行首偏移取。
 */
describe('useVirtualScroll: lineExtras 稀疏加高行', () => {
  /** 100 行 × 19px，其中第 10、20 行各加高 29px（模拟两行被撑到 48px 的按钮行）。 */
  function setup(extrasAt: Array<[number, number]> = [[10, 29], [20, 29]]) {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const lineExtras = ref<ReadonlyMap<number, number> | null>(new Map(extrasAt));
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, lineExtras, viewportRef });
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 0, scrollTop: 0 });
    attachMockViewport(viewportRef, mock);
    return { itemCount, rowHeight, lineExtras, viewportRef, vs, mock };
  }

  it('totalHeight = itemCount × rowHeight + 所有加高量之和', async () => {
    const { vs } = setup();
    await nextTick();
    expect(vs.totalHeight.value).toBe(100 * 19 + 29 * 2);
  });

  it('未传 lineExtras 时与旧定高行为一致（totalHeight 不含加高量）', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, viewportRef });
    await nextTick();
    expect(vs.totalHeight.value).toBe(1900);
  });

  it('空表（size=0）等价未传——不进入二分分支', async () => {
    const itemCount = ref(100);
    const rowHeight = ref(19);
    const lineExtras = ref<ReadonlyMap<number, number> | null>(new Map());
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, lineExtras, viewportRef });
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 1900, scrollTop: 190 });
    attachMockViewport(viewportRef, mock);
    await nextTick();
    getScrollListener(mock)();
    expect(vs.totalHeight.value).toBe(1900);
    expect(vs.startIndex.value).toBe(5); // floor(190/19)=10 → overscan 5
  });

  it('offsetY 计入其上方的加高量——滚动到加高行之后起点不偏', async () => {
    const { vs, mock } = setup();
    await nextTick();
    // 定高算法会认为第 30 行起点 = 570；实际第 10、20 行各高出 29px → 570 + 58
    mock.scrollTop = 30 * 19 + 29 * 2;
    getScrollListener(mock)();
    await nextTick();
    expect(vs.startIndex.value).toBe(25); // rawStart 30 - overscan 5
    expect(vs.offsetY.value).toBe(25 * 19 + 29 * 2);
  });

  it('startIndex 落在加高行内部时归属该行（不跳到下一行）', async () => {
    const { vs, mock } = setup();
    await nextTick();
    // 第 10 行占据 [190, 219)，定高算法在这里会把它算成第 11 行
    mock.scrollTop = 205;
    getScrollListener(mock)();
    await nextTick();
    expect(vs.startIndex.value).toBe(5); // rawStart = 10 → overscan 5
  });

  it('scrollToIndex 按真实偏移跳转——越过加高行不少推不多推', async () => {
    const { vs, mock } = setup();
    await nextTick();
    vs.scrollToIndex(50);
    expect(mock.scrollTop).toBe(50 * 19 + 29 * 2);
  });

  it('可见行数按真实高度算——视口内有加高行时不至于少渲染一行', async () => {
    const itemCount = ref(1000);
    const rowHeight = ref(19);
    // 前 30 行全加高 29px，视口 600px 里几乎全是加高行
    const extras = new Map<number, number>();
    for (let i = 0; i < 30; i++) extras.set(i, 29);
    const lineExtras = ref<ReadonlyMap<number, number> | null>(extras);
    const viewportRef = ref<HTMLElement | null>(null);
    const vs = useVirtualScroll({ itemCount, rowHeight, lineExtras, viewportRef });
    const mock = makeMockViewport({ clientHeight: 600, scrollHeight: 0, scrollTop: 0 });
    attachMockViewport(viewportRef, mock);
    await nextTick();
    // 30 行 × 48px = 1440 > 600 → 从 0 起至少需要 ceil(600/48) = 13 行才铺满视口；
    // overscan 之外渲染的行数必须 ≥ 13，否则底部留白。
    expect(vs.endIndex.value - vs.startIndex.value).toBeGreaterThanOrEqual(13 + 5);
  });

  it('越界 / 非正的加高项被忽略——不污染总高', async () => {
    const { vs } = setup([[10, 29], [999, 29], [-1, 29], [20, 0]]);
    await nextTick();
    // 索引越界（999 / -1）与非正值（0）都不计入
    expect(vs.totalHeight.value).toBe(100 * 19 + 29);
  });
});
