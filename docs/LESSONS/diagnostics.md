# 排查方法论 失败教训

> **TL;DR**：分层推进时"下一层失败"是进度不是回归（用阶段日志区分，显式作废过时假设）；"看起来卡死"先定性（忙转/阻塞 IO/等输入/会话已结束）；logcat 中文乱码 ≠ 内存路径损坏（信结构化验收字段）；被 catch 的异常默认不进 logcat（显式加日志）；回调层异常是黑洞（必须 try-catch + 打日志）。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 诊断推进会「揭开」下一层失败 | 下一层失败是进度；阶段日志区分 + 作废过时假设 | — |
| 2 | adb logcat 中文路径乱码 ≠ 路径损坏 | 信结构化验收字段（loaded/hasDVAR/SystemSaveInBinary） | — |
| 3 | Process.Initialize 返回 false 缺少异常诊断 | catch 里被 handleException 的异常不进 logcat；显式 Console.WriteLine | — |
| 4 | 运行时脚本 File.* 是全量迁移最后一块 | 初始化路径与运行时脚本路径两层，后者易忽略 | — |
| 5 | logcat 时间线 + DebugView 定位"假冻结" | 卡死先定性四类；lastTurnJson 是一手证据 | 2026-07-31 |
| 6 | 存档写失败的真实异常被统一 CodeEE 掩蔽 | 宽 catch 吞内层异常只留外皮；透出 `ex` 类型+消息后一举定位句柄泄漏 | 2026-08-22 |

---

## 1. 诊断推进会「揭开」下一层失败，勿误判为回归

**场景**：修掉「只加载 1 个 ERH」后，游戏输出突然变短，出现大量「バイナリ型セーブ」必須警告，并 `ErhLoadHeaderFilesFalse`。

**原因**：以前几乎没跑 `#DIM`；全量 1692 条 dim 后才触发「未读到 SystemSaveInBinary」的真实错误。再修 config 后标题通；再点接受才暴露 FileStream 存档。

**教训**：分层推进时，下一层失败是**进度**，不是「越修越坏」。用阶段日志区分：`ERH loaded` / `Config SystemSaveInBinary` / `Initialize OK` / 运行时 SAVEDATA。过时 handoff（如「Initialize false 主因」）必须显式作废，避免下一会话退回旧假设。

## 2. adb logcat 中文路径乱码 ≠ 内存中路径损坏

**场景**：`firstKey=鍙ｄ伉\...KOJO_K4.ERH` 一类 mojibake；同时游戏已成功加载上千 ERB。

**原因**：logcat / PowerShell 控制台编码与进程内 UTF-16 字符串不一致。  
真正坏路径通常伴随 `OpenOnCache FAIL`、0 files、或 `hasDVAR` 类逻辑键错误。

**教训**：优先信**结构化验收字段**（`loaded=142/142`、`hasDVAR`、`SystemSaveInBinary`、相对路径警告是否为 `SYSTEM\...`），不要仅凭 adb 中文乱码判定文件名损坏。

## 3. `Process.Initialize` 返回 false 时缺少异常诊断

**场景**：`Process.Initialize` 返回 false，`ConsoleStateManager` 设 `ConsoleState.Error` 状态，游戏显示"错误：未知错误"。

**结果**：Exception 被 `Initialize` 的 catch 块捕获，调用 `handleException` 输出到游戏控制台（可能被清空或覆盖），开发者在 adb 日志中看不到异常详情。

**原因**：`Initialize` 的 catch 块：
```csharp
catch (Exception e)
{
    handleException(e, null!, true);
    console.PrintSystemLine(trsl.ErhLoadingError.Text);
    return false;
}
```
`handleException` 把异常信息写入 `console`（游戏输出缓冲区），但这个缓冲区后续可能被 `OutputLog`/`RefreshStrings` 清空或覆盖，不会输出到 `Console.WriteLine` 能到达的 logcat DOTNET tag。

**解决**：在 catch 块中加 `Console.WriteLine` 日志：
```csharp
catch (Exception e)
{
    Console.WriteLine($"[proc] Initialize exception: {e}");
    handleException(e, null!, true);
    ...
}
```

**教训**：任何在 MAUI/Android 游戏内部被 catch 的异常，默认不会出现在 `adb logcat` 可见的日志流中。`handleException` 把错误写入游戏输出缓冲区，但该缓冲区可能在后续操作中被覆盖。必须显式加 `Console.WriteLine` 才能让异常信息出现在 logcat DOTNET tag。

## 4. 运行时脚本 `File.*` 调用是全量迁移的最后一块

**场景**：`Creator.Method.cs`（10 处）和 `VariableEvaluator.cs`（26 处）中有大量 `File.Exists`/`Directory.Exists`/`File.ReadAllText`/`FileStream` 等原始 System.IO 调用。初始化路径的 `File.*` 调用已迁移，但运行时脚本路径（ERB `GETDIR`、`EXIST`、`SAVEDATA`、`LOADDATA` 等指令）还有大量未迁移。

**解决**：新增 `SafCompat` 辅助类，包装 `File.*`/`Directory.*` 调用：
```csharp
internal static bool FileExists(string path)
{
    if (!path.StartsWith("content://")) return File.Exists(path);
    return GamePaths.Current?.DirAccessor?.FileExists(path) ?? false;
}
```
在脚本函数中逐处替换。SAF 写路径（`SAVEDATA`/`FileStream`）暂不支持，静默跳过并打出警告。

**教训**：SAF 文件读取迁移不是一次性的——它分布在初始化路径和运行时脚本路径两个层次。初始化路径（Preload/EraStreamReader/Loader）是核心，运行时脚本路径（Creator.Method/VariableEvaluator）是 ERB 脚本执行时触发的第二层。后者同样重要但易被忽略。

## 5. 用 logcat 时间线 + DebugView 定位"看起来卡死"的假冻结

**场景（2026-07-31）**：自动存档后游戏冻结，怀疑是存档慢 / 死锁 / IO 挂起 / 会话退出。

**定位步骤**：
1. **logcat 时间线**：`[SaveTo] begin → defer buffer → flush OK → [SaveTo] ok` 拆出每段耗时，区分"写缓冲慢"vs"ContentResolver 慢"
2. **前端 DebugView 的 `lastTurnJson`**：直接看到最后一回合的 `state` + `error`——`error="script requested exit (QUIT/EXIT)"` + `state=Running` 直接指向 GameExitException 被误处理
3. **`[bridge] game loop completed normally`**：游戏循环正常返回 ≠ 没问题——查它是被 Stop / 通道关闭 / 超时结束的
4. **线程 TID 消失 + 进程 CPU≈0%**：async 任务挂起在 await（如等输入），不是死锁也不是忙转；`/proc/<pid>/task/<tid>` 不存在不代表进程死了

**教训**：游戏"卡死"先定性——忙转（CPU 高）/ 阻塞 IO（wchan=binder_thread_read）/ 等输入（TID 消失、CPU 低）/ 会话已结束前端没收到通知（game loop completed normally + 前端停在旧帧）。DebugView 的 `lastTurnJson` 是最快的一手证据，`[bridge]` / `SaveTo` / `OpenWrite` 日志是拆时间线的骨架。

## 6. 存档写失败的真实异常被统一 CodeEE 掩蔽——透出内层才是一举定位的关键（2026-08-22）

**场景**：`TW4.881shoess` 首次进标题即报 `グローバルデータの保存中にエラーが発生しました`（`@SYSTEM_TITLE` 的 `SAVEGLOBAL`）。多处 `出し 経過時間` 后标题画面崩溃。仅看表面无法判断根因属于「IO/文件层」还是「数据层」。

**掩蔽链**：`VariableEvaluator.SaveGlobal()` 用一个**很宽的 `catch (SystemException ex)`** 把存档序列化路径里抛出的**任何** `SystemException` 一律转译成统一 CodeEE（`ErrorSavingGlobalData`），原始 `ex` 被丢弃。关键是 `EmueraException`（`FileEE`/`CodeEE` 基类）声明为 `ApplicationException : SystemException`——所以**内部 `FileEE`（InvalidArray/InvalidStream）与真实 .NET `IOException` 全都汇成同一句外皮**，从外皮完全无法反推。原内层异常只在 `#if HEADLESS` 的 `EmueraLog.Debug` 里打，而 agent.log 为空/被覆盖，`emuera.log` 只有转译后的 CodeEE → 根因不可见。

**定位步骤**：
1. **透出内层异常**：在 catch 里加 `EmueraLog.Error("SaveGlobal", $"path=… ex={ex.GetType().FullName}: {ex.Message}")`（Error 级别全构建可见，不再缩进 `#if HEADLESS`）。
2. **复现**：本仓库与上游 `SaveGlobal`/写出层/变量登记全部逐字一致，只有 `SafCompat.OpenWrite` vs 上游 `new FileStream` 不同 → 怀疑文件层。用 CLI `--ExeDir` 指向该游戏复现，首回合 `state=Error`；agent.log 拿到：
```
System.IO.IOException: The process cannot access the file '...\sav\global.sav'
because it is being used by another process.  at ... SafCompat.OpenWrite(...)
```
日志时序 `[LoadGlobal] ok → [SaveGlobal] ex=IOException` 直接定位：**读句柄未释放挡住了紧随的写**。

**根因**：`EraBinaryDataReader.CreateReader` 相对上游多加 `new BinaryReader(reader.BaseStream, Encoding.Unicode, leaveOpen:true)` → `bReader.Close()` **不再关闭底层 `fs`**；而各 Load 路径 `finally` 是 `if/else if`，二进制分支只 `bReader.Close()` 不关 `fs`——读句柄（`FileShare.Read`）泄漏，锁住 `sav/global.sav`。跟随其后的 `SAVEGLOBAL` 用 `SafCompat.OpenWrite`（`FileShare.None`）打开写 → 共享冲突 `IOException`。上游 `leaveOpen=false`，`bReader.Close()` 连带释放 `fs`，故不炸。只有**带已存在存档**的二进制游戏才会走到 `LOADGLOBAL→SAVEGLOBAL` 触发序列，所以「引入更多游戏后」才暴露。

**修复**：CreateReader 恢复 `leaveOpen=false`（一条，一次修全部 Load 路径）；LoadGlobal `finally` 防御性显式 `fs?.Close()`（幂等）。复现首回合由 `Error` → `WaitInput`，`SAVEGLOBAL` 过。

**教训**：
- **宽异常袋是定位毒瘤**：`catch(SystemException)`/`catch(Exception)` 里若只抛统一转译、不落内层，线上几乎无从定位。修复前先透出 `ex` 类型+消息，比猜「数据层 vs IO层」高效得多。
- **`leaveOpen` 语义牢记**：`BinaryReader`/`BinaryWriter` 构造多一个 `leaveOpen` 参数就会改变「Close 是否释放底层流」；凡是 `leaveOpen:true`，调用方**必须**显式关底层流。改动读取器共享构造函数最容易埋这类跨调用方的句柄泄漏。
- **文件锁问题的判断抓手**：`IOException: the process cannot access ... because it is being used by another process` + 时间线上前一步刚 `OpenRead/ok`，优先查「读句柄有没有释放」而非文件只读/权限。
