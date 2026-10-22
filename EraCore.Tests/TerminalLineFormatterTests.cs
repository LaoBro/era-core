using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MinorShift.Emuera;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Tests;
using MinorShift.Emuera.UI.Game;
using Xunit;
using Utils = MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace Emuera.Headless.Tests;

/// <summary>
/// CLI 渲染增强单测（TDD 红-绿）：
/// ① <see cref="TerminalLineFormatter.FormatLineWithAnsi"/> 补 \x1b[4m（underline）/ \x1b[9m（strikeout）；
/// ② <see cref="TerminalLineFormatter.BuildTerminalLine"/> / FormatLineWithAnsi 对
///    ConsoleImagePart / ConsoleRectangleShapePart 按 node.Width / charWidth 补空格占位
///    （与 ConsoleSpacePart 同公式），占位计入 textWidth 并影响对齐。
/// 环境：ConfigData 默认（FontSize=18 → charWidth=9；WindowX=760）。宽度断言基于该默认值。
/// </summary>
public class TerminalLineFormatterTests : IDisposable
{
    private readonly string _root;
    private readonly RootScope _rootScope;
    private readonly IDisposable _scope;

    public TerminalLineFormatterTests()
    {
        // ConsoleImagePart 构造会经 AppContents 读会话根（issue-009：作用域外没有回退），
        // 故夹具自带一个空的临时游戏根。
        _root = Path.Combine(Path.GetTempPath(), "emuera-linefmt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _rootScope = new RootScope(_root, new FileSystemGameDirAccessor());
        _scope = GlobalStatic.OpenScope(new ConfigData(_rootScope.Paths));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _rootScope.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly EmuColor White = new(255, 255, 255);

    private static StringStyle Style(EmuFontStyle fs) => new(White, fs, "TestFont");

    private static ConsoleDisplayLine LineOf(params AConsoleDisplayNode[] nodes)
    {
        var btn = new ConsoleButtonString(null!, nodes);
        return new ConsoleDisplayLine(new[] { btn }, isLogical: true, temporary: false);
    }

    private static Utils.MixedNum Px(int num) => new() { num = num, isPx = true };

    /// <summary>图片节点：构造后直接注入 Width（测试目标是 formatter，不依赖探针/几何）。</summary>
    private static ConsoleImagePart ImageWithWidth(int width)
    {
        var img = new ConsoleImagePart("img/x.png", null, null, Px(50), null, null);
        img.Width = width;
        return img;
    }

    /// <summary>矩形节点：percent% × FontSize(18)。SetWidth 后 Width=18×percent/100。</summary>
    private static ConsoleRectangleShapePart RectPct(int percent)
    {
        var rect = (ConsoleRectangleShapePart)ConsoleShapePart.CreateShape(
            "rect", [new Utils.MixedNum { num = percent }], new EmuColor(255, 0, 0), default, false);
        rect.SetWidth(new StringMeasure(), 0);
        return rect;
    }

    private static void SetAlign(ConsoleDisplayLine line, DisplayLineAlignment align)
    {
        var field = typeof(ConsoleDisplayLine).GetField("align", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(line, align);
    }

    // ---------- 问题①：underline / strikeout ----------

    [Fact]
    public void Underline_style_emits_ansi_4m()
    {
        var line = LineOf(new ConsoleStyledString("X", Style(EmuFontStyle.Underline)));
        var ansi = TerminalLineFormatter.FormatLineWithAnsi(line, null);
        Assert.Contains("\x1b[4m", ansi);
        Assert.DoesNotContain("\x1b[9m", ansi);
    }

    [Fact]
    public void Strikeout_style_emits_ansi_9m()
    {
        var line = LineOf(new ConsoleStyledString("X", Style(EmuFontStyle.Strikeout)));
        var ansi = TerminalLineFormatter.FormatLineWithAnsi(line, null);
        Assert.Contains("\x1b[9m", ansi);
        Assert.DoesNotContain("\x1b[4m", ansi);
    }

    [Fact]
    public void Underline_and_strikeout_both_emitted()
    {
        var line = LineOf(new ConsoleStyledString("X", Style(EmuFontStyle.Underline | EmuFontStyle.Strikeout)));
        var ansi = TerminalLineFormatter.FormatLineWithAnsi(line, null);
        Assert.Contains("\x1b[4m", ansi);
        Assert.Contains("\x1b[9m", ansi);
    }

    [Fact]
    public void Decorations_reset_between_segments_and_at_line_end()
    {
        var line = LineOf(
            new ConsoleStyledString("A", Style(EmuFontStyle.Strikeout)),
            new ConsoleStyledString("B", Style(EmuFontStyle.Regular)));
        var ansi = TerminalLineFormatter.FormatLineWithAnsi(line, null);
        // 段间样式变化必须 reset 后重开（reset 后紧跟颜色码），且 9m 只属于第一段
        Assert.Contains("\x1b[0m\x1b[38", ansi);
        // 行尾必须重置，防止装饰线泄漏到后续行
        Assert.EndsWith("\x1b[0m", ansi);
    }

    [Fact]
    public void Bold_and_italic_still_emitted()
    {
        var line = LineOf(new ConsoleStyledString("X", Style(EmuFontStyle.Bold | EmuFontStyle.Italic)));
        var ansi = TerminalLineFormatter.FormatLineWithAnsi(line, null);
        Assert.Contains("\x1b[1m", ansi);
        Assert.Contains("\x1b[3m", ansi);
    }

    // ---------- 问题②：image / rect 占位 ----------

    [Fact]
    public void Image_part_pads_spaces_in_build()
    {
        var line = LineOf(ImageWithWidth(100), new ConsoleStyledString("T", Style(EmuFontStyle.Regular)));
        string text = TerminalLineFormatter.BuildTerminalLine(line, out int width);
        // 100px / charWidth(9) = 11 列
        Assert.Equal(new string(' ', 11) + "T", text);
        Assert.Equal(12, width);
    }

    [Fact]
    public void Rect_part_pads_spaces_in_build()
    {
        var line = LineOf(RectPct(100), new ConsoleStyledString("T", Style(EmuFontStyle.Regular)));
        string text = TerminalLineFormatter.BuildTerminalLine(line, out int width);
        // 100% × 18px = 18px → 18/9 = 2 列
        Assert.Equal(new string(' ', 2) + "T", text);
        Assert.Equal(3, width);
    }

    [Fact]
    public void Image_and_rect_pad_spaces_in_ansi_path()
    {
        var line = LineOf(ImageWithWidth(100), RectPct(100),
            new ConsoleStyledString("T", Style(EmuFontStyle.Regular)));
        var ansi = TerminalLineFormatter.FormatLineWithAnsi(line, null);
        // 图片 11 列 + 矩形 2 列 = 13 个占位空格在文本段之前（无样式）；
        // 颜色转义码只出现在 "T" 段开头，行尾重置。
        Assert.StartsWith(new string(' ', 13), ansi);
        Assert.EndsWith("T\x1b[0m", ansi);
        Assert.Contains("\x1b[38", ansi);
    }

    [Fact]
    public void Zero_width_image_pads_nothing()
    {
        var line = LineOf(ImageWithWidth(0), new ConsoleStyledString("T", Style(EmuFontStyle.Regular)));
        string text = TerminalLineFormatter.BuildTerminalLine(line, out int width);
        Assert.Equal("T", text);
        Assert.Equal(1, width);
    }

    [Fact]
    public void Image_padding_flows_into_center_align()
    {
        var line = LineOf(ImageWithWidth(100)); // textWidth = 11
        SetAlign(line, DisplayLineAlignment.CENTER);
        string output = TerminalLineFormatter.FormatLineForTerminal(
            line, null, TerminalCharWidthConfig.Default, ansiEnabled: false);
        int gameWidth = TerminalLineFormatter.GetGameColumnWidth();
        int expectedPad = Math.Max((gameWidth - 11) / 2, 0);
        Assert.Equal(new string(' ', expectedPad + 11), output);
    }

    [Fact]
    public void Image_padding_flows_into_right_align()
    {
        var line = LineOf(ImageWithWidth(100)); // textWidth = 11
        SetAlign(line, DisplayLineAlignment.RIGHT);
        string output = TerminalLineFormatter.FormatLineForTerminal(
            line, null, TerminalCharWidthConfig.Default, ansiEnabled: false);
        int gameWidth = TerminalLineFormatter.GetGameColumnWidth();
        int expectedPad = Math.Max(gameWidth - 11, 0);
        Assert.Equal(new string(' ', expectedPad + 11), output);
    }

    [Fact]
    public void AlignOffset_matches_FormatLineForTerminal_for_image_line()
    {
        // 图片(11 列)+ 文本(1 列)= textWidth 12;ComputeAlignOffset(经 BuildSnapshot)
        // 必须与 FormatLineForTerminal 的居中前导空格一致——按钮命中区依赖该契约。
        var line = LineOf(ImageWithWidth(100), new ConsoleStyledString("T", Style(EmuFontStyle.Regular)));
        SetAlign(line, DisplayLineAlignment.CENTER);
        string output = TerminalLineFormatter.FormatLineForTerminal(
            line, null, TerminalCharWidthConfig.Default, ansiEnabled: false);
        int leading = TerminalDisplayWidth.LeadingDisplayWidth(output);

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { line }, EmuColor.Black, ConsoleState.WaitInput, null, "TestFont");

        int gameWidth = TerminalLineFormatter.GetGameColumnWidth();
        int expectedPad = Math.Max((gameWidth - 12) / 2, 0);
        Assert.Equal(expectedPad, snapshot.lines[0].AlignOffset);
        Assert.Equal(expectedPad + 11, leading); // 图片占位在文本之前
    }

    [Fact]
    public void Image_between_same_style_segments_resets_decoration()
    {
        // 中段占位空格不得继承前段 9m：reset 后输出空格，再重开样式渲染 B
        var line = LineOf(
            new ConsoleStyledString("A", Style(EmuFontStyle.Strikeout)),
            ImageWithWidth(100),
            new ConsoleStyledString("B", Style(EmuFontStyle.Strikeout)));
        var ansi = TerminalLineFormatter.FormatLineWithAnsi(line, null);
        // 空格前有 reset、空格后有重开样式码：占位空格不继承前段 9m
        Assert.Contains("\x1b[0m" + new string(' ', 11) + "\x1b[38", ansi);
    }
}
