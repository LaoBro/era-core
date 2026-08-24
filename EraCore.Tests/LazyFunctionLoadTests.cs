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
/// lazy-function-load（B 线 B1，Issue 03）单测：
/// 惰性模式下 labelDic 只注册全量标签桩（IsStub）、未调用函数保持桩不物化、
/// 被调用（事件根）经主循环物化且行链/帧尺寸与 eager 等价、物化幂等、
/// 回读失败三态降级为占位报错不崩溃、AnalysisMode 强制 eager。
/// 注：深度 BFS 的 setJumpTo 经 GlobalStatic.LabelDictionary 解析子层 Depth，故测试须载入 h.LabelDic。
/// </summary>
[Collection("LoaderTests")]
public class LazyFunctionLoadTests
{
	// 语料：EVENTFIRST（事件根，Depth=0）→ CALL CALLED；UNCALLED 无人调用。
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

	/// <summary>惰性加载后：事件根与调用目标被物化；未调用函数保持桩（IsStub）。</summary>
	[Fact]
	public async Task Lazy_stubs_uncalled_but_materializes_called_via_main_loop()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		await h.PreloadAsync();
		h.WriteErb("LAZY.ERB", Corpus);

		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		var labelDic = h.LabelDic;
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);
		Assert.True(ok, "lazy LoadErbDir failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var root = Find(labelDic, "EVENTFIRST");
		var called = Find(labelDic, "CALLED");
		var uncalled = Find(labelDic, "UNCALLED");
		Assert.NotNull(root);
		Assert.NotNull(called);
		Assert.NotNull(uncalled);

		// 被调用集合（事件根 + CALLF 目标）经主循环物化，不再桩。
		Assert.False(root.IsStub, "EVENTFIRST should be materialized (root/event)");
		Assert.False(called.IsStub, "CALLED should be materialized (transitive call)");
		// 未调用函数保持桩（sweep 跳过解析体）。
		Assert.True(uncalled.IsStub, "UNCALLED should remain a stub in lazy mode");

		// 已物化函数行链非空（非桩必须挂真实 body）。
		Assert.NotEqual("", SummarizeChain(called));

		// 帧尺寸：物化后 LocalLength/LocalsLength 与全量加载口径一致（#LOCALSIZE）。
		Assert.Equal(7, called.LocalLength);
		Assert.Equal(3, called.LocalsLength);
	}

	/// <summary>惰性下物化产物与 eager 全量加载对该函数逐行等价 + 帧尺寸一致。用两套 harness 对照。</summary>
	[Fact]
	public async Task Lazy_materialized_chain_matches_eager_full_load()
	{
		string? lazyChain = null;
		int lazyLocal = -1, lazyLocals = -1;
		{
			using var h = new LoaderTestHarness();
			await h.InitializeAsync();
			ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
			ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
			ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
			await h.PreloadAsync();
			h.WriteErb("LAZY2.ERB", Corpus);
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			var called = Find(h.LabelDic, "CALLED");
			Assert.False(called.IsStub);
			lazyChain = SummarizeChain(called);
			lazyLocal = called.LocalLength;
			lazyLocals = called.LocalsLength;
		}
		// eager 对照（harness 默认 eager；本测试 lazy 块上方已显式 SetValue(true)，需独立 harness 退出 eager 上下文）
		string? eagerChain = null;
		int eagerLocal = -1, eagerLocals = -1;
		{
			using var h = new LoaderTestHarness();
			await h.InitializeAsync();
			await h.PreloadAsync();
			h.WriteErb("LAZY2B.ERB", Corpus);
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			var called = Find(h.LabelDic, "CALLED");
			eagerChain = SummarizeChain(called);
			eagerLocal = called.LocalLength;
			eagerLocals = called.LocalsLength;
		}
		Assert.Equal(eagerChain, lazyChain);
		Assert.Equal(eagerLocal, lazyLocal);
		Assert.Equal(eagerLocals, lazyLocals);
	}

	/// <summary>物化幂等：已物化标签再次触发物化即 no-op，行链与状态不变。</summary>
	[Fact]
	public async Task Lazy_materialization_is_idempotent()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		await h.PreloadAsync();
		h.WriteErb("LAZY3.ERB", Corpus);
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		var called = Find(h.LabelDic, "CALLED");
		Assert.False(called.IsStub);
		var before = SummarizeChain(called);
		// 重复物化
		ErbLoader.MaterializeOnRuntime(called);
		Assert.False(called.IsStub);
		Assert.Equal(before, SummarizeChain(called));

		// 桩触发一次物化后即转非桩（幂等成立）。
		var uncalled = Find(h.LabelDic, "UNCALLED");
		Assert.True(uncalled.IsStub);
		ErbLoader.MaterializeOnRuntime(uncalled);
		Assert.False(uncalled.IsStub);
		ErbLoader.MaterializeOnRuntime(uncalled);
		Assert.Equal(SummarizeChain(uncalled), SummarizeChain(uncalled));
	}

	/// <summary>回读失败三态：源文件被删除 → 物化降级为占位报错（IsError 置位），不抛新异常。</summary>
	[Fact]
	public async Task Lazy_materialization_missing_source_degrades_without_throwing()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		var path = h.WriteErb("LAZY4.ERB", Corpus);
		await h.PreloadAsync();
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		var uncalled = Find(h.LabelDic, "UNCALLED");
		Assert.True(uncalled.IsStub);

		// load 结束后 Preload.ClearCachedLines 已清缓存；删除源文件 → 回读失败三态。
		File.Delete(path);
		ErbLoader.MaterializeOnRuntime(uncalled); // 不应抛
		Assert.True(uncalled.IsError, "source-missing materialization should set placeholder error");
		Assert.False(uncalled.IsStub, "failed materialization clears stub to stop retry");

		// 清理 Preload 缓存，避免污染后续测试。
		loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		await Task.CompletedTask;
	}

	/// <summary>运行时静态派发接缝：CalledFunction.CallFunction 命中未调用桩 → 就地物化 → 返回可调用标签（幂等）。</summary>
	[Fact]
	public async Task Lazy_runtime_callfunction_materializes_stub_via_seam()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		await h.PreloadAsync();
		h.WriteErb("LAZY6.ERB", Corpus);
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);

		var uncalled = Find(h.LabelDic, "UNCALLED");
		Assert.True(uncalled.IsStub, "UNCALLED must start as a stub");

		// 模拟运行期首调用：动态/静态派发到未调用目标 → 走 CalledFunction 物化接缝。
		var call = CalledFunction.CallFunction(h.LabelDic, "UNCALLED", null!);
		Assert.NotNull(call);
		Assert.NotNull(call!.TopLabel);
		// 物化后桩转非桩，行链已挂。
		Assert.False(uncalled.IsStub, "UNCALLED must be materialized after CallFunction seam");
		Assert.NotEqual("", SummarizeChain(uncalled));

		// 幂等：二次调用直接命中，不重复重解析（行链不变）。
		var before = SummarizeChain(uncalled);
		var call2 = CalledFunction.CallFunction(h.LabelDic, "UNCALLED", null!);
		Assert.NotNull(call2);
		Assert.Equal(before, SummarizeChain(uncalled));
	}

	/// <summary>AnalysisMode 一律强制 eager：即便开了 LazyFunctionLoad 也不产生桩。</summary>
	[Fact]
	public async Task Lazy_forced_eager_in_analysis_mode()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		await h.PreloadAsync();
		h.WriteErb("LAZY5.ERB", Corpus);

		var analysisEnv = new LoaderEnv(h.CsvDir, h.ErbDir, analysisMode: true,
			analysisFiles: new List<string>(), debugMode: false) { DirAccessor = h.Env.DirAccessor };
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, analysisEnv, _ => { });
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: true, h.LabelDic);
		Assert.True(ok);
		// 所有标签均应全量物化（无桩）。
		foreach (var name in new[] { "EVENTFIRST", "CALLED", "UNCALLED" })
			Assert.False(Find(h.LabelDic, name).IsStub, name + " must not be a stub in AnalysisMode");
	}

	/// <summary>标出事件/非事件标签（事件可能只存在于 eventLabelDic）。</summary>
	private static FunctionLabelLine Find(LabelDictionary labelDic, string name)
	{
		var ne = labelDic.GetNonEventLabel(name);
		if (ne != null)
			return ne;
		var ev = labelDic.GetEventLabels(name);
		if (ev != null)
		{
			foreach (var lst in ev)
			{
				if (lst == null)
					continue;
				foreach (var l in lst)
				{
					if (l != null)
						return l;
				}
			}
		}
		return null!;
	}

	/// <summary>
	/// 物化期 scan-line 上下文回归（Issue 05）：`@FUNC,CID` + `#DIM CID` 是合法写法——@ 参数就是本函数
	/// 私有变量。eager 经 setLabelsArg 的 setScanLine(label) 提供上下文；惰性物化（ReparseFunction）此前缺这步、
	/// 且新建标签不自指 ParentLabelLine，导致 GetVariableToken 够不到本函数私有变量 → 落入永不清理的
	/// privateDimList → 误报「この関数中では定義されていません」。修复后 eager/lazy 都必须零误报。
	/// 注：本测试必须用真实 scan-line 接线（line =&gt; Process.scaningLine = line），`_ =&gt; {}` 空接线会重现 bug。
	/// </summary>
	[Fact]
	public async Task Lazy_materialization_resolves_private_arg_without_false_warning()
	{
		const string argCorpus = """
@EVENTFIRST
	CALL PRINT_FACE(0)
	RETURN

@PRINT_FACE,CID
#DIM CID
	PRINTFORML face={CID}
	RETURN
""";
		foreach (bool lazy in new[] { false, true })
		{
			using var h = new LoaderTestHarness();
			await h.InitializeAsync();
			if (lazy)
			{
				ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
			}
			await h.PreloadAsync();
			h.WriteErb("LAZYARG.ERB", argCorpus);
			// 真实引擎接线（Process.cs 同款）；空接线会重现物化期误报。
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
			var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(ok, "LoadErbDir failed (lazy=" + lazy + ")\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

			var pf = Find(h.LabelDic, "PRINT_FACE");
			Assert.NotNull(pf);
			Assert.False(pf.IsError, "PRINT_FACE must not be flagged as error (lazy=" + lazy + ")");
			Assert.False(pf.IsStub, "PRINT_FACE should be materialized (called from EVENTFIRST) (lazy=" + lazy + ")");
			Assert.Equal(1, pf.Arg?.Length ?? -1);

			var falsePositives = h.Console.DisplayLineList
				.Select(l => l?.ToString() ?? "")
				.Where(s => s.Contains("この関数中では定義されていません"))
				.ToList();
			Assert.Empty(falsePositives);
		}
	}

	/// <summary>
	/// 物化期 $ 标签注册回归（Issue 05b）：惰性下桩跳过函数体（含 $ 标签），加载期不注册；物化
	/// （ReparseFunction）解析出 $ 标签但此前未补注册 labelDic → GOTO 的 SetJumpTo 经 GetLabelDollar
	/// 解析失败 → 误报「指定されたラベル名"$X"は現在の関数内に存在しません」。修复后 eager/lazy 零误报。
	/// </summary>
	[Fact]
	public async Task Lazy_materialization_registers_dollar_labels_without_false_warning()
	{
		const string gotoCorpus = """
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
	PRINTFORML hoge
	RETURN
""";
		foreach (bool lazy in new[] { false, true })
		{
			using var h = new LoaderTestHarness();
			await h.InitializeAsync();
			if (lazy)
			{
				ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
			}
			await h.PreloadAsync();
			h.WriteErb("LAZYGOTO.ERB", gotoCorpus);
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
			var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(ok, "LoadErbDir failed (lazy=" + lazy + ")\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

			var hasgoto = Find(h.LabelDic, "HASGOTO");
			Assert.NotNull(hasgoto);
			Assert.False(hasgoto.IsError, "HASGOTO must not be flagged as error (lazy=" + lazy + ")");
			Assert.False(hasgoto.IsStub, "HASGOTO should be materialized (called from EVENTFIRST) (lazy=" + lazy + ")");

			var falsePositives = h.Console.DisplayLineList
				.Select(l => l?.ToString() ?? "")
				.Where(s => s.Contains("存在しません"))
				.ToList();
			Assert.Empty(falsePositives);
		}
	}

	/// <summary>
	/// 物化期 [ 预处理回归（Issue 05c）：eraTW 惯用 [SKIPSTART]...[SKIPEND] 包「故意不平衡」的嵌套
	/// （IF 无 ENDIF / SELECTCASE 无 ENDSELECT），由预处理禁用使其不被解析。惰性下桩跳过函数体，
	/// 物化（ReparseFunction）此前不处理 [ 指令 → 块内容被误解析 → 嵌套错乱误报（IF/ENDIF/SELECTCASE/ENDSELECT 错配）。
	/// 修复后 eager/lazy 零误报。
	/// </summary>
	[Fact]
	public async Task Lazy_materialization_processes_preprocessor_blocks_without_false_warning()
	{
		const string skipCorpus = """
@EVENTFIRST
	CALL SKIPFUNC(0)
	RETURN

@SKIPFUNC,CID
#DIM CID
	PRINTFORML before
	[SKIPSTART]
	IF CID == 1
		SELECTCASE CID
		CASE 1
			PRINTFORML one
	ENDIF
	[SKIPEND]
	PRINTFORML after
	RETURN
""";
		foreach (bool lazy in new[] { false, true })
		{
			using var h = new LoaderTestHarness();
			await h.InitializeAsync();
			if (lazy)
			{
				ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
			}
			await h.PreloadAsync();
			h.WriteErb("LAZYSKIP.ERB", skipCorpus);
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
			var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(ok, "LoadErbDir failed (lazy=" + lazy + ")\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

			var skipfunc = Find(h.LabelDic, "SKIPFUNC");
			Assert.NotNull(skipfunc);
			Assert.False(skipfunc.IsError, "SKIPFUNC must not be flagged as error (lazy=" + lazy + ")");
			Assert.False(skipfunc.IsStub, "SKIPFUNC should be materialized (called from EVENTFIRST) (lazy=" + lazy + ")");

			var falsePositives = h.Console.DisplayLineList
				.Select(l => l?.ToString() ?? "")
				.Where(s => s.Contains("対応する") || s.Contains("分岐の外"))
				.ToList();
			Assert.Empty(falsePositives);
		}
	}

	/// <summary>
	/// 物化 body 与桩私有变量一致性回归（Issue 06，重构后）：ReparseFunction 直接以桩为解析目标，
	/// body 各行的 ParentLabelLine 必须指向桩自身、且绑定桩的私有变量 token——否则运行期参数赋值（写桩
	/// 私有变量）与 body 读取不一致：ReduceArgumentOnLoad=NO 时经 ParentLabelLine 运行期解析读到空值，
	/// YES 时 FORML 等参数解析期即绑定错 token（TK1.29.3 的 @SINGLE_DRAWLINE(LINE="-") 默认参 → DRAWLINE
	/// 空串崩溃；RESETDATA → SetDefaultLocalValue NRE 同根）。
	/// </summary>
	[Fact]
	public async Task Lazy_materialization_reparents_body_chain_to_stub()
	{
		const string defCorpus = """
@EVENTFIRST
	CALL SINGLE_DRAWLINE
	RETURN

@SINGLE_DRAWLINE(LINE = "-", COLOR = -1)
#DIM COLOR
#DIMS LINE
	DRAWLINEFORM %LINE%
	RETURN
""";
		foreach (bool lazy in new[] { false, true })
		{
			using var h = new LoaderTestHarness();
			await h.InitializeAsync();
			if (lazy)
			{
				ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
				ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
			}
			await h.PreloadAsync();
			h.WriteErb("LAZYDF.ERB", defCorpus);
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
			var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			Assert.True(ok, "LoadErbDir failed (lazy=" + lazy + ")\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

			var sd = h.LabelDic.GetNonEventLabel("SINGLE_DRAWLINE");
			Assert.NotNull(sd);
			Assert.False(sd.IsStub, "SINGLE_DRAWLINE should be materialized (called from EVENTFIRST) (lazy=" + lazy + ")");
			Assert.False(sd.IsError);
			// 默认参必须保留（@FUNC(LINE = "-") 的 "-" 与 -1）
			Assert.Equal(2, sd.Def?.Length ?? -1);
			Assert.Equal("-", sd.Def?[0]?.ToString());
			// 行链每一行必须指向桩自身（运行期私有变量解析与参数赋值同一目标；此前指向瞬态 reparse 标签）
			LogicalLine cur = sd.NextLine;
			int guard = 0;
			while (cur != null && cur is not NullLine && guard++ < 50)
			{
				Assert.Same(sd, cur.ParentLabelLine);
				cur = cur.NextLine;
			}
		}
	}

	/// <summary>
	/// Issue 06 实机回归（TW4.881shoess 初次遊玩崩溃）：运行时物化后 scaningLine 不得残留为陈旧行。
	/// eager 的 ParseFunctionWithCatch 以 finally ScanLine(null!) 收尾复位；物化路径（ReparseFunction）
	/// 此前漏掉复位——nestCheck/setJumpTo 的三遍扫描会把 GlobalStatic.Process.scaningLine 留在最后物化
	/// 函数体末尾（尾部 NullLine 或末行）。其后运行期再经 GetScaningLine 解析本函数私有变量时，scaningLine
	/// 优先于 state.ErrorLine 被读到 → 指向错误函数/无 ParentLabelLine → 私有变量查找被跳过 → 落入
	/// privateDimList → 误报「この関数中では定義されていません」（真实例：@SET_EXIST 的
	/// RETURNF OBJ_EXIST("SET", CLASS_NAME, O_ID) 在 @SETLIST_EXIST 先物化 EXIST_角色データ1 后崩溃）。
	/// 注：必须用真实 scan-line 接线（line =&gt; h.Process.scaningLine = line），空接线无法复现。
	/// </summary>
	[Fact]
	public async Task Lazy_runtime_materialization_does_not_poison_scaningline_for_private_resolution()
	{
		const string corpus = """
@EVENTFIRST
	CALL CALLED(0)
	RETURN

@CALLED,CID
#DIM CID
	PRINTFORML face={CID}
	RETURN

@UNCALLED
	PRINTL uncalled
	RETURN
""";
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		await h.PreloadAsync();
		h.WriteErb("LAZYSCAN.ERB", corpus);
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "LoadErbDir failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		// 加载收尾 scaningLine 已复位（对齐 eager）。
		Assert.Null(h.Process.scaningLine);

		var called = Find(h.LabelDic, "CALLED");
		var uncalled = Find(h.LabelDic, "UNCALLED");
		Assert.False(called.IsStub, "CALLED should be materialized (called from EVENTFIRST)");
		Assert.True(uncalled.IsStub, "UNCALLED should remain a stub in lazy mode");

		// 模拟运行期执行 CALLED 的某行（其私有变量 CID 需可解析）——state.ErrorLine 提供正确上下文。
		LogicalLine execLine = called.NextLine;
		while (execLine is not InstructionLine)
			execLine = execLine.NextLine;
		h.Process.getCurrentState.CurrentLine = execLine;

		// 运行期物化未调用桩（模拟动态 CALLFORM 命中）——此前会把 scaningLine 残留为陈旧行。
		ErbLoader.MaterializeOnRuntime(uncalled);
		Assert.False(uncalled.IsStub);
		Assert.Null(h.Process.scaningLine);

		// 修复前：scaningLine 残留最后物化函数体末行 → GetScaningLine 优先返回它 → 私有变量查找跳过 →
		// CID 落入 privateDimList → 抛「この関数中では定義されていません」；修复后不抛。
		var ex = Record.Exception(() => h.Process.IdentifierDictionary.GetVariableToken("CID", null, true));
		Assert.Null(ex);
	}
}