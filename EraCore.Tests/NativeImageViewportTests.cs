using System.Collections.Generic;
using System.Linq;
using EraCore.Native;
using MinorShift.Emuera.Output;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 图片层解耦的选择器与绘制计划：锚定行槽归并（上方表 → 窗口行 → 下方表）。
/// 全部为平台无关纯函数，Windows 可直接跑。
/// </summary>
public sealed class NativeImageViewportTests
{
    private const double LineHeight = 19;

    private static PrintSegment TextSegment(string text = "text")
        => new(text, null, null, null, null);

    private static PrintSegment ImageSegment(
        string src,
        int width,
        int height,
        int ypos,
        string? srcm = null,
        SegmentCrop? crop = null)
        => new(
            string.Empty,
            null,
            null,
            null,
            null,
            new SegmentImage(src, null, srcm, width, height, ypos, crop));

    private static DisplayEntry Entry(params PrintSegment[] segments)
        => new(new List<PrintSegment>(segments), null);

    private static DisplayLine Line(params PrintSegment[] segments)
        => new(new List<DisplayEntry> { Entry(segments) }, null, true);

    private static DisplayLine MultiEntryLine(params DisplayEntry[] entries)
        => new(new List<DisplayEntry>(entries), null, true);

    /// <summary>生成 lineCount 行文本，再把指定行替换成给定 entry 列表。</summary>
    private static NativeFrame Frame(int lineCount, params (int Line, DisplayEntry[] Entries)[] replacements)
    {
        var lines = new List<DisplayLine>(lineCount);
        for (var i = 0; i < lineCount; i++)
            lines.Add(Line(TextSegment()));

        foreach (var replacement in replacements)
            lines[replacement.Line] = MultiEntryLine(replacement.Entries);

        var snapshot = new DisplaySnapshot(lines, "#000000", null!, 13, 0, null, 0);
        return NativeRenderModel.FromSnapshot(snapshot);
    }

    private static NativeImageExtent SingleExtent(NativeFrame frame)
        => Assert.Single(frame.ImageExtentList);

    [Fact]
    public void Fixture_AnchorBelowWindow_DrawnAfterWindowText()
    {
        // 12 行、lineHeight=19、viewport=38、scrollY=0 → 基线窗口 [0,7)；
        // 图片锚第 10 行，ypos=-190 / height=210 使其矩形 [0,210] 覆盖整个视口。
        var frame = Frame(12,
            (10, new[] { Entry(ImageSegment("tall.png", 200, 210, -190, "tall_m.png")) }));

        var extent = SingleExtent(frame);
        Assert.Equal(10, extent.LineIndex);
        Assert.Equal("tall.png", extent.Image.Src);

        var selection = NativeImageViewport.Select(
            frame.ImageExtentList, 12, LineHeight, 0, 38, 1, 0, 7);

        Assert.Empty(selection.Above);
        Assert.Single(selection.Below);

        Assert.Equal(0, NativeImageViewport.Top(extent, 12, LineHeight, 0, 38, 1));
        Assert.Equal(210, NativeImageViewport.Bottom(extent, 12, LineHeight, 0, 38, 1));

        var steps = NativeImageViewport.Plan(frame, LineHeight, 0, 38, 1, 0, 7);
        Assert.Equal(8, steps.Count);
        Assert.All(steps.Take(7), step => Assert.True(step.DrawLine));
        Assert.False(steps[7].DrawLine);
        Assert.Equal(10, steps[7].LineIndex);
        Assert.Same(extent, steps[7].Image);
    }

    [Fact]
    public void AnchorAboveWindow_RectExtendsDown_GoesAbove()
    {
        // 锚定行 3 在窗口 [10,20) 上方；ypos=0/height=210 使矩形向下伸进视口。
        var frame = Frame(30,
            (3, new[] { Entry(ImageSegment("down.png", 100, 210, 0)) }));

        var selection = NativeImageViewport.Select(
            frame.ImageExtentList, 30, LineHeight, 190, 190, 1, 10, 20);

        Assert.Single(selection.Above);
        Assert.Empty(selection.Below);

        var steps = NativeImageViewport.Plan(frame, LineHeight, 190, 190, 1, 10, 20);
        Assert.False(steps[0].DrawLine);
        Assert.Equal(3, steps[0].LineIndex);
        Assert.Equal(11, steps.Count); // 1 张上方图 + 10 个窗口行
    }

    [Fact]
    public void AnchorInsideWindow_NeverEntersAboveOrBelow()
    {
        var frame = Frame(30,
            (12, new[] { Entry(ImageSegment("inline.png", 100, 19, 0)) }));

        var selection = NativeImageViewport.Select(
            frame.ImageExtentList, 30, LineHeight, 190, 190, 1, 10, 20);

        Assert.True(selection.IsEmpty);

        var steps = NativeImageViewport.Plan(frame, LineHeight, 190, 190, 1, 10, 20);
        Assert.Equal(10, steps.Count);
        Assert.All(steps, step => Assert.True(step.DrawLine));
    }

    [Fact]
    public void RectFullyOutsideViewport_NeverSelected()
    {
        // 锚定行 25 在窗口下方，但 ypos=0/height=19 → top=285，完全在视口 [0,190) 之外。
        var below = Frame(30,
            (25, new[] { Entry(ImageSegment("far-below.png", 100, 19, 0)) }));
        // 锚定行 3 在窗口上方，但矩形完全在视口上方。
        var above = Frame(30,
            (3, new[] { Entry(ImageSegment("far-above.png", 100, 19, 0)) }));

        var belowSelection = NativeImageViewport.Select(
            below.ImageExtentList, 30, LineHeight, 190, 190, 1, 10, 20);
        var aboveSelection = NativeImageViewport.Select(
            above.ImageExtentList, 30, LineHeight, 190, 190, 1, 10, 20);

        Assert.True(belowSelection.IsEmpty);
        Assert.True(aboveSelection.IsEmpty);
    }

    [Fact]
    public void SameAnchorLine_MultipleImages_KeepEntryAndSegmentOrder()
    {
        var frame = Frame(30,
            (3, new[]
            {
                Entry(ImageSegment("first.png", 100, 210, 0)),
                Entry(ImageSegment("second.png", 100, 210, 0)),
                Entry(ImageSegment("third.png", 100, 210, 0)),
            }));

        var selection = NativeImageViewport.Select(
            frame.ImageExtentList, 30, LineHeight, 190, 190, 1, 10, 20);

        Assert.Equal(
            new[] { "first.png", "second.png", "third.png" },
            selection.Above.Select(extent => extent.Image.Src));
        Assert.Equal(new[] { 0, 1, 2 }, selection.Above.Select(extent => extent.EntryIndex));
    }

    [Fact]
    public void SameEntry_MultipleImageSegments_KeepSegmentOrder()
    {
        var frame = Frame(30,
            (3, new[]
            {
                Entry(
                    ImageSegment("a.png", 100, 210, 0),
                    TextSegment(),
                    ImageSegment("b.png", 100, 210, 0)),
            }));

        var selection = NativeImageViewport.Select(
            frame.ImageExtentList, 30, LineHeight, 190, 190, 1, 10, 20);

        Assert.Equal(new[] { "a.png", "b.png" }, selection.Above.Select(extent => extent.Image.Src));
        Assert.Equal(new[] { 0, 2 }, selection.Above.Select(extent => extent.SegmentIndex));
    }

    [Fact]
    public void Scale_MultipliesVerticalExtent()
    {
        var frame = Frame(12,
            (10, new[] { Entry(ImageSegment("tall.png", 200, 210, -190)) }));
        var extent = SingleExtent(frame);

        // lineTop(10)=190；ypos×2=-380 → top=-190；height×2=420 → bottom=230。
        Assert.Equal(-190, NativeImageViewport.Top(extent, 12, LineHeight, 0, 38, 2));
        Assert.Equal(230, NativeImageViewport.Bottom(extent, 12, LineHeight, 0, 38, 2));
    }

    [Fact]
    public void ShortContent_BottomInset_NoPixelDrift()
    {
        // 2 行 × 19 = 38，视口 100 → BottomInset=62，第 1 行顶 = 19 + 62 = 81。
        var frame = Frame(2,
            (1, new[] { Entry(ImageSegment("short.png", 100, 19, 0)) }));
        var extent = SingleExtent(frame);

        Assert.Equal(81, NativeImageViewport.Top(extent, 2, LineHeight, 0, 100, 1));
        Assert.Equal(100, NativeImageViewport.Bottom(extent, 2, LineHeight, 0, 100, 1));

        var selection = NativeImageViewport.Select(
            frame.ImageExtentList, 2, LineHeight, 0, 100, 1, 2, 2);
        Assert.Single(selection.Above);
    }

    [Fact]
    public void Plan_OrderSnapshot_AboveThenWindowThenBelow()
    {
        var frame = Frame(30,
            (3, new[] { Entry(ImageSegment("above.png", 100, 210, 0)) }),
            (12, new[] { Entry(ImageSegment("inline.png", 100, 19, 0)) }),
            (21, new[] { Entry(ImageSegment("below.png", 100, 210, -190)) }));

        var steps = NativeImageViewport.Plan(frame, LineHeight, 190, 380, 1, 10, 20);

        Assert.Equal(12, steps.Count);
        Assert.False(steps[0].DrawLine);
        Assert.Equal(3, steps[0].LineIndex);
        Assert.Equal(Enumerable.Range(10, 10), steps.Skip(1).Take(10).Select(step => step.LineIndex));
        Assert.All(steps.Skip(1).Take(10), step => Assert.True(step.DrawLine));
        Assert.False(steps[11].DrawLine);
        Assert.Equal(21, steps[11].LineIndex);

        // 窗口内的锚定行 12 只走内联路径，绝不进两张表。
        Assert.DoesNotContain(steps, step => !step.DrawLine && step.LineIndex == 12);
    }
}
