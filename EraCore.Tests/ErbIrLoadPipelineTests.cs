using System;
using System.Collections.Generic;
using System.IO;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;
using Config = MinorShift.Emuera.Runtime.Config.Config;
using Path = System.IO.Path;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0033（第四刀前置子集）：IR 装载流水线的**直接单测**——临时目录构造 IR，**不起真引擎**
///（不构造 <c>Process</c>、不用 <c>LoaderTestHarness</c>）。这是本票 leverage 的证据：装载期的
/// 命中判定 / 建库 / 写盘收尾 / 常驻读句柄可在流水线边界上直接断言。
/// <list type="bullet">
/// <item>IR 关闭 → 纯旁路（结果对象只回传输入）。</item>
/// <item>miss → 开写器 → <c>Complete</c> → 下一轮命中（含命中全集与常驻读句柄）。</item>
/// <item><c>Abort</c> → 不留半截缓存，且幂等。</item>
/// <item><c>AppendFile</c> → <c>Complete</c> → 命中轮索引与 blob 可读回（生成期链路的端到端钉子）。</item>
/// <item>命中轮 <c>Complete</c> 为空操作，既有 IR 不被改写。</item>
/// </list>
/// <para>
/// 脚手架与 <c>ErbIrCacheTests</c> 同款最小形态：只 <c>GamePaths.Resolve(tempRoot)</c> + 配置作用域，
/// 不初始化引擎。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public class ErbIrLoadPipelineTests
{
	[Fact]
	public void Prepare_ir_disabled_is_pure_passthrough()
	{
		using var root = new TempRoot();
		var (env, irPath, pending, firstDirs, erbFileCount) = BuildFixtures(root.Root);
		var pipeline = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env));

		var result = pipeline.Prepare(irEnabled: false, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);

		Assert.False(result.Enabled);
		Assert.False(result.Hit);
		Assert.Null(result.State);
		// 命中轮全集（AllFiles）现随 State 生灭（第四刀）——State 为 null 即无全集。
		Assert.Same(pending, result.Pending);
		Assert.Same(firstDirs, result.FirstDirs);
		Assert.Equal(erbFileCount, result.ErbFileCount);
		Assert.False(pipeline.CollectIr);
		Assert.False(File.Exists(irPath));
	}

	[Fact]
	public void Prepare_miss_opens_writer_then_Complete_produces_hit_on_next_round()
	{
		using var root = new TempRoot();
		var (env, irPath, pending, firstDirs, erbFileCount) = BuildFixtures(root.Root);

		var buildRound = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env));
		var miss = buildRound.Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);

		Assert.True(miss.Enabled);
		Assert.False(miss.Hit);
		Assert.False(miss.State!.Available);
		Assert.Null(miss.State!.AllFiles); // 全集只对命中轮存在（miss 轮 State 非 null 但无全集）
		Assert.True(buildRound.CollectIr, "miss 轮必须开增量写器（生成期收集 IR blob）");

		buildRound.Complete(irPath, miss.Fingerprint, miss.FirstDirs, miss.Pending, miss.Hit, irSidecarLegacy: false);

		Assert.False(buildRound.CollectIr, "收尾后写器必须关闭（CollectIr 转 false）");
		Assert.True(File.Exists(irPath), "Complete 落盘后 IR 文件存在");

		var hitRound = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env));
		var hit = hitRound.Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);

		Assert.True(hit.Hit);
		Assert.True(hit.State!.Available);
		Assert.NotNull(hit.State.ReadStream); // issues/05 句柄常驻：命中轮开常驻读句柄
		Assert.NotNull(hit.State!.AllFiles); // issues/17 A′：命中轮全集（第四刀后归 IrState）
		Assert.Contains(pending[0].Filename, hit.State!.AllFiles!);
		Assert.False(hitRound.CollectIr, "命中轮不开写器");

		hit.State.CloseHandle();
	}

	[Fact]
	public void Abort_discards_writer_leaves_no_cache_and_is_idempotent()
	{
		using var root = new TempRoot();
		var (env, irPath, pending, firstDirs, erbFileCount) = BuildFixtures(root.Root);
		var pipeline = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env));

		pipeline.Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);
		Assert.True(pipeline.CollectIr);

		pipeline.Abort();

		Assert.False(pipeline.CollectIr);
		Assert.False(File.Exists(irPath), "未收尾 → 不得留下最终 IR 文件");
		Assert.False(File.Exists(irPath + ".tmp"), "Abort 必须清 tmp");

		pipeline.Abort(); // 幂等

		// 放弃后下一轮仍必须是 miss（自愈语义：没有半截缓存可被误读为命中）
		var next = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env)).Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);
		Assert.False(next.Hit);
	}

	[Fact]
	public void AppendFile_blobs_survive_Complete_and_are_readable_on_hit_round()
	{
		using var root = new TempRoot();
		var (env, irPath, pending, firstDirs, erbFileCount) = BuildFixtures(root.Root);

		var buildRound = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env));
		var miss = buildRound.Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);
		Assert.True(buildRound.CollectIr);

		buildRound.AppendFile(pending[0].Filename,
			new List<IrBlob> { IrBlob.Valid("@MAIN", 42, new[] { new IrLine(43, "RETURN") }) });
		buildRound.Complete(irPath, miss.Fingerprint, miss.FirstDirs, miss.Pending, miss.Hit, irSidecarLegacy: false);

		var hit = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env)).Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);
		Assert.True(hit.Hit);

		var entry = Assert.Single(hit.State!.Index);
		Assert.Equal(pending[0].Filename, entry.Key.File);
		Assert.Equal("@MAIN", entry.Key.Label);
		Assert.Equal(42, entry.Key.LineNo);

		// blob 本体只序列化行链（Label / LineNo 由索引条目承载，见 ErbIrCache.ReadBlob），故此处只断言行。
		var blob = hit.State.ReadBlob(entry.Value);
		var line = Assert.Single(blob.Lines);
		Assert.Equal(43, line.LineNo);
		Assert.Equal("RETURN", line.Text);

		hit.State.CloseHandle();
	}

	[Fact]
	public void Complete_on_hit_round_is_noop_and_keeps_existing_cache()
	{
		using var root = new TempRoot();
		var (env, irPath, pending, firstDirs, erbFileCount) = BuildFixtures(root.Root);

		var buildRound = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env));
		var miss = buildRound.Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);
		buildRound.Complete(irPath, miss.Fingerprint, miss.FirstDirs, miss.Pending, miss.Hit, irSidecarLegacy: false);

		// 命中轮：无写器 → Complete 是空操作（本地路径下 legacy 自愈门控关闭），既有 IR 不得被改写
		var hitRound = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env));
		var hit = hitRound.Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);
		Assert.True(hit.Hit);
		Assert.False(hitRound.CollectIr);

		hitRound.Complete(irPath, hit.Fingerprint, hit.FirstDirs, hit.Pending, hit.Hit, irSidecarLegacy: false);
		hit.State!.CloseHandle();

		var again = new ErbIrLoadPipeline(env, IrLoadContext.FromCurrent(env)).Prepare(true, irPath, pending, firstDirs, erbFileCount, irFirstDirs: null, irFileList: null);
		Assert.True(again.Hit, "命中轮收尾不得破坏既有 IR");
		again.State!.CloseHandle();
	}

	// ===== 脚手架（同 ErbIrCacheTests 的最小形态：临时根 + GamePaths.Resolve + 配置作用域，不起引擎）=====

	private static (LoaderEnv Env, string IrPath, List<(string Filename, string File)> Pending, List<string> FirstDirs, int ErbFileCount)
		BuildFixtures(string tempRoot)
	{
		var erbDir = Path.Combine(tempRoot, "erb") + Path.DirectorySeparatorChar;
		Directory.CreateDirectory(erbDir);
		File.WriteAllText(Path.Combine(erbDir, "MAIN.ERB"), "@MAIN\nRETURN\n");
		var env = new LoaderEnv(
			Path.Combine(tempRoot, "csv") + Path.DirectorySeparatorChar,
			erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
		{ DirAccessor = new FileSystemGameDirAccessor() };

		var pending = ErbIrLoadPipeline.BuildPendingList(env, IrLoadContext.FromCurrent(env), null, out var firstDirs, out var erbFileCount);
		Assert.NotEmpty(pending);

		var irPath = ErbIrCache.GetIrCachePath(tempRoot);
		Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
		return (env, irPath, pending, firstDirs, erbFileCount);
	}

	/// <summary>临时游戏根 + 配置作用域（<see cref="GamePaths"/> 与 <c>GlobalStatic</c> 均为进程级静态，须成对还原）。</summary>
	private sealed class TempRoot : IDisposable
	{
		private readonly IDisposable _scope;
		private readonly GamePaths _previous;

		internal string Root { get; }

		internal TempRoot()
		{
			Root = Path.Combine(Path.GetTempPath(), "emuera-irpipe-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Root);
			_previous = GamePaths.Current;
			GamePaths.Resolve(Root, new FileSystemGameDirAccessor());
			Preload.Clear();
			_scope = GlobalStatic.OpenScope(new ConfigData());
		}

		public void Dispose()
		{
			_scope.Dispose();
			GamePaths.SetCurrent(_previous);
			Preload.Clear();
			try { Directory.Delete(Root, recursive: true); } catch { /* 临时目录清理失败不掩盖断言 */ }
		}
	}
}
