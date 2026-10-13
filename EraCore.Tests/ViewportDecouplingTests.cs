using System;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 **M3/M4 视口解耦**接缝测试（视口侧）。
/// <para>
/// M3 把 <c>ConsoleInputHandler.MoveMouse</c> 的视口来源从
/// <c>_ui.ScrollBar.Value</c> / <c>_ui.MainPicBox.Height</c> / <c>Config.LineHeight</c>
/// 换成 <see cref="IViewportProvider"/>。几何命中端（<c>MoveMouse</c> 本身）见
/// <see cref="MoveMouseViewportTests"/> —— 它用 <c>LoaderTestHarness</c>，
/// 后者自己开 <c>GlobalStatic</c> scope，不能与本类的类级 scope 嵌套。
/// </para>
/// <para>
/// ⚠️ <b>为什么 <c>ScrollOffset</c> 不是原版 <c>ScrollBar.Value</c> 语义</b>：原版
/// <c>Value</c> 是「可视区底部行号 + 1」，它<b>依赖总行数</b>
/// （<c>verticalScrollBarUpdate()</c> 里 <c>Maximum = displayLineList.Count</c>）。
/// 而宿主侧根本不持有 <c>displayLineList</c> —— <c>ScrollController</c> 的
/// <c>ScrollBy/ScrollTo/Clamp</c> 全都把 <c>lineCount</c> 当<b>参数</b>收。
/// 沿用 <c>Value</c> 语义 ⇒ 宿主无法回答 ⇒ CLI 真实滚动永远进不了引擎（缺口 F）。
/// 故改为「距底偏移」，行数换算交给持有 <c>displayLineList</c> 的引擎。
/// </para>
/// <para>
/// ⚠️ <b>为什么视口不是 <c>ScrollController</c> 而是 <see cref="CliViewport"/></b>（M4 修正）：
/// 前者只能回答 <c>ScrollOffset</c>；<c>VisibleHeight</c> 需要 <c>lineCount</c> 与终端高度，
/// 而它从不持有这两个数。硬让它实现接口 ⇒ 只能退回 <c>Config.WindowY</c> ⇒ 真机「顶部按钮点不到」。
/// </para>
/// </summary>
public sealed class ViewportDecouplingTests : IDisposable
{
    private readonly IDisposable _scope;

    public ViewportDecouplingTests() => _scope = GlobalStatic.OpenScope(new ConfigData());

    public void Dispose() => _scope.Dispose();

    /// <summary>构造一个 CLI 视口：100 行内容、29 行终端。</summary>
    private static CliViewport MakeViewport(ScrollController? scroll = null, int lineCount = 100, int terminalHeight = 29)
        => new(scroll ?? new ScrollController(scrollVisibleLines: 10), () => lineCount, () => terminalHeight);

    // ===============================================================
    // ① CliViewport（spec-v3 §6 引擎-10）
    // ===============================================================

    [Fact]
    public void CliViewport_exposes_offset_as_distance_from_bottom()
    {
        var scroll = new ScrollController(scrollVisibleLines: 10);
        var vp = MakeViewport(scroll);

        Assert.Equal(0, vp.ScrollOffset);            // 跟随底部

        scroll.ScrollBy(7, lineCount: 100);
        Assert.Equal(7, vp.ScrollOffset);            // 回看 7 行 ⇒ 立刻可见

        scroll.ScrollTo(0, lineCount: 100);
        Assert.Equal(0, vp.ScrollOffset);
    }

    /// <summary>
    /// 缺口 F 的直接验收点：<b>CLI 的真实滚动</b>（<c>ScrollController</c>）
    /// 无需任何「同步回 ScrollBar」的胶水代码即可被引擎几何判定看见。
    /// 改动前这条路径根本不存在。
    /// </summary>
    [Fact]
    public void CliViewport_scroll_is_visible_without_scrollbar_sync()
    {
        var scroll = new ScrollController(scrollVisibleLines: 10);
        var vp = MakeViewport(scroll);

        // 模拟用户 PgUp：ScrollAction → 算子 → offset
        scroll.ApplyAction(ScrollAction.PageUp, windowHeight: 24, lineCount: 100);

        Assert.Equal(scroll.ScrollOffset, vp.ScrollOffset);
        Assert.True(vp.ScrollOffset > 0);
    }

    /// <summary>
    /// 视口高度 = <b>渲染行数 × 行高</b>，不是 <c>Config.WindowY</c>。
    /// 终端 29 行 ⇒ 渲染 28 行 ⇒ 532px ≠ WindowY(480)。
    /// </summary>
    [Fact]
    public void CliViewport_visible_height_is_rendered_lines_times_line_height()
    {
        var vp = MakeViewport();

        Assert.Equal(Config.LineHeight, vp.LineHeight);
        Assert.Equal(28 * Config.LineHeight, vp.VisibleHeight);
        Assert.NotEqual(Config.WindowY, vp.VisibleHeight);
    }

    /// <summary>内容不足一屏 ⇒ 视口高度随之收缩到内容高度。</summary>
    [Fact]
    public void CliViewport_visible_height_shrinks_with_short_content()
    {
        var vp = MakeViewport(lineCount: 4);

        Assert.Equal(4 * Config.LineHeight, vp.VisibleHeight);
    }

    [Fact]
    public void CliViewport_clamps_offset_and_keeps_it_non_negative()
    {
        var scroll = new ScrollController(scrollVisibleLines: 10);
        var vp = MakeViewport(scroll);

        scroll.ScrollBy(-999, lineCount: 100);
        Assert.Equal(0, vp.ScrollOffset);

        scroll.ScrollBy(int.MaxValue, lineCount: 100);
        Assert.Equal(90, vp.ScrollOffset);           // lineCount - visibleLines
    }

    // ===============================================================
    // ② HeadlessConsole 的视口真相源可换（AttachViewport）
    // ===============================================================

    [Fact]
    public void HeadlessConsole_default_viewport_is_itself()
    {
        var ui = new HeadlessConsole();

        Assert.Same(ui, ui.Viewport);
    }

    [Fact]
    public void AttachViewport_swaps_truth_source_and_tracks_live_scroll()
    {
        var ui = new HeadlessConsole();
        var scroll = new ScrollController(scrollVisibleLines: 10);
        var vp = MakeViewport(scroll);

        ui.AttachViewport(vp);

        Assert.Same(vp, ui.Viewport);
        Assert.Equal(0, ui.Viewport.ScrollOffset);

        scroll.ScrollBy(5, lineCount: 100);
        Assert.Equal(5, ui.Viewport.ScrollOffset);   // 真实滚动 ⇒ 引擎可见
    }

    [Fact]
    public void AttachViewport_rejects_null()
    {
        var ui = new HeadlessConsole();

        Assert.Throws<ArgumentNullException>(() => ui.AttachViewport(null!));
    }

    /// <summary>
    /// M4：两处 pull 点（<c>ConsoleInputHandler.EndMacro</c> / <c>InputMouseKey</c>）经
    /// <c>GetCursorPosition()</c> 取坐标再调 <c>MoveMouse</c> ⇒ 它必须反映<b>真实</b>鼠标位置，
    /// 否则引擎恒拿到 <see cref="EmuPoint.Empty"/>，命中判定永远在左上角。
    /// </summary>
    [Fact]
    public void HeadlessConsole_cursor_position_reflects_input_snapshot()
    {
        var host = new DefaultInputHost(InputCapability.Geometry);
        var ui = new HeadlessConsole(host);

        Assert.Equal(0, ui.GetCursorPosition().X);        // 未上报 ⇒ 骨架前的 Empty
        Assert.Equal(0, ui.GetCursorPosition().Y);

        host.SubmitGeometry(new EmuPoint(7, 9));

        Assert.Equal(7, ui.GetCursorPosition().X);
        Assert.Equal(9, ui.GetCursorPosition().Y);
        Assert.Equal(7, ui.GetMousePosition().X);
    }
}
