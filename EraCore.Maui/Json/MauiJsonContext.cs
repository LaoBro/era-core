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

/// <summary>gamesScanned 的 games 数组元素——{name,fullPath}（替代匿名类型）。</summary>
internal sealed record GameInfoDto(string Name, string FullPath);

/// <summary>{"type":"gamesScanned","games":[...],"rootDir":...,"rootDirExists":...}</summary>
internal sealed record GamesScannedMessage(
    string Type,
    List<GameInfoDto> Games,
    string? RootDir,
    bool RootDirExists);

/// <summary>{"type":"directoriesListed","currentPath":...,"parentPath":...,"subDirectories":[...]}</summary>
internal sealed record DirectoriesListedMessage(
    string Type,
    string? CurrentPath,
    string? ParentPath,
    List<string> SubDirectories);

/// <summary>{"type":"gameExited"}</summary>
internal sealed record GameExitedMessage(string Type);

/// <summary>{"type":"permissionStatus","granted":...}</summary>
internal sealed record PermissionStatusMessage(string Type, bool Granted);

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
///  "lazyFunctionLoad":...,"irDiskCacheLoad":...,"parallelErbLoad":...,"parallelParseScript":...,
///  "sweepGatePolicy":"auto"|"forceon"|"forceoff","safReadDegree":"auto"|"1"|"2"|"4"}
/// （后四字段为性能（启动加载）覆盖三态："auto"|"off"|"on"，缺省 auto=跟随游戏配置；
///   sweepGatePolicy 为 ADR-0026 诊断旋钮一，缺省 auto=跟随平台门；
///   safReadDegree 为 ADR-0026 诊断旋钮二，缺省 auto=每树 1 并发）
/// </summary>
internal sealed record ConfigMessage(
    string Type,
    int MaxLog,
    bool AgentLogEnabled,
    string DisplayReportOverride,
    string LazyFunctionLoad,
    string IrDiskCacheLoad,
    string ParallelErbLoad,
    string ParallelParseScript,
    string SweepGatePolicy,
    string SafReadDegree);

/// <summary>{"type":"backButtonPressed"}</summary>
internal sealed record BackButtonPressedMessage(string Type);

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
[JsonSerializable(typeof(GameExitedMessage))]
[JsonSerializable(typeof(PermissionStatusMessage))]
[JsonSerializable(typeof(GameThreadStatusMessage))]
[JsonSerializable(typeof(AgentLogMessage))]
[JsonSerializable(typeof(LayoutMessage))]
[JsonSerializable(typeof(ConfigMessage))]
[JsonSerializable(typeof(BackButtonPressedMessage))]
[JsonSerializable(typeof(HeadlessStateResponse))]
internal partial class MauiJsonContext : JsonSerializerContext
{
}
