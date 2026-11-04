using System;

namespace EraCore.Native;

/// <summary>
/// 原生前端的行布局口径（平台无关）。
///
/// <para>全部公式对齐 WinForms 参考实现：
/// 行高 = <c>Config.LineHeight</c>（一次字号同比，没有「行距倍率」）；
/// 行顶按 WinForms 的贴底语义（最后一行下沿贴视口底，短内容整体下沉）；
/// 行内基线取顶对齐（<c>-Ascent</c>），字体度量不参与行推进；
/// 对齐宽度 = <c>Config.DrawableWidth</c> = <c>WindowX - Max(2, FontSize/6)</c>，
/// 文本起点再右移同一个 shift。</para>
/// </summary>
public static class NativeTextLayout
{
    /// <summary>
    /// 实际生效字号：跟随游戏 → 游戏字号；否则用用户绝对值。
    /// 游戏字号非法（≤0）时退回用户字号，避免除零。
    /// </summary>
    public static double EffectiveFontSize(bool followGameFontSize, double userFontSize, double gameFontSize)
    {
        if (gameFontSize <= 0)
            return Math.Max(1, userFontSize);
        return followGameFontSize ? gameFontSize : Math.Max(1, userFontSize);
    }

    /// <summary>字号同比：生效字号 / 游戏字号；游戏字号非法（≤0）时按 1 处理。</summary>
    public static double FontScale(double fontSize, double gameFontSize)
        => fontSize / Math.Max(1, gameFontSize);

    /// <summary>
    /// 行高 = 游戏行高 × 字号同比，下限 1。
    /// WinForms 没有行距倍率，行推进严格等于 <c>Config.LineHeight</c>。
    /// </summary>
    public static double LineHeight(double gameLineHeight, double scale)
        => Math.Max(1, gameLineHeight * scale);

    /// <summary>
    /// WinForms <c>Config.DrawingParam_ShapePositionShift</c>：<c>Max(2, FontSize / 6)</c>（整数截断）。
    /// 既是可绘制区右侧留白，也是所有文本/图像/形状的左侧起点偏移。
    /// </summary>
    public static double HorizontalShift(double gameFontSize)
        => Math.Max(2, Math.Floor(Math.Max(0, gameFontSize) / 6));

    /// <summary>WinForms <c>Config.DrawableWidth</c>：<c>WindowX - ShapePositionShift</c>（单侧）。</summary>
    public static double DrawableWidth(double gameWindowWidth, double gameFontSize)
        => Math.Max(1, gameWindowWidth - HorizontalShift(gameFontSize));

    /// <summary>
    /// 短内容贴底补偿：WinForms 把最后一行下沿固定在视口底，
    /// 内容不足一屏时首行整体下沉 <c>viewportHeight - lineCount × lineHeight</c>。
    /// 内容超出一屏时为 0（正常滚动）。
    /// </summary>
    public static double BottomInset(int lineCount, double lineHeight, double viewportHeight)
        => Math.Max(0, viewportHeight - Math.Max(0, lineCount) * lineHeight);

    /// <summary>
    /// 第 <paramref name="index"/> 行顶边（视口坐标）：
    /// <c>index × lineHeight - scrollY + BottomInset</c>。
    /// 相邻行顶差恒等于行高；贴底时最后一行下沿 = 视口底。
    /// </summary>
    public static double LineTop(int index, double lineHeight, double scrollY, int lineCount, double viewportHeight)
        => index * lineHeight - scrollY + BottomInset(lineCount, lineHeight, viewportHeight);

    /// <summary>相邻两行基线的间距——恒等于行高，与字体 ascent/descent 无关。</summary>
    public static double BaselineSpacing(double lineHeight) => lineHeight;

    /// <summary>
    /// 行内基线偏移（WinForms 顶对齐）：<c>-Ascent</c>。
    /// Skia 的 <c>Ascent</c> 为负值，故 <c>-Ascent</c> 是基线到行顶的距离。
    /// </summary>
    public static double BaselineOffset(double ascent) => -ascent;
}
