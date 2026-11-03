using System;
using System.Collections.Generic;

namespace EraCore.Hosting;

/// <summary>虚拟滚动输入参数。行高与额外高度均为平台像素；默认固定行高。</summary>
public sealed record VirtualWindowOptions(
    double RowHeight,
    int Overscan = 5,
    IReadOnlyDictionary<int, double>? LineExtras = null);

/// <summary>虚拟滚动输出（纯值，不含 DOM/画布对象）。</summary>
public readonly record struct VirtualWindowState(
    int StartIndex,
    int EndIndexExclusive,
    double OffsetY,
    double TotalHeight,
    double MaxScrollY,
    double ScrollY,
    double ThumbY,
    double ThumbHeight,
    bool IsAtBottom);

/// <summary>
/// 平台无关的虚拟滚动算术：可见区间、overscan、贴底、滚动条比例、shift_head 补偿。
///
/// <para>输入只有 itemCount / scrollY / viewportHeight / rowHeight（可选稀疏额外高度）；
/// 输出只是索引与像素数字。DOM 的 scrollTop/translateY、终端行号、SKCanvas 裁剪都由前端接线。</para>
/// </summary>
public static class VirtualWindow
{
    private const double Epsilon = 0.5;

    public static VirtualWindowState Compute(
        int itemCount,
        double scrollY,
        double viewportHeight,
        VirtualWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var count = Math.Max(0, itemCount);
        var rowHeight = Math.Max(1, options.RowHeight);
        var viewport = Math.Max(0, viewportHeight);
        var overscan = Math.Max(0, options.Overscan);

        var tops = BuildTops(count, rowHeight, options.LineExtras);
        var totalHeight = tops[count];
        var maxScroll = Math.Max(0, totalHeight - viewport);
        var scroll = Math.Clamp(scrollY, 0, maxScroll);

        var firstVisible = FindFirstAtOrAfter(tops, scroll, count);
        if (firstVisible >= count)
            firstVisible = count > 0 ? count - 1 : 0;
        else if (firstVisible > 0 && tops[firstVisible] > scroll)
            firstVisible--;

        var contentBottom = scroll + viewport;
        var endExclusive = FindFirstAtOrAfter(tops, contentBottom, count);
        if (endExclusive < firstVisible + 1 && count > 0)
            endExclusive = Math.Min(count, firstVisible + 1);

        var start = Math.Max(0, firstVisible - overscan);
        var end = Math.Min(count, endExclusive + overscan);
        var offsetY = tops[start];

        double thumbHeight = 0;
        double thumbY = 0;
        if (maxScroll > 0 && totalHeight > 0)
        {
            thumbHeight = Math.Min(viewport, Math.Max(24, viewport * viewport / totalHeight));
            thumbY = (viewport - thumbHeight) * (scroll / maxScroll);
        }

        return new VirtualWindowState(
            start,
            end,
            offsetY,
            totalHeight,
            maxScroll,
            scroll,
            thumbY,
            thumbHeight,
            maxScroll <= 0 || scroll >= maxScroll - Epsilon);
    }

    /// <summary>第 index 行顶部的绝对像素偏移（index == itemCount 时为内容总高）。</summary>
    public static double OffsetOf(int index, int itemCount, VirtualWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var count = Math.Max(0, itemCount);
        var target = Math.Clamp(index, 0, count);
        var rowHeight = Math.Max(1, options.RowHeight);
        var tops = BuildTops(count, rowHeight, options.LineExtras);
        return tops[target];
    }

    /// <summary>
    /// shift_head 补偿：头部删除 <paramref name="removedCount"/> 行后，把 scrollY 减去被删行高，
    /// 保持视口内内容视觉位置不变。调用方只在非贴底时应用。
    /// </summary>
    public static double CompensateShiftHead(
        double scrollY,
        int removedCount,
        int itemCountBeforeRemoval,
        VirtualWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (removedCount <= 0)
            return Math.Max(0, scrollY);
        var removedHeight = OffsetOf(removedCount, itemCountBeforeRemoval, options);
        return Math.Max(0, scrollY - removedHeight);
    }

    private static double[] BuildTops(
        int count,
        double rowHeight,
        IReadOnlyDictionary<int, double>? extras)
    {
        var tops = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            var extra = 0d;
            if (extras is not null && extras.TryGetValue(i, out var value))
                extra = Math.Max(0, value);
            tops[i + 1] = tops[i] + rowHeight + extra;
        }
        return tops;
    }

    /// <summary>第一个 top &gt;= value 的索引；越界返回 count。</summary>
    private static int FindFirstAtOrAfter(double[] tops, double value, int count)
    {
        var lo = 0;
        var hi = count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (tops[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}
