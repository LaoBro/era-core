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
/// b2-irdiskcache：内容指纹的集合语义钉子 + IR 参与判定真值表 + 方案 B（content-URI → app 私有存储）。
/// <list type="bullet">
/// <item>指纹与输入顺序无关——2026-09-04 模拟器实测回归钉：SAF 上 pending 序 = Preload 读取完成序
/// （并行 degree&gt;1 时逐轮随机），顺序直接进哈希曾使指纹逐轮漂移、IR 永远 miss + 每轮白付
/// era_ir.dat 整库重建写。</item>
/// <item>排序只消解顺序维度：文件集合 / Rename 表变化仍必须改变指纹。</item>
/// <item><see cref="ErbLoader.ShouldUseIrCache"/> 唯一决策点：2026-09-04 方案 B（issues/05）后不含
/// content-URI 维度——SAF 游戏缓存镜像到 app 私有存储（可 seek），读放大止血解除。</item>
/// <item>方案 B 路径搬迁：content-URI → <c>AppDataPaths/IRCache/&lt;gameKey&gt;/era_ir.dat</c>，本地保持游戏目录；
/// 多游戏隔离（gameKey = 归一化 ExeDir 的 Fnv1a64）；孤儿清理（宽限期 + 活动会话保护）。</item>
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
	[InlineData(false, false, false, false)]
	[InlineData(false, false, true, false)]
	[InlineData(false, true, false, false)]
	[InlineData(false, true, true, false)]
	[InlineData(true, false, false, false)]
	[InlineData(true, false, true, false)]
	[InlineData(true, true, false, true)]
	[InlineData(true, true, true, false)]
	public void ShouldUseIrCache_truth_table(bool irDiskCacheConfigured, bool lazyFunctionLoad, bool analysisMode, bool expected)
		=> Assert.Equal(expected, ErbLoader.ShouldUseIrCache(irDiskCacheConfigured, lazyFunctionLoad, analysisMode));

	// ===== 方案 B（issues/05）：content-URI 搬迁到 app 私有存储 =====

	[Fact]
	public void ComputeGameKey_isolates_games_and_normalizes_trailing_separator()
	{
		using var _ = OpenScope();
		Assert.NotEqual(
			ErbIrCache.ComputeGameKey("content://x/a"),
			ErbIrCache.ComputeGameKey("content://x/b"));
		Assert.NotEqual(
			ErbIrCache.ComputeGameKey(@"D:\games\TW"),
			ErbIrCache.ComputeGameKey(@"D:\games\ERB"));
		// 尾部目录分隔符不改变键（同目录的两种拼写）。
		Assert.Equal(
			ErbIrCache.ComputeGameKey("content://x/a"),
			ErbIrCache.ComputeGameKey("content://x/a/"));
		Assert.Equal(
			ErbIrCache.ComputeGameKey(@"D:\games\TW"),
			ErbIrCache.ComputeGameKey(@"D:\games\TW\"));
	}

	[Fact]
	public void GetIrCachePath_relocates_content_uri_to_appdata_keeps_local_in_game_dir()
	{
		using var _ = OpenScope();
		string appDataBefore = AppDataPaths.Directory;
		try
		{
			var appData = Path.Combine(Path.GetTempPath(), "emuera-irpath-" + Guid.NewGuid().ToString("N"));
			AppDataPaths.Configure(appData);
			string uri = "content://com.android.externalstorage.documents/tree/primary%3AEmuera/document/primary%3AEmuera%2FTW";
			string key = ErbIrCache.ComputeGameKey(uri);
			// content-URI：搬到 app 私有存储（真实文件系统，可 seek），key 目录隔离。
			Assert.Equal(Path.Combine(appData, "IRCache", key, "era_ir.dat"), ErbIrCache.GetIrCachePath(uri));
			// 本地路径：保持 <游戏目录>/_IRCache/era_ir.dat 现状（桌面行为不变）。
			Assert.Equal(
				Path.Combine(@"D:\games\TW", "_IRCache", "era_ir.dat"),
				ErbIrCache.GetIrCachePath(@"D:\games\TW"));
		}
		finally
		{
			AppDataPaths.Configure(appDataBefore);
		}
	}

	[Fact]
	public void TryGetCacheInfo_reports_existing_cache_and_misses()
	{
		using var _ = OpenScope();
		var exeDir = Path.Combine(Path.GetTempPath(), "emuera-irinfo-" + Guid.NewGuid().ToString("N"));
		try
		{
			Directory.CreateDirectory(Path.Combine(exeDir, "_IRCache"));
			var path = Path.Combine(exeDir, "_IRCache", "era_ir.dat");
			File.WriteAllBytes(path, new byte[1234]);
			Assert.True(ErbIrCache.TryGetCacheInfo(exeDir, out long bytes, out DateTime built));
			Assert.Equal(1234, bytes);
			Assert.True(built > DateTime.MinValue && built.Kind == DateTimeKind.Utc);
		}
		finally
		{
			try { Directory.Delete(exeDir, recursive: true); } catch { }
		}
		// 不存在的缓存 → false（无信息承诺）。
		Assert.False(ErbIrCache.TryGetCacheInfo(Path.Combine(Path.GetTempPath(), "emuera-no-such-dir-xyz"), out var ignoredBytes, out var ignoredBuilt));
	}

	[Fact]
	public void CleanupOrphans_deletes_stale_orphan_keeps_current_active_and_grace_period()
	{
		using var _ = OpenScope();
		string appDataBefore = AppDataPaths.Directory;
		var appData = Path.Combine(Path.GetTempPath(), "emuera-orphan-" + Guid.NewGuid().ToString("N"));
		try
		{
			AppDataPaths.Configure(appData);
			string root = Path.Combine(appData, "IRCache");
			string currentKey = ErbIrCache.ComputeGameKey("content://x/current");
			string activeKey = ErbIrCache.ComputeGameKey("content://x/active");
			string staleOrphan = ErbIrCache.ComputeGameKey("content://x/stale");
			string freshOrphan = ErbIrCache.ComputeGameKey("content://x/fresh");
			foreach (var k in new[] { currentKey, activeKey, staleOrphan, freshOrphan })
				Directory.CreateDirectory(Path.Combine(root, k));
			// 超宽限期（40 天前）：应删；宽限期内（现在）：保留。
			Directory.SetLastWriteTimeUtc(Path.Combine(root, staleOrphan), DateTime.UtcNow - TimeSpan.FromDays(40));

			ErbIrCache.CleanupOrphans(["content://x/current"], activeExeDir: "content://x/active");

			Assert.True(Directory.Exists(Path.Combine(root, currentKey)), "current-library key must be kept");
			Assert.True(Directory.Exists(Path.Combine(root, activeKey)), "active-session key must be kept");
			Assert.False(Directory.Exists(Path.Combine(root, staleOrphan)), "stale orphan must be deleted");
			Assert.True(Directory.Exists(Path.Combine(root, freshOrphan)), "grace-period orphan must be kept");
		}
		finally
		{
			try { Directory.Delete(appData, recursive: true); } catch { }
			AppDataPaths.Configure(appDataBefore);
		}
	}

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
