using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;
using Utils = MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace Emuera.Headless.Tests;

/// <summary>
/// 02 — 协议 v8 segment 序列化单测（spec 测试 seam ①/②）。
/// BuildPrintOpsForLine 对 ConsoleImagePart / ConsoleRectangleShapePart 输出类型化
/// image/shape segment（01 产出的几何），不再空串丢弃。期望值来自 01 已断言的手算几何。
/// </summary>
[Collection("GamePathsIsolated")]
public class ProtocolV8SegmentTests : IDisposable
{
    private readonly string _gameDir;
    private readonly IDisposable _scope;

    public ProtocolV8SegmentTests()
    {
        _gameDir = Path.Combine(Path.GetTempPath(), "emuera-v8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "img"));
        GamePaths.Resolve(_gameDir, new FileSystemGameDirAccessor());
        _scope = GlobalStatic.OpenScope(new ConfigData());
        File.WriteAllBytes(Path.Combine(_gameDir, "img", "portrait.png"), Png(200, 100));
    }

    public void Dispose()
    {
        _scope.Dispose();
        Directory.Delete(_gameDir, true);
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new List<byte>
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
            (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        };
        bytes.Add((byte)(width >> 24));
        bytes.Add((byte)(width >> 16));
        bytes.Add((byte)(width >> 8));
        bytes.Add((byte)width);
        bytes.Add((byte)(height >> 24));
        bytes.Add((byte)(height >> 16));
        bytes.Add((byte)(height >> 8));
        bytes.Add((byte)height);
        bytes.AddRange(new byte[] { 8, 6, 0, 0, 0 });
        return bytes.ToArray();
    }

    private static Utils.MixedNum Px(int num) => new() { num = num, isPx = true };

    private static ConsoleDisplayLine LineOf(params AConsoleDisplayNode[] nodes)
    {
        var btn = new ConsoleButtonString(null!, nodes);
        return new ConsoleDisplayLine(new[] { btn }, isLogical: true, temporary: false);
    }

    [Fact]
    public void Image_part_produces_image_segment_with_geometry()
    {
        var img = new ConsoleImagePart("img/portrait.png", "img/btn.png", "img/map.png", Px(50), null, null);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(LineOf(img), "TestFont");
        var seg = Assert.Single(Assert.Single(ops).segments);

        // 01 设计：Text 保留 AltText 调试标记（CLI BuildString 用）；前端按 image 字段渲染。
        // B 之后（GetSprite 替身）：文件存在 → cImage!=null → Text 空（正常 sprite 语义）；
        // 仅当 GetSprite 未命中（缺文件）才保留 AltText。前端一律按 image 字段渲染。
        Assert.Equal("", seg.text);
        Assert.Null(seg.shape);
        Assert.NotNull(seg.image);
        Assert.Equal("img/portrait.png", seg.image!.src);
        Assert.Equal("img/btn.png", seg.image.srcb);
        Assert.Equal("img/map.png", seg.image.srcm);
        Assert.Equal(100, seg.image.width); // 200×100 + height=50px → 纵横比推算
        Assert.Equal(50, seg.image.height);
        Assert.Equal(0, seg.image.ypos);
    }

    [Fact]
    public void Rect_part_produces_shape_segment_with_geometry_and_color()
    {
        var rect = (ConsoleRectangleShapePart)ConsoleShapePart.CreateShape(
            "rect", [Px(10), Px(20), Px(30), Px(40)], new EmuColor(255, 0, 0), default, false);
        rect.SetWidth(new StringMeasure(), 0);

        var ops = ConsolePrintManager.BuildPrintOpsForLine(LineOf(rect), "TestFont");
        var seg = Assert.Single(Assert.Single(ops).segments);

        Assert.Null(seg.image);
        Assert.NotNull(seg.shape);
        Assert.Equal("rect", seg.shape!.type);
        Assert.Equal(13, seg.shape.x); // 10 + shape position shift(3)
        Assert.Equal(20, seg.shape.y);
        Assert.Equal(30, seg.shape.width); // 40 - 10
        Assert.Equal(40, seg.shape.height);
        Assert.Equal("#FF0000", seg.shape.color); // EmuColor.ToHex() 大写
    }

    [Fact]
    public void Plain_text_part_keeps_v7_fields_and_no_image_or_shape()
    {
        var style = new StringStyle(new EmuColor(0, 0, 255), colorChanged: true,
            buttonColor: EmuColor.Black, EmuFontStyle.Bold, fontname: "F");
        var ops = ConsolePrintManager.BuildPrintOpsForLine(
            LineOf(new ConsoleStyledString("hello", style)),
            "TestFont");
        var seg = Assert.Single(Assert.Single(ops).segments);

        Assert.Equal("hello", seg.text);
        Assert.Equal("#0000FF", seg.color);
        Assert.True(seg.bold);
        Assert.Null(seg.image);
        Assert.Null(seg.shape);
    }

    [Fact]
    public void Underline_style_is_preserved_for_slider_space_segments()
    {
        var style = new StringStyle(new EmuColor(64, 192, 64), colorChanged: true,
            buttonColor: EmuColor.Black, EmuFontStyle.Underline, fontname: "TestFont");

        var ops = ConsolePrintManager.BuildPrintOpsForLine(
            LineOf(new ConsoleStyledString(" ", style)),
            "TestFont");
        var seg = Assert.Single(Assert.Single(ops).segments);

        Assert.True(seg.underline);
    }

    [Fact]
    public void Strikeout_style_is_preserved_for_slider_space_segments()
    {
        // v11：PRINT_SLIDER 实际用 ERB FONTSTYLE 位4（删除线）画轨道——
        // 前端靠 strikeout 标志绘制，与 underline 对称。
        var style = new StringStyle(new EmuColor(64, 192, 64), colorChanged: true,
            buttonColor: EmuColor.Black, EmuFontStyle.Strikeout, fontname: "TestFont");

        var ops = ConsolePrintManager.BuildPrintOpsForLine(
            LineOf(new ConsoleStyledString(" ", style)),
            "TestFont");
        var seg = Assert.Single(Assert.Single(ops).segments);

        Assert.True(seg.strikeout);
    }
}
