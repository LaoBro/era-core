import { defineConfig, devices } from '@playwright/test';

const PORT = 5178;
const BASE_URL = `http://localhost:${PORT}`;

/**
 * Playwright 配置（B6 Web 列：贴底布局的真实浏览器判据）。
 *
 * 浏览器用系统 Edge（channel: 'msedge'）—— 免下载 ~170MB 的 Chromium。
 * 目标只是量 CSS 布局，不需要多浏览器矩阵。
 */
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: BASE_URL,
    channel: 'msedge',
  },
  projects: [
    { name: 'edge', use: { ...devices['Desktop Edge'] } },
  ],
  webServer: {
    command: `npm run dev -- --port ${PORT} --strictPort`,
    url: BASE_URL,
    reuseExistingServer: true,
    timeout: 60_000,
  },
});
