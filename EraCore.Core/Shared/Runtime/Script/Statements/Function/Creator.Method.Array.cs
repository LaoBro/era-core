using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	/// <summary>
	/// 按排序索引重排一维数组。返回 false 表示目标数组长度不足以容纳全部排序索引，不修改数组。
	/// </summary>
	internal static bool ArrayReorder<T>(T[] array, int[] sortedArray)
	{
		if (array.Length < sortedArray.Length)
			return false;
		var clone = (T[])array.Clone();
		for (int i = 0; i < sortedArray.Length; i++)
			array[i] = clone[sortedArray[i]];
		return true;
	}

	/// <summary>
	/// 按排序索引重排二维数组的第一维。返回 false 表示目标数组长度不足，不修改数组。
	/// </summary>
	internal static bool ArrayReorder(long[,] array, int[] sortedArray)
	{
		if (array.GetLength(0) < sortedArray.Length)
			return false;
		var clone = (long[,])array.Clone();
		for (int i = 0; i < sortedArray.Length; i++)
			for (int x = 0; x < array.GetLength(1); x++)
				array[i, x] = clone[sortedArray[i], x];
		return true;
	}

	/// <summary>
	/// 按排序索引重排二维数组的第一维。返回 false 表示目标数组长度不足，不修改数组。
	/// </summary>
	internal static bool ArrayReorder(string[,] array, int[] sortedArray)
	{
		if (array.GetLength(0) < sortedArray.Length)
			return false;
		var clone = (string[,])array.Clone();
		for (int i = 0; i < sortedArray.Length; i++)
			for (int x = 0; x < array.GetLength(1); x++)
				array[i, x] = clone[sortedArray[i], x];
		return true;
	}

	/// <summary>
	/// 按排序索引重排三维数组的第一维。返回 false 表示目标数组长度不足，不修改数组。
	/// </summary>
	internal static bool ArrayReorder(long[,,] array, int[] sortedArray)
	{
		if (array.GetLength(0) < sortedArray.Length)
			return false;
		var clone = (long[,,])array.Clone();
		for (int i = 0; i < sortedArray.Length; i++)
			for (int x = 0; x < array.GetLength(1); x++)
				for (int y = 0; y < array.GetLength(2); y++)
					array[i, x, y] = clone[sortedArray[i], x, y];
		return true;
	}

	/// <summary>
	/// 按排序索引重排三维数组的第一维。返回 false 表示目标数组长度不足，不修改数组。
	/// </summary>
	internal static bool ArrayReorder(string[,,] array, int[] sortedArray)
	{
		if (array.GetLength(0) < sortedArray.Length)
			return false;
		var clone = (string[,,])array.Clone();
		for (int i = 0; i < sortedArray.Length; i++)
			for (int x = 0; x < array.GetLength(1); x++)
				for (int y = 0; y < array.GetLength(2); y++)
					array[i, x, y] = clone[sortedArray[i], x, y];
		return true;
	}

	private sealed class ArrayMultiSortExMethod : FunctionMethod
	{
		public ArrayMultiSortExMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString1D, ArgType.Int, ArgType.Int | ArgType.DisallowVoid }, OmitStart = 2 },
					new ArgTypeList{ ArgTypes = { ArgType.RefInt1D, ArgType.RefString1D, ArgType.Int, ArgType.Int | ArgType.DisallowVoid }, OmitStart = 2 },
				];
			CanRestructure = false;
		}
		private string CheckVariableTerm(AExpression arg, string v)
		{
			var vname = v == null ? trerror.FirstArg.Text : v;
			if (!(arg is VariableTerm varTerm) || varTerm.Identifier.IsCalc || varTerm.Identifier.IsConst)
				return string.Format(trerror.NotVarFunc.Text, Name, vname);
			if (v == null && !varTerm.Identifier.IsArray1D)
				return string.Format(trerror.Not1DFuncArg.Text, Name, "1");
			if (varTerm.Identifier.IsCharacterData)
				return string.Format(trerror.IsCharaVarFunc.Text, Name, vname);
			if (!varTerm.Identifier.IsArray1D && !varTerm.Identifier.IsArray2D && !varTerm.Identifier.IsArray3D)
				return string.Format(trerror.NotDimVarFunc.Text, Name, vname);
			return null!;
		}
		private VariableTerm GetConvertedTerm(ExpressionMediator exm, string name)
		{
			WordCollection wc = LexicalAnalyzer.Analyse(new CharStream(name), LexEndWith.EoL, LexAnalyzeFlag.None);
			var term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);
			var err = CheckVariableTerm(term, name);
			if (err != null)
				throw new CodeEE(err);
			return (term as VariableTerm)!;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			bool isAscending = arguments.Count < 3 || arguments[2] == null || arguments[2].GetIntValue(exm) != 0;
			long fixedLength = arguments.Count < 4 ? -1 : arguments[3].GetIntValue(exm);
			if (fixedLength == 0) return 0;
			VariableTerm varTerm = arguments[0] is VariableTerm ? (VariableTerm)arguments[0] : GetConvertedTerm(exm, arguments[0].GetStrValue(exm));
			int[] sortedArray;
			if (varTerm.Identifier.IsInteger)
			{
				List<KeyValuePair<long, int>> sortList = [];
				long[] array = (long[])varTerm.Identifier.GetArray();
				var length = fixedLength > 0 ? Math.Min(fixedLength, array.Length) : array.Length;
				for (int i = 0; i < length; i++)
				{
					if (fixedLength == -1 && array[i] == 0)
						break;
					sortList.Add(new KeyValuePair<long, int>(array[i], i));
				}
				//素ではintの範囲しか扱えないので一工夫
				sortList.Sort((a, b) => isAscending ? a.Key.CompareTo(b.Key) : b.Key.CompareTo(a.Key));
				sortedArray = sortList.Select(p => p.Value).ToArray();
			}
			else
			{
				List<KeyValuePair<string, int>> sortList = [];
				string[] array = (string[])varTerm.Identifier.GetArray();
				var length = fixedLength > 0 ? Math.Min(fixedLength, array.Length) : array.Length;
				for (int i = 0; i < length; i++)
				{
					if (fixedLength == -1 && string.IsNullOrEmpty(array[i]))
						return 0;
					sortList.Add(new KeyValuePair<string, int>(array[i], i));
				}
				sortList.Sort((a, b) => isAscending ? a.Key.CompareTo(b.Key) : b.Key.CompareTo(a.Key));
				sortedArray = sortList.Select(p => p.Value).ToArray();
			}
			List<VariableTerm> varTerms = [];
			foreach (var nTerm in (string[])(arguments[1] as VariableTerm)!.Identifier.GetArray())
				varTerms.Add(GetConvertedTerm(exm, nTerm));
			foreach (var term in varTerms)
			{
				if (term.Identifier.IsArray1D)
				{
					if (term.IsInteger)
					{
						if (!ArrayReorder((long[])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
					else
					{
						if (!ArrayReorder((string[])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
				}
				else if (term.Identifier.IsArray2D)
				{
					if (term.IsInteger)
					{
						if (!ArrayReorder((long[,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
					else
					{
						if (!ArrayReorder((string[,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
				}
				else if (term.Identifier.IsArray3D)
				{
					if (term.IsInteger)
					{
						if (!ArrayReorder((long[,,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
					else
					{
						if (!ArrayReorder((string[,,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
				}
				else { throw new ExeEE(trerror.AbnormalArray.Text); }
			}
			return 1;
		}
		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			for (int i = 0; i < arguments.Count; i++)
				arguments[i] = arguments[i].Restructure(exm);
			return false;
		}
	}

	private sealed class SumArrayMethod : FunctionMethod
	{
		readonly bool isCharaRange;
		public SumArrayMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefIntArray, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			isCharaRange = false;
			CanRestructure = false;
		}
		public SumArrayMethod(bool isChara)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.CharacterData | ArgType.RefIntArray | ArgType.AllowConstRef, ArgType.Int, ArgType.Int }, OmitStart = 1 }
				];
			isCharaRange = isChara;
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm varTerm = (VariableTerm)arguments[0];
			long index1 = (arguments.Count >= 2 && arguments[1] != null) ? arguments[1].GetIntValue(exm) : 0;
			long index2 = (arguments.Count == 3 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : (isCharaRange ? exm.VEvaluator.CHARANUM : varTerm.GetLastLength());

			FixedVariableTerm p = varTerm.GetFixedVariableTerm(exm);
			if (!isCharaRange)
			{
				p.IsArrayRangeValid(index1, index2, "SUMARRAY", 2L, 3L);
				return VariableEvaluator.GetArraySum(p, index1, index2);
			}
			else
			{
				long charaNum = exm.VEvaluator.CHARANUM;
				if (index1 >= charaNum || index1 < 0 || index2 > charaNum || index2 < 0)
					throw new CodeEE(string.Format(trerror.CharacterRangeInvalid.Text, Name, index1, index2));
				return VariableEvaluator.GetArraySumChara(p, index1, index2);
			}
		}
	}

	private sealed class MaxArrayMethod : FunctionMethod
	{
		readonly bool isCharaRange;
		readonly bool isMax;
		readonly string funcName;
		public MaxArrayMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefInt1D | ArgType.AllowConstRef, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			isCharaRange = false;
			isMax = true;
			funcName = "MAXARRAY";
			CanRestructure = false;
		}
		public MaxArrayMethod(bool isChara)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.CharacterData | ArgType.RefInt1D | ArgType.AllowConstRef, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			isCharaRange = isChara;
			isMax = true;
			if (isCharaRange)
				funcName = "MAXCARRAY";
			else
				funcName = "MAXARRAY";
			CanRestructure = false;
		}
		public MaxArrayMethod(bool isChara, bool isMaxFunc)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = isChara
				? [
						new ArgTypeList{ ArgTypes = { ArgType.CharacterData | ArgType.RefInt1D | ArgType.AllowConstRef, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				]
				: [
						new ArgTypeList{ ArgTypes = { ArgType.RefInt1D | ArgType.AllowConstRef, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			isCharaRange = isChara;
			isMax = isMaxFunc;
			funcName = (isMax ? "MAX" : "MIN") + (isCharaRange ? "C" : "") + "ARRAY";
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm vTerm = (VariableTerm)arguments[0];
			long start = (arguments.Count > 1 && arguments[1] != null) ? arguments[1].GetIntValue(exm) : 0;
			long end = (arguments.Count > 2 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : (isCharaRange ? exm.VEvaluator.CHARANUM : vTerm.GetLength());
			FixedVariableTerm p = vTerm.GetFixedVariableTerm(exm);
			if (!isCharaRange)
			{
				p.IsArrayRangeValid(start, end, funcName, 2L, 3L);
				return VariableEvaluator.GetMaxArray(p, start, end, isMax);
			}
			else
			{
				long charaNum = exm.VEvaluator.CHARANUM;
				if (start >= charaNum || start < 0 || end > charaNum || end < 0)
					throw new CodeEE(string.Format(trerror.CharacterRangeInvalid.Text, funcName, start, end));
				return VariableEvaluator.GetMaxArrayChara(p, start, end, isMax);
			}
		}
	}

	private sealed class FindElementMethod : FunctionMethod
	{
		public FindElementMethod(bool last)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefAny1D | ArgType.AllowConstRef, ArgType.SameAsFirst, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = true; //すべて定数項ならできるはず
			HasUniqueRestructure = true;
			isLast = last;
			funcName = isLast ? "FINDLASTELEMENT" : "FINDELEMENT";
		}

		readonly bool isLast;
		readonly string funcName;

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			bool isExact = false;
			VariableTerm varTerm = (VariableTerm)arguments[0];

			long start = (arguments.Count > 2 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : 0;
			long end = (arguments.Count > 3 && arguments[3] != null) ? arguments[3].GetIntValue(exm) : varTerm.GetLength();
			if (arguments.Count > 4 && arguments[4] != null)
				isExact = arguments[4].GetIntValue(exm) != 0;

			FixedVariableTerm p = varTerm.GetFixedVariableTerm(exm);
			p.IsArrayRangeValid(start, end, funcName, 3L, 4L);

			if (arguments[0].GetOperandType() == typeof(long))
			{
				long targetValue = arguments[1].GetIntValue(exm);
				return VariableEvaluator.FindElement(p, targetValue, start, end, isExact, isLast);
			}
			else
			{
				Regex targetString;
				try
				{
					targetString = RegexFactory.GetRegex(arguments[1].GetStrValue(exm));
				}
				catch (ArgumentException e)
				{
					throw new CodeEE(string.Format(trerror.InvalidRegexArg.Text, Name, 2, e.Message));
				}
				return VariableEvaluator.FindElement(p, targetString, start, end, isExact, isLast);
			}
		}


		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			arguments[0].Restructure(exm);
			VariableTerm varToken = (arguments[0] as VariableTerm)!;
			bool isConst = varToken.Identifier.IsConst;
			for (int i = 1; i < arguments.Count; i++)
			{
				if (arguments[i] == null)
					continue;
				arguments[i] = arguments[i].Restructure(exm);
				if (isConst && !(arguments[i] is SingleTerm))
					isConst = false;
			}
			return isConst;
		}
	}

	private sealed class InRangeMethod : FunctionMethod
	{
		public InRangeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long value = arguments[0].GetIntValue(exm);
			long min = arguments[1].GetIntValue(exm);
			long max = arguments[2].GetIntValue(exm);
			return ((value >= min) && (value <= max)) ? 1L : 0L;
		}
	}

	private sealed class InRangeArrayMethod : FunctionMethod
	{
		public InRangeArrayMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefInt1D | ArgType.AllowConstRef, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 3 },
				];
			CanRestructure = false;
		}
		public InRangeArrayMethod(bool isChara)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.CharacterData | ArgType.RefInt1D | ArgType.AllowConstRef, ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 3 },
				];
			isCharaRange = isChara;
			CanRestructure = false;
		}
		private readonly bool isCharaRange;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long min = arguments[1].GetIntValue(exm);
			long max = arguments[2].GetIntValue(exm);

			VariableTerm varTerm = (arguments[0] as VariableTerm)!;
			long start = (arguments.Count > 3 && arguments[3] != null) ? arguments[3].GetIntValue(exm) : 0;
			long end = (arguments.Count > 4 && arguments[4] != null) ? arguments[4].GetIntValue(exm) : (isCharaRange ? exm.VEvaluator.CHARANUM : varTerm.GetLength());

			FixedVariableTerm p = varTerm.GetFixedVariableTerm(exm);

			if (!isCharaRange)
			{
				p.IsArrayRangeValid(start, end, "INRANGEARRAY", 4L, 5L);
				return VariableEvaluator.GetInRangeArray(p, min, max, start, end);
			}
			else
			{
				long charaNum = exm.VEvaluator.CHARANUM;
				if (start >= charaNum || start < 0 || end > charaNum || end < 0)
					throw new CodeEE(string.Format(trerror.CharacterRangeInvalid.Text, Name, start, end));
				return VariableEvaluator.GetInRangeArrayChara(p, min, max, start, end);
			}
		}
	}

	private sealed class ArrayMultiSortMethod : FunctionMethod
	{
		public ArrayMultiSortMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefAny1D, ArgType.RefAnyArray | ArgType.Variadic }, OmitStart = 1 },
				];
			CanRestructure = false;
			HasUniqueRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm varTerm = (arguments[0] as VariableTerm)!;
			int[] sortedArray;
			if (varTerm.Identifier.IsInteger)
			{
				List<KeyValuePair<long, int>> sortList = [];
				long[] array = (long[])varTerm.Identifier.GetArray();
				for (int i = 0; i < array.Length; i++)
				{
					if (array[i] == 0)
						break;
					sortList.Add(new KeyValuePair<long, int>(array[i], i));
				}
				//素ではintの範囲しか扱えないので一工夫
				sortList.Sort((a, b) => a.Key.CompareTo(b.Key));
				sortedArray = new int[sortList.Count];
				for (int i = 0; i < sortedArray.Length; i++)
					sortedArray[i] = sortList[i].Value;
			}
			else
			{
				List<KeyValuePair<string, int>> sortList = [];
				string[] array = (string[])varTerm.Identifier.GetArray();
				for (int i = 0; i < array.Length; i++)
				{
					if (string.IsNullOrEmpty(array[i]))
						#region EM_私家版_ARRAYMSORT_文字列配列処理修正
						break;
					#endregion
					sortList.Add(new KeyValuePair<string, int>(array[i], i));
				}
				sortList.Sort((a, b) => { return a.Key.CompareTo(b.Key); });
				sortedArray = new int[sortList.Count];
				for (int i = 0; i < sortedArray.Length; i++)
					sortedArray[i] = sortList[i].Value;
			}
			foreach (VariableTerm term in arguments.Cast<VariableTerm>())//もう少し賢い方法はないものだろうか
			{
				if (term.Identifier.IsArray1D)
				{
					if (term.IsInteger)
					{
						if (!ArrayReorder((long[])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
					else
					{
						if (!ArrayReorder((string[])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
				}
				else if (term.Identifier.IsArray2D)
				{
					if (term.IsInteger)
					{
						if (!ArrayReorder((long[,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
					else
					{
						if (!ArrayReorder((string[,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
				}
				else if (term.Identifier.IsArray3D)
				{
					if (term.IsInteger)
					{
						if (!ArrayReorder((long[,,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
					else
					{
						if (!ArrayReorder((string[,,])term.Identifier.GetArray(), sortedArray))
							return 0;
					}
				}
				else { throw new ExeEE(trerror.AbnormalArray.Text); }
			}
			return 1;
		}
		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			for (int i = 0; i < arguments.Count; i++)
				arguments[i] = arguments[i].Restructure(exm);
			return false;
		}
	}

	// ======== Skia 方言 P0：数组位运算（实现票 18，spec §5） ========
	// 语义对齐 Skia 的 BitArrayManager；era-core 无 SparseArray，故只处理 long[]。

	private sealed class BitSetMethod : FunctionMethod
	{
		public BitSetMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.RefInt1D, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long[] array = (long[])((VariableTerm)arguments[0]).Identifier.GetArray();
			long idx = arguments[1].GetIntValue(exm);
			long val = arguments.Count > 2 && arguments[2] != null ? arguments[2].GetIntValue(exm) : 1;
			long length = arguments.Count > 3 && arguments[3] != null ? arguments[3].GetIntValue(exm) : 1;
			BitArrayManager.BitSet(array, idx, val, length);
			return 1;
		}
	}

	private sealed class BitGetMethod : FunctionMethod
	{
		public BitGetMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.RefInt1D, ArgType.Int }, OmitStart = 1 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long[] array = (long[])((VariableTerm)arguments[0]).Identifier.GetArray();
			long idx = arguments[1].GetIntValue(exm);
			return BitArrayManager.BitGet(array, idx);
		}
	}

	private sealed class BitToggleMethod : FunctionMethod
	{
		public BitToggleMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.RefInt1D, ArgType.Int }, OmitStart = 1 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long[] array = (long[])((VariableTerm)arguments[0]).Identifier.GetArray();
			long idx = arguments[1].GetIntValue(exm);
			return BitArrayManager.BitToggle(array, idx);
		}
	}

	private sealed class BitIndexOfFirstMethod : FunctionMethod
	{
		public BitIndexOfFirstMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.RefInt1D, ArgType.Int }, OmitStart = 1 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long[] array = (long[])((VariableTerm)arguments[0]).Identifier.GetArray();
			long val = arguments.Count > 1 && arguments[1] != null ? arguments[1].GetIntValue(exm) : 0;
			return BitArrayManager.BitIndexOfFirst(array, val);
		}
	}
}
