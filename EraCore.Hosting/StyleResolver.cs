using System;
using MinorShift.Emuera.Output;

namespace EraCore.Hosting;

/// <summary>平台无关的文本样式解析结果（颜色可能为 null = 默认文字色）。</summary>
public readonly record struct HostTextStyle(
    string? Color,
    bool Bold,
    bool Italic,
    bool Underline,
    bool Strikeout);

/// <summary>
/// <see cref="PrintSegment"/> → 样式标志。只做语义归一；字体/绘制由前端负责。
/// </summary>
public static class StyleResolver
{
    public static HostTextStyle Resolve(PrintSegment segment, string? defaultColor = null)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return new HostTextStyle(
            segment.color ?? defaultColor,
            segment.bold == true,
            segment.italic == true,
            segment.underline == true,
            segment.strikeout == true);
    }
}
