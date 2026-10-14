import { test, expect, type Page } from '@playwright/test';

/**
 * 触屏 tap：前端到底发出什么帧、**顺序**如何。
 *
 * 动因：Android 上触控排版默认开（`ui.ts:179`「触屏是主要输入方式」）⇒ 触屏是正式场景，
 * 而 `COMF413` 这类脚本要求「`MOUSEB()` 在按下**之前**就有值」。触屏没有"把指针移过去"
 * 这个独立动作，所以能不能推进是个真问题，不能靠推断。
 *
 * 这里只回答**前端侧**：tap 是否产生悬停帧？它与按下帧谁先谁后？
 * 脚本侧能否出口由 `tests/test_web_input_to_script.py` 的同帧锚点推得
 * （悬停与按下落在同一脚本圈 ⇒ 需两次；早一圈 ⇒ 一次）。
 */
test.use({ hasTouch: true });

interface Reports {
  hover: string[];
  input: string[];
  keys: string[];
  focus: boolean[];
  timeline: string[];
}

async function setup(page: Page, awaiting: Record<string, unknown>): Promise<void> {
  await page.goto('/harness/layout.html');
  await page.waitForSelector('.terminal-content');
  await page.evaluate((a) => (window as unknown as { __setButtonRow(a: unknown): void }).__setButtonRow(a), awaiting);
  await page.evaluate(() => (window as unknown as { __clearReports(): void }).__clearReports());
}

function reports(page: Page): Promise<Reports> {
  return page.evaluate(() => (window as unknown as { __reports: Reports }).__reports);
}

async function center(page: Page): Promise<{ x: number; y: number }> {
  const box = await page.locator('.term-btn').boundingBox();
  if (!box) throw new Error('定位不到 .term-btn');
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}

test('触屏 tap（Void 态）：帧与顺序', async ({ page }) => {
  await setup(page, {}); // Void：与 COMF413 搜索模式同形

  const { x, y } = await center(page);
  await page.touchscreen.tap(x, y);
  await page.waitForTimeout(400);

  const r = await reports(page);

  // ① 触屏要能提交/推进：按键帧必须出现（否则连 GETKEYTRIGGERED 都没有）
  expect(r.keys).toContain('1:down');
  expect(r.keys).toContain('1:up');

  // ② 🔒 触屏 tap 会**合成** compatibility mouse events ⇒ 悬停帧照样发出。
  //    这一条此前只是推断（"触屏没有 hover"），实测推翻了它。
  const hoverIdx = r.timeline.indexOf('hover:42');
  expect(hoverIdx).toBeGreaterThanOrEqual(0);

  // ③ 🔒 且悬停**早于**按下 —— 与鼠标操作同序。
  //    顺序正确 ≠ 脚本能一次出口：脚本取的是**上一圈**的 MOUSEB，
  //    关键还要看两帧间隔是否大于一个脚本圈（`AWAIT 1` = 1ms）。
  //    脚本侧结论由 `tests/test_web_input_to_script.py` 的「背靠背」场景给出。
  const downIdx = r.timeline.indexOf('key:1:down');
  expect(hoverIdx).toBeLessThan(downIdx);
});
