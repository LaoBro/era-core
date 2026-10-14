/**
 * TerminalDisplay 布局夹具（仅 Playwright e2e 用，不参与生产构建）。
 *
 * 为什么需要它：happy-dom 没有布局引擎，SFC 样式也不进测试 DOM ⇒ 贴底这类**纯 CSS
 * 布局**在 vitest 里无法验证（只能靠源码契约断言）。本夹具在真实浏览器里挂载
 * TerminalDisplay，让 Playwright 能读到真实的 boundingBox。
 *
 * 注意：**不 mock** `useVirtualScroll` —— 真实布局依赖它算出的 totalHeight。
 */
import { createApp, h, nextTick } from 'vue';
import { createPinia } from 'pinia';
import TerminalDisplay from '../src/components/TerminalDisplay.vue';
import { useKeyMouseInput } from '../src/composables/useKeyMouseInput';
import { useGameStore } from '../src/stores/game';
import { useConnectionStore } from '../src/stores/connection';
import type { AwaitInfo, DisplaySnapshot } from '../src/types/protocol';

type Line = DisplaySnapshot['lines'][number];

function mkLine(text: string): Line {
  return {
    entries: [{ segments: [{ text, color: null, bold: null, italic: null, fontname: null }] }],
    align: 'left',
    isLineEnd: true,
  };
}

// 用渲染函数而非 template —— 项目用的 Vue 构建不含运行时模板编译器
const app = createApp({
  setup() {
    // M5a：装上真实键鼠上报（生产里由 App.vue 安装）。e2e 要用它验证
    // 「Void 下按钮的 mousedown 能到达宿主」—— 那才是放开 `disabled` 的真正理由
    // （实测：`disabled` 元素**仍派发** mouseenter/mousemove，但**不派发** mousedown/mouseup/click）。
    useKeyMouseInput();
    return () =>
      h(
        'div',
        {
          class: 'terminal-area',
          style: {
            position: 'relative',
            flex: '1',
            minHeight: '0',
            display: 'flex',
            flexDirection: 'column',
          },
        },
        [h(TerminalDisplay)],
      );
  },
});
app.use(createPinia());
app.mount('#app');

const game = useGameStore();
const conn = useConnectionStore();
conn.status = 'connected';

/**
 * 设置显示行数并等 DOM 更新完成（Playwright 侧 `page.evaluate` 会 await 返回的 Promise）。
 */
(window as unknown as Record<string, unknown>).__setLines = async (count: number) => {
  const lines = Array.from({ length: count }, (_, i) => mkLine(`x${i}`));
  game.displayState = {
    ...game.displayState,
    lines,
    bgImages: [],
    awaiting: { input: { type: 'IntValue' } },
  };
  game.currentTurnGeneration = 0;
  await nextTick();
  await new Promise((r) => requestAnimationFrame(() => r(null)));
};

// ---------- M5a：真悬停上报判据的夹具 ----------
//
// 为什么要真实浏览器：happy-dom 不实现「`disabled` 元素不派发鼠标事件」这条浏览器行为，
// 组件测试里永远测不出「按钮 disabled ⇒ 悬停报不上来」这个硬 blocker。
// Playwright 则**会**——`locator.hover()` 对原生 disabled 元素等到 actionable 超时，
// 于是「放开 disabled」这件事有了不可绕过的判据。

/** 组件真正发出去的帧内容（patch store 方法后记录，不是 mock 结果）。 */
const hoverReports: string[] = [];
const inputReports: string[] = [];
/** 键鼠事件，wire 口径 `code:down|up`（VK_LBUTTON = 1）。 */
const keyReports: string[] = [];
/** 焦点上报（M6）——`ISACTIVE` 的真值来源。 */
const focusReports: boolean[] = [];

conn.reportInputState = ((extra: {
  hoverValue?: string;
  keys?: { code: number; down: boolean }[];
  focus?: boolean;
}) => {
  if (extra.hoverValue !== undefined) hoverReports.push(extra.hoverValue);
  if (extra.focus !== undefined) focusReports.push(extra.focus);
  for (const k of extra.keys ?? []) keyReports.push(`${k.code}:${k.down ? 'down' : 'up'}`);
}) as typeof conn.reportInputState;

conn.sendInput = ((value: string) => {
  inputReports.push(value);
}) as typeof conn.sendInput;

/**
 * 摆一行带整数按钮（值 42）的画面，`awaiting` 决定等待态（`{}` = Void / 定时等待）。
 */
(window as unknown as Record<string, unknown>).__setButtonRow = async (awaiting: AwaitInfo) => {
  game.displayState = {
    ...game.displayState,
    lines: [
      {
        entries: [
          {
            segments: [{ text: '[42] ClickMe', color: null, bold: null, italic: null, fontname: null }],
            button: { value: 42, isInteger: true, generation: 0 },
          },
        ],
        align: 'left',
        isLineEnd: true,
      },
    ],
    bgImages: [],
    awaiting,
  };
  game.currentTurnGeneration = 0;
  await nextTick();
  await new Promise((r) => requestAnimationFrame(() => r(null)));
};

/** 清空上报记录（同一页面内跑多条判据时复用）。 */
(window as unknown as Record<string, unknown>).__clearReports = () => {
  hoverReports.length = 0;
  inputReports.length = 0;
  keyReports.length = 0;
  focusReports.length = 0;
};

(window as unknown as Record<string, unknown>).__reports = {
  hover: hoverReports,
  input: inputReports,
  keys: keyReports,
  focus: focusReports,
};
