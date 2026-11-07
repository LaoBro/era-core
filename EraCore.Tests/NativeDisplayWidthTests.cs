using System;
using System.Collections.Generic;
using EraCore.Hosting;
using EraCore.Native;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 对齐宽度口径的回归保护：WinForms/协议按「字符列 × FontSize/2」计算行宽
/// （<c>StringMeasure.GetDisplayLength</c>、<c>ConsoleDisplayLine.SetAlignment</c>），
/// 原生前端若拿字体实测 advance 来算，比例字体的 ASCII 会偏窄，导致：
/// - 同为固定列数的字符画每行得到不同的居中偏移 → 左边缘参差；
/// - 整行横线整段右移 → 贴不到左边。
/// </summary>
public sealed class NativeDisplayWidthTests
{
    private static readonly object Latin = new();
    private static readonly object Cjk = new();

    /// <summary>模拟比例字体：ASCII 5px/字（应占 8px 格），全角 16px/字。</summary>
    private static float Measure(string text, object typeface, NativeFontVariant variant)
        => text.Length * (ReferenceEquals(typeface, Cjk) ? 16f : 5f);

    private static object ResolveTypeface(char c) => c >= 0x3000 ? Cjk : Latin;

    private static NativeLineLayout BuildLine(string text, float halfCellWidth = 8f)
    {
        var line = new NativeLine(
            new List<NativeEntry> { new(new List<NativeSegment> { new(text, null, false, false, null, false, false) }, null) },
            "center");
        return NativeLineLayoutBuilder.Build(
            line, 1f, halfCellWidth, ResolveTypeface, Measure, out _, out _);
    }

    [Theory]
    [InlineData('A', false)]
    [InlineData(' ', false)]
    [InlineData(',', false)]
    [InlineData('あ', true)]
    [InlineData('漢', true)]
    [InlineData('\u3000', true)] // 全角空格
    [InlineData('\uFF21', true)] // 全角 A
    [InlineData('\u2501', true)] // ━ Box Drawing：游戏按全角
    [InlineData('\u25CF', true)] // ● Geometric：游戏按全角
    [InlineData('\u2605', true)] // ★ Misc Symbols：游戏按全角
    [InlineData('\u2588', false)] // █ Block Elements：游戏按半角
    [InlineData('\u2591', false)] // ░ Block Elements：游戏按半角
    public void IsWideChar_MatchesGameGrid(char c, bool wide)
        => Assert.Equal(wide, NativeDisplayWidth.IsWideChar(c));

    [Fact]
    public void Columns_CountsHalfAndWide()
    {
        Assert.Equal(7, NativeDisplayWidth.Columns("\u3000\u3000abc"));
        Assert.Equal(112, NativeDisplayWidth.Columns(new string('-', 112)));
        Assert.Equal(0, NativeDisplayWidth.Columns(null));
    }

    [Fact]
    public void Pixels_UsesFontSizeHalfCell()
    {
        Assert.Equal(896d, NativeDisplayWidth.Pixels(new string('-', 112), 8));
        Assert.Equal(816d, NativeDisplayWidth.Pixels(new string('-', 102), 8));
    }

    [Fact]
    public void Builder_GridWidth_UsesColumns_NotMeasuredAdvance()
    {
        // 2 个全角空格 + "abc" = 7 列；字体实测 2×16 + 3×5 = 47px，网格宽 7×8 = 56px。
        var layout = BuildLine("\u3000\u3000abc");

        Assert.Equal(47f, layout.TotalWidth);
        Assert.Equal(56f, layout.GridWidth);
    }

    [Fact]
    public void CenterOffset_StableForEqualColumnArtLines()
    {
        // TK 开始页 16 行字符画都是 102 列（尾部用半角/全角空格补齐）：
        // 列宽相同 → WinForms 逐行居中的偏移相同 → 左边缘对齐。
        var allHalf = BuildLine(new string(' ', 102));
        var padded = BuildLine("\u3000" + new string(' ', 100));

        Assert.Equal(102 * 8, NativeDisplayWidth.Columns(new string(' ', 102)) * 8);
        Assert.Equal(allHalf.GridWidth, padded.GridWidth);
        Assert.NotEqual(allHalf.TotalWidth, padded.TotalWidth);

        var drawableWidth = 898; // WindowX 900 - ShapePositionShift 2
        Assert.Equal(41d, AlignGeometry.OffsetPixels("center", allHalf.GridWidth, drawableWidth));
        Assert.Equal(41d, AlignGeometry.OffsetPixels("center", padded.GridWidth, drawableWidth));
    }

    [Fact]
    public void CenterOffset_DividerTouchesLeftEdge()
    {
        // DRAWLINEFORM '-' 生成 112 个 '-'：WinForms 偏移 1px（再叠加 Inset 2），几乎贴左。
        var divider = BuildLine(new string('-', 112));
        Assert.Equal(896f, divider.GridWidth);
        Assert.Equal(1d, AlignGeometry.OffsetPixels("center", divider.GridWidth, 898));
        // 用字体实测 advance 时偏移会被放大（模拟比例字体 5px/字 → 169px），整段横线右移。
        Assert.Equal(169d, AlignGeometry.OffsetPixels("center", divider.TotalWidth, 898));
    }

    [Fact]
    public void CenterOffset_FullWidthOnlyLineUnchanged()
    {
        // 纯全角行：字体本身 1em，实测宽与网格宽一致，修复前后偏移相同。
        var layout = BuildLine("漢字かな");
        Assert.Equal(layout.TotalWidth, layout.GridWidth);
        Assert.Equal(4 * 16f, layout.TotalWidth);
        Assert.Equal(417d, AlignGeometry.OffsetPixels("center", layout.GridWidth, 898));
        Assert.Equal(417d, AlignGeometry.OffsetPixels("center", layout.TotalWidth, 898));
    }

    [Fact]
    public void LeftAlign_OffsetIsZero()
    {
        var layout = BuildLine(new string('-', 112));
        Assert.Equal(0d, AlignGeometry.OffsetPixels("left", layout.GridWidth, 898));
    }
}
