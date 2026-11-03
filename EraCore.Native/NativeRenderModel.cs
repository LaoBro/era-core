using MinorShift.Emuera.Output;

namespace EraCore.Native;

/// <summary>渲染用的只读片段（由 <see cref="PrintSegment"/> 投影，去掉图像/形状的绘制细节）。</summary>
public sealed record NativeSegment(
    string Text,
    string? Color,
    bool Bold,
    bool Italic,
    string? FontName,
    bool Underline,
    bool Strikeout,
    bool IsImage,
    bool IsShape);

/// <summary>按钮引用（M1 仅保留，M2 用它做命中与提交）。</summary>
public sealed record NativeButton(
    object Value,
    bool IsInteger,
    long Generation,
    int? Col,
    int? Width);

public sealed record NativeEntry(IReadOnlyList<NativeSegment> Segments, NativeButton? Button);

public sealed record NativeLine(IReadOnlyList<NativeEntry> Entries, string? Align);

/// <summary>
/// 一帧可绘制数据：平台无关、不可变，供任意原生渲染后端消费。
///
/// <para>M1 不含图像层/精灵/动画——<c>SegmentImage</c>/<c>SegmentShape</c> 只降级为
/// <see cref="NativeSegment.IsImage"/>/<see cref="NativeSegment.IsShape"/> 标记（暂不绘制），
/// 文本仍按原顺序保留。</para>
/// </summary>
public sealed record NativeFrame(
    IReadOnlyList<NativeLine> Lines,
    string BackgroundColor,
    long Generation,
    int ProtocolVersion,
    long Revision,
    string? EndedTerminal)
{
    public static NativeFrame Empty { get; } =
        new(Array.Empty<NativeLine>(), "#000000", 0, 0, 0, null);
}

/// <summary><see cref="DisplaySnapshot"/> → <see cref="NativeFrame"/> 的纯函数投影。</summary>
public static class NativeRenderModel
{
    public static NativeFrame FromSnapshot(DisplaySnapshot? snapshot)
    {
        if (snapshot is null)
            return NativeFrame.Empty;

        var lines = new List<NativeLine>(snapshot.lines.Count);
        foreach (var line in snapshot.lines)
            lines.Add(ProjectLine(line));

        return new NativeFrame(
            lines,
            string.IsNullOrEmpty(snapshot.bgColor) ? "#000000" : snapshot.bgColor,
            snapshot.generation,
            snapshot.protocolVersion,
            snapshot.revision,
            snapshot.awaiting?.ended?.terminal);
    }

    public static NativeLine ProjectLine(DisplayLine line)
    {
        var entries = new List<NativeEntry>(line.entries.Count);
        foreach (var entry in line.entries)
        {
            var segments = new List<NativeSegment>(entry.segments.Count);
            foreach (var segment in entry.segments)
            {
                segments.Add(new NativeSegment(
                    segment.text,
                    segment.color,
                    segment.bold == true,
                    segment.italic == true,
                    segment.fontname,
                    segment.underline == true,
                    segment.strikeout == true,
                    segment.image is not null,
                    segment.shape is not null));
            }

            var button = entry.button is { } b
                ? new NativeButton(b.value, b.isInteger, b.generation, b.col, b.width)
                : null;

            entries.Add(new NativeEntry(segments, button));
        }

        return new NativeLine(entries, line.align);
    }
}
