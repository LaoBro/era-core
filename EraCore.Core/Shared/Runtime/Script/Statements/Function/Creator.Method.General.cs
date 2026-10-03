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
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class GetVarMethod : FunctionMethod
	{
		public GetVarMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string name = arguments[0].GetStrValue(exm);
			WordCollection wc = LexicalAnalyzer.Analyse(new CharStream(arguments[0].GetStrValue(exm)), LexEndWith.EoL, LexAnalyzeFlag.None);
			AExpression term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);

			if (term is VariableTerm)
			{
				VariableTerm var = (VariableTerm)term;

				if (var.Identifier == null)
					throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));
				if (!var.IsInteger)
					throw new CodeEE(string.Format(trerror.IsNotInt.Text, name));
				return var.GetIntValue(exm);
			}
			else
				throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));
		}
	}

	private sealed class GetVarsMethod : FunctionMethod
	{
		public GetVarsMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string name = arguments[0].GetStrValue(exm);
			WordCollection wc = LexicalAnalyzer.Analyse(new CharStream(arguments[0].GetStrValue(exm)), LexEndWith.EoL, LexAnalyzeFlag.None);
			AExpression term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);

			if (term is VariableTerm)
			{
				VariableTerm var = (VariableTerm)term;

				if (var.Identifier == null)
					throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));
				if (!var.IsString)
					throw new CodeEE(string.Format(trerror.IsNotStr.Text, name));
				return var.GetStrValue(exm);
			}
			else
				throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));
		}
	}

	private sealed class ExistVarMethod : FunctionMethod
	{
		public ExistVarMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableToken token = GlobalStatic.IdentifierDictionary.GetVariableToken(arguments[0].GetStrValue(exm), null!, true);
			if (token != null)
			{
				long res = 0;
				if (token.IsInteger) res |= 1;
				if (token.IsString) res |= 2;
				if (token.IsConst) res |= 4;
				if (token.IsArray2D) res |= 8;
				if (token.IsArray3D) res |= 16;
				return res;
			}
			return 0;
		}
	}

	private sealed class SetVarMethod : FunctionMethod
	{
		public SetVarMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Any } },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string name = arguments[0].GetStrValue(exm);
			WordCollection wc = LexicalAnalyzer.Analyse(new CharStream(arguments[0].GetStrValue(exm)), LexEndWith.EoL, LexAnalyzeFlag.None);
			AExpression term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);

			if (term is VariableTerm var)
			{
				if (var.Identifier == null || var.Identifier.IsConst)
					throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));
				if (var.IsString)
				{
					if (arguments[1].GetOperandType() != typeof(string))
						throw new CodeEE(string.Format(trerror.IsNotInt.Text, name));
					var.SetValue(arguments[1].GetStrValue(exm), exm);
				}
				else
				{
					if (arguments[1].GetOperandType() != typeof(long))
						throw new CodeEE(string.Format(trerror.IsNotStr.Text, name));
					var.SetValue(arguments[1].GetIntValue(exm), exm);
				}
				return 1;
			}
			else
				throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));
		}
	}

	private sealed class VarSetExMethod : FunctionMethod
	{
		public VarSetExMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Any, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string name = arguments[0].GetStrValue(exm);
			WordCollection wc = LexicalAnalyzer.Analyse(new CharStream(arguments[0].GetStrValue(exm)), LexEndWith.EoL, LexAnalyzeFlag.None);
			AExpression term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);

			if (term is VariableTerm var)
			{
				if (var.Identifier == null || var.Identifier.IsConst)
					throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));

				int start = (int)(arguments.Count >= 4 ? arguments[3].GetIntValue(exm) : 0);
				int end = (int)(arguments.Count == 5 ? arguments[4].GetIntValue(exm)
					: (var.Identifier.IsArray1D ? var.Identifier.GetLength()
					: (var.Identifier.IsArray2D ? var.Identifier.GetLength(1)
					: (var.Identifier.IsArray2D ? var.Identifier.GetLength(2) : 0))));
				bool setAllDims = arguments.Count >= 3 ? arguments[2].GetIntValue(exm) != 0 : true;
				if (var.IsString)
				{
					var val = string.Empty;
					if (arguments.Count > 1 && arguments[1].GetOperandType() != typeof(string))
						throw new CodeEE(string.Format(trerror.SetStrToInt.Text, name));
					if (arguments.Count > 1)
						val = arguments[1].GetStrValue(exm);
					if (var.Identifier.IsArray1D)
						var.Identifier.SetValueAll(val, start, end, 0);
					else if (var.Identifier.IsArray2D)
					{
						var array = (var.Identifier.GetArray() as string[,])!;
						var idx1 = var.GetElementInt(0, exm);
						var idx2 = var.GetElementInt(1, exm);
						for (int i = Math.Max(start, (int)idx2); i < end; i++)
							array[idx1, i] = val;
					}
					if (var.Identifier.IsArray3D)
					{
						var idx1 = var.GetElementInt(0, exm);
						var idx2 = var.GetElementInt(1, exm);
						var idx3 = var.GetElementInt(2, exm);
						var array = (var.Identifier.GetArray() as string[,,])!;
						for (int i = Math.Max(start, (int)idx3); i < end; i++)
							array[idx2, idx1, i] = val;
					}
				}
				else
				{
					long val = 0;
					if (arguments.Count > 1 && arguments[1].GetOperandType() != typeof(long))
						throw new CodeEE(string.Format(trerror.SetIntToStr.Text, name));
					if (arguments.Count > 1)
						val = arguments[1].GetIntValue(exm);
					if (var.Identifier.IsArray1D)
						var.Identifier.SetValueAll(val, start, end, 0);
					else if (var.Identifier.IsArray2D)
					{
						var array = (var.Identifier.GetArray() as long[,])!;
						var idx1 = var.GetElementInt(0, exm);
						var idx2 = var.GetElementInt(1, exm);
						if (setAllDims)
						{
							for (int j = 0; j < array.GetLength(0); j++)
								for (int i = Math.Max(start, (int)idx2); i < end; i++)
									array[j, i] = val;
						}
						else
						{
							for (int i = Math.Max(start, (int)idx2); i < end; i++)
								array[idx1, i] = val;
						}
					}
					if (var.Identifier.IsArray3D)
					{
						var idx1 = var.GetElementInt(0, exm);
						var idx2 = var.GetElementInt(1, exm);
						var idx3 = var.GetElementInt(2, exm);
						var array = (var.Identifier.GetArray() as long[,,])!;
						if (setAllDims)
						{
							for (int k = 0; k < array.GetLength(0); k++)
								for (int j = 0; j < array.GetLength(1); j++)
									for (int i = Math.Max(start, (int)idx3); i < end; i++)
										array[k, j, i] = val;
						}
						else
						{
							for (int i = Math.Max(start, (int)idx3); i < end; i++)
								array[idx2, idx1, i] = val;
						}
					}
				}
				return 1;
			}
			else
				throw new CodeEE(string.Format(trerror.IsNotVar.Text, name));
		}
	}

	private sealed class VarsizeMethod : FunctionMethod
	{
		public VarsizeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = true;
			//1808beta009 参照型変数の追加によりちょっと面倒になった
			HasUniqueRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableToken var = GlobalStatic.IdentifierDictionary.GetVariableToken(arguments[0].GetStrValue(exm), null!, true);
			if (var == null)
				throw new CodeEE(string.Format(trerror.NotVariableName.Text, Name, 1, arguments[0].GetStrValue(exm)));
			int dim = 0;
			if (arguments.Count == 2 && arguments[1] != null)
				dim = (int)arguments[1].GetIntValue(exm);
			if (Config.VarsizeDimConfig && dim > 0)
				dim--;
			return var.GetLength(dim);
		}
		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			arguments[0].Restructure(exm);
			if (arguments.Count > 1)
				arguments[1].Restructure(exm);
			if (arguments[0] is SingleTerm && (arguments.Count == 1 || arguments[1] is SingleTerm))
			{
				VariableToken var = GlobalStatic.IdentifierDictionary.GetVariableToken(arguments[0].GetStrValue(exm), null!, true);
				if (var == null || var.IsReference)//可変長の場合は定数化できない
					return false;
				return true;
			}
			return false;
		}
	}

	private sealed class CheckfontMethod : FunctionMethod
	{
		public CheckfontMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;//起動中に変わることもそうそうないはず……
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
#if HEADLESS
			// Headless 模式下不支持字体集合检查
			return 0;
#else
			using System.Drawing.Text.InstalledFontCollection ifc = new();
			long isInstalled = 0;
			foreach (FontFamily ff in ifc.Families)
			{
				#region EE_フォントファイル対応
				if (ff.Name == str)
				{
					isInstalled = 1;
					break;
				}
			}
			foreach (FontFamily ff in GlobalStatic.Pfc.Families)
			{
				if (ff.Name == str)
				{
					isInstalled = 1;
					break;
				}
			}
			#endregion
			return (isInstalled);
#endif
		}

	}

	private sealed class CheckdataMethod : FunctionMethod
	{
		public CheckdataMethod(EraSaveFileType type)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
			this.type = type;
		}

		readonly EraSaveFileType type;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long target = arguments[0].GetIntValue(exm);
			if (target < 0)
				throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, Name, 1, target));
			else if (target > int.MaxValue)
				throw new CodeEE(string.Format(trerror.ArgIsTooLarge.Text, Name, 1, target));
			EraDataResult result = exm.VEvaluator.CheckData((int)target, type);
			exm.VEvaluator.RESULTS = result.DataMes;
			return (long)result.State;
		}
	}

	private sealed class CheckdataStrMethod : FunctionMethod
	{
		public CheckdataStrMethod(EraSaveFileType type)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
			this.type = type;
		}

		readonly EraSaveFileType type;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string datFilename = arguments[0].GetStrValue(exm);
			EraDataResult result = exm.VEvaluator.CheckData(datFilename, type);
			exm.VEvaluator.RESULTS = result.DataMes;
			return (long)result.State;
		}
	}

	private sealed class FindFilesMethod : FunctionMethod
	{
		public FindFilesMethod(EraSaveFileType type)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String }, OmitStart = 0 },
				];
			CanRestructure = false;
			this.type = type;
		}

		readonly EraSaveFileType type;


		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string pattern = "*";
			if (arguments.Count > 0 && arguments[0] != null)
				pattern = arguments[0].GetStrValue(exm);
			List<string> filepathes = VariableEvaluator.GetDatFiles(type == EraSaveFileType.CharVar, pattern);
			string[] results = exm.VEvaluator.VariableData.DataStringArray[(int)(VariableCode.RESULTS & VariableCode.__LOWERCASE__)];
			if (filepathes.Count <= results.Length)
				filepathes.CopyTo(results);
			else
				filepathes.CopyTo(0, results, 0, results.Length);
			return filepathes.Count;
		}
	}

	private sealed class IsSkipMethod : FunctionMethod
	{
		public IsSkipMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return exm.Process.SkipPrint ? 1L : 0L;
		}
	}

	private sealed class MesSkipMethod : FunctionMethod
	{
		public MesSkipMethod(bool warn)
		{
			ReturnType = typeof(long);
			CanRestructure = false;
			this.warn = warn;
		}

		readonly bool warn;
		public override string CheckArgumentType(string name, List<AExpression> arguments)
		{
			if (arguments.Count > 0)
				return string.Format(trerror.TooManyFuncArgs.Text, name);
			if (warn)
			{
				// erb-loader-parallel 08：加载期读者统一经 GetLoadScaningLine
				ParserMediator.Warn(string.Format(trerror.FuncDeprecated.Text, name, "MESSKIP"), LogicalLineParser.GetLoadScaningLine() ?? GlobalStatic.Process.GetScaningLine(), 1, false, false, null!);
			}
			return null!;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return GlobalStatic.Console.MesSkip ? 1L : 0L;
		}
	}

	private sealed class GetColorMethod : FunctionMethod
	{
		public GetColorMethod(bool isDef)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = isDef;
			defaultColor = isDef;
		}

		readonly bool defaultColor;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			EmuColor color = defaultColor ? Config.ForeColor : GlobalStatic.Console.StringStyle.Color;
			return color.ToArgb() & 0xFFFFFF;
		}
	}

	private sealed class GetFocusColorMethod : FunctionMethod
	{
		public GetFocusColorMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return Config.FocusColor.ToArgb() & 0xFFFFFF;
		}
	}

	private sealed class GetBGColorMethod : FunctionMethod
	{
		public GetBGColorMethod(bool isDef)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = isDef;
			defaultColor = isDef;
		}

		readonly bool defaultColor;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			EmuColor color = defaultColor ? Config.BackColor : GlobalStatic.Console.bgColor;
			return color.ToArgb() & 0xFFFFFF;
		}
	}

	private sealed class GetStyleMethod : FunctionMethod
	{
		public GetStyleMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			EmuFontStyle fontstyle = GlobalStatic.Console.StringStyle.FontStyle;
			long ret = 0;
			if ((fontstyle & EmuFontStyle.Bold) == EmuFontStyle.Bold)
				ret |= 1;
			if ((fontstyle & EmuFontStyle.Italic) == EmuFontStyle.Italic)
				ret |= 2;
			if ((fontstyle & EmuFontStyle.Strikeout) == EmuFontStyle.Strikeout)
				ret |= 4;
			if ((fontstyle & EmuFontStyle.Underline) == EmuFontStyle.Underline)
				ret |= 8;
			return ret;
		}
	}

	private sealed class GetFontMethod : FunctionMethod
	{
		public GetFontMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return GlobalStatic.Console.StringStyle.Fontname;
		}
	}

	private sealed class CurrentAlignMethod : FunctionMethod
	{
		public CurrentAlignMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (exm.Console.Alignment == DisplayLineAlignment.LEFT)
				return "LEFT";
			else if (exm.Console.Alignment == DisplayLineAlignment.CENTER)
				return "CENTER";
			else
				return "RIGHT";
		}
	}

	private sealed class CurrentRedrawMethod : FunctionMethod
	{
		public CurrentRedrawMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return (exm.Console.Redraw == GameView.ConsoleRedraw.None) ? 0L : 1L;
		}
	}

	private sealed class ColorFromNameMethod : FunctionMethod
	{
		public ColorFromNameMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string colorName = arguments[0].GetStrValue(exm);
			int i;
			if (EmuColor.TryFromName(colorName, out var color))
			{
				i = (color.R << 16) + (color.G << 8) + color.B;
			}
			else
			{
				if (colorName.Equals("transparent", StringComparison.OrdinalIgnoreCase))
					throw new CodeEE(trerror.TransparentUnsupported.Text);
				i = -1;
			}
			return i;
		}
	}

	private sealed class ColorFromRGBMethod : FunctionMethod
	{
		public ColorFromRGBMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long r = arguments[0].GetIntValue(exm);
			if (r < 0 || r > 255)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 1, r, 0, 255));
			long g = arguments[1].GetIntValue(exm);
			if (g < 0 || g > 255)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 2, g, 0, 255));
			long b = arguments[2].GetIntValue(exm);
			if (b < 0 || b > 255)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 3, b, 0, 255));
			return (r << 16) + (g << 8) + b;
		}
	}

	private sealed class GetPrintCPerLineMethod : FunctionMethod
	{
		public GetPrintCPerLineMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return Config.PrintCPerLine;
		}
	}

	// ======== Skia 方言 P0：字符串动态求值（实现票 18，spec §5） ========
	// 对齐 Skia 语义：运行时解析表达式（CanRestructure=false，绝不能编译期折叠），
	// 解析/求值失败一律返回缺省值（第二参）。era-core 无 Float 类型，故只处理 Integer / String 分支。

	private sealed class EvalMethod : FunctionMethod
	{
		public EvalMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.Int }, OmitStart = 1 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string expressionStr = arguments[0].GetStrValue(exm);
			long defaultValue = arguments.Count > 1 && arguments[1] != null ? arguments[1].GetIntValue(exm) : 0;
			if (string.IsNullOrWhiteSpace(expressionStr))
				return defaultValue;
			try
			{
				CharStream st = new CharStream(expressionStr);
				WordCollection wc = LexicalAnalyzer.Analyse(st, LexEndWith.EoL, LexAnalyzeFlag.None);
				AExpression term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);
				if (term == null)
					return defaultValue;
				term = term.Restructure(exm);
				return term.GetOperandType() == typeof(long) ? term.GetIntValue(exm) : defaultValue;
			}
			catch (EmueraException)
			{
				return defaultValue;
			}
		}
	}

	private sealed class EvalSMethod : FunctionMethod
	{
		public EvalSMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String }, OmitStart = 1 }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string expressionStr = arguments[0].GetStrValue(exm);
			string defaultValue = arguments.Count > 1 && arguments[1] != null ? arguments[1].GetStrValue(exm) : "";
			if (string.IsNullOrWhiteSpace(expressionStr))
				return defaultValue;
			try
			{
				CharStream st = new CharStream(expressionStr);
				WordCollection wc = LexicalAnalyzer.Analyse(st, LexEndWith.EoL, LexAnalyzeFlag.None);
				AExpression term = ExpressionParser.ReduceExpressionTerm(wc, TermEndWith.EoL);
				if (term == null)
					return defaultValue;
				term = term.Restructure(exm);
				return term.GetOperandType() == typeof(string) ? term.GetStrValue(exm) : defaultValue;
			}
			catch (EmueraException)
			{
				return defaultValue;
			}
		}
	}
}
