using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using EraCore.Native;
using SkiaSharp;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 逐码位回退链（<c>EraGridMono → EmueraBlock → EraCoreCJK → 系统字体</c>）的行为保护。
///
/// <para>前一半用假覆盖表验证纯策略顺序；后一半直接用 <b>APK 内置的实际 ttf</b> 读 cmap / hmtx，
/// 断言「各码位段落到哪个字体源」与「ASCII / 半角符号 advance 恰为半角格」。字体文件由
/// <c>EraCore.Native.Android/Assets/fonts</c> 链接到测试输出目录，断言对象就是打包进 APK 的那一份。</para>
/// </summary>
public sealed class NativeFontFallbackTests
{
    private static readonly Lazy<FontSet> Fonts = new(LoadFonts, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void Select_PrefersGridWhenEverySourceCovers()
        => Assert.Equal(NativeFontSource.Grid, NativeFontFallback.Select('A', static (_, _) => true));

    [Fact]
    public void Select_FallsThroughToNextCoveringSource()
    {
        Assert.Equal(NativeFontSource.Block,
            NativeFontFallback.Select('\u2588', static (source, _) => source != NativeFontSource.Grid));
        Assert.Equal(NativeFontSource.Cjk,
            NativeFontFallback.Select('漢',
                static (source, _) => source == NativeFontSource.Cjk));
        Assert.Equal(NativeFontSource.System, NativeFontFallback.Select('\uE000', static (_, _) => false));
    }

    [Fact]
    public void Order_IsGridBlockCjk()
    {
        Assert.Equal(
            new[]
            {
                NativeFontSource.Grid,
                NativeFontSource.Block,
                NativeFontSource.Cjk,
            },
            NativeFontFallback.Order);
    }

    [Fact]
    public void RealAssets_ChainMapsCodepointSegmentsToExpectedSource()
    {
        var fonts = Fonts.Value;

        // ASCII 全段 → 网格字体。
        for (var c = (char)0x20; c <= (char)0x7E; c++)
            Assert.Equal(NativeFontSource.Grid, Resolve(fonts, c));

        // 全角框线 / 全角空格 → 网格字体（IPAGothic 有全角那 32 个，EmueraBlock 有半角那 96 个）。
        Assert.Equal(NativeFontSource.Grid, Resolve(fonts, '\u2500'));
        Assert.Equal(NativeFontSource.Grid, Resolve(fonts, '\u3000'));

        // Block Elements 是 IPAGothic 缺字区 → 必须落到 EmueraBlock（Web 回退链的第二级）。
        Assert.Equal(NativeFontSource.Block, Resolve(fonts, '\u2588'));
        Assert.Equal(NativeFontSource.Block, Resolve(fonts, '\u2591'));

        // CJK → 内置 Noto 子集。
        Assert.Equal(NativeFontSource.Cjk, Resolve(fonts, '漢'));
        Assert.Equal(NativeFontSource.Cjk, Resolve(fonts, 'あ'));

        // 三源都不覆盖 → 系统字体。
        Assert.Equal(NativeFontSource.System, Resolve(fonts, '\uE000'));
    }

    [Fact]
    public void RealAssets_GridFontCoversAsciiAndFullWidthSymbols()
    {
        var grid = Fonts.Value.Grid;

        for (var c = (char)0x20; c <= (char)0x7E; c++)
            Assert.NotEqual(0, grid.GetGlyph(c));

        Assert.NotEqual(0, grid.GetGlyph('\u2500')); // ─ 全角框线
        Assert.NotEqual(0, grid.GetGlyph('\u25A0')); // ■ 全角几何
        Assert.NotEqual(0, grid.GetGlyph('\u3000')); // 全角空格
        Assert.Equal(0, grid.GetGlyph('\u2588'));   // █ 不在 IPAGothic，交给 EmueraBlock
    }

    [Fact]
    public void RealAssets_AsciiAdvanceEqualsHalfCell()
    {
        var fonts = Fonts.Value;
        using var grid = new SKFont(fonts.Grid, 40f);

        // 网格字体的 ASCII advance = 0.5em，恰等于 FontSize/2 的半角格。
        foreach (var c in "Aa iIlmW@-|,.'\":;")
            Assert.Equal(20f, grid.MeasureText(c.ToString()), 0.01f);

        // 全角符号 1em = 两个半角格。
        Assert.Equal(40f, grid.MeasureText("\u2500"), 0.01f);
        Assert.Equal(40f, grid.MeasureText("\u3000"), 0.01f);
    }

    [Fact]
    public void RealAssets_BlockSymbolsAdvanceEqualsHalfCell()
    {
        var fonts = Fonts.Value;
        using var block = new SKFont(fonts.Block, 40f);

        Assert.Equal(20f, block.MeasureText("\u2588"), 0.01f); // █ Block Elements
        Assert.Equal(20f, block.MeasureText("\u2591"), 0.01f); // ░ Block Elements
    }

    [Fact]
    public void Builder_SplitsRunsByFontSourceIdentity()
    {
        var fonts = Fonts.Value;
        var line = new NativeLine(
            new List<NativeEntry>
            {
                new(new List<NativeSegment> { new("A\u2588漢", null, false, false, null, false, false) }, null),
            },
            "left");

        var measure = (string text, object typeface, NativeFontVariant variant) =>
            new SKFont((SKTypeface)typeface, 40f).MeasureText(text);

        var layout = NativeLineLayoutBuilder.Build(
            line, 1f, 20f, c => TypefaceOf(fonts, Resolve(fonts, c)), measure, out _, out _);

        Assert.Equal(3, layout.Runs.Count);
        Assert.Same(fonts.Grid, layout.Runs[0].Typeface);
        Assert.Same(fonts.Block, layout.Runs[1].Typeface);
        Assert.Same(fonts.Cjk, layout.Runs[2].Typeface);
    }

    private static NativeFontSource Resolve(FontSet fonts, char c)
        => NativeFontFallback.Select(c, (source, cp) => TypefaceOf(fonts, source).GetGlyph(cp) != 0);

    private static SKTypeface TypefaceOf(FontSet fonts, NativeFontSource source)
        => source switch
        {
            NativeFontSource.Grid => fonts.Grid,
            NativeFontSource.Block => fonts.Block,
            _ => fonts.Cjk,
        };

    private static FontSet LoadFonts()
        => new(
            SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "EraGridMono.ttf")),
            SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "EmueraBlock.ttf")),
            SKTypeface.FromFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "EraCoreCJK.ttf")));

    private sealed record FontSet(SKTypeface Grid, SKTypeface Block, SKTypeface Cjk);
}
