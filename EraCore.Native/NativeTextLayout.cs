using System;

namespace EraCore.Native;

/// <summary>
/// 原生前端的行高与基线几何唯一口径。
///
/// <para>行高来源固定为协议下发的 <c>HostMetrics.LineHeight</c>：只做一次「字号同比」缩放
/// （与 Web 的 <c>effectiveScale</c> 同口径），再乘以用户行距倍率。绘制侧逐行必须按该行高
/// 推进（相邻基线间距 == 行高）；字体度量只参与行内垂直居中（ascent 决定基线在格子里的
/// 位置），不得用来替代行推进。</para>
/// </summary>
public static class NativeTextLayout
{
    /// <summary>字号同比：前端字号 / 游戏字号；游戏字号非法（≤0）时按 1 处理。</summary>
    public static double FontScale(double fontSize, double gameFontSize)
        => fontSize / Math.Max(1, gameFontSize);

    /// <summary>行高 = 游戏行高 × 字号同比 × 行距倍率（倍率由调用方钳制），下限 1。</summary>
    public static double LineHeight(double gameLineHeight, double scale, double lineSpacing)
        => Math.Max(1, gameLineHeight * scale * lineSpacing);

    /// <summary>第 <paramref name="index"/> 行顶边（视口坐标）：<c>index * lineHeight - scrollY</c>。</summary>
    public static double LineTop(int index, double lineHeight, double scrollY)
        => index * lineHeight - scrollY;

    /// <summary>相邻两行基线的间距——恒等于行高，与字体 ascent/descent 无关。</summary>
    public static double BaselineSpacing(double lineHeight) => lineHeight;

    /// <summary>
    /// 行内基线偏移：把 <c>ascent..descent</c> 的字体墨迹在行高格子里垂直居中，
    /// 再取 ascent 得到相对行顶的基线位置。格子高度本身不参与该计算之外的行推进。
    /// </summary>
    public static double BaselineOffset(double lineHeight, double ascent, double descent)
        => (lineHeight - (descent - ascent)) / 2 - ascent;
}
