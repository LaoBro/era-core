using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0012 / Issue 02：ErbLoader 隔离单测。
/// 直接以显式依赖（console / exm / idDic / LoaderEnv / Action&lt;LogicalLine&gt; sink）构造，不持 Process 反向引用（F2）。
/// 下游 parser 经 ADR-0011 兼容层读 GlobalStatic——由 harness 的 Initialize 预热满足，非 ErbLoader 自身耦合。
/// 只验证可观察行为：标签装载、scaningLine sink 写入、返回值、空目录。
/// </summary>
[Collection("LoaderTests")]
public class ErbLoaderTests
{
	[Theory]
	[InlineData(true, false, false, true)]
	[InlineData(false, false, false, false)]
	[InlineData(true, true, false, false)]
	[InlineData(true, false, true, false)]
	[InlineData(false, false, true, false)]
	public void ParallelParseScript_policy_disables_sweep_on_android(
		bool configured, bool analysisMode, bool isAndroid, bool expected)
	{
		Assert.Equal(expected, ErbLoader.ShouldRunParallelSweep(configured, analysisMode, isAndroid));
	}

	/// <summary>
	/// 构造 ErbLoader + 一个记录 scaningLine 写入的 spy sink。
	/// sink 同时写入 <c>h.Process.scaningLine</c>：仅 spy.Add 而不回写会让串行路径的
	/// GetLoadScaningLine() 恒为 null、私有变量被误判未定义（ErbLoader.cs:2064 已记录该误报），
	/// 使被测行为偏离真实启动（Process.cs:627 注入的是 `line => process.scaningLine = line`）。
	/// </summary>
	private static ErbLoader NewLoaderWithSpy(LoaderTestHarness h, List<LogicalLine?> spy)
	{
		return new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env,
			line => { spy.Add(line); h.Process.scaningLine = line; });
	}

	[Fact]
	public async Task LoadErbDir_registers_event_label_and_returns_true()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("EVENT.ERB", "@EVENTFOO\nRETURN\n");
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		Assert.True(labelDic.Count > 0);
	}

	[Fact]
	public async Task LoadErbDir_scaningLine_sink_receives_the_label_line()
	{
		// ADR-0012 决策二：scaningLine 写经 sink 回调，不再直写 parentProcess.scaningLine。
		// spy 应在解析 @EVENTFOO 时收到该 FunctionLabelLine。
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("EVENT.ERB", "@EVENTFOO\nRETURN\n");
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.Contains(spy, l => l is FunctionLabelLine fl && fl.LabelName == "EVENTFOO");
	}

	[Fact]
	public async Task LoadErbDir_empty_dir_returns_true_with_no_labels()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		Assert.Equal(0, labelDic.Count);
	}

	[Fact]
	public async Task LoadErbDir_multiple_files_all_labels_registered()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@FUNC_A\nRETURN\n");
		h.WriteErb("B.ERB", "@FUNC_B\nRETURN\n");
		var spy = new List<LogicalLine?>();
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, spy);
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		Assert.True(labelDic.Count >= 2);
		Assert.Contains(spy, l => l is FunctionLabelLine fl && fl.LabelName == "FUNC_A");
		Assert.Contains(spy, l => l is FunctionLabelLine fl && fl.LabelName == "FUNC_B");
	}

	/// <summary>
	/// erb-loader-parallel Issue 01 行为锚点：同一文件内警告按行序出现。
	/// $LAB 重複警告（3 行目）必须先于 #ONLY on 非イベント関数警告（5 行目）。
	/// 产物化重构（生成→合并两步）不得改变文件内警告相对顺序。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_warning_order_within_file_follows_line_order()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("ORDER.ERB", "@FOO\n$LAB\n$LAB\nRETURN\n@BAR\n#ONLY\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		var lines = h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
		var dupDollar = lines.FindIndex(s => s.Contains("既に同じ関数内"));
		var onlyOnNonEvent = lines.FindIndex(s => s.Contains("イベント関数以外では#ONLY"));
		Assert.True(dupDollar >= 0, "LabelIsAlreadyDefined warning missing");
		Assert.True(onlyOnNonEvent >= 0, "UsableOnlyEventFunc warning missing");
		Assert.True(dupDollar < onlyOnNonEvent, "warnings within one file must keep line order");
		// 重複 $ 警告恰好一次（合并不得重复检测）
		Assert.Equal(1, lines.Count(s => s.Contains("既に同じ関数内")));
	}

	/// <summary>
	/// erb-loader-parallel Issue 01 行为锚点：跨文件警告按文件加载序出现，
	/// 且重名函数警告（FuncIsAlreadyDefined）在其文件内保持位置。
	/// AnalysisMode 打开重载检查门（env.AnalysisMode 替代 WarnNormalFunctionOverloading）。
	/// </summary>
	[Fact]
	public async Task AnalysisMode_overload_and_cross_file_warning_order_preserved()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@FOO\n@FOO\nRETURN\n");
		h.WriteErb("B.ERB", "@FB\n$LAB\n$LAB\nRETURN\n");
		await h.PreloadAsync();
		var analysisEnv = new LoaderEnv(h.CsvDir, h.ErbDir, analysisMode: true,
			analysisFiles: new List<string>(), debugMode: false) { DirAccessor = new FileSystemGameDirAccessor() };
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, analysisEnv, line => h.Process.scaningLine = line);
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		var lines = h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
		var overload = lines.FindIndex(s => s.Contains("は既に定義"));
		var dupDollar = lines.FindIndex(s => s.Contains("既に同じ関数内"));
		Assert.True(overload >= 0, "FuncIsAlreadyDefined warning missing");
		Assert.True(dupDollar >= 0, "LabelIsAlreadyDefined warning missing");
		Assert.True(overload < dupDollar, "A.ERB warnings must precede B.ERB warnings");
	}

	/// <summary>
	/// erb-loader-parallel Issue 01 行为锚点：#ONLY 跨文件重複宣言只对第二个文件警告一次。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_only_declaration_dup_across_files_warns_once()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@EVENTTRAIN\n#ONLY\nRETURN\n");
		h.WriteErb("B.ERB", "@EVENTTRAIN\n#ONLY\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		var count = h.Console.DisplayLineList.Count(l => l?.ToString()?.Contains("すでに#ONLYが宣言されています") == true);
		Assert.Equal(1, count);
	}

	/// <summary>
	/// erb-loader-parallel Issue 01 行为锚点：标签 Index / FileIndex 按文件加载序分配。
	/// 合并阶段按原文件顺序执行全部 LabelDictionary 写入必须复现该分配序。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_label_index_follows_file_order()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@FUNC_A\nRETURN\n");
		h.WriteErb("B.ERB", "@FUNC_B\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		var labels = labelDic.GetAllLabels(false).OrderBy(l => l.Index).ToList();
		Assert.Equal(2, labels.Count);
		Assert.Equal("FUNC_A", labels[0].LabelName);
		Assert.Equal("FUNC_B", labels[1].LabelName);
		Assert.True(labels[0].Index < labels[1].Index);
		Assert.True(labels[0].FileIndex < labels[1].FileIndex);
	}

	/// <summary>
	/// erb-loader-parallel Issue 01 行为锚点：LoadErbList（部分重载入口）同样装载标签并保留跨文件警告序。
	/// </summary>
	[Fact]
	public async Task LoadErbList_loads_labels_and_keeps_warning_order()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var pathA = h.WriteErb("A.ERB", "@FUNC_A\n#ONLY\nRETURN\n");
		var pathB = h.WriteErb("B.ERB", "@FUNC_B\n$LAB\n$LAB\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbList([pathA, pathB], labelDic);

		Assert.True(ok);
		Assert.True(labelDic.Count >= 2);
		var lines = h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
		var onlyOnNonEvent = lines.FindIndex(s => s.Contains("イベント関数以外では#ONLY"));
		var dupDollar = lines.FindIndex(s => s.Contains("既に同じ関数内"));
		Assert.True(onlyOnNonEvent >= 0);
		Assert.True(dupDollar >= 0);
		Assert.True(onlyOnNonEvent < dupDollar);
	}

	/// <summary>
	/// erb-loader-parallel Issue 01 行为锚点：部分重载（LoadErbList，非 AnalysisMode 时
	/// fname 为相对路径、与初载标签的 Position.Filename 一致）同一文件不产生对自身旧标签的
	/// FuncIsAlreadyDefined 假警告——旧路径解析前 IfFileLoadClearLabelWithPath 先清掉同文件名
	/// 旧标签；产物化后该清理在合并阶段，生成期检查必须排除（excludingFilename）。
	/// </summary>
	[Fact]
	public async Task LoadErbList_reload_same_file_no_spurious_overload_warning()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var path = h.WriteErb("RELOAD.ERB", "@FOO\nRETURN\n");
		await h.PreloadAsync();
		// 打开重载检查门（非 AnalysisMode 路径走 WarnNormalFunctionOverloading 配置）
		ConfigData.Current!.GetConfigItem(ConfigCode.WarnNormalFunctionOverloading).SetValue(true);
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		var before = h.Console.DisplayLineCount;
		var ok = await loader.LoadErbList([path], labelDic);

		Assert.True(ok);
		Assert.True(labelDic.Count >= 1);
		var spurious = h.Console.DisplayLineList
			.Skip(before)
			.Count(l => l?.ToString()?.Contains("は既に定義") == true);
		Assert.Equal(0, spurious);
	}

	/// <summary>
	/// 决策七测试：AnalysisMode 下对同一未调用函数标签不应重复调用 ParseFunctionWithCatch。
	/// spec Testing Decisions L378：AnalysisMode 下断言 warningList 无重复条目。
	/// spec Success Criteria L411：Assert.True(dupCount == 0)。
	/// 未调用函数（Depth == -1）含 BREAK（循环外非法）触发 nestCheck 警告，
	/// 修复前 AnalysisMode 会调用两次 ParseFunctionWithCatch → 警告重复；修复后仅一次。
	/// </summary>
	[Fact]
	public async Task AnalysisMode_no_duplicate_warnings_for_uncalled_function()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// UNCALLED_FUNC 从未被 CALL → Depth == -1；BREAK 在循环外 → nestCheck 发射 InvalidLoopInstruction 警告
		h.WriteErb("ANALYSIS_TEST.ERB", "@UNCALLED_FUNC\nBREAK\nRETURN\n");
		await h.PreloadAsync();

		// 自定义 AnalysisMode = true 的 LoaderEnv
		var analysisEnv = new LoaderEnv(h.CsvDir, h.ErbDir, analysisMode: true,
			analysisFiles: new List<string>(), debugMode: false) { DirAccessor = new FileSystemGameDirAccessor() };
		var spy = new List<LogicalLine?>();
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, analysisEnv,
			line => { spy.Add(line); h.Process.scaningLine = line; });
		var labelDic = new LabelDictionary();

		var initialLineCount = h.Console.DisplayLineCount;
		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		// 统计 InvalidLoopInstruction 警告本身 — PrintWarning 会回显源行（即 "BREAK" 字面量），
		// 故不能简单 Contains("BREAK")。改用警告签名 "REPEAT, FOR, WHILE, DOの中以外で" 定位真正的警告行。
		// spec L411 要求 dupCount == 0（零重复 = 恰好 1 次警告，而非 0 次空转）。
		// 修复前：2 次（L775 + L804 各调用一次 ParseFunctionWithCatch → nestCheck 发射两次警告）
		// 修复后：1 次（仅 L775 调用，L804 被 !env.AnalysisMode 守卫跳过）
		var breakWarningCount = h.Console.DisplayLineList
			.Skip(initialLineCount)
			.Count(l => l?.ToString()?.Contains("REPEAT, FOR, WHILE, DOの中以外で") == true);

		Assert.Equal(1, breakWarningCount);
	}

	/// <summary>
	/// erb-loader-parallel Issue 02 行为锚点：*#* 优先目录（firstDir）文件在主循环枚举里再次出现时
	/// 须被已加载集合去重（List→HashSet 交换后语义不变）——firstDir 文件只载一次、先于根目录文件，
	/// 不产生对自身的重名函数假警告。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_firstdir_file_deduped_from_main_enumeration()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.SearchSubdirectory).SetValue(true);
		Directory.CreateDirectory(Path.Combine(h.ErbDir, "1#PRI"));
		File.WriteAllText(Path.Combine(h.ErbDir, "1#PRI", "SUB.ERB"), "@FUNC_SUB\nRETURN\n");
		h.WriteErb("MAIN.ERB", "@FUNC_MAIN\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		var labels = labelDic.GetAllLabels(false).OrderBy(l => l.Index).ToList();
		Assert.Equal(2, labels.Count);
		// firstDir 优先：SUB 先于 MAIN 分配 Index
		Assert.Equal("FUNC_SUB", labels[0].LabelName);
		Assert.Equal("FUNC_MAIN", labels[1].LabelName);
		// 去重失效会双重装载 FUNC_SUB → 主循环重名警告或标签数 > 2
		Assert.Equal(1, labels.Count(l => l.LabelName == "FUNC_SUB"));
	}

	// ===== erb-loader-parallel Issue 03：并行解析 + 按序合并 =====
	// 默认配置 ParallelErbLoad=true，上方既有锚点测试已直接覆盖并行路径；
	// 以下测试针对并行化的独有风险面：跨文件检查占位槽、displayReport 按序补发、串行 fallback 等价。

	/// <summary>
	/// 03 行为锚点：并行路径下跨文件重名检查经合并期补检（占位槽）——警告仍发射、
	/// 行序位置正确（本文件更早行的警告在前）、senior 引用跨文件最早标签（优先于文件内首现）。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_parallel_cross_file_overload_keeps_order_and_senior()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.WarnNormalFunctionOverloading).SetValue(true);
		// B 的 #ONLY 警告（2 行目）必须先于 B 的 @FOO 跨文件重名警告（4 行目）
		h.WriteErb("A.ERB", "@FOO\nRETURN\n");
		h.WriteErb("B.ERB", "@BAR\n#ONLY\nRETURN\n@FOO\nRETURN\n");
		// C 文件内重名（InFileSenior 路径）+ D 跨文件重名（senior 引用 C 的首现）
		h.WriteErb("C.ERB", "@DUP\n@DUP\nRETURN\n");
		h.WriteErb("D.ERB", "@DUP\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		var lines = h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
		var onlyOnNonEvent = lines.FindIndex(s => s.Contains("イベント関数以外では#ONLY"));
		var overloadFoo = lines.FindIndex(s => s.Contains("FOO") && s.Contains("は既に定義"));
		Assert.True(onlyOnNonEvent >= 0, "UsableOnlyEventFunc warning missing");
		Assert.True(overloadFoo >= 0, "cross-file FuncIsAlreadyDefined warning missing (placeholder not resolved)");
		Assert.True(onlyOnNonEvent < overloadFoo, "overload warning must sit at its line position (after #ONLY line)");
		Assert.Contains("A.ERB", lines[overloadFoo]);
		var overloadDup = lines.Where(s => s.Contains("DUP") && s.Contains("は既に定義")).ToList();
		Assert.Equal(2, overloadDup.Count); // C 内 1 处 + D 跨文件 1 处
		Assert.Contains("C.ERB", overloadDup[0]);
		Assert.Contains("C.ERB", overloadDup[1]);
	}

	/// <summary>
	/// 03 行为锚点：并行路径下跨文件 #ONLY 重複宣言（AlreadyDeclaredOnly）仍只对第二个文件警告一次。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_parallel_cross_file_only_dup_warns_once()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@EVENTTRAIN\n#ONLY\nRETURN\n");
		h.WriteErb("B.ERB", "@EVENTTRAIN\n#ONLY\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);

		Assert.True(ok);
		var count = h.Console.DisplayLineList.Count(l => l?.ToString()?.Contains("すでに#ONLYが宣言されています") == true);
		Assert.Equal(1, count);
	}

	/// <summary>
	/// 03 等价性（spec 接缝 2 单测版）：同一游戏分别以并行 / 串行 fallback 加载，
	/// 标签签名（名:Index:FileIndex 序列）与警告输出（逐字）完全一致。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_serial_fallback_equivalent_to_parallel()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.WarnNormalFunctionOverloading).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.SearchSubdirectory).SetValue(true);
		Directory.CreateDirectory(Path.Combine(h.ErbDir, "1#PRI"));
		File.WriteAllText(Path.Combine(h.ErbDir, "1#PRI", "SUB.ERB"), "@FOO\nRETURN\n");
		// 覆盖面：firstDir 优先序 / 跨文件重名 / 文件内重名 / #ONLY 跨文件重複 / $ 重複 / 标签引数
		h.WriteErb("A.ERB", "@FOO\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n@SYS\nRETURN\n");
		h.WriteErb("B.ERB", "@FOO\n$LAB\n$LAB\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n@BFOO(X, Y)\nRETURN\n@BFOO\nRETURN\n");
		await h.PreloadAsync();

		var beforeParallel = h.Console.DisplayLineCount;
		var labelDicParallel = new LabelDictionary();
		await NewLoaderWithSpy(h, new List<LogicalLine?>()).LoadErbDir(h.ErbDir, displayReport: false, labelDicParallel);
		// DEBUG 构建有阶段性 ElapsedTime 计时行（毫秒数值天然不稳定），比对前过滤
		var parallelLines = h.Console.DisplayLineList.Skip(beforeParallel)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		var before = h.Console.DisplayLineCount;
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		var labelDicSerial = new LabelDictionary();
		await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
			.LoadErbDir(h.ErbDir, displayReport: false, labelDicSerial);
		var serialLines = h.Console.DisplayLineList.Skip(before)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		static List<string> Signature(LabelDictionary dic) =>
			dic.GetAllLabels(false).OrderBy(l => l.Index)
				.Select(l => $"{l.LabelName}:{l.Index}:{l.FileIndex}").ToList();
		Assert.Equal(Signature(labelDicSerial), Signature(labelDicParallel));
		Assert.Equal(serialLines, parallelLines);
	}

	/// <summary>
	/// 03 行为锚点：displayReport 逐文件进度行在并行模式下按文件序补发（PR 决策：按序补发而非聚合行），
	/// 跨窗口（文件数 > 并行度上限 8）不乱序。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_parallel_progress_lines_follow_file_order()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		for (int i = 0; i < 10; i++)
			h.WriteErb($"F{i:D2}.ERB", $"@FUNC_{i}\nRETURN\n");
		await h.PreloadAsync();
		var loader = NewLoaderWithSpy(h, new List<LogicalLine?>());
		var labelDic = new LabelDictionary();

		await loader.LoadErbDir(h.ErbDir, displayReport: true, labelDic);

		var lines = h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
		var idx = Enumerable.Range(0, 10).Select(i => lines.FindIndex(s => s.Contains($"F{i:D2}.ERB"))).ToArray();
		Assert.All(idx, v => Assert.True(v >= 0, "progress line missing for some file"));
		for (int i = 1; i < idx.Length; i++)
			Assert.True(idx[i - 1] < idx[i], $"progress lines out of order at F{i:D2}");
	}

	// ===== erb-loader-parallel Issue 08：ParseScript 未调用遍历并行化（else / callform 两分支同票同套）=====

	/// <summary>else 分支等价性语料：未调用函数真解析（IgnoreUncalledFunction:NO）+ FNC ONCE + FNF ONCE + nestCheck 警告。
	/// 注意 harness 语义：CALL 解析查 GlobalStatic.LabelDictionary（与传入 labelDic 分离），语料不可 CALL 已定义函数。</summary>
	private static void WriteUncalledSweepCorpus(LoaderTestHarness h)
	{
		h.WriteErb("A.ERB", "@EVENTTRAIN\nRETURN\n"
			+ "@UNCALLED_A\nBREAK\nCALL NOEXIST_FUNC\nRETURN\n");
		// B 文件内两个 FNF：首个发射、同文件第二个被 ONCE 去重（ignoredFNFWarningCount 路径）
		h.WriteErb("B.ERB", "@UNCALLED_B\nCALL NOEXIST_FUNC_B\nCALL NOEXIST_FUNC_B2\nBREAK\nRETURN\n");
	}

	/// <summary>
	/// 08 等价性（else 分支）：ParallelParseScript 并行（默认）与串行 fallback 输出逐字一致。
	/// 覆盖 D5 处置：#1 警告 per-label 缓冲回放、#2 FNF 事件缓冲 + 回放段 ONCE 决策重走
	///（ignoredFNFWarningFiles 按文件先到先得）、#4 FNC ONCE（ignoredFNCWarningFiles）、
	/// #5 nestCheck 集合局部化（BREAK 循环外警告）、#6 scaningLine 写点切换。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_parsescript_parallel_equivalent_to_serial_uncalled_branch()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		WriteUncalledSweepCorpus(h);
		// 未调用函数真解析 + FNC/FNF ONCE
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.FunctionNotCalledWarning).SetValue(DisplayWarningFlag.ONCE);
		ConfigData.Current!.GetConfigItem(ConfigCode.FunctionNotFoundWarning).SetValue(DisplayWarningFlag.ONCE);
		await h.PreloadAsync();

		var beforeParallel = h.Console.DisplayLineCount;
		var labelDicParallel = new LabelDictionary();
		await NewLoaderWithSpy(h, new List<LogicalLine?>()).LoadErbDir(h.ErbDir, displayReport: false, labelDicParallel);
		var parallelLines = h.Console.DisplayLineList.Skip(beforeParallel)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		var before = h.Console.DisplayLineCount;
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		var labelDicSerial = new LabelDictionary();
		await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
			.LoadErbDir(h.ErbDir, displayReport: false, labelDicSerial);
		var serialLines = h.Console.DisplayLineList.Skip(before)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		// sanity：语料确实触发了 FNC / FNF / ONCE 去重计数路径（并行路径已完整回放）
		Assert.Contains(parallelLines, s => s.Contains("UNCALLED_A") && s.Contains("一度も呼び出されません"));
		Assert.Contains(parallelLines, s => s.Contains("NOEXIST_FUNC") && s.Contains("存在しません"));
		Assert.Contains(parallelLines, s => s.Contains("REPEAT, FOR, WHILE, DOの中以外で"));
		Assert.Contains(parallelLines, s => s.Contains("呼び出した事に関する警告を")); // IgnoredUndefinedFuncCall（B 文件第二个 FNF 被去重）

		static List<string> Signature(LabelDictionary dic) =>
			dic.GetAllLabels(false).OrderBy(l => l.Index)
				.Select(l => $"{l.LabelName}:{l.Index}:{l.FileIndex}:{l.Depth}").ToList();
		Assert.Equal(Signature(labelDicSerial), Signature(labelDicParallel));
		Assert.Equal(serialLines, parallelLines);
	}

	/// <summary>
	/// 08 等价性（callform 分支）：TRYCALLLIST 使 useCallForm=true → 未调用遍历走 callform 分支并行解析。
	/// eraTW 真机同款分支（spec D4-3 双保险：单测验 else、真机验 callform，此处反向补齐单测面）。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_parsescript_parallel_equivalent_to_serial_callform_branch()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@EVENTTRAIN\nTRYCALLLIST\n\tFUNC FOO\nENDFUNC\nRETURN\n"
			+ "@UNCALLED_A\nBREAK\nCALL NOEXIST_FUNC\nRETURN\n");
		h.WriteErb("B.ERB", "@UNCALLED_B\nCALL NOEXIST_FUNC_B\nCALL NOEXIST_FUNC_B2\nBREAK\nRETURN\n");
		ConfigData.Current!.GetConfigItem(ConfigCode.FunctionNotFoundWarning).SetValue(DisplayWarningFlag.ONCE);
		await h.PreloadAsync();

		var beforeParallel = h.Console.DisplayLineCount;
		var labelDicParallel = new LabelDictionary();
		await NewLoaderWithSpy(h, new List<LogicalLine?>()).LoadErbDir(h.ErbDir, displayReport: false, labelDicParallel);
		var parallelLines = h.Console.DisplayLineList.Skip(beforeParallel)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		var before = h.Console.DisplayLineCount;
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		var labelDicSerial = new LabelDictionary();
		await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
			.LoadErbDir(h.ErbDir, displayReport: false, labelDicSerial);
		var serialLines = h.Console.DisplayLineList.Skip(before)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		// sanity：callform 分支确实在解析未调用函数（nestCheck/FNF 警告出现；FNC 警告不该出现）
		Assert.Contains(parallelLines, s => s.Contains("REPEAT, FOR, WHILE, DOの中以外で"));
		Assert.Contains(parallelLines, s => s.Contains("NOEXIST_FUNC") && s.Contains("存在しません"));
		Assert.DoesNotContain(parallelLines, s => s.Contains("一度も呼び出されません"));

		static List<string> Signature(LabelDictionary dic) =>
			dic.GetAllLabels(false).OrderBy(l => l.Index)
				.Select(l => $"{l.LabelName}:{l.Index}:{l.FileIndex}:{l.Depth}").ToList();
		Assert.Equal(Signature(labelDicSerial), Signature(labelDicParallel));
		Assert.Equal(serialLines, parallelLines);
	}

	/// <summary>
	/// 08 行为锚点：IgnoreUncalledFunction=true（默认）时未调用函数不解析、NextLine 标记 IsError
	///（"呼び出されないはずの関数が呼ばれた"），并行 / 串行输出一致且标记生效。
	/// </summary>
	[Fact]
	public async Task LoadErbDir_parsescript_parallel_uncalled_iserror_equivalent()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErb("A.ERB", "@FOO\nRETURN\n@UNCALLED_A\nRETURN\n");
		await h.PreloadAsync();

		var beforeParallel = h.Console.DisplayLineCount;
		var labelDicParallel = new LabelDictionary();
		await NewLoaderWithSpy(h, new List<LogicalLine?>()).LoadErbDir(h.ErbDir, displayReport: false, labelDicParallel);
		var parallelLines = h.Console.DisplayLineList.Skip(beforeParallel)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		var before = h.Console.DisplayLineCount;
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		var labelDicSerial = new LabelDictionary();
		await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
			.LoadErbDir(h.ErbDir, displayReport: false, labelDicSerial);
		var serialLines = h.Console.DisplayLineList.Skip(before)
			.Select(l => l?.ToString() ?? "").Where(s => !s.StartsWith("経過時間:")).ToList();

		// IsError 标记生效（并行路径：else 分支 ignoreUncalledFunction 路径，解析集为空）
		var uncalled = labelDicParallel.GetAllLabels(false).First(l => l.LabelName == "UNCALLED_A");
		Assert.True(uncalled.NextLine.IsError);
		Assert.Equal("呼び出されないはずの関数が呼ばれた", uncalled.NextLine.ErrMes);
		Assert.Equal(serialLines, parallelLines);
	}
}
