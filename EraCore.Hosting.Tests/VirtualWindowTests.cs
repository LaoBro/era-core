using Xunit;
using EraCore.Hosting;

namespace EraCore.Hosting.Tests;

public sealed class VirtualWindowTests
{
    private static readonly VirtualWindowOptions Fixed = new(RowHeight: 20, Overscan: 2);

    [Fact]
    public void Fixed_height_visible_range_and_overscan()
    {
        var state = VirtualWindow.Compute(
            itemCount: 100,
            scrollY: 200,
            viewportHeight: 100,
            options: Fixed);

        Assert.Equal(8, state.StartIndex);      // first visible 10 - overscan 2
        Assert.Equal(17, state.EndIndexExclusive); // rows 10..14 + overscan 2
        Assert.Equal(160, state.OffsetY);       // 8 * 20
        Assert.Equal(2000, state.TotalHeight);
        Assert.Equal(1900, state.MaxScrollY);
        Assert.False(state.IsAtBottom);
    }

    [Fact]
    public void Bottom_is_sticky_and_scrollbar_ratio_is_bounded()
    {
        var state = VirtualWindow.Compute(
            itemCount: 100,
            scrollY: 99999,
            viewportHeight: 100,
            options: Fixed);

        Assert.Equal(1900, state.ScrollY);
        Assert.True(state.IsAtBottom);
        Assert.True(state.ThumbHeight >= 24);
        Assert.True(state.ThumbY >= 0);
        Assert.True(state.ThumbY + state.ThumbHeight <= 100);
    }

    [Fact]
    public void Line_extras_shift_visible_range_and_total_height()
    {
        var options = new VirtualWindowOptions(
            RowHeight: 20,
            Overscan: 0,
            LineExtras: new Dictionary<int, double> { [0] = 40, [1] = 10 });

        var state = VirtualWindow.Compute(
            itemCount: 10,
            scrollY: 0,
            viewportHeight: 100,
            options: options);

        Assert.Equal(0, state.StartIndex);
        Assert.Equal(250, state.TotalHeight); // 10*20 + 50
        // 第 2 行顶部 = 60；视口 0..100 应覆盖 0/1/2 三行。
        Assert.Equal(3, state.EndIndexExclusive);
    }

    [Fact]
    public void Shift_head_compensation_keeps_visual_position()
    {
        var options = new VirtualWindowOptions(RowHeight: 20, Overscan: 0);
        var scroll = VirtualWindow.CompensateShiftHead(
            scrollY: 300,
            removedCount: 3,
            itemCountBeforeRemoval: 100,
            options: options);

        Assert.Equal(240, scroll);
    }

    [Fact]
    public void Empty_content_has_no_scroll_and_no_thumb()
    {
        var state = VirtualWindow.Compute(0, 0, 100, Fixed);
        Assert.Equal(0, state.StartIndex);
        Assert.Equal(0, state.EndIndexExclusive);
        Assert.Equal(0, state.TotalHeight);
        Assert.Equal(0, state.MaxScrollY);
        Assert.True(state.IsAtBottom);
        Assert.Equal(0, state.ThumbHeight);
    }
}
