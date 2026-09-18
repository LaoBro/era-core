using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using Xunit;
using Xunit.Abstractions;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue 22 S1：「表达式树 / Argument 对象图」**可编码性普查**（issue 22 §1 / §2 S1）。
///
/// 回答三件事：
///   ① 终态里有哪些类型、各几个字段、字段属于哪一类（值 / 符号引用 / 表引用 / 图节点 / 运行期状态 / 可重建）；
///   ② `argprimitive` / `assigndest` 在物化后是否已被 Pop 取空（未取空 ⇒ 终态仍含原始文本，格式面显著变大）；
///   ③ 运行期状态字段清单（必须排除，否则序列化的不是「解析终态」）。
///
/// 手法：**反射读私有字段**（测试跑 CoreCLR，不受 NativeAOT 限制）⇒ 本普查不改 Core 一行、
/// 不碰 blob 格式、不进发布路径。AOT 可用的编解码器届时需要少量 additive `internal` 访问器，
/// 报告中的「字段类型明细」可直接列出这些缺口。
///
/// 两个测试：
///   · <see cref="Census_synthetic_corpus_terminal_state"/>：合成语料，验证普查本身 + 口径不变量（硬断言）。
///   · <see cref="Census_real_game_corpus_when_configured"/>：设 `ERACORE_CENSUS_GAME_DIR` 指向真实游戏目录
///     时对真实语料普查（可配 `ERACORE_CENSUS_LABELS` 限标签数，默认 400）；未设则跳过（保持 CI 绿）。
///
/// 输出：ITestOutputHelper + 环境变量 `ERACORE_CENSUS_OUT` 指定时另写文件（便于留档到 .scratch）。
/// </summary>
[Collection("LoaderTests")]
public class IrObjectGraphCensusTests
{
	private readonly ITestOutputHelper _out;

	public IrObjectGraphCensusTests(ITestOutputHelper o) => _out = o;

	/// <summary>覆盖多类语句形态的语料（PRINT 系 / IF-ELSEIF / FOR-NEXT / SELECTCASE / 赋值 / 跳转 / CALL / 式中函数）。</summary>
	private const string Corpus = """
@SYSTEM_TITLE
	CALL CENSUS_FUNC
	RETURN

@CENSUS_FUNC
#DIM LOCALNUM, 3
#DIMS LOCALSTR
	LOCALNUM:1 = 7
	LOCALSTR = "abc"
	A = 1 + 2 * 3
	B += A
	C = A > B
	PRINTFORML hello
	PRINTV A
	PRINTFORMW %LOCALNUM%{B}
	IF A == 1
		PRINTL one
	ELSEIF A == 2
		PRINTL two
	ELSE
		PRINTL other
	ENDIF
	FOR LOCALNUM, 0, 3
		S = LOCALNUM
	NEXT
	SELECTCASE A
		CASE 1
			PRINTL case1
		CASEELSE
			PRINTL caseelse
	ENDSELECT
	GOTO CENSUS_DOLLAR
	PRINTL unreachable
$CENSUS_DOLLAR
	CALL CENSUS_CALLEE(A, "z")
	RETURN

@CENSUS_CALLEE(ARG, ARGS)
	PRINTV ARG
	PRINTS ARGS
	RETURN
""";

	[Fact]
	public async Task Census_synthetic_corpus_terminal_state()
	{
		using var h = new LoaderTestHarness();
		h.WriteErb("CENSUS.ERB", Corpus);
		await h.PreloadAsync();
		await h.InitializeAsync();

		// 对齐生产形态：真机 fp 行实测 `ra=0 nr=True`，即 ReduceArgumentOnLoad=YES(=0) → 派生的
		// NeedReduceArgumentOnLoad=true，此时 setArgument 才会对每行调 SetArgumentTo（ErbLoader.setArgument:3106）。
		// 两处坑（实测踩过）：
		//   ① 漏设 → 28 行里 17 行 Argument 为 null、argprimitive/assigndest 未 Pop，测的不是生产终态；
		//   ② NR 是**派生属性**（无 ConfigCode，只在 ConfigData.CheckUpdate:965-969 重算），光 SetValue 不生效
		//      ——必须显式 CheckUpdate；且必须设在本次手工加载之前（引擎自身的 Initialize 已经跑过）。
		ForceReduceArgumentOnLoad();
		Assert.True(MinorShift.Emuera.Runtime.Config.Config.NeedReduceArgumentOnLoad, "NR 应已生效为 true");

		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "LoadErbDir failed:\n" + string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var label = Find(h.LabelDic, "CENSUS_FUNC");
		Assert.NotNull(label);
		Assert.False(label!.IsStub, "CENSUS_FUNC 应已物化（harness 默认 eager）");

		var report = new StringBuilder();
		var stats = RunCensus(report, [label!], "合成语料 CENSUS_FUNC（NR=YES，eager）");
		Emit(report, "synthetic");

		Assert.True(stats.InstrLines >= 10, $"语料应产出 ≥10 条指令行，实际 {stats.InstrLines}");
		Assert.True(stats.ExprTypes.Count >= 3, $"应观察到 ≥3 种表达式节点，实际 {stats.ExprTypes.Count}");
		Assert.True(stats.ArgTypes.Count >= 6, $"应观察到 ≥6 种 Argument，实际 {stats.ArgTypes.Count}");
		// 生产形态（NR=true）的终态必须不含原始文本/词表结构——这是格式面的硬边界
		Assert.True(stats.ArgPrimitiveKept == 0, $"NR=true 下 argprimitive 应全部 Pop 取空，实际未取空 {stats.ArgPrimitiveKept}");
		Assert.True(stats.AssignDestKept == 0, $"NR=true 下 assigndest 应全部 Pop 取空，实际未取空 {stats.AssignDestKept}");
		// Argument==null 只软约束：个别行（Debug 系等）本就无参数解析；明细见报告，量级抬升即口径偏离
		Assert.True(stats.NullArgs <= 2, $"Argument==null 行数异常抬升：{stats.NullArgs}/{stats.InstrLines}（明细：{string.Join(" ", stats.NullArgDetail)}）");
	}

	[Fact]
	public async Task Census_real_game_corpus_when_configured()
	{
		var gameDir = Environment.GetEnvironmentVariable("ERACORE_CENSUS_GAME_DIR");
		if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
		{
			_out.WriteLine("跳过：未设 ERACORE_CENSUS_GAME_DIR（真实语料普查按需开启）。");
			return;
		}
		var cap = int.TryParse(Environment.GetEnvironmentVariable("ERACORE_CENSUS_LABELS"), out var c) && c > 0 ? c : 400;

		using var h = new LoaderTestHarness(gameDir);
		// 真实语料必须**按游戏自带配置**加载，否则 harness 的默认配置会把游戏跑挂：
		//   ① 默认 SystemSaveInBinary=NO → 游戏 VARIABLE.ERH 等 50+ 处「キャラ型変数に SAVEDATA
		//      フラグ…バイナリ型セーブ オプションが必須」以 **error 语义**触发（ParserMediator.Warn isError:true）
		//      → noError=false → Process.Initialize 返回 false（2026-09-18 实测）；
		//   ② 默认语言编码未设 → LangManager.lang 为 null → VARIABLE.ERH 的 #DIM 常量求值 NRE。
		// 正路：LoadConfig(exeDir)（ConfigData.cs:785，读 <exeDir>/emuera.config + csv 下 default/fixed），
		// 再补跑 ApplyPostLoadEffects()（LangManager.setEncode 的唯一入口，:874-887；幂等）。
		ConfigData.Current!.LoadConfig(gameDir);
		ConfigData.Current!.ApplyPostLoadEffects();
		await h.InitializeAsync(); // 用游戏自带配置加载（生产形态；真机 fp 实测 NR=true）

		var all = h.LabelDic.GetAllLabels(true);
		var labels = all.Where(l => l != null).Take(cap).ToList();

		var report = new StringBuilder();
		var stats = RunCensus(report, labels, $"真实语料 {gameDir}（前 {labels.Count}/{all.Count} 个标签，eager，**游戏自带配置**）");
		Emit(report, "real");

		Assert.True(labels.Count > 0, "真实语料应至少解析出一个函数标签");
		Assert.True(stats.InstrLines > 100, $"真实语料应产出 >100 条指令行，实际 {stats.InstrLines}");
	}

	/// <summary>
	/// 真实语料的 **NR=YES 变体**——必须单独跑的原因（2026-09-18 实测）：
	/// 本机 PC 副本的 `emuera.config` 是 `ロード時に引数を解析する:NO`，而**真机 fp 行是 `ra=0 nr=True`（YES）**。
	/// 两者物化终态完全不同：NR=NO 下实测 11504 行里 **3229 行没有 Argument**（参数解析被推到运行期首次执行），
	/// NR=YES 才是 v3 量到 `setArgument 2332ms` 的那个生产形态。⇒ 类型覆盖必须两种都采，
	/// 编解码器要覆盖**两者并集**。
	/// 手法：引擎初始化（含一次 NR=NO 全量加载）之后强开 NR + CheckUpdate，再用**全新 LabelDictionary**
	/// 二次 LoadErbDir（等价于「改完配置再开一局」的生产路径）。
	/// </summary>
	[Fact]
	public async Task Census_real_game_corpus_nr_forced_when_configured()
	{
		var gameDir = Environment.GetEnvironmentVariable("ERACORE_CENSUS_GAME_DIR");
		if (string.IsNullOrEmpty(gameDir) || !Directory.Exists(gameDir))
		{
			_out.WriteLine("跳过：未设 ERACORE_CENSUS_GAME_DIR（真实语料普查按需开启）。");
			return;
		}
		var cap = int.TryParse(Environment.GetEnvironmentVariable("ERACORE_CENSUS_LABELS"), out var c) && c > 0 ? c : 400;

		using var h = new LoaderTestHarness(gameDir);
		ConfigData.Current!.LoadConfig(gameDir);
		ConfigData.Current!.ApplyPostLoadEffects();
		await h.InitializeAsync();

		ForceReduceArgumentOnLoad();
		Assert.True(MinorShift.Emuera.Runtime.Config.Config.NeedReduceArgumentOnLoad, "NR 应已强开为 true");

		var dic = new LabelDictionary();
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, dic);
		Assert.True(ok, "NR=YES 二次 LoadErbDir failed:\n" + string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		var all = dic.GetAllLabels(true);
		var labels = all.Where(l => l != null).Take(cap).ToList();

		var report = new StringBuilder();
		var stats = RunCensus(report, labels, $"真实语料 {gameDir}（前 {labels.Count}/{all.Count} 个标签，eager，**NR 强开 YES**）");
		Emit(report, "real-nr");

		Assert.True(labels.Count > 0, "真实语料应至少解析出一个函数标签");
		Assert.True(stats.InstrLines > 100, $"真实语料应产出 >100 条指令行，实际 {stats.InstrLines}");
	}

	// ================= 普查主体 =================

	private sealed class CensusStats
	{
		internal int InstrLines, GotoLines, NullArgs, ArgPrimitiveKept, AssignDestKept;
		internal readonly List<string> NullArgDetail = [];
		internal readonly SortedDictionary<string, int> ArgTypes = new(StringComparer.Ordinal);
		internal readonly SortedDictionary<string, int> ExprTypes = new(StringComparer.Ordinal);
	}

	private CensusStats RunCensus(StringBuilder report, IEnumerable<FunctionLabelLine> labels, string title)
	{
		var stats = new CensusStats();
		var lineTypeCount = new SortedDictionary<string, int>(StringComparer.Ordinal);
		var argTypeCount = stats.ArgTypes;
		var exprTypeCount = stats.ExprTypes;
		var fieldOwner = new Dictionary<string, Type>(StringComparer.Ordinal);
		var fieldClass = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
		var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

		foreach (var label in labels)
		{
			for (LogicalLine? n = label.NextLine; n != null; n = n.NextLine)
			{
				if (n is NullLine || n is FunctionLabelLine)
					break;
				Bump(lineTypeCount, n.GetType().Name);

				if (n is GotoLabelLine)
				{
					stats.GotoLines++;
					ClassifyInstance(n, fieldOwner, fieldClass);
					continue;
				}
				if (n is not InstructionLine ins)
					continue;

				stats.InstrLines++;
				var arg = ins.Argument;
				Bump(argTypeCount, arg?.GetType().Name ?? "<null>");
				if (arg == null)
				{
					stats.NullArgs++;
					if (stats.NullArgDetail.Count < 10)
						stats.NullArgDetail.Add($"{ins.FunctionCode}@{ins.Position?.LineNo ?? -1}");
				}

				// 开放问题②：argprimitive / assigndest 是否已被 Pop 取空（只读判定，不改对象）
				if (GetField(ins, "argprimitive")?.GetValue(ins) != null)
					stats.ArgPrimitiveKept++;
				if (GetField(ins, "assigndest")?.GetValue(ins) != null)
					stats.AssignDestKept++;

				if (arg != null)
				{
					ClassifyInstance(arg, fieldOwner, fieldClass);
					WalkExpressions(arg, exprTypeCount, fieldOwner, fieldClass, visited);
				}
				ClassifyInstance(ins, fieldOwner, fieldClass, skip: ["Argument"]);
			}
		}

		report.AppendLine($"=== issue 22 S1 可编码性普查：{title} ===");
		report.AppendLine($"配置：NeedReduceArgumentOnLoad={MinorShift.Emuera.Runtime.Config.Config.NeedReduceArgumentOnLoad}"
			+ $" ReduceArgumentOnLoad={MinorShift.Emuera.Runtime.Config.Config.ReduceArgumentOnLoad}"
			+ $" 标签数={labels.Count()}");
		report.AppendLine($"行链：InstructionLine={stats.InstrLines} GotoLabelLine={stats.GotoLines}");
		report.AppendLine("行类型：" + Join(lineTypeCount));
		report.AppendLine("Argument 类型：" + Join(argTypeCount));
		report.AppendLine("表达式节点类型：" + Join(exprTypeCount));
		report.AppendLine($"开放问题② argprimitive：未取空={stats.ArgPrimitiveKept}（0 = 终态不含原始文本）");
		report.AppendLine($"开放问题② assigndest ：未取空={stats.AssignDestKept}（0 = 终态不含词表 LHS）");
		report.AppendLine($"Argument==null：{stats.NullArgs} 条"
			+ (stats.NullArgDetail.Count == 0 ? "" : "（前若干：" + string.Join(" ", stats.NullArgDetail) + "）"));
		report.AppendLine("字段分类（类型 → 类目:字段数）：");
		foreach (var (owner, classes) in fieldClass)
			report.AppendLine($"  {owner}: " + Join(classes));

		// 字段可编码面汇总：只在 Argument / AExpression 节点上统计（行/函数头类型另有口径）
		var aggregate = new SortedDictionary<string, int>(StringComparer.Ordinal);
		int nodeTypes = 0, nodeFields = 0;
		foreach (var (owner, classes) in fieldClass)
		{
			if (!fieldOwner.TryGetValue(owner, out var t) || !IsGraphNodeType(t))
				continue;
			nodeTypes++;
			foreach (var (cat, cnt) in classes)
			{
				aggregate[cat] = aggregate.TryGetValue(cat, out var v) ? v + cnt : cnt;
				nodeFields += cnt;
			}
		}
		report.AppendLine($"字段可编码面汇总（仅 Argument/AExpression，{nodeTypes} 个类型 / {nodeFields} 个字段）：" + Join(aggregate));
		return stats;
	}

	private void Emit(StringBuilder report, string tag)
	{
		var text = report.ToString();
		_out.WriteLine(text);
		var destPath = Environment.GetEnvironmentVariable("ERACORE_CENSUS_OUT");
		if (string.IsNullOrEmpty(destPath))
			return;
		// 两个测试都写同一环境变量 → 用 tag 区分文件名（s1-census.txt → s1-census.synthetic.txt）
		var path = Path.ChangeExtension(destPath, tag + Path.GetExtension(destPath));
		File.WriteAllText(path, text, Encoding.UTF8);
	}

	private static void ForceReduceArgumentOnLoad()
	{
		var cfg = ConfigData.Current!;
		cfg.GetConfigItem(ConfigCode.ReduceArgumentOnLoad).SetValue(ReduceArgumentOnLoadFlag.YES);
		cfg.CheckUpdate();
	}

	// ================= 反射辅助 =================

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

	private static void Bump(SortedDictionary<string, int> d, string k)
		=> d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

	private static string Join(SortedDictionary<string, int> d)
		=> string.Join(" ", d.Select(kv => $"{kv.Key}={kv.Value}"));

	private static FieldInfo? GetField(object o, string name)
		=> o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

	private static IEnumerable<FieldInfo> InstanceFields(Type t)
	{
		for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
			foreach (var f in cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				yield return f;
	}

	/// <summary>必须排除的运行期状态字段（三遍之外的执行期改写）。
	/// 注：`LoopEnd`/`LoopCounter`/`LoopStep` 是属性名，其底层字段为 `subData`/`cnt`/`step`
	/// （2026-09-18 普查实测纠正——按属性名排除会漏掉后两个）。</summary>
	private static readonly string[] RuntimeStateFields = ["LoopEnd", "LoopCounter", "LoopStep", "subData", "cnt", "step"];

	/// <summary>可由其它字段推导、不必序列化的字段。</summary>
	private static readonly string[] DerivedFields = ["transporter", "allArgIsConst", "argsPrimitive"];

	/// <summary>是否属「图节点」家族（Argument / AExpression / StrForm 派生）——用于字段可编码面汇总。
	/// 注：`CalledFunction` 是运行期绑定对象、不算节点本体，故不纳入汇总。</summary>
	private static bool IsGraphNodeType(Type t)
	{
		var n = t.Name;
		return n.Contains("Argument") || n.Contains("Term") || n.Contains("StrForm");
	}

	private static string Classify(Type ft)
	{
		if (ft.IsPrimitive || ft.IsEnum || ft == typeof(string) || ft == typeof(decimal))
			return "值";
		var n = ft.Name;
		if (n.Contains("VariableToken") || n.EndsWith("VariableData"))
			return "符号引用";
		if (n.Contains("FunctionMethod") || n.Contains("OperatorMethod") || n.Contains("FunctionIdentifier") || n.Contains("FunctionCode"))
			return "表引用";
		return "图节点";
	}

	private static void ClassifyInstance(object? o, Dictionary<string, Type> owner,
		SortedDictionary<string, SortedDictionary<string, int>> classes, string[]? skip = null)
	{
		if (o == null)
			return;
		var t = o.GetType();
		if (owner.ContainsKey(t.Name))
			return;
		owner[t.Name] = t;
		var bucket = new SortedDictionary<string, int>(StringComparer.Ordinal);
		foreach (var f in InstanceFields(t))
		{
			if (skip != null && skip.Contains(f.Name))
				continue;
			var cat = RuntimeStateFields.Contains(f.Name) ? "运行期状态"
				: DerivedFields.Contains(f.Name) ? "可重建"
				: Classify(f.FieldType);
			bucket[cat] = bucket.TryGetValue(cat, out var v) ? v + 1 : 1;
		}
		classes[t.Name] = bucket;
	}

	/// <summary>沿对象图递归收集表达式节点类型与字段归类（含数组 / 集合 / 私有字段）。</summary>
	private static void WalkExpressions(object? root, SortedDictionary<string, int> typeCount,
		Dictionary<string, Type> owner, SortedDictionary<string, SortedDictionary<string, int>> classes,
		HashSet<object> visited)
	{
		if (root == null || !visited.Add(root))
			return;

		var t = root.GetType();
		if (root is AExpression)
			Bump(typeCount, t.Name);
		ClassifyInstance(root, owner, classes);

		foreach (var f in InstanceFields(t))
		{
			if (RuntimeStateFields.Contains(f.Name) || DerivedFields.Contains(f.Name))
				continue;
			object? v;
			try
			{
				v = f.GetValue(root);
			}
			catch
			{
				continue;
			}
			if (v == null || v is string)
				continue;
			if (v is AExpression or Argument)
			{
				WalkExpressions(v, typeCount, owner, classes, visited);
				continue;
			}
			if (v is IEnumerable en)
			{
				foreach (var item in en)
					if (item is AExpression or Argument)
						WalkExpressions(item, typeCount, owner, classes, visited);
				continue;
			}
			var vn = v.GetType().Name;
			if (vn.Contains("StrForm") || vn.Contains("CalledFunction") || vn.Contains("Argument"))
				WalkExpressions(v, typeCount, owner, classes, visited);
		}
	}
}
