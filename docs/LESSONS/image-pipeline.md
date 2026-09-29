# 图片/尺寸探针 失败教训

> **TL;DR**：HEADLESS 用「轻量文件头尺寸探针」取代「解码整图」来拿 sprite 几何，会**天然丢失旧实现的格式覆盖**——旧实现解码任意格式（含 webp），新探针若只实现 png/jpeg，webp 图片就会落回"源尺寸 0"→ 前端 0 宽不可见。换「读取多样、解码能力弱」的实现时，必须对照旧实现清点格式覆盖，而不是只做"验过的那几种"。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 尺寸探针替代解码会丢格式覆盖 | ImageSizeProbe 只支持 png/jpeg → webp sprite 源尺寸 0 → 图片段 width=0 不可见 | 2026-08-22 |

---

## 1. `ImageSizeProbe` 不覆盖 WebP → 图集 webp sprite 0 宽不可见（2026-08-22）

**场景**：`TW4.881shoess` 能进游戏，但标题图空白。`resources/title.webp` 存在，图片服务端（`/assets/TW_title000`）一直返回 200 `image/webp`——不是服务或浏览器解码的问题。

**表象**：标题屏每个 `<img src='TW_titleNNN'>` 变成图片 segment，但 `width=0, height=16`（无 crop）。前端 SegmentRenderer 对无 crop 图片段渲 `width: scaled(0)=0px` → 完全不可见。

**根因**：尺寸来源是 [ImageSizeProbe.TryGetPixelSize](../../EraCore.Core/Shared/UI/Game/Image/ImageSizeProbe.cs)，**只识别 PNG/JPEG**，对 `.webp` 返回 false → [AppContents.GetSprite](../../EraCore.Core/Shared/UI/Game/Image/AppContents.cs) 走 sprite 名表分支时 `ProbeFile` 尺寸为 0，建不出替身 → `ConsoleImagePart` 源尺寸 0 → 缺省宽度推算失效回退 0。

**为什么旧实现（emoera.em）没问题**：上游 `ConsoleImagePart` 直接读 `cImage.DestBaseSize`，而 `cImage` 来自 `AppContents.GetSprite`——上游**解码整张位图**（System.Drawing + libwebp，专门处理 webp），任意格式尺寸天然正确。本仓库 Core 的 HEADLESS 路径为便携/Android（无 System.Drawing、无新依赖）改为**纯 C# 头探针**，初版只覆盖 png/jpeg，**漏了 webp**。

**修复**：给 `ImageSizeProbe` 补 WebP 头解析（RIFF 容器，不解码像素）：VP8L（`0x2F` + 32 位 LE 宽/高各 14 位）、VP8X（canvas 宽/高 24 位 LE）、VP8（关键帧起始码 + 紧排 28 位）；`TryGetPixelSize` 按 `RIFF…WEBP` 分发。并加 VP8L/VP8X、坏 magic、落盘读取 4 项单测。验证：标题图片段从 `w=0` → `w=1041, crop 正确`，图片正常显示。

**教训**：
- **轻量读取 ≠ 旧解码的格式覆盖**。用头探针/只读头替换「解码整图」时，功能上限由「探针认识的格式」决定；只实现验过的 png/jpeg，其它真实格式（webp 常见于 era 游戏）就直接掉到"尺寸 0"。替换实现前，先对照旧实现**清点支持的格式全集**。
- **图片"不显示"先看协议几何不是看服务/浏览器**。服务端 200 + 正确 MIME ≠ 图片可见；`width=0` 这种几何问题同样表现为空白。用 `/snapshot` 检查图片 segment 的 width/height/crop，比猜"格式能不能解码"更快。
- **新增格式解析按规范独立构造测试字节**，断言值来自格式规范而非实现重算（防 tautological）。