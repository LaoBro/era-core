using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameView;

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
using System.Text;
using System.Text.RegularExpressions;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class RegexpMatchMethod : FunctionMethod
	{
		public RegexpMatchMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.Int }, OmitStart = 2 },
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.RefInt, ArgType.RefString1D } },
				];
			CanRestructure = false;
		}

		static void Output(MatchCollection matches, Regex reg, string[] values)
		{
			var idx = 0;
			foreach (Match match in matches)
				foreach (var name in reg.GetGroupNames())
				{
					if (idx >= values.Length) return;
					values[idx] = match.Groups[name].Value;
					idx++;
				}
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string baseString = arguments[0].GetStrValue(exm);
			Regex reg;
			try
			{
				reg = RegexFactory.GetRegex(arguments[1].GetStrValue(exm));
			}
			catch (ArgumentException e)
			{
				throw new CodeEE(string.Format(trerror.InvalidRegexArg.Text, Name, 2, e.Message));
			}
			var matches = reg.Matches(baseString);
			var ret = matches.Count;
			if (arguments.Count == 3 && arguments[2].GetIntValue(exm) != 0)
			{
				exm.VEvaluator.RESULT_ARRAY[1] = reg.GetGroupNumbers().Length;
				if (ret > 0) Output(matches, reg, exm.VEvaluator.RESULTS_ARRAY);
			}
			if (arguments.Count == 4)
			{
				(arguments[2] as VariableTerm)!.SetValue(reg.GetGroupNumbers().Length, exm);
				if (ret > 0) Output(matches, reg, ((arguments[3] as VariableTerm)!.Identifier.GetArray() as string[])!);
			}
			return ret;
		}
	}

	private sealed class BarStringMethod : FunctionMethod
	{
		public BarStringMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long var = arguments[0].GetIntValue(exm);
			long max = arguments[1].GetIntValue(exm);
			long length = arguments[2].GetIntValue(exm);
			return ExpressionMediator.CreateBar(var, max, length);
		}
	}

	private sealed class PrintCLengthMethod : FunctionMethod
	{
		public PrintCLengthMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return Config.PrintCLength;
		}
	}

	private sealed class StrlenMethod : FunctionMethod
	{
		public StrlenMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			return LangManager.GetStrlenLang(str);
		}
	}

	private sealed class StrlenuMethod : FunctionMethod
	{
		public StrlenuMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			return str.Length;
		}
	}

	private sealed class SubstringMethod : FunctionMethod
	{
		public SubstringMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.Int}, OmitStart = 1 }
				];
			CanRestructure = true;
		}

		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			int start = 0;
			int length = -1;
			if ((arguments.Count >= 2) && (arguments[1] != null))
				start = (int)arguments[1].GetIntValue(exm);
			if ((arguments.Count >= 3) && (arguments[2] != null))
				length = (int)arguments[2].GetIntValue(exm);

			return LangManager.GetSubStringLang(str, start, length);
		}
	}

	private sealed class SubstringuMethod : FunctionMethod
	{
		public SubstringuMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.Int}, OmitStart = 1 }
				];
			CanRestructure = true;
		}

		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			int start = 0;
			int length = -1;
			if ((arguments.Count >= 2) && (arguments[1] != null))
				start = (int)arguments[1].GetIntValue(exm);
			if ((arguments.Count >= 3) && (arguments[2] != null))
				length = (int)arguments[2].GetIntValue(exm);
			if ((start >= str.Length) || (length == 0))
				return "";
			if ((length < 0) || (length > str.Length))
				length = str.Length;
			if (start <= 0)
			{
				if (length == str.Length)
					return str;
				else
					start = 0;
			}
			if ((start + length) > str.Length)
				length = str.Length - start;

			return str.Substring(start, length);
		}
	}

	private sealed class StrfindMethod : FunctionMethod
	{
		public StrfindMethod(bool unicode)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.Int}, OmitStart = 2 }
				];
			CanRestructure = true;
			this.unicode = unicode;
		}

		readonly bool unicode;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{

			string target = arguments[0].GetStrValue(exm);
			string word = arguments[1].GetStrValue(exm);
			int UFTstart = 0;
			if ((arguments.Count >= 3) && (arguments[2] != null))
			{
				if (unicode)
				{
					UFTstart = (int)arguments[2].GetIntValue(exm);
				}
				else
				{
					UFTstart = LangManager.GetUFTIndex(target, (int)arguments[2].GetIntValue(exm));
				}
			}
			if (UFTstart < 0 || UFTstart >= target.Length)
				return -1;
			int index = target.IndexOf(word, UFTstart, StringComparison.Ordinal);
			if (index > 0 && !unicode)
			{
				string subStr = target.Substring(0, index);
				index = LangManager.GetStrlenLang(subStr);
			}
			return index;
		}
	}

	private sealed class StrCountMethod : FunctionMethod
	{
		public StrCountMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			Regex reg;
			try
			{
				reg = RegexFactory.GetRegex(arguments[1].GetStrValue(exm));
			}
			catch (ArgumentException e)
			{
				throw new CodeEE(string.Format(trerror.InvalidRegexArg.Text, Name, 2, e.Message));
			}
			return reg.Count(arguments[0].GetStrValue(exm));
		}
	}

	private sealed class ToStrMethod : FunctionMethod
	{
		public ToStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String }, OmitStart = 1 }
				];
			CanRestructure = true;
		}

		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long i = arguments[0].GetIntValue(exm);
			if ((arguments.Count < 2) || (arguments[1] == null))
				return i.ToString();
			string format = arguments[1].GetStrValue(exm);
			string ret;
			try
			{
				ret = i.ToString(format);
			}
			catch (FormatException)
			{
				throw new CodeEE(string.Format(trerror.InvalidFormat.Text, Name, 2));
			}
			return ret;
		}
	}

	private sealed class ToIntMethod : FunctionMethod
	{
		public ToIntMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			if (str == null || string.IsNullOrEmpty(str))
				return 0;
			//全角文字が入ってるなら無条件で0を返す
			if (str.Length < LangManager.GetStrlenLang(str))
				return 0;
			CharStream st = new(str);
			if (!char.IsDigit(st.Current) && st.Current != '+' && st.Current != '-')
				return 0;
			else if ((st.Current == '+' || st.Current == '-') && !char.IsDigit(st.Next))
				return 0;
			long ret = LexicalAnalyzer.ReadInt64(st, true);
			if (!st.EOS)
			{
				if (st.Current == '.')
				{
					st.ShiftNext();
					while (!st.EOS)
					{
						if (!char.IsDigit(st.Current))
							return 0;
						st.ShiftNext();
					}
				}
				else
					return 0;
			}
			return ret;
		}
	}

	enum StrFormType
	{
		Upper = 0,
		Lower = 1,
		Half = 2,
		Full = 3,
	};

	private sealed class StrChangeStyleMethod : FunctionMethod
	{
		readonly StrFormType strType;
		public StrChangeStyleMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			strType = StrFormType.Upper;
			CanRestructure = true;
		}
		public StrChangeStyleMethod(StrFormType type)
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			strType = type;
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			if (str == null || string.IsNullOrEmpty(str))
				return "";
			switch (strType)
			{
				case StrFormType.Upper:
					return str.ToUpper();
				case StrFormType.Lower:
					return str.ToLower();
				case StrFormType.Half:
					return StringConverter.Convert(str, StrConvFlags.Narrow, Config.Language);
				case StrFormType.Full:
					return StringConverter.Convert(str, StrConvFlags.Wide, Config.Language);
			}
			return "";
		}
	}

	private sealed class LineIsEmptyMethod : FunctionMethod
	{
		public LineIsEmptyMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return GlobalStatic.Console.EmptyLine ? 1L : 0L;
		}
	}

	private sealed class ReplaceMethod : FunctionMethod
	{
		public ReplaceMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.Int }, OmitStart = 3 },
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.RefString1D | ArgType.AllowConstRef, ArgType.Int } },
				];
			HasUniqueRestructure = true;
			CanRestructure = false;
		}

		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			return arguments.Count < 4 || arguments[3].GetIntValue(exm) != 1;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string baseString = arguments[0].GetStrValue(exm);
			Regex reg = null!;
			int type = arguments.Count == 4 ? (int)arguments[3].GetIntValue(exm) : 0;
			if (type != 2)
			{
				try
				{
					reg = RegexFactory.GetRegex(arguments[1].GetStrValue(exm));
				}
				catch (ArgumentException e)
				{
					throw new CodeEE(string.Format(trerror.InvalidRegexArg.Text, Name, 2, e.Message));
				}
			}
			if (arguments.Count == 4)
			{
				switch (type)
				{
					case 1:
						{
							if (!(arguments[2] is VariableTerm varTerm) || varTerm.Identifier.IsCalc || !varTerm.Identifier.IsArray1D || !varTerm.Identifier.IsString || varTerm.Identifier.IsConst)
								throw new CodeEE(string.Format(trerror.ArgIsNotNDStrArray.Text, Name, 3, 1));
							var items = ((arguments[2] as VariableTerm)!.Identifier.GetArray() as string[])!;
							int idx = 0;
							return reg!.Replace(baseString, (Match match) =>
							{
								if (idx < items.Length)
								{
									return items[idx++];
								}
								return string.Empty;
							});
						}
					case 2:
						{
							// 正規表現を使わず
							return baseString.Replace(arguments[1].GetStrValue(exm), arguments[2].GetStrValue(exm));
						}
				}
			}
			// type == 0 or > 2 or omitted.
			return reg!.Replace(baseString, arguments[2].GetStrValue(exm));
		}
	}

	private sealed class UnicodeMethod : FunctionMethod
	{
		public UnicodeMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long i = arguments[0].GetIntValue(exm);
			if ((i < 0) || (i > 0xFFFF))
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 1, i, 0, 0xFFFF));
			//改行関係以外の制御文字は警告扱いに変更
			//とはいえ、改行以外の制御文字を意図的に渡すのはそもそもコーディングに問題がありすぎるので、エラーでもいい気はする
			if ((i < 0x001F && i != 0x000A && i != 0x000D) || (i >= 0x007F && i <= 0x009F))
			{
				//コード実行中の場合
				if (GlobalStatic.Process.getCurrentLine != null)
					GlobalStatic.Console.PrintSystemLine(string.Format(trerror.WarnPrefix.Text,
						GlobalStatic.Process.getCurrentLine.Position!.Value.Filename,
						GlobalStatic.Process.getCurrentLine.Position!.Value.LineNo,
						string.Format(trerror.InvalidUnicode.Text, Name, i)));
				else
					// erb-loader-parallel 03：生成期（#DIM 初期值解析任务内）读 ScanLineLocal，
					// 避免并行任务间共享全局 scaningLine 错位；加载期之外（无 AsyncLocal 值）回落全局
					ParserMediator.Warn(string.Format(trerror.InvalidUnicode.Text, Name, i), LogicalLineParser.ScanLineLocal.Value ?? GlobalStatic.Process.scaningLine, 1, false, false, null!);
				return "";
			}
			string s = new(new char[] { (char)i });

			return s;
		}
	}

	private sealed class UnicodeByteMethod : FunctionMethod
	{
		public UnicodeByteMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string target = arguments[0].GetStrValue(exm);
			int length = Encoding.UTF32.GetEncoder().GetByteCount(target.ToCharArray(), 0, target.Length, false);
			byte[] bytes = new byte[length];
			Encoding.UTF32.GetEncoder().GetBytes(target.ToCharArray(), 0, target.Length, bytes, 0, false);
			long i = BitConverter.ToInt32(bytes, 0);

			return i;
		}
	}

	private sealed class ConvertIntMethod : FunctionMethod
	{
		public ConvertIntMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long toBase = arguments[1].GetIntValue(exm);
			if ((toBase != 2) && (toBase != 8) && (toBase != 10) && (toBase != 16))
				throw new CodeEE(string.Format(trerror.ArgShouldBeSpecificValue.Text, Name, 2, "2, 8, 10, 16"));
			return Convert.ToString(arguments[0].GetIntValue(exm), (int)toBase);
		}
	}

	private sealed class IsNumericMethod : FunctionMethod
	{
		public IsNumericMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string baseStr = arguments[0].GetStrValue(exm);

			//全角文字があるなら数値ではない
			if (baseStr.Length < LangManager.GetStrlenLang(baseStr))
				return 0;
			CharStream st = new(baseStr);
			if (!char.IsDigit(st.Current) && st.Current != '+' && st.Current != '-')
				return 0;
			else if ((st.Current == '+' || st.Current == '-') && !char.IsDigit(st.Next))
				return 0;
			if (!LexicalAnalyzer.NumericCheck(st))
				return (0);
			if (!st.EOS)
			{
				if (st.Current == '.')
				{
					st.ShiftNext();
					while (!st.EOS)
					{
						if (!char.IsDigit(st.Current))
							return 0;
						st.ShiftNext();
					}
				}
				else
					return 0;
			}
			return 1;
		}
	}

	private sealed class EscapeMethod : FunctionMethod
	{
		public EscapeMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return Regex.Escape(arguments[0].GetStrValue(exm));
		}
	}

	private sealed class EncodeToUniMethod : FunctionMethod
	{
		public EncodeToUniMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string baseStr = arguments[0].GetStrValue(exm);
			if (baseStr.Length == 0)
				return -1;
			long position = (arguments.Count > 1 && arguments[1] != null) ? arguments[1].GetIntValue(exm) : 0;
			if (position < 0)
				throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, Name, 2, position));
			if (position >= baseStr.Length)
				throw new CodeEE(string.Format(trerror.EncodeToUni2ndArgError.Text, Name, position, baseStr));
			return char.ConvertToUtf32(baseStr, (int)position);
		}
	}

	public sealed class CharAtMethod : FunctionMethod
	{
		public CharAtMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			long pos = arguments[1].GetIntValue(exm);
			if (pos < 0 || pos >= str.Length)
				return "";
			return str[(int)pos].ToString();
		}
	}

	public sealed class GetLineStrMethod : FunctionMethod
	{
		public GetLineStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			if (string.IsNullOrEmpty(str))
				throw new CodeEE(string.Format(trerror.ArgIsEmptyString.Text, Name, 1));
			return exm.Console.getStBar(str);
		}
	}

	public sealed class StrFormMethod : FunctionMethod
	{
		public StrFormMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			HasUniqueRestructure = true;
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			string destStr;
			try
			{
				StrFormWord wt = LexicalAnalyzer.AnalyseFormattedString(new CharStream(str), FormStrEndWith.EoL, false);
				StrForm strForm = StrForm.FromWordToken(wt);
				destStr = strForm.GetString(exm);
			}
			catch (CodeEE e)
			{
				throw new CodeEE(string.Format(trerror.InvalidFormString.Text, Name, str, e.Message));
			}
			catch
			{
				throw new CodeEE(string.Format(trerror.UnexectedFormStringErr.Text, Name, str));
			}
			return destStr;
		}
		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			arguments[0].Restructure(exm);
			//引数が文字列式等ならお手上げなので諦める
			if (!(arguments[0] is SingleTerm) && !(arguments[0] is VariableTerm))
				return false;
			//引数が確定値でない文字列変数なら無条件で不可（結果が可変なため）
			if ((arguments[0] is VariableTerm) && !((VariableTerm)arguments[0]).Identifier.IsConst)
				return false;
			string str = arguments[0].GetStrValue(exm);
			try
			{
				StrFormWord wt = LexicalAnalyzer.AnalyseFormattedString(new CharStream(str), FormStrEndWith.EoL, false);
				StrForm strForm = StrForm.FromWordToken(wt);
				if (!strForm.IsConst)
					return false;
			}
			catch (Exception e)
			{
				if (e is CodeEE)
					throw;
				//パースできないのはエラーがあるかここではわからないからとりあえず考えない
				EmueraLog.Debug(Name, e.Message);
				return false;
			}
			return true;
		}
	}

	public sealed class JoinMethod : FunctionMethod
	{
		public JoinMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefAnyArray | ArgType.AllowConstRef, ArgType.String, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				];
			HasUniqueRestructure = true;
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm varTerm = (VariableTerm)arguments[0];
			string delimiter = (arguments.Count >= 2 && arguments[1] != null) ? arguments[1].GetStrValue(exm) : ",";
			long index1 = (arguments.Count >= 3 && arguments[2] != null) ? arguments[2].GetIntValue(exm) : 0;
			long index2 = (arguments.Count == 4 && arguments[3] != null) ? arguments[3].GetIntValue(exm) : varTerm.GetLastLength() - index1;

			FixedVariableTerm p = varTerm.GetFixedVariableTerm(exm);

			if (index2 < 0)
				throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, Name, 4, index2));

			p.IsArrayRangeValid(index1, index1 + index2, "STRJOIN", 2L, 3L);
			return VariableEvaluator.GetJoinedStr(p, delimiter, index1, index2);
		}
		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			//第1変数は変数名なので、定数文字列変数だと事故が起こるので独自対応
			VariableTerm varTerm = (VariableTerm)arguments[0];
			bool canRerstructure = varTerm.Identifier.IsConst;
			for (int i = 1; i < arguments.Count; i++)
			{
				if (arguments[i] == null)
					continue;
				arguments[i] = arguments[i].Restructure(exm);
				canRerstructure &= arguments[i] is SingleTerm;
			}
			return canRerstructure;
		}
	}
}
