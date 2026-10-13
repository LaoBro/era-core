using System;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 **M3 视口解耦**接缝测试（视口侧）。
/// <para>
/// M3 只做一件事：把 <c>ConsoleInputHandler.MoveMouse</c> 的视口来源从
/// <c>_ui.ScrollBar.Value</c> / <c>_ui.MainPicBox.Height</c> / <c>Config.LineHeight</c>
/// 换成 <see cref="IViewportProvider"/>，并让 CLI 的 <see cref="ScrollController"/>
/// 成为首个真实实现。几何命中端（<c>MoveMouse</c> 本身）见
/// <see cref="MoveMouseViewportTests"/> —— 它用 <c>LoaderTestHarness</c>，
/// 后者自己开 <c>GlobalStatic</c> scope，不能与本类的类级 scope 嵌套。
/// </para>
/// <para>
/// ⚠️ <b>为什么 <c>ScrollOffset</c> 不是原版 <c>ScrollBar.Value</c> 语义</b>：原版
/// <c>Value</c> 是「可视区底部行号 + 1」，它<b>依赖总行数</b>
/// （<c>verticalScrollBarUpdate()</c> 里 <c>Maximum = displayLineList.Count</c>）。
/// 而宿主侧根本不持有 <c>displayLineList</c> —— <c>ScrollController</c> 的
/// <c>ScrollBy/ScrollTo/Clamp</c> 全都把 <c>lineCount</c> 当<b>参数</b>收。
/// 沿用 <c>Value</c> 语义 ⇒ <c>ScrollController</c> 无法实现本接口
/// ⇒ CLI 真实滚动永远进不了引擎（缺口 F）。故改为「距底偏移」，
/// 行数换算交给持有 <c>displayLineList</c> 的引擎。
/// </para>
/// </summary>
public sealed class ViewportDecouplingTests : IDisposable
{
    private readonly IDisposable _scope;

    public ViewportDecouplingTests() => _scope = GlobalStatic.OpenScope(new ConfigData());

    public void Dispose() => _scope.Dispose();

    // ===============================================================
    // ① ScrollController 作为 IViewportProvider（spec-v3 §6 引擎-10）
    // ===============================================================

    [Fact]
    public void ScrollController_exposes_offset_as_distance_from_bottom()
    {
        var scroll = new ScrollController(scrollVisibleLines: 10);
        IViewportProvider vp = scroll;

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
    public void ScrollController_scroll_is_visible_without_scrollbar_sync()
    {
        var scroll = new ScrollController(scrollVisibleLines: 10);
        IViewportProvider vp = scroll;

        // 模拟用户 PgUp：ScrollAction → 算子 → offset
        scroll.ApplyAction(ScrollAction.PageUp, windowHeight: 24, lineCount: 100);

        Assert.Equal(scroll.ScrollOffset, vp.ScrollOffset);
        Assert.True(vp.ScrollOffset > 0);
    }

    [Fact]
    public void ScrollController_visible_height_is_lines_times_line_height()
    {
        var scroll = new ScrollController(scrollVisibleLines: 12);
        IViewportProvider vp = scroll;

        Assert.Equal(Config.LineHeight, vp.LineHeight);
        Assert.Equal(12 * Config.LineHeight, vp.VisibleHeight);

        scroll.UpdateVisibleLines(30);
        Assert.Equal(30 * Config.LineHeight, vp.VisibleHeight);
    }

    [Fact]
    public void ScrollController_clamps_offset_and_keeps_it_non_negative()
    {
        var scroll = new ScrollController(scrollVisibleLines: 10);
        IViewportProvider vp = scroll;

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

        ui.AttachViewport(scroll);

        Assert.Same(scroll, ui.Viewport);
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
}
