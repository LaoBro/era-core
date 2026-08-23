using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MinorShift.Emuera;
using MinorShift.Emuera.UI.Game.Image;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 01 — ImageSizeProbe 尺寸探针单测（spec 测试 seam ②）。
/// 手工构造 PNG/JPEG 头字节，断言解析结果——期望值来自图片格式规范（独立来源），
/// 非实现重算，避免 tautological 断言。
/// </summary>
public class ImageSizeProbeTests
{
    // ---------- PNG（IHDR） ----------

    /// <summary>构造最小合法 PNG 头：8 字节 signature + IHDR chunk（length=13 + "IHDR" + 宽高大端 + 5 字节字段）。</summary>
    private static byte[] PngBytes(int width, int height)
    {
        var bytes = new List<byte>
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x0D, // IHDR chunk length = 13
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
        bytes.AddRange(new byte[] { 8, 6, 0, 0, 0 }); // bit depth, color type, compression, filter, interlace
        return bytes.ToArray();
    }

    [Fact]
    public void Png_ihdr_returns_width_and_height()
    {
        var png = PngBytes(300, 200);

        var ok = ImageSizeProbe.TryParsePng(png, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(300, width);
        Assert.Equal(200, height);
    }

    [Fact]
    public void Png_bad_signature_returns_false()
    {
        var png = PngBytes(300, 200);
        png[0] = 0x00;

        Assert.False(ImageSizeProbe.TryParsePng(png, out _, out _));
    }

    [Fact]
    public void Png_truncated_header_returns_false()
    {
        var png = PngBytes(300, 200).Take(10).ToArray();

        Assert.False(ImageSizeProbe.TryParsePng(png, out _, out _));
    }

    // ---------- JPEG（SOF 扫描） ----------

    /// <summary>
    /// 构造 JPEG 段流：SOI + 若干 APPn 段 + SOF0。
    /// SOF0 payload：precision(1) + height(2) + width(2)。
    /// </summary>
    private static byte[] JpegBytes(byte sofMarker, int width, int height, params (byte marker, byte[] payload)[] appSegments)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 }; // SOI
        foreach (var (marker, payload) in appSegments)
        {
            bytes.Add(0xFF);
            bytes.Add(marker);
            bytes.Add((byte)((payload.Length + 2) >> 8));
            bytes.Add((byte)(payload.Length + 2));
            bytes.AddRange(payload);
        }
        bytes.Add(0xFF);
        bytes.Add(sofMarker);
        bytes.Add(0x00);
        bytes.Add(0x0B); // length = 11 = 8 + 3×1 component
        bytes.Add(8); // precision
        bytes.Add((byte)(height >> 8));
        bytes.Add((byte)height);
        bytes.Add((byte)(width >> 8));
        bytes.Add((byte)width);
        bytes.Add(1); // components 数量
        bytes.AddRange(new byte[] { 1, 0x11, 0 }); // 1 个 component：id/sampling/quant
        return bytes.ToArray();
    }

    [Fact]
    public void Jpeg_scans_past_app_segments_to_sof0()
    {
        var jpeg = JpegBytes(0xC0, 400, 300,
            (0xE0, new byte[14]), // APP0 (JFIF)
            (0xE1, new byte[12])); // APP1 (Exif)

        var ok = ImageSizeProbe.TryParseJpeg(jpeg, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(400, width);
        Assert.Equal(300, height);
    }

    [Fact]
    public void Jpeg_sof2_is_recognized_as_frame_header()
    {
        var jpeg = JpegBytes(0xC2, 640, 480);

        var ok = ImageSizeProbe.TryParseJpeg(jpeg, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(640, width);
        Assert.Equal(480, height);
    }

    [Fact]
    public void Jpeg_without_sof_returns_false()
    {
        // SOI + APP0，无任何 SOF 段
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange(new byte[14]);

        Assert.False(ImageSizeProbe.TryParseJpeg(bytes.ToArray(), out _, out _));
    }

    [Fact]
    public void Jpeg_bad_soi_returns_false()
    {
        var jpeg = JpegBytes(0xC0, 400, 300);
        jpeg[0] = 0x00;

        Assert.False(ImageSizeProbe.TryParseJpeg(jpeg, out _, out _));
    }

    [Fact]
    public void Jpeg_truncated_segment_returns_false()
    {
        var jpeg = JpegBytes(0xC0, 400, 300).Take(8).ToArray();

        Assert.False(ImageSizeProbe.TryParseJpeg(jpeg, out _, out _));
    }

    // ---------- WebP（VP8L / VP8X） ----------

    /// <summary>构造 VP8L（无损）头：RIFF/WEBP + 签名 0x2F + 32 位 LE（宽-1 / 高-1 各 14 位）。</summary>
    private static byte[] WebpVp8LBytes(int width, int height)
    {
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("WEBP"));
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("VP8L"));
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        bytes.Add(0x2F);
        uint bits = ((uint)(width - 1) & 0x3FFFu) | (((uint)(height - 1) & 0x3FFFu) << 14);
        bytes.Add((byte)bits);
        bytes.Add((byte)(bits >> 8));
        bytes.Add((byte)(bits >> 16));
        bytes.Add((byte)(bits >> 24));
        while (bytes.Count < 40) bytes.Add(0);
        return bytes.ToArray();
    }

    /// <summary>构造 VP8X（扩展画布）头：canvas 宽-1/高-1 各 24 位 LE。</summary>
    private static byte[] WebpVp8XBytes(int width, int height)
    {
        var bytes = new List<byte>();
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("WEBP"));
        bytes.AddRange(System.Text.Encoding.ASCII.GetBytes("VP8X"));
        bytes.AddRange(new byte[] { 0, 0, 0, 0 });
        bytes.Add(0x0A);
        bytes.Add(0x00); // flags
        bytes.Add(0x00); // reserved
        bytes.Add(0x00); // reserved
        uint w = (uint)(width - 1);
        uint h = (uint)(height - 1);
        bytes.Add((byte)w); bytes.Add((byte)(w >> 8)); bytes.Add((byte)(w >> 16));
        bytes.Add((byte)h); bytes.Add((byte)(h >> 8)); bytes.Add((byte)(h >> 16));
        while (bytes.Count < 40) bytes.Add(0);
        return bytes.ToArray();
    }

    [Fact]
    public void Webp_vp8l_returns_width_and_height()
    {
        var webp = WebpVp8LBytes(1041, 557);

        var ok = ImageSizeProbe.TryParseWebp(webp, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(1041, width);
        Assert.Equal(557, height);
    }

    [Fact]
    public void Webp_vp8x_returns_width_and_height()
    {
        var webp = WebpVp8XBytes(200, 100);

        var ok = ImageSizeProbe.TryParseWebp(webp, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(200, width);
        Assert.Equal(100, height);
    }

    [Fact]
    public void Webp_bad_magic_returns_false()
    {
        var webp = WebpVp8LBytes(200, 100);
        webp[0] = 0x00;

        Assert.False(ImageSizeProbe.TryParseWebp(webp, out _, out _));
    }

    [Fact]
    public void TryGetPixelSize_reads_webp_from_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emuera-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "title.webp");
            File.WriteAllBytes(file, WebpVp8LBytes(1041, 557));
            var accessor = new FileSystemGameDirAccessor();

            var ok = ImageSizeProbe.TryGetPixelSize(accessor, file, out var w, out var h);

            Assert.True(ok);
            Assert.Equal(1041, w);
            Assert.Equal(557, h);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- 文件读取壳 ----------

    [Fact]
    public void TryGetPixelSize_reads_png_from_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emuera-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "portrait.png");
            File.WriteAllBytes(file, PngBytes(200, 100));
            var accessor = new FileSystemGameDirAccessor();

            var ok = ImageSizeProbe.TryGetPixelSize(accessor, file, out var w, out var h);

            Assert.True(ok);
            Assert.Equal(200, w);
            Assert.Equal(100, h);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetPixelSize_reads_jpeg_from_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emuera-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "portrait.jpg");
            File.WriteAllBytes(file, JpegBytes(0xC0, 400, 300));
            var accessor = new FileSystemGameDirAccessor();

            var ok = ImageSizeProbe.TryGetPixelSize(accessor, file, out var w, out var h);

            Assert.True(ok);
            Assert.Equal(400, w);
            Assert.Equal(300, h);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TryGetPixelSize_missing_file_returns_false()
    {
        var accessor = new FileSystemGameDirAccessor();

        Assert.False(ImageSizeProbe.TryGetPixelSize(accessor, "C:/nonexistent/missing.png", out _, out _));
    }

    [Fact]
    public void TryGetPixelSize_non_image_file_returns_false()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emuera-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "GameBase.csv");
            File.WriteAllText(file, "A,B,C\n1,2,3\n");
            var accessor = new FileSystemGameDirAccessor();

            Assert.False(ImageSizeProbe.TryGetPixelSize(accessor, file, out _, out _));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
