using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;
using Utils = MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace Emuera.Headless.Tests;

/// <summary>
/// 01 — 无头下 ConsoleImagePart / rect 几何产出单测（spec 测试 seam ②）。
/// 期望值来自独立来源：PNG 像素尺寸（200×100）+ 参数语义（px/%-fontsize）+ 既有 WinForms 几何公式，
/// 非实现重算。环境装配沿用 LoaderTestHarness 风格（GamePaths.Resolve + Config scope）。
/// </summary>
[Collection("GamePathsIsolated")]
public class ConsoleImageGeometryTests : IDisposable
{
    private readonly string _gameDir;
    private readonly IDisposable _scope;

    public ConsoleImageGeometryTests()
    {
        _gameDir = Path.Combine(Path.GetTempPath(), "emuera-geom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "img"));
        GamePaths.Resolve(_gameDir, new FileSystemGameDirAccessor());
        _scope = GlobalStatic.OpenScope(new ConfigData()); // FontSize 默认 18
        // 200×100 PNG —— 纵横比推算的独立已知来源
        File.WriteAllBytes(Path.Combine(_gameDir, "img", "portrait.png"), Png(200, 100));
        // issue 07：图集夹具——atlas.png 16×8（2×1 网格，每格 8×4）；FACE_1=左格、FACE_2=右格
        Directory.CreateDirectory(Path.Combine(_gameDir, "resources"));
        File.WriteAllText(Path.Combine(_gameDir, "resources", "Face.csv"),
            "FACE_1,atlas.png,0,0,8,4\nFACE_2,atlas.png,8,0,8,4\n");
        File.WriteAllBytes(Path.Combine(_gameDir, "resources", "atlas.png"), Png(16, 8));
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

    // ---------- ConsoleImagePart：无 sprite（HEADLESS 恒无）+ 探针成功 ----------

    [Fact]
    public void Img_height_only_derives_width_by_aspect_ratio()
    {
        var part = new ConsoleImagePart("img/portrait.png", null, null, Px(50), null, null);

        // 200×100，height=50px → width = 200×50/100 = 100
        Assert.Equal(100, part.Width);
        Assert.Equal(50, part.Height);
        Assert.Equal(0, part.YPos);
        Assert.Equal(0, part.Top);
        Assert.Equal(50, part.Bottom);
    }

    [Fact]
    public void Img_px_width_and_height_use_raw_values()
    {
        var part = new ConsoleImagePart("img/portrait.png", null, null, Px(50), Px(40), null);

        Assert.Equal(40, part.Width);
        Assert.Equal(50, part.Height);
    }

    [Fact]
    public void Img_percent_dimensions_scale_with_font_size()
    {
        // FontSize=18：height=200% → 36，width=50% → 9
        var part = new ConsoleImagePart("img/portrait.png", null, null,
            new Utils.MixedNum { num = 200, isPx = false },
            new Utils.MixedNum { num = 50, isPx = false }, null);

        Assert.Equal(9, part.Width);
        Assert.Equal(36, part.Height);
    }

    [Fact]
    public void Img_negative_ypos_shifts_above_line()
    {
        var part = new ConsoleImagePart("img/portrait.png", null, null, Px(50), null, Px(-50));

        Assert.Equal(-50, part.YPos);
        Assert.Equal(-50, part.Top);
        Assert.Equal(0, part.Bottom);
    }

    [Fact]
    public void Img_ypos_beyond_line_height_extends_bottom()
    {
        // ypos=50px 且高 50px → Bottom=100，超默认行高 18
        var part = new ConsoleImagePart("img/portrait.png", null, null, Px(50), null, Px(50));

        Assert.Equal(50, part.Top);
        Assert.Equal(100, part.Bottom);
    }

    [Fact]
    public void Img_missing_file_keeps_explicit_params_and_alttext()
    {
        // 探针失败：显式 width/height 参数仍生效（ticket 01：回退参数），Text 保留 AltText 调试标记
        var part = new ConsoleImagePart("img/nope.png", null, null, Px(50), Px(40), null);

        Assert.Equal(40, part.Width);
        Assert.Equal(50, part.Height);
        Assert.StartsWith("<img", part.Text);
    }

    [Fact]
    public void Img_missing_file_width_defaults_to_zero()
    {
        // 探针失败 + width 缺省：无法纵横比推算 → 回退 0（ticket 01：回退参数或 0）；height 仍用参数
        var part = new ConsoleImagePart("img/nope.png", null, null, Px(50), null, null);

        Assert.Equal(0, part.Width);
        Assert.Equal(50, part.Height);
        Assert.Equal(0, part.YPos);
    }

    // ---------- issue 07：裁切矩形几何（协议 v9） ----------
    // 期望值独立推导：裁切 (x,y,w,h) 缩放绘制到显示尺寸 (W,H)——缩放系数 = 显示/裁切，
    // img 元素尺寸 = 整图×缩放，img 偏移 = -原点×缩放（WinForms SpriteF.GraphicsDraw 语义）。

    [Fact]
    public void Cropped_sprite_reports_scaled_crop_geometry()
    {
        // FACE_1 裁切 (0,0,8,4) @ 整图 16×8，height=18px → 显示 36×18；
        // 缩放 36/8=4.5、18/4=4.5：img 元素 16×4.5=72 × 8×4.5=36，原点 (0,0) → 偏移 0
        var part = new ConsoleImagePart("Face_1", null, null, Px(18), null, null);

        Assert.True(part.HasCrop);
        Assert.Equal(36, part.Width);
        Assert.Equal(18, part.Height);
        Assert.Equal(0, part.CropX);
        Assert.Equal(0, part.CropY);
        Assert.Equal(72, part.CropImgWidth);
        Assert.Equal(36, part.CropImgHeight);
    }

    [Fact]
    public void Cropped_sprite_with_offset_origin_scales_margins_negative()
    {
        // FACE_2 裁切原点 (8,0)：img 左移 8×4.5 = 36 → margin-left = -36
        var part = new ConsoleImagePart("Face_2", null, null, Px(18), null, null);

        Assert.True(part.HasCrop);
        Assert.Equal(36, part.Width);
        Assert.Equal(-36, part.CropX);
        Assert.Equal(0, part.CropY);
        Assert.Equal(72, part.CropImgWidth);
        Assert.Equal(36, part.CropImgHeight);
    }

    [Fact]
    public void Cropped_sprite_with_explicit_display_size_scales_geometry()
    {
        // FACE_1 + height=90px → 显示 180×90（8×4 纵横比）；缩放 180/8=22.5、90/4=22.5：
        // img 元素 16×22.5=360 × 8×22.5=180
        var part = new ConsoleImagePart("Face_1", null, null, Px(90), null, null);

        Assert.True(part.HasCrop);
        Assert.Equal(180, part.Width);
        Assert.Equal(90, part.Height);
        Assert.Equal(360, part.CropImgWidth);
        Assert.Equal(180, part.CropImgHeight);
    }

    [Fact]
    public void Direct_path_image_has_no_crop()
    {
        // 直接相对路径（无 csv 裁切）→ HasCrop false
        var part = new ConsoleImagePart("img/portrait.png", null, null, Px(50), null, null);

        Assert.False(part.HasCrop);
        Assert.Equal(0, part.CropImgWidth);
    }

    [Fact]
    public void PrintImg_flows_through_layout_with_derived_width()
    {
        // 集成：PrintImg 指令 → 节点几何 → 布局宽度，全链路无 sprite。
        // GamePaths.Current 是全局静态，xUnit 并行下可能被其他测试类覆盖——
        // 使用时 Resolve 幂等覆盖（LoaderTestHarness 同款模式），紧邻 PrintImg 缩小竞态窗口。
        GamePaths.Resolve(_gameDir, new FileSystemGameDirAccessor());
        var ui = new HeadlessConsole();
        var console = new EmueraConsole(ui, new NullTerminalSetup());
        console.PrintImg("img/portrait.png", null!, null!, Px(50), null!, null);

        var line = console.PrintBuffer.FlushSingleLine(new StringMeasure(), false);

        var btn = Assert.Single(line.Buttons);
        Assert.Equal(100, btn.Width); // 200×100 + height=50px → 纵横比推算 width=100
    }

    // ---------- ConsoleRectangleShapePart：无头下 rect 几何 ----------

    [Fact]
    public void Rect_one_param_fills_line_height()
    {
        var shape = (ConsoleRectangleShapePart)ConsoleShapePart.CreateShape(
            "rect", [new Utils.MixedNum { num = 100 }], default, default, false);
        shape.SetWidth(new StringMeasure(), 0);

        // 1 参 = 整行色条（宽 100% → FontSize 18）。SetWidth 公式（WinForms 原版）：
        //   Width=18，rectX=0+shift(3)，rectWidth=Width-0=18 → Rect=(3,0,18,18)，Y=0 高=行高 18
        Assert.Equal(18, shape.Width);
        Assert.Equal(new EmuRectangle(3, 0, 18, 18), shape.Rect);
        Assert.Equal(0, shape.Top);
        Assert.Equal(18, shape.Bottom);
    }

    [Fact]
    public void Rect_four_params_absolute_position()
    {
        var shape = (ConsoleRectangleShapePart)ConsoleShapePart.CreateShape(
            "rect", [Px(10), Px(20), Px(30), Px(40)], default, default, false);
        shape.SetWidth(new StringMeasure(), 0);

        // 4 参 = x/y/w/h（px）。SetWidth 公式：Width=10+30=40，rectX=10+shift(3)=13，
        //   rectWidth=40-10=30 → Rect=(13,20,30,40)；Top=min(0,20)=0，Bottom=max(18,60)=60
        Assert.Equal(40, shape.Width);
        Assert.Equal(new EmuRectangle(13, 20, 30, 40), shape.Rect);
        Assert.Equal(0, shape.Top);
        Assert.Equal(60, shape.Bottom);
    }

    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
