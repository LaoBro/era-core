using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// NativeAOT Android SIGSEGV 后续（handoff 第 3 步）：桌面并行未调用清扫的等价性语料。
///
/// 与既有 <see cref="ErbLoaderTests"/> 的 08 等价性用例的差异在判据强度：既有用例的
/// Signature 只有 `LabelName:Index:FileIndex:Depth`（标签元信息），并行清扫若写坏
/// InstructionLine 的跳转字段（JumpTo / JumpToEndCatch / IfCaseList / LoopEnd）——恰是
/// setJumpTo（ErbLoader.cs:3040）的写入面、也是崩溃的高危嫌疑——既有判据完全看不见。
/// 本套件的签名逐指令带上这些字段的解析结果，使「函数体被并行写坏」能被观测到。
///
/// 语料分两组，不可混写：TRYCALLLIST / TRYJUMPLIST 会置 useCallForm=true（ErbLoader.cs:3082），
/// 而该值决定走 callform 分支还是 else 分支（:1533），两分支的并行条件还不同。混入会让
/// 未调用清扫整体改道，覆盖变成假象。
/// </summary>
[Collection("LoaderTests")]
public class UncalledSweepEquivalenceTests
{
	// 规模 208：RunUncalledSweepParallel 的 chunk = Max(1, N / (MaxDOP * 16))，MaxDOP = Min(ProcessorCount, 8)。
	// N > 128 才能让分区粒度真正拉开（N 小时 chunk 恒为 1，分区行为与真实大游戏差异过大）。
	private const int UncalledCount = 208;
	private const int FileCount = 16;
	private const int PerFile = UncalledCount / FileCount; // 13

	// ---------------- 语料 ----------------

	/// <summary>A 组（else 分支）：不得出现 TRYCALLLIST / TRYJUMPLIST / CALLFORM 系指令。</summary>
	private static void WriteCorpusA(LoaderTestHarness h, bool tamper)
	{
		var root = new StringBuilder();
		root.Append("@EVENTFIRST\n");
		root.Append("CALL CALLED_ROOT\n");
		root.Append("PRINTFORML root\n");
		root.Append("RETURN\n\n");
		root.Append("@CALLED_ROOT\n");
		root.Append("#LOCALSIZE 4\n");
		root.Append("PRINTFORML called\n");
		root.Append("RETURN\n\n");
		h.WriteErb("000_ROOT.ERB", root.ToString());
		WriteUncalledFiles(h, tamper);
	}

	/// <summary>B 组（callform 分支）：TRYCALLLIST 置 useCallForm=true，未调用清扫改走 callform 分支。</summary>
	private static void WriteCorpusB(LoaderTestHarness h, bool tamper)
	{
		h.WriteErb("000_ROOT.ERB",
			"@EVENTTRAIN\nTRYCALLLIST\n\tFUNC FOO\nENDFUNC\nRETURN\n");
		WriteUncalledFiles(h, tamper);
	}

	private static void WriteUncalledFiles(LoaderTestHarness h, bool tamper)
	{
		for (int f = 0; f < FileCount; f++)
		{
			var sb = new StringBuilder();
			for (int k = 0; k < PerFile; k++)
				sb.Append(MakeUncalled(f * PerFile + k));
			var text = sb.ToString();
			// 区分能力自检用：抽掉 UNCALLED_000 的行标签，使 GOTO SKIP_000 失去目标。
			// 会同时改变指令链、跳转解析结果与后续行号，签名必然不同。
			if (tamper && f == 0)
				text = text.Replace("$SKIP_000\n", "", StringComparison.Ordinal);
			h.WriteErb($"U_{f:D2}.ERB", text);
		}
	}

	/// <summary>
	/// 单个未调用函数：私有变量（#DIM/#DIMS）+ 轮换的控制流结构 + 函数内 GOTO 跳转。
	/// 控制流轮换覆盖 nestCheck / setJumpTo 的各类跳转写入面；GOTO + $行标签是 JumpTo 的直接靶子。
	/// </summary>
	private static string MakeUncalled(int i)
	{
		var b = new StringBuilder();
		b.Append("@UNCALLED_").Append(i.ToString("D3")).Append('\n');
		b.Append("#LOCALSIZE 12\n");
		b.Append("#LOCALSSIZE 4\n");
		b.Append("#DIM LNUM_").Append(i.ToString("D3")).Append('\n');
		b.Append("#DIMS LSTR_").Append(i.ToString("D3")).Append('\n');
		string n = "LNUM_" + i.ToString("D3");
		switch (i % 6)
		{
			case 0:
				b.Append("IF ").Append(n).Append(" == 0\n PRINTFORML zero\nELSEIF ").Append(n)
				 .Append(" == 1\n PRINTFORML one\nELSE\n PRINTFORML other\nENDIF\n");
				break;
			case 1:
				b.Append("FOR COUNT, 0, 4\n PRINTFORML loop\nNEXT\n");
				break;
			case 2:
				b.Append("REPEAT 3\n PRINTFORML rep\nREND\n");
				break;
			case 3:
				b.Append("WHILE ").Append(n).Append(" < 3\n ").Append(n).Append(" += 1\nWEND\n");
				break;
			case 4:
				b.Append("SELECTCASE ").Append(n)
				 .Append("\n CASE 0\n  PRINTFORML c0\n CASE 1\n  PRINTFORML c1\n CASEELSE\n  PRINTFORML cx\nENDSELECT\n");
				break;
			default:
				b.Append("IF ").Append(n)
				 .Append(" >= 0\n FOR COUNT, 0, 2\n  IF COUNT == 1\n   PRINTFORML inner\n  ENDIF\n NEXT\nENDIF\n");
				break;
		}
		b.Append("GOTO SKIP_").Append(i.ToString("D3")).Append('\n');
		b.Append("PRINTFORML dead\n");
		b.Append("$SKIP_").Append(i.ToString("D3")).Append('\n');
		b.Append("PRINTFORML tail\n");
		b.Append("RETURN\n\n");
		return b.ToString();
	}

	// ---------------- 加载与签名 ----------------

	/// <summary>
	/// 以给定并行开关加载一遍语料，返回强签名。每次调用用独立 harness——避免首次加载的
	/// 副作用（Preload 缓存消费、LabelDictionary 残留）流入第二次。
	/// </summary>
	private static async Task<List<string>> LoadAndSignAsync(
		Action<LoaderTestHarness, bool> writeCorpus, bool parallelParseScript, bool tamper = false)
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// 隔离变量：ERB 生成/合并保持串行，只让 ParseScript 未调用清扫成为唯一差异源。
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(parallelParseScript);
		// else 分支要求 IgnoreUncalledFunction=false 才跑并行解析体（ErbLoader.cs:1574）。
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);

		writeCorpus(h, tamper);
		await h.PreloadAsync();

		// scan-line sink 必须真实写入 Process.scaningLine（对齐 Process.cs:627 的 `line => process.scaningLine = line`）。
		// 若沿用其他 loader 单测的 `_ => { }` 空实现，串行路径的 GetLoadScaningLine() 恒为 null、
		// 回落 Process.GetScaningLine() 仍是 null，私有变量一律被误判「この関数中では定義されていません」
		//（ErbLoader.cs:2064 已记录该误报）；并行路径走 ThreadLocal SweepScanLine 不受影响——
		// 于是会造出「串行报错、并行不报」的假差异。语料含 #DIM 私有变量时必现。
		// 须载入 h.LabelDic：setJumpTo 的子层 Depth 解析经 GlobalStatic.LabelDictionary。
		var ok = await new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line)
			.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "LoadErbDir failed\n"
			+ string.Join("\n", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		return StrongSignature(h.LabelDic);
	}

	private static List<string> StrongSignature(LabelDictionary dic)
	{
		var sig = new List<string>();
		// 按名排序保证确定性（并行完成顺序不影响输出）。
		foreach (var label in dic.GetAllLabels(true).OrderBy(l => l.LabelName, StringComparer.Ordinal))
		{
			var parts = new List<string>();
			var node = label.NextLine;
			while (node != null)
			{
				if (node is NullLine || node is FunctionLabelLine)
					break;
				parts.Add(DescribeLine(node));
				node = node.NextLine;
			}
			sig.Add($"{label.LabelName}|idx={label.Index}|depth={label.Depth}"
				+ $"|stub={(label.IsStub ? 1 : 0)}|err={(label.IsError ? 1 : 0)}"
				+ "|" + string.Join(" ; ", parts));
		}
		return sig;
	}

	/// <summary>
	/// 单条指令的签名。除 FunctionCode@行号 外，带上跳转写入面的解析结果：
	/// J=JumpTo / E=JumpToEndCatch / LE=LoopEnd / C=IfCaseList / ERR=IsError。
	/// </summary>
	private static string DescribeLine(LogicalLine node)
	{
		int ln = node.Position?.LineNo ?? -1;
		if (node is GotoLabelLine)
			return "LABEL@" + ln;
		if (node is InstructionLine ins)
		{
			var sb = new StringBuilder();
			sb.Append(ins.FunctionCode).Append('@').Append(ln);
			sb.Append(":J=").Append(RefOf(ins.JumpTo));
			sb.Append(":E=").Append(RefOf(ins.JumpToEndCatch));
			if (ins.LoopEnd != 0)
				sb.Append(":LE=").Append(ins.LoopEnd);
			var cases = ins.IfCaseList;
			if (cases != null && cases.Count > 0)
			{
				sb.Append(":C=[");
				bool first = true;
				foreach (var c in cases)
				{
					if (!first) sb.Append(',');
					sb.Append(RefOf(c));
					first = false;
				}
				sb.Append(']');
			}
			if (ins.IsError)
				sb.Append(":ERR=").Append(ins.ErrMes);
			return sb.ToString();
		}
		return node.GetType().Name + "@" + ln;
	}

	private static string RefOf(LogicalLine? line)
		=> line == null ? "-" : (line.Position?.LineNo ?? -2).ToString();

	// ---------------- 断言辅助 ----------------

	private static void AssertSignaturesEqual(List<string> expected, List<string> actual, string what)
	{
		if (expected.Count != actual.Count)
			Assert.Fail($"{what}: 签名条数不同 serial={expected.Count} parallel={actual.Count}");
		for (int i = 0; i < expected.Count; i++)
		{
			if (expected[i] == actual[i])
				continue;
			Assert.Fail($"{what}: 第 {i} 条签名不同\n  serial  : {expected[i]}\n  parallel: {actual[i]}");
		}
	}

	/// <summary>统计解析成功的跳转目标数（":J=" 后非 '-'）。用于 sanity：语料确实产生了跳转解析。</summary>
	private static int CountResolvedJumps(List<string> sig)
	{
		int n = 0;
		foreach (var s in sig)
		{
			int i = 0;
			while ((i = s.IndexOf(":J=", i, StringComparison.Ordinal)) >= 0)
			{
				int p = i + 3;
				if (p < s.Length && s[p] != '-')
					n++;
				i = p;
			}
		}
		return n;
	}

	/// <summary>
	/// sanity：语料规模生效，且未调用函数体确实被解析（不作为桩跳过）。
	/// 缺了这一步，语料写错时两次加载会「一致地什么都没解析」，测试假绿。
	/// </summary>
	private static void AssertCorpusEffective(List<string> sig, string what)
	{
		Assert.True(sig.Count >= UncalledCount,
			$"{what}: 签名条数 {sig.Count} 少于语料函数数 {UncalledCount}，语料可能未装载");
		Assert.Contains(sig, s => s.Contains("PRINTFORML@", StringComparison.Ordinal)
			&& !s.Contains("|stub=1", StringComparison.Ordinal));
		int jumps = CountResolvedJumps(sig);
		Assert.True(jumps > 100,
			$"{what}: 解析出的跳转目标仅 {jumps} 个，控制流/行标签语料可能未生效");
	}

	// ---------------- 测试 ----------------

	/// <summary>
	/// A 组（else 分支，无 callform 系）：208 个未调用函数 + 循环/条件/嵌套控制流 + 私有变量 +
	/// 函数内 GOTO，并行清扫与串行清扫的函数体（含跳转字段）必须逐字一致。
	/// </summary>
	[Fact]
	public async Task UncalledSweep_parallel_matches_serial_else_branch()
	{
		var serial = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: false);
		var parallel = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: true);

		AssertCorpusEffective(serial, "else 分支");
		AssertSignaturesEqual(serial, parallel, "else 分支（无 callform 系）");
	}

	/// <summary>
	/// B 组（callform 分支）：TRYCALLLIST 置 useCallForm=true，未调用清扫走 callform 分支并行解析。
	/// 该分支同时是 useCallForm 共享实例字段（ref 写入）的靶场。
	/// </summary>
	[Fact]
	public async Task UncalledSweep_parallel_matches_serial_callform_branch()
	{
		var serial = await LoadAndSignAsync(WriteCorpusB, parallelParseScript: false);
		var parallel = await LoadAndSignAsync(WriteCorpusB, parallelParseScript: true);

		AssertCorpusEffective(serial, "callform 分支");
		AssertSignaturesEqual(serial, parallel, "callform 分支（TRYCALLLIST）");
	}

	/// <summary>
	/// 对照一（分诊用）：同一开关连跑两次必须完全一致。
	/// 上面的等价性用例是「先 serial 后 parallel」，两次加载共用进程；若 GlobalStatic 存在跨 harness
	/// 残留或首次加载预热效应，观察到的差异就会是「第一次 vs 第二次」而非「串行 vs 并行」。
	/// 本用例绿 ⇒ 差异可归因于并行开关本身；红 ⇒ 加载非幂等，等价性用例的结论不成立。
	/// </summary>
	[Fact]
	public async Task Serial_load_is_idempotent_across_harnesses()
	{
		var first = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: false);
		var second = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: false);
		AssertSignaturesEqual(first, second, "串行两次加载（对照：排除顺序/静态残留）");
	}

	/// <summary>对照二：并行连跑两次自身必须稳定（并行调度顺序不固定，需确认它自身是确定的）。</summary>
	[Fact]
	public async Task Parallel_load_is_idempotent_across_harnesses()
	{
		var first = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: true);
		var second = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: true);
		AssertSignaturesEqual(first, second, "并行两次加载（对照）");
	}

	/// <summary>
	/// 区分能力自检：抽掉一处行标签后签名必须改变。
	/// 没有这一步，签名函数若退化成常量，上面两个用例会「永远一致」地假绿。
	/// </summary>
	[Fact]
	public async Task Strong_signature_detects_tampered_jump_target()
	{
		var clean = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: false, tamper: false);
		var tampered = await LoadAndSignAsync(WriteCorpusA, parallelParseScript: false, tamper: true);

		Assert.NotEqual(clean, tampered);
	}
}
