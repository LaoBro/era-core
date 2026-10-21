using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using Xunit;
using Utils = MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace Emuera.Headless.Tests;

/// <summary>
/// 02 — TestAdapter v8 往返测试（spec 测试 seam ①，ADR-0013 决策四）。
/// TestAdapter 能从 v8 协议输出重建显示状态：image/shape segment、bgImages、
/// 三个背景 op 的往返重建断言。若 TestAdapter 能做到，任何前端也能。
/// </summary>
[Collection("GamePathsIsolated")]
public class AdapterV8Tests : IDisposable
{
    private readonly string _gameDir;
    private readonly IDisposable _scope;

    public AdapterV8Tests()
    {
        _gameDir = Path.Combine(Path.GetTempPath(), "emuera-v8ta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_gameDir, "img"));
        GamePaths.Resolve(_gameDir, new FileSystemGameDirAccessor());
        _scope = GlobalStatic.OpenScope(new ConfigData());
        File.WriteAllBytes(Path.Combine(_gameDir, "img", "portrait.png"), Png(200, 100));
        // issue 07：图集夹具——atlas.png 16×8（2×1 网格）；FACE_2=右格 (8,0,8,4)
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

    // ---------- 快照：image/shape segment + bgImages ----------

    [Fact]
    public void T_snapshot_reconstructs_image_and_shape_segments()
    {
        // 图片节点 + 矩形节点同一行（几何需先布局）
        var img = new ConsoleImagePart("img/portrait.png", "img/btn.png", "img/map.png",
            new Utils.MixedNum { num = 50, isPx = true }, null, null);
        var rect = (ConsoleRectangleShapePart)ConsoleShapePart.CreateShape(
            "rect", [new Utils.MixedNum { num = 100 }], new EmuColor(255, 0, 0), default, false);
        rect.SetWidth(new StringMeasure(), 0);
        var btn = new ConsoleButtonString(null!, new AConsoleDisplayNode[] { img, rect });
        var line = new ConsoleDisplayLine(new[] { btn }, isLogical: true, temporary: false);

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { line }, EmuColor.Black, ConsoleState.WaitInput, null, "TestFont",
            bgImages: [new BgImageState("bg/forest.png", 1, 0.5f)]);

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        var segs = adapter.Lines[0].Entries[0].Segments;
        Assert.Equal(2, segs.Count);
        var imageSeg = segs[0];
        Assert.NotNull(imageSeg.image);
        Assert.Equal("img/portrait.png", imageSeg.image!.src);
        Assert.Equal("img/btn.png", imageSeg.image.srcb);
        Assert.Equal("img/map.png", imageSeg.image.srcm);
        Assert.Equal(100, imageSeg.image.width);
        Assert.Equal(50, imageSeg.image.height);
        var shapeSeg = segs[1];
        Assert.NotNull(shapeSeg.shape);
        Assert.Equal("rect", shapeSeg.shape!.type);
        Assert.Equal("#FF0000", shapeSeg.shape.color);

        var bg = Assert.Single(adapter.BgImages!);
        Assert.Equal("bg/forest.png", bg.src);
        Assert.Equal(1, bg.depth);
        Assert.Equal(0.5f, bg.opacity);
    }

    // ---------- issue 07：裁切矩形 segment 往返 ----------

    [Fact]
    public void T_snapshot_reconstructs_cropped_image_segment()
    {
        // 图集 sprite（FACE_2 裁切 (8,0,8,4) @ 整图 16×8）→ 快照 → TestAdapter 往返，
        // crop 字段（已缩放几何）完整保留——前端能做到的任何重建，TestAdapter 都能。
        var img = new ConsoleImagePart("Face_2", null, null,
            new Utils.MixedNum { num = 18, isPx = true }, null, null);
        var btn = new ConsoleButtonString(null!, new AConsoleDisplayNode[] { img });
        var line = new ConsoleDisplayLine(new[] { btn }, isLogical: true, temporary: false);

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { line }, EmuColor.Black, ConsoleState.WaitInput, null, "TestFont");

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        var imageSeg = adapter.Lines[0].Entries[0].Segments[0].image!;
        Assert.Equal("Face_2", imageSeg.src);
        Assert.Equal(36, imageSeg.width);
        Assert.Equal(18, imageSeg.height);
        Assert.NotNull(imageSeg.crop);
        // 缩放 36/8=4.5：原点 (8,0) → margin-left -36；img 元素 16×4.5=72 × 8×4.5=36
        Assert.Equal(-36, imageSeg.crop!.x);
        Assert.Equal(0, imageSeg.crop.y);
        Assert.Equal(72, imageSeg.crop.imgWidth);
        Assert.Equal(36, imageSeg.crop.imgHeight);
    }

    [Fact]
    public void T_snapshot_uncropped_image_has_null_crop()
    {
        // 直接相对路径（无裁切）→ crop 缺省 null，JSON 省略（WhenWritingNull）
        var img = new ConsoleImagePart("img/portrait.png", null, null,
            new Utils.MixedNum { num = 50, isPx = true }, null, null);
        var btn = new ConsoleButtonString(null!, new AConsoleDisplayNode[] { img });
        var line = new ConsoleDisplayLine(new[] { btn }, isLogical: true, temporary: false);

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { line }, EmuColor.Black, ConsoleState.WaitInput, null, "TestFont");
        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        var imageSeg = adapter.Lines[0].Entries[0].Segments[0].image!;
        Assert.Null(imageSeg.crop);
    }

    // ---------- diff：bgImages 变更 ----------

    [Fact]
    public void T_diff_updates_bg_images_state()
    {
        var adapter = new TestAdapter();
        var diff = new DisplayDiff(
            new List<LineOp>(),
            bgColor: null,
            bgImages: [new BgImageState("bg/sea.png", 2, 1f)]);

        adapter.ApplyDiff(diff);

        var bg = Assert.Single(adapter.BgImages!);
        Assert.Equal("bg/sea.png", bg.src);
        Assert.Equal(2, bg.depth);
    }

    [Fact]
    public void T_diff_cleared_bg_images_becomes_empty_list()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(new List<LineOp>(), null,
            [new BgImageState("bg/sea.png", 2, 1f)]));

        adapter.ApplyDiff(new DisplayDiff(new List<LineOp>(), null, new List<BgImageState>()));

        Assert.Empty(adapter.BgImages!);
    }

    // ---------- ops：三个背景 op 重放 ----------

    [Fact]
    public void T_ops_set_remove_clear_bg_image_replay_state()
    {
        var adapter = new TestAdapter();

        adapter.ApplyOps(new List<TurnOp>
        {
            new SetBgImageOp("bg/a.png", 0, 1f),
            new SetBgImageOp("bg/b.png", 1, 0.5f),
            new RemoveBgImageOp("bg/a.png"),
        });

        var bg = Assert.Single(adapter.BgImages!);
        Assert.Equal("bg/b.png", bg.src);

        adapter.ApplyOps(new List<TurnOp> { new ClearBgImageOp() });
        Assert.Empty(adapter.BgImages!);
    }

    // ---------- JSON wire 往返（spec seam ①："序列化→重建"） ----------

    [Fact]
    public void T_json_wire_carries_v8_fields_and_keeps_v7_fields()
    {
        // 构造含 image/shape/bgImages 的快照
        var img = new ConsoleImagePart("img/portrait.png", "img/btn.png", "img/map.png",
            new Utils.MixedNum { num = 50, isPx = true }, null, null);
        var rect = (ConsoleRectangleShapePart)ConsoleShapePart.CreateShape(
            "rect", [new Utils.MixedNum { num = 100 }], new EmuColor(255, 0, 0), default, false);
        rect.SetWidth(new StringMeasure(), 0);
        var btn = new ConsoleButtonString(null!, new AConsoleDisplayNode[] { img, rect });
        var line = new ConsoleDisplayLine(new[] { btn }, isLogical: true, temporary: false);
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { line }, EmuColor.Black, ConsoleState.WaitInput, null, "TestFont",
            bgImages: [new BgImageState("bg/forest.png", 1, 0.5f)]);

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        using var doc = JsonDocument.Parse(json);

        // v8 新字段在 wire 上以 spec 形状出现
        var segs = doc.RootElement.GetProperty("lines")[0].GetProperty("entries")[0].GetProperty("segments");
        var image = segs[0].GetProperty("image");
        Assert.Equal("img/portrait.png", image.GetProperty("src").GetString());
        Assert.Equal("img/btn.png", image.GetProperty("srcb").GetString());
        Assert.Equal(100, image.GetProperty("width").GetInt32());
        Assert.Equal(50, image.GetProperty("height").GetInt32());
        Assert.Equal("rect", segs[1].GetProperty("shape").GetProperty("type").GetString());
        Assert.Equal("#FF0000", segs[1].GetProperty("shape").GetProperty("color").GetString());
        var bg = doc.RootElement.GetProperty("bgImages")[0];
        Assert.Equal("bg/forest.png", bg.GetProperty("src").GetString());
        Assert.Equal(1, bg.GetProperty("depth").GetInt32());
        Assert.Equal(0.5, bg.GetProperty("opacity").GetDouble());
        Assert.Equal(12, doc.RootElement.GetProperty("protocolVersion").GetInt32());

        // v7 已知字段仍在——老客户端按 v7 解析不炸、忽略未知字段自然降级
        Assert.Equal("#000000", doc.RootElement.GetProperty("bgColor").GetString());
        // v12：快照的输入状态只由 awaiting 表达，顶层 state 已退场。
        // 本夹具 currentRequest=null ⇒ 两个正交维度皆无（快照瞬态形态 `{}`）。
        Assert.False(doc.RootElement.TryGetProperty("state", out _));
        var awaiting = doc.RootElement.GetProperty("awaiting");
        Assert.False(awaiting.TryGetProperty("input", out _));
        Assert.False(awaiting.TryGetProperty("wait", out _));

        // 反序列化回对象：字段完整
        var back = JsonSerializer.Deserialize<DisplaySnapshot>(json, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        Assert.NotNull(back);
        Assert.Single(back!.bgImages!);
        Assert.Equal("bg/forest.png", back.bgImages![0].src);
        Assert.Equal("img/portrait.png", back.lines[0].entries[0].segments[0].image!.src);
        Assert.Equal("rect", back.lines[0].entries[0].segments[1].shape!.type);
    }

    [Fact]
    public void T_json_wire_carries_crop_fields()
    {
        // issue 07：裁切图 sprite → wire 上 crop 以 {x,y,imgWidth,imgHeight} 形状出现
        var img = new ConsoleImagePart("Face_2", null, null,
            new Utils.MixedNum { num = 18, isPx = true }, null, null);
        var btn = new ConsoleButtonString(null!, new AConsoleDisplayNode[] { img });
        var line = new ConsoleDisplayLine(new[] { btn }, isLogical: true, temporary: false);
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { line }, EmuColor.Black, ConsoleState.WaitInput, null, "TestFont");

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        using var doc = JsonDocument.Parse(json);

        var image = doc.RootElement.GetProperty("lines")[0].GetProperty("entries")[0]
            .GetProperty("segments")[0].GetProperty("image");
        var crop = image.GetProperty("crop");
        Assert.Equal(-36, crop.GetProperty("x").GetInt32());
        Assert.Equal(0, crop.GetProperty("y").GetInt32());
        Assert.Equal(72, crop.GetProperty("imgWidth").GetInt32());
        Assert.Equal(36, crop.GetProperty("imgHeight").GetInt32());
        Assert.Equal(12, doc.RootElement.GetProperty("protocolVersion").GetInt32());
    }
}
