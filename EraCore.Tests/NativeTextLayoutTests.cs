using EraCore.Native;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 原生前端的布局口径不变式（对齐 WinForms 参考实现）：
/// 行高只来自 HostMetrics.LineHeight 的一次字号同比，没有行距倍率；
/// 相邻行顶差恒等于行高；短内容贴底；行内基线顶对齐（-Ascent）；
/// 对齐宽度是 Config.DrawableWidth（game width），不是视口宽度。
/// </summary>
public sealed class NativeTextLayoutTests
{
    [Fact]
    public void LineHeight_NoScaling_EqualsGameLineHeight()
    {
        Assert.Equal(19, NativeTextLayout.LineHeight(19, 1));
    }

    [Fact]
    public void FontScale_IsFrontendFontOverGameFont()
    {
        Assert.Equal(1, NativeTextLayout.FontScale(18, 18));
        Assert.Equal(32.0 / 18, NativeTextLayout.FontScale(32, 18), 12);
        // 游戏字号非法时按 1，避免除零。
        Assert.Equal(18, NativeTextLayout.FontScale(18, 0));
    }

    [Fact]
    public void EffectiveFontSize_FollowsGameUnlessUserOptedOut()
    {
        // 默认跟随：游戏 16 覆盖存下来的 18。
        Assert.Equal(16, NativeTextLayout.EffectiveFontSize(true, 18, 16));
        // 用户显式调整后使用绝对值。
        Assert.Equal(18, NativeTextLayout.EffectiveFontSize(false, 18, 16));
        // 未打开游戏（游戏字号非法）退回用户绝对值，避免字号变成 0。
        Assert.Equal(18, NativeTextLayout.EffectiveFontSize(true, 18, 0));
    }

    [Fact]
    public void LineHeight_ScalesWithFontExactlyOnce()
    {
        var scale = NativeTextLayout.FontScale(32, 18);
        Assert.Equal(19 * (32.0 / 18), NativeTextLayout.LineHeight(19, scale), 9);
    }

    [Fact]
    public void AdjacentLineTops_AdvanceExactlyByLineHeight()
    {
        const double lineHeight = 33.78;
        const double scrollY = 12.5;
        const int lineCount = 40;
        const double viewport = 600;

        for (var index = 0; index < 40; index++)
        {
            var delta = NativeTextLayout.LineTop(index + 1, lineHeight, scrollY, lineCount, viewport)
                        - NativeTextLayout.LineTop(index, lineHeight, scrollY, lineCount, viewport);
            Assert.Equal(lineHeight, delta, 12);
        }
    }

    [Fact]
    public void ShortContent_IsBottomAnchoredLikeWinForms()
    {
        // WinForms：pointY = RenderHeight - LineHeight，再按行数上推 → 首行 = H - N×LH。
        const double lineHeight = 19;
        const int lineCount = 10;
        const double viewport = 400;

        Assert.Equal(400 - 10 * 19, NativeTextLayout.LineTop(0, lineHeight, 0, lineCount, viewport));
        Assert.Equal(400 - 19, NativeTextLayout.LineTop(lineCount - 1, lineHeight, 0, lineCount, viewport));
        Assert.Equal(viewport,
            NativeTextLayout.LineTop(lineCount - 1, lineHeight, 0, lineCount, viewport) + lineHeight);
    }

    [Fact]
    public void OverfullContent_FirstLineTopIsZeroWhenScrolledToTop()
    {
        const double lineHeight = 19;
        const int lineCount = 40;
        const double viewport = 400;

        Assert.Equal(0, NativeTextLayout.BottomInset(lineCount, lineHeight, viewport));
        Assert.Equal(0, NativeTextLayout.LineTop(0, lineHeight, 0, lineCount, viewport));
    }

    [Fact]
    public void LineTop_SubtractsScrollOffset()
    {
        Assert.Equal(47, NativeTextLayout.LineTop(3, 19, 10, 40, 400));
    }

    [Fact]
    public void BaselineOffset_IsTopAlignedAndIndependentOfLineHeight()
    {
        // WinForms 顶对齐：point.Y - Ascent（Ascent 为负）。
        Assert.Equal(17.2, NativeTextLayout.BaselineOffset(-17.2), 9);
        // 行高变化不改变行内基线偏移；行推进由 LineTop/LineHeight 单独控制。
        Assert.Equal(
            NativeTextLayout.BaselineOffset(-17.2),
            NativeTextLayout.BaselineOffset(-17.2));
    }

    [Fact]
    public void BaselineSpacing_EqualsLineHeight_RegardlessOfFontMetrics()
    {
        Assert.Equal(19, NativeTextLayout.BaselineSpacing(19));
        Assert.Equal(33.78, NativeTextLayout.BaselineSpacing(33.78), 12);
    }

    [Fact]
    public void HorizontalShift_MatchesWinFormsShapePositionShift()
    {
        // Math.Max(2, FontSize / 6)，整数截断。
        Assert.Equal(2, NativeTextLayout.HorizontalShift(6));
        Assert.Equal(2, NativeTextLayout.HorizontalShift(16));
        Assert.Equal(3, NativeTextLayout.HorizontalShift(18));
        Assert.Equal(4, NativeTextLayout.HorizontalShift(24));
        Assert.Equal(2, NativeTextLayout.HorizontalShift(0));
    }

    [Fact]
    public void DrawableWidth_UsesGameWidthMinusDoubleShift()
    {
        // WinForms Config.DrawableWidth = WindowX - ShapePositionShift × 2。
        Assert.Equal(756, NativeTextLayout.DrawableWidth(760, 16));
        Assert.Equal(754, NativeTextLayout.DrawableWidth(760, 18));
        Assert.Equal(1, NativeTextLayout.DrawableWidth(0, 18));
    }

    [Fact]
    public void LineHeight_ClampsToAtLeastOne()
    {
        Assert.Equal(1, NativeTextLayout.LineHeight(0, 0));
        Assert.Equal(1, NativeTextLayout.LineHeight(-5, 1));
    }
}
