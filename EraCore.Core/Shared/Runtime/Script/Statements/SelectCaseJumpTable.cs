using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>
/// SELECTCASE コンパイル時ジャンプテーブル。
/// 全 CASE が Normal・定数・型一致・キー重複なしの場合のみ構築され、実行時に O(1) ディスパッチを行う。
/// いずれかの条件を満たさない場合は null のまま（従来の線形スキャンにフォールバック）＝ゼロ挙動回帰。
/// </summary>
internal sealed class SelectCaseJumpTable
{
	/// <summary>int SELECTCASE 用テーブル（この型のときのみ非 null）。</summary>
	public Dictionary<long, InstructionLine>? IntTable;

	/// <summary>
	/// string SELECTCASE 用テーブル（この型のときのみ非 null）。
	/// 比較は ordinal（C# の string == と同義）に合わせる。Config.StrComper / SCExpression は To 型の比較のみに効くため使わない。
	/// </summary>
	public Dictionary<string, InstructionLine>? StrTable;

	/// <summary>最初の CASEELSE、無ければ ENDSELECT。ヒットなし時のジャンプ先。</summary>
	public InstructionLine DefaultTarget = null!;

	/// <summary>int ブロックの建表閾値（分岐数＝定数値の総数）。中央値の小ブロックは辞書より線形スキャンが有利なため。</summary>
	private const int MinIntBranchCount = 8;

	/// <summary>string ブロックの建表閾値（文字列比較は高コストなので低め）。</summary>
	private const int MinStrBranchCount = 4;

	/// <summary>
	/// ENDSELECT 収束ブロック（IfCaseList 完成・JumpTo 回填・型検査の後）で呼ばれる。
	/// 全 CASE が Normal・定数・型一致・キー重複なしのときのみテーブルを構築し、それ以外は null を返す。
	/// eager（ParseFunctionWithCatch）と lazy（ReparseFunction）は同一の nestCheck 経路を共有するため、物化関数は自動で建表される。
	/// </summary>
	public static SelectCaseJumpTable? TryBuild(InstructionLine selectLine, ExpressionMediator exm)
	{
		if (selectLine.Argument is not ExpressionArgument expArg || expArg.Term == null)
			return null;
		AExpression term = expArg.Term;
		Type selectType = term.GetOperandType();
		bool isInt = selectType == typeof(long);
		bool isStr = selectType == typeof(string);
		if (!isInt && !isStr)
			return null;

		InstructionLine? defaultTarget = null;
		int keyCount = 0;
		Dictionary<long, InstructionLine> intTable = isInt ? new() : null!;
		Dictionary<string, InstructionLine> strTable = isStr ? new(StringComparer.Ordinal) : null!;

		foreach (var caseLine in selectLine.IfCaseList)
		{
			// 線形スキャンと同じ順序：IsError 行は最初にスキップ（先に CASEELSE 判定すると誤 break で挙動が変わる）
			if (caseLine.IsError)
				continue;
			// 線形スキャンは最初の CASEELSE で break するため、それ以降の CASE キーは収集しない（収集すると挙動が変わる）
			if (caseLine.FunctionCode == FunctionCode.CASEELSE)
			{
				defaultTarget ??= caseLine;
				break;
			}
			var caseExps = ((CaseArgument)caseLine.Argument).CaseExps;
			if (caseExps.Length == 0)
				continue;
			foreach (var exp in caseExps)
			{
				if (exp.CaseType != CaseExpressionType.Normal)
					return null; // TO / IS → フォールバック
				if (exp.GetOperandType() != selectType)
					return null; // 型不一致 → フォールバック
				if (!exp.LeftTerm.IsConst)
					return null; // 変数など非定数 → フォールバック（現状 IsConst ⟺ SingleTerm）
				keyCount++;
				if (isInt)
				{
					if (!intTable.TryAdd(exp.LeftTerm.GetIntValue(exm), caseLine))
						return null; // 重複キー → フォールバック（線形スキャンの first-wins に回帰）
				}
				else
				{
					if (!strTable.TryAdd(exp.LeftTerm.GetStrValue(exm), caseLine))
						return null;
				}
			}
		}

		defaultTarget ??= selectLine.JumpTo as InstructionLine; // CASEELSE 無し → ENDSELECT
		if (defaultTarget == null)
			return null;

		int threshold = isInt ? MinIntBranchCount : MinStrBranchCount;
		if (keyCount < threshold)
			return null;

		return new SelectCaseJumpTable
		{
			IntTable = intTable,
			StrTable = strTable,
			DefaultTarget = defaultTarget,
		};
	}
}
