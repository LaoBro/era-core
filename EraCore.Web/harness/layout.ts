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
import { useGameStore } from '../src/stores/game';
import { useConnectionStore } from '../src/stores/connection';
import type { DisplaySnapshot } from '../src/types/protocol';

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
  render: () =>
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
    ),
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
