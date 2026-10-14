<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import { useGameStore } from '../stores/game';
import { useConnectionStore } from '../stores/connection';
import { useUiStore, TOUCH_TARGET_MIN_PX } from '../stores/ui';
import { useVirtualScroll } from '../composables/useVirtualScroll';
import { usePinchZoom } from '../composables/usePinchZoom';
import {
  shouldSubmitButtonValue,
  shouldAdvanceOnTerminalClick,
  isLiveButton,
} from '../lib/inputRouting';
import {
  computeLineHitExpansions,
  computeLineExtras,
  type HitExpansion,
} from '../lib/touchLayout';
import { resolveResource } from '../lib/resourceResolver';
import { clickToImagePixel, loadImageAndReadPixel, sortBgImages } from '../lib/imageLayout';
import { neighborCover } from '../lib/imageCover';
import SegmentRenderer from './SegmentRenderer.vue';
import type { ButtonValue, SegmentImage, DisplayLine, DisplayEntry } from '../types/protocol';

/**
 * TerminalDisplay.vue — Emuera 终端渲染器（issue 03 / issue 12 固定宽度布局 /
 * 虚拟滚动与 shift_head 协议扩展）。
 *
 * 输入：`game.displayState`（由 `applyDiff` 累积更新的不可变结构化状态）。
 * 输出：可视化终端——按 `lines[].entries[].segments[]` 渲染文本片段，
 *      颜色 / 对齐 / 背景色正确应用；按钮 entry 渲染为可点击元素。
 *
 * 渲染策略（与 C# HeadlessConsole 渲染模型不同）：
 * - DOM 流式布局而非绝对定位——`entries[]` 按顺序内联排列，由浏览器盒模型自动算位置。
 *   按钮的 `ButtonRef.col`/`width` 在 CLI 终端是字符列几何（供 SGR mouse hit-test），
 *   Web 端用原生 click 事件做 hit-test，故 col/width 不参与 DOM 定位。
 *   monospace 字体 + `white-space: pre` 保证 CJK 双宽字符与 C# 计算一致——
 *   多按钮行中非按钮 entry 的字符数正好填补按钮间空隙，自然对齐。
 *   **`pre` 而非 `pre-wrap`**：Emuera 的 `ConsoleDisplayLine` 语义是"一行不拆分"——
 *   WinForms GDI 下字符画按 FontSize/2 的 ASCII 字符宽度算列数，浏览器 monospace
 *   每字符宽度约 0.6em，比 GDI 宽——`pre-wrap` 会让字符画被浏览器拆行。改用 `pre`
 *   保持行完整性，超长行由 `.terminal` 的 `overflow-x: auto` 水平滚动兜底。
 * - segment 颜色（`color` 字段，hex 形如 "#FF0000"）直接映射 CSS `color`。
 *   bold/italic 同理。`fontname` 暂不映射（Emuera 字体名与系统字体名不一致，
 *   issue 03 不做字体替换；issue 08 字体面板会处理）。
 * - 行对齐 `align`（left/center/right）映射 CSS `text-align`。
 * - 容器背景色 `bgColor` 设置在 `.terminal` 根上。
 * - 默认文字色 `defaultTextColor`（游戏 ConfigCode.ForeColor）覆盖 `.terminal` 的
 *   `--term-color-text`——普通文字段的 `color` 在协议里为 null（与默认色相同），靠它画对。
 *
 * 虚拟滚动（spec.md「虚拟滚动与shift_head协议」）：
 * - 仅渲染视口内 + overscan 缓冲的行（~40 行），DOM 节点从 ~50000 降到 ~400。
 * - `useVirtualScroll` composable 维护 startIndex/endIndex/totalHeight/offsetY/isStickyToBottom。
 * - spacer div 撑总高度（itemCount * rowHeight），可见行用 translateY(offsetY) 定位。
 * - `isStickyToBottom` 同时控制"新行自动跟随"与"点击推进守卫"——用户向上滚过后
 *   点击非按钮区域不再推进游戏，让用户安心翻看历史。
 *
 * shift_head / clear_screen 协议：
 * - game store 的 applyDiff 归约时产出声明式 DisplaySignal（shift_head/clear_screen），
 *   存入 lastDisplaySignal；本组件 watch 此信号驱动滚动副作用——
 *   shift_head：非贴底时调 adjustScrollTop(-count * rowHeight) 保持视觉位置；
 *   clear_screen（全清）：重置 isStickyToBottom=true + scrollToBottom——全清视为新画面。
 *
 * 布局策略：`.terminal` 容器填满父宽度（`flex: 1`），内容由 `.terminal-content`
 * （`width: windowWidth × scale px; margin: 0`）约束在游戏设计宽度内、靠左对齐。
 * 这解决了固定宽度布局导致的一半黑一半灰/滚动条不在窗口边缘的问题，
 * 同时保持居中行在游戏宽度内居中（不受窗口实际宽度影响）。
 * 靠左对齐为右侧虚拟按键预留空间；窗口宽于屏幕时保持原宽自然溢出到屏幕外，
 * 由 `.terminal` 的 overflow-x 横向滚动兜底。
 * - `font-size` = `game.fontSize` 像素（默认 18）
 * - `line-height` = `game.lineHeight` 像素（**用绝对像素，不要用 `lineHeight / fontSize` 比例**——Emuera 的 `LineHeight` 是绝对像素行距，WinForms `mainPicBox` 按 `LineHeight` 铺行；用比例会让 inline 元素（按钮等）行距叠加错位。默认 19）
 * - `font-family` 首选 = `game.fontName`（来自 ConfigCode.FontName，默认 "ＭＳ ゴシック"），
 *   浏览器找不到该字体时 fallback 到 `ui-monospace, 'Cascadia Mono', Consolas, ...` 链。
 *   ASCII 字符画对字体宽度高度敏感——"ＭＳ ゴシック" 是 GDI 默认等宽日文字体，与 Consolas
 *   等浏览器默认 monospace 字形差异显著，必须读游戏字体名才能正确还原字符画视觉。
 * - `.term-line` 的 `min-height` 用 CSS 变量 `--term-line-min-height` 与 LineHeight 对齐
 * - 容器无 padding——与 WinForms mainPicBox / CLI 终端一致，字符画从容器边缘开始渲染
 *
 * 按钮点击：调 `conn.sendInput(String(value))`——C# HandleWsInput 期望 string，
 * integer 按钮的 value 转 string 后发送，C# 端按 inputType 自行解析回 long。
 */
const game = useGameStore();
const conn = useConnectionStore();
const ui = useUiStore();

/**
 * Issue 12：从 store 派生有效布局值——null 时 fallback 到 Emuera 默认值。
 *
 * 默认值来源：C# ConfigData.SetDefault() 的 ConfigCode.WindowX/FontSize/LineHeight/FontName 默认值。
 * gameColumns 默认值 = (760 - max(2, 18/6=3)) / max(18/2, 1) = 757/9 = 84
 * （与 CLI GetGameColumnWidth 一致，C# 整数除法）。
 * fontName 默认值 = "ＭＳ ゴシック"（GDI 默认等宽日文字体）。
 * 多数 Emuera 游戏不修改 emuera.config，默认值是常见情况。
 */
const effectiveFontSize = computed(() => game.fontSize ?? 18);
const effectiveLineHeight = computed(() => game.lineHeight ?? 19);
const effectiveWindowWidth = computed(() => game.windowWidth ?? 760);
const effectiveFontName = computed(() => game.fontName ?? 'ＭＳ ゴシック');

/**
 * font-family 字符串——游戏字体名在前，浏览器 fallback 字体链在后。
 *
 * 字体名含空格 / 日文时必须用引号包裹（CSS font-family 语法），否则解析失败。
 * 'ＭＳ ゴシック' 是日文系统字体，非日文 Windows / Android 等平台没有——浏览器找不到时
 * 自动 fallback 到 'EmueraMonoJP'（内置 IPA ゴシック，与 MS Gothic 度量兼容，见 styles/fonts.css），
 * 保证固定网格排版跨平台一致；IPAGothic 缺失的字形由 'EmueraBlock' 逐字形回退补齐
 * （DejaVu Sans 提取全部四区符号，无程序化字形）；最后才退到 ui-monospace 链
 * （覆盖 Windows/macOS/Linux 主流字体）。
 */
const MONOSPACE_FALLBACK = "'EmueraMonoJP', 'EmueraBlock', ui-monospace, 'Cascadia Mono', Consolas, 'Courier New', monospace";
const effectiveFontFamily = computed(() => {
  const name = effectiveFontName.value;
  if (!name) return MONOSPACE_FALLBACK;
  return `'${name.replace(/'/g, "\\'")}', ${MONOSPACE_FALLBACK}`;
});

/** 虚拟滚动的基准行高——不含触控加高，含 scale。useVirtualScroll 据此定位绝大多数行。 */
const effectiveRowHeight = computed(() => effectiveLineHeight.value * game.effectiveScale);

/**
 * 触控排版的目标行高（含 scale）——按钮盒按它取高，于是点击盒是 48px 级别的触控目标。
 *
 * 盒高恒等于这个值、且**以文字行为中心**向上下各长 `(目标行高 - 基准行高) / 2`；
 * **行高**只在必要时才撑开（见下方 touchLineExtras）：某行下方窗口内有列区间重叠的存活
 * 按钮时，按几何上必需的量撑开槽位让两张盒不相撞——相邻行才是满额，隔 1 行只要一点，
 * 再远根本不用撑。撑高只加槽位：盒与文字位置都不变。撑高的那些行由 useVirtualScroll
 * 的 `lineExtras`（稀疏额外高度表）吸收。
 */
const touchRowHeight = computed(() =>
  Math.max(effectiveLineHeight.value, TOUCH_TARGET_MIN_PX) * game.effectiveScale,
);

/** 单行因触控排版多出来的高度（px）；< 0 时取 0（游戏自身行距已够大则不再加）。 */
const touchLineExtraPx = computed(() => Math.max(0, touchRowHeight.value - effectiveRowHeight.value));

/**
 * 按钮盒的纵向半高增量（px，含 scale）——盒以文字行为中心向上下各长这么多，
 * 于是盒高 = 基准行高 + 2 × 本值 = 目标行高。
 *
 * 实现是 `padding-top/bottom` 外扩 + **等量负 margin 抵消**（见 .term-btn-touch）：
 * 盒变高而它在行盒里的贡献不变，所以行高仍等于基准行距、后续行一格不动，
 * 也就完全不必进虚拟滚动的加高表（Chromium 实测：排版总高与纯文本严格一致）。
 * 这个值处处一致——热区相撞由撑高解决，不靠收窄盒高（见 lineStyle 的注释）。
 */
const touchHalfGrowPx = computed(() => touchLineExtraPx.value / 2);

/** .terminal 容器内联 style——动态绑定 width（ch 单位）/font-size/line-height/font-family
 * + CSS 变量（行高、触控热区、默认文字色）。 */
const terminalStyle = computed<Record<string, string>>(() => {
  const style: Record<string, string> = {
    // 字体缩放随 effectiveScale；窗口宽度由 .terminal-content 的固定 width 同步缩放（见 contentStyle），
    // 二者等比 → 内容始终铺满游戏窗口。超长行由 overflow-x: auto 水平滚动兜底。
    // 浏览器 monospace 字符宽度（≈0.6em）与 GDI（FontSize/2=0.5em）不同，文本溢出由 overflow-x 处理。
    fontSize: `${effectiveFontSize.value * game.effectiveScale}px`,
    lineHeight: `${effectiveLineHeight.value * game.effectiveScale}px`,
    // font-family：游戏字体名在前，fallback 链在后——ASCII 字符画对字体宽度敏感，
    // "ＭＳ ゴシック"（GDI 默认）与 Consolas 等字形差异显著。
    fontFamily: effectiveFontFamily.value,
    // CSS 变量供 .term-line min-height 引用——必须含 effectiveScale，与缩放后的行高及
    // 虚拟滚动 rowHeight 严格一致（否则 scale<1 时行被拉高、行排版与滚动漂移错位）
    '--term-line-min-height': `${effectiveLineHeight.value * game.effectiveScale}px`,
    // 触控排版的两个热区变量（都只在 .term-btn-touch 上消费，关闭开关时零视觉影响）：
    // 纵向热区外扩量（一侧）的默认值——盒高 = 内容行高 + 2 × 本值。冲突密集的行由
    // lineStyle 按行收窄，见 computeVerticalLayout；被撑高的行不需要覆盖（撑高只加槽位，
    // 盒与文字位置都不动）。横向热区不在这里给固定值——由 hitStyle() 逐按钮注入。
    '--hit-v': `${touchHalfGrowPx.value}px`,
  };
  // 游戏默认文字色（ConfigCode.ForeColor）——turn 协议只在「与默认色不同」的 segment 上带
  // color（SegmentRenderer 只应用 s.color），普通文字段没有颜色可应用。这里覆盖
  // --term-color-text：`.terminal` 与 `.term-btn` 都引用该变量（WinForms 里按钮文字与普通
  // 文字同色），一处覆盖即两者同时生效；未读到（null）时保留主题令牌作 fallback。
  if (game.defaultTextColor) style['--term-color-text'] = game.defaultTextColor;
  if (game.displayState.bgColor) style.backgroundColor = game.displayState.bgColor;
  return style;
});

/**
 * 游戏窗口（.terminal-content）内联 style——游戏输出存放在与游戏宽度相同的窗口里：
 * 固定宽 = 游戏设计宽度 × 缩放系数，靠左对齐（margin: 0，为右侧虚拟按键预留空间）。
 * 窗口比屏幕宽时保持原宽、自然溢出到屏幕外（由 .terminal 的 overflow-x 横向滚动兜底）。
 * 缩放（effectiveScale）同时作用于字体（terminalStyle）与窗口宽度，内容与窗口等比缩放，
 * 横线等覆盖整行的元素始终铺满窗口（不会因内容缩小而脱离窗口）。
 */
const contentStyle = computed<Record<string, string>>(() => ({
  width: `${effectiveWindowWidth.value * game.effectiveScale}px`,
  margin: '0',
}));

/**
 * Issue 12：终端容器 ref——虚拟滚动视口元素。
 *
 * 虚拟滚动改造前用于 scroll 事件监听 + scrollToBottom；改造后传给 useVirtualScroll
 * 由其管理 scroll listener + isStickyToBottom + startIndex/endIndex 计算。
 */
const terminalRef = ref<HTMLDivElement | null>(null);

/** 单个 entry 上的按钮当前是否为有效按钮——决定要不要把它撑成触控目标。 */
function isLiveButtonEntry(entry: DisplayEntry): boolean {
  const button = entry.button;
  if (!button) return false;
  // issue 07：判据改读 awaiting（`input` 维度即「服务端此刻在等你发输入」）。
  return isLiveButton({
    awaiting: game.awaiting,
    buttonGeneration: button.generation,
    currentTurnGeneration: game.currentTurnGeneration,
  });
}

/**
 * 图形按钮——内容由游戏以**像素**画出来（图片 `<img>` 或色块 `<rect>`），不是文字。
 *
 * 为什么要单独挑出来：这类按钮的 `ButtonRef.width` 恒为 0（C# 侧 `ConsoleImagePart.Text`
 * 是空串，`BuildPrintOpsForLine` 按文本算显示宽度，图片的显示宽度另由 px 决定）。于是
 * 拿列几何去处理它会同时错三处：横向按「(目标列数 − 0) / 2」外扩（对 px 尺寸的图毫无
 * 意义）、把它当有效按钮撑高所在行并画圆角矩形、还占一个零宽列区间当障碍物。
 * 真机上它的观感也多余——图形按钮本身就是够大的点击目标。
 */
function isGraphicButtonEntry(entry: DisplayEntry): boolean {
  return entry.segments.some((seg) => seg.image != null || seg.shape != null);
}

/**
 * 该按钮是否**参与触控排版**——有效按钮，且内容不是图形（图片 / 色块）。
 *
 * 三个消费点必须用同一个判据，否则会出现「不套触控盒却被撑高」这类不一致：
 * `isTouchTarget`（套不套盒）、传给 `computeLineExtras` 的判据（撑不撑高）、
 * 传给 `computeLineHitExpansions` 的判据（横不横向外扩）。
 * 图形按钮照旧**可点**——这里只决定呈现层的放大，不碰可提交性（ADR-0021）。
 */
function isTouchEligible(entry: DisplayEntry): boolean {
  return isLiveButtonEntry(entry) && !isGraphicButtonEntry(entry);
}

/**
 * 触控排版下该按钮是否套上触控盒（关闭开关时恒 false，零成本短路）。
 *
 * 判据是「开关开 + 参与触控排版」，**不再**以「本行需要撑高」为前提：撑高只解决纵向
 * 触控目标，横向热区与圆角提示在行距本已够大的游戏里（LineHeight ≥ 48px，撑高量为 0）
 * 同样有价值——那种游戏里按钮仍有可能是两列宽的窄按钮。
 */
function isTouchTarget(entry: DisplayEntry): boolean {
  if (!ui.touchLayout) return false;
  return isTouchEligible(entry);
}

/**
 * 触控排版的纵向撑高表（纯函数，见 lib/touchLayout.ts 的 `computeLineExtras`）：
 * 每行只对**它下方**窗口内的列区间冲突负责，撑高量取几何上必需的最小值——相邻行满额
 * （撑开槽位让两张盒正好相切），隔 1 行只需一点，再远根本不用撑。撑高只加槽位，
 * 按钮盒与文字位置都不变。
 *
 * null / 空表时 useVirtualScroll 走原定高快路径。扫描是 O(lines × 窗口)：绝大多数行没有
 * 按钮，第一步就被筛掉；且只在 lines / generation / inputType / scale / 开关变化时重算，
 * 触控排版关闭时整个计算被短路，普通终端不受影响。
 */
const touchLineExtras = computed<Map<number, number> | null>(() => {
  if (!ui.touchLayout) return null;
  return computeLineExtras(game.displayState.lines, {
    rowHeightPx: effectiveRowHeight.value,
    targetRowPx: touchRowHeight.value,
    isLive: (entry) => isTouchEligible(entry),
  });
});

/**
 * 虚拟滚动实例——传入 itemCount（lines.length）、rowHeight、viewportRef。
 *
 * lineExtras：触控排版下被撑高的那些行。行高因此不再均匀，定位改由 composable 内的
 * 二分承担；整体上大多数行仍是标准行高，所以传稀疏表而不是全量行高数组。
 *
 * onStickyChange 回调同步到 ui store，供 InputBar 实现 AnyKey 全局 click 守卫。
 * 用户向上滚过后 isStickyToBottom=false，InputBar.onGlobalClick 拒绝推进游戏。
 */
const vs = useVirtualScroll({
  itemCount: computed(() => game.displayState.lines.length),
  rowHeight: effectiveRowHeight,
  lineExtras: touchLineExtras,
  viewportRef: terminalRef,
  onStickyChange: (v) => ui.setStickyToBottom(v),
});

/**
 * 双指捏合缩放——手势距离比例 → game.setScale（clamp [0.5, 2.0] + localStorage 持久化）。
 * 渲染层（terminalStyle/contentStyle/effectiveRowHeight）随 effectiveScale 自动生效，
 * 虚拟滚动的 scrollTop 补偿由 useVirtualScroll 的 rowHeight watch 完成，此处零渲染改动。
 * click 抑制（手势结束后 300ms）内置——避免捏合合成的 click 误推进游戏（见 composable 注释）。
 * 依赖 `.terminal` 的 `touch-action: pan-x pan-y`（见下方 CSS）——
 * 单指原生滚动保留，双指手势归 JS 接管，浏览器原生页面缩放/双击放大被禁用。
 */
usePinchZoom({
  target: terminalRef,
  getScale: () => game.effectiveScale,
  setScale: (v) => game.setScale(v),
});

/**
 * 渲染窗口两端需为跨行图片扩展的行数——**只扫视口邻域**（lib/imageCover.ts 的 `neighborCover`）。
 *
 * 为什么需要它：图片 DOM 挂在锚定行 `.term-line` 内，靠 `overflow: visible` 把超出 1 行高的
 * 部分画到相邻行上（SegmentRenderer 的 maskStyle/imageStyle）。虚拟滚动只渲染
 * `lines.slice(start, end)`——锚定行一旦离开窗口，图片节点就被卸载，即使图片箱体
 * 仍与视口相交也会整体消失。故按「窗口邻域里真实存在的图」把两端各扩若干行，
 * 保证锚定行不因滚出 slice 范围而被卸载。
 *
 * 为什么不用「全局最大覆盖量」：全局口径下，几千行之外的一张大图会**恒定**把每一帧的窗口
 * 两端各撑开 R 行（R = 最大图高 / 行高），即使视口附近一张图都没有；且维持它要每回合重扫
 * 全量行（实测 5000 行 0.26ms / 20000 行 2.25ms）。邻域口径下无图邻域**零扩展**，
 * 且每帧只扫邻域（约 0.02ms），O(N)/回合 归零。完备性论证与半径假设见 lib/imageCover.ts。
 *
 * 行数换算用**未缩放**的 `effectiveLineHeight`：图片几何与行高同乘 `effectiveScale`，
 * 行数本就与缩放无关——用缩放后的 `effectiveRowHeight` 换算会在 scale>1 时少算行数（症状复现）。
 */
const coverSpan = computed(() =>
  neighborCover(game.displayState.lines, {
    start: vs.startIndex.value,
    end: vs.endIndex.value,
    rowHeightPx: effectiveLineHeight.value,
    scale: game.effectiveScale,
    offsetOf: vs.offsetOf,
  }),
);

/** 扩展后的渲染起始行——在 useVirtualScroll 的 startIndex（含 overscan）基础上再上扩。 */
const renderStart = computed(() =>
  Math.max(0, vs.startIndex.value - coverSpan.value.backRows),
);
/** 扩展后的渲染结束行（不含）——下扩后钳制到 lines.length。 */
const renderEnd = computed(() =>
  Math.min(game.displayState.lines.length, vs.endIndex.value + coverSpan.value.fwdRows),
);

/**
 * 虚拟滚动可见行的切片——`game.displayState.lines.slice(renderStart, renderEnd)`。
 *
 * v-for 渲染此切片而非全量 lines；每行 :key 用绝对索引 `startIndex + i`
 * （与原 lineIdx 语义一致——索引在两次全清之间稳定映射到"位置"，spec.md决策二）。
 * 窗口两端按跨行图片覆盖量扩展（见上方 coverSpan），使锚定行留在窗口内。
 */
const visibleLines = computed(() => {
  const start = renderStart.value;
  const end = renderEnd.value;
  return game.displayState.lines.slice(start, end).map((line, i) => ({
    line,
    absIdx: start + i,
  }));
});

/**
 * 渲染窗口的 translateY——必须用**扩展后**起点 renderStart 的绝对偏移。
 *
 * 窗口是 `translateY(offsetY)` + 行在窗口内**自然流式**排布（每行高 = rowHeight + 该行
 * 触控加高）。扩展起点上移了 coverSpan.backRows 行后，若仍用 `vs.offsetY`（= offsetOf(原 startIndex)）
 * 定位，多出来的这些行会把可见行整体往下推——所以位移必须换成 `offsetOf(新 start)`：
 * 多出来的行落在原窗口上方、可见行像素位置一格不动。无图 / 未扩展时 renderStart === vs.startIndex，
 * `offsetOf` 与 `vs.offsetY` 严格相等，行为与扩展前逐字节一致。
 */
const windowOffsetY = computed(() => vs.offsetOf(renderStart.value));

/**
 * 触控排版的横向热区外扩表——`绝对行号 → (entry 下标 → 左右外扩列数)`。
 *
 * 只算**已渲染的行**：外扩量只影响可见行的按钮盒样式，不必为几万行历史文本预计算；
 * 也因此不参与 `useVirtualScroll` 的记账（外扩用负 margin 抵消行内推进，行高与总宽都不变）。
 *
 * 几何全部来自快照既有的 `ButtonRef.col` / `width`（见 lib/touchLayout.ts），零 DOM 测量、
 * 零字体度量依赖。关闭触控排版时短路成空表。
 */
const hitExpansions = computed<ReadonlyMap<number, ReadonlyMap<number, HitExpansion>>>(() => {
  const table = new Map<number, ReadonlyMap<number, HitExpansion>>();
  if (!ui.touchLayout) return table;
  for (const item of visibleLines.value) {
    const expansions = computeLineHitExpansions(item.line, {
      targetCols: ui.touchTargetCols,
      isLive: (entry) => isTouchEligible(entry),
    });
    if (expansions.size > 0) table.set(item.absIdx, expansions);
  }
  return table;
});

/**
 * 单个按钮的热区外扩内联样式——注入 `--hit-l` / `--hit-r`（单位 `ch`）。
 *
 * 用 `ch` 而非 px：等宽终端下 1ch 就是 1 个 ASCII 字符推进宽，与快照里的「列」一一对应
 * （全角字符占 2 列 = 2ch），因此列几何可以直接落成样式，不需要把列换算成像素。
 *
 * 左右可以不对称——`.term-btn-touch` 用等量的负 margin 抵消各自一侧的 padding，
 * 所以两侧独立取值不会移动文字，也不会改变行内推进宽度。
 */
function hitStyle(item: { line: DisplayLine; absIdx: number }, entryIdx: number): Record<string, string> {
  const expansion = hitExpansions.value.get(item.absIdx)?.get(entryIdx);
  if (!expansion) return {};
  const style: Record<string, string> = {};
  if (expansion.left > 0) style['--hit-l'] = `${expansion.left}ch`;
  if (expansion.right > 0) style['--hit-r'] = `${expansion.right}ch`;
  return style;
}

/**
 * 把 ButtonValue 转 wire 字符串。
 * - number → String(num)（如 1 → "1"）
 * - string → 原值（如 "cancel" → "cancel"）
 *
 * 与 C# BuildInputJsonl 对称：value 字段始终序列化为 JSON string。
 */
function valueToWire(v: ButtonValue): string {
  return typeof v === 'number' ? String(v) : v;
}

/**
 * 按钮点击处理器——提交判定收敛到纯函数 inputRouting.shouldSubmitButtonValue，四重守卫：
 * 0. EnterKey/AnyKey 下按钮惰性——不提交 value（点击落到终端推进路径，避免多回显一行；
 *    与 CLI/WinForms 一致）
 * 1. `awaiting.input` 缺省 → 服务端此刻不期待输入，按钮不可点击（原来读 state !== 'WaitInput'）
 * 2. button.generation !== game.currentTurnGeneration → 旧回合按钮不可点击
 * 3. game.inputInFlight → 同一回合内防重复点击（乐观锁）
 *
 * 通过守卫后置 inputInFlight + 发送输入。服务端响应新 turn 时
 * applyTurn 会清锁 + 更新 generation。
 *
 * 按钮点击不受 isStickyToBottom 守卫影响——按钮走自身 @click，受 generation/inputInFlight
 * 守卫保护。即使翻看历史时残留按钮仍可点（spec.md 用户故事 9）。点击成功后置锁，
 * 由底部 inputInFlight watch 滚回底部——小屏幕上翻看历史后点击按钮仍能看到结果。
 */
function onButtonClick(entry: DisplayEntry): void {
  const button = entry.button;
  if (!button) return;
  if (!shouldSubmitButtonValue({
    connected: conn.status === 'connected',
    awaiting: game.awaiting,
    buttonGeneration: button.generation,
    currentTurnGeneration: game.currentTurnGeneration,
    inputInFlight: game.inputInFlight,
    canInput: conn.canInput,
  })) return;
  game.setInputInFlight();
  conn.sendInput(valueToWire(button.value));
}

// ---------- 真悬停上报（ADR-0031 / spec-v3 **M5a**） ----------
//
// ## 为什么必须放开 `disabled`
//
// Void 等待（`AWAIT` / `TWAIT t,1`）下 `awaiting.input` 为空 ⇒ 按钮原本 `:disabled`。
//
// 🔴 **实测事实（Edge，2026-10-15 —— 初稿在此写错过一次）**：原生 `disabled` 按钮
// **仍派发** `mouseenter` / `mousemove`，**不派发** `mousedown` / `mouseup` / `click`。
// ⇒ 所以放开 `disabled` 的理由**不是悬停**（悬停在 disabled 下本来就能上报），
// 而是**按下**：`COMF413:135` 的出口需要 `GETKEYTRIGGERED(0x01)`（← mousedown），
// 而按住/松开在这类元素上整条消失。
//   判据因此锚在 `keys`（VK_LBUTTON）上，只断言 `hoverValue` 会漏掉这条（e2e 已验证）。
// 另一半（`MOUSEB()`）由 M5a 的悬停上报提供。
//
// ## 为什么用 `aria-disabled` 而不是删掉 `disabled`
//
// ADR-0021 重开决策 6：直接删会让按钮不再置灰，玩家会以为「能点」，而它**并不能提交**。
// `aria-disabled` 保留「不可提交」的语义与视觉弱化，同时让元素继续接收指针事件。
// **可提交性仍由 `shouldSubmitButtonValue` 独占把关** —— 这里放开的只是「可感知性」。
//
// ## 上报口径
//
// - 只报**指针真实所在**的按钮。不用最近点击值冒充悬停（= E-min，ADR-0031 决策 5 已回退）。
// - 清空用**空串**而非省略字段：服务端 `cmd.hoverValue != null` 判别，省略与 `null` 都等于
//   「没有这一项」，清不掉；`""` 才会走到 `SubmitHover("")` ⇒ `MOUSEB()` 返回空串。
// - 去重：值与上次相同就不发帧（鼠标在同一按钮内移动不产生请求）。
// - 新回合清空：画面换了，旧悬停值必然不可信 ⇒ 宁可空也不能给脚本一个陈旧值。

/** 按钮当前是否「可提交」——仅用于呈现（`aria-disabled`），判定仍走 `shouldSubmitButtonValue`。 */
const canSubmitButtons = computed(() => conn.status === 'connected' && !!game.awaiting.input);

/** 最近一次上报的悬停值（wire 口径：`''` = 无悬停）。用于去重。 */
let lastHoverWire: string | null = null;

function reportHover(value: string | null): void {
  const wire = value ?? '';
  if (lastHoverWire === wire) return;
  lastHoverWire = wire;
  conn.reportInputState({ hoverValue: wire });
}

function onButtonHover(entry: DisplayEntry): void {
  if (!entry.button) return;
  reportHover(valueToWire(entry.button.value));
}

function onButtonLeave(): void {
  reportHover(null);
}

/**
 * 移动兜底：覆盖两种 `mouseenter/leave` 抓不到的情形——
 * - 新回合清空后指针未移动（行 DOM 可能被虚拟滚动重建，浏览器不保证补派发 enter）；
 * - 指针从按钮移到空白但没触发 leave（同上）。
 *
 * 值直接从 `data-hover-value` 读，与目标是不是按钮本身无关（子元素 `closest` 命中即可）。
 * 去重后开销等价于 mouseenter：同一按钮内移动零请求。
 */
function onTerminalMouseMove(e: MouseEvent): void {
  const el = (e.target as HTMLElement | null)?.closest?.('.term-btn') as HTMLElement | null;
  reportHover(el?.dataset.hoverValue ?? null);
}

// 新回合 = 新画面：旧悬停值作废（fail-safe，见文件头）。
watch(() => game.currentTurnGeneration, () => reportHover(null));

onBeforeUnmount(() => {
  // 组件卸载即无画面可言，清掉宿主里的悬停值，避免跨会话残留。
  lastHoverWire = null;
  conn.reportInputState({ hoverValue: '' });
});

/**
 * 终端点击处理器——推进判定收敛到纯函数 inputRouting.shouldAdvanceOnTerminalClick。
 *
 * 仅 EnterKey/AnyKey 模式推进（匹配 CLI DispatchMouseMiss）。
 * 按钮区域：非 EnterKey/AnyKey 态跳过（按钮有独立 @click 处理）；EnterKey/AnyKey 态
 * 按钮惰性、点击同样推进——与 onButtonClick 守卫 0 配套，该态下按钮点击既不提交 value
 * 也不成死区（stale 按钮同样推进）。
 *
 * 虚拟滚动 + sticky 守卫（spec.md决策三）：isStickyToBottom=false 时拒绝推进——
 * 用户翻看历史时点击应视为"继续翻/选中"而非"推进游戏"。滚回底部 = "我看完了，可以继续"。
 */
function onTerminalClick(e: MouseEvent): void {
  const target = e.target as HTMLElement | null;
  if (!shouldAdvanceOnTerminalClick({
    isButton: !!(target && target.closest('.term-btn')),
    isStickyToBottom: vs.isStickyToBottom.value,
    awaiting: game.awaiting,
    connected: conn.status === 'connected',
    inputInFlight: game.inputInFlight,
    canInput: conn.canInput,
  })) return;
  game.setInputInFlight();
  conn.sendInput('');
}

/**
 * 构造单个 segment 的内联 style 对象。
 * 返回 Partial<CSSStyleDeclaration> 风格的对象——Vue :style 接受驼峰键。
 *
 * `color` 是 hex 字符串（C# EmuColor.ToHex），直接赋值。
 * `bold` / `italic` 是 boolean，映射 fontWeight / fontStyle。
 * `fontname` 暂忽略——见组件头注释。
 */
// ---------- 背景图层（issue 04，spec Q6） ----------
//
// .terminal 内 fixed 定位层：滚动不随内容动（WinForms 主画布语义）；z-index 低于文本层。
// sortBgImages 升序 → 渲染顺序小 depth 在前、大 depth 在后（CSS 后者在上）=
// WinForms 降序烘焙（大 depth 最终在上）。opacity 逐张应用。
// 掩膜渲染（image/rect 三态）已下沉到 SegmentRenderer.vue（review 2026-08-06 抽重复）。
// 过滤 opacity<=0 的隐藏层（spec D）：透明度 0 = 不显示，渲染与否视觉等价；
// 过滤后可避免隐藏层请求资源 + 解码整图 + 上传 GPU 纹理的纯浪费。filter 保序，与 sort 顺序等价。
const sortedBgImages = computed(() =>
  sortBgImages(game.displayState.bgImages).filter((bg) => bg.opacity > 0),
);

/** 背景层内联样式——fixed 相对视口，宽度对齐游戏窗口（contentStyle 同款宽度）。
    z-index:-1：在 .terminal-content 的 stacking context 内沉到文本之下（文本 z-index auto），
    任何静态内容都浮在背景之上。 */
const bgLayerStyle = computed<Record<string, string>>(() => ({
  position: 'fixed',
  top: '0',
  left: '0',
  width: `${effectiveWindowWidth.value * game.effectiveScale}px`,
  height: '100%',
  zIndex: '-1',
  pointerEvents: 'none',
  overflow: 'hidden',
}));

// ---------- srcm 热区点击（issue 04，spec Q6） ----------
//
// 含 srcm 映射图的图片是按钮热区：点击取映射图对应像素色（0xRRGGBB）作为按钮输入值
// （WinForms ConsoleButtonString 语义）。提交十进制（C# 按 inputType 解析整数）。
// 非 srcm 图片不阻止冒泡——按钮内图片点击自然走既有按钮路径。
//
// 像素数学用 img 元素自身 rect（非掩膜 rect）：img 有 margin-top:ypos 且高=seg.height，
// 与掩膜（高=行高）不等——img rect 反映实际绘制位置，点击偏移自然补偿 ypos，
// 且"点击在图片外返回 null"的语义才准确。
//
// 连点竞态：读像素是异步的（await 前无法置 inputInFlight——读失败时置锁会卡死，
// 游戏没收到输入就没有新 turn 清锁）。用本地 busy 标志防重入；成功提交才置锁。
const imageClickBusy = ref(false);

async function onImageClick(e: MouseEvent, img: SegmentImage, entry: DisplayEntry): Promise<void> {
  if (!img.srcm) return;
  const button = entry.button;
  if (!button) return;
  if (!shouldSubmitButtonValue({
    connected: conn.status === 'connected',
    awaiting: game.awaiting,
    buttonGeneration: button.generation,
    currentTurnGeneration: game.currentTurnGeneration,
    inputInFlight: game.inputInFlight,
    canInput: conn.canInput,
  })) return;
  if (imageClickBusy.value) return;

  e.stopPropagation(); // 热区阻断：不提交按钮原值，改提交映射色（失败路径同样阻断——语义是"热区接管"）

  const maskEl = e.currentTarget as HTMLElement;
  const imgEl = maskEl.querySelector('img');
  if (!imgEl) return;
  const rect = imgEl.getBoundingClientRect();
  const natural = { width: imgEl.naturalWidth, height: imgEl.naturalHeight };
  const pixel = clickToImagePixel(rect, natural, e.clientX, e.clientY);
  if (!pixel) return;

  imageClickBusy.value = true;
  try {
    const rgb = await loadImageAndReadPixel(resolveResource(img.srcm), pixel.x, pixel.y);
    if (rgb == null) return; // 映射图加载失败/无 CORS——热区静默失效（已阻断冒泡）
    game.setInputInFlight();
    conn.sendInput(String(rgb)); // 十进制（0xRRGGBB 的数值）
  } finally {
    imageClickBusy.value = false;
  }
}

/**
 * 单行样式——对齐 +（被撑高的行）精确行高。
 *
 * 为什么被撑高的行要显式钉高：按钮盒在行盒里溢出，行盒最终还会叠加当前字体的
 * ascent/descent（每种游戏字体不同，可能多出 1~2px）。而 useVirtualScroll 的加高表按
 * "基准行高 + extra" 记账，多出来的零头会随行数累积成漂移。显式 height 让 DOM 与账本
 * 严格一致，行超过的部分仍在 overflow 语义下正常绘制（图片溢出不受影响）。
 *
 * **不要**在这里覆盖 `--hit-v`（盒高必须处处一致）：
 * - 收窄它 → 同屏出现两种高度的矩形（38px 那些上下紧贴文本），真机上一眼看出不齐；
 *   热区相撞改用撑高解决，见 computeLineExtras。
 * - 归零它 → 盒被撑满整槽，而 Chromium 的 button 布局会把内容在内容盒里垂直居中，
 *   按钮文字整体下沉半个增量、离开游戏给它的位置。
 * 这两条都在真机上错过一次，别再改回去。
 */
function lineStyle(item: { line: DisplayLine; absIdx: number }): Record<string, string> {
  const style: Record<string, string> = { textAlign: lineAlign(item.line) };
  const extra = touchLineExtras.value?.get(item.absIdx);
  if (extra !== undefined) style.height = `${effectiveRowHeight.value + extra}px`;
  return style;
}

/** 行 align 映射 CSS text-align。null / undefined 默认 'left'。 */
function lineAlign(line: DisplayLine): 'left' | 'center' | 'right' {
  return line.align ?? 'left';
}

// ---------- 显示变更信号：shift_head 补偿 + clear_screen 回底 ----------
//
// game store 在 applyDiff 归约时产出声明式 DisplaySignal（shift_head/clear_screen），
// 本 watch 据此驱动滚动副作用——不再跨 store 传 tick/count/watch 手指 scrollTop：
// - shift_head：非贴底查看历史时调 adjustScrollTop(-count * rowHeight) 保持视觉位置
//   （原来第 N 行仍在新位置的同一像素处）；贴底时新内容自然到达，无需补偿。
// - clear_screen：全清视为新画面，强制 isStickyToBottom=true + scrollToBottom——
//   覆盖 race 降级产生的 ClearScreenOp + Append（spec.md决策五）。
// 不能直接 watch(lines.length) 检测全清：applyDiff 单 tick 内顺序应用 clear_screen + append，
// Vue 响应式只观察最终 length（= append 后行数），length 突变到 0 的中间态不可见。
watch(() => game.lastDisplaySignal, (signal) => {
  if (!signal) return; // reset() 置 null——不触发
  if (signal.type === 'shift_head') {
    if (vs.isStickyToBottom.value) return; // 用户在底部——无需补偿
    vs.adjustScrollTop(-signal.count * effectiveRowHeight.value);
  } else if (signal.type === 'clear_screen') {
    if (!vs.isStickyToBottom.value) {
      vs.isStickyToBottom.value = true;
      ui.setStickyToBottom(true);
    }
    vs.scrollToBottom();
  }
});

// ---------- 提交输入后回到底部 ----------
//
// 玩家翻看历史（isStickyToBottom=false）后点击按钮 / 终端推进时，新回合输出不会自动跟随，
// 小屏幕上无法确认点击是否已生效。提交动作本身是明确意图——置锁（inputInFlight）时立即
// 滚回底部 + 置 sticky=true，后续新行自然自动跟随。
// 与 spec.md 用户故事 9 兼容：历史区按钮仍可点击，只是点击后回到底部查看结果。
// InputBar 的 submit() 同样置锁（TerminalDisplay 无法触达），两条提交路径共享此单一收口。
watch(() => game.inputInFlight, (inFlight) => {
  if (inFlight) vs.scrollToBottom();
});
</script>

<template>
  <div
    ref="terminalRef"
    class="terminal"
    :style="terminalStyle"
    @click="onTerminalClick"
    @mousemove="onTerminalMouseMove"
  >
    <div class="terminal-content" :style="contentStyle">
    <!-- v8 背景图层：fixed 定位、z-index 低于文本（见 bgLayerStyle/sortedBgImages 注释） -->
    <div v-if="sortedBgImages.length > 0" class="term-bg-layer" :style="bgLayerStyle">
      <img
        v-for="bg in sortedBgImages"
        :key="`${bg.src}:${bg.depth}`"
        class="term-bg-img"
        :src="resolveResource(bg.src)"
        :style="{ opacity: bg.opacity }"
        draggable="false"
      />
    </div>
    <!-- 2026-10-14（§4.3）：原 .terminal-empty 空状态块已删除 —— 四处提示均为重复呈现
         （加载/连接态 → TerminalView 的 StatusBanner overlay-status；解析错误 →
         StatusBanner kind="error" + InputBar.errorText；选择游戏 → MAUI 游戏列表），
         且不符合「顶部窄条、不常驻大条幅」的既有约定。
         lines.length === 0 ⇒ spacer 高度为 0 ⇒ 空白屏，与原版 Emuera 启动初期一致。 -->
    <!-- 虚拟滚动：spacer 撑总高度，可见行用 translateY(offsetY) 定位 -->
    <div
      class="term-virtual-spacer"
      :style="{ height: `${vs.totalHeight.value}px` }"
    >
      <div
        class="term-virtual-window"
        :style="{ transform: `translateY(${windowOffsetY}px)` }"
      >
        <div
          v-for="item in visibleLines"
          :key="item.absIdx"
          class="term-line"
          :style="lineStyle(item)"
        >
          <template v-for="(entry, entryIdx) in item.line.entries" :key="entryIdx">
            <!-- M5a（ADR-0021 重开决策 6）：`aria-disabled` 取代原生 `disabled` ——
                 实测后者**不派发** mousedown/mouseup/click（mouseenter 仍派发），
                 于是 Void 下 `GETKEYTRIGGERED(0x01)` 永远拿不到触发，COMF413 卡死。
                 可提交性由 onButtonClick → shouldSubmitButtonValue 独占把关，与此处无关。 -->
            <button
              v-if="entry.button"
              class="term-btn"
              :class="{ 'term-btn-touch': isTouchTarget(entry) }"
              :style="hitStyle(item, entryIdx)"
              :title="`点击提交: ${valueToWire(entry.button.value)}`"
              :aria-disabled="!canSubmitButtons"
              :data-hover-value="valueToWire(entry.button.value)"
              @click="onButtonClick(entry)"
              @mouseenter="onButtonHover(entry)"
              @mouseleave="onButtonLeave"
            >
              <SegmentRenderer
                :segments="entry.segments"
                @img-click="(e, img) => onImageClick(e, img, entry)"
              />
            </button>
            <span v-else class="term-entry">
              <SegmentRenderer
                :segments="entry.segments"
                @img-click="(e, img) => onImageClick(e, img, entry)"
              />
            </span>
          </template>
        </div>
      </div>
    </div>
    </div>
  </div>
</template>

<style scoped>
.terminal {
  /* monospace 字体——CJK 字符在浏览器中通常以 2x ASCII 宽度渲染，
     与 C# HeadlessConsole 的 display-width 计算一致，保证多按钮行对齐。
     font-family 实际值由 inline style 动态绑定（issue 12）——游戏字体名在前，
     此处 fallback 链在后。inline style 优先级高于此声明。 */
  font-family: ui-monospace, 'Cascadia Mono', Consolas, 'Courier New', monospace;
  /* font-size / line-height / font-family 由 inline style 动态绑定——
     font-size/line-height 用像素，font-family 首选游戏字体名。 */
  color: var(--term-color-text);
  background-color: var(--term-color-bg);
  /* pre：保留 PRINT 输出中的空格 / 缩进，长行不自动换行。
     Emuera 的 ConsoleDisplayLine 语义是"一行不拆分"——WinForms GDI 下字符画按
     FontSize/2 的 ASCII 字符宽度算列数，浏览器 monospace 每字符宽度约 0.6em
     比 GDI 宽，pre-wrap 会让字符画被浏览器拆行。改用 pre 保持行完整性，
     超长行由下方 overflow-x: auto 水平滚动兜底。
     C# 端的换行已结构化为 DisplayLine——这里不再做语义换行。 */
  white-space: pre;
  /* .terminal 填满父容器宽度（flex: 1），内容宽度由 .terminal-content 约束。
     超长行由 overflow-x: auto 水平滚动兜底（保留行不拆分）。
     垂直方向由父容器 align-items: stretch（默认）撑满。
     无 padding——与 WinForms mainPicBox / CLI 终端一致，字符画从容器边缘开始渲染。 */
  overflow-y: auto;
  overflow-x: auto;
  /* 触屏手势仲裁（usePinchZoom 的前置依赖，见其注释）：
     pan-x pan-y——单指横/纵向滚动保留给浏览器原生（虚拟滚动依赖 scrollTop），
     双指手势归 JS 接管、浏览器原生页面缩放/双击放大被禁用。 */
  touch-action: pan-x pan-y;
  flex: 1;
  min-height: 0;
  width: 100%;
  box-sizing: border-box;
}
.terminal-content {
  /* 固定宽 = 游戏设计宽度（× scale），靠左对齐；width/margin 由 inline style 动态绑定 */
  /* v8：相对定位 + z-index 1——浮在背景图层（z-index 0）之上 */
  position: relative;
  z-index: 1;
  /* 贴底渲染（2026-10-14 spec §4.1 方案 A）：内容不足一屏时块贴容器底部、上方留白——
     与原版 Emuera（GetLinePointY：末行恒锚可绘制区底部）一致，也让 Web 与已落地的
     CLI 贴底（B1/B4）保持同构。
     为什么安全（纯 CSS、零 JS 记账改动）：
     - 内容高于 100% 时容器 auto 高度 = totalHeight，自由空间为负 ⇒ justify-content 不生效
       ⇒ 满屏行为与改动前逐字节一致；
     - 不需要 clientHeight ⇒ 天然跟随 resize / 旋屏 / 软键盘 / InputBar 出没，无需 ResizeObserver；
     - 不碰 offsetOf / totalHeight / scrollTop 账本链 ⇒ 虚拟滚动既有测试全绿。 */
  min-height: 100%;
  display: flex;
  flex-direction: column;
  justify-content: flex-end;
}
/* v8 背景图层——fixed 相对视口（滚动不随内容动，WinForms 主画布语义）；
   z-index 0 低于文本层；pointer-events:none 不拦截任何点击 */
.term-bg-layer {
  pointer-events: none;
}
.term-bg-img {
  /* 原图尺寸渲染，左上对齐；opacity 由 inline style 逐张应用 */
  position: absolute;
  top: 0;
  left: 0;
}
/* 虚拟滚动 spacer——撑总高度（itemCount * rowHeight），position:relative 让子元素 absolute 定位 */
.term-virtual-spacer {
  position: relative;
  width: 100%;
  /* 贴底（§4.1）：父级 .terminal-content 改为 flex 列后，spacer 不得被 flex-shrink 压扁
     （否则虚拟滚动总高度塌陷 ⇒ 可见行窗口错位）。 */
  flex: none;
}
/* 虚拟滚动可见行窗口——translateY(offsetY) 定位，absolute 占满 spacer 宽度 */
.term-virtual-window {
  position: absolute;
  top: 0;
  left: 0;
  right: 0;
}
.term-line {
  display: block;
  /* issue 12：min-height 用 CSS 变量，与 line-height 对齐——避免行距叠加错位。
     变量由 .terminal inline style 注入，未注入时回落 1.5em（理论不应发生）。 */
  min-height: var(--term-line-min-height, 1.5em);
  /* 行内 inline 元素从左到右流式排列——按钮 entry 与文本 entry 自然交替。 */
}
.term-entry {
  display: inline;
}
.term-seg {
  /* 纯文本片段——颜色 / 粗体 / 斜体由 :style 内联应用 */
}
.term-btn {
  /* 按钮视觉：与普通文本完全一致；hover 时用下划线 + 低对比背景强调
     （ui-redesign-spec §6.2：文字优先、细边界或下划线，避免大块圆角胶囊）。
     WinForms 中按钮没有下划线/边框，只是悬浮高亮。
     color 固定为终端文字色——不继承父行，避免按钮自带阴影/系统色影响。 */
  display: inline;
  background: transparent;
  color: var(--term-color-text);
  border: none;
  border-radius: 0;
  padding: 0;
  margin: 0;
  cursor: pointer;
  font: inherit;
  text-decoration: none;
  /* 与文本基线对齐，避免按钮盒子顶起行高 */
  vertical-align: baseline;
  transition: background var(--motion-fast), text-decoration-color var(--motion-fast);
}
/* 触控盒自己管 hover / active 视觉（见下方 .term-btn-touch:xxx）——这里用 :not 排除，
   避免 0,4,0 的 hover 背景把热区矩形顶掉（那会让触控提示在桌面端忽隐忽现）。 */
.term-btn:not(.term-btn-touch):hover:not([aria-disabled='true']) {
  /* 双声明：旧 WebView 无 color-mix → 落到满强度 */
  background: var(--term-btn-hover-bg);
  background: color-mix(in srgb, var(--term-btn-hover-bg) 60%, transparent);
  text-decoration: underline;
  text-underline-offset: 2px;
  text-decoration-color: var(--term-btn-hover-line);
  text-decoration-color: color-mix(in srgb, var(--term-btn-hover-line) 70%, transparent);
}
.term-btn:not(.term-btn-touch):active:not([aria-disabled='true']) {
  background: var(--term-btn-active);
  background: color-mix(in srgb, var(--term-btn-active) 25%, transparent);
}
/**
 * 触控排版的按钮盒。
 *
 * 横向——热区按需外扩，且**文本一格都不动**：
 *   padding 把盒子撑宽（热区变大），等量负 margin 把行内推进宽度还原（后续字符横向位置
 *   不变）。二者相消后，这一行的排版与普通文本完全一致——这是「长按钮不必再挤开左右文本」
 *   与「邻居隔得够远时也不必挤开」的实现本体。外扩量由 lib/touchLayout.ts 按快照的
 *   col/width 列几何逐按钮算出，经 --hit-l / --hit-r 注入（ch 单位，1ch = 1 个 ASCII 字符
 *   = 1 列）。按钮自身已够宽、或与邻居的余量不足时外扩量为 0，它完全按原样渲染。
 *   z-index 是必需的：外扩区与后续文本 entry 在几何上重叠，不提升层叠则事件被文本取走、
 *   热区失效。
 *
 * 纵向——盒高恒为一个触控目标高、以文字行为中心上下各长 --hit-v，**处处一致**（不做任何
 *   按行收窄——那会做出 38px 那种上下紧贴文本的「压缩矩形」）；行高是否撑开由 lineStyle
 *   决定，而撑开只加槽位、不动盒与文字：
 *   ① 撑高的行（**它下方**窗口内有列区间重叠的存活按钮——判定见 computeLineExtras）：行被
 *      钉到「基准行高 + 几何上必需的撑高量」（相邻行才是满额，隔 1 行只要一点，再远不用），
 *      盒仍是同样的高度，于是与下方那张盒正好相切、命中边界就是槽边界；文字仍落在游戏
 *      给它的位置（撑高只往该行下方加空白）。
 *   ② 不撑高的行：行维持基准行距，盒靠「padding-top/bottom 外扩 + 等量负 margin 抵消」
 *      自己长到目标高度，上下各溢出 --hit-v、压住相邻的非按钮文本——与横向同一取舍。
 *      负 margin 抵消了 padding 对行盒高度的贡献，所以排版总高与后续行位置一格不变
 *      （Chromium 实测总高严格等于行数 × 基准行距），不进加高表。
 *
 * 视觉——圆角矩形提示就画在这个盒子上，所以「提示范围 == 真实热区」自动成立，不存在
 *   提示与热区不一致的误导。用低对比填充 + 极淡描边而非实心块：一屏二三十个按钮时
 *   高对比会糊成一堵墙。热区覆盖到的相邻文本仍可读（低 alpha，不做不透明遮挡）。
 *
 * 常显下划线在触控模式下取消——与圆角矩形同时存在会很吵；下划线留给 hover。
 *
 * `user-select: none`——终端文本默认可选中，不收掉的话点热区会顺手选中半屏文本。
 */
.term-btn-touch {
  display: inline-block;
  /* 盒高由「内容行高 + 2 × --hit-v」决定，**不要**给 min-height：那会把盒钉在目标高度上，
     于是 --hit-v 被收窄时盒收不动、而负 margin 仍按收窄后的值抵消，行盒反而被撑高、
     与虚拟滚动的账本失配（探针实测 19px 的行涨到 29px）。内容的行高恒等于基准行高，
     所以盒高正常恰为目标高度；内容更高时盒跟着长，不会截断——「最小宽高」语义仍然成立。 */
  padding-left: var(--hit-l, 0px);
  padding-right: var(--hit-r, 0px);
  /* 纵向：定义「内容盒落在行盒的哪一段」。值是全局一致的满额（盒高 = 内容行高 +
     2 × 本值 = 目标高度）；**不按行收窄**——那会让同屏出现两种高度的矩形。
     与横向同理，负 margin 与 padding 等量相消——盒变高，行盒贡献不变
     （撑高与否都不影响这一行的行盒高度，撑高只由 lineStyle 决定）。 */
  padding-top: var(--hit-v, 0px);
  padding-bottom: var(--hit-v, 0px);
  margin-left: calc(var(--hit-l, 0px) * -1);
  margin-right: calc(var(--hit-r, 0px) * -1);
  margin-top: calc(var(--hit-v, 0px) * -1);
  margin-bottom: calc(var(--hit-v, 0px) * -1);
  position: relative;
  z-index: 1;
  vertical-align: baseline;
  border-radius: var(--radius-hit, 6px);
  /* 双声明：旧 WebView 无 color-mix → 落到 token（等价低 alpha 字面量）。
     描边用 **inset**：外描边会溢出盒子 1px，而按钮盒恰好铺满 48px 行高、上下相邻行
     严丝合缝，于是相邻两行的描边各外溢 1px 就会在视觉上叠成 2px 的压边。内描边永不
     超出盒子，纵向横向都不可能重叠；填充本来就裁在盒内，所以「矩形 == 真实热区」仍成立。 */
  background: var(--term-hit-bg);
  background: color-mix(in srgb, var(--term-btn-active) 12%, transparent);
  box-shadow: inset 0 0 0 1px var(--term-hit-line);
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--term-btn-active) 25%, transparent);
  -webkit-user-select: none;
  user-select: none;
  transition: background var(--motion-fast), box-shadow var(--motion-fast),
    text-decoration-color var(--motion-fast);
}
/* hover 只补下划线，不替换热区矩形——矩形是常驻的触控提示。 */
.term-btn-touch:hover:not([aria-disabled='true']) {
  text-decoration: underline;
  text-underline-offset: 2px;
  text-decoration-color: var(--term-btn-hover-line);
  text-decoration-color: color-mix(in srgb, var(--term-btn-hover-line) 70%, transparent);
}
/* 按下时加强——触屏没有 hover，这是一过性反馈的唯一来源（确认感）。 */
.term-btn-touch:active:not([aria-disabled='true']) {
  /* 双声明：旧 WebView 无 color-mix → 落到满强度 */
  background: var(--term-btn-active);
  background: color-mix(in srgb, var(--term-btn-active) 30%, transparent);
  box-shadow: inset 0 0 0 1px var(--term-btn-active);
  box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--term-btn-active) 55%, transparent);
}
/* M5a（ADR-0021 重开决策 6）：不可提交改用 `aria-disabled` 表达 ——
   原生 `:disabled` 会让浏览器连 mouseenter/mousedown 都不派发，悬停报不上来。
   这里只做视觉弱化（不给 hover/active 反馈、光标不变手型），不改变事件可达性。 */
.term-btn[aria-disabled='true'] {
  cursor: default;
}
</style>
