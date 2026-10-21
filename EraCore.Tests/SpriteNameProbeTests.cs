using MinorShift.Emuera.Files;
using System;
using System.IO;
using MinorShift.Emuera;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 探针 sprite 名回退（issue：era 游戏 <c>&lt;img src='Face_1'&gt;</c> 的 src 是 sprite 名，
/// 映射在 resources/*.csv）。<c>TryGetImageSize</c> 先按根相对路径试（兼容 test_game 的
/// <c>img/test.png</c>），失败后经 <see cref="ImageNameTable"/> 查 sprite 名 → 文件再探针。
/// GamePaths 敏感（静态 Current）→ 放串行隔离集合。
/// </summary>
[Collection("GamePathsIsolated")]
public class SpriteNameProbeTests : IDisposable
{
    private readonly string _root;
    private readonly GamePaths _paths;

    public SpriteNameProbeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "emuera_spriteprobe_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "resources"));
        Directory.CreateDirectory(Path.Combine(_root, "img"));
        // 图集夹具：1_Face.png 为 16×8（2×1 网格，每格 8×4）。
        // FACE_1=左格 (0,0,8,4)、FACE_2=右格 (8,0,8,4)；
        // FULL=无裁切列（整图）、BIG=裁切越界 (180×180 > 16×8，宽容回退整图)
        File.WriteAllText(Path.Combine(_root, "resources", "Face.csv"),
            "FACE_1,1_Face.png,0,0,8,4\nFACE_2,1_Face.png,8,0,8,4\nFULL,full.png\nBIG,1_Face.png,0,0,180,180\n");
        File.WriteAllBytes(Path.Combine(_root, "resources", "1_Face.png"), AtlasPng()); // 16×8
        File.WriteAllBytes(Path.Combine(_root, "resources", "full.png"), MiniPng());   // 8×4 整图
        // 直接相对路径（test_game 形态）
        File.WriteAllBytes(Path.Combine(_root, "img", "test.png"), MiniPng());
        _paths = GamePaths.Resolve(_root, new FileSystemGameDirAccessor());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void Sprite_name_returns_probed_size()
    {
        // Face_1 裁切 (0,0,8,4) 在图集 16×8 内 → 探针尺寸 = 裁切尺寸
        Assert.True(AppContents.TryGetImageSize("Face_1", out var w, out var h));
        Assert.Equal(8, w);
        Assert.Equal(4, h);
    }

    [Fact]
    public void Direct_relative_path_still_works()
    {
        // 兼容 test_game：相对路径优先，不经 sprite 表
        Assert.True(AppContents.TryGetImageSize("img/test.png", out var w, out var h));
        Assert.Equal(8, w);
        Assert.Equal(4, h);
    }

    [Fact]
    public void Unknown_name_returns_false()
    {
        Assert.False(AppContents.TryGetImageSize("nope", out _, out _));
    }

    // ---------- issue 07：裁切尺寸语义 ----------

    [Fact]
    public void TryGetImageSize_returns_crop_size_for_cropped_sprite()
    {
        // Face_2 裁切 (8,0,8,4) → 尺寸 = 裁切矩形尺寸（非整图 16×8）
        Assert.True(AppContents.TryGetImageSize("Face_2", out var w, out var h));
        Assert.Equal(8, w);
        Assert.Equal(4, h);
    }

    [Fact]
    public void TryGetImageSize_out_of_range_crop_falls_back_to_full_size()
    {
        // BIG 裁切 180×180 > 整图 16×8 → 宽容回退整图尺寸
        Assert.True(AppContents.TryGetImageSize("BIG", out var w, out var h));
        Assert.Equal(16, w);
        Assert.Equal(8, h);
    }

    /// <summary>8×4 RGBA PNG 头（signature + IHDR + IEND）——探针只需前 24 字节。</summary>
    private static byte[] MiniPng()
    {
        return PngHeader(8, 4);
    }

    /// <summary>16×8 RGBA PNG 头（图集夹具，2×1 网格）——探针只需 IHDR。</summary>
    private static byte[] AtlasPng()
    {
        return PngHeader(16, 8);
    }

    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(bytes, 0);
        // IHDR chunk: len(4) tag(4) width(4) height(4) bitdepth(1) colortype(1) ...
        byte[] ihdr = [
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
            0x08, 0x06, 0x00, 0x00, 0x00,
        ];
        ihdr.CopyTo(bytes, 8);
        // IEND 占位（探针不校验 CRC）
        bytes[29] = 0x49; bytes[30] = 0x45; bytes[31] = 0x4E; bytes[32] = 0x44;
        return bytes;
    }

    // ---------- B：GetSprite 无头替身（SPRITECREATED 依赖它） ----------

    [Fact]
    public void GetSprite_sprite_name_returns_created_stub_with_size()
    {
        var sprite = AppContents.GetSprite("Face_1");
        Assert.NotNull(sprite);
        Assert.True(sprite!.IsCreated); // SPRITECREATED 判定依赖
        Assert.Equal(8, sprite.DestBaseSize.Width);   // 裁切 8×4
        Assert.Equal(4, sprite.DestBaseSize.Height);
        // issue 07：替身携带整图尺寸 + 裁切原点
        var hs = Assert.IsType<HeadlessSprite>(sprite);
        Assert.Equal(new EmuSize(16, 8), hs.SourceSize);
        Assert.Equal(new EmuPoint(0, 0), hs.CropOrigin);
        Assert.True(hs.HasCrop); // 裁切 8×4 ≠ 整图 16×8
    }

    [Fact]
    public void GetSprite_cropped_sprite_carries_source_and_origin()
    {
        // Face_2：右格 (8,0,8,4)——DestBaseSize=裁切尺寸、SourceSize=整图、原点偏移
        var sprite = AppContents.GetSprite("Face_2");
        Assert.NotNull(sprite);
        var hs = Assert.IsType<HeadlessSprite>(sprite!);
        Assert.Equal(new EmuSize(8, 4), hs.DestBaseSize);
        Assert.Equal(new EmuSize(16, 8), hs.SourceSize);
        Assert.Equal(new EmuPoint(8, 0), hs.CropOrigin);
        Assert.True(hs.HasCrop);
    }

    [Fact]
    public void GetSprite_full_image_sprite_has_no_crop()
    {
        // FULL：无裁切列 → 整图，HasCrop false（无容器裁剪）
        var sprite = AppContents.GetSprite("FULL");
        Assert.NotNull(sprite);
        var hs = Assert.IsType<HeadlessSprite>(sprite!);
        Assert.Equal(new EmuSize(8, 4), hs.DestBaseSize);
        Assert.Equal(new EmuSize(8, 4), hs.SourceSize);
        Assert.False(hs.HasCrop);
    }

    [Fact]
    public void GetSprite_out_of_range_crop_falls_back_to_full()
    {
        // BIG：裁切 180×180 > 整图 16×8 → 宽容回退整图（WinForms 告警跳过，无头不破显示）
        var sprite = AppContents.GetSprite("BIG");
        Assert.NotNull(sprite);
        var hs = Assert.IsType<HeadlessSprite>(sprite!);
        Assert.Equal(new EmuSize(16, 8), hs.DestBaseSize);
        Assert.False(hs.HasCrop);
    }

    [Fact]
    public void GetSprite_unknown_name_returns_null()
    {
        Assert.Null(AppContents.GetSprite("nope"));
    }

    [Fact]
    public void GetSprite_direct_relative_path_returns_stub()
    {
        // test_game 形态：相对路径（img/test.png）也能建替身
        var sprite = AppContents.GetSprite("img/test.png");
        Assert.NotNull(sprite);
        Assert.True(sprite!.IsCreated);
        Assert.Equal(8, sprite.DestBaseSize.Width);
        Assert.False(Assert.IsType<HeadlessSprite>(sprite).HasCrop);
    }

    [Fact]
    public void GetSprite_switches_map_on_game_root_change()
    {
        // 目录切换：新根同名 sprite → 新文件尺寸
        var root2 = Path.Combine(Path.GetTempPath(), "emuera_spriteprobe2_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root2, "resources"));
            // 16×4 PNG：IHDR width=16
            var png = MiniPng();
            png[16] = 0; png[17] = 0; png[18] = 0; png[19] = 16;
            File.WriteAllBytes(Path.Combine(root2, "resources", "1_Face.png"), png);
            File.WriteAllText(Path.Combine(root2, "resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");
            GamePaths.Resolve(root2, new FileSystemGameDirAccessor());
            try
            {
                var sprite = AppContents.GetSprite("Face_1");
                Assert.NotNull(sprite);
                Assert.Equal(16, sprite!.DestBaseSize.Width);
            }
            finally
            {
                GamePaths.Resolve(_root, new FileSystemGameDirAccessor());
            }
        }
        finally
        {
            try { Directory.Delete(root2, true); } catch { }
        }
    }
}
