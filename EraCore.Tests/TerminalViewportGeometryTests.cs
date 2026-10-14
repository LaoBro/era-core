using System;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 M4：CLI 终端行列 ↔ 游戏像素的换算。
/// <para>
/// 🔒 <b>本文件是「<c>VisibleHeight</c> 必须是视口行数 × 行高、不能是 <c>Config.WindowY</c>」的回归锚点</b>。
/// 真机表现：终端 33 行时按钮「经常点不到」。推导如下 ——
/// <list type="bullet">
/// <item>引擎用 <c>H</c> 同时算 <c>topLineNo = bottomLineNo − H/LH</c> 与 <c>relPointY = pointY − H</c>；</item>
/// <item>若 <c>H</c> 固定 480（25 行）而终端渲染 32 行，多出的 7 行会落到 <c>y &lt; 0</c>；</item>
/// <item><c>ClientRectangle.Contains</c> 把 <c>y &lt; 0</c> 挡掉 ⇒ <c>MoveMouse</c> 不跑 ⇒ 顶部按钮永久点不到。</item>
/// </list>
/// 取 <c>H = VisibleLines × LH</c>（视口高度，2026.10.14 贴底起与内容量无关）后，
/// 内容行 <c>row ∈ [BaseRow, VisibleLines)</c> 全部有 <c>0 ≤ y &lt; H</c>，且代入引擎行扫描得
/// <c>relPointY ≡ 0</c>（见 <see cref="Bottom_anchored_rows_yield_zero_rel_point_y"/>）⇒ 恒定命中。
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

    // ---------- BaseRow（贴底块的起始终端行 = 顶部留白） ----------

    /// <summary>内容不足一屏 ⇒ 块下方贴 <c>visibleLines-1</c>，上方留白 = <c>VisibleLines − RenderedLines</c>。</summary>
    [Fact]
    public void BaseRow_short_content_is_top_padding()
        => Assert.Equal(24, TerminalViewportGeometry.BaseRow(lineCount: 4, terminalHeight: 29, scrollOffset: 0));

    /// <summary>内容占满视口 ⇒ 无留白，贴底 = 顶部对齐。</summary>
    [Fact]
    public void BaseRow_long_content_is_zero()
        => Assert.Equal(0, TerminalViewportGeometry.BaseRow(lineCount: 100, terminalHeight: 29, scrollOffset: 0));

    /// <summary>Scroll Mode 下视口被历史切片填满 ⇒ 同样无留白。</summary>
    [Fact]
    public void BaseRow_scroll_mode_is_zero()
        => Assert.Equal(0, TerminalViewportGeometry.BaseRow(lineCount: 100, terminalHeight: 29, scrollOffset: 10));

    /// <summary>空屏 ⇒ 整屏留白（<c>LastContentRow</c> 仍给出 <c>visibleLines-1</c>）。</summary>
    [Fact]
    public void BaseRow_empty_content_is_all_padding()
        => Assert.Equal(28, TerminalViewportGeometry.BaseRow(lineCount: 0, terminalHeight: 29, scrollOffset: 0));

    // ---------- VisibleHeight ----------

    /// <summary>
    /// 视口高度 = <b>视口行数</b> × 行高（2026.10.14 贴底起不再随内容量收缩）。
    /// </summary>
    [Fact]
    public void VisibleHeight_is_visible_lines_times_line_height()
        => Assert.Equal(28 * LH,
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
    /// 贴底后渲染块占终端行 <c>[BaseRow, BaseRow + RenderedLines)</c>（内容不足一屏时上方留白）。
    /// </summary>
    [Theory]
    [InlineData(4, 29, 0)]      // 内容不足一屏
    [InlineData(100, 29, 0)]    // 内容多于一屏
    [InlineData(100, 33, 0)]    // 终端高于游戏窗口（真机场景）
    [InlineData(100, 33, 10)]   // 叠加 Scroll Mode
    public void Every_rendered_row_falls_inside_drawable_area(int lineCount, int terminalHeight, int scrollOffset)
    {
        int rendered = TerminalViewportGeometry.RenderedLines(lineCount, terminalHeight, scrollOffset);
        int baseRow = TerminalViewportGeometry.BaseRow(lineCount, terminalHeight, scrollOffset);
        int h = TerminalViewportGeometry.VisibleHeightPixels(lineCount, terminalHeight, scrollOffset);

        for (int row = baseRow; row < baseRow + rendered; row++)
        {
            int y = TerminalViewportGeometry.PointYAtViewportRow(row);
            Assert.True(y >= 0 && y < h, $"row={row} ⇒ y={y} 超出 [0, {h})");
        }
    }

    // ---------- 贴底不变量：relPointY ≡ 0（spec §1.1 的机器检查） ----------

    /// <summary>
    /// 🔒 把 <c>TerminalRenderer</c> 的贴底布局代入引擎 <c>MoveMouse</c> 的行扫描算术
    /// （<c>ConsoleInputHandler.cs</c>：<c>bottomLineNo = lineCount − offset − 1</c>、
    /// <c>relPointY = pointY − VisibleHeight</c>，自 <c>bottomLineNo</c> 向上逐行 <c>+= LH</c>），
    /// 断言每一行的 <c>relPointY</c> 都 ≡ 0 ⇒ <c>MOUSEB</c> 恒定命中，且与 row / 内容量无关。
    /// 这正是「改贴底从根上消除 M4 甲/乙取舍」的数学核心。
    /// </summary>
    [Theory]
    [InlineData(3, 33, 0)]      // §7 探针：稀疏
    [InlineData(25, 33, 0)]     // 接近满屏
    [InlineData(100, 33, 0)]    // 满屏
    [InlineData(100, 33, 10)]   // Scroll Mode
    public void Bottom_anchored_rows_yield_zero_rel_point_y(int lineCount, int terminalHeight, int scrollOffset)
    {
        int lh = LH;
        int visible = TerminalViewportGeometry.VisibleLines(terminalHeight, scrollOffset);
        int rendered = TerminalViewportGeometry.RenderedLines(lineCount, terminalHeight, scrollOffset);
        int baseRow = TerminalViewportGeometry.BaseRow(lineCount, terminalHeight, scrollOffset);
        int startLine = TerminalViewportGeometry.StartLine(lineCount, terminalHeight, scrollOffset);
        int h = TerminalViewportGeometry.VisibleHeightPixels(lineCount, terminalHeight, scrollOffset);
        int bottomLineNo = Math.Min(lineCount - 1, lineCount - scrollOffset - 1);

        for (int i = 0; i < rendered; i++)
        {
            int lineNo = startLine + i;
            int terminalRow = baseRow + i;                       // TerminalRenderer 贴底写屏行
            int pointY = TerminalViewportGeometry.PointYAtViewportRow(terminalRow);
            // 引擎：relPointY = pointY - VH + (bottomLineNo - lineNo + 1) * LH
            int relPointY = pointY - h + (bottomLineNo - lineNo + 1) * lh;
            Assert.Equal(0, relPointY);
        }
    }
}
