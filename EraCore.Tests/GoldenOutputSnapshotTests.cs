using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue 01：**黄金输出四件套冻结**（ADR-0027 决策四）——把「逐位等价」从人工比日志变成机检快照。
/// 四件套 = ① 警告序列（文本+顺序+级别）② FNF 决策结果 ③ Depth 普查 ④ <c>parsescript_diag</c> 阶段形状。
/// <para>
/// <b>基座</b>：语料一律取自 <see cref="ErbLoaderEquivalenceCorpus"/>（既有等价性语料，未新建），
/// 判定经既有 <see cref="LoaderTestHarness"/> 构造与断言，**不 mock 内部协作对象**。
/// </para>
/// <para>
/// <b>数值纪律</b>：不断言任何墙钟数值（ms 用 <c>{…:F0}</c> 发射，噪声 ±1.5s，ADR-0024 判读规则）——
/// ④ 只断言**字段名序列与计数**，ms 值仅做「可解析为整数」的形状检查。
/// </para>
/// <para>
/// <b>可观测面</b>（零产品改动）：① ② 经 <c>EmueraConsole.DisplayLineList</c> 增量行；
/// ③ 经 <c>FunctionLabelLine.Depth</c> 直读与 ④ 的 <c>depth_hist</c> 行互证；
/// ④ 经 <see cref="EmueraLog.SetPlatformSink"/> 抓 <c>ErbLoader</c> category 的三行文本。
/// </para>
/// <para>
/// <b>已知限制</b>：③ 的基座语料只覆盖 <c>depth ∈ {-1, 0}</c>（-1 = 未被调用到的标签，0 = 事件/系统标签）；
/// 更深的调用层级分布需要新语料，而本票的验收项要求「未新建语料」——故更深分布由既有逐标签
/// <c>depth=</c> 断言承接（<see cref="ErbLoaderTests"/>、<see cref="UncalledSweepEquivalenceTests"/>）。
/// </para>
/// <para>
/// <b>第四刀增补</b>（ADR-0027 决策二.4 / issue 05）：<see cref="Golden_ir_and_lazy_matrix_matches_across_ir_modes"/>
/// 把「IR 三态 × 懒加载开关」纳入同一套判据——IR off/miss/hit/invalidated 逐位相等；
/// lazy 开关的差异只允许是 ADR-0025 决策六定义的「未调用函数告警是否发射」那一类（③④ 仍逐位相等）。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public class GoldenOutputSnapshotTests
{
	/// <summary>警告行格式（`Lang.cs` 的 `Warning1/2/3`）：级别嵌在文本里，无独立级别字段落盘。</summary>
	private static readonly Regex WarningRow = new(@"^警告Lv(\d+):([^:]+):(\d+)行目:(.*)$", RegexOptions.Compiled);

	// ===== 采集 =====

	/// <summary>
	/// 一次装载采集到的四件套原料。
	/// <list type="bullet">
	/// <item><c>Warnings</c> ① 警告行投影 `Lv{级别}|{文件}:{行}|{消息}`，保持**原始发射顺序**。</item>
	/// <item><c>FnfRows</c> ② FNF 相关行（`警告Lv…` 中含「存在しません」者）+ FNF 去重计数行。</item>
	/// <item><c>DepthHistogram</c> ③ `label.Depth → 计数`（与 `depth_hist` 同源同语义）。</item>
	/// <item><c>DiagPairs</c> ④ `parsescript_diag` 三行的 (键 → 值) 投影：`phases::main_ms=12` 形态。</item>
	/// </list>
	/// </summary>
	private sealed record LoadCapture(List<string> Warnings, List<string> FnfRows,
		List<string> DepthHistogram, List<string> DiagPairs);

	/// <summary>
	/// 跑一遍语料并采集四件套。配置 = 未调用函数真解析 + FNC/FNF ONCE（对齐
	/// <see cref="ErbLoaderTests"/> 的等价性用例），DisplayReport 恒开（diag 采集门控）。
	/// </summary>
	private static async Task<LoadCapture> CaptureAsync(Action<LoaderTestHarness> writeCorpus, bool parallelParseScript)
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		writeCorpus(h);
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.FunctionNotCalledWarning).SetValue(DisplayWarningFlag.ONCE);
		ConfigData.Current!.GetConfigItem(ConfigCode.FunctionNotFoundWarning).SetValue(DisplayWarningFlag.ONCE);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(parallelParseScript);
		ConfigData.Current!.GetConfigItem(ConfigCode.DisplayReport).SetValue(true);
		await h.PreloadAsync();

		var diagRaw = new List<string>();
		void Sink(EmueraLog.Level _, string line)
		{
			if (!line.Contains("parsescript_diag", StringComparison.Ordinal))
				return;
			lock (diagRaw)
				diagRaw.Add(Normalize(line, h.TempRoot));
		}

		var before = h.Console.DisplayLineCount;
		var labelDic = new LabelDictionary();
		EmueraLog.SetPlatformSink(Sink);
		bool ok;
		try
		{
			ok = await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
				.LoadErbDir(h.ErbDir, displayReport: false, labelDic);
		}
		finally
		{
			EmueraLog.SetPlatformSink(null);
		}
		Assert.True(ok, "LoadErbDir failed\n" + Dump(h));

		return Project(h, before, diagRaw, labelDic);
	}

	/// <summary>四件套投影（① 警告序列 / ② FNF 决策行 / ③ 深度普查 / ④ diag 形状）——两套采集共用。</summary>
	private static LoadCapture Project(LoaderTestHarness h, int before, List<string> diagRaw, LabelDictionary labelDic)
	{
		var lines = h.Console.DisplayLineList.Skip(before)
			.Select(l => l?.ToString() ?? "")
			.Where(s => s.Length > 0)
			.Select(s => Normalize(s, h.TempRoot))
			.ToList();

		// ① 警告序列（文本+顺序+级别）。経過時間: 行是墙钟，按既有先例剔除。
		var warnings = new List<string>();
		foreach (var s in lines)
		{
			if (s.StartsWith("経過時間:", StringComparison.Ordinal))
				continue;
			var m = WarningRow.Match(s);
			if (m.Success)
				warnings.Add($"Lv{m.Groups[1].Value}|{m.Groups[2].Value}:{m.Groups[3].Value}|{m.Groups[4].Value}");
		}

		// ② FNF：本套只对「未定义函数的调用」警告行 + ONCE 去重汇总行取快照（去重是决策本体）。
		var fnfRows = new List<string>();
		foreach (var s in lines)
		{
			if (s.Contains("呼び出した事に関する警告を", StringComparison.Ordinal))
			{
				fnfRows.Add(s); // 汇总行不套用 警告Lv 定位格式
				continue;
			}
			var fm = WarningRow.Match(s);
			if (fm.Success && fm.Groups[4].Value.Contains("存在しません", StringComparison.Ordinal))
				fnfRows.Add($"Lv{fm.Groups[1].Value}|{fm.Groups[2].Value}:{fm.Groups[3].Value}|{fm.Groups[4].Value}");
		}

		// ③ 深度普查：与 ParseScript 内的 depthHist 同源（GetAllLabels(true) + label.Depth）。
		var histogram = labelDic.GetAllLabels(true)
			.GroupBy(l => l.Depth)
			.OrderBy(g => g.Key)
			.Select(g => $"d{g.Key}={g.Count()}")
			.ToList();

		// ④ diag：三行 → (序号, 键, 值) 投影；ms 值不进断言（只有形状）。
		var diagPairs = new List<string>();
		foreach (var line in diagRaw)
		{
			var payload = line[(line.IndexOf("parsescript_diag", StringComparison.Ordinal) + "parsescript_diag".Length)..].Trim();
			var space = payload.IndexOf(' ');
			var kind = space < 0 ? payload : payload[..space];
			var rest = space < 0 ? "" : payload[(space + 1)..];
			foreach (var tok in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
				diagPairs.Add($"{kind}::{tok}");
		}

		return new LoadCapture(warnings, fnfRows, histogram, diagPairs);
	}

	/// <summary>IR 磁盘缓存三态（矩阵维度之一）。</summary>
	private enum IrMode
	{
		/// <summary>IR 关闭（<c>IrDiskCacheLoad=false</c>）：纯源路径。</summary>
		Off,
		/// <summary>IR 启用 + 首轮（无缓存文件）→ miss 建库。</summary>
		Miss,
		/// <summary>第二轮命中（缓存由前置轮建立、源未变）。</summary>
		Hit,
		/// <summary>缓存存在但源已变（重写语料 → mtime 变 → 指纹失配）→ 判废重建。</summary>
		Invalidated,
	}

	/// <summary>
	/// 矩阵采集：<c>IR 三态</c> × <c>lazy 开关</c>，只采集**最后一轮**的四件套。
	/// 除这两维外其余配置与 <see cref="CaptureAsync"/> 逐字对齐（否则矩阵内部不可比）。
	/// <para>
	/// 前提耦合（由 <see cref="ErbIrLoadPipeline.ShouldUseIrCache"/> 决定，真值表见 ErbIrCacheTests）：
	/// IR 参与要求 <c>lazy=true</c>——故 <c>lazy=false</c> 下 IR 三态**退化为 IR off**。
	/// 矩阵因此把「lazy=off × IR miss」也采一格，用来断言这条退化（而非假设）。
	/// </para>
	/// </summary>
	private static async Task<LoadCapture> CaptureMatrixAsync(bool lazy, IrMode irMode, Action<LoaderTestHarness> writeCorpus)
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		writeCorpus(h);
		var data = ConfigData.Current!;
		data.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		data.GetConfigItem(ConfigCode.FunctionNotCalledWarning).SetValue(DisplayWarningFlag.ONCE);
		data.GetConfigItem(ConfigCode.FunctionNotFoundWarning).SetValue(DisplayWarningFlag.ONCE);
		data.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		data.GetConfigItem(ConfigCode.DisplayReport).SetValue(true);
		data.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(lazy);
		data.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(irMode != IrMode.Off);
		await h.PreloadAsync();

		// 前置轮：Hit/Invalidated 需先建库（否则采集轮无从命中）
		if (irMode is IrMode.Hit or IrMode.Invalidated)
		{
			bool pre = await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
				.LoadErbDir(h.ErbDir, displayReport: false, new LabelDictionary());
			Assert.True(pre, "矩阵前置轮 LoadErbDir 失败（建库轮）\n" + Dump(h));
			if (irMode == IrMode.Invalidated)
			{
				// 重写同一语料：内容不变、mtime 变 → 指纹（含逐文件 mtime）失配 → 判废重建。
				writeCorpus(h);
				await h.PreloadAsync();
			}
		}

		var diagRaw = new List<string>();
		void Sink(EmueraLog.Level _, string line)
		{
			if (!line.Contains("parsescript_diag", StringComparison.Ordinal))
				return;
			lock (diagRaw)
				diagRaw.Add(Normalize(line, h.TempRoot));
		}

		var before = h.Console.DisplayLineCount;
		var labelDic = new LabelDictionary();
		EmueraLog.SetPlatformSink(Sink);
		bool ok;
		try
		{
			ok = await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
				.LoadErbDir(h.ErbDir, displayReport: false, labelDic);
		}
		finally
		{
			EmueraLog.SetPlatformSink(null);
		}
		Assert.True(ok, "矩阵采集轮 LoadErbDir 失败\n" + Dump(h));
		return Project(h, before, diagRaw, labelDic);
	}

	/// <summary>剔除临时根绝对路径（每次运行不同），使快照只含稳定内容。</summary>
	private static string Normalize(string s, string tempRoot)
		=> string.IsNullOrEmpty(tempRoot) ? s : s.Replace(tempRoot, "<TMP>/", StringComparison.OrdinalIgnoreCase);

	private static string Dump(LoaderTestHarness h)
		=> string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? ""));

	// ===== ① 警告序列 =====

	[Fact]
	public async Task Golden_warning_sequence_is_frozen()
	{
		var cap = await CaptureAsync(h => ErbLoaderEquivalenceCorpus.WriteFnfWarningCorpus(h), parallelParseScript: false);

		// sanity：语料确实触发了三类警告（否则下面的快照就是「冻结了空集」）
		Assert.Contains(cap.Warnings, s => s.Contains("一度も呼び出されません", StringComparison.Ordinal));
		Assert.Contains(cap.Warnings, s => s.Contains("存在しません", StringComparison.Ordinal));
		Assert.Contains(cap.Warnings, s => s.Contains("REPEAT, FOR, WHILE, DOの中以外で", StringComparison.Ordinal));

		Assert.Equal(ExpectedWarningSequence, cap.Warnings);
	}

	/// <summary>
	/// ① 的冻结值：文本 + **顺序** + 级别。语料 = A.ERB（`@UNCALLED_A`: BREAK + CALL NOEXIST_FUNC）、
	/// B.ERB（`@UNCALLED_B`: 两个 FNF，同文件第二个被 ONCE 去重）。
	/// <para>
	/// <b>顺序是被冻结的事实本身</b>：同一 label 内 FNC（物化期）→ nestCheck（`BREAK` 在循环外）→
	/// setJumpTo（FNF）——三pass 的发射次序在行号上不可解释（B.ERB:4 先于 B.ERB:2），正是 ADR-0027
	/// 要求「警告发射序与原版逐位等价」的观测面。
	/// </para>
	/// </summary>
	private static readonly List<string> ExpectedWarningSequence =
	[
		"Lv1|A.ERB:3|関数\"@UNCALLED_A\"は定義されていますが一度も呼び出されません",
		"Lv2|A.ERB:4|REPEAT, FOR, WHILE, DOの中以外で\"BREAK\"文が使われました",
		"Lv2|A.ERB:5|指定された関数名\"@NOEXIST_FUNC\"は存在しません",
		"Lv1|B.ERB:1|関数\"@UNCALLED_B\"は定義されていますが一度も呼び出されません",
		"Lv2|B.ERB:4|REPEAT, FOR, WHILE, DOの中以外で\"BREAK\"文が使われました",
		"Lv2|B.ERB:2|指定された関数名\"@NOEXIST_FUNC_B\"は存在しません",
	];

	// ===== ② FNF 决策结果 =====

	[Fact]
	public async Task Golden_fnf_decisions_are_frozen()
	{
		var cap = await CaptureAsync(h => ErbLoaderEquivalenceCorpus.WriteFnfWarningCorpus(h), parallelParseScript: false);

		// ONCE 去重是「决策本体」：每个文件首个 FNF 发射、同文件后续被忽略并计入汇总行。
		Assert.Equal(ExpectedFnfRows, cap.FnfRows);
	}

	/// <summary>
	/// ② 的冻结值：**去重汇总行在前、被发射的 FNF 在后**（汇总行在 `ErbLoader.cs:1835` 由
	/// <c>output.PrintError</c> 直出，而警告行走 `FlushWarningList` 缓冲——顺序本身是事实）。
	/// 去重决策 = 每文件首个 FNF 发射、同文件后续忽略：A.ERB 1 条 + B.ERB 1 条 + 1 条被忽略。
	/// </summary>
	private static readonly List<string> ExpectedFnfRows =
	[
		"警告Lv2:定義されていない関数を呼び出した事に関する警告を1件無視しました",
		"Lv2|A.ERB:5|指定された関数名\"@NOEXIST_FUNC\"は存在しません",
		"Lv2|B.ERB:2|指定された関数名\"@NOEXIST_FUNC_B\"は存在しません",
	];

	// ===== ③ Depth 普查 =====

	[Fact]
	public async Task Golden_depth_histogram_is_frozen()
	{
		var cap = await CaptureAsync(h => ErbLoaderEquivalenceCorpus.WriteFnfWarningCorpus(h), parallelParseScript: false);

		Assert.Equal(ExpectedDepthHistogram, cap.DepthHistogram);
	}

	/// <summary>
	/// ③ 的冻结值：`-1` = 未被调用到的标签（既有约定：Depth 保持 -1；本语料里两个 `@UNCALLED_*`
	/// 因 harness 的 CALL 解析语义不进入调用图），`0` = 事件标签。更深分布不在既有语料内（见类文档）。
	/// </summary>
	private static readonly List<string> ExpectedDepthHistogram =
	[
		"d-1=2",
		"d0=1",
	];

	// ===== ④ parsescript_diag 阶段形状 =====

	[Fact]
	public async Task Golden_parsescript_diag_shape_is_frozen_serial()
	{
		var cap = await CaptureAsync(h => ErbLoaderEquivalenceCorpus.WriteFnfWarningCorpus(h), parallelParseScript: false);

		var keys = cap.DiagPairs.Select(s => s.Split("::")[0] + "::" + s.Split("::")[1].Split('=')[0]).ToList();
		Assert.Equal(ExpectedDiagKeys, keys);

		// 计数稳定 ⇒ 进断言；ms 值抖动 ⇒ 只做「可解析为 F0 整数」的形状检查。
		Assert.Equal("1", ValueOf(cap, "phases::labels_main"));
		Assert.Equal("2", ValueOf(cap, "phases::labels_uncalled"));
		Assert.Equal("False", ValueOf(cap, "phases::uncalled_parallel")); // 注意：该字段是 bool，不是 0/1
		AssertIntShaped(cap, "phases::main_ms");
		AssertIntShaped(cap, "steps::main_setjumpto_ms");

		// depth_hist 行与 ③ 的直读直方图必须逐项相等（同一事实的两条出口）。
		Assert.Equal(ExpectedDepthHistogram, cap.DiagPairs.Where(s => s.StartsWith("depth_hist::", StringComparison.Ordinal))
			.Select(s => s["depth_hist::".Length..].Split('=')[0] + "=" + s.Split('=')[1]).ToList());
	}

	/// <summary>
	/// ④ 并行配置：形状必须一致。既有事实——并行清扫段把 `_parseDiag` 置空（`ErbLoader.cs:2015`），
	/// 故 `uncalled_*` 三个桶恒为 0；本断言把这一既有空洞钉住，防止将来误以为「漏采」而顺手改行为。
	/// </summary>
	[Fact]
	public async Task Golden_parsescript_diag_shape_is_frozen_parallel()
	{
		var cap = await CaptureAsync(h => ErbLoaderEquivalenceCorpus.WriteFnfWarningCorpus(h), parallelParseScript: true);

		var keys = cap.DiagPairs.Select(s => s.Split("::")[0] + "::" + s.Split("::")[1].Split('=')[0]).ToList();
		Assert.Equal(ExpectedDiagKeys, keys);

		Assert.Equal("True", ValueOf(cap, "phases::uncalled_parallel"));
		Assert.Equal("0", ValueOf(cap, "steps::uncalled_setargument_ms"));
		Assert.Equal("0", ValueOf(cap, "steps::uncalled_nestcheck_ms"));
		Assert.Equal("0", ValueOf(cap, "steps::uncalled_setjumpto_ms"));
	}

	private static readonly List<string> ExpectedDiagKeys =
	[
		"phases::main_ms", "phases::callform_ms", "phases::uncalled_ms",
		"phases::labels_main", "phases::labels_uncalled", "phases::uncalled_parallel",
		"steps::main_setargument_ms", "steps::main_nestcheck_ms", "steps::main_setjumpto_ms",
		"steps::uncalled_setargument_ms", "steps::uncalled_nestcheck_ms", "steps::uncalled_setjumpto_ms",
		"depth_hist::d-1", "depth_hist::d0",
	];

	private static string ValueOf(LoadCapture cap, string key)
		=> cap.DiagPairs.First(s => s.StartsWith(key + "=", StringComparison.Ordinal))[(key.Length + 1)..];

	/// <summary>形状检查：该字段非空且可解析为整数（ms 用 <c>{…:F0}</c> 发射，故形态恒为整数文本）。</summary>
	private static void AssertIntShaped(LoadCapture cap, string key)
		=> Assert.True(long.TryParse(ValueOf(cap, key), out _), $"{key} 应发射为整数文本");

	// ===== 探针有效性自检（常驻）=====

	/// <summary>
	/// 常驻自检（替代一次性手工注入）：同一**既有**语料的 tamper 变体（抽掉一处行标签，
	/// 即 <see cref="ErbLoaderEquivalenceCorpus.WriteCorpusA"/> 的 tamper 开关）必须使警告序列快照不同。
	/// 若本用例红 ⇒ 上面的快照断言退化成常量（假绿），四件套失效。
	/// </summary>
	[Fact]
	public async Task Golden_warning_snapshot_detects_corpus_tamper()
	{
		var clean = await CaptureAsync(h => ErbLoaderEquivalenceCorpus.WriteCorpusA(h, tamper: false), parallelParseScript: false);
		var tampered = await CaptureAsync(h => ErbLoaderEquivalenceCorpus.WriteCorpusA(h, tamper: true), parallelParseScript: false);

		Assert.NotEqual(clean.Warnings, tampered.Warnings);
	}

	// ===== 矩阵：IR 三态 × 懒加载开关（ADR-0027 第四刀验收项 2/3）=====

	/// <summary>
	/// 6 格矩阵 = IR{off, miss, hit, invalidated} × lazy{off, on} 中有意义的 6 格。
	/// <para>
	/// 断言分三层（全部由 2026-10-16 实测标定，非假设）：
	/// </para>
	/// <list type="number">
	/// <item><b>IR 三态逐位相等</b>（验收项 2 的严格形式）：同一 lazy 值下 off/miss/hit/invalidated
	/// 四格的 ①警告序列 ②FNF 决策 ③depth 普查 ④diag 形状**全等**——IR 只换物化后背（源 ↔ blob），
	/// 不改可观测输出。</item>
	/// <item><b>lazy=off 的退化钉子</b>：<see cref="ErbIrLoadPipeline.ShouldUseIrCache"/> 要求 lazy
	/// ⇒ lazy=off 下 IR 三态退化为 IR off，故这两格必须逐位相等（把前提耦合钉成测试而非注释）。</item>
	/// <item><b>lazy 开关的差异被钉住</b>（验收项 3）：③④ 两态逐位相等；①② 的差异**不断言相等**——
	/// ADR-0025 决策六明文「警告延迟到物化时发射是本决策的**定义性行为**」「警告分歧文档化但不断言相等」。
	/// 故此处断言差异的**确切内容**：lazy=on 的警告序列 = lazy=off 中「未调用」行的有序子集
	/// （未调用函数不被解析 ⇒ 其 BREAK/FNF 系解析告警不发射），且 FNF 行为空。</item>
	/// </list>
	/// </summary>
	[Fact]
	public async Task Golden_ir_and_lazy_matrix_matches_across_ir_modes()
	{
		Action<LoaderTestHarness> corpus = h => ErbLoaderEquivalenceCorpus.WriteFnfWarningCorpus(h);

		// ── ① IR 三态逐位相等（lazy=on；IR 参与的前提）──
		var lazyOnIrOff = await CaptureMatrixAsync(lazy: true, IrMode.Off, corpus);
		foreach (var mode in new[] { IrMode.Miss, IrMode.Hit, IrMode.Invalidated })
		{
			var cell = await CaptureMatrixAsync(lazy: true, mode, corpus);
			AssertSameFourTuple(lazyOnIrOff, cell, $"lazy=on ir={mode} 应与 ir=Off 逐位相等（IR 只换物化后背）");
		}

		// ── ② lazy=off 退化：IR 不参与（ShouldUseIrCache 要求 lazy）⇒ 与 IR off 逐位相等 ──
		var lazyOffIrOff = await CaptureMatrixAsync(lazy: false, IrMode.Off, corpus);
		var lazyOffIrMiss = await CaptureMatrixAsync(lazy: false, IrMode.Miss, corpus);
		AssertSameFourTuple(lazyOffIrOff, lazyOffIrMiss,
			"lazy=off 下 IR 不参与（ShouldUseIrCache 要求 lazy）⇒ IR 开关不改变任何可观测输出");

		// ── ③ lazy 开关：③④ 逐位相等（④ 的 ms 值屏蔽，ADR-0024 判读规则），
		//    ①② 差异钉成 ADR-0025 决策六的定义性形态 ──
		Assert.Equal(lazyOffIrOff.DepthHistogram, lazyOnIrOff.DepthHistogram);
		Assert.Equal(MaskWallClock(lazyOffIrOff.DiagPairs), MaskWallClock(lazyOnIrOff.DiagPairs));

		var uncalledOnly = lazyOffIrOff.Warnings
			.Where(w => w.Contains("一度も呼び出されません", StringComparison.Ordinal))
			.ToList();
		Assert.True(uncalledOnly.Count > 0, "语料应含未调用标签的告警（否则本对比失去意义）");
		Assert.Equal(uncalledOnly, lazyOnIrOff.Warnings); // 惰性 = 未调用函数不被解析 ⇒ 只剩免解析导出的告警
		Assert.Empty(lazyOnIrOff.FnfRows);                // FNF 系告警同样依赖解析
		Assert.NotEqual(lazyOffIrOff.Warnings, lazyOnIrOff.Warnings); // 差异确实存在（防空转）
	}

	/// <summary>四件套逐位相等断言（④ 的 ms 值屏蔽——墙钟噪声，ADR-0024 判读规则；失败时打印两侧全文）。</summary>
	private static void AssertSameFourTuple(LoadCapture expected, LoadCapture actual, string because)
	{
		// ms 只保证形状（整数文本），不进逐位比较。
		Assert.True(AllMsAreIntShaped(expected.DiagPairs), "expected 侧 ms 字段必须是整数文本（形状保证）");
		Assert.True(AllMsAreIntShaped(actual.DiagPairs), "actual 侧 ms 字段必须是整数文本（形状保证）");

		var expDiag = MaskWallClock(expected.DiagPairs).ToList();
		var actDiag = MaskWallClock(actual.DiagPairs).ToList();
		bool same = expected.Warnings.SequenceEqual(actual.Warnings)
			&& expected.FnfRows.SequenceEqual(actual.FnfRows)
			&& expected.DepthHistogram.SequenceEqual(actual.DepthHistogram)
			&& expDiag.SequenceEqual(actDiag);
		Assert.True(same,
			$"{because}\n-- warnings --\n{string.Join("\n", expected.Warnings)}\n  vs\n{string.Join("\n", actual.Warnings)}" +
			$"\n-- fnf --\n{string.Join("\n", expected.FnfRows)}\n  vs\n{string.Join("\n", actual.FnfRows)}" +
			$"\n-- depth --\n{string.Join("\n", expected.DepthHistogram)}\n  vs\n{string.Join("\n", actual.DepthHistogram)}" +
			$"\n-- diag --\n{string.Join("\n", expDiag)}\n  vs\n{string.Join("\n", actDiag)}");
	}

	/// <summary>把 `_ms=&lt;数字&gt;` 归一为 `_ms=&lt;ms&gt;`：墙钟噪声不进比较（ADR-0024 判读规则）。</summary>
	private static IEnumerable<string> MaskWallClock(IEnumerable<string> diagPairs)
		=> diagPairs.Select(p => Regex.Replace(p, @"_ms=\d+", "_ms=<ms>"));

	/// <summary>ms 字段必须是整数文本（形状保证——计数/形状稳定，ms 值抖动）。</summary>
	private static bool AllMsAreIntShaped(IEnumerable<string> diagPairs)
		=> diagPairs.All(p =>
		{
			var m = Regex.Match(p, @"_ms=(.+)$");
			return !m.Success || long.TryParse(m.Groups[1].Value, out _);
		});

}
