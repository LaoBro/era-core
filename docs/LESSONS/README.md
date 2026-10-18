# 失败教训索引（渐进式披露）

> 本目录收纳项目各阶段的失败教训（原 `docs/LESSONS.md` 单文件已拆分为按主题分类的多文件）。
> **渐进式披露**：先在下方索引按"一句话摘要"定位主题，再进对应文件看 TL;DR 清单，需要细节才读具体小节。
> 新增教训：按主题追加到对应文件末尾（含日期），勿再堆回单个大文件。

## 快速定位

| 主题 | 一句话摘要 |
|---|---|
| [terminal-windows.md](terminal-windows.md) | Windows 终端渲染：ANSI 转义需显式启用、清行别填满行、Ambiguous 宽度按组探测、宽度计算排除转义码 |
| [win32-pinvoke.md](win32-pinvoke.md) | P/Invoke 结构体：BOOL 用 int 不用 bool、布局与 Win32 原始定义一一对应、过滤条件别凭直觉扩大 |
| [csharp-pitfalls.md](csharp-pitfalls.md) | C# 陷阱：静态字段按声明序初始化、单行 if 插日志必须加大括号 |
| [maui-android-lifecycle.md](maui-android-lifecycle.md) | MAUI Android 生命周期：MainPage 构造早于 Activity.OnCreate、RegisterForActivityResult 时序、白屏三连 |
| [maui-android-webview.md](maui-android-webview.md) | MAUI Android WebView/桥接/资源通道：file:// 混 https 子资源不拦截、平台判断按 hostname、favicon 自动请求、JS→C# 通道选型、前端地板由 Vite build.target 决定（旧 WebView 首行 SyntaxError 黑屏） |
| [saf-file-access.md](saf-file-access.md) | Android SAF 文件访问：content:// 不是文件路径、docId 提取口径统一、缓存/预取/失效设计、sprite 名解析、真实路径 accessor 须镜像 SAF 通配语义、精确名读取亦然（挂载容错≠代码容错）、改 accessor 后须审计直连枚举 |
| [nativeaot.md](nativeaot.md) | NativeAOT 发布：JSON 反射禁用、RequestDelegateGenerator、ILC 增量缓存、目录隔离补 DefaultItemExcludes、Android 转译环境（Houdini）并发加载 GC 死循环（真机 ARM64 零复现）、启动即闪退 = dex 缺 NativeAotRuntimeProvider（增量 flag 不认运行时形态切换，清 obj-aot 重建） |
| [testing-and-ci.md](testing-and-ci.md) | 构建/测试/CI 环境：增量构建 0 警告假象、文件锁先查环境、行号基线脆弱、残留进程干扰回归、两轴 code-review、Windows 全绿≠跨平台正确（大小写/分隔符需 WSL2 关卡）、含中文 .ps1 必须 UTF-8 with BOM（PS 5.1 按 GBK 读） |
| [diagnostics.md](diagnostics.md) | 排查方法论：诊断推进揭开下一层、logcat 时间线拆段、中文乱码≠路径损坏、异常可见性 |
| [web-fonts.md](web-fonts.md) | Web 跨平台字体：MS Gothic 符号区宽度非均匀、回退链缺口、Unifont 按实测宽度选字、CFF→TTF 漏 scale、Vite 内联、dist-maui/SkipVueBuild、WebView 缓存 |
| [web-css-dom.md](web-css-dom.md) | Web CSS/DOM 行为：overflow hidden 仍可程序滚动、focus() 滚所有可滚祖先、transform 动画溢出撑 scrollHeight、截断用 clip 不用 hidden、fixed 不动=祖先被滚、视图切换（v-if）丢滚动位置须"不卸载 + visibility"保住 |
| [image-pipeline.md](image-pipeline.md) | 图片/尺寸探针：轻量头探针替代解码会丢格式覆盖（webp→0 宽不可见）；图片"不显示"先查协议几何 |
| [dialect-and-sqlite.md](dialect-and-sqlite.md) | 方言移植与 SQLite：profile 必须在 scope 内解析、methodList 并入 funcDic 形成命令形、键级 delta 漏签名、`.als` 静默中断、IR 同名覆盖、原生库分发位置 |

## 约定

- 每篇文件顶部是 **TL;DR** + **教训清单表**（扫读层），往下是逐条 **场景/结果/原因/解决/教训**（细节层）。
- 日期标注在标题后（`（2026-08-08）` 或正文），便于按时间回溯。
- 跨主题教训（如 SAF 性能与缓存）归主主题文件；方法论归 `diagnostics.md`。
