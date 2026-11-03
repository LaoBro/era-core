using System;
using System.Collections.Generic;
using MinorShift.Emuera.Output;

namespace EraCore.Hosting;

/// <summary>按钮在字符列上的区间（Start 含，EndExclusive 不含）。</summary>
public readonly record struct ColumnSpan(int Start, int EndExclusive)
{
    public int Width => EndExclusive - Start;
    public bool Contains(int column) => column >= Start && column < EndExclusive;
}

/// <summary>一行的按钮命中项；保留 Core 的 value/isInteger/generation 语义。</summary>
public sealed record ColumnHit(
    int EntryIndex,
    ColumnSpan Span,
    object Value,
    bool IsInteger,
    long Generation);

/// <summary>
/// 从 <see cref="DisplayLine"/> / <see cref="ButtonRef"/> 的字符列几何生成命中区。
///
/// <para>只做平台无关的列算术；把列换成像素/终端单元由前端完成。
/// Native 若使用比例字体，应优先用实际测量出的 run 位置，列几何仅作回退/校验。</para>
/// </summary>
public static class ColumnHitGeometry
{
    public static IReadOnlyList<ColumnHit> FromLine(DisplayLine line, int alignOffsetColumns = 0)
    {
        ArgumentNullException.ThrowIfNull(line);
        var hits = new List<ColumnHit>(line.entries.Count);
        for (var i = 0; i < line.entries.Count; i++)
        {
            var button = line.entries[i].button;
            if (button?.col is not { } col || button.width is not { } width || width <= 0)
                continue;

            var start = col + alignOffsetColumns;
            hits.Add(new ColumnHit(
                i,
                new ColumnSpan(start, start + width),
                button.value,
                button.isInteger,
                button.generation));
        }
        return hits;
    }

    /// <summary>命中测试：返回第一个包含该列的按钮；没有则 null。</summary>
    public static ColumnHit? HitTest(DisplayLine line, int column, int alignOffsetColumns = 0)
    {
        foreach (var hit in FromLine(line, alignOffsetColumns))
            if (hit.Span.Contains(column))
                return hit;
        return null;
    }
}
