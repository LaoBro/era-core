/// <reference types="vitest/config" />
/// <reference types="node" />
import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';

// Vite 配置：dev server 监听 5173，/ws 代理到 C# Kestrel server (localhost:8080)，
// 避免开发时浏览器跨域。生产构建产物 dist/ 由 C# csproj pre-build target 复制到 wwwroot/。
//
// Vitest 配置合并到同一文件（spec.md：「vitest.config.ts（可合并到 vite.config.ts）」）。
// test.environment='node' 因被测对象是协议层纯函数，无 DOM 依赖。
//
// base 路径：默认 '/'（Headless 同源服务），MAUI 场景由 build/VueBuild.targets 经
// VITE_BASE 环境变量（值 './'）+ vite build --base=./ 命令行参数双保险传入。
// 这里读 process.env.VITE_BASE 作 fallback，让 vite.config.ts 自身可独立 npm run build。
//
// assetsDir='static'：生产模式前端构建产物由 C# Kestrel wwwroot 同源服务，但 C# 侧
// 已有游戏图片通道路由 /assets/{**path}（spec Q3/Q5）——若前端产物仍放 /assets/ 会被
// 该路由抢先匹配（已匹配 endpoint 时 StaticFileMiddleware 跳过），导致 JS/CSS 全部 404。
// 故前端静态产物放到 /static/，与游戏图片通道 /assets/ 彻底分离，互不遮蔽。
// build.target / cssTarget：WebView 地板 = Chromium 61。
// 目标机（旧 Android）开发者选项里有两个 WebView 实现：默认 Chromium 61、备选 58（暂不支持）。
// 这是「前端实际支持的最小 WebView」，与 MAUI 侧 SupportedOSPlatformVersion=21 无关——
// 后者只约束 .NET 侧；前端地板完全由这里决定。
// 默认值（Vite 6 = es2020/edge88/firefox78/chrome87/safari14；Vite 8 起已抬到 chrome111）会让产物
// 含 `??` 等 ES2020 语法 → 旧 WebView 第一行即 Uncaught SyntaxError → 整页黑屏，且构建期零告警。
// 注：必须写浏览器 target，不能写 'es2018'——es 版本 target 按「ES2018 全部特性可用」处理，
// 而 Chromium 61 实际缺 async 迭代(63)/Promise.finally(63)/正则 s·lookbehind(62)/named group(64)，
// 用 'es2018' 会把它们放过去。'chrome61' 是严格更强的集合。
export default defineConfig({
  plugins: [vue()],
  base: process.env.VITE_BASE ?? '/',
  build: {
    assetsDir: 'static',
    target: ['chrome61'],
    // 显式钉住 CSS 目标，避免随 build.target 联动（Vite 内部 cssTarget = target）。
    // 目的（实测）：以现代目标（如 Vite 8 默认的 chrome111）压缩时，esbuild 会把 4 条定位长手
    // 合并成 `inset:`（本仓 CSS 有 6 处这种候选），而 Chromium 61 不支持 `inset`（Chrome 87 才有）；
    // 钉 chrome61 后保持长手写法（实测同一份内容 +132 B）。当前默认 target 数组含 safari14，
    // 恰因 Safari 14 不支持 inset 而没触发合并——即「碰巧没坏」，不是设计保证。
    cssTarget: 'chrome61',
  },
  server: {
    port: 5173,
    proxy: {
      // spec.md user story 1+2：WS 旁路端点，开发期由 Vite 代理到 C# Kestrel。
      '/ws': {
        target: 'ws://localhost:8080',
        ws: true,
        changeOrigin: true,
      },
      // C# KestrelGameServer 全部 HTTP 端点——dev 模式下前端 fetch 同源 5173，
      // 经 Vite 代理到 8080，避免 CORS。生产模式前端由 C# wwwroot 同源服务，无此问题。
      // 端点列表（KestrelGameServer.MapRoutes）：
      //   /session(POST/DELETE)、/turn(GET)、/input(POST)、/config(GET)、/snapshot(GET)、
      //   /load-game(POST, issue 05)、/state(GET, issue 05)、/native/pick-directory(POST, issue 05)、
      //   /game/scan(POST, Web 游戏扫描)、/game/dirs(POST, 目录浏览)
      '/session': 'http://localhost:8080',
      '/turn': 'http://localhost:8080',
      '/input': 'http://localhost:8080',
      '/config': 'http://localhost:8080',
      '/snapshot': 'http://localhost:8080',
      '/load-game': 'http://localhost:8080',
      '/state': 'http://localhost:8080',
      '/native': 'http://localhost:8080',
      '/game': 'http://localhost:8080',
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/__tests__/**/*.test.ts'],
  },
});
