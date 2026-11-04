using EraCore.Native;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 原生前端行高/基线口径的不变式：行高只来自 HostMetrics.LineHeight 的一次同比缩放，
/// 相邻基线间距必须等于行高；字体度量只决定行内基线位置，不参与行推进。
/// </summary>
public sealed class NativeTextLayoutTests
{
    [Fact]
    public void LineHeight_NoScalingUnitSpacing_EqualsGameLineHeight()
    {
        Assert.Equal(19, NativeTextLayout.LineHeight(19, 1, 1));
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
    public void LineHeight_ScalesWithFontOnceThenLineSpacing()
    {
        var fontSize = 32.0;
        var gameFontSize = 18.0;
        var scale = NativeTextLayout.FontScale(fontSize, gameFontSize);

        // 19 × (32/18) × 1.00 = 33.78；再 × 行距 2.00 = 67.56。
        Assert.Equal(19 * (32.0 / 18), NativeTextLayout.LineHeight(19, scale, 1), 9);
        Assert.Equal(19 * (32.0 / 18) * 2, NativeTextLayout.LineHeight(19, scale, 2), 9);
    }

    [Fact]
    public void AdjacentBaselines_AdvanceExactlyByLineHeight()
    {
        const double lineHeight = 33.78;
        const double scrollY = 12.5;

        for (var index = 0; index < 20; index++)
        {
            var delta = NativeTextLayout.LineTop(index + 1, lineHeight, scrollY)
                        - NativeTextLayout.LineTop(index, lineHeight, scrollY);
            Assert.Equal(lineHeight, delta, 12);
        }
    }

    [Fact]
    public void FirstLineTop_IsZeroAtTopScroll()
    {
        Assert.Equal(0, NativeTextLayout.LineTop(0, 19, 0));
    }

    [Fact]
    public void LineTop_SubtractsScrollOffset()
    {
        Assert.Equal(47, NativeTextLayout.LineTop(3, 19, 10));
    }

    [Fact]
    public void BaselineSpacing_EqualsLineHeight_RegardlessOfFontMetrics()
    {
        // 同字号不同字体度量（HHEA vs OS/2）只影响行内基线，不影响行推进。
        var tall = NativeTextLayout.BaselineOffset(19, -17.2, 4.3);
        var shortGlyph = NativeTextLayout.BaselineOffset(19, -13.0, 3.0);
        Assert.NotEqual(tall, shortGlyph);

        Assert.Equal(19, NativeTextLayout.BaselineSpacing(19));
        Assert.Equal(19, NativeTextLayout.BaselineSpacing(NativeTextLayout.BaselineSpacing(19)));
    }

    [Fact]
    public void BaselineOffset_CentersInkInsideLineCell()
    {
        // 墨迹高度 = descent - ascent = 21.5；格高 19 → 上下各溢出 1.25，基线 = 19/2 + 21.5/2。
        Assert.Equal(15.95, NativeTextLayout.BaselineOffset(19, -17.2, 4.3), 9);
    }

    [Fact]
    public void LineHeight_ClampsToAtLeastOne()
    {
        Assert.Equal(1, NativeTextLayout.LineHeight(0, 0, 0));
        Assert.Equal(1, NativeTextLayout.LineHeight(-5, 1, 1));
    }
}
