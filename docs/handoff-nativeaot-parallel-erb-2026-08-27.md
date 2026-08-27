# NativeAOT Android `ParallelErbLoad` 卡死交接

- 日期：2026-08-27
- 工作区：`D:\LaoBro\era-core`
- 分支状态：`main...era-core/main [ahead 46]`，调查期间工作树保持干净
- 文档目的：为后续修复 Android NativeAOT 游戏加载卡死提供事实、代码入口和复现路径

## 当前结论

用户已确认发布使用 `PublishAot=true`。同一个 Android NativeAOT APK、同一个游戏进行 A/B 测试时：

- `ParallelErbLoad=off`：游戏可以成功加载
- `ParallelErbLoad=on`：可能把 UI 一起卡住，或直接闪退
- 关闭懒加载和 IR 不能解决问题
- 普通构建可以正常进入游戏

因此当前最高置信度判断是：**问题直接关联 2026-08-24 提交 `37bfeec` 引入并默认开启的 ERB 并行生成/合并路径。NativeAOT/Android 更容易暴露该路径的竞态、尾任务异常和内存峰值，但不能简单归结为 NativeAOT 不支持多线程。**

`f940096` 主要新增 MAUI Preferences 三态性能覆盖，使该 A/B 开关可从设置页控制；它不是并行加载实现本身。`26d6fac` 把 ERB/CSV 预加载改成并行，是可能放大 Android SAF 压力的前置因素。

## 加载链和关键开关

MAUI 进入游戏的大致顺序：

```text
MainPage.OnReloadGame
  -> GamePaths.Resolve / Validate / SAF write probe
  -> EmueraRuntimeInitializer.Initialize
  -> ApplyPerformanceOverrides
  -> RecreateHost / BridgeHost.Start
  -> Session.GameLoopAsync
  -> ConsoleStateManager.Initialize
  -> Preload.LoadTreesAsync
  -> Process.Initialize
  -> ErbLoader.LoadErbDir
```

关键代码：

- MAUI reload：`EraCore.Maui/MainPage.xaml.cs:242-333`
- 性能覆盖：`EraCore.Maui/BridgeHost.cs:149-165`
- 配置项：`EraCore.Core/Shared/Runtime/Config/ConfigData.cs:108-125`
- ERB 分支：`EraCore.Core/Shared/Runtime/Script/Loader/ErbLoader.cs:202-217`
- 串行 fallback：`ErbLoader.cs:1019-1028`
- 并行调度：`ErbLoader.cs:1039-1082`

`ParallelErbLoad` 只控制 ERB 生成/合并阶段：

```csharp
if (Config.Config.ParallelErbLoad && !env.AnalysisMode)
    await LoadPendingParallel(...);
else
    await LoadPendingSerial(...);
```

`ParallelParseScript` 是另一个独立开关，控制后续未调用函数遍历（`ErbLoader.cs:1421-1467, 1676-1726`），不能用它替代 `ParallelErbLoad` 的 A/B 结论。

## 直接相关提交

### `37bfeec`：首要回归来源

提交标题：`新增(erb-loader): ERB 并行解析 + 按原文件顺序合并 ... 默认并行、串行 fallback 开关`

它引入了：

- 所有 ERB 文件都创建 `Task.Run`
- `SemaphoreSlim` 以最多 8 个任务限制生成阶段
- 生成结果放到 `arts[]`
- 主循环再按文件顺序等待并合并
- 通过占位警告延后跨文件重名和 `#ONLY` 检查
- 将 `scaningLine` 和警告缓冲部分改成 `AsyncLocal`

此提交还把默认 `ParallelErbLoad` 设为 `true`。用户的开关 A/B 正好切换该提交新增的主路径。

### `26d6fac`：前置并发放大器

该提交将 `Preload` 的 ERB/CSV 两棵目录树改成 `Task.WhenAll`；每棵树的文件读取又使用 `Task.Run` + `Parallel.ForEach`：

- `EraCore.Core/Shared/Runtime/Utils/Preload.cs:44-75`
- `Preload.cs:131-137`
- `ConsoleStateManager.cs:61-64`

Android SAF 的 `SafGameDirAccessor.OpenRead` 会将每个 content URI 全量复制到 `MemoryStream`（`EraCore.Maui/Services/SafGameDirAccessor.cs:332-378`）。因此真实 Android 加载可能同时存在：预加载双树并发、每棵树文件并发，以及 ERB 生成并发。

这能增加 Binder、线程池、内存和 GC 压力，但用户“仅关闭 `ParallelErbLoad` 就恢复”的结果仍更直接指向 `LoadPendingParallel`。

### `f940096`：开关接入，不是并行实现

它在 `MainPage.OnReloadGame` 的初始化完成后、Session 创建前应用四个 MAUI 性能覆盖（`MainPage.xaml.cs:290-297`）。当前局即时切换不改变已加载的 ERB，必须重新进入游戏。

代码仍有一个独立缺陷：如果游戏的 `csv/_fixed.config` 固定某项，`ConfigItem.Value` 在 `Fixed=true` 时直接返回（`ConfigItem.cs:62-70`），`ApplyPerformanceOverrides` 不检查实际写入结果。当前用户的 A/B 已说明目标包中 `ParallelErbLoad` 的 off 至少产生了有效行为，但后续修复仍应记录实际配置值和 `Fixed` 状态。

`fb26005` 主要处理 NativeAOT JSON source generation/警告；`8258563`、`953d027`、`85b6cba` 主要是构建、测试和可空性调整。它们没有直接改动 ERB 并行调度。

## 最高优先级代码风险

### P0：异常后尾任务未收束，`SemaphoreSlim` 可能先被 Dispose

位置：`EraCore.Core/Shared/Runtime/Script/Loader/ErbLoader.cs:1039-1082`。

当前流程：

1. 一次性为全部 pending 文件创建 `Task.Run`。
2. 任务等待 `gate`，生成 `ErbFileArtifact`，最后 `gate.Release()`。
3. 主循环按索引 `await tasks[i]`。
4. 如果较早索引的任务记录了异常，主循环立即 `throw errors[i]`。
5. 方法离开，`using var gate` Dispose，但更高索引任务可能仍在等待或继续生成。
6. 尾任务随后可能在已释放的 `gate` 上 `Release()`，或在等待阶段因 Dispose 失败；这些异常可能无人观察。

尾任务还可能继续访问 `arts`、`errors`、`ParserMediator`、`IdentifierDictionary` 和其他当前游戏状态。串行路径 `LoadPendingSerial` 没有此类后台尾任务。

修复时必须保证：出现首个错误后取消后续工作、等待所有已启动任务结束，再释放 gate 和返回错误；`WaitAsync` 未成功取得 gate 时不能执行 `Release()`。

### P0：共享 `IdentifierDictionary.privateDimList` 的并发竞态

同一个 `IdentifierDictionary` 被注入 `ErbLoader`（`ErbLoader.cs:28-44`），并行 `Task.Run` 通过当前 `GlobalStatic` 访问同一实例。多个 ERB 的 `#DIM/#DIMS` 会走：

```text
LogicalLineParser.cs:283-289
  -> UserDefinedVariable.cs:180
  -> IdentifierDictionary.CheckUserPrivateVarName
```

`IdentifierDictionary.cs:117` 定义：

```csharp
List<string> privateDimList = [];
```

写入在 `IdentifierDictionary.cs:424`，读取在 `IdentifierDictionary.cs:645`。没有锁，也不是并发集合。多个文件同时追加私有变量时可能出现列表损坏、丢项、错误判断或异常。现有并行测试语料主要覆盖跨文件重名、`#ONLY`、进度和等价性，没有覆盖多文件大量 `#DIM/#DIMS`。

最小临时防护可以锁住 Add/Contains，但应先确认顺序语义；更稳妥的长期方案是把生成期私有变量收集变成每文件/每任务状态，在合并或后续阶段统一更新共享字典。

### P0/P1：信号量没有限制完成产物的驻留量

`LoadPendingParallel` 的 `SemaphoreSlim` 只限制同时执行 `GenerateErbFile` 的数量，不限制 `arts[]` 中已完成产物的数量。主循环必须按文件顺序合并；如果第一个文件较慢，后面的文件可以先完成并把完整 `ErbFileArtifact` 留在数组里，驻留量可能接近整个游戏，而不是最多 8 个文件。

关闭 Lazy 后，产物包含更完整的函数体/逻辑行链，内存峰值会更高。NativeAOT Android 上这可能表现为长时间 GC、UI 无响应或进程被系统杀掉。需要改成有界滑动窗口或 bounded producer/consumer，使未合并结果数量也受到限制；合并后及时释放产物引用。

### P1：SAF 和多层并发的资源放大

`SafGameDirAccessor.OpenRead` 对 content URI 同步查询并全量缓冲；`Preload`、ERB 生成和后续解析各自还有并行度。SAF 没有面向整个加载流程的统一并发上限。

这不是纯 NativeAOT 代码问题，但它能解释“打开并行时 UI 也卡住”以及低内存设备上的闪退。后续应考虑独立的 SAF I/O 限流，避免目录扫描和脚本解析同时把 Binder/线程池推满。

## 不能混淆的历史问题

仓库文档中记录过 NativeAOT 扫描游戏阶段的匿名 JSON 反射异常：

- `docs/2026.8.4.安卓性能优化2/maui-nativeaot-whitescreen-analysis-2026.8.8.md`
- `docs/LESSONS/nativeaot.md`

历史症状是 `JsonSerializerIsReflectionDisabled` 后 JNI 异常传播失败，表现为白屏/挂起；相关 JSON source-generation 已有修复。除非当前完整 logcat 再次出现 `JsonSerializerIsReflectionDisabled`、`JavaProxyThrowable` 或 `propagateUncaughtException`，不要把它直接当作本次 `loadGame` 阶段卡死根因。

## 现有测试和覆盖缺口

已有：

- `EraCore.Tests/ErbLoaderTests.cs:325-447`：并行跨文件重名、`#ONLY`、串行等价、进度顺序
- `EraCore.Tests/ErbLoaderTests.cs:450+`：`ParallelParseScript` 相关等价测试
- `EraCore.Tests/b2IrDiskCacheTests.cs`：本地文件 IR 行为
- `EraCore.Tests/LoaderTestHarness.cs:78,142`：固定使用 `FileSystemGameDirAccessor`

缺失：

- 真 Android `SafGameDirAccessor` + `ErbLoader.LoadErbDir` 的集成测试
- 多文件、多 `#DIM/#DIMS` 的并行竞态测试
- 前序文件失败时尾任务取消/等待/信号量释放测试
- 大型 `ErbFileArtifact` 的有界内存测试
- NativeAOT Android 真机完整 `loadGame -> Preload -> ERB -> 首帧` 测试
- `Preferences -> ApplyPerformanceOverrides -> 实际 Config/Fixed` 集成测试

`LoaderTestHarness.InitializeAsync` 会强制 eager（`LoaderTestHarness.cs:81-103`），所以全量测试通过不代表产品默认 lazy/NativeAOT 路径已覆盖。

## 推荐复现矩阵

固定同一 APK、同一游戏目录、同一设备；每次冷启动或完整退出后只点一次游戏，避免 MAUI reload 并发干扰。

1. 四个性能开关全部 `off`，确认能加载。
2. 只打开 `ParallelErbLoad`，其余保持 `off`。
3. 只打开 `ParallelParseScript`，确认它是否独立触发。
4. 两个并行开关都打开。
5. 重复上述步骤时分别比较 `LazyFunctionLoad`/`IrDiskCacheLoad`，但不要在同一轮改变多个变量。

建议使用仓库 `test_game` 与真实大游戏各做一次，并做 SAF 路径和本地文件路径对照。

### 真机日志

同时保存完整、未过滤的：

```text
adb shell getprop ro.build.version.sdk
adb logcat -c
adb logcat -v threadtime > nativeaot-parallel-erb.log
```

卡住时记录：

```text
adb shell pidof <package>
adb shell top -H -p <pid> -n 1
adb shell dumpsys meminfo <pid>
```

搜索：

```text
LoadPendingParallel
phase=genmerge
phase=parsescript
Preload
OpenRead buffered OK
ObjectDisposedException
OutOfMemoryError
FATAL EXCEPTION
SIGABRT
lowmemory
```

阶段判别：

- 只有 `File:Preload:Start`：优先查 SAF/Binder/预加载，不要归因到 ERB 生成
- 有 `Preload:End`，无 `csm Process.Initialize OK`：查 Config/CSV/ERH/ERB
- 有 `LoadPendingParallel`，无 `phase=genmerge`：并行 ERB 任务、尾任务或内存峰值
- 有 `phase=genmerge`/`csm Process.Initialize OK`，无 `Init:End`/首帧：查 ParseScript、初始脚本或 Session/首帧订阅
- 高 CPU、内存持续上升：优先 artifact/eager 解析/GC 或脚本计算
- 低 CPU、SAF 日志停滞：优先 Provider/Binder 阻塞
- `alive=false` 但前端仍 Loading：查隐藏的 GameLoop Task fault

如需临时 instrument，应在 `LoadPendingParallel` 记录每个文件的 idx、任务开始/结束、是否取得 gate、异常和 artifact 大小；并在 `ApplyPerformanceOverrides` 记录四项实际 bool 与 `Fixed` 状态。现有 `PushConfigMessage` 只反映 Preferences 三态，不足以证明引擎值。

## 后续修复建议

按优先级：

1. **建立 A/B 基线**：保持 `ParallelErbLoad=off` 的串行路径作为对照，保留 `on` 供修复过程差分验证；本轮不修改 Android 默认值。
2. **修复调度生命周期**：有界窗口/Channel；链接取消令牌；异常时取消并 `await` 所有已启动任务；所有任务收束后再 Dispose gate。
3. **修复共享状态**：审计生成期所有 `GlobalStatic` 读写；先处理 `privateDimList`，再检查 `ParserMediator`、`scaningLine`、宏/变量表和其他静态缓存。
4. **控制内存**：限制未合并 artifact 数量；合并后清空引用；记录分配量和进程峰值。
5. **处理 SAF 并发**：对 ContentResolver 读/目录枚举设置统一上限，后续再评估是否能避免整文件 MemoryStream。
6. **补回归测试**：优先添加失败尾任务、`#DIM/#DIMS` 多文件和 bounded artifact 测试，再做 Android/NativeAOT 真机 A/B。

本轮不直接删除所有并行，也不切换发布默认；保持串行和并行两种路径可比较，待代码修复与真机门禁完成后再单独决定 Android `auto` 策略。

## 相关资料

- `docs/adr/0019-android-saf-file-access.md`
- `docs/adr/0023-preload-cache-loadwindow-lifecycle.md`
- `docs/adr/0024-parsescript-parallel-uncalled-sweep.md`
- `docs/2026.8.3.安卓性能优化/saf-accel.md`
- `docs/2026.8.4.安卓性能优化2/nativeaot-device-validation-2026.8.8.md`
- `docs/2026.8.4.安卓性能优化2/maui-nativeaot-whitescreen-analysis-2026.8.8.md`

## Suggested skills

后续代理开始修复前建议按需调用：

- `diagnosing-bugs`：继续做 NativeAOT 真机日志和线程/内存判别
- `tdd`：先为并行异常收束、`#DIM/#DIMS` 竞态和有界调度补回归测试
- `implement`：实现 bounded scheduler、取消/尾任务收束和共享状态修复
- `codebase-design`：评估把 ERB 生成期状态从 `GlobalStatic`/共享字典中隔离出去
- `code-review`：修复后检查并发、异常和 NativeAOT 行为回归

---

## 修复方案（实施版）

> 状态：**第一阶段（失败可见切片，§6.2）已实现**（2026-08-27，含红测 `SessionInitFailureTests`）；**§3 调度器重写（erb-bounded-lifecycle）已实现**（有界滑动窗口 + `GenerationResult` + linked CTS + 尾任务收束 + 测试接缝 + §4.1 `privateDimList` 锁 + 判别日志，红测 `ErbLoaderBoundedSchedulerTests` 5 项）；§4.2 共享写审计、§5 SAF 限流、§6.3 剩余观测待实现。本文只定义修复边界、实施顺序和验收门槛，不把未验证的假设当作 NativeAOT 的平台限制。
> 根因排序：P0 任务生命周期未收束；P0 生成期共享状态竞态；P0/P1 未合并 artifact 无上界；P1 SAF 与多层并发放大。历史 JSON 反射问题不纳入本次修复，除非新日志再次出现对应特征。
> 本轮 NativeAOT 仍处于实验性阶段，跳过发布止血，不把 Android 默认关闭作为修复前置条件；保留 on/off 对照用于验证。
> 实施顺序（2026-08-27 审查后修订）：**失败可见切片（E 的前半，原第 5 步）提前为第一阶段**。理由：当前加载期 C# 层异常会让 `_loading` 永远为 true、前端永久 Loading（证据见 6.1）；先修它能让后续真机 A/B 中所有可捕异常以错误回合浮出，把"卡住靠 logcat 考古"变成"直接读错误原因"。它不替代根因修复——线程池饿死型无响应与原生层崩溃（SIGABRT/OOM）没有异常可捕，A/B/C/D 仍按计划执行。

### 方案总览：原理、工作量与代价

以下工作量是 Core/MAUI 代码已有测试基础上的粗估，不含 Android 设备排队和问题反复定位时间。artifact 内存上界与调度器生命周期属于同一批改动，建议一起实施。

| 方案 | 原理 | 工作量（粗估） | 代价/风险 |
|---|---|---:|---|
| A. 有界调度 + 异常收束 | 用固定滑动窗口替代全量 Task/结果数组；按文件序合并；异常或取消后收束全部已启动任务。 | 中高，3-5 人日 | 需要重写 `LoadPendingParallel`，并严格保持异常、warning、进度顺序；窗口过小会损失吞吐。 |
| B. 生成期共享状态隔离 | 先锁住 `privateDimList` 的 Add/Contains；再把其他生成期写入移到 per-file artifact，在合并阶段统一写入。 | 小到大，1-7 人日 | 锁会让极短临界区串行化；状态迁移可能改变旧脚本的顺序或错误语义，必须做串并行等价测试。 |
| C. artifact 内存控制 | 让“运行中 worker 数”和“已完成未合并 artifact 数”共用窗口上限；合并后立即释放临时容器。 | 中，随 A 一起约 0.5-2 人日 | 不能降低单个超大文件的峰值；严格有界后可能减少并行度，换取较低 GC/oom 风险。 |
| D. SAF/Preload 统一限流 | 消除 ERB/CSV 双树与每树文件并行的额度叠加，为 SAF 读取设置全局预算并记录实际并发。 | 中，2-4 人日 | Android SAF 启动可能变慢，需要按设备调优；改变的是 I/O 吞吐和峰值，不直接修复 ERB 竞态。 |
| E. 失败状态与观测闭环 | 让配置实际值、任务阶段、峰值和异常可观测；加载失败时结束 Loading 并阻止旧任务写新 host。 | 小到中，1-2 人日 | 会增加日志和少量状态管理；reload 版本保护可能改变边界时序，但能避免隐藏 fault。 |
| F. 回归与真机门禁 | 用可控失败/延迟 fixture 验证生命周期和顺序，再做本地/SAF、JIT/AOT、真实游戏 A/B。 | 中高，3-6 人日以上 | 主要成本是测试矩阵和实体设备；通过桌面测试不能证明 Android NativeAOT 稳定。 |

推荐优先级：**E 的"失败可见切片"（Session 收束 + 失败语义统一 + Loading 清理红测 + 配置日志）是第一阶段，先于一切根因修复落地**；A + B 是根因修复，C 随 A 一起落地；D 是资源放大治理；F 贯穿各阶段。E 剩余部分（reload 版本保护）保持独立小改动。发布止血本轮不单列为实施步骤。

### 1. 修复目标与不变式

本次修复以 `ParallelErbLoad=on` 在 Android NativeAOT 上可以安全失败、可观测、可回退为第一目标，性能恢复排在稳定性之后。实现必须满足以下不变式：

1. **任务生命周期完整**：任何生成或合并异常都必须取消后续工作，并等待所有已经启动的任务结束；不能在尾任务仍运行时释放同步原语、清理 `GlobalStatic` 或返回失败。
2. **内存有界**：并发生成中的文件数和已经生成但尚未合并的 artifact 数量都不得超过同一个显式窗口上限。不能再按全部 `pending.Count` 创建任务和结果数组。
3. **合并顺序不变**：`LabelDictionary` 写入、警告、控制台回放、`#ONLY` 合并、进度输出和错误处理仍按原文件顺序执行；并行只是生成阶段的执行方式变化。
4. **生成期无共享写**：除明确受保护的兼容状态外，worker 不得写 `LabelDictionary`、全局警告列表、全局扫描行、跨文件列表或 IR writer。所有共享写集中在有序合并阶段。
5. **失败可见且可回退**（第一阶段优先落地，见 §6）：加载失败必须让 `Process.Initialize`/MAUI 会话收到失败结果并结束 Loading 状态；`Session.GameLoopAsync` 对初始化阶段异常整体收束并派发 error turn，不允许 `_gameTask` 静默 fault 导致永久 "Loading"；串行路径始终保留为立即回退开关。
6. **配置可证明**：日志记录 Preferences 请求值、实际引擎值和 `Fixed` 状态，不能只记录三态设置值。

非目标：本次不继续扩大 `ParallelParseScript`，不把 ParseScript 的共享深度/跳转状态并行化；不通过关闭所有并行来掩盖 Core 侧竞态；不重做 SAF 的全部存储抽象。

### 2. 开关与验证策略（本轮跳过发布止血）

NativeAOT 目前仍是实验性目标，本轮不修改 Android 的 `auto` 默认值，也不把发布降级作为修复条件。保留现有 `ParallelErbLoad` 的 on/off 开关，用于桌面、Android JIT 和 Android NativeAOT 的差分验证。

- `off` 作为串行基线，`on` 作为待验证的 bounded 并行路径；每次 A/B 只改变一个变量。
- `EraCore.Maui/BridgeHost.cs:143-165` 仍应补充 `requested/effective/fixed/platform` 观测，避免把 Preferences 三态误认为引擎实际值。
- `ConfigItem<T>.Value` 在 `Fixed=true` 时不会写入（`EraCore.Core/Shared/Runtime/Config/ConfigItem.cs:62-70`），覆盖测试必须验证“请求值”和“实际值”都被记录。
- `PushConfigMessage` 继续用于同步 Preferences 三态，但不作为引擎有效值的唯一证据。
- 若 bounded 并行尚未通过门禁，使用 `off` 做稳定性基线即可；本轮不要求新增 Android 默认关闭逻辑。

发布默认策略留给后续产品决策：代码修复与测试完成后，再根据 NativeAOT 是否进入正式支持范围决定是否调整 Android `auto`。

### 3. P0：重写 ERB 并行调度生命周期

#### 3.1 选择有界滑动窗口

改动集中在 `EraCore.Core/Shared/Runtime/Script/Loader/ErbLoader.cs:1039-1082`，保留 `LoadPendingSerial` 作为语义基准。推荐移除“全量 Task + `SemaphoreSlim`”组合，使用按索引推进的有界滑动窗口。窗口大小为 `clamp(Min(ProcessorCount, MaxParallelErbFiles), 1, pending.Count)`；维护 `index -> Task<GenerationResult>`，先启动窗口内文件，合并一个文件后再启动下一个。

实现要点：

- 不创建 `arts[pending.Count]`、`errors[pending.Count]` 或 `tasks[pending.Count]`；运行表只持有窗口内任务。
- `MaxParallelErbFiles` 同时代表 active worker 和未合并 artifact 的上限。`pending.Count == 0` 时直接返回。
- 主循环严格按 `mergeIndex` await；当前 artifact 合并并释放临时容器后，才补充下一个文件。最慢的低索引文件最多让窗口内的高索引产物暂存。
- `MergeErbFile` 仍是唯一共享写入口。`PendingOverloads`、`PendingOnlys` 在 artifact 内收集并在合并期解析，保持跨文件语义和警告槽位顺序。
- `isOnlyEvent` 不作为 worker 的共享可变视图传入；并行生成使用每任务独立的空视图，只有 `MergeErbFile` 更新共享列表。

#### 3.2 统一结果和异常语义

当前 `GenerateErbFile` 在并行调用中使用 `replayOnFailure:false`，异常时 artifact 可能未回传，外层只能看到 `errors[idx]`。改为显式的内部 `GenerationResult`（或等价的 artifact + exception 结果），至少承载文件索引、文件名、完整/部分 artifact、异常和取消状态。

- worker 捕获生成异常并返回失败结果，不在 worker 线程写控制台或全局 warning。
- 主循环只在按序处理到该索引时认定它是首个错误，回放该文件已有的 `ConsoleOps`/warnings 一次，然后保留原异常类型和堆栈抛出；不能因竞态提前报告高索引异常。
- `art.HasError` 仍按现有语义合并并继续，不把脚本诊断错误误当作调度异常。
- `MergeErbFile` 自身抛错时也走同一收束路径。部分初始化的 `LabelDictionary`/host 不得继续进入游戏，应由外层按现有失败路径丢弃。
- 如果暂时保留 `SemaphoreSlim`，必须记录 `enteredGate`，只有 `WaitAsync` 成功后才 `Release`；首选直接删除 gate，消除 Dispose 竞态。

#### 3.3 取消、等待和上下文清理

从 `LoadErbDir` 向 `LoadPendingParallel` 传入可选 `CancellationToken`，并在并行调度内部创建 linked CTS：外部取消用于 reload/会话退出，内部取消用于本轮首个可报告异常。

- worker 在开始生成前检查 token；`GenerateErbFileCore` 的读行循环按固定间隔检查 token，避免取消后继续扫描巨大文件。
- 发现按序首错或外部取消后调用 `Cancel`，但不能只调用 `Cancel` 就返回；`finally` 必须 await 所有已启动任务。
- 收束中的 `OperationCanceledException` 只能作为次生状态记录，不能覆盖原始加载异常。
- `GenerateErbFile` 的 finally 同时清理 `ParserMediator` warning buffer、`LogicalLineParser.ScanLineMirror` 和 `ScanLineLocal`。当前 `ErbLoader.cs:598-601` 已清前两项，但要确认 `ScanLineLocal` 不在 worker 上残留。
- 所有 worker 收束后才允许清理 `GlobalStatic.Process.RuntimeLoader`、`_activeArgLoader`、IR writer 和新 host。异常路径继续调用 IR writer `Abort`，避免半截缓存被下一轮命中。

### 4. P0：消除生成期共享状态竞态

#### 4.1 立即防护 `privateDimList`

`IdentifierDictionary.privateDimList` 在 `EraCore.Core/Shared/Runtime/Script/Data/IdentifierDictionary.cs:117` 是普通 `List<string>`，写入在 `CheckUserPrivateVarName:424`，读取在 `ThrowException:645`。这是当前已确认的生成期共享写。

第一版修复应增加该列表专用锁，`Add` 和 `Contains` 都在同一锁内完成。不用 `ConcurrentBag`，因为实际语义是集合查询；保持重复项和 `Contains` 结果语义不变。随后增加多文件 `#DIM/#DIMS` 压力测试，反复比较串行和并行的错误、警告及标签结果。

#### 4.2 后续收敛为每文件状态

锁是止血措施，不是最终隔离模型。并行生成前应冻结 `IdentifierDictionary` 的只读表；凡是生成期仍会写入的字段，都应改成 artifact 的 per-file 收集项，再由 `MergeErbFile` 按序合并。至少审计：

- `privateDimList`、`CharaDimList`；
- `varTokenDic`、`localvarTokenDic`、`varData` 的新增/扩容路径；
- 宏表、重命名表、静态缓存以及 `_irWriter`；
- `ParserMediator.RenameDic`、`warningList`、`GlobalStatic.Process.scaningLine` 的加载期间读写。

审计结果要形成“生成期允许读 / 生成期禁止写 / 只能在合并期写”的表，并在测试构建中用 observer 或断言捕获新共享写。per-file warning buffer 和 `LogicalLineParser` 的 AsyncLocal 接缝继续保留，但必须有成功、异常、取消三条清理测试。

### 5. P1：限制 SAF 与 Preload 的资源放大

当前 `Preload.LoadTreesAsync` 在 `EraCore.Core/Shared/Runtime/Utils/Preload.cs:131-137` 同时加载 ERB/CSV 两棵树；每棵树内部又在 `:64-75` 使用 `Task.Run + Parallel.ForEach`。Android `SafGameDirAccessor.ReadAllBytes/OpenRead` 在 `:323-378` 会把 content URI 全量复制到 `MemoryStream`，会叠加 Binder、线程池、临时 byte[]、解码结果和 ERB artifact 峰值。

建议第二阶段建立统一加载预算：

1. `Preload.LoadTreesAsync` 取消两棵树的无条件 `Task.WhenAll`，或让两棵树共享同一个全局读取 gate；不能各自拥有独立额度。
2. `Preload.Load` 使用显式 `ParallelOptions`/取消令牌。SAF accessor 使用保守的 1-2 个并发读取，桌面文件系统可保留更高配置。
3. 不在 `SafGameDirAccessor.OpenRead` 内用同步 `Wait` 伪造异步限流；优先在 Preload 调度层限流，避免阻塞调用线程。
4. 保留缓存语义和 `ClearCachedLines` 收尾；先控制临时峰值，再根据 `dumpsys meminfo` 评估是否需要改变整文件缓冲策略。
5. 日志记录 `tree/readDegree/activeReads/bytes/elapsedMs`，区分 SAF 阻塞、解码和 ERB 生成耗时。

这一阶段完成前，不把 SAF 并发和 ERB 生成并发简单相乘；测试必须断言整个加载流程的实际读取上限。

### 6. P0（第一阶段）：失败可见切片——server 干净地错误退出

> 2026-08-27 审查修订：本节前半从原"P1 观测配套"提升为**第一阶段**，先于 §3/§4 的根因修复实施。目标：任何 Core 加载期 C# 异常都必须让会话以 error turn 干净结束、前端 Loading 清除并显示错误原因，而不是整个 app 卡死。

#### 6.1 已核实的失败传播链断点（代码证据）

当前"加载失败"有两种截然不同的结局，取决于异常发生在哪一层：

- **能干净失败的路径**：`LoadPendingParallel` 抛出的异常会被 `Process.Initialize` 的大 catch 捕获（`Process.cs:208-216`）→ `return false` → `ConsoleStateManager.Initialize` 置 `State=Error`（`ConsoleStateManager.cs:80-88`）→ runLoop 正常启动、initial turn 发出后因终态立即退出并派发 final error turn。
- **永久假死的路径**（已核实三个断点）：
  1. `Session.GameLoopAsync`（`EraCore.Core/Server/Session.cs:104-173`）的 try/catch 只包住 runLoop 回调内部；而 `console.Initialize()` 是在 `GameLoopComposer.RunAsync`（`EraCore.Core/GameLoopComposer.cs:38`）进入回调**之前** await 的。这一段抛出时无人捕获——`_gameTask` 静默 fault，`_loading` 永远为 true，`StateString` 永远返回 `"Loading"`，前端看不到任何错误。
  2. `ConsoleStateManager.Initialize` 主流程**只有 `finally`、没有 `catch`**（`ConsoleStateManager.cs:55-105`）；其中 `Preload.LoadTreesAsync`（`:63`）位于 `Process.Initialize` 保护圈之外，预载树一抛即穿透到上一断点。
  3. MAUI 侧 `MainPage.OnReloadGame`（`MainPage.xaml.cs:242-335`）只对"选目录/初始化配置"阶段做了错误回退（旧 host 推 error turn），`RecreateHost()`/`_host.Start()` 之后新 host 内部的加载失败（即上一条 fault）没有对应处理。

#### 6.2 第一阶段范围（约 1-2 人日，单独立提交 `loader-observability`）

> **实施状态（2026-08-27）：四项已全部实现。** Session 收束见 `EraCore.Core/Server/Session.cs`（`GameLoopAsync` 外层 try/catch + `DeliverInitStageFailure`/`FinalizeEndedLoop` 提取）；CSM 失败语义见 `ConsoleStateManager.cs:94-107`（主流程加 catch → 置 `State=Error` 不抛）；配置日志见 `EraCore.Maui/BridgeHost.cs`（`ApplyPerformanceOverrides` 输出 requested/effective/fixed/platform）。红测：`EraCore.Tests/SessionInitFailureTests.cs`（configData=null 注入 bootstrap NRE，断言 HasEnded/LastError/非 Loading/错误回合可达）——修复前 10s 挂死失败，修复后 338ms 通过；全量 873 测试通过，Core/Server/Cli/Maui 零警告。**遗留**：Vue/WebView 侧渲染错误回合的端到端断言（`EraCore.Maui.Tests`）未覆盖，属 §6.3 后续观测工作。

1. **Session 层收束**：`GameLoopAsync` 对 `GameLoopComposer.RunAsync` 整体加 try/catch，与现有 runLoop 回调内 catch 同构——置 `_loading=false`、写 `_lastError`、用现成 `ErrorInfo.From` 发结构化 error turn（`{"error", "errorInfo", "state"}`，NativeAOT 下禁用匿名类型反射序列化）、走 `EndOfGame` 收尾并 `finally` 复用现有清理。这是"C# server 干净地错误退出"的主体，改动很小。
2. **统一失败语义**：`ConsoleStateManager.Initialize` 目前"返回 false 置 Error、抛异常穿透"两条腿走路。约定：CSM 保持只置 `State=Error` 不抛（`Preload.LoadTreesAsync` 包进同一 try）；Session 层 try/catch 仅作最后防线，两层都不静默吞异常（照记 AgentLog）。
3. **前端契约红测**：验证 MAUI 托管模式（`fullDiffOnFirstTurn=true`）下，session 在加载期间就结束时 Vue 的 Loading 态被清除并渲染最终 error turn——红测：faulted initialize → 断言收到错误回合、`HasEnded=true`、`StateString != "Loading"`。
4. **配置可证明日志**（原不变式 6，随本切片落地）：`ApplyPerformanceOverrides`（`EraCore.Maui/BridgeHost.cs:143-165`）输出 requested/effective/fixed/platform 四项；`ConfigItem<T>.Value` 在 `Fixed=true` 时不写入（`ConfigItem.cs:62-70`），必须记录实际引擎值而非 Preferences 三态。没有这层日志，错误消息只有半截，无法证明失败时 `ParallelErbLoad` 的引擎实际值。

#### 6.3 切片之后保留的观测工作（仍为 P1，随各阶段收尾）

- `MainPage.OnReloadGame` 新 host 初始化失败时保留旧 host，且必须确认新初始化任务及其所有 ERB worker 已收束。
- 对 reload 增加"同一时刻只有一个初始化轮次"的取消/版本号保护；后来的路径选择应取消前一轮，旧轮不能在新 host 上写状态。若首轮修复后仍无 reload 重现，可作为独立小改动，不与 ERB 调度重构混合提交。
- 每次加载输出 `LoadPendingParallel: files/degree/window/started/completed/cancelled/peakArtifacts`，失败输出 `idx/filename/stage/exception`，不要输出游戏脚本内容。
- `phase=genmerge`、`phase=parsescript`、`Preload` 和 `OpenRead buffered OK` 日志必须能够拼出完整时间线；保留现有 GC 增量和 heap 统计。

#### 6.4 边界：本切片不解决什么

- 线程池饿死型的"卡住"（全量 `Task.Run` + SAF 全量 MemoryStream 缓冲把池推满）**没有异常可捕**，仍表现为无响应——由 §3 有界调度和 §5 SAF 限流治理。
- NativeAOT 层原生崩溃（SIGABRT、lowmemorykiller 杀进程）在 C# 侧不存在"干净退出"——只能靠 §9 门禁与日志判别。
- 尾任务竞态、`privateDimList` 损坏等根因仍由 §3/§4 修复。

### 7. 实施顺序与提交边界

按以下顺序实施，每一步都保持可构建、可回退。**第 1、2 步构成第一阶段（先让失败可见），3-5 步是根因修复**：

1. **建立基线与红测**：固定 `ParallelErbLoad=off/on` 对照，增加失败尾任务、取消后 await、窗口上界、AsyncLocal 清理和多文件 `#DIM/#DIMS` 测试。并发测试使用可控 test hook/`ManualResetEventSlim`，不要依赖随机 `Sleep`。同时补失败可见红测（见 6.2 第 3 条）。
2. **失败可见切片**（§6.2）：Session 层整体收束 `GameLoopComposer.RunAsync` 异常并派发 error turn；统一 CSM 初始化失败语义（置 `Error` 不抛）；Loading 状态清理红测通过；requested/effective/Fixed 配置日志落地。**此步不碰调度与并行逻辑**——它是后续所有真机 A/B 的诊断基础设施：任何可捕异常都会带着 `errorInfo` 浮到前端。
3. **调度器修复**：替换全量任务数组为有界滑动窗口；引入 `GenerationResult`、linked CTS、异常收束和 artifact 临时容器释放。保持 on/off 都可切换。
4. **共享状态修复**：先锁住 `privateDimList`，再完成共享写审计；若发现其他生成期写，移动到 artifact + 有序合并。
5. **SAF/Preload 限流**：统一 ERB/CSV 读取预算，加入 fake accessor 并发计数测试，再上真机。
6. **剩余观测与端到端验证**：§6.3 的阶段指标日志补齐，完成桌面、Android JIT 和 NativeAOT 的 A/B 门禁。
7. **单独做默认策略决策**：所有门禁通过后，再根据 NativeAOT 是否进入正式支持范围决定 Android `auto`；本轮不要求改变默认值。

建议提交保持可逆且按上述边界分开：`loader-observability`（先行）、`erb-bounded-lifecycle`、`erb-shared-state`、`preload-saf-budget`、`nativeaot-gate`，避免一个大提交同时改变调度、配置和 UI 失败处理。

### 8. 回归测试设计

#### 8.1 Core/loader 测试

在 `EraCore.Tests/ErbLoaderTests.cs` 及必要的测试夹具中补充：

- **串并行等价**：标签顺序、`$` 标签、`#ONLY`、跨文件重名、警告顺序、进度顺序、`HasError` 和最终 `LabelDictionary` 与串行基线一致。
- **尾任务收束**：低索引文件故意失败，高索引文件进入可控阻塞；断言 `LoadErbDir` 返回/失败前所有已启动 worker 已退出，没有在失败之后写入 label/warning/IR。
- **异常回放一次**：生成中途产生 console op/warning 后抛异常，断言按序回放一次且原异常类型/文件位置保留。
- **窗口上界**：用 observer 记录 active worker、unmerged artifact、已释放 artifact，断言均不超过窗口；断言不会为全部文件创建长期任务引用。
- **取消与清理**：外部 token、首错内部 token、正常完成三种路径都检查任务收束、warning buffer、`ScanLineLocal`/`ScanLineMirror` 和 runtime loader 状态。
- **私有变量压力**：生成几十/几百个包含多个 `#DIM/#DIMS` 的文件，重复多轮并行加载；与串行错误和 warning 快照比较，不能出现 `ArgumentOutOfRangeException`、丢项或误报。
- **模式矩阵**：`LazyFunctionLoad`、`IrDiskCacheLoad`、`AnalysisMode`、`ParallelParseScript` 分别开关；确认 `ParallelParseScript` 不改变本次 ERB 调度结论。
- **IR 生命周期**：正常完成可 Complete；worker/merge 异常会 Abort；下一轮不会命中半截缓存。

测试夹具应允许注入文件生成延迟、失败点和 observer；不要把生产代码的并发正确性建立在真实线程调度的偶然性上。

#### 8.2 Preload/MAUI 测试

- fake `IGameDirAccessor` 记录最大并发 `ReadAllBytes`，覆盖 ERB/CSV 双树，断言统一上限而不是每棵树各自上限。
- 覆盖 SAF 读失败、空字节、单文件 fallback、取消和 `ClearCachedLines`。
- 在 `EraCore.Maui.Tests` 增加 `auto/on/off` 的平台默认解析、`Fixed=true` 时实际值不变、日志快照和错误 turn/Loading 状态测试。
- 对 `MainPage.OnReloadGame` 做两次快速 reload 测试，确认旧初始化不会在新 host 上继续写入。

### 9. NativeAOT Android 验收门禁

> **构建记录（2026-08-28，erb-bounded-lifecycle 工作树）**：`dotnet publish EraCore.Maui/EraCore.Maui.csproj -f net10.0-android -r android-arm64 -c Release -p:PublishAot=true -p:RunAOTCompilation=false`，.NET 10.0.400 + Microsoft.Android workload 36.1.69。产物 `EraCore.Maui/bin-aot/Release/net10.0-android/android-arm64/com.eracore.maui-Signed.apk`（27,317,728 字节），SHA-256 `3725c53ebd222ebbd4e7236de45638ddc7a1897dcec9b64394d633134e7c4c03`。仅 XA1040/IL3053 实验性提示（csproj 已条件豁免）。注意：此 APK 含尚未提交的有界调度改动；on/off A/B 用同一 APK 经设置页开关切换即可。
>
> **构建记录二（2026-08-28，含 771708b/dcd5c25/7c10328）**：同命令重建，SHA-256 `4fe1ea80a6dc1881ba7ad7102652a1c704708eec8c33236444db5bcf40adaedf`。新增：EmueraLog→logcat（tag=Emuera）、Preload SAF 每树 1 并发。**前版 APK 判别结论**：SIGABRT 崩溃为 Preload SAF 读取风暴中 monodroid bridge `take_global_ref` abort，两轮开关均跑同等风暴（2722 vs 2550 次 OpenRead）——该崩溃类与 `ParallelErbLoad` 无因果，且 MuMu 为 x86_64+houdini ARM 翻译，结果不作为门禁；真机复测仍必须执行。
>
> **构建记录三（2026-08-28，含 dcd82c8 IR 命中 NRE 修复）**：SHA-256 `f9a6ff759da1407abd15b9d312d26b1385e2f55a681464d41ee2710f1fc1ad75`。四开关全开错误屏根因：IR 命中桩不恢复 `#FUNCTION` 的 Depth=0 → Depth-BFS 不物化 → 式中方法走进空行链桩 NRE（桌面真实语料复现，dcd82c8 修复 + 红测）。模拟器 off/on 两轮稳定通过（bounded 调度日志：2388 文件、window=4、peakActive=4、genmerge 3.5s）。

固定同一 `android-arm64` 设备、同一 APK、同一游戏目录和同一版本配置；冷启动前完整退出应用，每轮只发起一次加载。生产 MAUI 的目标是 `net10.0-android`，Release 使用项目现有 `RunAOTCompilation=true`；若使用显式 `PublishAot=true`，必须单独记录构建参数和 APK SHA。

建议顺序：

1. 构建并安装 Release NativeAOT APK，保存版本、ABI、API level、设备型号和 APK SHA。
2. A/B：`ParallelErbLoad=off`、旧并行实现（若可构建复现）、bounded 并行实现；其他开关逐项固定，不在一轮同时改变多个变量。
3. 每个配置至少冷启动 10 次，并覆盖 `test_game`、真实大游戏、本地路径和 SAF URI；另测一次 reload 和连续进入/退出。
4. 收集完整 logcat 和应用内日志，重点检查 `LoadPendingParallel`、`phase=genmerge`、`phase=parsescript`、`OpenRead buffered OK`、`ObjectDisposedException`、`OutOfMemoryError`、`FATAL EXCEPTION`、`SIGABRT`、`lowmemory`。
5. 卡住时保存 `adb shell getprop ro.build.version.sdk`、`adb logcat -c`、`adb logcat -v threadtime`、`adb shell pidof <package>`、`adb shell top -H -p <pid> -n 1` 和 `adb shell dumpsys meminfo <pid>` 的完整输出。

放行条件：

- 10/10 冷启动完成并到达首帧，不能出现 UI 永久 Loading、闪退、ANR、OOM 或未观察任务异常。
- 失败 fixture 中所有 worker 在错误返回前结束，下一次加载不受上一轮尾任务影响。
- 本地和 SAF 路径的标签/警告/错误结果与串行基线一致。
- 记录 bounded 并行的峰值 PSS、GC、读取并发和启动 p50/p95；内存没有明显超过串行基线，性能收益不是以稳定性换取。
- NativeAOT 门禁同时覆盖 Android Debug/JIT 对照、Release Full AOT、至少一个低内存/低核设备；实验项目冒烟通过不能替代生产壳验收。

未满足任一稳定性条件时，只把 Android `off` 作为验证基线；本轮不修改 `auto` 默认，也不因桌面性能测试通过而宣称 NativeAOT 已受支持。

### 10. 完成定义

本问题只有在以下条件全部满足后才算修复：

- **失败可见切片已合并**（§6.2）：`Session.GameLoopAsync` 对初始化阶段异常整体收束并派发 error turn，CSM 初始化失败语义统一，faulted-initialize 红测通过——加载期任何 C# 层异常都以错误回合干净结束 Loading，不再存在 `_gameTask` 静默 fault 导致的永久 "Loading"；
- 有界调度、异常收束、取消传播和 artifact 释放已合并，并有确定性回归测试；
- `privateDimList` 及审计出的其他生成期共享写已受保护或迁移到有序合并；
- SAF/Preload 读取并发有统一预算，真实峰值有日志证据；
- Core、MAUI 和 NativeAOT Android 构建/测试通过，且遵守 `TreatWarningsAsErrors`；
- 真机 A/B 在真实游戏上稳定到达首帧，日志无尾任务、Dispose、OOM、ANR 和隐藏 fault；
- `ParallelErbLoad` 的 on/off A/B、默认值和回退行为都有明确记录；是否改变 Android `auto` 由后续产品支持决策单独确认。

在这些条件之前，bounded 并行仍是待验证实验路径；`ParallelErbLoad=off` 只作为稳定性基线，不代表根因已经修复。
