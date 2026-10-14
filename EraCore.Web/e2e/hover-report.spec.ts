import { test, expect, type Page } from '@playwright/test';

/**
 * M5a：真悬停 / 真按键上报的**真实浏览器**判据（spec-v3 §8 L1'''）。
 *
 * ## 实测事实（Edge，2026-10-15，见本文件首条判据）
 *
 * 原生 `disabled` 按钮在真实浏览器里：
 * - **仍派发** `mouseenter` / `mousemove`（只读事件不受影响）
 * - **不派发** `mousedown` / `mouseup` / `click`
 *
 * ⇒ 所以「放开 `disabled`」的**真正理由不是悬停**（悬停在 disabled 下也能上报），
 * 而是**按下**：`COMF413:135` 的出口需要 `GETKEYTRIGGERED(0x01)`（← mousedown），
 * 而按住/松开事件在 disabled 元素上整条消失。判据必须锚在 `keys` 上才防得住假绿 ——
 * 只断言 `hoverValue` 的话，改回原生 `disabled` 测试照样全绿（已实测验证）。
 *
 * ## 用真实浏览器的理由
 *
 * happy-dom 不实现上述任何一条浏览器行为 ⇒ 组件测试只能靠源码契约断言撑着。
 */

/** 装好夹具并摆一行带按钮（值 42）的画面。`awaiting` 决定等待态（`{}` = Void / 定时等待）。 */
async function setup(page: Page, awaiting: Record<string, unknown>) {
  await page.goto('/harness/layout.html');
  await page.waitForSelector('.terminal-content');
  await page.evaluate(async (a) => {
    await (window as unknown as { __setButtonRow(a: unknown): Promise<void> }).__setButtonRow(a);
  }, awaiting);
}

interface Reports {
  hover: string[];
  input: string[];
  keys: string[];
}

function reports(page: Page): Promise<Reports> {
  return page.evaluate(() => (window as unknown as { __reports: Reports }).__reports);
}

/** 按钮中心坐标——`locator.click()` 不可用（Playwright 把 `aria-disabled` 判成 not enabled）。 */
async function center(page: Page): Promise<{ x: number; y: number }> {
  const box = await page.locator('.term-btn').boundingBox();
  expect(box).not.toBeNull();
  return { x: box!.x + box!.width / 2, y: box!.y + box!.height / 2 };
}

test('Void 下按钮用 aria-disabled 表达不可提交（不带原生 disabled）', async ({ page }) => {
  await setup(page, {});

  const btn = page.locator('.term-btn');
  await expect(btn).toHaveAttribute('aria-disabled', 'true');
  await expect(btn).not.toHaveAttribute('disabled', /.*/);
});

test('Void 下真实按下 ⇒ 宿主收到 VK_LBUTTON 电平 + 悬停值（放开 disabled 的真正理由）', async ({ page }) => {
  await setup(page, {});

  const { x, y } = await center(page);
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.up();

  const { keys, hover } = await reports(page);

  // 关键：原生 disabled 元素实测 **0 次** mousedown ⇒ 这两条会一起变空。
  // 这是 `GETKEYTRIGGERED(0x01)`（COMF413 出口的一半）的唯一来源。
  expect(keys).toEqual(['1:down', '1:up']);
  // 另一半：`ISNUMERIC(MOUSE_OVER)` ← MOUSEB() ← 悬停值
  expect(hover).toContain('42');
});

test('指针移出按钮 ⇒ 上报空串清空（服务端靠空串清，省略字段清不掉）', async ({ page }) => {
  await setup(page, {});

  const { x, y } = await center(page);
  await page.mouse.move(x, y);
  // 移到按钮外（画面左上角空白区）
  await page.mouse.move(1, 1);

  const { hover } = await reports(page);
  expect(hover.at(-1)).toBe('');
});

test('Void 下点击不提交（issue 10 红线：放开的是可感知性，不是可提交性）', async ({ page }) => {
  await setup(page, {});

  const { x, y } = await center(page);
  await page.mouse.click(x, y);

  // 先证明事件通道是通的（指针确实落在按钮上、悬停已上报）——
  // 否则「input 为空」可能只是因为根本没点到，属于假绿。
  const { hover, input } = await reports(page);
  expect(hover).toContain('42');
  expect(input).toEqual([]);
});

test('可提交态下悬停照常上报（不只在 Void 下工作）', async ({ page }) => {
  await setup(page, { input: { type: 'IntValue' } });

  const btn = page.locator('.term-btn');
  await expect(btn).toHaveAttribute('aria-disabled', 'false');
  const { x, y } = await center(page);
  await page.mouse.move(x, y);

  const { hover } = await reports(page);
  expect(hover).toContain('42');
});
