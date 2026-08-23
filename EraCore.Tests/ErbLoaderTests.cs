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
	/// <summary>构造 ErbLoader + 一个记录 scaningLine 写入的 spy sink。</summary>
	private static ErbLoader NewLoaderWithSpy(LoaderTestHarness h, List<LogicalLine?> spy)
	{
		return new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => spy.Add(line));
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
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, analysisEnv, _ => { });
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
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, analysisEnv, line => spy.Add(line));
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
}
