using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Text;
using MinorShift.Emuera;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// sprite 名 → 相对游戏根文件路径 映射表（无头复刻 WinForms <c>AppContents.LoadContents</c> 的 csv 扫描）。
/// <para>
/// era 游戏 HTML <c>&lt;img src='Face_1'&gt;</c> 的 src 是 <b>sprite 名</b>（如 <c>FACE_1</c>），真实文件是
/// <c>resources/1_Face.png</c>——映射在 <c>resources/*.csv</c> 第一、二列（<c>FACE_1,1_Face.png,0,0,180,180</c>）。
/// 无头下 LoadContents 是 stub（sprite 表不构建），本表只解析「名→文件路径」供探针/资源通道复用，不加载位图。
/// </para>
/// </summary>
public class ImageNameTableTests : IDisposable
{
    static ImageNameTableTests()
    {
        // SJIS(932) 代码页在 .NET Core 默认不注册（引擎启动时注册，测试进程需手动）
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private readonly string _root;
    private readonly FileSystemGameDirAccessor _accessor = new();

    public ImageNameTableTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "emuera_imgnametable_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "resources"));
        Directory.CreateDirectory(Path.Combine(_root, "resources", "sub"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* 临时目录清理失败可忽略 */ }
    }

    private void WriteFile(string relPath, string content, Encoding? enc = null)
    {
        var p = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content, enc ?? new UTF8Encoding(false));
    }

    [Fact]
    public void Resolve_sprite_name_maps_to_resources_file()
    {
        WriteFile(Path.Combine("resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "Face_1", out var path));
        Assert.Equal("resources/1_Face.png", path);
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        WriteFile(Path.Combine("resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "face_1", out var path));
        Assert.Equal("resources/1_Face.png", path);
        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "FaCe_1", out _));
    }

    [Fact]
    public void Resolve_second_csv_column()
    {
        WriteFile(Path.Combine("resources", "Custom.csv"), "c1,c1.png,0,0,180,180\n");

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "c1", out var path));
        Assert.Equal("resources/c1.png", path);
    }

    [Fact]
    public void Anime_row_is_skipped()
    {
        WriteFile(Path.Combine("resources", "Anime.csv"), "anim,ANIME,180,180\n");

        Assert.False(ImageNameTable.TryResolve(_accessor, _root, "anim", out _));
    }

    [Fact]
    public void Row_without_extension_is_skipped()
    {
        WriteFile(Path.Combine("resources", "Bad.csv"), "bad,noext\n");

        Assert.False(ImageNameTable.TryResolve(_accessor, _root, "bad", out _));
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        WriteFile(Path.Combine("resources", "Face.csv"),
            "; comment line\r\n\r\n   \nFACE_1,1_Face.png,0,0,180,180\n");

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "Face_1", out var path));
        Assert.Equal("resources/1_Face.png", path);
    }

    [Fact]
    public void Subdirectory_csv_resolves_relative_to_root()
    {
        WriteFile(Path.Combine("resources", "sub", "x.csv"), "x1,x1.png,0,0,100,100\n");

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "x1", out var path));
        Assert.Equal("resources/sub/x1.png", path);
    }

    [Fact]
    public void Unknown_name_returns_false()
    {
        WriteFile(Path.Combine("resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");

        Assert.False(ImageNameTable.TryResolve(_accessor, _root, "nope", out _));
    }

    [Fact]
    public void Directory_switch_reloads_map()
    {
        WriteFile(Path.Combine("resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");
        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "Face_1", out var p1));
        Assert.Equal("resources/1_Face.png", p1);

        // 第二个游戏根：同名 sprite 映射到不同文件
        var root2 = Path.Combine(Path.GetTempPath(), "emuera_imgnametable2_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root2, "resources"));
            File.WriteAllText(Path.Combine(root2, "resources", "Face.csv"), "FACE_1,2_Face.png,0,0,180,180\n");

            Assert.True(ImageNameTable.TryResolve(_accessor, root2, "Face_1", out var p2));
            Assert.Equal("resources/2_Face.png", p2);
            // 回切 root1 仍正确
            Assert.True(ImageNameTable.TryResolve(_accessor, _root, "Face_1", out var p3));
            Assert.Equal("resources/1_Face.png", p3);
        }
        finally
        {
            try { Directory.Delete(root2, true); } catch { }
        }
    }

    [Fact]
    public void ShiftJis_csv_decodes_correctly()
    {
        // TK 的 resources csv 是 Shift-JIS（含日文文件名）
        WriteFile(Path.Combine("resources", "Face.csv"),
            "立ち絵,立ち絵.png,0,0,200,300\n",
            Encoding.GetEncoding(932));

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "立ち絵", out var path));
        Assert.Equal("resources/立ち絵.png", path);
    }

    [Fact]
    public void Utf8Bom_csv_strips_bom_from_first_name()
    {
        // UTF-8 带 BOM 的 csv：GetString 保留 \uFEFF，若不剥除首行 name 前缀 BOM 永 miss
        WriteFile(Path.Combine("resources", "Face.csv"),
            "FACE_1,1_Face.png,0,0,180,180\n",
            new UTF8Encoding(true)); // encoderShouldEmitUTF8Identifier = true → 写 BOM

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "Face_1", out var path));
        Assert.Equal("resources/1_Face.png", path);
    }

    [Fact]
    public void Resources_dir_without_csv_returns_false()
    {
        // resources/ 存在但无 csv：表空（已缓存），任何查询 false
        Assert.False(ImageNameTable.TryResolve(_accessor, _root, "Face_1", out _));
    }

    // ---------- issue 07：裁切矩形（csv 第 3-6 列 tokens[2..5]） ----------

    [Fact]
    public void Crop_rect_parsed_from_columns_3_to_6()
    {
        WriteFile(Path.Combine("resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");

        Assert.True(ImageNameTable.TryResolveEntry(_accessor, _root, "Face_1", out var entry));
        Assert.NotNull(entry);
        Assert.Equal("resources/1_Face.png", entry!.RelativePath);
        Assert.Equal(new SpriteCrop(0, 0, 180, 180), entry.Crop);
    }

    [Fact]
    public void Crop_rect_with_offset_origin_parsed()
    {
        // 图集右格：原点 (8,0)，裁切 8×4
        WriteFile(Path.Combine("resources", "Atlas.csv"), "FACE_2,1_Face.png,8,0,8,4\n");

        Assert.True(ImageNameTable.TryResolveEntry(_accessor, _root, "Face_2", out var entry));
        Assert.Equal(new SpriteCrop(8, 0, 8, 4), entry!.Crop);
    }

    [Fact]
    public void Row_without_crop_columns_has_null_crop()
    {
        // 仅 name,file 两列（无裁切）→ Crop null（全图）
        WriteFile(Path.Combine("resources", "Simple.csv"), "FACE_1,1_Face.png\n");

        Assert.True(ImageNameTable.TryResolveEntry(_accessor, _root, "Face_1", out var entry));
        Assert.Null(entry!.Crop);
    }

    [Fact]
    public void Row_with_partial_crop_columns_has_null_crop()
    {
        // 4 列（name,file,x,y）不足 6 列 → WinForms tokens.Length>=6 才解析矩形 → Crop null
        WriteFile(Path.Combine("resources", "Partial.csv"), "FACE_1,1_Face.png,0,0\n");

        Assert.True(ImageNameTable.TryResolveEntry(_accessor, _root, "Face_1", out var entry));
        Assert.Null(entry!.Crop);
    }

    [Fact]
    public void Row_with_non_numeric_crop_has_null_crop()
    {
        WriteFile(Path.Combine("resources", "Bad.csv"), "FACE_1,1_Face.png,a,b,180,180\n");

        Assert.True(ImageNameTable.TryResolveEntry(_accessor, _root, "Face_1", out var entry));
        Assert.Null(entry!.Crop);
    }

    [Fact]
    public void Row_with_zero_size_crop_has_null_crop()
    {
        // w 或 h ≤ 0 → 非合法裁切（WinForms 正性校验）→ Crop null
        WriteFile(Path.Combine("resources", "Zero.csv"), "FACE_1,1_Face.png,0,0,0,180\nFACE_2,1_Face.png,0,0,180,0\n");

        Assert.True(ImageNameTable.TryResolveEntry(_accessor, _root, "Face_1", out var e1));
        Assert.Null(e1!.Crop);
        Assert.True(ImageNameTable.TryResolveEntry(_accessor, _root, "Face_2", out var e2));
        Assert.Null(e2!.Crop);
    }

    [Fact]
    public void TryResolve_still_returns_path_only()
    {
        // 兼容既有调用方：TryResolve 只出路径，裁切经 TryResolveEntry
        WriteFile(Path.Combine("resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");

        Assert.True(ImageNameTable.TryResolve(_accessor, _root, "Face_1", out var path));
        Assert.Equal("resources/1_Face.png", path);
    }
}
