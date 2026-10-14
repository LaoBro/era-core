import { test, expect, type Page } from '@playwright/test';

/**
 * B6 Web 列：**真实浏览器**里的贴底布局判据（spec §4.1 / §7）。
 *
 * vitest 侧只有源码契约断言（happy-dom 无布局、SFC 样式不进测试 DOM）—— 它能防误删，
 * 但证明不了真实布局。本文件用 Playwright 量 boundingBox 补上这一环。
 *
 * 判据（容器 600px、行高 19px ⇒ 约 31 行可见）：
 * - 空屏   ⇒ spacer 高 0（空白可绘制区，与原版 Emuera 启动初期一致）
 * - 稀疏   ⇒ spacer 底边 == 内容区底边（贴底），内容高 < 容器高
 * - 溢出   ⇒ spacer 顶边 == 内容区顶边（baseRow=0，与改动前逐字节一致）
 */

async function setLines(page: Page, count: number) {
  await page.goto('/harness/layout.html');
  await page.waitForSelector('.terminal-content');
  await page.evaluate(async (n) => {
    await (window as unknown as { __setLines(n: number): Promise<void> }).__setLines(n);
  }, count);
}

/** 容差 2px：取整与亚像素布局 */
const EPS = 2;

test('空屏：spacer 高度为 0（空白可绘制区）', async ({ page }) => {
  await setLines(page, 0);

  const spacer = await page.locator('.term-virtual-spacer').boundingBox();
  expect(spacer, 'spacer 应存在（模板改为无条件渲染）').not.toBeNull();
  expect(spacer!.height).toBe(0);
});

test('稀疏内容：内容块贴内容区底部（贴底渲染）', async ({ page }) => {
  await setLines(page, 3);

  const content = await page.locator('.terminal-content').boundingBox();
  const spacer = await page.locator('.term-virtual-spacer').boundingBox();
  expect(content).not.toBeNull();
  expect(spacer).not.toBeNull();

  // 内容不足一屏
  expect(spacer!.height).toBeLessThan(content!.height);
  // 底边对齐 ⇒ 贴底（改动前是顶边对齐）
  const bottomGap = Math.abs(spacer!.y + spacer!.height - (content!.y + content!.height));
  expect(bottomGap, `spacer 底边应贴内容区底边（差 ${bottomGap}px）`).toBeLessThanOrEqual(EPS);
  // 上方留白 = 容器高 − 内容高
  const topPad = spacer!.y - content!.y;
  expect(Math.abs(topPad - (content!.height - spacer!.height))).toBeLessThanOrEqual(EPS);
});

test('内容溢出：块顶回到内容区顶部（与改动前逐字节一致）', async ({ page }) => {
  await setLines(page, 60);

  const terminal = await page.locator('.terminal').boundingBox();
  const content = await page.locator('.terminal-content').boundingBox();
  const spacer = await page.locator('.term-virtual-spacer').boundingBox();
  expect(terminal).not.toBeNull();
  expect(spacer).not.toBeNull();

  // 内容高于视口 ⇒ 自由空间为负 ⇒ justify-content 不生效
  //（注意：溢出时 .terminal-content 会随内容长高，故与视口 .terminal 比，不与 content 比）
  expect(spacer!.height).toBeGreaterThan(terminal!.height);
  const topGap = Math.abs(spacer!.y - content!.y);
  expect(topGap, `溢出时 spacer 顶边应在内容区顶边（差 ${topGap}px）`).toBeLessThanOrEqual(EPS);
});
