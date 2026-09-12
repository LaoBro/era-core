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
		// issues/12 M4：指纹含 firstDir 清单、IR 写入清单区（IsIrHitRound 读侧与 fp 口径逐位一致）。
		var pending = ErbLoader.BuildPendingList(env, null, out var firstDirs, out _);
		Assert.NotEmpty(pending);
		long fp = ErbIrCache.ComputeContentFingerprint(pending, null, firstDirs);
		string irPath = ErbIrCache.GetIrCachePath(tempRoot);
		Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
		using (var writer = new ErbIrCache.IrCacheWriter(irPath))
			Assert.True(writer.Complete(fp, firstDirs));

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
			var pending = ErbLoader.BuildPendingList(env, null, out _, out _);
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

	/// <summary>
	/// issue 09 真机修复（launch7 list-diff 定位：Config.GetFiles 的 SAF 通配/displayName 差异导致
	/// 清单漂移、IR 永不命中）——命中轮清单改走 <see cref="Preload.EnumerateFilesFromTree"/>（与缓存键
	/// 同源的 Preload 语义枚举）。本测试钉住：两来源在子目录 + 中文/空格文件名下集合与指纹一致。
	/// </summary>
	[Fact]
	public async Task FileSystem_enumerate_files_from_tree_matches_cache_keys()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-listdiff-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		var previousPaths = GamePaths.Current;
		GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());
		var config = new ConfigData();
		using var scope = new RootedScope(GlobalStatic.OpenScope(config), tempRoot, previousPaths);
		var erbDir = Path.Combine(tempRoot, "erb") + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(erbDir);
		Directory.CreateDirectory(Path.Combine(erbDir, "SUB"));
		File.WriteAllText(Path.Combine(erbDir, "TOP.ERB"), "@T\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "SUB", "中文 名前.ERB"), "@C\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "SUB", "NESTED.ERB"), "@N\nRETURN\n");

		Preload.Clear();
		try
		{
			var accessor = new FileSystemGameDirAccessor();
			await Preload.Load(erbDir, accessor);
			var cacheKeys = Preload.GetAllCachedKeys().ToList();
			Assert.Equal(3, cacheKeys.Count);
			// 模拟 GetFilesFromCache 输出（缓存键 → 相对路径）
			var fromCache = cacheKeys
				.Where(k => k.EndsWith(".ERB", StringComparison.OrdinalIgnoreCase)
					&& SafPath.IsUnderRoot(erbDir, k))
				.Select(k => (Logical: SafPath.GetRelativePathFromRoot(erbDir, k), Physical: k))
				.ToList();
			// 命中轮来源：Preload 语义枚举（与缓存键同源）
			var fromTree = Preload.EnumerateFilesFromTree(erbDir, accessor, ".ERB")
				.Select(kv => (Logical: kv.Key, Physical: kv.Value)).ToList();
			Assert.Equal(fromCache.Count, fromTree.Count);

			var treeLogical = fromTree.Select(x => x.Logical).ToHashSet(StringComparer.Ordinal);
			var cacheLogical = fromCache.Select(x => x.Logical).ToHashSet(StringComparer.Ordinal);
			Assert.Equal(treeLogical, cacheLogical);
			Assert.Equal(
				ErbIrCache.ComputeContentFingerprint(fromTree, null),
				ErbIrCache.ComputeContentFingerprint(fromCache, null));
		}
		finally
		{
			Preload.Clear();
		}
	}

	// ===== issues/12 M4：IR 头存 firstDir 清单（命中轮免 SAF 通配扫描）=====

	[Fact]
	public void FirstDirs_roundTrip_nonEmpty_and_empty_list()
	{
		using var _ = OpenScope();
		string dir = Path.Combine(Path.GetTempPath(), "emuera-irfd-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			string path = Path.Combine(dir, "era_ir.dat");
			using (var w = new ErbIrCache.IrCacheWriter(path))
				Assert.True(w.Complete(123, ["1#EV", "sub\\2#EV2"]));
			Assert.True(ErbIrCache.TryReadFirstDirs(path, out var dirs));
			// 写入序 = 权威加载序，读回逐位一致
			Assert.Equal(new List<string> { "1#EV", "sub\\2#EV2" }, dirs);

			// count=0 是合法状态（v2 IR 确认无 *#* 目录）——「有/无清单」以 version==2 为准
			string emptyPath = Path.Combine(dir, "era_ir_empty.dat");
			using (var w = new ErbIrCache.IrCacheWriter(emptyPath))
				Assert.True(w.Complete(456, []));
			Assert.True(ErbIrCache.TryReadFirstDirs(emptyPath, out var emptyDirs));
			Assert.Empty(emptyDirs);
		}
		finally
		{
			try { Directory.Delete(dir, recursive: true); } catch { }
		}
	}

	[Fact]
	public void TryReadFirstDirs_rejects_missing_or_corrupt_file()
	{
		using var scope = OpenScope();
		string path = Path.Combine(Path.GetTempPath(), "emuera-irfd-bad-" + Guid.NewGuid().ToString("N") + ".dat");
		// v1 尺寸（40B）的垃圾头：magic/版本/边界校验任一失败即判废
		File.WriteAllBytes(path, new byte[40]);
		try
		{
			Assert.False(ErbIrCache.TryReadFirstDirs(path, out _));
			Assert.False(ErbIrCache.TryLoad(path, 0, out _));
			// 文件缺失同样 false（调用方回退 SAF 扫描）
			Assert.False(ErbIrCache.TryReadFirstDirs(path + ".missing", out _));
		}
		finally
		{
			try { File.Delete(path); } catch { }
		}
	}

	[Fact]
	public void Fingerprint_includes_firstDir_set_order_invariant()
	{
		using var _ = OpenScope();
		var noDirs = ErbIrCache.ComputeContentFingerprint(Files, null);
		var forward = ErbIrCache.ComputeContentFingerprint(Files, null, ["1#A", "2#B"]);
		var reversed = ErbIrCache.ComputeContentFingerprint(Files, null, ["2#B", "1#A"]);
		// 集合语义：清单顺序无关（SAF 扫描序不稳定，与文件集合同款钉子）
		Assert.Equal(forward, reversed);
		// 任何 *#* 目录增删 → fp 变 → 命中轮 miss → 全扫重建自愈
		Assert.NotEqual(noDirs, forward);
		Assert.NotEqual(forward, ErbIrCache.ComputeContentFingerprint(Files, null, ["1#A"]));
	}

	[Fact]
	public void BuildPendingList_firstDir_override_matches_scan_local()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-irfd-eq-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		var previousPaths = GamePaths.Current;
		GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());
		var config = new ConfigData();
		// 嵌套 *#* 目录（ev/1#INNER）只有在 SearchSubdirectory 开启时才进入 * 扫描（Config.cs:985 门控）。
		config.GetConfigItem(ConfigCode.SearchSubdirectory).SetValue(true);
		using var scope = new RootedScope(GlobalStatic.OpenScope(config), tempRoot, previousPaths);
		var erbDir = Path.Combine(tempRoot, "erb") + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(Path.Combine(erbDir, "1#EV"));
		Directory.CreateDirectory(Path.Combine(erbDir, "1#EV", "NEST"));
		Directory.CreateDirectory(Path.Combine(erbDir, "2#EV2"));
		File.WriteAllText(Path.Combine(erbDir, "1#EV", "A.ERB"), "@A\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "1#EV", "NEST", "B.ERB"), "@B\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "TOP.ERB"), "@T\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "2#EV2", "C.ERB"), "@C\nRETURN\n");
		// 嵌套 *#* 目录：相对名含分隔符（`ev\1#INNER`）。issues/15 §0.3 的 ResolveFirstDirPath
		// 漏改回归点——其文件不被任何其它 firstDir 覆盖，故 override 侧重建立分隔符错误会直接漏文件/错序。
		Directory.CreateDirectory(Path.Combine(erbDir, "ev", "1#INNER"));
		File.WriteAllText(Path.Combine(erbDir, "ev", "1#INNER", "D.ERB"), "@D\nRETURN\n");
		var env = new LoaderEnv(
			Path.Combine(tempRoot, "csv") + Path.DirectorySeparatorChar,
			erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
		{ DirAccessor = new FileSystemGameDirAccessor() };

		Preload.Clear();
		try
		{
			// 建库轮：扫描路径取清单
			var scanned = ErbLoader.BuildPendingList(env, null, out var scanDirs, out _);
			Assert.Equal(3, scanDirs.Count);
			// 命中轮：同一清单走 override（清 memo 模拟冷缓存）
			Preload.Clear();
			var fromIr = ErbLoader.BuildPendingList(env, scanDirs, out var irDirs, out _);
			// override 与扫描的待载清单逐位一致（含 firstDir 嵌套子目录文件）+ 清单原样透传
			Assert.Equal(scanned, fromIr);
			Assert.Equal(scanDirs, irDirs);
			// 合并序语义保持：firstDir 段（4 文件：1#EV/A、1#EV/NEST/B、ev/1#INNER/D、2#EV2/C）整体在前、
			// 主枚举文件（TOP.ERB）收尾。若 override 侧分隔符未归一会把 ev/1#INNER/D 落到段尾 → 顺序错。
			Assert.Equal(5, fromIr.Count);
			Assert.Equal(4, fromIr.FindIndex(p => p.Filename == "TOP.ERB"));
		}
		finally
		{
			Preload.Clear();
		}
	}

	[Fact]
	public void BuildPendingList_firstDir_override_resolves_content_uri_dirs()
	{
		// InMemory stub 的 GetDirectories 恒空（真机 SAF 才有目录扫描）——本钉子专攻 override 侧：
		// 相对名经 accessor.CombinePath 重建目录 document URI 后，子树文件枚举正确
		//（BuildDocumentUriUsingTree 语义，裸拼接会被 Android 解析成目录本身）。
		const string Root = "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FFirstDirOverride";
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var rootedScope = OpenRootedScope(Root, accessor);
		var erbDir = SafCompat.ResolveSubPath(Root, "erb");
		SafCompat.CreateDirectory(erbDir);
		SafCompat.CreateDirectory(SafCompat.CombinePath(erbDir, "1#EV")); // stub 的 DirectoryExists 走显式注册集合
		accessor.Seed(SafCompat.CombinePath(SafCompat.CombinePath(erbDir, "1#EV"), "A.ERB"), Encoding.UTF8.GetBytes("@A\nRETURN\n"));
		accessor.Seed(SafCompat.CombinePath(erbDir, "TOP.ERB"), Encoding.UTF8.GetBytes("@T\nRETURN\n"));

		Preload.Clear();
		try
		{
			var env = new LoaderEnv(
				erbDir, erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
			{ DirAccessor = accessor };
			var pending = ErbLoader.BuildPendingList(env, new List<string> { "1#EV" }, out var firstDirs, out _);
			Assert.Equal(2, pending.Count);
			// Filename 相对 firstDir（既有语义：firstDir 段的枚举源即 <see cref="Preload.EnumerateFilesFromTree"/> 的 per-dir 视角）
			Assert.Equal("A.ERB", pending[0].Filename); // firstDir 文件在前
			Assert.Equal("TOP.ERB", pending[1].Filename);
			Assert.Equal(new List<string> { "1#EV" }, firstDirs); // override 原样透传（进 fp 口径）
		}
		finally
		{
			Preload.Clear();
		}
	}

	[Fact]
	public void IsIrHitRound_uses_ir_firstDir_list_and_self_heals_on_new_dir()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-irfd-hit-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		var previousPaths = GamePaths.Current;
		GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());
		var config = new ConfigData();
		using var scope = new RootedScope(GlobalStatic.OpenScope(config), tempRoot, previousPaths);
		config.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(true);
		config.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		var erbDir = Path.Combine(tempRoot, "erb") + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(Path.Combine(erbDir, "1#EV"));
		File.WriteAllText(Path.Combine(erbDir, "1#EV", "A.ERB"), "@A\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "TOP.ERB"), "@T\nRETURN\n");
		var env = new LoaderEnv(
			Path.Combine(tempRoot, "csv") + Path.DirectorySeparatorChar,
			erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
		{ DirAccessor = new FileSystemGameDirAccessor() };

		// 建库轮：扫描清单 + fp（含清单）→ 写 IR（含清单区）
		var pending = ErbLoader.BuildPendingList(env, null, out var firstDirs, out _);
		var dir = Assert.Single(firstDirs);
		Assert.Equal("1#EV", dir);
		long fp = ErbIrCache.ComputeContentFingerprint(pending, null, firstDirs);
		string irPath = ErbIrCache.GetIrCachePath(tempRoot);
		Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
		using (var writer = new ErbIrCache.IrCacheWriter(irPath))
			Assert.True(writer.Complete(fp, firstDirs));

		Preload.Clear();
		try
		{
			// 命中轮：预判定读 IR 清单（零扫描）→ fp 一致 → hit
			Assert.True(ErbLoader.IsIrHitRound(env), "IR + 清单一致 → 命中轮（跳过内容 Preload + 免目录扫描）");
		}
		finally
		{
			Preload.Clear();
		}

		// 自愈钉子：新增 *#* 目录（含新 .ERB）→ 文件集合变 → fp miss → 预判定 false → 建库轮回退全扫重建
		Directory.CreateDirectory(Path.Combine(erbDir, "2#NEW"));
		File.WriteAllText(Path.Combine(erbDir, "2#NEW", "NEW.ERB"), "@N\nRETURN\n");
		Preload.Clear();
		try
		{
			Assert.False(ErbLoader.IsIrHitRound(env), "新增 *#* 目录 → fp 失配 → 走完整 Preload（自愈）");
		}
		finally
		{
			Preload.Clear();
		}
	}

	/// <summary>OpenScope 的带根路径变体：解析 GamePaths 到指定根 + 访问器，dispose 时还原。</summary>
	// ===== issues/13 M5：清单 sidecar（era_ir.list）——路线 2 显式失效 =====

	[Fact]
	public void FileList_sidecar_roundtrip_and_corrupt_rejection()
	{
		using var scope = OpenScope();
		var irPath = Path.Combine(Path.GetTempPath(), "emuera-fltest-" + Guid.NewGuid().ToString("N"), "era_ir.dat");
		Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
		var pending = new List<(string Logical, string RelToRoot)>
		{
			("A.ERB", "A.ERB"),
			("EV\\B.ERB", "EV\\B.ERB"),
		};
		var tree = new List<string> { "A.ERB", "EV\\B.ERB", "SUB.ERH" };

		Assert.True(ErbIrCache.TryWriteFileList(irPath, pending, tree));
		Assert.True(ErbIrCache.TryReadFileList(irPath, out var readPending, out var readTree));
		Assert.Equal(pending, readPending);
		Assert.Equal(tree, readTree);

		// 损坏（垃圾字节 / 截断）→ false（调用方回退真实枚举，安全方向）
		File.WriteAllBytes(ErbIrCache.GetFileListPath(irPath), new byte[] { 1, 2, 3 });
		Assert.False(ErbIrCache.TryReadFileList(irPath, out var rp, out var rt));
	}

	[Fact]
	public void FileList_sidecar_csv_section_roundtrip_and_legacy_read()
	{
		// issues/14 B：CSV 尾段三态——①新格式 round-trip；②legacy（无尾段）读成功且 legacy=true；
		// ③尾段越界 = 损坏。简单读签名（2 out）对两种布局都必须继续可用（调用方零改动契约）。
		using var scope = OpenScope();
		var irPath = Path.Combine(Path.GetTempPath(), "emuera-flcsv-" + Guid.NewGuid().ToString("N"), "era_ir.dat");
		Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
		var pending = new List<(string Logical, string RelToRoot)> { ("A.ERB", "A.ERB") };
		var tree = new List<string> { "A.ERB", "SUB.ERH" };
		var csv = new List<string> { "GAMEBASE.CSV", "ABL\\ABL0.CSV" };

		// ① 新格式（带 CSV 尾段）
		Assert.True(ErbIrCache.TryWriteFileList(irPath, pending, tree, csv));
		Assert.True(ErbIrCache.TryReadFileListFull(irPath, out var p1, out var t1, out var c1, out var legacy1));
		Assert.False(legacy1);
		Assert.Equal(pending, p1);
		Assert.Equal(tree, t1);
		Assert.Equal(csv, c1);
		// 简单读对带尾段文件照常工作（忽略尾段）
		Assert.True(ErbIrCache.TryReadFileList(irPath, out _, out var t1Simple));
		Assert.Equal(tree, t1Simple);

		// ② legacy 布局（csvTreeRels=null → 不写尾段，与 14 之前字节兼容）
		Assert.True(ErbIrCache.TryWriteFileList(irPath, pending, tree));
		Assert.True(ErbIrCache.TryReadFileListFull(irPath, out var p2, out var t2, out var c2, out var legacy2));
		Assert.True(legacy2);
		Assert.Null(c2);
		Assert.Equal(pending, p2);
		Assert.Equal(tree, t2);
		Assert.True(ErbIrCache.TryReadFileList(irPath, out var p2Simple, out var t2Simple));
		Assert.Equal(pending, p2Simple);
		Assert.Equal(tree, t2Simple);

		// ③ 新格式 + 尾部杂字节 = 损坏（不再被当作合法尾段）
		using (var fs = File.Open(ErbIrCache.GetFileListPath(irPath), FileMode.Append))
			fs.WriteByte(0xFF);
		Assert.False(ErbIrCache.TryReadFileListFull(irPath, out _, out _, out _, out _));
	}

	[Fact]
	public void CombinePaths_default_matches_CombinePath_loop()
	{
		// issues/14 A1：DIM 默认实现 ≡ 逐条 CombinePath（所有未重写批量的实现的行为契约，
		// 也是 replay/SeedTreeMemo 换装后的等价性兜底）。
		var accessor = new FileSystemGameDirAccessor();
		var basePath = Path.Combine(Path.GetTempPath(), "emuera-cp-" + Guid.NewGuid().ToString("N"));
		string[] rels = ["TOP.ERB", "SYS\\A.ERB", "SYS\\EVT\\B.ERB", "SUB.ERH"];
		// DIM 只能经接口分派调用（类类型上不可见）——生产调用点（Preload/ErbLoader）恰都以接口持有。
		IGameDirAccessor accessorItf = accessor;
		var batched = accessorItf.CombinePaths(basePath, rels);
		Assert.Equal(rels.Length, batched.Count);
		for (int i = 0; i < rels.Length; i++)
			Assert.Equal(accessor.CombinePath(basePath, rels[i]), batched[i]);
	}

	[Fact]
	public void TryDeleteCache_removes_ir_and_sidecar_local_layout()
	{
		using var scope = OpenScope();
		string exeDir = GamePaths.Current.ExeDir;
		string irPath = ErbIrCache.GetIrCachePath(exeDir);
		Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
		File.WriteAllText(irPath, "ir");
		File.WriteAllText(ErbIrCache.GetFileListPath(irPath), "list");
		// issues/10 10-B：era_src.dat（CSV/ERH 内容缓存）随重扫同清——三件套一起失效
		string srcPath = ErbIrCache.GetSrcCachePath(exeDir);
		File.WriteAllText(srcPath, "src");

		Assert.True(ErbIrCache.TryDeleteCache(exeDir));
		Assert.False(File.Exists(irPath));
		Assert.False(File.Exists(ErbIrCache.GetFileListPath(irPath)));
		Assert.False(File.Exists(srcPath));
		Assert.True(ErbIrCache.TryDeleteCache(exeDir), "再删幂等（无文件也是成功）");
	}

	[Fact]
	public void FileList_replay_matches_enumeration_including_nested_dirs()
	{
		// M5 等价钉子：命中轮 sidecar 回放 ≡ 建库轮真实枚举——logical 与重建物理路径逐位一致
		//（含嵌套子目录），fp 亦恒等（content-URI stat 降级 0，集合一致即指纹一致）。
		// sidecar 存本地临时目录（存储与内容解耦；真机布局同构——appdata 真实 FS）。
		const string Root = "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FFileListReplay";
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var rootedScope = OpenRootedScope(Root, accessor);
		var erbDir = SafCompat.ResolveSubPath(Root, "erb");
		SafCompat.CreateDirectory(erbDir); // stub 走显式注册集合（M4 同款）
		// stub 的 GetFiles 只返回直接子项（M4 同款限制）——平铺种子，嵌套递归由本地 FS 等价测试覆盖
		accessor.Seed(SafCompat.CombinePath(erbDir, "TOP.ERB"), Encoding.UTF8.GetBytes("@T\nRETURN\n"));
		accessor.Seed(SafCompat.CombinePath(erbDir, "SUB.ERH"), Encoding.UTF8.GetBytes("#DIM X\n"));

		Preload.Clear();
		try
		{
			var env = new LoaderEnv(erbDir, erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
			{ DirAccessor = accessor };
			var scanned = ErbLoader.BuildPendingList(env, null, out var firstDirs, out _);
			Assert.Single(scanned); // stub 平铺：仅 TOP.ERB（.ERB 过滤）

			// 建库轮收尾写 sidecar（与 LoadErbDir Complete 同构：pending rel 对 + 树清单）
			var irPath = Path.Combine(Path.GetTempPath(), "emuera-flreplay-" + Guid.NewGuid().ToString("N"), "era_ir.dat");
			Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
			Assert.True(Preload.TryGetTreeFiles(erbDir, out var treeFiles));
			Assert.Contains("SUB.ERH", treeFiles.Select(f => SafPath.GetRelativePathFromRoot(erbDir, f))); // ② 区含非 .erb（喂 ErhLoader）
			var pendingRels = scanned.Select(p => (p.Filename, SafPath.GetRelativePathFromRoot(erbDir, p.File))).ToList();
			var treeRels = treeFiles.Select(f => SafPath.GetRelativePathFromRoot(erbDir, f)).ToList();
			Assert.True(ErbIrCache.TryWriteFileList(irPath, pendingRels, treeRels));

			Assert.True(ErbIrCache.TryReadFileList(irPath, out var flPending, out var flTree));
			Assert.Equal(treeRels, flTree);
			var replayed = ErbLoader.BuildPendingList(env, null, out _, out _, flPending);
			Assert.Equal(scanned.Count, replayed.Count);
			for (int i = 0; i < scanned.Count; i++)
			{
				Assert.Equal(scanned[i].Filename, replayed[i].Filename);
				Assert.Equal(scanned[i].File, replayed[i].File);
			}
			Assert.Equal(
				ErbIrCache.ComputeContentFingerprint(scanned.Select(p => (p.Filename, p.File)).ToList(), null, firstDirs),
				ErbIrCache.ComputeContentFingerprint(replayed.Select(p => (p.Filename, p.File)).ToList(), null, firstDirs));
		}
		finally
		{
			Preload.Clear();
		}
	}

	[Fact]
	public void FileList_replay_matches_enumeration_local_nested_dirs()
	{
		// 本地 FS（真实递归枚举）的嵌套目录等价钉子——stub 只支持平铺，真机 SAF 的递归语义由本测试代言
		//（M4 同款分工：stub 测 override 侧，递归语义由本地 FS 覆盖）。
		using var scope = OpenScope();
		string exeDir = GamePaths.Current.ExeDir;
		var erbDir = Path.Combine(exeDir, "erb") + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(Path.Combine(erbDir, "SYS", "EVT"));
		File.WriteAllText(Path.Combine(erbDir, "TOP.ERB"), "@T\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "SYS", "A.ERB"), "@A\nRETURN\n");
		File.WriteAllText(Path.Combine(erbDir, "SYS", "EVT", "B.ERB"), "@B\nRETURN\n");
		var env = new LoaderEnv(
			Path.Combine(exeDir, "csv") + Path.DirectorySeparatorChar,
			erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
		{ DirAccessor = new FileSystemGameDirAccessor() };

		Preload.Clear();
		try
		{
			var scanned = ErbLoader.BuildPendingList(env, null, out var firstDirs, out _);
			Assert.Equal(3, scanned.Count);

			var irPath = Path.Combine(Path.GetTempPath(), "emuera-fllocal-" + Guid.NewGuid().ToString("N"), "era_ir.dat");
			Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
			Assert.True(Preload.TryGetTreeFiles(erbDir, out var treeFiles));
			var pendingRels = scanned.Select(p => (p.Filename, SafPath.GetRelativePathFromRoot(erbDir, p.File))).ToList();
			var treeRels = treeFiles.Select(f => SafPath.GetRelativePathFromRoot(erbDir, f)).ToList();
			Assert.Equal(3, treeRels.Count);
			Assert.True(ErbIrCache.TryWriteFileList(irPath, pendingRels, treeRels));

			Assert.True(ErbIrCache.TryReadFileList(irPath, out var flPending, out var flTree));
			Assert.Equal(treeRels, flTree);
			var replayed = ErbLoader.BuildPendingList(env, null, out _, out _, flPending);
			Assert.Equal(scanned.Count, replayed.Count);
			for (int i = 0; i < scanned.Count; i++)
			{
				Assert.Equal(scanned[i].Filename, replayed[i].Filename);
				Assert.Equal(scanned[i].File, replayed[i].File);
			}
		}
		finally
		{
			Preload.Clear();
		}
	}

	[Fact]
	public void GameKey_unifies_content_uri_encoding_forms()
	{
		// issues/13 M5：content URI 的编码形态不统一（引擎侧 SAF 树 URI 保持 %3A/%2F 编码、
		// 扫库侧出现裸 `:` `/`）曾使加载侧缓存键与扫库侧探测键分叉——TryGetCacheInfo 恒 false
		//（徽章假阴）、TryDeleteCache 删空。规范化统一按解码形态哈希，两侧收敛。
		using var _scope = OpenScope();
		const string encoded = "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK1.29.3";
		const string decoded = "content://com.android.externalstorage.documents/tree/primary:emuera/document/primary:emuera/TK1.29.3";
		Assert.Equal(ErbIrCache.ComputeGameKey(encoded), ErbIrCache.ComputeGameKey(decoded));
		// 本地路径语义不变
		Assert.NotEqual(ErbIrCache.ComputeGameKey(@"C:\games\a"), ErbIrCache.ComputeGameKey(@"C:\games\a\b"));
	}

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
