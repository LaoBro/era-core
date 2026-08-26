using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// b2-irdiskcache（B 线 B2，阶段① blob 库）单测：
/// 首次加载生成 IR 文件；第二次加载命中 IR（索引直建桩免读源），物化经 blob 后背且产物与源后背/eager 逐行等价；
/// 指纹失效（改脚本次数）→ 重建不跑旧 IR（无幽灵）；开关 IrDiskCacheLoad:FALSE → 完全 B1（不生成 IR）；
/// IR 损坏 → 静默回退源，不抛新异常。
/// 注：复用 lazy 模式真实引擎 harness；第二次加载须新建 loader（_ir 为 loader 实例状态）并经同一 TempRoot 命中磁盘 IR。
/// </summary>
[Collection("LoaderTests")]
public class b2IrDiskCacheTests
{
	private const string Corpus = """
@EVENTFIRST
	CALL CALLED
	PRINTFORM 3
	RETURN

@CALLED
#LOCALSIZE 7
#LOCALSSIZE 3
	RETURN

@UNCALLED
	PRINTFORM 99
	X = 1
	RETURN
""";

	private static string FullIrPath(string tempRoot) => Path.Combine(tempRoot, "_IRCache", "era_ir.dat");

	private static string SummarizeChain(LogicalLine header)
	{
		var list = new List<string>();
		var n = header.NextLine;
		while (n != null)
		{
			if (n is NullLine || n is FunctionLabelLine)
				break;
			if (n is GotoLabelLine g)
				list.Add($"G@{n.Position?.LineNo ?? -1}");
			else if (n is InstructionLine ins)
				list.Add($"{ins.FunctionCode}@{n.Position?.LineNo ?? -1}");
			n = n.NextLine;
		}
		return string.Join(";", list);
	}

	private static FunctionLabelLine Find(LabelDictionary labelDic, string name)
	{
		var ne = labelDic.GetNonEventLabel(name);
		if (ne != null)
			return ne;
		var ev = labelDic.GetEventLabels(name);
		if (ev != null)
			foreach (var lst in ev)
				foreach (var l in lst ?? [])
					if (l != null)
						return l;
		return null!;
	}

	private static void EnableLazy(LoaderTestHarness h)
	{
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		// IrDiskCacheLoad 默认 true；测试显式对齐（含 fallback 数组路径）
		ConfigData.Current!.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(true);
	}

	/// <summary>
	/// 三态等价核心：同一语料，首次加载（源后背）+ 第二次加载（IR 后背）物化产物逐行链 + 帧尺寸等价。
	/// 第二次加载在索引命中后，CalledFunction 经主循环 + UNCALLED 经运行时接缝都以 blob 后背物化。
	/// </summary>
	[Fact]
	public async Task Ir_second_launch_materializes_via_blob_equiv_to_source()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		var irErbPath = h.WriteErb("IR2.ERB", Corpus);
		var irFile = FullIrPath(h.TempRoot);

		// ---- 首次加载：生成 IR，源后背物化 ----
		string? srcCalled = null, srcUncalled = null;
		int srcLocal = -1, srcLocals = -1;
		{
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(ok, "round1 LoadErbDir failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
			Assert.True(File.Exists(irFile), "first launch should build IR cache file");

			var called = Find(h.LabelDic, "CALLED");
			Assert.False(called.IsStub);
			srcCalled = SummarizeChain(called);
			srcLocal = called.LocalLength;
			srcLocals = called.LocalsLength;

			var uncalled = Find(h.LabelDic, "UNCALLED");
			Assert.True(uncalled.IsStub);
			ErbLoader.MaterializeOnRuntime(uncalled); // 源后背（本轮未命中 IR）
			Assert.False(uncalled.IsStub);
			srcUncalled = SummarizeChain(uncalled);
		}
		// 清理缓存文件以便……不删——IR 文件保留供第二轮命中。

		// ---- 第二次加载：索引直建桩（免读源），物化全走 blob 后背 ----
		string? blobCalled = null, blobUncalled = null;
		int blobLocal = -1, blobLocals = -1;
		{
			// 复用同一引擎（TempRoot 不变）；新 loader 使 _ir 从磁盘重新加载命中。
			var irMtimeBefore = File.GetLastWriteTimeUtc(irFile);
			var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			var ok2 = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(ok2, "round2 LoadErbDir failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
			// 源未改动 → 指纹命中 → IR 不应被重建。
			Assert.Equal(irMtimeBefore, File.GetLastWriteTimeUtc(irFile));
			// 证明 blob 后背：删除源文件后，IRCACHE 命中的函数仍无需源即可物化（warm start 独立于源）。
			File.Delete(irErbPath);
			// 主循环已经 blob 后背物化 CALLED
			var called2 = Find(h.LabelDic, "CALLED");
			Assert.False(called2.IsStub, "CALLED should be materialized (blob) in round2"); ;
			blobCalled = SummarizeChain(called2);
			blobLocal = called2.LocalLength;
			blobLocals = called2.LocalsLength;

			// 运行时接缝 blob 后背物化 UNCALLED（源已被删除 → 若回退源必失败，blob 才能成功）
			var uncalled2 = Find(h.LabelDic, "UNCALLED");
			Assert.True(uncalled2.IsStub, "UNCALLED must start as stub in round2");
			ErbLoader.MaterializeOnRuntime(uncalled2);
			Assert.False(uncalled2.IsStub, "UNCALLED must materialize via IR blob without source (proves blob backend)");
			Assert.False(uncalled2.IsError, "UNCALLED must not degrade to placeholder when source removed + blob hit");
			blobUncalled = SummarizeChain(uncalled2);
		}

		// 物化产物等价：行链逐项相等。
		Assert.Equal(srcCalled, blobCalled);
		Assert.Equal(srcUncalled, blobUncalled);
		// 帧尺寸（#LOCALSIZE/#LOCALSSIZE）等价。
		Assert.Equal(srcLocal, blobLocal);
		Assert.Equal(srcLocals, blobLocals);
	}

	/// <summary>开关 FALSE：完全不生成 IR，行为 = B1 现状（可正常物化）。</summary>
	[Fact]
	public async Task Ir_disabled_falls_back_to_pure_source()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		ConfigData.Current!.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(false);
		await h.PreloadAsync();
		h.WriteErb("IROFF.ERB", Corpus);
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "disabled LoadErbDir failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		Assert.False(File.Exists(FullIrPath(h.TempRoot)), "IrDiskCacheLoad:FALSE must not create IR cache");

		var uncalled = Find(h.LabelDic, "UNCALLED");
		Assert.True(uncalled.IsStub);
		ErbLoader.MaterializeOnRuntime(uncalled);
		Assert.False(uncalled.IsStub, "disabled-mode must still materialize via source");
	}

	/// <summary>失效：改脚本 → 指纹变 → 重建 IR，物化产物反映新内容（无幽灵残留）。</summary>
	[Fact]
	public async Task Ir_invalidated_on_script_change_rebuilds_without_ghost()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		var path = h.WriteErb("IRCHG.ERB", Corpus);
		var irFile = FullIrPath(h.TempRoot);

		// 首次：建缓存，物化 UNCALLED（源）
		string? beforeChain = null;
		{
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(File.Exists(irFile));
			var before = Find(h.LabelDic, "UNCALLED");
			ErbLoader.MaterializeOnRuntime(before);
			beforeChain = SummarizeChain(before);
			Assert.NotEqual("", beforeChain);
		}

		// 改脚本：UNCALLED 函数体变化（新增一行）
		File.SetLastWriteTimeUtc(path, DateTime.UtcNow); // 提前触碰 mtime，保证改动后 mtime 前进
		System.Threading.Thread.Sleep(20);
		File.WriteAllText(path, Corpus.Replace("\tX = 1", "\tX = 1\n\tPRINTFORM extra"), System.Text.Encoding.UTF8);

		// 第二次：指纹变化 → IR 失效重建；UNCALLED 物化反映新内容（无幽灵）
		{
			var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			var after = Find(h.LabelDic, "UNCALLED");
			ErbLoader.MaterializeOnRuntime(after);
			Assert.False(after.IsStub);
			var afterChain = SummarizeChain(after);
			// 新内容比旧多一行 → 行链项数增加，且与旧链不同（无幽灵残留旧 IR）
			Assert.NotEqual(beforeChain, afterChain);
			Assert.True(afterChain.Split(';').Length > beforeChain!.Split(';').Length,
				"rebuilt IR should reflect added line (no ghost)\nbefore=" + beforeChain + "\nafter=" + afterChain);
		}
	}

	/// <summary>IR 损坏 → 加载入口校验失败回退源（整轮回退），物化仍正确、不抛新异常。</summary>
	[Fact]
	public async Task Ir_corrupt_falls_back_to_source_silently()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		h.WriteErb("IRBAD.ERB", Corpus);
		var irFile = FullIrPath(h.TempRoot);

		// 首轮建缓存
		{
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(File.Exists(irFile));
		}
		// 损坏（改字节破坏 magic）
		File.WriteAllBytes(irFile, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 });

		// 第二轮：校验失败 → 回退源加载；物化正确、不抛
		{
			var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			var ok = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(ok, "corrupt IR should silently fall back\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
			var uncalled = Find(h.LabelDic, "UNCALLED");
			ErbLoader.MaterializeOnRuntime(uncalled);
			Assert.False(uncalled.IsStub);
			Assert.NotEqual("", SummarizeChain(uncalled));
		}
	}

	/// <summary>IR 后背物化回归：私有参数/默认参/GOTO/预处理块在 blob 后背下不误报（沿用 Issue 05/06 语料）。</summary>
	[Fact]
	public async Task Ir_second_launch_resolves_private_arg_default_and_goto_without_false_warning()
	{
		const string corpus = """
@EVENTFIRST
	CALL HASGOTO(0)
	RETURN

@HASGOTO,CID
#DIM CID
	IF CID == 0
		GOTO HOGE
	ENDIF
	RETURN
$HOGE
	PRINTFORML hoge={CID}
	RETURN
""";
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		h.WriteErb("IRPRIV.ERB", corpus);
		// 真实 scan-line 接线（级联 BlobLineSource 私有变量解析依赖 scaningLine）
		var loader1 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok1 = await loader1.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok1, "round1 failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		var g1 = Find(h.LabelDic, "HASGOTO");
		ErbLoader.MaterializeOnRuntime(g1);
		Assert.False(g1.IsStub);

		// 第二次命中 IR：blob 后背重建私有变量 + goto，零误报
		var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok2 = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok2, "round2 failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		var g2 = Find(h.LabelDic, "HASGOTO");
		Assert.False(g2.IsStub, "HASGOTO should be materialized via blob in round2");
		Assert.False(g2.IsError, "HASGOTO must not be error via blob backend\n" + string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		Assert.Equal(1, g2.Arg?.Length ?? -1);

		var falsePositives = h.Console.DisplayLineList
			.Select(l => l?.ToString() ?? "")
			.Where(s => s.Contains("この関数中では定義されていません") || s.Contains("存在しません")).ToList();
		Assert.Empty(falsePositives);
	}

	/// <summary>
	/// IR 后背物化回归：式中调用 #FUNCTION 方法不被误判「未被标记 #FUNCTION」。
	/// IR 桩签名延后（IsMethod 未预建），式中解析（GetFunctionMethod）须先补算签名——否则对 CHECK_CHARA/IS_NAMED
	/// 等 #FUNCTION 方法报 UsedNonMethodFunc（用户实机 D:\era\TW4.881shoess 二次打开大量「行内函数形式调用」警告）。
	/// </summary>
	[Fact]
	public async Task Ir_second_launch_resolves_inline_hash_function_method_without_false_warning()
	{
		const string corpus = """
@EVENTFIRST
	SIF TWICE(2) == 4
		RETURN
	RETURN

@TWICE,N
#FUNCTION
	RESULT = N * 2
	RETURNF
""";
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		h.WriteErb("IRMETHOD.ERB", corpus);

		// 首次（建缓存）
		var loader1 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		await loader1.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		// 第二次（IR 命中，blob 后背）
		var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "round2 failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var twice = Find(h.LabelDic, "TWICE");
		Assert.NotNull(twice);
		// 式中调用只触发签名物化（IsMethod/Arg 就位），函数体可保持桩——关键回归是 IsMethod 不被误判为 false。
		Assert.True(twice.IsMethod, "TWICE must keep #FUNCTION IsMethod flag across IR materialization (signature resolved)");

		var falsePositives = h.Console.DisplayLineList
			.Select(l => l?.ToString() ?? "")
			.Where(s => s.Contains("行内函数形式调用") || s.Contains("#FUNCTION")).ToList();
		Assert.True(falsePositives.Count == 0, "falsePositives:\n" + string.Join("\n", falsePositives));
	}

	/// <summary>
	/// IR 后背物化回归：函数体引用自身 #DIM 私有变量 + ARG 不误报（scaningLine→ParentLabelLine→GetPrivateVariable 上下文）。
	/// 用户实机 TW4.881shoess 二次打开大量「变量 X 未在此函数中定义」「不存在执行中的函数，无法获取/更改 ARG」。
	/// </summary>
	[Fact]
	public async Task Ir_second_launch_resolves_private_dim_plus_arg_without_false_warning()
	{
		const string corpus = """
@EVENTFIRST
	CALL HAS_PRIV(3)
	RETURN

@HAS_PRIV,C
#DIM LOCAL_VALUE
#DIMS LOCAL_NAME
	LOCAL_VALUE = ARG
	IF LOCAL_VALUE:C == 0
		LOCAL_NAME:C = "x"
	ENDIF
	RETURN
""";
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		h.WriteErb("IRPRIV2.ERB", corpus);
		var loader1 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		await loader1.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		h.Console.DisplayLineList.Clear(); // 只测第二轮
		var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "round2 failed\n" + string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var hp = Find(h.LabelDic, "HAS_PRIV");
		Assert.NotNull(hp);
		Assert.False(hp.IsStub, "HAS_PRIV should be materialized via blob in round2. stub=" + hp.IsStub + " err=" + hp.IsError + " depth=" + hp.Depth + "\n" + string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var falsePositives = h.Console.DisplayLineList
			.Select(l => l?.ToString() ?? "")
			.Where(s => s.Contains("未在此函数中定义") || s.Contains("不存在执行中的函数") || s.Contains("定义されていません")).ToList();
		Assert.True(falsePositives.Count == 0, "falsePositives:\n" + string.Join("\n", falsePositives));
	}

	/// <summary>
	/// IR 后背物化回归（运行时接缝）：第二轮加载后未被调用的事件函数保持桩，运行时首调（MaterializeOnRuntime）
	/// 经 blob 后背物化，其 #DIM REF 私有变量 + ARG 必须可解析——对应实机 TW4.881shoess 游玩时
	/// 「变量 X 未在此函数中定义 / 不存在执行中的函数无法获取 ARG」警告。
	/// </summary>
	[Fact]
	public async Task Ir_runtime_materialization_resolves_dim_ref_plus_arg_cleanly()
	{
		const string corpus = """
@EVENTFIRST
	RETURN

@DAILY_EVENT,C
#DIM REF TARGET_ARR,100
#DIMS LABEL_NAME
	IF TARGET_ARR:ARG == 0
		LABEL_NAME:ARG = "x"
	ENDIF
	RETURN
""";
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		h.WriteErb("IRDT.ERB", corpus);
		var loader1 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		await loader1.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		h.Console.DisplayLineList.Clear();
		var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "round2 failed\n" + string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var ev = Find(h.LabelDic, "DAILY_EVENT");
		Assert.True(ev.IsStub, "DAILY_EVENT must stay stub (uncalled during load)");
		// 运行时首调经 blob 后背物化
		ErbLoader.MaterializeOnRuntime(ev);
		Assert.False(ev.IsStub, "DAILY_EVENT must materialize via blob at runtime");
		Assert.False(ev.IsError, "DAILY_EVENT must not degrade: err=" + ev.ErrMes);

		var falsePositives = h.Console.DisplayLineList
			.Select(l => l?.ToString() ?? "")
			.Where(s => s.Contains("未在此函数中定义") || s.Contains("不存在执行中的函数") || s.Contains("定义されていません") || s.Contains("既に使われ")).ToList();
		Assert.True(falsePositives.Count == 0, "falsePositives:\n" + string.Join("\n", falsePositives));
	}

	/// <summary>IR blob 后背运行时物化：形参直接命名 ARG/ARG:1 的函数（实机 @EVENT_COUNTER(ARG, ARG:1) 等 Lv2 警告根因候选）。</summary>
	[Fact]
	public async Task Ir_runtime_materialization_arg_named_params_clean()
	{
		const string corpus = """
@EVENTFIRST
	RETURN

@EVENT_COUNTER(ARG, ARG:1)
	SIF ARG:1 == 1
		RETURN 0
	RETURN 0
""";
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h);
		await h.PreloadAsync();
		h.WriteErb("IRARG.ERB", corpus);
		var loader1 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		await loader1.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		h.Console.DisplayLineList.Clear();
		var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "round2 failed\n" + string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var ev = Find(h.LabelDic, "EVENT_COUNTER");
		if (ev != null && ev.IsStub)
		{
			ErbLoader.MaterializeOnRuntime(ev);
			Assert.False(ev.IsStub, "EVENT_COUNTER must materialize via blob: err=" + ev.ErrMes);
			Assert.False(ev.IsError);
		}
		var falsePositives = h.Console.DisplayLineList
			.Select(l => l?.ToString() ?? "")
			.Where(s => s.Contains("不存在执行中的函数") || s.Contains("未在此函数中定义") || s.Contains("定义されていません")).ToList();
		Assert.True(falsePositives.Count == 0, "falsePositives:\n" + string.Join("\n", falsePositives));
	}
}