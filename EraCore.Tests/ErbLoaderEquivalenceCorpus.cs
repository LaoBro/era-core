using System;
using System.Text;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ERB 装载等价性的**共享语料基座**（issue 01 提炼：原本分别内联在
/// <see cref="UncalledSweepEquivalenceTests"/> 与 <see cref="ErbLoaderTests"/> 的 private static 方法里）。
/// <para>
/// 提炼动因：黄金输出快照（issue 01）必须以**既有语料**为基座，「未新建语料」；
/// 复制一份内联语料会造出两个会漂移的真相源。故此处只做**零行为变化**的搬动，两个既有套件改为调用本类。
/// </para>
/// <para>
/// 语料分三组，不可混写：A/B 组覆盖 <c>useCallForm</c> 两条分支（TRYCALLLIST 会置 true，
/// 两分支的并行条件不同，混入会让未调用清扫整体改道、覆盖变成假象）；FNF 组覆盖
/// 「函数未定义 + ONCE 去重 + 未调用函数真解析」的警告路径。
/// </para>
/// </summary>
internal static class ErbLoaderEquivalenceCorpus
{
	// 规模 208：RunUncalledSweepParallel 的 chunk = Max(1, N / (MaxDOP * 16))，MaxDOP = Min(ProcessorCount, 8)。
	// N > 128 才能让分区粒度真正拉开（N 小时 chunk 恒为 1，分区行为与真实大游戏差异过大）。
	internal const int UncalledCount = 208;
	internal const int FileCount = 16;
	internal const int PerFile = UncalledCount / FileCount; // 13

	/// <summary>A 组（else 分支）：不得出现 TRYCALLLIST / TRYJUMPLIST / CALLFORM 系指令。</summary>
	internal static void WriteCorpusA(LoaderTestHarness h, bool tamper)
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
	internal static void WriteCorpusB(LoaderTestHarness h, bool tamper)
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
	internal static string MakeUncalled(int i)
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

	/// <summary>
	/// FNF/FNC 警告语料（原 <c>ErbLoaderTests.WriteUncalledSweepCorpus</c>）：未调用函数真解析
	///（IgnoreUncalledFunction:NO）+ FNC ONCE + FNF ONCE + nestCheck 警告。
	/// 注意 harness 语义：CALL 解析查 GlobalStatic.LabelDictionary（与传入 labelDic 分离），语料不可 CALL 已定义函数。
	/// </summary>
	internal static void WriteFnfWarningCorpus(LoaderTestHarness h)
	{
		h.WriteErb("A.ERB", "@EVENTTRAIN\nRETURN\n"
			+ "@UNCALLED_A\nBREAK\nCALL NOEXIST_FUNC\nRETURN\n");
		// B 文件内两个 FNF：首个发射、同文件第二个被 ONCE 去重（ignoredFNFWarningCount 路径）
		h.WriteErb("B.ERB", "@UNCALLED_B\nCALL NOEXIST_FUNC_B\nCALL NOEXIST_FUNC_B2\nBREAK\nRETURN\n");
	}
}
