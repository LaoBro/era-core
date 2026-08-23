using System;

namespace MinorShift.Emuera.UI.Game.Image;

/// <summary>
/// 纯 C# 图片尺寸探针——无 System.Drawing、无新依赖、Android 安全（issue 01）。
/// 读取 PNG/JPEG 文件头解析像素尺寸，供无头模式下 <see cref="ConsoleImagePart"/>
/// 的几何计算使用（sprite 表不可用时按原图纵横比推算宽度）。
/// 解析失败返回 false（调用方回退到参数或 0）。
/// </summary>
internal static class ImageSizeProbe
{
    /// <summary>解析 PNG 头（IHDR chunk）。成功时返回像素宽高。</summary>
    public static bool TryParsePng(ReadOnlySpan<byte> header, out int width, out int height)
    {
        width = 0;
        height = 0;
        // signature(8) + length(4) + "IHDR"(4) + width(4) + height(4) = 24 字节
        if (header.Length < 24)
            return false;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!header[..8].SequenceEqual(signature))
            return false;
        if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
            return false;
        width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
        height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
        return width > 0 && height > 0;
    }

    /// <summary>
    /// 解析 JPEG 尺寸——扫描段流直到 SOF（帧头）marker。
    /// SOF0-3/5-7/9-11/13-15（排除 DHT/DAC/JPG）段内 offset 3 起为 height(2)/width(2)。
    /// 跳过 APPn/COM/DQT 等带长度段与 RST/TEM/SOI/EOI/填充等独立 marker。
    /// </summary>
    public static bool TryParseJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return false;
        int i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF)
                return false; // 段必须以 FF 开头
            byte marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // 填充字节
                continue;
            }
            // 独立 marker（无长度字段）：TEM(01)、RST(FFD0-D7)、SOI(D8)、EOI(D9)、填充(00)
            if (marker == 0x00 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
            {
                i += 2;
                continue;
            }
            if (i + 4 > data.Length)
                return false;
            int len = (data[i + 2] << 8) | data[i + 3];
            if (len < 2 || i + 2 + len > data.Length)
                return false;
            // SOF：C0-CF 内排除 DHT(C4)、JPG(C8)、DAC(CC)
            bool isSof = (marker & 0xF0) == 0xC0 && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                if (len < 7)
                    return false;
                height = (data[i + 5] << 8) | data[i + 6];
                width = (data[i + 7] << 8) | data[i + 8];
                return width > 0 && height > 0;
            }
            i += 2 + len;
        }
        return false;
    }

    /// <summary>
    /// 解析 WebP 尺寸（RIFF 容器）。支持三种块：VP8X（扩展画布）、VP8L（无损）、VP8（有损关键帧）。
    /// 全部为纯头字段解析，无需解码像素。
    /// </summary>
    public static bool TryParseWebp(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        // RIFF(4) + size(4) + WEBP(4) + fourcc(4) + chunkSize(4) = 30 字节头
        if (data.Length < 30)
            return false;
        if (data[0] != (byte)'R' || data[1] != (byte)'I' || data[2] != (byte)'F' || data[3] != (byte)'F')
            return false;
        if (data[8] != (byte)'W' || data[9] != (byte)'E' || data[10] != (byte)'B' || data[11] != (byte)'P')
            return false;
        // 块负载从 offset 20 开始；按 fourcc（data[12..15]）分派三种块
        bool vp8x = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'X';
        bool vp8l = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'L';
        bool vp8 = data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)' ';
        if (vp8x)
        {
            // data[20]=0x0A；[21]=flags；[24..26] canvas 宽-1(LE24)；[27..29] canvas 高-1(LE24)
            width = data[24] | (data[25] << 8) | (data[26] << 16);
            height = data[27] | (data[28] << 8) | (data[29] << 16);
            width += 1;
            height += 1;
        }
        else if (vp8l)
        {
            // data[20]=0x2F 签名；data[21..24] 32 位 LE：位[0..14) 宽-1，位[14..28) 高-1
            if (data[20] != 0x2F)
                return false;
            uint bits = (uint)(data[21] | (data[22] << 8) | (data[23] << 16) | (data[24] << 24));
            width = (int)(bits & 0x3FFFu) + 1;
            height = (int)((bits >> 14) & 0x3FFFu) + 1;
        }
        else if (vp8)
        {
            // 关键帧：帧标签 data[20..22]，起始码 data[23..25]=9d 01 2a，
            // 之后 28 位紧排：data[26..27] 低 6 位续宽，data[27](高 2 位)+data[28]+data[29](低 4 位)组成高
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
                return false;
            width = data[26] | ((data[27] & 0x3F) << 8);
            height = ((data[27] >> 6) & 0x03) | (data[28] << 2) | ((data[29] & 0x0F) << 10);
        }
        else
        {
            return false;
        }
        return width > 0 && height > 0;
    }

    /// <summary>
    /// 经 <see cref="IGameDirAccessor"/> 读取文件头部并解析尺寸（PNG/JPEG/WebP 分发）。
    /// 只读前 64KB（PNG 头 24B 足够；JPEG SOF 段在 SOS 之前，实际位于文件前部；WebP 头 30B）。
    /// 文件缺失、非图片、解析失败均返回 false。
    /// </summary>
    public static bool TryGetPixelSize(IGameDirAccessor accessor, string fullPath, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (accessor == null || string.IsNullOrEmpty(fullPath))
            return false;
        using var stream = accessor.OpenRead(fullPath);
        if (stream == null)
            return false;
        Span<byte> buffer = stackalloc byte[64 * 1024];
        // Stream.Read 不保证一次填满（SAF 内容流可能部分读）——循环读至 EOF 或缓冲区满
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0)
                break;
            read += n;
        }
        if (read < 2)
            return false;
        // PNG signature: 89 50 4E 47
        if (read >= 8 && buffer[0] == 0x89 && buffer[1] == 0x50)
            return TryParsePng(buffer[..read], out width, out height);
        // JPEG SOI: FF D8
        if (buffer[0] == 0xFF && buffer[1] == 0xD8)
            return TryParseJpeg(buffer[..read], out width, out height);
        // WebP: RIFF....WEBP
        if (read >= 12 && buffer[0] == (byte)'R' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'F'
            && buffer[8] == (byte)'W' && buffer[9] == (byte)'E' && buffer[10] == (byte)'B' && buffer[11] == (byte)'P')
            return TryParseWebp(buffer[..read], out width, out height);
        return false;
    }
}
