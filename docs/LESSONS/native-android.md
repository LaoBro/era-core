# Android 原生前端（Avalonia + SkiaSharp）教训

> **TL;DR**：Avalonia 12 的 AppBuilder 必须在 `AvaloniaAndroidApplication` 子类；Java 类名要 `[Register]` 固定；`ApplicationId` 不能含 Java 关键字（`native`）；Release 必须嵌入程序集；Activity 要声明完整 `ConfigurationChanges`；Avalonia 边到边绘制，状态栏/IME 都要自己做 inset；虚拟滚动与按钮命中用 Hosting 的几何，不要用 `col×FontSize/2`。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | Avalonia 12 AppBuilder 在 Application 子类 | 不是 Activity；`AvaloniaAndroidApplication<TApp>` + manifest `android:name` | 2026-11-03 |
| 2 | Java 类名必须 `[Register]` 固定 | 否则生成 `crc…` 名，manifest 对不上 → `ClassNotFoundException` 闪退 | 2026-11-03 |
| 3 | `ApplicationId` 不能含 Java 关键字 | `com.eracore.native` 的 `native` 是 Java 关键字，javac 报“需要标识符”；改为 `nativeapp` | 2026-11-03 |
| 4 | AppCompat 主题必需 | 缺 `Theme.AppCompat` 会在启动抛异常；`styles.xml` + `android:theme` | 2026-11-03 |
| 5 | Debug 嵌入程序集 | Fast Deployment 常报 `No assemblies found`；`EmbedAssembliesIntoApk=true` | 2026-11-03 |
| 6 | `ConfigurationChanges` 要全 | 缺 `ScreenLayout/SmallestScreenSize/Density` 会让旋转/wm size 重建 Activity；勿在 `OnDestroy` 无条件停会话 | 2026-11-03 |
| 7 | 状态栏 inset | Avalonia 在 Android 上边到边绘制；用 `status_bar_height` 加根 Padding，否则首行被时钟压住 | 2026-11-03 |
| 8 | IME 用 `IInputPane` + `AdjustResize` | 键盘遮挡高度加到根 Padding；模拟器 IME 可能只露出工具栏，`inset` 仍可作为证据 | 2026-11-03 |
| 9 | AndroidAsset 用 Link 映射 test_game | 不复制仓库 fixture；`AndroidAsset Include="..\test_game\**\*" Link=...`；解压到 filesDir 带 marker | 2026-11-03 |
| 10 | 虚拟滚动用 Hosting `VirtualWindow` | 不要自己算 first/last/贴底/滚动条；revision 变化才重绘 | 2026-11-03 |
| 11 | 按钮命中用实测 run 位置 | `ButtonRef.col/width` 是字符列（按 FontSize/2），比例字体下会偏；命中区在绘制阶段由 Skia 度量产出 | 2026-11-03 |
| 12 | 终态要按会话状态隐藏输入栏 | `QUIT` 后快照 `awaiting` 可能仍是旧输入等待；再用 `CoreSessionState.Completed/Stopped/Failed` 兜底 | 2026-11-03 |
| 13 | 图片解码要缓存且不得阻塞绘制 | 后台解码 + 绘制线程只取就绪图；淘汰跳过正在返回的条目，否则释放正在绘制的 `SKImage` | 2026-11-04 |
| 14 | 图片占位宽度 = 绘制宽度 | 统一走 `NativeGeometry.ImageWidth`；形状占 `x + width`。当 0 宽会让加图后行宽跳动 | 2026-11-04 |

---

## 1. Avalonia 12 AppBuilder 位置

**场景**：按 Avalonia 11 经验把 `AppBuilder.Configure` 放在 Activity，启动即崩。
**原因**：Avalonia 12 Android 的配置入口移到 `AvaloniaAndroidApplication<TApp>`。
**解决**：`[Register("com.eracore.nativeapp.NativeAndroidApplication")]` 子类 + manifest `android:name`；Activity 用非泛型 `AvaloniaMainActivity`。

## 2. Java 关键字 ApplicationId

**场景**：`com.eracore.native` 构建时 javac 报 `package com.eracore.native;` “需要标识符”。
**原因**：`native` 是 Java 关键字，不能作为包名段。
**解决**：`ApplicationId=com.eracore.nativeapp`，同步 `[Register]` 与 manifest。

## 3. Debug Fast Deployment

**场景**：Debug APK 安装后 `No assemblies found ... Assuming this is part of Fast Deployment` → SIGABRT。
**解决**：Debug 也设 `EmbedAssembliesIntoApk=true`；Release 用 AOT。

## 4. Activity 重建

**场景**：转屏/`wm size` 后 Activity 重建，旧实现 `OnDestroy` 停会话，新 Activity 空白。
**解决**：声明 `Orientation | ScreenSize | ScreenLayout | SmallestScreenSize | Density | FontScale | LayoutDirection`；只在 `IsFinishing` 时停会话。

## 5. 状态栏与 IME

**场景**：首行文本被状态栏时钟压住；键盘遮挡输入栏。
**解决**：根 Padding 加 `StatusBarHeightDp`；订阅 `IInputPane.StateChanged` 把 `EndRect.Height` 加到根 Padding。模拟器可能只显示 IME 工具栏（`inset≈41`），真机键盘需真机复验。

## 6. 按钮命中

**场景**：若用 `ButtonRef.col × FontSize/2` 换算像素，Noto Sans CJK 的 ASCII advance 不是精确 0.5em，按钮会点偏。
**解决**：Skia 绘制时同时产出实测 run 的命中区；`ColumnHitGeometry` 只作语义/回退。

## 7. 终态输入栏

**场景**：`QUIT` 后最后一帧快照的 `awaiting` 可能仍是上一个输入等待，输入栏不消失。
**解决**：`UpdateInputBar` 同时检查 `CoreSessionState.Completed/Stopped/Failed`，终态隐藏输入栏。

## 8. 图片、形状与背景渲染

**场景**：带立绘/CG/背景的 era 游戏在原生端出现大片空白；或加图后行宽跳动。

**做法**：
- `NativeRenderModel` 把 `SegmentImage`（含 `crop`）、`SegmentShape`、`snapshot.bgImages` 投影成 `NativeImage`/`NativeShape`/`NativeBgImage`，几何已是显示像素。
- `NativeImageCache` 按 `src`（直接相对路径或 sprite 名）缓存 `SKBitmap` + `SKImage`；字节经 `GameTree.TryReadImage` 读取（与 `/assets` 同一套消毒 + sprite 名回退），后台线程解码（并发 2），绘制线程只取已就绪的结果，滚动不重复解码。预取只覆盖当前可见行窗口（含 overscan），避免为整份快照一次排队上千张图。
- 占位/绘制宽度统一走 `NativeGeometry`：图片占 `image.width`，形状占 `x + width`（与 Web/CLI 一致）；缩放随 `NativeRenderStyle.Scale` 同比变化。
- 裁剪（`crop`）用「裁剪矩形 + 整图负偏移」实现，等价于 Web 的掩膜 + 负 margin。
- 背景固定不随内容滚动，按 `depth` 升序绘制，`opacity` 映射到 alpha；等比缩放到铺满画布（cover：取 `max(视口宽/图宽, 视口高/图高)` 作唯一缩放系数，水平居中、顶部对齐，超出部分裁切）。**语义权威是 Emuera WinForms `BakeBackground`**，Native 已对齐它；Web 端 `.term-bg-img` 也已对齐（`width/height:100%` + `object-fit:cover` + `object-position:center top`），三端同一套规则。Web 的 cover 基准是游戏窗口列宽（`effectiveWindowWidth × scale`），屏幕更宽时右侧留白是有意设计、不是缺陷；Native 以整块画布为基准，属实现差异。
- `srcm`：绘制阶段为含 `srcm` 的图片记录三块矩形（掩膜行高带、`<img>` 元素矩形、裁剪可见矩形）；点击时按 Web `clickToImagePixel` 语义反算自然像素，再读映射图同坐标像素，以 `0xRRGGBB` 的十进制提交。映射图与显示图一起预取；显示图未解码 / 坐标越界 / 映射像素 alpha=0 分别拒绝并写不同日志。**跨前端已知差异**：Web `readPixel` 忽略 alpha，透明像素仍返回 RGB 并提交；Native **有意收紧**，遇到 `alpha == 0` 拒绝提交并记日志。这是产品取舍，不是 bug；若将来要与 Web 完全一致，只需放宽 `NativeGameView.TryReadSrcmPixel` 的透明判定。

**坑**：
- 淘汰只能发生在绘制线程，且必须跳过「正在返回给绘制线程」的条目；否则会释放正在绘制的 `SKImage`。
- 解码失败或文件缺失画红色占位框并写 `EraCoreNative` 日志，不静默丢图。
- 有 `image`/`shape` 时不要再画同片段的 `text`（`text` 是调试标记，缺图时 Protocol 会带 AltText）；都画会把占位框排到文字右侧，占位看不见。
- `srcb`（悬停图）、多帧动图未实现。

## 9. 游戏库：删除只对「已复制」生效，原目录只移除记录

**场景**：玩家想清理已复制进私有目录的游戏、释放空间。

**做法**：
- 库页条目右侧操作按钮按来源分流：`Path` → `"移除记录"`（只删 settings.json 登记，**绝不删用户原目录**）；`Internal` → `"删除"`。
- 删除是两段式确认：第一次点击文案变 `"确认删除？"`，3 秒内未再点自动复位；第二次点击才执行。不引入 MessageBox 依赖，也不用长按菜单。
- `GameLibrary.DeleteInternalGame` 在后台线程递归删除 `filesDir/games/<dir>`（`.extracted-v1` 在目录内，随目录一起清掉），成功后清 `library.json` 的 `dir:<name>` 游玩记录；删除期间状态栏显示 `"正在删除…"` 并禁用导入类按钮。
- 安全边界：目标只由 `GamesDirectory()` + 经 `SanitizeName` 校验的目录名拼出；删前用 `Path.GetFullPath` 规范化，要求目标以 `games/` 前缀开头、且父目录恰为 `games/`。越界或来源不是 `Internal` 一律拒绝。
- 内置示例 `test_game` / `test_game_b` **允许删除**：`NativeRuntime` 启动时 `EnsureBuiltInGamesAsync` 会因 marker 缺失重新解压，成功文案会提示「下次启动时重新解压」。
- 判定与真删的公共逻辑放在平台无关的 `EraCore.Native.GameLibraryDeletePolicy`（`EraCore.Native`），Android 侧只负责取 `Context`、拼路径与写日志；因此这些边界（越界、非直接子目录、符号链接逃逸、Path 拒绝、记录清理）能被 `EraCore.Tests` 在 Windows 上直接覆盖。
