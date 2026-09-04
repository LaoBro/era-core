using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;
// 测试命名空间（MinorShift.Emuera.Tests）无外层 Config 命名空间可命中，直接写 Config.Config 会绑定到
// 类 Config 而非命名空间——用别名钉住静态门面（生产代码在 Runtime.* 命名空间内无此歧义）。
using Config = MinorShift.Emuera.Runtime.Config.Config;

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

	// ===== issue 09：命中轮跳过内容 Preload =====
	// 命中轮（缓存空）清单改由 Config.Config.GetFiles 枚举——与 Preload.GetFilesFromCache 的
	// logical 集合必须一致（同一 content-URI 树），否则 LoadErbDir 内判定与建库轮指纹漂移、命中轮永不命中。

	[Fact]
	public async Task ContentUri_file_listing_consistent_between_cache_and_config()
	{
		const string Root = "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FListingConsistency";
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var rootedScope = OpenRootedScope(Root, accessor);
		var erbDir = SafCompat.ResolveSubPath(Root, "erb");
		SafCompat.CreateDirectory(erbDir);
		accessor.Seed(SafCompat.CombinePath(erbDir, "MAIN.ERB"), Encoding.UTF8.GetBytes("@M\nRETURN\n"));
		accessor.Seed(SafCompat.CombinePath(erbDir, "EVENT.ERB"), Encoding.UTF8.GetBytes("@E\nRETURN\n"));
		accessor.Seed(SafCompat.CombinePath(erbDir, "SUB.ERB"), Encoding.UTF8.GetBytes("@S\nRETURN\n"));

		Preload.Clear();
		try
		{
			await Preload.Load(erbDir, accessor);
			var fromCache = Preload.GetFilesFromCache(erbDir, ".ERB", "test");
			Assert.NotNull(fromCache);
			var fromConfig = Config.GetFiles(erbDir, "*.ERB");
			Assert.NotEmpty(fromConfig);

			// logical 集合相等（命中轮/预判定与建库轮同树枚举一致）
			Assert.Equal(
				fromConfig.Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase),
				fromCache!.Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
			// 物理路径（指纹输入的另一半）同源枚举，应逐位一致
			Assert.Equal(
				fromConfig.Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal),
				fromCache.Select(kv => kv.Value).ToHashSet(StringComparer.Ordinal));
			// 两来源指纹恒等（长度/mtime 对 content-URI 均降级 0 → 集合一致即指纹一致）
			Assert.Equal(
				ErbIrCache.ComputeContentFingerprint(
					fromConfig.Select(kv => (kv.Key, kv.Value)).ToList(), null),
				ErbIrCache.ComputeContentFingerprint(
					fromCache.Select(kv => (kv.Key, kv.Value)).ToList(), null));
		}
		finally
		{
			Preload.Clear();
		}
	}

	[Fact]
	public async Task IsIrHitRound_detects_valid_ir_and_falls_back_to_full_preload_otherwise()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-irhit-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		var previousPaths = GamePaths.Current;
		GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());
		var config = new ConfigData();
		using var scope = new RootedScope(GlobalStatic.OpenScope(config), tempRoot, previousPaths);
		config.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(true);
		config.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);

		var erbDir = Path.Combine(tempRoot, "erb") + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(erbDir);
		File.WriteAllText(Path.Combine(erbDir, "MAIN.ERB"), "@M\nRETURN\n");
		var env = new LoaderEnv(
			Path.Combine(tempRoot, "csv") + Path.DirectorySeparatorChar,
			erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
		{ DirAccessor = new FileSystemGameDirAccessor() };

		// 建一个合法（空函数集）IR：指纹来自同一清单构建路径——BuildPendingList 即 IsIrHitRound 内部所用。
		var pending = ErbLoader.BuildPendingList(env, out _, out _);
		Assert.NotEmpty(pending);
		long fp = ErbIrCache.ComputeContentFingerprint(pending, null);
		string irPath = ErbIrCache.GetIrCachePath(tempRoot);
		Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
		using (var writer = new ErbIrCache.IrCacheWriter(irPath))
			Assert.True(writer.Complete(fp));

		Assert.True(ErbLoader.IsIrHitRound(env), "valid IR + 开关齐备 → 命中轮（跳过内容 Preload）");

		File.Delete(irPath);
		Assert.False(ErbLoader.IsIrHitRound(env), "IR 缺失 → 走完整 Preload");

		// 建库轮语义：lazy 关闭 → 不判命中，直接走完整 Preload
		config.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(false);
		Assert.False(ErbLoader.IsIrHitRound(env));
	}

	[Fact]
	public async Task ContentUri_empty_cache_falls_back_to_enumeration_for_pending_list()
	{
		// issue 09 真机回归钉（2026-09-05 launch4 分析发现）：命中轮跳过内容 Preload →
		// Preload 缓存整体为空。GetFilesFromCache 必须返回 null（而非空列表）让调用方走
		// Config.GetFiles 枚举兜底，否则 content-URI 命中轮 ERB 清单为空、游戏不加载任何脚本。
		const string Root = "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FListingEmptyCache";
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var rootedScope = OpenRootedScope(Root, accessor);
		var erbDir = SafCompat.ResolveSubPath(Root, "erb");
		SafCompat.CreateDirectory(erbDir);
		accessor.Seed(SafCompat.CombinePath(erbDir, "MAIN.ERB"), Encoding.UTF8.GetBytes("@M\nRETURN\n"));

		Preload.Clear();
		try
		{
			// 缓存空（命中轮语义）→ GetFilesFromCache 返回 null，调用方走枚举兜底
			Assert.Null(Preload.GetFilesFromCache(erbDir, ".ERB", "test"));

			var env = new LoaderEnv(
				erbDir, erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
			{ DirAccessor = accessor };
			var pending = ErbLoader.BuildPendingList(env, out _, out _);
			Assert.Single(pending);
			Assert.Equal("MAIN.ERB", pending[0].Filename);

			// 对照：缓存加载过 → GetFilesFromCache 返回缓存清单（非 null）
			await Preload.Load(erbDir, accessor);
			var fromCache = Preload.GetFilesFromCache(erbDir, ".ERB", "test");
			Assert.NotNull(fromCache);
			Assert.Equal("MAIN.ERB", Assert.Single(fromCache!).Key);
		}
		finally
		{
			Preload.Clear();
		}
	}

	/// <summary>OpenScope 的带根路径变体：解析 GamePaths 到指定根 + 访问器，dispose 时还原。</summary>
	private static IDisposable OpenRootedScope(string root, IGameDirAccessor accessor)
	{
		var previous = GamePaths.Current;
		GamePaths.Resolve(root, accessor);
		var config = new ConfigData();
		return new RootedScope(GlobalStatic.OpenScope(config), tempRoot: null!, previous);
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

	private sealed class RootedScope(IDisposable inner, string tempRoot, GamePaths previous) : IDisposable
	{
		public void Dispose()
		{
			inner.Dispose();
			GamePaths.SetCurrent(previous);
			if (tempRoot != null)
			{
				try { Directory.Delete(tempRoot, recursive: true); } catch { /* 临时目录清理失败不掩盖断言 */ }
			}
		}
	}
}
