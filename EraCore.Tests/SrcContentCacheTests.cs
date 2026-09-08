using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Sub;
using Xunit;
// 测试命名空间无外层 Config 命名空间可命中，用别名钉住静态门面（同 ErbIrCacheTests 注释）。
using Config = MinorShift.Emuera.Runtime.Config.Config;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issues/10 10-B：CSV/ERH 源内容缓存（era_src.dat v2 扁平格式）的集成钉子。AppDataIsolated 串行
/// 集合——写入经 AppDataPaths.Configure 重定向到临时 appdata。覆盖：建库轮写（行取自 Preload.files）
/// → 命中轮重水化（零 SAF 查询，路线 2——不校验新鲜度，用户重扫是权威）→ EraStreamReader.OpenOnCache
/// 咽喉消费；损坏/半截文件判 miss + 删除（防撕裂读半截）；旋钮关闭全链路静默；
/// 大小写不敏感查表（R11 真机回归钉：磁盘名 GameBase.csv ↔ 引擎请求 GAMEBASE.CSV）。
/// </summary>
[Collection("AppDataIsolated")]
public class SrcContentCacheTests
{
	[Fact]
	public async Task Write_then_rehydrate_serves_lines_via_open_on_cache()
	{
		using var scope = OpenScope(out var accessor, out var env, out var csvDir, out var erbDir, out var srcPath);
		SeedCorpus(accessor, csvDir, erbDir);

		// 建库轮：内容 Preload → 写内容缓存
		await Preload.LoadTreesAsync(erbDir, csvDir, accessor);
		SrcContentCache.TryWriteAfterPreload(env);
		Assert.True(File.Exists(srcPath), "建库轮收尾应写 era_src.dat");

		// 轮界清空后命中轮重水化 → 快照可查
		Preload.Clear();
		SrcContentCache.TryRehydrate(env);
		Assert.NotNull(SrcContentCache.TryGetLines(accessor.CombinePath(csvDir, "ABL.CSV")));

		// 咽喉消费：EraStreamReader 在 Preload miss 后吃快照（CSV / ERH / .als 全形态）
		Assert.True(ReadFirstLine(accessor.CombinePath(csvDir, "ABL.CSV"), out var abl));
		Assert.Equal("1,体力", abl);
		Assert.True(ReadFirstLine(accessor.CombinePath(csvDir, "GAMEBASE.CSV"), out var gb));
		Assert.Equal("コード,1", gb); // 大小写不敏感：磁盘名 GameBase.csv ↔ 请求 GAMEBASE.CSV（R11 钉）
		Assert.True(ReadFirstLine(accessor.CombinePath(csvDir, "ALIAS.ALS"), out var als));
		Assert.Equal("体力,HP", als);
		Assert.True(ReadFirstLine(accessor.CombinePath(erbDir, "HEADER.ERH"), out var erh));
		Assert.Equal("#DIM X", erh);

		// 未缓存形态：.erb（IR 领域）与 .txt 不入快照 → null（照旧懒读，不受污染）
		Assert.Null(SrcContentCache.TryGetLines(accessor.CombinePath(erbDir, "TOP.ERB")));
		Assert.Null(SrcContentCache.TryGetLines(accessor.CombinePath(csvDir, "NOTE.TXT")));

		// 命中不删缓存：再次重水化幂等（同轮内二次调用安全）
		SrcContentCache.TryRehydrate(env);
		Assert.True(File.Exists(srcPath));
	}

	[Fact]
	public async Task Rehydrate_serves_stale_lines_without_validation_route2()
	{
		// 路线 2 语义钉：文件在缓存写入后被「外部修改」（stub 内容改写），重水化仍按快照喂行——
		// 新鲜度权威 = 用户重扫（TryDeleteCache），不做任何自动校验。
		using var scope = OpenScope(out var accessor, out var env, out var csvDir, out var erbDir, out var srcPath);
		SeedCorpus(accessor, csvDir, erbDir);
		await Preload.LoadTreesAsync(erbDir, csvDir, accessor);
		SrcContentCache.TryWriteAfterPreload(env);
		Preload.Clear();

		accessor.Seed(accessor.CombinePath(csvDir, "ABL.CSV"), Encoding.UTF8.GetBytes("999,改过\n"));
		SrcContentCache.TryRehydrate(env);
		Assert.True(ReadFirstLine(accessor.CombinePath(csvDir, "ABL.CSV"), out var line));
		Assert.Equal("1,体力", line); // 快照旧行照常服务——路线 2 的明确代价
		Assert.True(File.Exists(srcPath));
	}

	[Fact]
	public async Task Rehydrate_rejects_corrupt_file()
	{
		using var scope = OpenScope(out var accessor, out var env, out var csvDir, out var erbDir, out var srcPath);
		SeedCorpus(accessor, csvDir, erbDir);
		await Preload.LoadTreesAsync(erbDir, csvDir, accessor);
		SrcContentCache.TryWriteAfterPreload(env);
		Preload.Clear();

		Directory.CreateDirectory(Path.GetDirectoryName(srcPath)!);
		await File.WriteAllBytesAsync(srcPath, new byte[] { 9, 9, 9, 9, 1, 2, 3 });
		SrcContentCache.TryRehydrate(env);
		Assert.Null(SrcContentCache.TryGetLines(accessor.CombinePath(csvDir, "ABL.CSV")));
		Assert.False(File.Exists(srcPath));
	}

	[Fact]
	public async Task Knob_off_disables_write_and_rehydrate()
	{
		using var scope = OpenScope(out var accessor, out var env, out var csvDir, out var erbDir, out var srcPath,
			srcContentCache: false);
		SeedCorpus(accessor, csvDir, erbDir);
		await Preload.LoadTreesAsync(erbDir, csvDir, accessor);
		SrcContentCache.TryWriteAfterPreload(env);
		Assert.False(File.Exists(srcPath), "旋钮 off = 建库轮不写");
		SrcContentCache.TryRehydrate(env);
		Assert.Null(SrcContentCache.TryGetLines(accessor.CombinePath(csvDir, "ABL.CSV"))); // 旋钮 off = 命中轮不重水化
	}

	// ── 基建 ───────────────────────────────────────────────────────────────────────

	private const string Root = "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FSrcContentCache";

	private static void SeedCorpus(SafCompatContentUriTests.InMemoryContentDirAccessor accessor, string csvDir, string erbDir)
	{
		// stub 走显式注册集合（M4/M5 同款）；GetFiles 只返回直接子项 → 平铺语料
		accessor.Seed(accessor.CombinePath(csvDir, "ABL.CSV"), Encoding.UTF8.GetBytes("1,体力\n2,気力\n"));
		accessor.Seed(accessor.CombinePath(csvDir, "TALENT.CSV"), Encoding.UTF8.GetBytes("0,天才\n"));
		// R11 真机回归钉：provider displayName 是磁盘真实大小写（GameBase.csv），引擎固定名请求恒全大写
		//（GAMEBASE.CSV）——快照查表必须大小写不敏感（镜像 SAF 懒读的 provider 解析语义）
		accessor.Seed(accessor.CombinePath(csvDir, "GameBase.csv"), Encoding.UTF8.GetBytes("コード,1\n"));
		accessor.Seed(accessor.CombinePath(csvDir, "ALIAS.ALS"), Encoding.UTF8.GetBytes("体力,HP\n"));
		accessor.Seed(accessor.CombinePath(csvDir, "NOTE.TXT"), Encoding.UTF8.GetBytes("not engine input"));
		accessor.Seed(accessor.CombinePath(erbDir, "TOP.ERB"), Encoding.UTF8.GetBytes("@T\nRETURN\n"));
		accessor.Seed(accessor.CombinePath(erbDir, "HEADER.ERH"), Encoding.UTF8.GetBytes("#DIM X\n"));
	}

	/// <summary>AppData 重定向 + 根化 GamePaths + Config 域（AppDataIsolated 串行集合，镜像 ErbFileListCacheTests）。</summary>
	private static IDisposable OpenScope(out SafCompatContentUriTests.InMemoryContentDirAccessor accessor,
		out LoaderEnv env, out string csvDir, out string erbDir, out string srcPath, bool srcContentCache = true)
	{
		var tempAppData = Path.Combine(Path.GetTempPath(), "emuera-srcappdata-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempAppData);
		var previousAppData = AppDataPaths.Directory;
		AppDataPaths.Configure(tempAppData);
		accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		var previousPaths = GamePaths.Current;
		GamePaths.Resolve(Root, accessor);
		var config = new ConfigData();
		if (!srcContentCache)
			config.GetConfigItem(ConfigCode.SrcContentCache).SetValue(false);
		var scope = GlobalStatic.OpenScope(config);
		var csv = SafCompat.ResolveSubPath(Root, "csv");
		var erb = SafCompat.ResolveSubPath(Root, "erb");
		SafCompat.CreateDirectory(csv);
		SafCompat.CreateDirectory(erb);
		env = new LoaderEnv(csv, erb, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
			{ DirAccessor = accessor };
		csvDir = csv;
		erbDir = erb;
		srcPath = SrcContentCache.GetSrcCachePath(Root);
		return new Scope(scope, tempAppData, previousAppData, previousPaths);
	}

	private static bool ReadFirstLine(string path, out string line)
	{
		using var reader = new EraStreamReader(false);
		if (!reader.OpenOnCache(path, SafPath.GetLogicalFileName(path)))
		{
			line = "";
			return false;
		}
		var st = reader.ReadEnabledLine();
		line = st?.ToString() ?? "";
		return true;
	}

	private sealed class Scope(IDisposable inner, string tempAppData, string previousAppData, GamePaths previous) : IDisposable
	{
		public void Dispose()
		{
			Preload.Clear();
			GamePaths.SetCurrent(previous);
			inner.Dispose();
			AppDataPaths.Configure(previousAppData);
			try { Directory.Delete(tempAppData, recursive: true); } catch { /* 临时目录清理失败不掩盖断言 */ }
		}
	}
}
