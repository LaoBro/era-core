# 构建与发布

各平台的构建/发布方式汇总。日常快速构建（跑起来玩）见根 [`README.md`](../../README.md)「快速开始」；本机环境陷阱见 [`CLAUDE.md`](../../CLAUDE.md)。

---

## 发布（推荐：`scripts/publish.ps1`）

所有可分发产物统一收集到仓库根 `artifacts/`，完成后脚本打印最终产物路径（exe/apk，非 dll）。

MAUI 目标默认走**快路径**：仅当前端源码比 `EraCore.Maui/wwwroot` 产物新（或传 `-Rebuild`）时才重建前端（`npm run build` → `dist-maui` → 清空并重拷 `wwwroot`），源码未变化时直接复用现有 `wwwroot`，避免每次发布都重建（见 [`docs/LESSONS/testing-and-ci.md`](../LESSONS/testing-and-ci.md) #11）。

```powershell
.\scripts\publish.ps1                    # 显示用法
.\scripts\publish.ps1 cli                # CLI 普通发布（框架依赖单文件）→ artifacts/cli/
.\scripts\publish.ps1 cli-aot            # CLI NativeAOT win-x64 → artifacts/cli-aot/win-x64/
.\scripts\publish.ps1 maui-win           # MAUI Windows 普通发布 → artifacts/maui-win/
.\scripts\publish.ps1 maui-win-aot       # MAUI Windows NativeAOT → artifacts/maui-win-aot/win-x64/
.\scripts\publish.ps1 maui-android       # MAUI Android 普通 Release → artifacts/maui-android/<rid>/
.\scripts\publish.ps1 maui-android-aot   # MAUI Android NativeAOT → artifacts/maui-android-aot/<rid>/
.\scripts\publish.ps1 all                # 全部
.\scripts\publish.ps1 list               # 列出已有产物
```

常用选项：

- `-Rid arm64|x64|all`：Android RID 短名（默认 arm64，实际传 `android-arm64`/`android-x64`）
- `-Rebuild`：**全量重建兜底**——清 artifacts 输出 + AOT 目标清 `obj-aot`/`bin-aot` + 强制前端重建。产物异常时用（如 Android NativeAOT 启动即闪退 = dex 缺 `NativeAotRuntimeProvider`，见 [`docs/LESSONS/nativeaot.md`](../LESSONS/nativeaot.md) #9；或前端改动未进 APK）
- `-SkipFrontend`：强制跳过前端重建（即使源码有更新也不重建；默认仅在源码未变化时复用 `wwwroot`）
- `-Clean`：发布前清空对应 artifacts 子目录（`-Rebuild` 已包含此行为）
- `-MsvcLib <path>`：`cli-aot` 手动指定 MSVC LIB（分号分隔三段），不传则自动探测

Android 目标需 Android SDK + JDK 17+（`JAVA_HOME`）；`cli-aot` 需 VS「使用 C++ 的桌面开发」工作负载（脚本自动探测 MSVC）。

---

## 原生 dotnet 命令（参考，自定义参数/排查时用）

以下为不带脚本封装的原始命令。MAUI 目标直接使用时注意前端滞后问题（见下方 Android NativeAOT 约束 3）。

### CLI & Server 模式（共享 `EraCore.Cli`）

```bash
dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug
```

发布单文件：

```bash
dotnet publish EraCore.Cli/EraCore.Cli.csproj -c Release --no-self-contained -o publish/cli
```

### MAUI Windows 桌面应用

```bash
dotnet build EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

MAUI 不支持 `PublishSingleFile`。发布需框架依赖或独立部署：

```bash
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-windows10.0.19041.0 -c Release --self-contained -r win-x64
```

### Android APK

```bash
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-android -c Release
```

需要 Android SDK + JDK 17+（`JAVA_HOME`）环境。

---

## Android NativeAOT（PublishAot）

NativeAOT 将托管代码编译为原生 so（APK 内 0 托管 dll，仅 `lib/<abi>/libEmuera.Maui.so`），启动更快、包体更小，但构建更慢、对反射/序列化有限制。实验验证（2026-08-08）：MuMu 模拟器 + 真机 arm64 均可运行。

```powershell
# x64（模拟器，如 MuMu）——单行命令，PowerShell 不认 bash 的 `\` 续行符
# 无需 -p:TreatWarningsAsErrors=false：csproj 自带 TreatWarningsAsErrors=true，
# 且 PublishAot=true 时经 WarningsNotAsErrors 把 XA1040/IL3053 降级为警告（不阻塞）。
# HTTP 请求/响应 JSON 的 IL2026/IL3050 已于 2026-08-27 在根因清零（源生成重载），
# 故新警告会直接变 error 拦截回归——勿再加 false 覆盖。
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-android -r android-x64 -c Release -p:PublishAot=true -p:RunAOTCompilation=false -p:AndroidPackageFormats=apk -p:PublishDir=artifacts/nativeaot/maui-android-x64/ -p:SkipVueBuild=true

# arm64（真机）
dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-android -r android-arm64 -c Release -p:PublishAot=true -p:RunAOTCompilation=false -p:AndroidPackageFormats=apk -p:PublishDir=artifacts/nativeaot/maui-android-arm64/ -p:SkipVueBuild=true
```

**关键约束（务必遵守）：**

1. **目录隔离**：`EraCore.Maui/Directory.Build.props` 在 `PublishAot=true` 时自动把中间产物/输出重定向到 `obj-aot/`/`bin-aot/`，并补回 `DefaultItemExcludes`（`obj/**;bin/**`）——**NativeAOT 与普通构建（Mono Full AOT）互不污染**。不要手动传 `-p:BaseIntermediateOutputPath`（全局属性会传染 Core 项目，且破坏 SDK 默认排除导致 CS0579）。
2. **JSON 序列化必须走源生成**：NativeAOT 下 `System.Text.Json` 反射序列化被禁用（`JsonSerializerIsReflectionDisabled` 抛异常）。壳层消息走 `EraCore.Maui/Json/MauiJsonContext.cs`（具名 record + `[JsonSourceGenerationOptions(CamelCase)]`），协议层走 Core 的 `EmueraJsonContext`（显示/回合/配置）与 `ServerJsonContext`（HTTP 请求 body POCO）。**新增壳层消息禁止匿名类型 `JsonSerializer.Serialize(new {...})`**。
3. **`-p:SkipVueBuild=true`**：跳过 Vue 前端构建（使用 `EraCore.Maui/wwwroot/` 现有产物）。若改了 `EraCore.Web/` 前端代码，需先 `npm run build -- --base=./ --outDir=dist-maui` 并拷贝到 `EraCore.Maui/wwwroot` 再构建（`scripts/publish.ps1` 的 MAUI 目标会在发布前自动完成这步）。
4. **`-p:RunAOTCompilation=false`**：避免 Mono Full AOT 与 NativeAOT 双重编译（`PublishAot=true` 时默认 `RunAOTCompilation=true`，需显式关掉）。
5. **ILC 警告治理**：构建会多出 IL2026/IL3050/IL207x 等 AOT 警告。HTTP 请求/响应 JSON 路径（`HttpRouteDispatcher.Parse<T>` / `GameServerProtocol` 的 `JsonArray.Add`）已于 2026-08-27 在根因清零（改源生成重载）；壳层亦已清零。剩余豁免（DataTable 反射、PluginManager 动态加载、`Results.Json` resolver）逐条登记于 [`docs/2026.8.4.安卓性能优化2/nativeaot-verify-report.md`](../2026.8.4.安卓性能优化2/nativeaot-verify-report.md) §5.2 并附实测/理由。新增警告需登记到 §5.2，不许静默 suppress。
6. **已知风险**：`AndroidEnableMarshalMethods=false`（csproj）与 NativeAOT JNI 通道的兼容性未做压力验证；游戏加载链路（loadGame → turn 渲染）在 NativeAOT 下待完整真机验证（2026-08-08 已通过：进入游戏选择界面，0 FATAL）。

---

## 前端与 MAUI 资源同步

MAUI 的 `wwwroot` 由 `build/VueBuild.targets` 从 **`dist-maui/`**（不是 `npm run build` 默认的 `dist/`）复制填充。

**发布提醒**（改过前端/字体后）：

- publish 时**不要带 `-p:SkipVueBuild=true`**（上面的 README/示例命令默认带它，那是"未改前端"的场景），否则 `wwwroot`/APK 沿用旧前端。
- 安装前**先卸载旧 APK**——Android WebView 对 `file://` 资源有缓存，覆盖安装可能继续用旧字体。
- 产物验证：`dist-maui/assets/` 里 <4KB 的字体（如 `EmueraBlock`）会被 Vite 内联为 css `data:font` base64，没有独立 woff2 文件是正常现象，别误判"没打包"。

---

## 警告治理约定

I-12 阶段 1 已启用按路径分级的质量护栏。`EraCore.Core/Shared/`（迁移自 `Emuera/`）下的历史警告已全局抑制；修改自有源码时应关注新引入的 CA/CS 警告。

`EraCore.Core` / `EraCore.Server` / `EraCore.Cli` / `EraCore.Maui` 均启用 `TreatWarningsAsErrors`（Maui 在 `PublishAot=true` 时经 `WarningsNotAsErrors` 降级 XA1040/IL3053 两条 SDK 信息性警告）。
