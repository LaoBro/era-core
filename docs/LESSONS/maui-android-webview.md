# MAUI Android WebView / 桥接 / 资源通道 失败教训

> **TL;DR**：Android WebView 的页面 scheme、JS→C# 通道、资源拦截各有隐性约束——file:// 页面里的 https 子资源不进入 `shouldInterceptRequest`（页面必须与资源同 https 虚拟域）；平台判断必须按 hostname 不能只看 protocol；浏览器总会自动请求 `/favicon.ico`；WebView 回调里异常会被静默吞掉必须显式打日志；**前端地板由 Vite `build.target` 决定，不是 `SupportedOSPlatformVersion`**（默认 target 含 `??` → 旧 WebView 首行 SyntaxError 黑屏；降 target 又会清空 `import.meta` 打断懒加载）。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | WebView 远程调试需显式启用 | `SetWebContentsDebuggingEnabled(true)`；edge://inspect 比 chrome://inspect 稳 | — |
| 2 | file:// 下 CORS 拦截 ES Module/CSS | 需 `AllowFileAccessFromFileURLs` + `AllowUniversalAccessFromFileURLs` | — |
| 3 | AddJavascriptInterface 桥接不可靠 | 用户交互后静默失效；用 `ShouldOverrideUrlLoading` + URL scheme 备选通道 | — |
| 4 | bridge:// 拦截不要忘记 Query 参数 | 用 `Uri.GetQueryParameter()`，别手动拼 Host+Path+Query | — |
| 5 | NavigationPage.Navigated 对 file:// 不触发 | 用 HandlerChanged 或 OnAppearing 延迟触发 | — |
| 6 | **file:// 页面 + https 子资源不拦截（2026-08-08）** | WebViewAssetLoader 前提是页面与资源同 https 域；整页迁到 https://game.local | 2026-08-08 |
| 7 | **平台判断 protocol-only 误判（2026-08-08）** | isAndroidMaui 只看 file: 误判；按 hostname 区分 app.local/game.local | 2026-08-08 |
| 8 | **浏览器自动请求 /favicon.ico（2026-08-08）** | index.html 加 `<link rel="icon" href="data:,">` 阻止 | 2026-08-08 |
| 9 | **WebView 回调异常被静默吞（2026-08-08）** | shouldInterceptRequest 内必须 try-catch + logcat | 2026-08-08 |
| 10 | **AssetsPathHandler 绑定只有单参构造（2026-08-08）** | 自定义 IPathHandler 读子目录 + MIME 映射 | 2026-08-08 |
| 11 | **前端地板由 Vite build.target 决定，不由 SupportedOSPlatformVersion（2026-09-16）** | 默认 target 含 `??` → 旧 WebView 首行 SyntaxError 黑屏；target < es2020 还会清空 `import.meta` 破坏懒加载 | 2026-09-16 |

---

## 1. MAUI Android WebView 远程调试需显式启用

**场景**：app 白屏，需要用 Chrome DevTools 检查 Vue 前端 JS 运行时错误。

**结果**：`chrome://inspect` 看不到 `com.emuera.maui` 的页面。

**原因**：Android WebView 默认关闭远程调试，需要代码中显式调用 `Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true)`。

**解决**：在 `MauiProgram.ConfigureAndroidWebView()`（`MauiProgram.cs`）中添加该静态调用。此外，`edge://inspect` 比 `chrome://inspect` 连接更稳定——Chrome 有时返回 HTTP 404。

**教训**：WebView 远程调试不是默认开启的。只要用到 MAUI WebView + Android，必须在初始化阶段显式启用。Edge DevTools 是比 Chrome 更稳定的备用方案。

## 2. file:// 协议下 WebView 被 CORS 策略拦截 ES Module 和 CSS

**场景**：MAUI Android 用 `file:///android_asset/wwwroot/index.html` 加载 Vite 构建的 Vue SPA。

**结果**：白屏，WebView Console 报错：
- `Access to script at 'file:///...js' from origin 'null' has been blocked by CORS policy`
- `Access to CSS stylesheet at 'file:///...css' from origin 'null' has been blocked by CORS policy`

**原因**：Vite 默认构建产物使用 ES Module（`<script type="module">`）和独立 CSS 文件。Android WebView 在 `file://` 协议下默认禁止跨源加载（`origin: null` 无法加载 `file://` 资源）。

**解决**：在 `ConfigureAndroidWebView()` 中设置：
```csharp
wv.Settings.AllowFileAccessFromFileURLs = true;
wv.Settings.AllowUniversalAccessFromFileURLs = true;
```
两行分别放行 `file:// → file://` 和 `file:// → 任意源` 的跨域请求。

**替代方案**：也可让 Vite 构建为 IIFE 格式 + 内联 CSS（`vite-plugin-singlefile`），但修改 WebView 设置更简单，且不改变前端构建流程。

**教训**：`file://` 协议有严格的跨域限制，混合 Vite ES Module 构建产物时必须在 WebView 设置中显式放行。这是 MAUI + Vite 组合在 Android 上的必踩坑。

## 3. MAUI Android——AddJavascriptInterface 桥接在 WebView 中不可靠

**场景**：MAUI Android WebView 中通过 `AddJavascriptInterface` 注册 C# 对象供 JS 调用。启动时 `window.emueraBridge.postMessage()` 能正常工作，用户交互后静默失效——JS 端对象存在、调用不抛异常，但 C# 方法永不被触发。

**结果**：点击按钮无任何反应，C# 端收不到消息。`adb logcat` 也无 `Bridge.PostMessage` 日志。

**原因**：Android WebView 的 `AddJavascriptInterface` 在 MAUI 壳中存在可靠性问题——JS 引擎线程与 UI 线程间的 Java bridge 可能因 WebView 进程重启、JS context 重建或线程安全问题而静默断开。JS 端仍看到 `window.emueraBridge` 对象（来自旧绑定缓存），调用却不再触发 C#。

**解决**：使用 `WebViewClient.ShouldOverrideUrlLoading` + 隐藏 iframe 作为 JS→C# 备用通道：
```csharp
// C# 端——WebViewClient 拦截 bridge:// URL
platformView.SetWebViewClient(new BridgeClient(this));

class BridgeClient : WebViewClient
{
    public override bool ShouldOverrideUrlLoading(WebView view, IWebResourceRequest request)
    {
        if (request?.Url?.Scheme == "bridge")
        {
            var path = request.Url.Host + request.Url.Path;
            BridgeUrlReceived?.Invoke(path);
            return true;
        }
        return base.ShouldOverrideUrlLoading(view, request);
    }
}
```

```typescript
// JS 端——隐藏 iframe 发送 bridge:// URL
function sendBridgeUrl(action: string): void {
  const iframe = document.createElement('iframe');
  iframe.style.display = 'none';
  iframe.src = `bridge://${action}`;
  document.body.appendChild(iframe);
  setTimeout(() => document.body.removeChild(iframe), 500);
}
```

**教训**：`AddJavascriptInterface` 不应作为 MAUI WebView 的唯一 JS→C# 通道。`ShouldOverrideUrlLoading` + URL scheme 是 Android 文档推荐的标准备选方案，比 Java bridge 更底层、更可靠。二者可并存——启动初期走 bridge，备用 URL 拦截兜底。

## 4. bridge:// URL 拦截——不要忘记 Query 参数

**场景**：`WebViewClient.ShouldOverrideUrlLoading` 中手动拼接 URL 组件构建消息路径。

**结果**：`bridge://post?msg=...` 的 `?msg=...` 部分被丢弃，收到的消息只有 `"post"`（不含数据）。

**原因**：`request.Url.Host + request.Url.Path` 只含域名和路径，不含 Query 字符串。`bridge://post?msg=...` 解析后：`Host="post"`, `Path=""`, `Query="?msg=..."`。需同时拼接 `Query` 或用 `Android.Net.Uri.GetQueryParameter("msg")` 直接取参数。

**教训**：URL 解析用 SDK 的 `Uri.GetQueryParameter()`，不要手动拼接 `Host+Path+Query`。

## 5. Android——NavigationPage.Navigated 对 file:///android_asset/ URL 不触发

**场景**：在 `OnWebViewNavigated` 事件中触发自动启动内置游戏。

**结果**：事件永不被触发，因为 Android 上 `file:///android_asset/wwwroot/index.html` 的加载不走 MAUI 的 `Navigated` 事件路径。

**解决**：改用 `WebView.HandlerChanged` 事件（在 `OnPageFinished` 之后的时机）触发，或直接在 `Page.OnAppearing` 中延迟执行。

**教训**：Android `file:///android_asset/` 导航事件在 MAUI 中不可靠（`Navigated` 不触发，`Navigating` 也可能不触发）。需要自动触发的逻辑应放在 `HandlerChanged`（必触发）或 `OnAppearing` 中加延迟执行。

## 6. file:// 页面 + https 子资源不进入 shouldInterceptRequest——WebViewAssetLoader 的设计前提（2026-08-08）

**场景**：MAUI Android 用 `file:///android_asset/wwwroot/index.html` 加载页面，游戏图片用 `https://game.local/{path}` 由 `WebViewAssetLoader` 拦截（`AddPathHandler("/", GameAssetPathHandler)`）从 SAF 读字节。

**结果**：**图片全空但排版正常**。`edge://inspect` Network 面板图片请求 `ERR_NAME_NOT_RESOLVED`（~2 秒 DNS 超时）——请求直接走了真实网络，`game.local` 无法解析。Windows 端页面是 `https://app.local`（https 虚拟域）→ 图片同 https 域 → 正常；Android 端页面是 `file://` → 异常。无论 NativeAOT 与否均复现。

**原因**：AndroidX `WebViewAssetLoader` 的官方设计前提是**页面本身也用 https 虚拟域加载**（与资源同域）。`file://` 页面里的 `https://` 子资源请求根本不会进入 `WebViewClient.shouldInterceptRequest`（Chromium/WebView 的 file 页面跨 scheme 限制），`WebViewAssetLoader` 无从拦截，请求落到真实网络。

**解决**：整页迁到 https 虚拟域，与 Windows 模式对称：
1. `WebViewAssetLoader.Builder().SetDomain("game.local")` 注册两个 PathHandler：
   - `/wwwroot/` → 读 `android_asset/wwwroot/` 前端静态文件（index.html/js/css）
   - `/` → 游戏图片资源（AssetChannel → SAF 读字节）
2. `ResolveWebViewUrl` ANDROID 分支：`file:///android_asset/wwwroot/index.html` → `https://game.local/wwwroot/index.html`
3. 前端 `isMauiEnvironment()` 增加 `hostname === 'game.local'` 判定（原 file: 分支保留作旧部署兼容）

**验证**：`edge://inspect` Network 面板图片请求由 `ERR_NAME_NOT_RESOLVED` 变 `200`；logcat 出现 `GameAssetPathHandler: serve ...`。

**教训**：
- **`WebViewAssetLoader`（以及自定义 `shouldInterceptRequest` 拦截）只在页面与子资源同为 http(s) 域时可靠**。file:// 页面 + https 子资源是不拦截的陷阱组合。排查"图片空但排版正常"先看 Network 面板：`ERR_NAME_NOT_RESOLVED` = 拦截未生效（请求走网络），不是读字节失败。
- **Android 与 Windows 的"对称架构"要端到端对齐**：Windows 页面用 https 虚拟域（app.local），Android 页面必须同样处理，不能只在资源层对齐。
- 迁移页面 URL 会连锁影响：平台判断（见下条）、favicon（见下下条）、桥接通道——迁移后必须全流程回归。

## 7. 平台判断不能只看 protocol——isAndroidMaui 把 Android 误判为 Windows（2026-08-08）

**场景**：页面从 `file://` 迁到 `https://game.local/wwwroot/` 后，游戏列表页点「选择主目录」**毫无反应**。

**原因**：`MauiGameList.vue` 的平台判断是 protocol-only：
```ts
isWindowsMaui: window.location.protocol === 'https:' || protocol === 'ms-appx-web:'  // 没查 hostname！
isAndroidMaui: window.location.protocol === 'file:'                                   // 只看 file:
```
页面变 `https://game.local` 后：`isAndroidMaui` = **false**（protocol 不再是 file:），`isWindowsMaui` = **误判 true**（https: 就中）。于是点「选择主目录」走了 **Windows 的 `pickGameFolder()` 流程** → C# `HandlePickFolder` → `AndroidJsBridge.PickFolderAsync` 是接口默认空实现（返回 null）→ 无任何反应。

**解决**：按 hostname 分流（与 `isMauiEnvironment()` 对称）：
```ts
isWindowsMaui: protocol === 'ms-appx-web:' || (protocol === 'https:' && hostname === 'app.local')
isAndroidMaui: protocol === 'file:' || (protocol === 'https:' && hostname === 'game.local')
```
同时给 `MAUI_WINDOWS_VIRTUAL_HOST` 补 `export`（组件 import 用）。

**教训**：
- **两个 MAUI 平台页面都可能是 https 后，必须用 hostname 区分，不能只看 protocol**。`https:` 分支要么查 hostname，要么用 `isMauiEnvironment()` 的平台细分辅助函数，杜绝"protocol 一摸就判 Windows"。
- **平台误判的症状是"功能走了错误的通道"**：Android 点按钮走了 Windows 流程（空实现）→ 静默无反应。排障时先确认"点击后 Vue 走的哪个分支"（console 日志）。
- 迁移页面 URL 后，所有 `protocol === 'file:'` / `protocol === 'https:'` 的判断点都要重新审计（用 grep 全项目搜）。

## 8. 浏览器总会自动请求 /favicon.ico——拦截通道放行后报 DNS 错误（2026-08-08）

**场景**：页面迁到 `https://game.local/wwwroot/` 后，控制台持续报 `Failed to load resource: net::ERR_NAME_NOT_RESOLVED (/favicon.ico)`。

**原因**：浏览器**即使 HTML 没有 `<link rel="icon">` 也会自动请求 `/favicon.ico`**。该请求落到 `https://game.local/favicon.ico` → 兜底图片 PathHandler → `.ico` 不在图片白名单（png/jpg/gif/webp/bmp）→ reject → WebView 走真实网络 → `game.local` DNS 失败。Windows 上同样请求 `https://app.local/favicon.ico`，但 WebView2 虚拟主机映射对缺失文件返回 404（静默），所以之前未暴露。

**解决**：`index.html` 加空 favicon data URI，Chromium 解析到 `<link rel="icon">` 后不再自动请求：
```html
<link rel="icon" href="data:," />
```

**教训**：**只要页面有可解析的 favicon 引用（含 data URI），浏览器就不发 /favicon.ico 网络请求**。WebView 拦截通道对"未匹配/被拒"的请求会放行到网络层报 DNS/网络错误（Windows WebView2 对映射内缺失文件静默 404 是特例，不要依赖）。

## 9. WebView 回调异常被静默吞——拦截器必须显式打日志（2026-08-08）

**场景**：`WebViewAssetLoader.IPathHandler.Handle` 内 `AssetChannel.TryGetImage` 若抛未捕获异常，图片加载失败且 **logcat 无任何痕迹**。

**原因**：`shouldInterceptRequest` 回调抛出的 .NET 异常在 Java/WebView 边界被吞掉，既不打日志也不崩溃——排查盲区。

**解决**：`Handle` 包 try-catch，异常写 `Android.Util.Log.Error`（同时保留 `reject`/`serve` 的 Info/Warn 日志，作为"拦截是否生效、读取是否成功"的第一手证据）。

**教训**：**WebView 资源回调是异常黑洞**——任何 .NET 异常上抛到 JNI 边界都会无声消失。图片/资源加载失败的排查起点是 logcat 的 `AssetLoader intercepted` / `GameAssetPathHandler: serve|reject` 日志，而不是猜。

## 10. 第三方库 .NET 绑定 API 与 Java 不完全对齐——AssetsPathHandler 只有单参构造（2026-08-08）

**场景**：用 `new WebViewAssetLoader.AssetsPathHandler(context, "wwwroot")` 指定子目录，构建报 `CS1729 不包含采用 2 个参数的构造函数`。

**原因**：`Xamarin.AndroidX.WebKit 1.9.0` 的 .NET 绑定只暴露 `AssetsPathHandler(Context)` 单参构造（固定读 `android_asset/` 根），未暴露 Java 侧的 `(Context, String assetsPath)` 重载。

**解决**：自定义 `IPathHandler`（读 `android_asset/wwwroot/{path}` + `MimeTypeMap`/兜底 MIME 映射），不依赖绑定未暴露的重载。注意 `text/javascript` 是 ES Module 必需的 JS MIME，`MimeTypeMap` 缺失时手动兜底。

**教训**：**用第三方 AndroidX/Java 库前先确认 .NET 绑定的实际 API 面**（构造重载、方法签名），不能按 Java 文档直接写。绑定遗漏重载是常态，编译错误即第一信号。

## 11. 前端地板由 Vite build.target 决定，不是 SupportedOSPlatformVersion（2026-09-16）

**场景**：旧 Android 机（无 Play 商店；开发者选项里两个 WebView 实现：默认 Chromium 61、备选 58）安装最新 AOT 包，.NET 侧全部正常——控制面 server 起来、AssetLoader 正常响应、JS/CSS 资源送达。

**结果**：整页黑屏，无任何 UI。`chromium CONSOLE: "Uncaught SyntaxError: Unexpected token ?", index-*.js (1)` —— 第一行就炸。MAUI 侧 `Navigated` 只报成功，从 .NET 侧完全看不出问题。

**原因**：
- 前端产物按 Vite 默认 target（Vite 6 = `['es2020','edge88','firefox78','chrome87','safari14']`）编译，主包含 42 处 `??`（含 Vue 3.5 运行时自身的 `e??null` / `t??{}`）→ Chromium 61 连解析都过不去（`??` 要 Chrome 80）。同时项目里还有动态 `import()`（63）与 `import.meta`（64）。
- `SupportedOSPlatformVersion=21` 只约束 .NET 侧，对 WebView 版本**没有任何约束**。前端实际地板由 `build.target` 决定，而它比 Android 9 的首版 WebView（70+，可经 Play 升级）还高。

**解决**（见 ADR-0029）：
1. `vite.config.ts` 钉 `build.target: ['chrome61']` + `build.cssTarget: 'chrome61'`。**不要写 `'es2018'`**：es 版本 target 按「该版 ES 全部特性可用」处理，而 Chromium 61 缺 async 迭代(63)/`Promise.finally`(63)/正则 `s`·lookbehind(62)/named group(64)；浏览器 target 才是真实能力集合（实测同一产物 `es2018` 残留 `for await`、`chrome61` 全降级）。
2. **去掉动态导入**：`App.vue` 两个视图改静态导入。否则 Vite 注入 `__vitePreload(..., import.meta.url)`，而 esbuild 在 target < es2020 时把 `import.meta` 清空成 `{}`（官方 warning：`will be empty`）→ 预载助手 `new URL(dep, undefined)` 抛 `Invalid URL` → 懒加载视图在**所有**平台（含 Windows WebView2）都打不开，且构建期不报错。
3. `index.html` 加纯 ES5 探针（module 支持 + `Proxy` 特性检测），能力不足时填入可读文案，把黑屏变成看得见的失败（已装机的旧包也能看到）。
4. `AbortController`（Chrome 66，控制通道 `/control/wait` 在用）加特性守卫跳过——调用点是 `void startControlWatch(...)`，不守卫会每次连接留一条未处理 rejection。

**验证**：本地三份同源受控构建对照——最终产物 `??` / `catch{` / `import.meta` / 动态 `import(` 全为 0；体积净效果 gzip 口径约 −4%（两视图内联本身省字节，语法降级 +0.6% 被抵消）。真机需确认主界面与 Settings/Debug 均可进。

**教训**：
- **前端地板 = `build.target`，不是 `SupportedOSPlatformVersion`**。任何"支持到 Android X / WebView Y"的说法都要落到 Vite target 上验证；Vite 默认 target 随主版本持续抬高（Vite 8 默认已是 `chrome111`），必须显式钉住，并在升级 Vite 时复核。
- **黑屏的第一信号是首行 SyntaxError**：WebView 的 `Console` 消息（依赖 `SetWebContentsDebuggingEnabled(true)`）比 MAUI 侧 `Navigated`/`logcat` 更早定位问题；看到 `Unexpected token ?` 先查 `??`/`?.`，不要先怀疑资源通道。
- **降 target 会连带清空 `import.meta`**（esbuild 明确警告 "will be empty"）：凡用到动态导入 + 相对 base（本项目 `base='./'`）的产物，"只改一行 target"就会静默打断懒加载。降 target 前先全仓 grep 动态 `import(`。
- **esbuild 对正则 named group / lookbehind / `\p{}` / BigInt 字面量既不降级也不报错**（BigInt 仅 warning）——设了低 target 之后这类语法变成"谁写谁崩"的隐性雷区，需要 lint/构建期扫描兜底。
- **体积不是主要代价**：同源 A/B 实测，把两视图从懒加载改静态导入本身就是省字节（少 chunk 头部 + 跨模块摇树），真正代价在"能力边界"而非 KB。
