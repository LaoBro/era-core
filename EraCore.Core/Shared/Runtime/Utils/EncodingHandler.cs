using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MinorShift.Emuera.Runtime.Utils;

public static class EncodingHandler
{
	public static readonly Encoding UTF8Encoding = new UTF8Encoding(false, true);
	public static readonly Encoding shiftjisEncoding = GetEncoding(932);
	public static readonly Encoding UTF8BOMEncoding = new UTF8Encoding(true, true);


	/// <summary>
	/// ADR-0019：从字节数组检测编码并分行。
	/// <para>
	/// ADR-0037：原先还有按 <c>path</c> 直读的 <c>ReadAllLinesWithDetection(string)</c> 重载——
	/// 它是 <see cref="EraStreamReader"/> 兜底才存在的最后一条直连读缝，随兜底一并删除；
	/// 调用方一律先经 <see cref="IGameDirAccessor"/> 取字节再进本方法。
	/// </para>
	/// </summary>
	public static string[] ReadAllLinesFromBytes(byte[] bytes)
	{
		var bomEnc = DetectBomEncoding(bytes);
		if (bomEnc != null)
		{
			int bomLen = bomEnc == UTF8BOMEncoding ? 3 : 2;
			return SplitLines(bomEnc.GetString(bytes, bomLen, bytes.Length - bomLen));
		}
		// 无 BOM：尝试 UTF-8（严格模式，遇无效字节抛 DecoderFallbackException）
		try
		{
			return SplitLines(UTF8Encoding.GetString(bytes));
		}
		catch (DecoderFallbackException)
		{
			// UTF-8 失败 → 假设 SHIFT-JIS（CP 932 对绝大多数日文文件可解码）
			return SplitLines(shiftjisEncoding.GetString(bytes));
		}
	}

	/// <summary>
	/// 检查字节序列的 BOM 标记，返回对应编码；无 BOM 返回 null。
	/// 供 ReadAllLinesWithDetection 与 DetectEncoding 共享，避免 BOM 级联逻辑重复。
	/// </summary>
	private static Encoding? DetectBomEncoding(byte[] bytes)
	{
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
			return UTF8BOMEncoding;
		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
			return Encoding.Unicode;
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
			return Encoding.BigEndianUnicode;
		return null;
	}

	/// <summary>
	/// 将已解码的字符串按 \r\n / \r / \n 分割为行数组。
	/// 语义与 <see cref="File.ReadAllLines(string, Encoding)"/> 一致（不包含末尾空行）。
	/// </summary>
	private static string[] SplitLines(string content)
	{
		if (string.IsNullOrEmpty(content))
			return [];
		var lines = new List<string>();
		using var sr = new StringReader(content);
		string? line;
		while ((line = sr.ReadLine()) != null)
			lines.Add(line);
		return lines.ToArray();
	}

	/// <summary>从已读取的字节判断 BOM、UTF-8 或 Shift-JIS，供 SAF 调用方避免回退到本地 File API。</summary>
	public static Encoding DetectEncoding(byte[] bytes)
	{
		var bomEnc = DetectBomEncoding(bytes);
		if (bomEnc != null)
			return bomEnc;
		try
		{
			UTF8Encoding.GetString(bytes);
			return UTF8Encoding;
		}
		catch (DecoderFallbackException)
		{
			return shiftjisEncoding;
		}
	}

	public static Encoding DetectEncoding(Stream stream)
	{
		var pos = stream.Position;
		try
		{
			using var sr = new StreamReader(stream, UTF8Encoding, true, -1, true);
			sr.Peek();
			if (!UTF8Encoding.Equals(sr.CurrentEncoding))
			{
				stream.Seek(pos, SeekOrigin.Begin);
				return sr.CurrentEncoding;
			}
			sr.ReadToEnd();
			sr.Dispose();
			stream.Seek(pos, SeekOrigin.Begin);
			return UTF8Encoding;
		}
		catch
		{
			stream.Seek(pos, SeekOrigin.Begin);
			return shiftjisEncoding;
		}
	}
	public static Encoding GetEncoding(int codePage)
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
	}
}
