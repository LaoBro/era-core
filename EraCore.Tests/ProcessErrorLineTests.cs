using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 决策二测试：Process.getRawTextFormFilewithLine 改用 Preload 缓存。
/// spec Testing Decisions L376：预填充 Preload 后修改磁盘文件，断言返回缓存行（间接验证不走 File.ReadLines）；
/// 验证 .csv 分支 key 使用 CsvDir（决策二 bug 修复）。
/// spec Success Criteria L405：调用 100 次总耗时 &lt; 50ms（无磁盘 I/O）。
/// </summary>
[Collection("LoaderTests")]
public class ProcessErrorLineTests
{
	[Fact]
	public async Task GetRawText_returns_cached_content_not_disk_content()
	{
		// 决策二核心：Preload 缓存后修改磁盘文件，getRawTextFormFilewithLine 应返回缓存内容
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("ERROR_TEST.ERB", "PRINTL hello\nPRINTL world\n");
		await h.PreloadAsync();

		// 修改磁盘文件 — 缓存应不受影响
		File.WriteAllText(Path.Combine(h.ErbDir, "ERROR_TEST.ERB"), "PRINTL CHANGED\n");

		var pos = new ScriptPosition("ERROR_TEST.ERB", 0); // LineNo = 0+1 = 1
		var result = MinorShift.Emuera.GameProc.Process.getRawTextFormFilewithLine(pos);

		Assert.Equal("PRINTL hello", result);
		Assert.DoesNotContain("CHANGED", result);
	}

	[Fact]
	public async Task GetRawText_csv_branch_uses_CsvDir()
	{
		// 决策二 bug 修复：原 .csv 分支误传 ErbDir 给 DetectEncoding，改用 Preload 后 key 应基于 CsvDir
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var csvPath = Path.Combine(h.CsvDir, "TESTCSV.CSV");
		File.WriteAllText(csvPath, "name,value\nfoo,bar\n");
		await h.PreloadAsync();

		var pos = new ScriptPosition("TESTCSV.CSV", 1); // LineNo = 1+1 = 2
		var result = MinorShift.Emuera.GameProc.Process.getRawTextFormFilewithLine(pos);

		Assert.Equal("foo,bar", result);
	}

	[Fact]
	public async Task GetRawText_out_of_range_line_returns_placeholder()
	{
		// preload-cache-lifecycle 01（ADR-0023）：行号越界从返回 "" 改为占位文本（三态失败之一）。
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("SMALL.ERB", "PRINTL only line\n");
		await h.PreloadAsync();

		var pos = new ScriptPosition("SMALL.ERB", 99);
		var result = MinorShift.Emuera.GameProc.Process.getRawTextFormFilewithLine(pos);

		PlaceholderAssert.IsPlaceholder(result, "SMALL.ERB", "100");
	}

	[Fact]
	public async Task GetRawText_non_erb_csv_returns_empty()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		var pos = new ScriptPosition("TEST.TXT", 0);
		var result = MinorShift.Emuera.GameProc.Process.getRawTextFormFilewithLine(pos);

		Assert.Equal("", result);
	}

	[Fact]
	public async Task GetRawText_100_calls_under_50ms_no_disk_io()
	{
		// spec Success Criteria L405：调用 100 次总耗时 < 50ms（无磁盘 I/O）
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var lines = new System.Text.StringBuilder();
		for (int i = 0; i < 50; i++)
			lines.AppendLine($"PRINTL line{i}");
		h.WriteErb("PERF.ERB", lines.ToString());
		await h.PreloadAsync();

		var pos = new ScriptPosition("PERF.ERB", 10);
		var sw = System.Diagnostics.Stopwatch.StartNew();
		for (int i = 0; i < 100; i++)
			_ = MinorShift.Emuera.GameProc.Process.getRawTextFormFilewithLine(pos);
		sw.Stop();

		Assert.True(sw.ElapsedMilliseconds < 50,
			$"100 calls took {sw.ElapsedMilliseconds}ms, expected < 50ms");
	}
}
