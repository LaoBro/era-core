using System;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// EmitPrintOps 几何扩展单测（ADR-0013 决策三）。
/// 验证 BuildPrintOpsForLine 产出的 ButtonRef 携带正确的 col/width，
/// 几何计算与 TerminalDisplayWidth.GetDisplayWidth 一致。
/// 用 TestButtonFactory 创建测试按钮（null console → Generation=0），不依赖真实 console/Config。
/// </summary>
public class OpGeometryTests
{
    // ---------- 单按钮行 ----------

    [Fact]
    public void Single_button_gets_col_zero_and_correct_width()
    {
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Single(ops);
        Assert.Equal("print", ops[0].type);
        var btn = ops[0].button;
        Assert.NotNull(btn);
        Assert.Equal(0, btn!.col);
        Assert.Equal(4, btn.width); // "[OK]" = 4 ASCII chars
    }

    // ---------- 多按钮行（col 累加） ----------

    [Fact]
    public void Multiple_buttons_accumulate_column_position()
    {
        var buttons = TestButtonFactory.CreateButtons(("[OK]", 1), ("[Cancel]", 2));
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Equal(2, ops.Count);

        // 第一个按钮：col=0, width=4 ("[OK]")
        Assert.Equal(0, ops[0].button!.col);
        Assert.Equal(4, ops[0].button!.width);

        // 第二个按钮：col=4（前序 "[OK]" 宽度累加）, width=8 ("[Cancel]")
        Assert.Equal(4, ops[1].button!.col);
        Assert.Equal(8, ops[1].button!.width);
    }

    [Fact]
    public void Three_buttons_accumulate_column_sequentially()
    {
        var buttons = TestButtonFactory.CreateButtons(("[0]", 0), ("[1]", 1), ("[2]", 2));
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Equal(3, ops.Count);
        Assert.Equal(0, ops[0].button!.col); // "[0]" 宽 3
        Assert.Equal(3, ops[0].button!.width);
        Assert.Equal(3, ops[1].button!.col); // "[1]" 累加 col=3
        Assert.Equal(3, ops[1].button!.width);
        Assert.Equal(6, ops[2].button!.col); // "[2]" 累加 col=6
        Assert.Equal(3, ops[2].button!.width);
    }

    // ---------- CJK 双宽字符按钮 ----------

    [Fact]
    public void Cjk_button_width_counts_double_width_chars()
    {
        // "確定" = 2 CJK 字符，每字 2 列 → width=4
        var buttons = new[] { TestButtonFactory.CreateButton("確定", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Single(ops);
        var btn = ops[0].button!;
        Assert.Equal(0, btn.col);
        Assert.Equal(4, btn.width);
    }

    [Fact]
    public void Mixed_ascii_cjk_button_width_sums_correctly()
    {
        // "[確定]" = [ + 確 + 定 + ] = 1 + 2 + 2 + 1 = 6
        var buttons = new[] { TestButtonFactory.CreateButton("[確定]", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Single(ops);
        var btn = ops[0].button!;
        Assert.Equal(0, btn.col);
        Assert.Equal(6, btn.width);
    }

    [Fact]
    public void Cjk_then_ascii_buttons_accumulate_column()
    {
        // "確定" width=4, "[OK]" width=4
        var buttons = TestButtonFactory.CreateButtons(("確定", 1), ("[OK]", 2));
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Equal(2, ops.Count);
        Assert.Equal(0, ops[0].button!.col);
        Assert.Equal(4, ops[0].button!.width);
        Assert.Equal(4, ops[1].button!.col); // 累加前序 CJK 宽度
        Assert.Equal(4, ops[1].button!.width);
    }

    // ---------- 非按钮条目（ButtonRef=null） ----------

    [Fact]
    public void Non_button_entry_produces_null_button_ref()
    {
        var nonButton = TestButtonFactory.CreateNonButton("plain text");
        var line = new ConsoleDisplayLine(new[] { nonButton }, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Single(ops);
        Assert.Null(ops[0].button);
    }

    [Fact]
    public void Space_shape_is_serialized_as_display_padding()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var space = new ConsoleSpacePart(new MinorShift.Emuera.Primitives.EmuRectangleF(0, 0, 18, 18));
        space.SetWidth(new StringMeasure(), 0);
        var button = new ConsoleButtonString(null!, new AConsoleDisplayNode[] { space });
        var line = new ConsoleDisplayLine(new[] { button }, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Single(ops);
        Assert.Equal("  ", ops[0].segments[0].text); // 100% font size = 18px = 2 ASCII columns
    }

    [Fact]
    public void Mixed_button_and_non_button_accumulate_column_through_non_button()
    {
        // [OK](button, width=4) + plain(non-button, width=10) + [Cancel](button, width=8)
        var buttons = new ConsoleButtonString[]
        {
            TestButtonFactory.CreateButton("[OK]", 1),
            TestButtonFactory.CreateNonButton("plain text"), // 10 ASCII chars
            TestButtonFactory.CreateButton("[Cancel]", 2),
        };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Equal(3, ops.Count);

        // 第一个按钮 col=0
        Assert.Equal(0, ops[0].button!.col);
        Assert.Equal(4, ops[0].button!.width);

        // 非按钮：ButtonRef=null，但仍占用列宽 10
        Assert.Null(ops[1].button);

        // 第二个按钮 col=4+10=14
        Assert.Equal(14, ops[2].button!.col);
        Assert.Equal(8, ops[2].button!.width);
    }

    // ---------- 空行 ----------

    [Fact]
    public void Empty_line_produces_no_ops()
    {
        var line = new ConsoleDisplayLine(Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        Assert.Empty(ops);
    }

    // ---------- 几何与 TerminalDisplayWidth 一致性 ----------

    [Fact]
    public void Button_width_matches_terminal_display_width()
    {
        foreach (var text in new[] { "[OK]", "[Cancel]", "確定", "[確定]", "Hello, World!", "★星★" })
        {
            var buttons = new[] { TestButtonFactory.CreateButton(text, input: 1) };
            var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

            var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

            var expected = TerminalDisplayWidth.GetDisplayWidth(text);
            Assert.Equal(expected, ops[0].button!.width);
        }
    }

    // ---------- value/isInteger 保留 ----------

    [Fact]
    public void Button_value_and_is_integer_preserved_with_geometry()
    {
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 42) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(line, "TestFont");

        var btn = ops[0].button!;
        Assert.Equal(42L, (long)btn.value);
        Assert.True(btn.isInteger);
        Assert.Equal(0, btn.col);
        Assert.Equal(4, btn.width);
    }
}
