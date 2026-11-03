using System;
using MinorShift.Emuera.Output;

namespace EraCore.Hosting;

/// <summary>
/// 行对齐的纯几何：`left/center/right` → 前导偏移（列或像素）。
/// 字体度量与最终落位由前端负责；这里只给可复用的公式。
/// </summary>
public static class AlignGeometry
{
    public static int OffsetColumns(string? align, int contentColumns, int containerColumns)
    {
        if (contentColumns <= 0 || containerColumns <= 0)
            return 0;
        var spare = containerColumns - contentColumns;
        if (spare <= 0)
            return 0;
        return align switch
        {
            "center" => spare / 2,
            "right" => spare,
            _ => 0,
        };
    }

    public static double OffsetPixels(string? align, double contentWidth, double containerWidth)
    {
        if (contentWidth <= 0 || containerWidth <= 0)
            return 0;
        var spare = containerWidth - contentWidth;
        if (spare <= 0)
            return 0;
        return align switch
        {
            "center" => spare / 2,
            "right" => spare,
            _ => 0,
        };
    }

    /// <summary>快捷入口：直接读 <see cref="DisplayLine.align"/>。</summary>
    public static int OffsetColumns(DisplayLine line, int contentColumns, int containerColumns)
        => OffsetColumns(line.align, contentColumns, containerColumns);
}
