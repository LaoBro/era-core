using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// b2-irdiskcache：内容指纹的集合语义钉子 + IR 参与判定真值表。
/// <list type="bullet">
/// <item>指纹与输入顺序无关——2026-09-04 模拟器实测回归钉：SAF 上 pending 序 = Preload 读取完成序
/// （并行 degree&gt;1 时逐轮随机），顺序直接进哈希曾使指纹逐轮漂移、IR 永远 miss + 每轮白付
/// era_ir.dat 整库重建写。</item>
/// <item>排序只消解顺序维度：文件集合 / Rename 表变化仍必须改变指纹。</item>
/// <item><see cref="ErbLoader.ShouldUseIrCache"/> 唯一决策点：content-URI（SAF 流不可 seek，
/// hit 路径每函数全量重读 blob 库，实测 31.5MB × 2724 次）一律不参与 IR。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class ErbIrCacheTests
{
	private static readonly List<(string Logical, string Physical)> Files =
	[
		("ERB\\SYSTEM\\A.ERB", "content://x/A"),
		("ERB\\EVENT\\B.ERB", "content://x/B"),
		("ERB\\C.ERB", "content://x/C"),
	];

	[Fact]
	public void Fingerprint_is_invariant_under_input_order()
	{
		using var _ = OpenScope();
		var forward = ErbIrCache.ComputeContentFingerprint(Files, null);
		var reversed = ErbIrCache.ComputeContentFingerprint(Files.AsEnumerable().Reverse().ToList(), null);
		Assert.Equal(forward, reversed);
	}

	[Fact]
	public void Fingerprint_changes_when_file_set_changes()
	{
		using var _ = OpenScope();
		var baseline = ErbIrCache.ComputeContentFingerprint(Files, null);
		var withExtraFile = ErbIrCache.ComputeContentFingerprint(Files.Append(("ERB\\D.ERB", "content://x/D")).ToList(), null);
		var withRenamedFile = ErbIrCache.ComputeContentFingerprint(
			Files.Select(f => f.Logical == "ERB\\C.ERB" ? ("ERB\\X.ERB", f.Physical) : f).ToList(), null);
		Assert.NotEqual(baseline, withExtraFile);
		Assert.NotEqual(baseline, withRenamedFile);
	}

	[Fact]
	public void Fingerprint_changes_when_rename_table_changes()
	{
		using var _ = OpenScope();
		var baseline = ErbIrCache.ComputeContentFingerprint(Files, null);
		var withRename = ErbIrCache.ComputeContentFingerprint(Files, new Dictionary<string, string> { ["%ARG"] = "1" });
		Assert.NotEqual(baseline, withRename);
	}

	[Theory]
	[InlineData(false, false, false, false, false)]
	[InlineData(false, false, false, true, false)]
	[InlineData(false, false, true, false, false)]
	[InlineData(false, false, true, true, false)]
	[InlineData(false, true, false, false, false)]
	[InlineData(false, true, false, true, false)]
	[InlineData(false, true, true, false, false)]
	[InlineData(false, true, true, true, false)]
	[InlineData(true, false, false, false, false)]
	[InlineData(true, false, false, true, false)]
	[InlineData(true, false, true, false, false)]
	[InlineData(true, false, true, true, false)]
	[InlineData(true, true, false, false, true)]
	[InlineData(true, true, false, true, false)]
	[InlineData(true, true, true, false, false)]
	[InlineData(true, true, true, true, false)]
	public void ShouldUseIrCache_truth_table(bool irDiskCacheConfigured, bool lazyFunctionLoad, bool analysisMode, bool irPathIsContentUri, bool expected)
		=> Assert.Equal(expected, ErbLoader.ShouldUseIrCache(irDiskCacheConfigured, lazyFunctionLoad, analysisMode, irPathIsContentUri));

	/// <summary>ComputeContentFingerprint 读 Config.Config.*（指纹的解析配置集输入）——开 GlobalStatic 域。</summary>
	private static IDisposable OpenScope()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-irfp-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());
		var config = new ConfigData();
		return new Scope(GlobalStatic.OpenScope(config), tempRoot);
	}

	private sealed class Scope(IDisposable inner, string tempRoot) : IDisposable
	{
		public void Dispose()
		{
			inner.Dispose();
			try { Directory.Delete(tempRoot, recursive: true); } catch { /* 临时目录清理失败不掩盖断言 */ }
		}
	}
}
