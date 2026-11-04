using System.Collections.Generic;
using EraCore.Native;
using MinorShift.Emuera.Output;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// <see cref="NativeRenderModel"/> 的投影契约：图片（含 srcb/srcm/crop）、形状、背景层。
/// 几何字段是原生绘制与占位宽度的唯一来源，投影丢字段就等于前端丢图。
/// </summary>
public sealed class NativeRenderModelTests
{
    private static DisplayLine Line(params PrintSegment[] segments)
        => new(new List<DisplayEntry> { new(new List<PrintSegment>(segments), null) }, null, true);

    [Fact]
    public void ImageSegment_ProjectsFullGeometry()
    {
        var segment = new PrintSegment(
            string.Empty, null, null, null, null,
            new SegmentImage("FACE_1", "FACE_1_B", "FACE_1_M", 36, 18, 2,
                new SegmentCrop(-36, 0, 72, 36)));

        var native = NativeRenderModel.ProjectLine(Line(segment));
        var projected = Assert.Single(Assert.Single(native.Entries).Segments);

        Assert.True(projected.IsImage);
        Assert.False(projected.IsShape);
        var image = Assert.IsType<NativeImage>(projected.Image);
        Assert.Equal("FACE_1", image.Src);
        Assert.Equal("FACE_1_B", image.Srcb);
        Assert.Equal("FACE_1_M", image.Srcm);
        Assert.Equal(36, image.Width);
        Assert.Equal(18, image.Height);
        Assert.Equal(2, image.YPos);
        var crop = Assert.IsType<NativeCrop>(image.Crop);
        Assert.Equal(-36, crop.X);
        Assert.Equal(0, crop.Y);
        Assert.Equal(72, crop.ImgWidth);
        Assert.Equal(36, crop.ImgHeight);

        // 占位宽度 = 绘制宽度（与纯文本宿主同一口径）。
        Assert.Equal(36f, NativeGeometry.ImageWidth(image, 1f));
    }

    [Fact]
    public void ShapeSegment_ProjectsGeometryAndFlowWidth()
    {
        var segment = new PrintSegment(
            string.Empty, null, null, null, null, null,
            new SegmentShape("rect", 0, 0, 84, 20, "#FFFFFF"));

        var native = NativeRenderModel.ProjectLine(Line(segment));
        var projected = Assert.Single(Assert.Single(native.Entries).Segments);

        Assert.True(projected.IsShape);
        Assert.False(projected.IsImage);
        var shape = Assert.IsType<NativeShape>(projected.Shape);
        Assert.Equal("rect", shape.Type);
        Assert.Equal(84, shape.Width);
        Assert.Equal("#FFFFFF", shape.Color);
        Assert.Equal(84f, NativeGeometry.ShapeWidth(shape, 1f));
    }

    [Fact]
    public void TextSegment_StaysTextOnly()
    {
        var native = NativeRenderModel.ProjectLine(Line(
            new PrintSegment("hello", "#FF0000", true, false, null)));

        var projected = Assert.Single(Assert.Single(native.Entries).Segments);
        Assert.False(projected.IsImage);
        Assert.False(projected.IsShape);
        Assert.Null(projected.Image);
        Assert.Null(projected.Shape);
        Assert.Equal("hello", projected.Text);
        Assert.True(projected.Bold);
    }

    [Fact]
    public void Backgrounds_ProjectWithDepthAndOpacity()
    {
        var snapshot = new DisplaySnapshot(
            new List<DisplayLine>(),
            "#101010",
            null!,
            1,
            7,
            new List<BgImageState>
            {
                new("bg/night.png", 3, 0.4f),
                new("bg/base.png", 1, 1f),
            },
            7);

        var frame = NativeRenderModel.FromSnapshot(snapshot);

        Assert.Equal("#101010", frame.BackgroundColor);
        Assert.Equal(2, frame.BackgroundLayers.Count);
        Assert.Equal("bg/night.png", frame.BackgroundLayers[0].Src);
        Assert.Equal(3, frame.BackgroundLayers[0].Depth);
        Assert.Equal(0.4f, frame.BackgroundLayers[0].Opacity);
        Assert.Equal("bg/base.png", frame.BackgroundLayers[1].Src);
    }
}
