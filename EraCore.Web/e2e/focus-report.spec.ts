import { test, expect, type Page } from '@playwright/test';

/**
 * M6：焦点上报的**真实浏览器**判据（spec-v3 §7 M6）。
 *
 * `ISACTIVE` 的真值只是副产品，本项真正要防的是**幽灵按键**：窗口失焦后浏览器
 * 不会派发后续的 keyup / mouseup，电平若停在 1，脚本侧就是「玩家一直按着」。
 * 前端的一半是补发全量 up + 上报 `focus:false`，另一半是宿主清键（C#
 * `NotifyFocusChanged(false)`，由 `InputHostFocusTests` 覆盖）。这里钉住前端那一半。
 *
 * ⚠️ `window.dispatchEvent(new Event('blur'))` 是合成事件 —— 它验证的是**接线**
 * （监听器挂在哪个事件、发了什么帧），不验证浏览器何时派发该事件。真实失焦时序
 * 属于浏览器行为，不需要 era-core 保证。
 */

interface Reports {
  hover: string[];
  input: string[];
  keys: string[];
  focus: boolean[];
}

async function setup(page: Page): Promise<void> {
  await page.goto('/harness/layout.html');
  await page.waitForSelector('.terminal-content');
  await page.evaluate(() => (window as unknown as { __clearReports(): void }).__clearReports());
}

function reports(page: Page): Promise<Reports> {
  return page.evaluate(() => (window as unknown as { __reports: Reports }).__reports);
}

const blur = (page: Page) =>
  page.evaluate(() => {
    window.dispatchEvent(new Event('blur'));
  });
const focus = (page: Page) =>
  page.evaluate(() => {
    window.dispatchEvent(new Event('focus'));
  });

test('失焦 ⇒ 上报 focus:false', async ({ page }) => {
  await setup(page);

  await blur(page);

  const { focus: reports_ } = await reports(page);
  expect(reports_.at(-1)).toBe(false);
});

test('重新聚焦 ⇒ 上报 focus:true', async ({ page }) => {
  await setup(page);

  await blur(page);
  await focus(page);

  const { focus: reports_ } = await reports(page);
  expect(reports_.at(-1)).toBe(true);
});

test('🔒 失焦时按住不放的键被补发 up（幽灵按键的前端一半）', async ({ page }) => {
  await setup(page);

  await page.keyboard.down('a');       // 真实 keydown（code=KeyA ⇒ VK 0x41）
  await blur(page);                    // 失焦：浏览器此后不会再送 keyup

  const { keys, focus: reports_ } = await reports(page);
  expect(keys).toContain('65:down');
  expect(keys).toContain('65:up');     // ← 补发，否则电平永久停在 1
  expect(reports_.at(-1)).toBe(false);
});

test('没有任何按键时失焦也上报（旧实现会因按下表为空把整帧吞掉）', async ({ page }) => {
  await setup(page);

  await blur(page);

  const { keys, focus: reports_ } = await reports(page);
  expect(keys).toEqual([]);
  expect(reports_).toEqual([false]);
});
