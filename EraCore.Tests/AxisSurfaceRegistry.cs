using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Sessions;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 一个能力轴的公共面声明：模块目录、命名空间、宿主门面与 public 类型白名单。
/// <para>
/// 白名单是唯一真值：各模块的 <c>*PublicSurfaceTests</c> 与 issue-010 的聚合守卫都引用这里。
/// 新增公开类型必须显式登记，否则守卫转红。
/// </para>
/// </summary>
internal sealed record AxisSurface(
    string Axis,
    string Namespace,
    string Directory,
    Type Facade,
    string[] AllowedPublicTypes)
{
    public IEnumerable<Type> PublicTypes()
        => Facade.Assembly.GetTypes()
            .Where(t => t.Namespace == Namespace && t.IsPublic);
}

/// <summary>
/// 三轴 + 会话组装根的公共面白名单（issue-002 / 004 / 005 / 006 出生，issue-010 聚合）。
/// </summary>
internal static class AxisSurfaceRegistry
{
    public static readonly AxisSurface Input = new(
        "Input",
        "MinorShift.Emuera.Input",
        "Input",
        typeof(InputPort),
        [
            "InputCapabilities",
            "InputCapability",
            "InputFrame",
            "InputGeometry",
            "InputKeyEvent",
            "InputPort",
        ]);

    public static readonly AxisSurface Output = new(
        "Output",
        "MinorShift.Emuera.Output",
        "Output",
        typeof(OutputPort),
        [
            "AppendLinesOp",
            "AwaitEnded",
            "AwaitInfo",
            "AwaitInput",
            "AwaitWait",
            "BgImageState",
            "ButtonRef",
            "ClearLineDiffOp",
            "ClearScreenOp",
            "DisplayDiff",
            "DisplayEntry",
            "DisplayLine",
            "DisplaySnapshot",
            "ErrorInfo",
            "LineOp",
            "OutputPort",
            "PrintSegment",
            "SegmentCrop",
            "SegmentImage",
            "SegmentShape",
            "ShiftHeadLineOp",
            "Turn",
            "TurnRecord",
        ]);

    public static readonly AxisSurface Files = new(
        "Files",
        "MinorShift.Emuera.Files",
        "Files",
        typeof(GameTree),
        [
            "AppDataPaths",
            "FileSystemGameDirAccessor",
            "GameLibrary",
            "GameTree",
            "IGameDirAccessor",
        ]);

    public static readonly AxisSurface Sessions = new(
        "Sessions",
        "MinorShift.Emuera.Sessions",
        "Sessions",
        typeof(CoreSession),
        [
            "CoreSession",
            "CoreSessionOptions",
            "CoreSessionStartResult",
            "CoreSessionState",
        ]);

    public static readonly IReadOnlyList<AxisSurface> All = [Input, Output, Files, Sessions];
}
