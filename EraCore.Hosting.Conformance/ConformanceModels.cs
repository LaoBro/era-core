using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EraCore.Hosting.Conformance;

/// <summary>
/// 语言无关的协议形状：与 <c>turn-protocol.md</c> 的 DTO 字段逐项对齐。
/// Core 的 DTO 构造器是 internal，故夹具与适配器之间用这些可构造的镜像类型承载。
/// </summary>
public sealed record ConformanceSnapshot
{
    public IReadOnlyList<ConformanceLine> Lines { get; init; } = Array.Empty<ConformanceLine>();
    public string? BgColor { get; init; }
    public IReadOnlyList<ConformanceBgImage>? BgImages { get; init; }
    public ConformanceAwaiting Awaiting { get; init; } = new();
    public int ProtocolVersion { get; init; }
    public long Generation { get; init; }
    public long Revision { get; init; }
}

/// <summary>一条完整回合帧（字段名对齐 <c>TurnRecord</c>）。</summary>
public sealed record ConformanceTurn
{
    public ConformanceAwaiting Awaiting { get; init; } = new();
    public ConformanceDiff? Diff { get; init; }
    public string? Error { get; init; }
    public ConformanceErrorInfo? ErrorInfo { get; init; }
    public int? ProtocolVersion { get; init; }
    public long Generation { get; init; }
    public long Revision { get; init; }

    public bool HasUnknownAwaitingKeys => Awaiting.HasUnknownKeys;
    public bool IsTerminal => Awaiting.Ended is not null;
}

public sealed record ConformanceDiff
{
    public IReadOnlyList<ConformanceLineOp> LineOps { get; init; } = Array.Empty<ConformanceLineOp>();
    public string? BgColor { get; init; }
    public IReadOnlyList<ConformanceBgImage>? BgImages { get; init; }
}

/// <summary>
/// lineOps 的统一形状：<c>type</c> 为 append/clear_line_diff/clear_screen/shift_head。
/// 未用字段保持 null，语言无关。
/// </summary>
public sealed record ConformanceLineOp
{
    public string Type { get; init; } = string.Empty;
    public IReadOnlyList<ConformanceLine>? NewLines { get; init; }
    public int? ClearCount { get; init; }
    public int? Count { get; init; }
}

public sealed record ConformanceLine
{
    public IReadOnlyList<ConformanceEntry> Entries { get; init; } = Array.Empty<ConformanceEntry>();
    public string? Align { get; init; }
    public bool IsLineEnd { get; init; } = true;

    public string Text => string.Concat(Entries.SelectMany(e => e.Segments).Select(s => s.Text ?? string.Empty));
}

public sealed record ConformanceEntry
{
    public IReadOnlyList<ConformanceSpan> Segments { get; init; } = Array.Empty<ConformanceSpan>();
    public ConformanceButton? Button { get; init; }
}

/// <summary>PrintSegment 的镜像。</summary>
public sealed record ConformanceSpan
{
    public string? Text { get; init; }
    public string? Color { get; init; }
    public bool? Bold { get; init; }
    public bool? Italic { get; init; }
    public string? FontName { get; init; }
    public bool? Underline { get; init; }
    public bool? Strikeout { get; init; }
    public ConformanceImage? Image { get; init; }
    public ConformanceShape? Shape { get; init; }
}

public sealed record ConformanceImage
{
    public string? Src { get; init; }
    public string? Srcb { get; init; }
    public string? Srcm { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Ypos { get; init; }
    public ConformanceCrop? Crop { get; init; }
}

public sealed record ConformanceCrop
{
    public int X { get; init; }
    public int Y { get; init; }
    public int ImgWidth { get; init; }
    public int ImgHeight { get; init; }
}

public sealed record ConformanceShape
{
    public string? Type { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string? Color { get; init; }
}

public sealed record ConformanceBgImage
{
    public string Src { get; init; } = string.Empty;
    public long Depth { get; init; }
    public float Opacity { get; init; }
}

/// <summary>ButtonRef 的镜像。value 保留 JSON 原值，按 isInteger 还原为 long/string。</summary>
public sealed record ConformanceButton
{
    public JsonElement Value { get; init; }
    public bool IsInteger { get; init; }
    public long Generation { get; init; }
    public int? Col { get; init; }
    public int? Width { get; init; }

    public object ToValue()
    {
        switch (Value.ValueKind)
        {
            case JsonValueKind.Number:
                return Value.TryGetInt64(out var integer) ? integer : Value.GetDouble();
            case JsonValueKind.String:
                var text = Value.GetString() ?? string.Empty;
                return IsInteger && long.TryParse(text, out var parsed) ? parsed : text;
            case JsonValueKind.True:
            case JsonValueKind.False:
                return Value.GetBoolean();
            default:
                return Value.ToString();
        }
    }
}

public sealed record ConformanceErrorInfo
{
    public string Type { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public ConformanceErrorInfo? Inner { get; init; }
}

/// <summary>
/// awaiting 的镜像。额外键经 <see cref="Unknown"/> 捕获——未知键必须被宿主观测到并回退，
/// 不能被静默丢弃（§2）。
/// </summary>
public sealed record ConformanceAwaiting
{
    public ConformanceAwaitInput? Input { get; init; }
    public ConformanceAwaitWait? Wait { get; init; }
    public ConformanceAwaitEnded? Ended { get; init; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Unknown { get; init; }

    public bool HasUnknownKeys => Unknown is { Count: > 0 };
}

public sealed record ConformanceAwaitInput
{
    public string Type { get; init; } = string.Empty;
}

public sealed record ConformanceAwaitWait
{
    public long DurationMs { get; init; }
    public bool? DisplayTime { get; init; }
    public string? TimeUpMessage { get; init; }
    public bool? TimedOut { get; init; }
}

public sealed record ConformanceAwaitEnded
{
    public string Terminal { get; init; } = string.Empty;
}

/// <summary>宿主投影后的 awaiting 输入界面状态（与 Hosting 的 <c>AwaitingView</c> 同义）。</summary>
public sealed record AwaitingUiState(
    string Mode,
    string? InputType,
    bool IsWaitingForInput,
    bool RequiresValue,
    bool AllowsEmpty,
    bool IsTerminal,
    string? Terminal,
    long? WaitDurationMs,
    bool DisplayCountdown,
    string? TimeUpMessage,
    bool TimedOut,
    long? RemainingMs,
    bool HasUnknownKeys)
{
    public static AwaitingUiState Empty { get; } = new(
        "None", null, false, false, true, false, null, null, false, null, false, null, false);

    public bool AdvancesOnAnyClick => Mode is "AnyKey" or "EnterKey";

    public bool CanSubmitButton(long buttonGeneration, long currentGeneration)
    {
        if (!IsWaitingForInput || Mode is "AnyKey" or "EnterKey" or "Ended" or "Unknown")
            return false;
        return buttonGeneration == currentGeneration;
    }

    /// <summary>与 <c>AwaitingView.From</c> 相同的判据；未知键保守降级为不可交互。</summary>
    public static AwaitingUiState From(
        ConformanceAwaiting awaiting,
        DateTimeOffset? waitStartedAt = null,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(awaiting);

        if (awaiting.Ended is { } ended)
        {
            return new AwaitingUiState(
                "Ended", null, false, false, true, true, ended.Terminal,
                null, false, null, false, null, false);
        }

        var inputType = awaiting.Input?.Type;
        var mode = inputType switch
        {
            "IntValue" or "StrValue" => "Text",
            "AnyValue" => "Text",
            "AnyKey" => "AnyKey",
            "EnterKey" => "EnterKey",
            null => "None",
            _ => "ButtonOnly",
        };
        var requiresValue = inputType is "IntValue" or "StrValue";
        var allowsEmpty = !requiresValue;

        var wait = awaiting.Wait;
        var duration = wait?.DurationMs;
        var displayCountdown = duration is > 0 && wait?.DisplayTime == true;
        var timedOut = wait?.TimedOut == true;
        var timeUpMessage = timedOut ? wait?.TimeUpMessage : null;

        long? remaining = null;
        if (duration is > 0 && waitStartedAt is { } started)
        {
            var elapsed = ((now ?? DateTimeOffset.UtcNow) - started).TotalMilliseconds;
            remaining = Math.Max(0, (long)(duration.Value - elapsed));
        }

        var unknown = awaiting.HasUnknownKeys;
        return new AwaitingUiState(
            unknown ? "Unknown" : mode,
            inputType,
            unknown ? false : inputType is not null,
            requiresValue,
            allowsEmpty,
            false,
            null,
            duration,
            displayCountdown,
            timeUpMessage,
            timedOut,
            remaining,
            unknown);
    }
}

/// <summary>渲染后的一行（适配器投影结果）。</summary>
public sealed record RenderedSegment(
    string Text,
    string? Color,
    bool Bold,
    bool Italic,
    string? FontName,
    bool Underline,
    bool Strikeout,
    bool IsImage,
    bool IsShape);

public sealed record RenderedButton(object Value, bool IsInteger, long Generation, int? Col, int? Width);

public sealed record RenderedLine(
    IReadOnlyList<RenderedSegment> Segments,
    string? Align,
    IReadOnlyList<RenderedButton> Buttons)
{
    public string Text => string.Concat(Segments.Select(s => s.Text));
}

/// <summary>按钮命中结果。<see cref="IsLive"/> 表示 generation 与当前回合一致且处于可交互等待。</summary>
public sealed record ButtonHit(
    int Column,
    int Row,
    object Value,
    bool IsInteger,
    long Generation,
    bool IsLive);

/// <summary>结构化告警：Code 稳定可断言，Detail 供人读。</summary>
public sealed record HostWarning(string Code, string Detail);
