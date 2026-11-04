using EraCore.Native;
using Xunit;

namespace EraCore.Hosting.Tests;

/// <summary>
/// 行内图片/形状的「占位宽度 = 绘制宽度」口径（<see cref="NativeGeometry"/>）。
/// 纯文本宿主按同一数值补空格占位，原生渲染按同一数值推进游标，因此加图不会改变行宽。
/// </summary>
public sealed class NativeRenderGeometryTests
{
    private static NativeImage Image(int width, int height = 100, int ypos = 0)
        => new("resources/sample.png", null, null, width, height, ypos, null);

    [Fact]
    public void ImageWidth_EqualsDeclaredWidth()
    {
        Assert.Equal(120f, NativeGeometry.ImageWidth(Image(120), 1f));
    }

    [Fact]
    public void ImageWidth_FollowsStyleScale()
    {
        // 设置页放大 1.5 倍字号时，占位与绘制同比放大。
        Assert.Equal(180f, NativeGeometry.ImageWidth(Image(120), 1.5f));
    }

    [Fact]
    public void ImageWidth_NeverNegative()
    {
        Assert.Equal(0f, NativeGeometry.ImageWidth(Image(-40), 1f));
    }

    [Fact]
    public void ShapeFlowWidth_IsXPlusWidth()
    {
        var shape = new NativeShape("rect", 10, 0, 30, 20, "#FF0000");
        Assert.Equal(40f, NativeGeometry.ShapeWidth(shape, 1f));
    }

    [Fact]
    public void ShapeFlowWidth_FullRowRectOccupiesWholeRow()
    {
        var shape = new NativeShape("rect", 0, 0, 84, 20, "#FFFFFF");
        Assert.Equal(84f, NativeGeometry.ShapeWidth(shape, 1f));
    }

    [Fact]
    public void ShapeFlowWidth_NegativeOriginDoesNotShrinkBelowZero()
    {
        var shape = new NativeShape("rect", -50, 0, 10, 20, "#FFFFFF");
        Assert.Equal(0f, NativeGeometry.ShapeWidth(shape, 1f));
    }

    [Fact]
    public void TextOnlySegment_IsNeitherImageNorShape()
    {
        var segment = new NativeSegment("hello", null, false, false, null, false, false);
        Assert.False(segment.IsImage);
        Assert.False(segment.IsShape);
        Assert.Null(segment.Image);
        Assert.Null(segment.Shape);
    }

    [Fact]
    public void FrameWithoutBackgrounds_ExposesEmptyLayerList()
    {
        var frame = new NativeFrame(
            System.Array.Empty<NativeLine>(), "#000000", 1, 1, 1, null);
        Assert.Empty(frame.BackgroundLayers);
    }

    [Fact]
    public void FrameBackgrounds_KeepDeclaredOrder()
    {
        var top = new NativeBgImage("resources/top.png", 5, 1f);
        var bottom = new NativeBgImage("resources/bottom.png", 1, 0.5f);
        var frame = new NativeFrame(
            System.Array.Empty<NativeLine>(), "#000000", 1, 1, 1, null, new[] { top, bottom });

        Assert.Equal(2, frame.BackgroundLayers.Count);
        Assert.Equal("resources/top.png", frame.BackgroundLayers[0].Src);
        Assert.Equal(5, frame.BackgroundLayers[0].Depth);
        Assert.Equal(0.5f, frame.BackgroundLayers[1].Opacity);
    }
}
