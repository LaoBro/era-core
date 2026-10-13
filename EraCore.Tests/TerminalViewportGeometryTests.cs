using System;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 M4：CLI 终端行列 ↔ 游戏像素的换算。
/// <para>
/// 🔒 <b>本文件是「<c>VisibleHeight</c> 必须是渲染行数 × 行高、不能是 <c>Config.WindowY</c>」的回归锚点</b>。
/// 真机表现：终端 33 行时按钮「经常点不到」。推导如下 ——
/// <list type="bullet">
/// <item>引擎用 <c>H</c> 同时算 <c>topLineNo = bottomLineNo − H/LH</c> 与 <c>relPointY = pointY − H</c>；</item>
/// <item>若 <c>H</c> 固定 480（25 行）而终端渲染 32 行，多出的 7 行落到 <c>y &lt; 0</c>；</item>
/// <item><c>ClientRectangle.Contains</c> 把 <c>y &lt; 0</c> 挡掉 ⇒ <c>MoveMouse</c> 不跑 ⇒ 顶部按钮永久点不到。</item>
/// </list>
/// 取 <c>H = 渲染行数 × LH</c> 后，<c>y = row × LH</c> 自洽：代入得
/// <c>relPointY = (row − R)·LH + LH·(R − row) = 0</c>，<b>与 row 无关</b> ⇒ 恒定命中。
/// </para>
/// </summary>
public sealed class TerminalViewportGeometryTests : IDisposable
{
    private readonly IDisposable _scope;

    public TerminalViewportGeometryTests() => _scope = GlobalStatic.OpenScope(new ConfigData());

    public void Dispose() => _scope.Dispose();

    private static int LH => Config.LineHeight;      // 19

    // ---------- VisibleLines ----------

    [Fact]
    public void VisibleLines_normal_mode_reserves_one_row()
        => Assert.Equal(28, TerminalViewportGeometry.VisibleLines(terminalHeight: 29, scrollOffset: 0));

    [Fact]
    public void VisibleLines_scroll_mode_reserves_two_rows()
        => Assert.Equal(27, TerminalViewportGeometry.VisibleLines(terminalHeight: 29, scrollOffset: 5));

    /// <summary>VT 屏幕尚未建立 ⇒ 退化为游戏视口行数（<c>WindowY / LineHeight</c>）。</summary>
    [Fact]
    public void VisibleLines_without_screen_falls_back_to_game_viewport()
        => Assert.Equal(Config.WindowY / LH, TerminalViewportGeometry.VisibleLines(terminalHeight: 0, scrollOffset: 0));

    // ---------- StartLine ----------

    /// <summary>内容多于一屏：终端第 0 行<b>不是</b>内容第 0 行。</summary>
    [Fact]
    public void StartLine_long_content_skips_earlier_lines()
        => Assert.Equal(72, TerminalViewportGeometry.StartLine(lineCount: 100, terminalHeight: 29, scrollOffset: 0));

    [Fact]
    public void StartLine_short_content_is_zero()
        => Assert.Equal(0, TerminalViewportGeometry.StartLine(lineCount: 4, terminalHeight: 29, scrollOffset: 0));

    // ---------- RenderedLines ----------

    [Fact]
    public void RenderedLines_short_content_is_line_count()
        => Assert.Equal(4, TerminalViewportGeometry.RenderedLines(lineCount: 4, terminalHeight: 29, scrollOffset: 0));

    [Fact]
    public void RenderedLines_long_content_is_capped_by_terminal()
        => Assert.Equal(28, TerminalViewportGeometry.RenderedLines(lineCount: 100, terminalHeight: 29, scrollOffset: 0));

    [Fact]
    public void RenderedLines_empty_content_is_zero()
        => Assert.Equal(0, TerminalViewportGeometry.RenderedLines(lineCount: 0, terminalHeight: 29, scrollOffset: 0));

    // ---------- VisibleHeight ----------

    [Fact]
    public void VisibleHeight_is_rendered_lines_times_line_height()
        => Assert.Equal(4 * LH,
            TerminalViewportGeometry.VisibleHeightPixels(lineCount: 4, terminalHeight: 29, scrollOffset: 0));

    /// <summary>
    /// 🔒 <b>核心不变量</b>：终端比游戏窗口高时 <c>VisibleHeight</c> 必须<b>大于</b>
    /// <c>Config.WindowY</c> —— 否则顶部若干行会被 <c>ClientRectangle</c> 挡掉。
    /// </summary>
    [Fact]
    public void VisibleHeight_exceeds_WindowY_when_terminal_is_taller()
    {
        int h = TerminalViewportGeometry.VisibleHeightPixels(lineCount: 100, terminalHeight: 33, scrollOffset: 0);

        Assert.Equal(32 * LH, h);
        Assert.True(h > Config.WindowY, "终端 33 行 ⇒ 视口 32 行 = 608px，必须 > WindowY(480)");
    }

    [Fact]
    public void VisibleHeight_never_collapses_to_zero()
        => Assert.True(TerminalViewportGeometry.VisibleHeightPixels(lineCount: 0, terminalHeight: 29, scrollOffset: 0) >= 1);

    // ---------- 行列 → 像素 ----------

    [Fact]
    public void PointY_is_row_times_line_height()
    {
        Assert.Equal(0, TerminalViewportGeometry.PointYAtViewportRow(0));
        Assert.Equal(LH, TerminalViewportGeometry.PointYAtViewportRow(1));
        Assert.Equal(3 * LH, TerminalViewportGeometry.PointYAtViewportRow(3));
    }

    [Fact]
    public void PointX_is_column_times_char_width()
    {
        int charWidth = Math.Max(Config.FontSize / 2, 1);

        Assert.Equal(0, TerminalViewportGeometry.PointXAtViewportCol(0));
        Assert.Equal(3 * charWidth, TerminalViewportGeometry.PointXAtViewportCol(3));
    }

    // ---------- 自洽性：渲染出来的每一行都必须落在可绘制区内 ----------

    /// <summary>
    /// 命中判定的前提是 <c>ClientRectangle.Contains(point)</c>。本测试把「渲染块」整体代入，
    /// 断言<b>每一行</b>都在界内 —— 这是「按钮点得到」的机器检查。
    /// </summary>
    [Theory]
    [InlineData(4, 29, 0)]      // 内容不足一屏
    [InlineData(100, 29, 0)]    // 内容多于一屏
    [InlineData(100, 33, 0)]    // 终端高于游戏窗口（真机场景）
    [InlineData(100, 33, 10)]   // 叠加 Scroll Mode
    public void Every_rendered_row_falls_inside_drawable_area(int lineCount, int terminalHeight, int scrollOffset)
    {
        int rendered = TerminalViewportGeometry.RenderedLines(lineCount, terminalHeight, scrollOffset);
        int h = TerminalViewportGeometry.VisibleHeightPixels(lineCount, terminalHeight, scrollOffset);

        for (int row = 0; row < rendered; row++)
        {
            int y = TerminalViewportGeometry.PointYAtViewportRow(row);
            Assert.True(y >= 0 && y < h, $"row={row} ⇒ y={y} 超出 [0, {h})");
        }
    }
}
