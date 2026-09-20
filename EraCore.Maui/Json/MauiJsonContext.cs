using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EraCore.Maui.Json;

// =====================================================================
// 3.4 壳层 NativeAOT 硬性改造（2026.8.8 白屏根因修复）
// ---------------------------------------------------------------------
// 白屏根因（logcat 实证）：
//   FATAL EXCEPTION: main — System.InvalidOperationException:
//   JsonSerializerIsReflectionDisabled
//     at BridgeHost.HandleScanGames() → JsonSerializer.Serialize(匿名类型)
//   NativeAOT 下 System.Text.Json 反射序列化被禁用（IL3050 警告的运行时
//   形态），Vue 启动发 scanGames → C# 壳层匿名类型序列化 → 抛异常 →
//   JNI 回调用 mono.android.Runtime.propagateUncaughtException 的导出缺失
//   → 进程挂起白屏。
// ---------------------------------------------------------------------
// 修复：所有壳层消息改具名 record + 本源生成上下文（wire 契约不变——
//   PropertyNamingPolicy = CamelCase 产出与匿名类型一致的字段名；
//   不设 WhenWritingNull，null 字段仍写出，与旧行为逐字节一致）。
// =====================================================================

// ---- DTO：Vue ⇄ C# 壳层消息（字段名经 CamelCase policy 映射为旧 camelCase wire）----

/// <summary>{"type":"folderPicked","path":...}</summary>
internal sealed record FolderPickedMessage(string Type, string? Path);

/// <summary>{"type":"folderPicked","error":...}</summary>
internal sealed record FolderPickedError(string Type, string Error);

/// <summary>{"type":"safDirectoryPicked","error":...}</summary>
internal sealed record SafDirectoryPickedError(string Type, string Error);

/// <summary>{"type":"safDirectoryPicked","cancelled":true}</summary>
internal sealed record SafDirectoryPickedCancelled(string Type, bool Cancelled);

/// <summary>
/// {"type":"safDirectoryPicked","path":...,"hasWrite":...,"writeProbeOk":...,
///  "writeProbeDetail":...,"error":...,"needRepickForWrite":true}
/// </summary>
internal sealed record SafDirectoryPickedRepick(
    string Type,
    string? Path,
    bool HasWrite,
    bool WriteProbeOk,
    string? WriteProbeDetail,
    string Error,
    bool NeedRepickForWrite);

/// <summary>
/// gamesScanned 的 games 数组元素——{name,fullPath,irCached,irCacheBytes,irCacheBuiltUtc}（替代匿名类型）。
/// issues/05：irCached/irCacheBytes/irCacheBuiltUtc 为 IR 缓存存在性提示（非有效性承诺）。
/// </summary>
internal sealed record GameInfoDto(
    string Name,
    string FullPath,
    bool IrCached,
    long IrCacheBytes,
    string? IrCacheBuiltUtc);

/// <summary>
/// {"type":"gamesScanned","games":[...],"rootDir":...,"rootDirExists":...,"displayPath":...}
/// <para>
/// issues/15：DisplayPath 是 RootDir 的**可读形态**——SAF 的 content URI 解成
/// 「内部存储 / games / eratoho」，让用户看得出自己选的是哪个目录（纯路径由 Core 的
/// <c>SafPath.TryGetDisplayPath</c> 给出，前端不重复实现）。真实文件路径恒等于 RootDir。
/// </para>
/// <para>
/// 前端渲染用它，判定「快速 / 兼容」模式**仍必须用 RootDir 原文的 content:// 前缀**——
/// 展示名不含协议信息，拿它判模式会把 SAF 轨误判成快速轨。
/// </para>
/// </summary>
internal sealed record GamesScannedMessage(
    string Type,
    List<GameInfoDto> Games,
    string? RootDir,
    bool RootDirExists,
    string? DisplayPath);

/// <summary>{"type":"directoriesListed","currentPath":...,"parentPath":...,"subDirectories":[...]}</summary>
internal sealed record DirectoriesListedMessage(
    string Type,
    string? CurrentPath,
    string? ParentPath,
    List<string> SubDirectories);

/// <summary>
/// storageRoots 的 roots 数组元素——issues/15 S2（C2 页内真实路径浏览器）的可浏览根。
/// <paramref name="Path"/> 是真实文件路径（非 content URI），由系统给出挂载点，前端不猜。
/// </summary>
internal sealed record StorageRootDto(
    string Path,
    string Label,
    bool Primary,
    bool Removable);

/// <summary>
/// {"type":"storageRoots","roots":[{path,label,primary,removable}]}
/// <para>
/// issues/15 S2：页内真实路径浏览器的起点清单。Android 由 <c>StorageManager.StorageVolumes</c>
/// 给（主存储 + SD/OTG 卷），Windows 由 <c>DriveInfo</c> 给就绪盘。
/// </para>
/// </summary>
internal sealed record StorageRootsMessage(string Type, List<StorageRootDto> Roots);

/// <summary>{"type":"gameExited"}</summary>
internal sealed record GameExitedMessage(string Type);

/// <summary>{"type":"permissionStatus","granted":...}</summary>
internal sealed record PermissionStatusMessage(string Type, bool Granted);

/// <summary>
/// {"type":"modeDowngraded","reason":"all-files-access-revoked"}
/// <para>
/// issues/15（方案 B）：真实路径轨失效——「所有文件访问」被系统收回，主目录已自动退回落地的
/// SAF URI（兼容轨）。用户什么都没做却变慢了，必须说一声；只给原因码，文案由前端出。
/// </para>
/// </summary>
internal sealed record ModeDowngradedMessage(string Type, string Reason);

/// <summary>{"type":"gameThreadStatus","alive":...}</summary>
internal sealed record GameThreadStatusMessage(string Type, bool Alive);

/// <summary>{"type":"agentLog","content":...,"truncated":...}</summary>
internal sealed record AgentLogMessage(string Type, string Content, bool Truncated);

/// <summary>
/// {"type":"layout","state":"Loading","gameDir":...,"windowWidth":...,"fontSize":...,
///  "lineHeight":...,"gameColumns":...,"fontName":...}
/// </summary>
internal sealed record LayoutMessage(
    string Type,
    string State,
    string? GameDir,
    int WindowWidth,
    int FontSize,
    int LineHeight,
    int GameColumns,
    string? FontName);

/// <summary>
/// {"type":"config","maxLog":...,"agentLogEnabled":...,"displayReportOverride":"auto"|"off"|"on",
///  "lazyFunctionLoad":...,"irDiskCacheLoad":...,"irFirstDirList":...,"parallelErbLoad":...,"parallelParseScript":...,
///  "sweepGatePolicy":"auto"|"forceon"|"forceoff","safReadDegree":"auto"|&lt;1-64&gt;,
///  "maxParallelErbFiles":"auto"|"4"|"8"|"16","dimLockPolicy":"auto"|"nolock"}
/// （后四字段为性能（启动加载）覆盖三态："auto"|"off"|"on"，缺省 auto=跟随游戏配置；
///   sweepGatePolicy 为 ADR-0026 诊断旋钮一，缺省 auto=跟随平台门；
///   safReadDegree 为 ADR-0026 诊断旋钮二，"auto" 或钳制后的数值字符串，缺省 auto=每树 1 并发；
///   maxParallelErbFiles 为 ADR-0026 诊断旋钮三（06-K1），缺省 auto=min(核数,8)；
///   dimLockPolicy 为 ADR-0026 诊断旋钮四（06-K2），缺省 auto=恒加锁，nolock=纯取证档）
/// </summary>
internal sealed record ConfigMessage(
    string Type,
    int MaxLog,
    bool AgentLogEnabled,
    string DisplayReportOverride,
    string LazyFunctionLoad,
    string IrDiskCacheLoad,
    string IrFirstDirList,
    string IrFileListCache,
    string SrcContentCache,
    string ParallelErbLoad,
    string ParallelParseScript,
    string SweepGatePolicy,
    string SafReadDegree,
    string MaxParallelErbFiles,
    string DimLockPolicy);

/// <summary>{"type":"backButtonPressed"}</summary>
internal sealed record BackButtonPressedMessage(string Type);

/// <summary>{"type":"irCacheRescanned","ok":bool}——issues/13 M5 重扫（删 IR+sidecar）结果回执。</summary>
internal sealed record IrCacheRescannedMessage(string Type, bool Ok, string Path);

/// <summary>
/// {"type":"autoLoadGame","path":...}——issues/13 M6：C# → Vue 的"请帮我加载这个游戏"消息。
/// <para>
/// 由控制面 <c>POST /load-game</c> 触发，C# 侧<b>不自行初始化运行时</b>，而是请 Vue 调它现成的
/// <c>loadGameFromPath(path)</c>。这样走的是与用户手点完全相同的链路
/// （Vue → loadGame → HandleLoadGame → MainPage.OnReloadGame → RecreateHost + Start），
/// 前端 game store 状态天然正确，无需在 C# 侧另写一套 UI 状态同步。
/// </para>
/// </summary>
internal sealed record AutoLoadGameMessage(string Type, string Path);

/// <summary>
/// Headless Server GET /state 的最小子集——MAUI Windows 远程探测只读 state / gameDir。
/// 其余字段（sessionId / 窗口布局）忽略。
/// </summary>
internal sealed record HeadlessStateResponse(string? State, string? GameDir);

/// <summary>
/// MAUI 壳层 JSON 源生成上下文（3.4 NativeAOT 硬性改造）。
/// 覆盖 BridgeHost / MainPage 全部壳层消息；协议层（TurnRecord 等）继续走
/// Core 的 <see cref="MinorShift.Emuera.GameView.EmueraJsonContext"/>。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FolderPickedMessage))]
[JsonSerializable(typeof(FolderPickedError))]
[JsonSerializable(typeof(SafDirectoryPickedError))]
[JsonSerializable(typeof(SafDirectoryPickedCancelled))]
[JsonSerializable(typeof(SafDirectoryPickedRepick))]
[JsonSerializable(typeof(GameInfoDto))]
[JsonSerializable(typeof(GamesScannedMessage))]
[JsonSerializable(typeof(DirectoriesListedMessage))]
[JsonSerializable(typeof(StorageRootDto))]
[JsonSerializable(typeof(StorageRootsMessage))]
[JsonSerializable(typeof(GameExitedMessage))]
[JsonSerializable(typeof(PermissionStatusMessage))]
[JsonSerializable(typeof(ModeDowngradedMessage))]
[JsonSerializable(typeof(GameThreadStatusMessage))]
[JsonSerializable(typeof(AgentLogMessage))]
[JsonSerializable(typeof(LayoutMessage))]
[JsonSerializable(typeof(ConfigMessage))]
[JsonSerializable(typeof(BackButtonPressedMessage))]
[JsonSerializable(typeof(IrCacheRescannedMessage))]
[JsonSerializable(typeof(AutoLoadGameMessage))]
[JsonSerializable(typeof(HeadlessStateResponse))]
internal partial class MauiJsonContext : JsonSerializerContext
{
}
