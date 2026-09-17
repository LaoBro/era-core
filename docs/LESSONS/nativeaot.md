# NativeAOT 发布 失败教训

> **TL;DR**：AOT 下 JSON 反射是硬禁用（匿名类型必崩）、库项目 minimal API 必须开请求委托生成器、匿名类型不可序列化、ILC 增量产物可能不更新（改代码后清 bin-aot/obj-aot 全量重建）、输出目录重定向必须补 `DefaultItemExcludes`、**Android x86 转译环境（Houdini）下并发加载会触发 GC 标记死循环——真机 ARM64 零复现，别把模拟器当验收环境**、**Android 启动即闪退先查 `NativeAotRuntimeProvider` 是否进了 dex——`_AddStaticResources` 的增量 flag 在运行时形态切换后会跳过生成，清 obj-aot 全量重建**。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 无 VS C++ workload 也能跑 NativeAOT | `IlcUseEnvironmentalTools` + 手动 CppLinker + LIB 环境变量 | 2026-08-07 |
| 2 | NativeAOT 三座大山 | JSON 反射禁用 / RequestDelegateGenerator / 匿名类型序列化 | 2026-08-07 |
| 3 | ILC 增量发布产物不更新 | 清 bin-aot/obj-aot 全量重建；grep 二进制用 UTF-16LE | 2026-08-07 |
| 4 | 消 ILC 警告不能只换序列化入口 | 显式 context 丢 converter 多态；用 JsonTypeInfo 重载 | 2026-08-07 |
| 5 | 壳层反射 JSON 是 NativeAOT 白屏根因 | 进程存活的白屏 = 托管异常被吞；IL3050 运行时是 JsonSerializerIsReflectionDisabled | 2026-08-08 |
| 6 | 目录隔离的 CS0579 根治 | 重定向 Base*OutputPath 后必须补 DefaultItemExcludes | 2026-08-08 |
| 7 | NativeAOT 警告治理分层法 | 代码级修复优先、豁免带证据；DAM 对字典/数组取值 Type 无效；csc 与 ILC 报码不同 | 2026-08-10 |
| 8 | Android 转译环境下并发加载 GC 死循环 | Houdini 转译特有（真机 ARM64 零复现）；转译检测不能用 `uname`；符号化走 `.dbg` 而非 `.map` | 2026-09-15 |
| 9 | 启动即闪退 = dex 缺 NativeAotRuntimeProvider | `_AddStaticResources` 增量 flag 跳过生成；manifest 声明了类但 dex 没有；清 obj-aot 重建 | 2026-09-17 |

---

## 1. 无 VS C++ workload 也能跑 win-x64 NativeAOT——IlcUseEnvironmentalTools + 手动 CppLinker

**场景**：3.2 引擎 NativeAOT 验证（`nativeaot-verify` 分支）。本机无 VS C++ 桌面 workload：VS 18 只装了 MSVC bin（lib 只有 onecore 子集），Windows Kits 无 Lib，vswhere 也认不出 VS 18 的 VC.Tools 组件（findvcvarsall 报 "Platform linker not found"）。

**解决**（全部写死，避免重查）：
1. 装 Windows SDK 库：`winget install Microsoft.WindowsSDK.10.0.26100`（提供 um/x64 + ucrt/x64 的 kernel32.lib/ucrt.lib）。
2. `-p:IlcUseEnvironmentalTools=true` 跳过 findvcvarsall（_VCVarsAllFound 为空 → 不再触发 linker-not-found error）。
3. `-p:CppLinker="...\MSVC\14.51.36231\bin\Hostx64\x64\link.exe"` + `-p:CppLibCreator=...\lib.exe`（Windows 路径，Git Bash 的 /c/... 风格 link.exe 不认）。
4. `LIB` 环境变量 = MSVC `lib/onecore/x64` + Windows SDK `um/x64` + `ucrt/x64`。

**教训**：
- **lld-link 不是 link.exe 的 drop-in**：不认 `/NOEXP`、`/SOURCELINK` 语义不同，`could not open '/NOEXP'` 直接失败——Windows NativeAOT 链接直接用 MSVC `link.exe`，别在 lld 上浪费时间。
- 微软文档写的"需要 Desktop Development for C++ workload"是充分条件不是必要条件；`onecore/x64` 的 CRT 库（libcmt/libcpmt/oldnames）+ link.exe 就够。
- 排查顺序：先确认 `vswhere -requires VC.Tools.x86.x64` 是否命中——组件注册缺失时，任何"装 VS"的自动化都不如手动 CppLinker 快。

## 2. NativeAOT 三座大山：JSON 反射禁用、minimal API 委托生成器、匿名类型序列化

**场景**：AOT 发布成功（ILC 链接通过）但 server 模式启动即崩/全端点 500。三个问题层层叠叠，每个都是"编译通过、运行炸"。

**教训一（System.Text.Json 反射是硬禁用）**：AOT 下 `JsonSerializer.Deserialize<JSONConfigData>(json)`（无 options/context）直接抛 `JsonSerializerIsReflectionDisabled`——不是慢，是**崩**。所有 JSON 路径必须源生成 context（`[JsonSerializable]` + `JsonSerializerContext`）；`TurnJsonOptions`/`JsonOpts` 这类 options 加 `TypeInfoResolver = context` 即可让既有 `Serialize(value, options)` 调用零改动走源生成（converter 递归 `Serialize((object)value, options)` 经 resolver 按运行时类型解析——前提是 context 显式注册了全部多态叶子类型）。

**教训二（库项目的 Map* 必须开请求委托生成器）**：minimal API 的 `MapGet/MapPost` 在 `PublishAot=true` 时 SDK 只对 **web 项目**自动启用静态委托生成；**库项目（如 Emuera.Headless.Server）必须显式 `<EnableRequestDelegateGenerator>true</EnableRequestDelegateGenerator>`**，否则 AOT 下 RequestDelegateFactory 走反射路径，**所有端点 500（连 404 都 500 是强烈信号）**。顺带加 `<IsAotCompatible>true</IsAotCompatible>` 激活分析器。

**教训三（匿名类型在 AOT 下不可序列化）**：`Results.Json(new { ... })` 响应全部要替换——统一改 `JsonObject`（JsonNode 内建 converter）+ `ConfigureHttpJsonOptions(TypeInfoResolverChain.Insert(0, context))` 让 Results.Json 的默认 options 能解析 JsonObject。**别用 `Results.Json(value, jsonTypeInfo)` 显式注入 TypeInfo**：context 的源生成 options 无 encoder 属性（.NET 10），TypeInfo 走默认 encoder 会把 `'` 转义成 `\u0027` 破坏 wire；走 HttpJsonOptions（默认 UnsafeRelaxedJsonEscaping）才与托管时代逐字节一致。wire 回归靠 xUnit 端点断言抓（`Assert.Contains("Missing 'value' field", body)` 直接命中 \u0027）。

**教训四（AOT 下 Kestrel 异常无处可查）**：500 无 body 无日志时，先在管道最前加全局 try-catch 中间件 `Console.Error.WriteLine(ex)`——本次正是靠它抓到 `JsonTypeInfo metadata for type 'JsonObject' was not provided` 才定位到 resolver 问题。排查完移除。

**教训五（托管/AOT 对照定位法）**：同一请求托管 200 / AOT 500 = AOT 特有；全端点共同失败（含 404）→ 管道/路由层；单端点失败 → handler 内。逐层缩小比瞎猜快得多。

## 3. ILC 增量发布产物不更新——改代码后必须清 bin-aot/obj-aot 全量重建

**场景**：修完代码重跑 `dotnet publish`（同 -o 目录）成功，但复现时行为没变；`grep -c "新字符串" exe` 返回 0，以为代码没进去。

**真相**：两个陷阱叠加——
1. **ILC 增量缓存**：obj-aot 下的 ILC 中间产物（native/*.obj 等）在输入 dll 时间戳判断下可能复用旧编译，exe 不含新代码。`dotnet clean` 清不掉自定义 BaseOutputPath（bin-aot/obj-aot），要手动删目录强制全量。
2. **grep 二进制搜不到 .NET 字符串**：程序集字符串字面量是 **UTF-16LE** 存储，`grep "UNEXPECTED"`（ASCII）永远 0——用 `python` 搜 `'UNEXPECTED'.encode('utf-16-le')` 才准。**先确认代码真在产物里，再开始排查运行时行为**。

**教训**：AOT 验证期改代码 → publish → 复现，若现象不变，先查"产物是否真的新"（二进制搜新字符串 UTF-16 + 时间戳 + 清目录全量重建），别在旧产物上反复烧时间。

## 4. 消 ILC 警告不能只换序列化入口——显式 context 会丢 converter 多态

**场景**：`test_snapshot.py` 稳定 1 failed：turn diff 序列化成 `{"lineOps":[{"type":"append"}]}` 空壳（`AppendLinesOp.newLines` 全丢）。托管/AOT/stash 后均复现；xUnit 685 全绿但未覆盖该路径，成了漏网之鱼。

**原因**：为消 IL2026/IL3050，把三处 `Serialize(turn, TurnJsonOptions)` 改成显式 `EmueraJsonContext.Default.TurnRecord`。但 context 的 `JsonSourceGenerationOptions` **不含 Converters**——`LineOpConverter`/`TurnOpConverter`（按运行时类型装箱 Write 的多态序列化）不参与 → 抽象基类 `LineOp` 直接序列化只输出 type 鉴别符，子类字段全丢。`TurnJsonOptions`（带 Converters + TypeInfoResolver）才是正确形态。

**教训**：
1. **源生成 context 序列化抽象基类/接口成员时不会自动多态 dispatch**——多态必须靠 `[JsonPolymorphic]`（会引入 `$type` 字段破坏 wire）或自定义 converter（挂在 options.Converters）。换序列化入口（context TypeInfo vs options）会静默改变 wire，务必有内容级断言兜底。
2. 消 `Serialize(T, JsonSerializerOptions)` 警告的正解是 **JsonTypeInfo 重载**：`Serialize(turn, (JsonTypeInfo<TurnRecord>)TurnJsonOptions.GetTypeInfo(typeof(TurnRecord)))`——`GetTypeInfo(Type)` 与 TypeInfo 重载均无 RUC/RDC 标注（反射验证），converter 链完整保留，wire 逐字节一致。别用"换 context"这种看似等价的手段。
3. **e2e 的文本内容断言能抓到单元测试漏掉的 wire 回归**——xUnit 685 绿 ≠ diff 内容正确；test_snapshot 的 diff 内容断言是这条防线的最后一道。

## 5. 壳层反射 JSON 是 NativeAOT 白屏根因，且异常传播缺陷掩盖了崩溃

**场景**：生产 `Emuera.Maui` 开 `PublishAot=true` 构建成功（26.7MB 纯 NativeAOT），但真机/模拟器**白屏**。实验项目（最小壳）NativeAOT 正常——差异在壳层代码。

**取证**（MuMu x64 + logcat）：
- 进程存活（`pidof` 有值）但 `dumpsys activity top` 无 WebView 视图行；`edge://inspect` 看不到条目。
- logcat 抓到 **`FATAL EXCEPTION: System.InvalidOperationException: JsonSerializerIsReflectionDisabled`**，堆栈：`BridgeHost.HandleScanGames` → `JsonSerializer.Serialize(new {...})`（匿名类型反射序列化）→ JNI 回调 `BridgeClient.ShouldOverrideUrlLoading`。
- 前端 `scanGames` 已正常到达 C#（bridge 链路好的），崩溃发生在 C# 侧回包序列化。

**原因**（两层叠加）：
1. **NativeAOT 禁用反射序列化**：`JsonSerializer.Serialize(匿名类型)` 抛 `JsonSerializerIsReflectionDisabled`（IL3050 警告的运行时形态）。壳层 BridgeHost 有 18 处匿名类型，Core 层已治理过（EmueraJsonContext），壳层漏了。
2. **NativeAOT 异常传播缺陷掩盖崩溃**：异常从 WebView JNI 回调抛出时，`mono.android.Runtime.propagateUncaughtException` 的 JNI 导出在 NativeAOT 下**不存在**（logcat: `No implementation found for ... propagateUncaughtException`）→ 异常无法正常终止进程/打印崩溃框 → **UI 线程挂起** → 表现是"白屏+进程存活"，而不是崩溃退出。这就是"pidof 有值 + 无 crash 日志 + 无 console 报错"矛盾的根因。

**修复**：
1. 新增 `Emuera.Maui/Json/MauiJsonContext.cs`：全部壳层消息改具名 record + `[JsonSourceGenerationOptions(PropertyNamingPolicy = CamelCase)]` 源生成上下文，wire 与匿名类型逐字节一致（camelCase 字段名、null 仍写出）。
2. `BridgeHost.cs` 15 处 + `MainPage.xaml.cs` 1 处匿名类型 → `MauiJsonContext.Default.Xxx`。TurnRecord 不动（走 Core `TurnJsonOptions`，已挂 EmueraJsonContext）。
3. 验证：0 FATAL，进入游戏选择界面。

**教训**：
1. **NativeAOT 排查白屏，先查"进程是否存活"**：存活=托管层异常被吞/挂起（JNI 回调内异常 + propagateUncaughtException 缺失），崩溃退出=Java 层/链接问题。logcat 搜 `FATAL EXCEPTION` 能直接定位。
2. **"构建成功 ≠ 可运行"**：NativeAOT 的 IL 警告（IL2026/IL3050）是运行时崩溃的预告——**IL3050 的运行时形态就是 `JsonSerializerIsReflectionDisabled` 抛异常**。凡是 `JsonSerializer.Serialize(new {...})` 匿名类型/未注册类型，AOT 下必崩，代码评审就要拦。
3. **实验项目验证通过 ≠ 生产项目可过**：最小壳无桥接消息/无复杂 JSON，NativeAOT 差异全在壳层业务代码。门控验证必须用真实生产路径（scanGames → HandleScanGames → gamesScanned 回包）。
4. 壳层 JSON 与 Core 同模式治理：新增消息类型必须走 `MauiJsonContext`（壳层）/`EmueraJsonContext`（Core），禁止匿名类型。

## 6. NativeAOT 目录隔离的 CS0579 根治——重定向后必须补 DefaultItemExcludes

**场景**：`Emuera.Maui/Directory.Build.props` 在 `PublishAot=true` 时重定向 `BaseOutputPath=bin-aot\`、`BaseIntermediateOutputPath=obj-aot\`。构建报连环 `CS0579 特性重复`（AssemblyInfo/.NETCoreApp/TargetPlatform 等），且**构建期间并发清理会变成文件锁 Access denied**。

**原因**：SDK 默认 `DefaultItemExcludes` 含 `$(BaseIntermediateOutputPath)/**`——重定向后变成 `obj-aot/**`，**旧的 `obj/` 目录不再被排除**，其中残留的普通构建生成物（`obj/Debug/net10.0-windows.../App.g.cs`、`obj/Release/.../AssemblyInfo.cs` 等）全部回流进编译 glob，与新目录生成物重复 → CS0579/MAUIX2000/CS0234 连环报错。命令行全局传 `-p:BaseIntermediateOutputPath` 还会传染 ProjectReference（Core）同病。

**修复**：`Directory.Build.props` 的 PublishAot 分支显式补回排除：
```xml
<DefaultItemExcludes>$(DefaultItemExcludes);obj/**;bin/**</DefaultItemExcludes>
```
一次性根治，之后 AOT 构建无需每次手动清 `obj/`。

**教训**：
1. **重定向 `Base*OutputPath` 后必须同步补 `DefaultItemExcludes`**，否则 SDK 对旧目录的默认排除失效——这是"输出目录重定向"方案的必配项，不是可选项。
2. **命令行全局传 `Base*OutputPath` 是毒药**：全局属性传染整个项目图（ProjectReference 全中招）。目录级隔离必须放 `Directory.Build.props`（仅本目录项目生效），并保留"构建前清残留"的兜底。
3. **构建期间不要并发清理中间产物**：`dotnet publish` 运行中删除 `obj/` 下文件会与构建写文件竞争 → `UnauthorizedAccessException: Access to the path ... denied`（MSB3491）。等构建完全结束（进程退干净）再清理，或依赖 DefaultItemExcludes 根治后不再需要清理。
4. 此类"中间文件污染"的排障顺序：先看错误是编译期（CS0579 重复源）还是文件系统（Access denied 锁），前者清残留+修排除，后者查并发/残留进程。

## 7. NativeAOT 警告治理分层法——代码级修复优先、豁免带证据、csc/ILC 报码不同

**场景**：给 `Emuera.Maui` 开质量护栏（`TreatWarningsAsErrors=true`）后，NativeAOT 构建（`PublishAot=true`）报 30+3 error：Core 层 DataTable XML 序列化（IL2026/IL3050）、TurnRecord converter 装箱递归（IL2026/IL3050）、插件反射加载（IL2026/IL2072）、Lang 特性扫描（IL2070/IL2075）、列类型解析（IL2072），壳层 1 处 options 重载序列化（IL2026/IL3050），外加程序集级 IL3053 与官方 XA1040。

**治理顺序（先根治后豁免）**：
1. **能代码级修复的不豁免**：`TurnOpConverter.Write` 的装箱反射 `Serialize((object)value, options)` → `options.GetTypeInfo(value.GetType())` 非泛型重载（无 RUC/RDC 标注，context 已注册全部多态叶子，wire 不变）。
2. **[JsonConverter] 特性挂基类 = 教训 4 的补全正解**：`ShowError` 用 options 重载（依赖 Converters 多态），不能直接换显式 context（教训 4 已踩过——丢 converter）。正解是给抽象基类 `TurnOp`/`LineOp` 挂 `[JsonConverter(typeof(TurnOpConverter))]`——source-gen 生成 TypeInfo 时自动采用类型上的自定义 converter，此后显式 `EmueraJsonContext.Default.TurnRecord` 与 options 路径 wire 逐字节一致（协议回归全绿验证）。
3. **DAM 返回注解对"数组/字典取值的 Type"不适用**：想用 `[return: DynamicallyAccessedMembers]` 消除 `DataColumnCollection.Add(string, Type)` 的 IL2072，但 `IntToType` 返回 `builtInDTTypes[i-1]`、`NameToType` 返回字典值——**ILC 报 IL2063「返回值无法静态确定满足 DAM」**。`Dictionary<string, Type>` 元素不支持 DAM 注解，此路不通 → 撤销返回注解，改在**唯一调用点**（`Creator.Method.cs` 列类型解析方法）豁免 IL2072。
4. **csc 与 ILC 对同一反射点报不同警告码**：`Lang.queryManagedClass` 的 `GetProperties()` 反射，csc 编译期报 **IL2075**，ILC 链接期报 **IL2065**（"值无法静态确定"）——豁免时两个码都要覆盖，否则编译过、链接炸。
5. **程序集级汇总与官方提示放行**：`Mono.Android` 绑定库固有 `IL3053`（程序集 AOT 汇总，非自有代码）与 `XA1040`（Android NativeAOT 官方实验性提示）→ `WarningsNotAsErrors`（仅 `PublishAot=true` 条件），**自有代码的具体码（IL2026/IL3050/IL207x）仍按 error 拦截**——放行汇总码不放开具体码。
6. **豁免必须带 Justification**：每个 `[UnconditionalSuppressMessage]` 附理由（实测证据 test_datatable_aot.py 13/13 / 路径禁用前提 / 运行时经 resolver 无反射），不静默 suppress；`EraBinaryDataWriter/Reader`、`Creator.Method` 等 DataTable 系列引用同一实测证据链。

**教训**：
1. **治理顺序 = 能修则修、豁免是最后手段**：`GetTypeInfo`/`[JsonConverter]` 属根治（无 warning 无 suppress）；DAM 注解看似根治实则对数据源取值无效（IL2063），白绕一圈——**先看数据源形态（typeof 常量 vs 数组/字典取值）再决定 DAM 还是豁免**。
2. **csc 与 ILC 警告码可能不同**：同一反射点编译期 IL2075、链接期 IL2065。豁免列表要同时覆盖两个阶段的码，验证要等 ILC 链接过才算闭环（编译过 ≠ 链接过）。
3. **放行"程序集级汇总码"（IL3053）要谨慎**：只对绑定库固有（Mono.Android）放行；自有代码的 IL3053 意味着还有具体警告没清，放行会掩盖。
4. **带 converter 多态的序列化入口替换**：显式 context 会丢 options.Converters（教训 4）；`[JsonConverter]` 挂基类让两种入口行为一致，是比"换入口"更根本的解法——**改序列化后必须跑协议级内容断言回归**（run_all.py 的 test_jsonl/test_snapshot），单测全绿不代表 wire 正确。

## 8. Android x86 转译环境（Houdini）下并发加载触发 GC 标记死循环——真机 ARM64 零复现

**场景**：`EraCore.Maui` NativeAOT（`PublishAot=true`、`android-arm64`）在 MuMu 12（x86_64 宿主 + Houdini 转译 arm64-v8a）上冷启动 TK1.29.3 纯净版（2388 个 ERB），**间歇性失败且形态不定**：

| 形态 | 现场 |
|---|---|
| Access Violation | `IrBlob.ToArray` 遍历 `List<IrLine>`；NativeAOT 不支持抛 `AccessViolationException` → `FailFast` → `SystemNative_Abort` → **SIGABRT** |
| Access Violation | `String.InternalSubString`（`Preload` 并行读盘循环体内） |
| 卡死 | `WKS::gc_heap` 标记阶段死循环：进程存活、单线程 100% 烧 CPU（14,911 ticks vs 次高 143）、无任何崩溃日志 |

**变量归因（逐个排除后收敛）**：

| 变量 | 实验 | 结果 |
|---|---|---|
| IR 磁盘缓存 | 关 `IrDiskCacheLoad` | 仍失败 |
| ERB 并行 genmerge | 关 `ParallelErbLoad` | 崩溃转移到 `Preload` 循环体（3 轮 1 次） |
| preload 并发度 | `safReadDegree=1 / 2` | 与 ERB 并行同开时仍卡死 |
| 两条并行都收窄 | ERB off + degree ≤ 2 | 18/18 通过（**实验锚点，不是可发布方案**） |
| 真机 ARM64 | 默认并行配置 | **10/10 通过，历史零崩零卡** |
| 文件系统位置 | 游戏从宿主共享挂载 `/mnt/shared` 换到内部存储 `/storage/emulated/0` | **仍卡死，且冻结栈逐帧相同** |

⇒ 唯一剩下的相关变量 = **ARM64 代码跑在 x86 转译层上**。

**取证要点**（Houdini 转储自带 `pc` + build-id，且 `pc` 零抖动）：

```
#00  WKS::gc_heap::mark_object_simple1   +0x6b8      ← 719 次转储恒定于此
#01  WKS::gc_heap::drain_mark_queue
#02  WKS::gc_heap::mark_phase → gc1 → garbage_collect → GarbageCollectGeneration
#08  GCHeap::Alloc → RhpGcAlloc → RhpNewObject
#11  EraStreamReader.ReadEnabledLine                  ← 托管：正在读行并分配
#12  ErbLoader.GenerateErbFileCore
#14  RunGenerateWorkerAsync_d__57.MoveNext            ← ERB 并行 worker
```

`debuggerd -b` 同线程原生栈：`art::SignalChain::Handler` + `__restore_rt` **嵌套在 Houdini 帧之间**，栈顶 8 帧**无一落在自有 `.so`** ⇒ 反复「信号 → 处理 → 返回 → 立即再故障」。
（`WKS` = Workstation GC，即分配线程自己执行的阻塞式 GC。）

**代码审计结论（先审计、再归因）**：两条并行路径均**无应用层共享可写状态**——`Preload` 循环体只写两个 `ConcurrentDictionary`；`FileSystemGameDirAccessor.NameMaps` 的内层普通 `Dictionary` 只在 `GetOrAdd` 工厂内构造、发布后只读；`ResolveExactMiss` 因路径来自磁盘枚举而 `File.Exists` 恒真、直接短路；`SafPath` 纯函数；`EncodingHandler` 的共享静态 `Encoding` 每次 `GetString` 内部新建 decoder（线程安全）；`AgentLog` 全部写入在 `lock` 内；全仓无 `CollectionsMarshal`。⇒ 归因推到运行时层（WKS GC × 翻译态线程挂起），不是自有竞态。

**注意**：触发剂量**不是线程数**——`deg=2`（全局 4 并发，双树 `WhenAll`）通过，而 ERB `window=4`（同为全局 4 并发）必卡死。更像「单位时间内处于翻译态的托管执行量」。

**现状**：截至 2026-09-15 未修（属运行时层，非本仓可改）。可发布的绕行 = 转译环境检测 → 仅在该环境强制 preload 串行 + 关闭 ERB 并行；真机并行不受影响。

**教训**：

1. **真机才是判据，模拟器不是验收环境。** 同一 APK 同一配置：真机 ARM64 10/10 通过且历史零崩溃，x86 转译模拟器必崩必卡。并发/内存类缺陷先在真机定性，别拿模拟器结论当发布门。
2. **转译检测不能用 `uname`——MuMu 会把它伪造成 `aarch64`。** 可靠判据：`Build.SUPPORTED_ABIS[0] != "arm64-v8a"` 且 `RuntimeInformation.ProcessArchitecture == Arm64`。实测对照：`ro.product.cpu.abi` = `x86_64`（真机 `arm64-v8a`）、`abilist` = `x86_64,arm64-v8a,x86`、`/system/lib64/libhoudini.so` 存在（真机不存在）。`RuntimeInformation.OSArchitecture` 同理不可信（它读 `uname`）。
3. **符号化优先找 `.dbg`，不要走 `.map`。** `IlcGenerateMapFile` 产出的 `*.map.xml` 是**对象节点尺寸清单、全文件零地址**（实测 87 MB）——**完全无法做 pc 符号化**。真正可用的是 `.gnu_debuglink` 指向的 `EraCore.Maui.so.dbg`（37 万符号，段地址已是最终 vaddr，build-id 与 `.so` 一致）。ILC 的 `.o` 可作备用但需叠加段偏移。
4. **Houdini 卡死的取证路径与普通崩溃不同。** 转储以 ~30 次/秒刷屏，256 KB logcat 主缓冲 **23 秒即回绕**，业务日志（`preload_policy` / `genmerge`）会被整段挤掉——但**不影响办案**：转储自带 `pc` + `build-id`，`pc` 零抖动即可直接定位到函数级。第一优先是 `logcat -d`（**不清缓冲**）抓转储 + `debuggerd -b` 看原生栈，而不是找业务日志。**要跑多轮取证，先开 logcat 常驻重定向再启动应用，不要事后补。**
5. **「并发必炸」不等于「我们代码有 race」——先逐点审计共享可写状态。** 把两条并行路径的每个共享点查完（`ConcurrentDictionary` 是否真安全、日志汇聚点是否加锁、有无无边界检查的数组访问、写入字典是否只在工厂内构造），才能把结论推到运行时层；否则容易在自有代码里空转几天。
6. **「补锁后症状消失」必须核门控可达性。** 真机上曾出现的间歇 NRE 由 `userDefinedStaticVarList` 并发追加撕裂所致（已补 `_staticVarListLock`，`447cf03`），但该锁只在 `RunUncalledSweepParallel` 可达，而 `ShouldRunParallelSweep` 的 Auto 分支是 `configured && !analysisMode && !isAndroid` ⇒ **Android + Auto 下该路径恒不运行，这把锁够不着**；真机上它只在 `sweepGatePolicy=ForceOn` 覆盖时才可能生效。**它与模拟器的 GC 崩溃是两个不同层次的问题，不可混为一谈。**

## 9. Android NativeAOT 启动即闪退 = dex 缺 `NativeAotRuntimeProvider`——增量 flag 不认运行时形态切换

**场景**：`EraCore.Maui` Android NativeAOT APK 装到真机，一打开立刻退回桌面（启动即崩）。此前刚改过前端（目录选择器卷切换），第一反应容易怀疑前端改动——但**崩溃与前端无关**。

**取证**（先排除前端，再抓崩溃栈）：
1. 确认 APK 确实带最新前端：从 APK 解出 `assets/wwwroot/static/index-*.js`，搜本次改动的标记字符串（中文文案）命中 → 前端产物没问题；`check-legacy-syntax.mjs`（chrome61 语法护栏）也通过。
2. `adb logcat -d *:E` 抓到崩溃栈：
   ```
   FATAL EXCEPTION: main
   java.lang.RuntimeException: Unable to get provider net.dot.jni.nativeaot.NativeAotRuntimeProvider:
     java.lang.ClassNotFoundException: Didn't find class "net.dot.jni.nativeaot.NativeAotRuntimeProvider"
   ```
   进程死在 `ActivityThread.installContentProviders`（`handleBindApplication` 阶段）——**Activity 还没创建、WebView/JS 一行都没跑**。这是打包层问题，不是运行时/前端问题。
3. 核对 dex：把坏 APK 的 `classes.dex`/`classes2.dex` 读出来按 ASCII 搜 `NativeAotRuntimeProvider` → **两个都没有**；对照已知可用的实验工程 APK，其 `classes2.dex` 里**有**这个类。

**原因**：这个 Java 类由 workload 的 `_AddStaticResources` target 生成（`Xamarin.Android.Common.targets`，`CopyResource ResourceName="NativeAotRuntimeProvider.java"`，条件 `_AndroidRuntime == 'NativeAOT'`）。但该 target 是**增量的**——`Outputs="$(_AndroidStaticResourcesFlag)"`（即 `obj-aot/.../static.flag`），flag 比输入新就整个跳过。取证时间线：`obj-aot` 里同目录的 `JavaInteropRuntime.java`/`NativeAotEnvironmentVars.java` 是 9/4 生成的，唯独 `NativeAotRuntimeProvider.java` 从未存在，而 `static.flag` 9/4 已被 touch——9/17 的 NativeAOT 构建判定 up-to-date 直接跳过 → **manifest 声明了 provider，dex 却永远缺这个类**。根源是 9/4 那次 `_AddStaticResources` 跑的时候运行时形态不是 NativeAOT（没生成此文件），flag 却照样被 touch；**增量 flag 不认运行时形态（Mono / CoreCLR / NativeAOT）切换**。

**修复**：删 `EraCore.Maui/obj-aot` + `bin-aot` 全量重建 → 新 APK `classes2.dex` 含该类 → 装机启动正常（`NativeAotRuntimeProvider()` → `JavaInteropRuntime.init()` → WebView 配置 → bridge 注册 → 加载前端，全链路通）。

**教训**：
1. **Android「启动即闪退」先读崩溃栈再定方向**：栈是 `Unable to get provider ... ClassNotFoundException` = dex 缺类，进程死在 ContentProvider 安装阶段，WebView/JS 没跑——别先怀疑前端或业务代码。反之「进程存活的白屏」才是托管异常被吞（教训 5）。两者判据相反，别混。
2. **取证第一步查 dex 内容**：从 APK 读出 `classes*.dex` 按 ASCII 搜缺失类名（类名是 ASCII，PowerShell `[Text.Encoding]::ASCII.GetString` 后 `.Contains()` 即可），并对照一个已知可用的 APK。比猜快得多。
3. **切换 Android 运行时形态（尤其 `PublishAot` 开/关）必须清 `obj-aot`（乃至 `obj`）全量重建**：`_AddStaticResources` 的 `static.flag` 对 Mono/CoreCLR/NativeAOT 共用，一种形态构建后 touch 了 flag，换形态再构建会被增量判定跳过 → manifest 声明的类没进 dex。这与教训 3（ILC 增量缓存）同源：**增量缓存陷阱不只在 ILC 原生产物，也在 Java 源文件生成**——凡是「改构建形态但复用中间目录」，先怀疑陈旧增量状态。
4. 白屏分析文档（`maui-nativeaot-whitescreen-analysis-2026.8.8.md` §2.2）早已写明「前置条件：构建前清理旧 obj-aot/ 残留」，但没固化进发布脚本，本次正是漏清踩中——**已知前置条件要变成脚本默认行为，不能靠人记**（可让 `Publish-MauiAndroidAot` 默认清 `obj-aot`，或 `-Clean` 连带清它）。
