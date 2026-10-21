using MinorShift.Emuera.Files;
using System;
using System.IO;
using MinorShift.Emuera;
using MinorShift.Emuera.Assets;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 05 — AssetChannel 共享资源通道（spec Q5 单点实现：消毒/白名单/字节/MIME 四步收敛）。
/// 03 的 Kestrel /assets handler 内联了这四步；05 双平台（Windows 拦截 + 安卓 PathHandler）
/// 复用同一函数，保证"校验单点实现"。
/// </summary>
public class AssetChannelTests : IDisposable
{
    private readonly string _root;
    private readonly FileSystemGameDirAccessor _accessor = new();

    public AssetChannelTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "emuera_asset_channel_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "img"));
        Directory.CreateDirectory(Path.Combine(_root, "csv"));
        Directory.CreateDirectory(Path.Combine(_root, "resources"));
        // 手工构造最小 PNG（签名 + IHDR + IEND，尺寸 8×4）——与探针测试同款字节
        File.WriteAllBytes(Path.Combine(_root, "img", "test.png"), MiniPng());
        File.WriteAllText(Path.Combine(_root, "csv", "GameBase.csv"), "dummy");
        // sprite 表（era 的 Face_1 → resources/1_Face.png）
        File.WriteAllText(Path.Combine(_root, "resources", "Face.csv"), "FACE_1,1_Face.png,0,0,180,180\n");
        File.WriteAllBytes(Path.Combine(_root, "resources", "1_Face.png"), MiniPng());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Valid_image_path_returns_bytes_and_mime()
    {
        var expected = MiniPng();
        Assert.True(AssetChannel.TryGetImage(_accessor, _root, "img/test.png", out var bytes, out var mime));
        Assert.Equal(expected, bytes); // 原样返回文件内容（TryGetImage 不解析内容，仅文件名+扩展名白名单）
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void Sprite_name_resolves_via_resources_csv()
    {
        // era：<img src='Face_1'> 的 src 是 sprite 名（无扩展名）→ 资源通道经 resources csv 映射
        Assert.True(AssetChannel.TryGetImage(_accessor, _root, "Face_1", out var bytes, out var mime));
        Assert.Equal(MiniPng(), bytes);
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void Sprite_name_case_insensitive()
    {
        Assert.True(AssetChannel.TryGetImage(_accessor, _root, "face_1", out _, out _));
    }

    [Fact]
    public void Unknown_sprite_name_rejected()
    {
        Assert.False(AssetChannel.TryGetImage(_accessor, _root, "nope", out _, out _));
    }

    [Fact]
    public void Non_image_extension_rejected()
    {
        Assert.False(AssetChannel.TryGetImage(_accessor, _root, "csv/GameBase.csv", out _, out _));
    }

    [Fact]
    public void Traversal_rejected()
    {
        Assert.False(AssetChannel.TryGetImage(_accessor, _root, "../outside.png", out _, out _));
        Assert.False(AssetChannel.TryGetImage(_accessor, _root, "img/%2e%2e/csv/GameBase.csv", out _, out _));
    }

    [Fact]
    public void Absolute_path_rejected()
    {
        Assert.False(AssetChannel.TryGetImage(_accessor, _root, "/etc/passwd", out _, out _));
    }

    [Fact]
    public void Missing_file_returns_false()
    {
        Assert.False(AssetChannel.TryGetImage(_accessor, _root, "img/nope.png", out _, out _));
    }

    [Fact]
    public void Jpeg_extension_maps_mime()
    {
        File.WriteAllBytes(Path.Combine(_root, "img", "photo.jpg"), [0xFF, 0xD8, 0xFF]);
        Assert.True(AssetChannel.TryGetImage(_accessor, _root, "img/photo.jpg", out _, out var mime));
        Assert.Equal("image/jpeg", mime);
    }

    /// <summary>
    /// 契约测试：读取路径必须经 <c>CombinePath</c> 重建，而不是直接用
    /// <c>AssetPathValidator.TryResolve</c> 的裸拼接产物——SAF 下裸拼接的 content URI
    /// 不是合法 document URI（ResolveDocId 只取 tree 第一段），安卓图片会恒 404
    /// （03 注释预警的坑，code-review 2026-08-06 抓到）。
    /// </summary>
    [Fact]
    public void Read_path_goes_through_combine_path_not_raw_resolve()
    {
        var recording = new RecordingDirAccessor();
        AssetChannel.TryGetImage(recording, "content://com.android.externalstorage.documents/tree/primary%3AEmuera",
            "img/test.png", out _, out _);

        Assert.True(recording.CombinePathCalled);
        Assert.Equal("content://root/img/test.png", recording.LastReadPath);
    }

    /// <summary>记录 CombinePath/ReadAllBytes 调用链的 fake——验证读取路径重建契约。</summary>
    private sealed class RecordingDirAccessor : IGameDirAccessor
    {
        public bool CombinePathCalled { get; private set; }
        public string? LastReadPath { get; private set; }

        public string CombinePath(string basePath, string filename)
        {
            CombinePathCalled = true;
            return $"content://root/{filename}";
        }

        public byte[]? ReadAllBytes(string path)
        {
            LastReadPath = path;
            return [0x89, 0x50];
        }

        public bool DirectoryExists(string path) => throw new NotSupportedException();
        public string[] GetDirectories(string path) => throw new NotSupportedException();
        public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => throw new NotSupportedException();
        public bool FileExists(string path) => throw new NotSupportedException();
        public string[] GetFiles(string path) => throw new NotSupportedException();
        public string[] GetFiles(string path, string searchPattern, SearchOption searchOption) => throw new NotSupportedException();
        public string ReadAllText(string path) => throw new NotSupportedException();
        public Stream? OpenRead(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public Stream OpenWrite(string path) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
        public bool HasWriteAccess() => throw new NotSupportedException();
        public string ResolveSubPath(string basePath, string subDir) => throw new NotSupportedException();
        public string GetParentPath(string path) => throw new NotSupportedException();
        public string GetFileName(string path) => throw new NotSupportedException();
    }

    private static byte[] MiniPng()
    {
        // PNG signature + IHDR(8×4, 8bit RGBA) + IEND —— 探针只需头 24 字节
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] ihdr = [0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x04, 0x08, 0x06, 0x00, 0x00, 0x00];
        byte[] iend = [0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44];
        var bytes = new byte[sig.Length + ihdr.Length + iend.Length];
        sig.CopyTo(bytes, 0);
        ihdr.CopyTo(bytes, sig.Length);
        iend.CopyTo(bytes, sig.Length + ihdr.Length);
        return bytes;
    }
}
