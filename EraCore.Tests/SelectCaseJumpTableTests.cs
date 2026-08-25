using System;
using System.Collections.Generic;
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
/// SELECTCASE コンパイル時ジャンプテーブル（.scratch/selectcase-jumptable/spec.md）の xUnit 专项。
///
/// 検証範囲：
/// 1. 建表条件——全 Normal・定数・型一致・キー重複なし・規模閾値（int≥8 / string≥4）を満たすブロックのみテーブルを構築。
/// 2. テーブル内容——キー→CASE 行のマッピング、DefaultTarget（CASEELSE / ENDSELECT）。
/// 3. fast path 派発 == 線形スキャン（フォールバック）== eager 全量、CASEELSE 兜底・無一致→ENDSELECT。
/// 4. 不合格ブロック（TO / IS / 変数 / 重複キー / 型不一致）→ テーブル null、挙動は現状（線形スキャン）と一致。
/// 5. 惰性——物化関数は自動建表、未呼び出し桩はボディ無し（テーブル無し）。
///
/// 実行接続：select 式に RESULT（int）/ RESULTS（string）を使い、h.VEvaluator の setter で値を設定してから
/// h.Process.DoDebugNormalFunction で SELECTCASE 行の DoInstruction を直接実行し、state.CurrentLine（JumpTo の結果）を検証する。
/// </summary>
[Collection("LoaderTests")]
public class SelectCaseJumpTableTests
{
	// ---------- 語料生成 ----------

	private static string IntCorpus(int keys, bool withElse = true, string name = "SC_INT")
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine("@" + name);
		sb.AppendLine("\tSELECTCASE RESULT");
		for (int i = 0; i < keys; i++)
		{
			sb.AppendLine($"\tCASE {i}");
			sb.AppendLine($"\t\tPRINT {i}");
		}
		if (withElse)
		{
			sb.AppendLine("\tCASEELSE");
			sb.AppendLine("\t\tPRINT 99");
		}
		sb.AppendLine("\tENDSELECT");
		sb.AppendLine("\tRETURN");
		return sb.ToString();
	}

	private static string StrCorpus(int keys, bool withElse = true, string name = "SC_STR")
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine("@" + name);
		sb.AppendLine("\tSELECTCASE RESULTS");
		for (int i = 0; i < keys; i++)
		{
			sb.AppendLine($"\tCASE \"{(char)('a' + i)}\"");
			sb.AppendLine($"\t\tPRINT {(char)('a' + i)}");
		}
		if (withElse)
		{
			sb.AppendLine("\tCASEELSE");
			sb.AppendLine("\t\tPRINT z");
		}
		sb.AppendLine("\tENDSELECT");
		sb.AppendLine("\tRETURN");
		return sb.ToString();
	}

	private const string CorpusTo = """
@SC_TO
	SELECTCASE RESULT
	CASE 0
		PRINT 0
	CASE 5 TO 10
		PRINT range
	CASE 20
		PRINT 20
	CASEELSE
		PRINT 99
	ENDSELECT
	RETURN
""";

	private const string CorpusIs = """
@SC_IS
	SELECTCASE RESULT
	CASE 0
		PRINT 0
	CASE IS > 5
		PRINT gt5
	CASEELSE
		PRINT 99
	ENDSELECT
	RETURN
""";

	private const string CorpusVar = """
@SC_VAR
	SELECTCASE RESULT
	CASE 0
		PRINT 0
	CASE (RESULT + 1)
		PRINT expr
	CASE 20
		PRINT 20
	ENDSELECT
	RETURN
""";

	private const string CorpusDup = """
@SC_DUP
	SELECTCASE RESULT
	CASE 1
		PRINT a
	CASE 1
		PRINT b
	CASE 2
		PRINT c
	CASEELSE
		PRINT 99
	ENDSELECT
	RETURN
""";

	private const string CorpusMismatch = """
@SC_MM
	SELECTCASE RESULT
	CASE 0
		PRINT 0
	CASE "a"
		PRINT a
	CASE 20
		PRINT 20
	ENDSELECT
	RETURN
""";

	private static string LazyCorpus()
	{
		var sb = new System.Text.StringBuilder();
		sb.AppendLine("@EVENTFIRST");
		sb.AppendLine("\tCALL CALLED");
		sb.AppendLine("\tRETURN");
		sb.AppendLine();
		sb.AppendLine("@CALLED");
		sb.AppendLine("\tSELECTCASE RESULT");
		for (int i = 0; i < 10; i++)
		{
			sb.AppendLine($"\tCASE {i}");
			sb.AppendLine($"\t\tPRINT {i}");
		}
		sb.AppendLine("\tENDSELECT");
		sb.AppendLine("\tRETURN");
		sb.AppendLine();
		sb.AppendLine("@UNCALLED");
		sb.AppendLine("\tSELECTCASE RESULT");
		for (int i = 0; i < 10; i++)
		{
			sb.AppendLine($"\tCASE {i}");
			sb.AppendLine($"\t\tPRINT {i}");
		}
		sb.AppendLine("\tENDSELECT");
		sb.AppendLine("\tRETURN");
		return sb.ToString();
	}

	// ---------- ヘルパー ----------

	private static async Task<LoaderTestHarness> LoadCorpusAsync(string erbName, string corpus)
	{
		var h = new LoaderTestHarness();
		try
		{
			await h.InitializeAsync();
			ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
			ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
			// 默认 IgnoreUncalledFunction=true 时未调用函数（Depth=-1）不跑三遍解析（nestCheck 不执行、
			// Argument 保持 null、跳转表不建）。测试语料是独立函数，须关闭该配置才能覆盖 eager 全量语义。
			ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
			await h.PreloadAsync();
			h.WriteErb(erbName, corpus);
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
			if (!ok)
			{
				var lines = string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? ""));
				h.Dispose();
				throw new InvalidOperationException("LoadErbDir failed: " + lines);
			}
			return h;
		}
		catch
		{
			h.Dispose();
			throw;
		}
	}

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
		throw new InvalidOperationException("label not found: " + name);
	}

	/// <summary>ラベル直後の行チェーンから最初の SELECTCASE 行を探す。</summary>
	private static InstructionLine FirstSelectCase(FunctionLabelLine label)
	{
		LogicalLine? n = label.NextLine;
		while (n != null)
		{
			if (n is NullLine || n is FunctionLabelLine)
				break;
			if (n is InstructionLine ins && ins.FunctionCode == FunctionCode.SELECTCASE)
				return ins;
			n = n.NextLine;
		}
		throw new InvalidOperationException("SELECTCASE not found");
	}

	/// <summary>SELECTCASE 直後の CASE / CASEELSE / ENDSELECT 行を順に収集（本文行はスキップ）。</summary>
	private static List<InstructionLine> CaseLines(InstructionLine selectCase)
	{
		var list = new List<InstructionLine>();
		LogicalLine? n = selectCase.NextLine;
		while (n != null)
		{
			if (n is NullLine || n is FunctionLabelLine)
				break;
			if (n is InstructionLine ins &&
				(ins.FunctionCode == FunctionCode.CASE || ins.FunctionCode == FunctionCode.CASEELSE || ins.FunctionCode == FunctionCode.ENDSELECT))
				list.Add(ins);
			n = n.NextLine;
		}
		return list;
	}

	private static void DispatchInt(LoaderTestHarness h, InstructionLine sc, long value)
	{
		h.VEvaluator.RESULT = value;
		h.Process.DoDebugNormalFunction(sc, false);
	}

	private static void DispatchStr(LoaderTestHarness h, InstructionLine sc, string value)
	{
		h.VEvaluator.RESULTS = value;
		h.Process.DoDebugNormalFunction(sc, false);
	}

	// ---------- 1. 可建表 int 块 ----------

	/// <summary>int ブロック（10 鍵 ≥ 閾値 8）：テーブル構築・内容・O(1) 派発（命中 / CASEELSE 兜底）。</summary>
	[Fact]
	public async Task Qualified_int_block_builds_table_and_dispatches()
	{
		using var h = await LoadCorpusAsync("SCJ1.ERB", IntCorpus(10));
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_INT"));
		var cases = CaseLines(sc); // [CASE 0..9, CASEELSE, ENDSELECT]
		Assert.Equal(12, cases.Count);

		var table = sc.SelectCaseJumpTable;
		Assert.NotNull(table);
		for (int i = 0; i < 10; i++)
			Assert.Same(cases[i], table!.IntTable![i]);
		Assert.Same(cases[10], table!.DefaultTarget); // CASEELSE

		// 命中キー → 該当 CASE 行
		DispatchInt(h, sc, 5);
		Assert.Same(cases[5], h.Process.getCurrentState.CurrentLine);
		// 未ヒット → CASEELSE
		DispatchInt(h, sc, 100);
		Assert.Same(cases[10], h.Process.getCurrentState.CurrentLine);
	}

	/// <summary>CASEELSE 無し int ブロック：DefaultTarget = ENDSELECT、未ヒット→ENDSELECT。</summary>
	[Fact]
	public async Task Qualified_int_block_without_else_defaults_to_endselect()
	{
		using var h = await LoadCorpusAsync("SCJ2.ERB", IntCorpus(10, withElse: false));
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_INT"));
		var cases = CaseLines(sc); // [CASE 0..9, ENDSELECT]
		Assert.Equal(11, cases.Count);

		var table = sc.SelectCaseJumpTable;
		Assert.NotNull(table);
		Assert.Same(cases[10], table!.DefaultTarget); // ENDSELECT

		DispatchInt(h, sc, 12345);
		Assert.Same(cases[10], h.Process.getCurrentState.CurrentLine);
	}

	// ---------- 2. 可建表 string 块 ----------

	/// <summary>string ブロック（4 鍵 = 閾値）：Ordinal 比較でテーブル構築・大小文字区別（"B" ≠ "b"）を検証。</summary>
	[Fact]
	public async Task Qualified_string_block_builds_ordinal_table_and_dispatches()
	{
		using var h = await LoadCorpusAsync("SCJ3.ERB", StrCorpus(4));
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_STR"));
		var cases = CaseLines(sc); // [CASE a..d, CASEELSE, ENDSELECT]
		Assert.Equal(6, cases.Count);

		var table = sc.SelectCaseJumpTable;
		Assert.NotNull(table);
		Assert.Same(cases[0], table!.StrTable!["a"]);
		Assert.Same(cases[1], table!.StrTable!["b"]);
		Assert.Same(cases[3], table!.StrTable!["d"]);
		Assert.Same(cases[4], table!.DefaultTarget); // CASEELSE

		DispatchStr(h, sc, "c");
		Assert.Same(cases[2], h.Process.getCurrentState.CurrentLine);
		// ordinal（string == と同じ）：大文字は不一致 → CASEELSE
		DispatchStr(h, sc, "B");
		Assert.Same(cases[4], h.Process.getCurrentState.CurrentLine);
		DispatchStr(h, sc, "zz");
		Assert.Same(cases[4], h.Process.getCurrentState.CurrentLine);
	}

	// ---------- 3. 不合格块：null フォールバック、挙動は現状と一致 ----------

	/// <summary>TO（範囲）を含む → テーブル null、線形スキャンで正しく範囲判定。</summary>
	[Fact]
	public async Task To_case_falls_back_to_linear_scan()
	{
		using var h = await LoadCorpusAsync("SCJ4.ERB", CorpusTo);
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_TO"));
		var cases = CaseLines(sc); // [CASE 0, CASE 5 TO 10, CASE 20, CASEELSE, ENDSELECT]
		Assert.Null(sc.SelectCaseJumpTable);

		DispatchInt(h, sc, 7); // 範囲ヒット
		Assert.Same(cases[1], h.Process.getCurrentState.CurrentLine);
		DispatchInt(h, sc, 20);
		Assert.Same(cases[2], h.Process.getCurrentState.CurrentLine);
		DispatchInt(h, sc, 100); // CASEELSE 兜底
		Assert.Same(cases[3], h.Process.getCurrentState.CurrentLine);
	}

	/// <summary>IS（比較）を含む → テーブル null、線形スキャンで正しく比較。</summary>
	[Fact]
	public async Task Is_case_falls_back_to_linear_scan()
	{
		using var h = await LoadCorpusAsync("SCJ5.ERB", CorpusIs);
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_IS"));
		var cases = CaseLines(sc); // [CASE 0, CASE IS > 5, CASEELSE, ENDSELECT]
		Assert.Null(sc.SelectCaseJumpTable);

		DispatchInt(h, sc, 9); // 9 > 5
		Assert.Same(cases[1], h.Process.getCurrentState.CurrentLine);
		DispatchInt(h, sc, 3); // 不一致 → CASEELSE
		Assert.Same(cases[2], h.Process.getCurrentState.CurrentLine);
	}

	/// <summary>非定数（変数式）を含む → テーブル null、線形スキャンで正しく判定。</summary>
	[Fact]
	public async Task Non_const_case_falls_back_to_linear_scan()
	{
		using var h = await LoadCorpusAsync("SCJ6.ERB", CorpusVar);
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_VAR"));
		var cases = CaseLines(sc); // [CASE 0, CASE (RESULT+1), CASE 20, ENDSELECT]
		Assert.Null(sc.SelectCaseJumpTable);

		DispatchInt(h, sc, 0); // 最初の CASE 0 に命中（式は評価されない＝決定的）
		Assert.Same(cases[0], h.Process.getCurrentState.CurrentLine);
		DispatchInt(h, sc, 20); // RESULT+1=21 ≠ 20 → 最後の CASE 20
		Assert.Same(cases[2], h.Process.getCurrentState.CurrentLine);
	}

	/// <summary>重複キー → テーブル null、線形スキャンの first-wins を保持。</summary>
	[Fact]
	public async Task Duplicate_key_falls_back_to_linear_scan()
	{
		using var h = await LoadCorpusAsync("SCJ7.ERB", CorpusDup);
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_DUP"));
		var cases = CaseLines(sc); // [CASE 1, CASE 1, CASE 2, CASEELSE, ENDSELECT]
		Assert.Null(sc.SelectCaseJumpTable);

		DispatchInt(h, sc, 1); // first-wins：最初の CASE 1
		Assert.Same(cases[0], h.Process.getCurrentState.CurrentLine);
		DispatchInt(h, sc, 2);
		Assert.Same(cases[2], h.Process.getCurrentState.CurrentLine);
	}

	/// <summary>型不一致（int select に string CASE）→ テーブル null、線形スキャンの挙動（実行時型判定）を保持。</summary>
	[Fact]
	public async Task Type_mismatch_falls_back_to_linear_scan()
	{
		using var h = await LoadCorpusAsync("SCJ8.ERB", CorpusMismatch);
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_MM"));
		var cases = CaseLines(sc); // [CASE 0, CASE "a", CASE 20, ENDSELECT]
		Assert.Null(sc.SelectCaseJumpTable);

		DispatchInt(h, sc, 0);
		Assert.Same(cases[0], h.Process.getCurrentState.CurrentLine);
		DispatchInt(h, sc, 20); // CASE "a" は int 比較で不一致 → 最後の CASE 20
		Assert.Same(cases[2], h.Process.getCurrentState.CurrentLine);
	}

	// ---------- 4. 規模閾値 ----------

	/// <summary>int 3 鍵（< 8）：テーブル無し、線形スキャンで派発は正しい。</summary>
	[Fact]
	public async Task Below_int_threshold_no_table_but_dispatch_correct()
	{
		using var h = await LoadCorpusAsync("SCJ9.ERB", IntCorpus(3));
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_INT"));
		var cases = CaseLines(sc); // [CASE 0..2, CASEELSE, ENDSELECT]
		Assert.Null(sc.SelectCaseJumpTable);

		DispatchInt(h, sc, 1);
		Assert.Same(cases[1], h.Process.getCurrentState.CurrentLine);
		DispatchInt(h, sc, 77); // CASEELSE
		Assert.Same(cases[3], h.Process.getCurrentState.CurrentLine);
	}

	/// <summary>string 3 鍵（< 4）：テーブル無し、線形スキャンで派発は正しい。</summary>
	[Fact]
	public async Task Below_str_threshold_no_table_but_dispatch_correct()
	{
		using var h = await LoadCorpusAsync("SCJ10.ERB", StrCorpus(3));
		var sc = FirstSelectCase(Find(h.LabelDic, "SC_STR"));
		var cases = CaseLines(sc); // [CASE a..c, CASEELSE, ENDSELECT]
		Assert.Null(sc.SelectCaseJumpTable);

		DispatchStr(h, sc, "b");
		Assert.Same(cases[1], h.Process.getCurrentState.CurrentLine);
		DispatchStr(h, sc, "zz"); // CASEELSE
		Assert.Same(cases[3], h.Process.getCurrentState.CurrentLine);
	}

	// ---------- 5. 惰性 ----------

	/// <summary>惰性：被呼び出し関数は物化→自動建表・派発 OK；未呼び出しは桩のまま（テーブル無し）；物化後は eager と同じ行へ派発。</summary>
	[Fact]
	public async Task Lazy_materialized_functions_build_table_stubs_do_not()
	{
		// lazy 側
		LoaderTestHarness? h = null;
		List<InstructionLine> lazyCases = null!;
		try
		{
			h = new LoaderTestHarness();
			await h.InitializeAsync();
			ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
			ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
			ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
			await h.PreloadAsync();
			h.WriteErb("SCL.ERB", LazyCorpus());
			var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
			if (!await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic))
				throw new InvalidOperationException("lazy LoadErbDir failed");

			var called = Find(h.LabelDic, "CALLED");
			var uncalled = Find(h.LabelDic, "UNCALLED");
			Assert.False(called.IsStub, "CALLED should be materialized (call target)");
			Assert.True(uncalled.IsStub, "UNCALLED should remain a stub");

			// 物化関数は自動建表（ReparseFunction → 同一 nestCheck 経路）
			var scCalled = FirstSelectCase(called);
			Assert.NotNull(scCalled.SelectCaseJumpTable);
			lazyCases = CaseLines(scCalled);
			DispatchInt(h, scCalled, 3);
			Assert.Same(lazyCases[3], h.Process.getCurrentState.CurrentLine);

			// 未呼び出し桩の物化 → 建表・派発
			ErbLoader.MaterializeOnRuntime(uncalled);
			Assert.False(uncalled.IsStub);
			var scUncalled = FirstSelectCase(uncalled);
			Assert.NotNull(scUncalled.SelectCaseJumpTable);
			var uncalledCases = CaseLines(scUncalled);
			DispatchInt(h, scUncalled, 7);
			Assert.Same(uncalledCases[7], h.Process.getCurrentState.CurrentLine);
		}
		finally
		{
			h?.Dispose();
		}

		// eager 对照：同一語料 → 同じ関数も建表・同じ行（LineNo）へ派発
		using (var h2 = await LoadCorpusAsync("SCLE.ERB", LazyCorpus()))
		{
			var sc2 = FirstSelectCase(Find(h2.LabelDic, "CALLED"));
			Assert.NotNull(sc2.SelectCaseJumpTable);
			DispatchInt(h2, sc2, 3);
			Assert.Equal(lazyCases[3].Position!.Value.LineNo, h2.Process.getCurrentState.CurrentLine.Position!.Value.LineNo);
		}
	}
}
