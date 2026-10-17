using System.IO;
using System.Text;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 决策零测试：EncodingHandler.ReadAllLinesWithDetection 与 File.ReadAllLines 行为等价。
/// 覆盖 UTF-8（无 BOM）、UTF-8（有 BOM）、SHIFT-JIS 三种编码，
/// 以及末尾无换行、混合换行符（\r\n / \r / \n）边界场景。
/// spec Testing Decisions L372：UTF-8/SHIFT-JIS/BOM 三种编码下 ReadAllLines 行为等价。
/// </summary>
public class EncodingHandlerReadAllLinesTests
{
	private static string WriteTempFile(byte[] bytes)
	{
		var path = Path.Combine(Path.GetTempPath(), "emuera-enc-test-" + Path.GetRandomFileName());
		File.WriteAllBytes(path, bytes);
		return path;
	}

	[Fact]
	public void Utf8NoBom_lines_match_FileReadAllLines()
	{
		var content = "line1\nline2\nline3\n";
		var bytes = Encoding.UTF8.GetBytes(content);
		var path = WriteTempFile(bytes);
		try
		{
			var expected = File.ReadAllLines(path, EncodingHandler.UTF8Encoding);
			var actual = EncodingHandler.ReadAllLinesFromBytes(bytes);
			Assert.Equal(expected, actual);
			Assert.Equal(new[] { "line1", "line2", "line3" }, actual);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void Utf8WithBom_bom_stripped_and_lines_correct()
	{
		var content = "あいう\nline2\n";
		var bytes = EncodingHandler.UTF8BOMEncoding.GetBytes(content);
		var path = WriteTempFile(bytes);
		try
		{
			var actual = EncodingHandler.ReadAllLinesFromBytes(bytes);
			Assert.Equal(new[] { "あいう", "line2" }, actual);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void ShiftJis_japanese_content_decoded_correctly()
	{
		// SHIFT-JIS 编码的日文内容（非 UTF-8 兼容字节，触发 UTF-8 解码失败回退）
		var shiftJis = Encoding.GetEncoding(932);
		var content = "日本語テスト\nline2\n";
		var bytes = shiftJis.GetBytes(content);
		var path = WriteTempFile(bytes);
		try
		{
			var actual = EncodingHandler.ReadAllLinesFromBytes(bytes);
			Assert.Equal(new[] { "日本語テスト", "line2" }, actual);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void TrailingNewline_does_not_produce_extra_empty_line()
	{
		// spec L84：文件末尾换行的边界 — 与 File.ReadAllLines 一致，不产生多余空行
		var content = "line1\nline2\n";
		var bytes = Encoding.UTF8.GetBytes(content);
		var path = WriteTempFile(bytes);
		try
		{
			var expected = File.ReadAllLines(path, Encoding.UTF8);
			var actual = EncodingHandler.ReadAllLinesFromBytes(bytes);
			Assert.Equal(expected, actual);
			Assert.Equal(2, actual.Length);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void NoTrailingNewline_last_line_preserved()
	{
		// spec L84：文件末尾无换行的边界
		var content = "line1\nline2";
		var bytes = Encoding.UTF8.GetBytes(content);
		var path = WriteTempFile(bytes);
		try
		{
			var expected = File.ReadAllLines(path, Encoding.UTF8);
			var actual = EncodingHandler.ReadAllLinesFromBytes(bytes);
			Assert.Equal(expected, actual);
			Assert.Equal(2, actual.Length);
			Assert.Equal("line2", actual[1]);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void MixedLineEndings_all_handled()
	{
		// spec L84：混合换行符（\r\n / \r / \n）— StringReader.ReadLine 与 File.ReadAllLines 语义一致
		var content = "line1\r\nline2\rline3\nline4";
		var bytes = Encoding.UTF8.GetBytes(content);
		var path = WriteTempFile(bytes);
		try
		{
			var expected = File.ReadAllLines(path, Encoding.UTF8);
			var actual = EncodingHandler.ReadAllLinesFromBytes(bytes);
			Assert.Equal(expected, actual);
			Assert.Equal(4, actual.Length);
		}
		finally { File.Delete(path); }
	}

	[Fact]
	public void EmptyFile_returns_empty_array()
	{
		// ADR-0037：被测入口改为字节版；临时文件与 File.ReadAllLines 仅作 oracle 保留。
		var path = WriteTempFile([]);
		try
		{
			var actual = EncodingHandler.ReadAllLinesFromBytes(File.ReadAllBytes(path));
			Assert.Empty(actual);
		}
		finally { File.Delete(path); }
	}
}
