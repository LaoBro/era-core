using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 占位文本断言（语言无关：括号 + 文件名 + 行号——jpn/eng/zhs 三语形态同构）。
/// </summary>
internal static class PlaceholderAssert
{
	internal static void IsPlaceholder(string result, string filename, string lineNo)
	{
		Assert.NotEqual("", result);
		Assert.StartsWith("(", result);
		Assert.EndsWith(")", result);
		Assert.Contains(filename, result);
		Assert.Contains(lineNo, result);
	}
}

/// <summary>
/// preload-cache-lifecycle 01（ADR-0023）：Preload 源文本缓存生命周期收缩。
/// 覆盖两条收尾挂点（LoadErbDir / LoadErbList）的清空语义、路径索引常驻、
/// 以及报错回读的成功路径与三态降级占位（文件不存在 / 行号越界）。
/// 注意 ScriptPosition(srcFile, srcLineNo) 构造器内部 +1 转 1 基行号。
/// </summary>
[Collection("LoaderTests")]
public class PreloadCacheLifecycleTests
{
	private static ErbLoader NewLoader(LoaderTestHarness h) =>
		new(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);

	[Fact]
	public async Task LoadErbDir_clears_cached_lines_but_keeps_path_index()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var erbPath = h.WriteErb("LIFECYCLE.ERB", "@LIFECYCLE_FUNC\nRETURN\n");
		await h.PreloadAsync();
		Assert.NotNull(Preload.TryGetFileLines(erbPath));

		var ok = await NewLoader(h).LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());

		Assert.True(ok);
		// 源文本缓存已清空（~180 MiB @ eraTW 规模的回收点）
		Assert.Null(Preload.TryGetFileLines(erbPath));
		// 路径索引常驻——回读定位通道
		Assert.True(Preload.TryGetFullPath("LIFECYCLE.ERB", out var indexed));
		Assert.Equal(erbPath, indexed);
	}

	[Fact]
	public async Task LoadErbList_clears_cached_lines_on_partial_reload()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var erbPath = h.WriteErb("PARTIAL.ERB", "@PARTIAL_FUNC\nRETURN\n");
		await h.PreloadAsync();

		await NewLoader(h).LoadErbList([erbPath], new LabelDictionary());

		Assert.Null(Preload.TryGetFileLines(erbPath));
		Assert.True(Preload.TryGetFullPath("PARTIAL.ERB", out _));
	}

	[Fact]
	public async Task GetRawText_readback_from_disk_after_cache_cleared()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// 0 基行 1 = 1 基行 2 = PRINTL marker_line_2
		h.WriteErb("READBACK.ERB", "@READBACK_FUNC\n\tPRINTL marker_line_2\nRETURN\n");
		await h.PreloadAsync();
		await NewLoader(h).LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());

		// 缓存已清空 → 按需回读成功路径：仍能取到原行文本
		var line = Process.getRawTextFormFilewithLine(new ScriptPosition("READBACK.ERB", 1));
		Assert.Contains("marker_line_2", line);
	}

	[Fact]
	public async Task GetRawText_placeholder_when_source_file_missing()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var erbPath = h.WriteErb("GONE.ERB", "@GONE_FUNC\n\tPRINTL will_be_deleted\nRETURN\n");
		await h.PreloadAsync();
		await NewLoader(h).LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());
		File.Delete(erbPath);

		// 文件不存在 → 占位文本，不抛异常
		var line = Process.getRawTextFormFilewithLine(new ScriptPosition("GONE.ERB", 1));
		PlaceholderAssert.IsPlaceholder(line, "GONE.ERB", "2");
	}

	[Fact]
	public async Task GetRawText_placeholder_when_line_out_of_range()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("SHORT.ERB", "@SHORT_FUNC\nRETURN\n");
		await h.PreloadAsync();
		await NewLoader(h).LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());

		// 0 基 98 → 1 基行 99，文件只有 2 行 → 行号越界占位
		var line = Process.getRawTextFormFilewithLine(new ScriptPosition("SHORT.ERB", 98));
		PlaceholderAssert.IsPlaceholder(line, "SHORT.ERB", "99");
	}

	[Fact]
	public async Task GetRawText_returns_empty_for_synthetic_line_zero()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("ZERO.ERB", "@ZERO_FUNC\nRETURN\n");
		await h.PreloadAsync();
		await NewLoader(h).LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());

		// 0 基 -1 → 1 基行 0 合成位置（Preload 警告先例）保持返回 ""——加载期警告输出零变化
		var line = Process.getRawTextFormFilewithLine(new ScriptPosition("ZERO.ERB", -1));
		Assert.Equal("", line);
	}

	[Fact]
	public async Task GetRawText_cache_hit_window_unchanged()
	{
		// 加载窗口内（缓存未清空的旁路验证）：PreloadAsync 后直接回读 = 原行为
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("WARM.ERB", "@WARM_FUNC\n\tPRINTL warm_window_line\nRETURN\n");
		await h.PreloadAsync();

		var line = Process.getRawTextFormFilewithLine(new ScriptPosition("WARM.ERB", 1));
		Assert.Contains("warm_window_line", line);
	}
}
