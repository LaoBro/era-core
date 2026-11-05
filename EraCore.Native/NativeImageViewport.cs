using System;
using System.Collections.Generic;

namespace EraCore.Native;

/// <summary>
/// 图片层的虚拟滚动选择（纯函数，平台无关）。
///
/// <para>文本仍按行窗口绘制，图片则按 <see cref="NativeFrame.ImageExtentList"/> 里
/// 「锚定行索引 + 行内 op 序号」的天然顺序单独筛选：只保留「图片矩形 ∩ 视口」相交的项，
/// 并分成「锚定行在窗口上方」与「锚定行在窗口下方」两张表。渲染顺序必须是
/// <c>上方表 → 窗口行（图片保持内联在 op 流里）→ 下方表</c>，
/// 这样才与「按行序绘制」的 z 序完全等价；窗口内锚定行的图不得进入两张表，避免重复绘制。</para>
///
/// <para>矩形用 <see cref="NativeTextLayout.LineTop"/> 现算（含贴底补偿），不缓存绝对坐标：
/// 滚动、字号缩放与短内容下沉都只需换入参，不需要重建索引。</para>
/// </summary>
public static class NativeImageViewport
{
    /// <summary>图片上沿（视口坐标）：锚定行顶 + <c>ypos × scale</c>。</summary>
    public static double Top(
        NativeImageExtent extent,
        int lineCount,
        double lineHeight,
        double scrollY,
        double viewportHeight,
        double scale)
    {
        ArgumentNullException.ThrowIfNull(extent);
        return NativeTextLayout.LineTop(extent.LineIndex, lineHeight, scrollY, lineCount, viewportHeight)
            + extent.Image.YPos * scale;
    }

    /// <summary>图片下沿（视口坐标）。</summary>
    public static double Bottom(
        NativeImageExtent extent,
        int lineCount,
        double lineHeight,
        double scrollY,
        double viewportHeight,
        double scale)
        => Top(extent, lineCount, lineHeight, scrollY, viewportHeight, scale)
            + Math.Max(0, extent.Image.Height) * scale;

    /// <summary>
    /// 图片矩形是否与（可扩展的）视口纵向相交。
    /// <paramref name="marginPixels"/> 用于预取余量；0 表示严格相交。
    /// 宽/高为 0 的图不参与（绘制本身也会跳过）。
    /// </summary>
    public static bool Intersects(
        NativeImageExtent extent,
        int lineCount,
        double lineHeight,
        double scrollY,
        double viewportHeight,
        double scale,
        double marginPixels = 0)
    {
        ArgumentNullException.ThrowIfNull(extent);
        var width = extent.Image.Width * scale;
        var height = extent.Image.Height * scale;
        if (width <= 0 || height <= 0)
            return false;

        var top = Top(extent, lineCount, lineHeight, scrollY, viewportHeight, scale);
        return top + height > -marginPixels && top < viewportHeight + marginPixels;
    }

    /// <summary>
    /// 按窗口范围拆分锚定行在窗口外的可见图：
    /// 锚定行 &lt; windowStart 进 <see cref="NativeImageSelection.Above"/>，
    /// 锚定行 ≥ windowEndExclusive 进 <see cref="NativeImageSelection.Below"/>；
    /// 窗口内（含 overscan）一律排除，由行循环内联绘制。
    /// 两张表都保持 extents 的原有（锚定行 + op）顺序。
    /// </summary>
    public static NativeImageSelection Select(
        IReadOnlyList<NativeImageExtent> extents,
        int lineCount,
        double lineHeight,
        double scrollY,
        double viewportHeight,
        double scale,
        int windowStart,
        int windowEndExclusive,
        double marginPixels = 0)
    {
        ArgumentNullException.ThrowIfNull(extents);

        List<NativeImageExtent>? above = null;
        List<NativeImageExtent>? below = null;

        foreach (var extent in extents)
        {
            if (extent.LineIndex >= windowStart && extent.LineIndex < windowEndExclusive)
                continue;
            if (!Intersects(extent, lineCount, lineHeight, scrollY, viewportHeight, scale, marginPixels))
                continue;

            if (extent.LineIndex < windowStart)
                (above ??= new List<NativeImageExtent>()).Add(extent);
            else
                (below ??= new List<NativeImageExtent>()).Add(extent);
        }

        IReadOnlyList<NativeImageExtent> aboveList =
            (IReadOnlyList<NativeImageExtent>?)above ?? Array.Empty<NativeImageExtent>();
        IReadOnlyList<NativeImageExtent> belowList =
            (IReadOnlyList<NativeImageExtent>?)below ?? Array.Empty<NativeImageExtent>();
        return new NativeImageSelection(aboveList, belowList);
    }

    /// <summary>
    /// 整帧绘制计划（纯函数，可直接在 Windows 单测里做序列快照）：
    /// 上方表的每张图 → 窗口内每一行（整行，含内联图片）→ 下方表的每张图。
    /// 渲染器只按这个序列执行，禁止另起「先文字后图片」的两趟式分层。
    /// </summary>
    public static IReadOnlyList<NativeDrawStep> Plan(
        NativeFrame frame,
        double lineHeight,
        double scrollY,
        double viewportHeight,
        double scale,
        int windowStart,
        int windowEndExclusive)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var selection = Select(
            frame.ImageExtentList,
            frame.Lines.Count,
            lineHeight,
            scrollY,
            viewportHeight,
            scale,
            windowStart,
            windowEndExclusive);

        var start = Math.Max(0, windowStart);
        var end = Math.Min(frame.Lines.Count, windowEndExclusive);
        var steps = new List<NativeDrawStep>(selection.Above.Count + selection.Below.Count + Math.Max(0, end - start));

        foreach (var extent in selection.Above)
            steps.Add(NativeDrawStep.ImageOnly(extent));
        for (var index = start; index < end; index++)
            steps.Add(NativeDrawStep.Line(index));
        foreach (var extent in selection.Below)
            steps.Add(NativeDrawStep.ImageOnly(extent));

        return steps;
    }
}

/// <summary>锚定行在窗口外的可见图片（两个列表都保持帧内原有顺序）。</summary>
public sealed record NativeImageSelection(
    IReadOnlyList<NativeImageExtent> Above,
    IReadOnlyList<NativeImageExtent> Below)
{
    public static NativeImageSelection Empty { get; } =
        new(Array.Empty<NativeImageExtent>(), Array.Empty<NativeImageExtent>());

    public bool IsEmpty => Above.Count == 0 && Below.Count == 0;
}

/// <summary>
/// 一个绘制槽：<see cref="DrawLine"/> 为 true 时画整行（文本 + 内联图片），
/// 否则只画 <see cref="Image"/> 这一张（锚定行在窗口外，绝不画该行文本）。
/// </summary>
public readonly record struct NativeDrawStep(int LineIndex, bool DrawLine, NativeImageExtent? Image)
{
    public static NativeDrawStep Line(int lineIndex) => new(lineIndex, true, null);

    public static NativeDrawStep ImageOnly(NativeImageExtent extent) => new(extent.LineIndex, false, extent);
}
