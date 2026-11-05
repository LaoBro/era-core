using System;
using System.Collections.Generic;

namespace EraCore.Native;

/// <summary>字体变体（与 <see cref="NativeSegment.Bold"/>/<see cref="NativeSegment.Italic"/> 组合一一对应）。</summary>
public enum NativeFontVariant
{
    Regular,
    Bold,
    Italic,
    BoldItalic,
}

/// <summary>
/// 一个文本 run 的布局结果。**只存字体身份（<c>Typeface</c> 不透明的稳定标识），不存 <c>SKFont</c>**——
/// 字体由长期池按 (Typeface, Variant, FontSize) 提供，缓存不跨帧持有字体对象。
/// <c>Width</c> 是构建时用同一支字体量测出的实测宽度，命中缓存时直接复用。
/// </summary>
public readonly record struct NativeLayoutRun(
    string Text,
    object Typeface,
    NativeFontVariant Variant,
    float Width,
    string? Color,
    bool Underline,
    bool Strikeout);

/// <summary>
/// 行内一个 op：文本 run（<see cref="RunIndex"/> ≥ 0）或图片 / 形状（两者为 null 且宽度 &gt; 0 只会出现在异常数据）。
/// <see cref="Width"/> 与绘制时的 <c>x += Width</c> 同源，保证命中缓存后前进量一致。
/// </summary>
public readonly record struct NativeLayoutOp(
    float Width,
    int RunIndex,
    NativeImage? Image,
    NativeShape? Shape,
    NativeButton? Button,
    int EntryIndex,
    int SegmentIndex);

/// <summary>entry 的行内起点与命中区宽度（不含 <c>startX</c>/对齐/滚动偏移）。</summary>
public readonly record struct NativeLayoutEntry(float StartX, float Width, NativeButton? Button);

/// <summary>行内某张图片的水平前缀宽度（显示像素，已含缩放；不含 startX/对齐偏移/滚动）。</summary>
public readonly record struct NativeLineImageOffset(int EntryIndex, int SegmentIndex, float PrefixWidth);

/// <summary>
/// 一行的完整按行布局：整行总宽 + op 流 + 文本 run + entry 命中区 + 图片前缀宽。
/// 不可变，可跨帧复用（仅当 frame 引用或 style 全字段变化时由缓存整体丢弃）。
/// </summary>
public sealed record NativeLineLayout(
    float TotalWidth,
    IReadOnlyList<NativeLayoutOp> Ops,
    IReadOnlyList<NativeLayoutRun> Runs,
    IReadOnlyList<NativeLayoutEntry> Entries,
    IReadOnlyList<NativeLineImageOffset> Images)
{
    /// <summary>按 (EntryIndex, SegmentIndex) 找图片的前缀宽度；找不到返回 null。</summary>
    public float? PrefixOf(int entryIndex, int segmentIndex)
    {
        foreach (var image in Images)
        {
            if (image.EntryIndex == entryIndex && image.SegmentIndex == segmentIndex)
                return image.PrefixWidth;
        }
        return null;
    }

    public static NativeLineLayout Empty { get; } = new(
        0f,
        Array.Empty<NativeLayoutOp>(),
        Array.Empty<NativeLayoutRun>(),
        Array.Empty<NativeLayoutEntry>(),
        Array.Empty<NativeLineImageOffset>());
}

/// <summary>
/// <see cref="NativeLine"/> → <see cref="NativeLineLayout"/> 的纯函数构建器。
///
/// <para>平台无关：字体解析与文本量测由调用方注入（Android 侧为 Skia，测试可注入确定性假实现），
/// 因此「走缓存」与「强制重算」的结果可以在 Windows 单测里逐字段对拍。</para>
///
/// <para>累加口径与历史 <c>LayoutLine</c> 完全一致：按 entry → segment 顺序，文本 run（按 typeface
/// 切分）在前，图片其次，形状最后；每个 op 的宽度都累加进整行总宽，图片前缀宽在追加图片 op 之前记录。</para>
/// </summary>
public static class NativeLineLayoutBuilder
{
    /// <summary>
    /// 构建一行布局。<paramref name="resolveTypeface"/> 返回字体的稳定身份对象（同一字体必须返回同一实例），
    /// <paramref name="measureText"/> 用 (文本, 字体身份, 变体) 量测宽度。
    /// <paramref name="measuredRuns"/>/<paramref name="measuredChars"/> 是本次真正量测的规模，供诊断统计。
    /// </summary>
    public static NativeLineLayout Build(
        NativeLine line,
        float scale,
        Func<char, object> resolveTypeface,
        Func<string, object, NativeFontVariant, float> measureText,
        out int measuredRuns,
        out int measuredChars)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(resolveTypeface);
        ArgumentNullException.ThrowIfNull(measureText);

        var ops = new List<NativeLayoutOp>(line.Entries.Count);
        var runs = new List<NativeLayoutRun>();
        var entries = new List<NativeLayoutEntry>(line.Entries.Count);
        var images = new List<NativeLineImageOffset>();
        var total = 0f;
        measuredRuns = 0;
        measuredChars = 0;

        for (var entryIndex = 0; entryIndex < line.Entries.Count; entryIndex++)
        {
            var entry = line.Entries[entryIndex];
            var entryStart = total;

            for (var segmentIndex = 0; segmentIndex < entry.Segments.Count; segmentIndex++)
            {
                var segment = entry.Segments[segmentIndex];
                // 与 Web 一致：有 image/shape 时忽略 text（text 只是 CLI 调试标记，
                // 缺图时 Protocol 仍会带 AltText）。否则会把占位框推到文字右侧，导致看不见。
                var hasVisual = segment.Image is not null || segment.Shape is not null;
                if (!hasVisual && !string.IsNullOrEmpty(segment.Text))
                {
                    var variant = PickVariant(segment);
                    AppendTextRuns(
                        segment, entry.Button, variant, entryIndex, segmentIndex,
                        ops, runs, ref total, resolveTypeface, measureText,
                        ref measuredRuns, ref measuredChars);
                }

                if (segment.Image is { } image)
                {
                    var width = NativeGeometry.ImageWidth(image, scale);
                    // 前缀宽 = 该图片 op 之前所有 op 的累计宽度（追加前记录，与历史 BuildImageLayout 同源）。
                    images.Add(new NativeLineImageOffset(entryIndex, segmentIndex, total));
                    ops.Add(new NativeLayoutOp(width, -1, image, null, entry.Button, entryIndex, segmentIndex));
                    total += width;
                }

                if (segment.Shape is { } shape)
                {
                    // 与 Web 掩膜一致：形状占 x + width，起笔从 x 开始。
                    var width = NativeGeometry.ShapeWidth(shape, scale);
                    ops.Add(new NativeLayoutOp(width, -1, null, shape, entry.Button, entryIndex, segmentIndex));
                    total += width;
                }
            }

            entries.Add(new NativeLayoutEntry(entryStart, total - entryStart, entry.Button));
        }

        return new NativeLineLayout(
            total,
            ops.ToArray(),
            runs.ToArray(),
            entries.ToArray(),
            images.ToArray());
    }

    /// <summary>
    /// 按码点选择「内置字体 → 系统回退字体」，把文本切成同字体的连续 run。
    /// 与历史 <c>SplitByTypeface</c> 的切分口径一致：逐 UTF-16 单元解析，身份变化处断开。
    /// </summary>
    private static void AppendTextRuns(
        NativeSegment segment,
        NativeButton? button,
        NativeFontVariant variant,
        int entryIndex,
        int segmentIndex,
        List<NativeLayoutOp> ops,
        List<NativeLayoutRun> runs,
        ref float total,
        Func<char, object> resolveTypeface,
        Func<string, object, NativeFontVariant, float> measureText,
        ref int measuredRuns,
        ref int measuredChars)
    {
        var text = segment.Text;
        var start = 0;
        var current = resolveTypeface(text[0]);
        for (var i = 1; i < text.Length; i++)
        {
            var typeface = resolveTypeface(text[i]);
            if (ReferenceEquals(typeface, current))
                continue;
            AppendRun(segment, button, variant, entryIndex, segmentIndex,
                text.Substring(start, i - start), current,
                ops, runs, ref total, measureText, ref measuredRuns, ref measuredChars);
            start = i;
            current = typeface;
        }
        AppendRun(segment, button, variant, entryIndex, segmentIndex,
            text.Substring(start), current,
            ops, runs, ref total, measureText, ref measuredRuns, ref measuredChars);
    }

    private static void AppendRun(
        NativeSegment segment,
        NativeButton? button,
        NativeFontVariant variant,
        int entryIndex,
        int segmentIndex,
        string text,
        object typeface,
        List<NativeLayoutOp> ops,
        List<NativeLayoutRun> runs,
        ref float total,
        Func<string, object, NativeFontVariant, float> measureText,
        ref int measuredRuns,
        ref int measuredChars)
    {
        var width = measureText(text, typeface, variant);
        var runIndex = runs.Count;
        runs.Add(new NativeLayoutRun(
            text,
            typeface,
            variant,
            width,
            segment.Color,
            segment.Underline,
            segment.Strikeout));
        ops.Add(new NativeLayoutOp(
            width, runIndex, null, null, button, entryIndex, segmentIndex));
        total += width;
        measuredRuns++;
        measuredChars += text.Length;
    }

    private static NativeFontVariant PickVariant(NativeSegment segment)
    {
        if (segment.Bold)
            return segment.Italic ? NativeFontVariant.BoldItalic : NativeFontVariant.Bold;
        return segment.Italic ? NativeFontVariant.Italic : NativeFontVariant.Regular;
    }
}
