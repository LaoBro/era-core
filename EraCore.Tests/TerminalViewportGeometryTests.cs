using System;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 M4：CLI 终端行列 ↔ 游戏像素的换算。
/// <para>
/// 🔒 <b>本文件是「CLI 顶部对齐 vs 引擎底部对齐」这个坑的回归锚点</b>。
/// M4 初稿写成 <c>y = row × LineHeight</c>，真机表现是「按钮经常点不到」——
/// 因为 <c>relPointY</c> 整体平移了 <c>WindowY − renderedLines × LineHeight</c>，
/// 底行算出来是 −5 而非 0。若有人再把 Y 退回「行号 × 行高」，这些测试会转红。
/// </para>
/// </summary>
public sealed class TerminalViewportGeometryTests : IDisposable
{
    private readonly IDisposable _scope;

    public TerminalViewportGeometryTests() => _scope = GlobalStatic.OpenScope(new ConfigData());

    public void Dispose() => _scope.Dispose();

    private static int H => Config.WindowY;          // 480
    private static int LH => Config.LineHeight;      // 19

    /// <summary>可绘制区底部那一行的 Y —— 内容最后一行必须落在这里。</summary>
    private static int BottomRowY => H - LH;         // 461

    // ---------- X：列宽累加 ----------

    [Fact]
    public void PointX_is_column_times_char_width()
    {
        int charWidth = Math.Max(Config.FontSize / 2, 1);

        Assert.Equal(0, TerminalViewportGeometry.PointXAtViewportCol(0));
        Assert.Equal(charWidth, TerminalViewportGeometry.PointXAtViewportCol(1));
        Assert.Equal(3 * charWidth, TerminalViewportGeometry.PointXAtViewportCol(3));
    }

    // ---------- Y：内容块贴底 ----------

    /// <summary>内容不足一屏：内容<b>最后一行</b>贴底，上方的行依次往上。</summary>
    [Fact]
    public void PointY_short_content_bottom_row_sits_on_drawable_bottom()
    {
        // lineCount=4，终端 29 行 ⇒ visibleLines=28 ⇒ startLine=0 ⇒ 行 0..3 渲染在终端 0..3
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 3, lineCount: 4, terminalHeight: 29, scrollOffset: 0);

        Assert.Equal(BottomRowY, y);                 // ⇒ MOUSEY = −19
        Assert.Equal(-LH, y - H);
    }

    [Fact]
    public void PointY_short_content_top_row_is_offset_by_rendered_height()
    {
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 0, lineCount: 4, terminalHeight: 29, scrollOffset: 0);

        // 4 行内容 ⇒ 顶行距底部 3 行
        Assert.Equal(BottomRowY - 3 * LH, y);
    }

    /// <summary>内容满屏（25 行）：顶行 ≈ −475，与原版「左上 MY ≈ −H」一致。</summary>
    [Fact]
    public void PointY_full_screen_top_row_matches_M0_calibration()
    {
        int rows = H / LH;                            // 25
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 0, lineCount: rows, terminalHeight: 29, scrollOffset: 0);

        Assert.Equal(BottomRowY - (rows - 1) * LH, y);
        Assert.Equal(-475, y - H);                    // M0 标定：左上 MY ≈ −480
    }

    /// <summary>
    /// 内容多于一屏：<b>终端第 0 行不是内容第 0 行</b> —— 渲染从 <c>startLine</c> 开始。
    /// 这正是「row × LineHeight」会错的地方之一。
    /// </summary>
    [Fact]
    public void PointY_long_content_uses_start_line()
    {
        // lineCount=100，终端 29 ⇒ visibleLines=28 ⇒ startLine=72 ⇒ 终端 0..27 渲染行 72..99
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 0, lineCount: 100, terminalHeight: 29, scrollOffset: 0);

        Assert.Equal(BottomRowY - 27 * LH, y);        // 行 72 距底 27 行
    }

    [Fact]
    public void PointY_long_content_last_visible_row_sits_on_bottom()
    {
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 27, lineCount: 100, terminalHeight: 29, scrollOffset: 0);

        Assert.Equal(BottomRowY, y);
    }

    /// <summary>Scroll Mode：视口上移，贴底的变成更早的行。</summary>
    [Fact]
    public void PointY_scroll_mode_shifts_which_line_is_at_bottom()
    {
        // offset=10 ⇒ visibleLines=27（多留状态栏）⇒ startLine=63 ⇒ 底行是行 89
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 26, lineCount: 100, terminalHeight: 29, scrollOffset: 10);

        Assert.Equal(BottomRowY, y);
    }

    /// <summary>
    /// 点在内容<b>下方</b>的空白/状态栏 ⇒ <c>MOUSEY &gt; 0</c>。
    /// 对应原版「输入框内 MY ∈ [0, +20]」—— 引擎不做内外判定，只做命中判定。
    /// </summary>
    [Fact]
    public void PointY_below_content_is_positive_MOUSEY()
    {
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 32, lineCount: 26, terminalHeight: 33, scrollOffset: 0);

        Assert.True(y - H > 0);
    }

    /// <summary>屏幕尚未建立（terminalHeight=0）⇒ 退化为游戏视口行数，不崩。</summary>
    [Fact]
    public void PointY_without_screen_falls_back_to_game_viewport()
    {
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 0, lineCount: 100, terminalHeight: 0, scrollOffset: 0);

        int visibleLines = Math.Max(1, H / LH);
        Assert.Equal(BottomRowY - (visibleLines - 1) * LH, y);
    }

    /// <summary>空内容（lineCount=0）不得产生负溢出或异常。</summary>
    [Fact]
    public void PointY_empty_content_is_clamped()
    {
        int y = TerminalViewportGeometry.PointYAtViewportRow(row: 0, lineCount: 0, terminalHeight: 29, scrollOffset: 0);

        Assert.Equal(BottomRowY, y);
    }
}
