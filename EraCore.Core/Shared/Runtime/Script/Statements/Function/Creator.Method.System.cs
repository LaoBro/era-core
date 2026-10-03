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
using System.IO;
using System.Linq;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class IsDefinedMethod : FunctionMethod
	{
		public IsDefinedMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return (GlobalStatic.IdentifierDictionary.GetMacro(arguments[0].GetStrValue(exm)) != null) ? 1 : 0;
		}
	}

	private sealed class EnumNameMethod : FunctionMethod
	{
		public enum EType
		{
			Function,
			Variable,
			Macro
		}
		public enum EAction
		{
			BeginsWith,
			EndsWith,
			With
		}
		private EType type;
		private EAction action;
		public EnumNameMethod(EType type, EAction act)
		{
			ReturnType = typeof(long);
			CanRestructure = false;
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString1D }, OmitStart = 1 },
				];
			this.type = type;
			action = act;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string arg = arguments[0].GetStrValue(exm).ToUpper();
			string[] array = null!;
			switch (type)
			{
				case EType.Function:
					array = GlobalStatic.Process.LabelDictionary.NoneventKeys;
					break;
				case EType.Variable:
					array = GlobalStatic.IdentifierDictionary.VarKeys;
					break;
				case EType.Macro:
					array = GlobalStatic.IdentifierDictionary.MacroKeys;
					break;
			}
			List<string> strs = [];
			if (arg.Length > 0)
				foreach (string item in array!)
			{
				if (item.Length < arg.Length) continue;
				switch (action)
					{
						case EAction.BeginsWith:
							if (item.ToUpper().StartsWith(arg, StringComparison.Ordinal)) strs.Add(item);
							break;
						case EAction.EndsWith:
							if (item.ToUpper().LastIndexOf(arg, StringComparison.Ordinal) == item.Length - arg.Length) strs.Add(item);
							break;
						case EAction.With:
							if (item.ToUpper().Contains(arg, StringComparison.Ordinal)) strs.Add(item);
							break;
					}
				}
			string[] output;
			if (arguments.Count == 2)
				output = ((arguments[1] as VariableTerm)!.Identifier.GetArray() as string[])!;
			else
				output = exm.VEvaluator.RESULTS_ARRAY;
			string[] ret = strs.ToArray();
			int outputlength = Math.Min(output.Length, ret.Length);
			Array.Copy(ret, output, outputlength);
			return outputlength;
		}
	}

	private sealed class EnumFilesMethod : FunctionMethod
	{
		public EnumFilesMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.Int, ArgType.RefString1D }, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dir = Utils.GetValidPath(arguments[0].GetStrValue(exm));
			if (dir == null || !SafCompat.DirectoryExists(dir)) return -1;
			var pattern = arguments.Count > 1 ? arguments[1].GetStrValue(exm) : "*";
			var option = arguments.Count > 2
				? (arguments[2].GetIntValue(exm) == 0 ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories)
				: SearchOption.TopDirectoryOnly;
			string[] files;
			try
			{
				files = SafCompat.GetFiles(dir, pattern, option);
				for (int i = 0; i < files.Length; i++)
				{
					files[i] = SafPath.GetRelativePathFromRoot(Program.ExeDir, files[i]);
				}

			}
			catch (Exception e)
			{
				if (e is CodeEE)
					throw;
				EmueraLog.Warn(Name, e.Message);
				return -1;
			}
			string[] output;
			if (arguments.Count == 4)
				output = ((arguments[3] as VariableTerm)!.Identifier.GetArray() as string[])!;
			else
				output = exm.VEvaluator.RESULTS_ARRAY;
			var ret = Math.Min(files.Length, output.Length);
			Array.Copy(files, output, ret);
			return ret;
		}
	}

	private sealed class MoveTextBoxMethod : FunctionMethod
	{
		public MoveTextBoxMethod(bool b = false)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
			resume = b;
		}
		bool resume;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (resume) exm.Console.UIAdapter.ResetTextBoxPos();
			else exm.Console.UIAdapter.SetTextBoxPos(
				(int)arguments[0].GetIntValue(exm),
				(int)arguments[1].GetIntValue(exm),
				(int)arguments[2].GetIntValue(exm));
			return 1;
		}
	}

	private sealed class GetSaveNosMethod : FunctionMethod
	{
		public GetSaveNosMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return Config.SaveDataNos;
		}
	}

	public sealed class GetConfigMethod : FunctionMethod
	{
		public GetConfigMethod(bool typeisInt)
		{
			if (typeisInt)
			{
				funcname = "GETCONFIG";
				ReturnType = typeof(long);
			}
			else
			{
				funcname = "GETCONFIGS";
				ReturnType = typeof(string);
			}
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		private readonly string funcname;
		private SingleTerm GetSingleTerm(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			if (str == null || str.Length == 0)
				throw new CodeEE(string.Format(trerror.ArgIsEmptyString.Text, Name, 1));
			string errMes = null!;
			SingleTerm term = ConfigData.GetConfigValueInERB(str, ref errMes)!;
			if (errMes != null)
				throw new CodeEE(errMes);
			return term;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (ReturnType != typeof(long))
				throw new ExeEE(funcname + "関数:不正な呼び出し");
			SingleTerm term = GetSingleTerm(exm, arguments);
			if (term is not SingleLongTerm singleLongTerm)
				throw new CodeEE(string.Format(trerror.InvalidType.Text, Name, "GETCONFIGS"));
			return singleLongTerm.Int;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (ReturnType != typeof(string))
				throw new ExeEE(funcname + "関数:不正な呼び出し");
			SingleTerm term = GetSingleTerm(exm, arguments);
			if (term is not SingleStrTerm singleStrTerm)
				throw new CodeEE(string.Format(trerror.InvalidType.Text, Name, "GETCONFIG"));
			return singleStrTerm.Str;
		}
	}

	public sealed class ClientSizeMethod : FunctionMethod
	{
		public ClientSizeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			switch (Name)
			{
				case "CLIENTWIDTH":
					return exm.Console.ClientWidth;
				case "CLIENTHEIGHT":
					return exm.Console.ClientHeight;
			}
			throw new ExeEE("ClientSize:" + Name + ":異常な分岐");
		}
	}

	public sealed class GraphicsDisposeMethod : FunctionMethod
	{
		public GraphicsDisposeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (Config.TextDrawingMode == TextDrawingMode.WINAPI)
				throw new CodeEE(string.Format(trerror.GDIPlusOnly.Text, Name));
			GraphicsImage g = ReadGraphics(Name, exm, arguments, 0);
			if (!g.IsCreated)
				return 0;
			g.GDispose();
			return 1;
		}
	}

	static readonly short[] keytoggle = new short[256];

	private sealed class GetKeyStateMethod : FunctionMethod
	{
		public GetKeyStateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (!exm.Console.IsActive)//アクティブでないならスルー
				return 0;
			long keycode = arguments[0].GetIntValue(exm);
			if (keycode < 0 || keycode > 255)
				return 0;
#if HEADLESS
			short s = 0;
#else
			short s = WinInput.GetKeyState((int)keycode);
#endif
			short toggle = keytoggle[keycode];
			keytoggle[keycode] = (short)((s & 1) + 1);//初期値0、トグル状態に応じて1か2を代入。
			switch (Name)
			{
				case "GETKEY": return (s < 0) ? 1 : 0;
				case "GETKEYTRIGGERED": return (s < 0) && (toggle != keytoggle[keycode]) ? 1 : 0;//初回はtrue、2回目以降はトグル状態が前回と違う場合のみ1
			}
			throw new ExeEE("異常な分岐");
		}
	}

	private sealed class MousePosMethod : FunctionMethod
	{
		public MousePosMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			switch (Name)
			{
				case "MOUSEX": return exm.Console.GetMousePosition().X;
				case "MOUSEY": return exm.Console.GetMousePosition().Y;
			}
			throw new ExeEE("異常な名前");
		}
	}

	private sealed class MouseButtonMethod : FunctionMethod
	{
		public MouseButtonMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
#if HEADLESS
			// Headless 模式下无鼠标交互
			return "";
#else
			bool b = exm.Console.AlwaysRefresh;
			Point point = exm.Console.UIAdapter.MainPicBox.PointToClient(exm.Console.UIAdapter.GetCursorPosition());
			exm.Console.AlwaysRefresh = true;
			if (exm.Console.UIAdapter.MainPicBox.ClientRectangle.Contains(point))
				exm.Console.MoveMouse(point);
			exm.Console.AlwaysRefresh = b;
			if (exm.Console.PointingSring != null)
			{
				if (!exm.Console.PointingSring.IsButton)
					return "";
				if (exm.Console.PointingSring.IsInteger)
					return exm.Console.PointingSring.Input.ToString();
				return exm.Console.PointingSring.Inputs;
			}
			return "";
#endif
		}
	}

	private sealed class IsActiveMethod : FunctionMethod
	{
		public IsActiveMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return exm.Console.IsActive ? 1 : 0;
		}
	}

	private sealed class SetAnimeTimerMethod : FunctionMethod
	{
		public SetAnimeTimerMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long i64 = arguments[0].GetIntValue(exm);
			if (i64 < int.MinValue || i64 > short.MaxValue)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 1, i64, int.MinValue, short.MaxValue));
			exm.Console.setRedrawTimer((int)i64);
			return 1;
		}
	}

	/// <summary>
	/// Skia 方言 P0：GETANIMETIMER（实现票 28，spec §5 追加 2）。返回当前重绘定时器周期（毫秒），
	/// 未启用为 0——与既有 <see cref="SetAnimeTimerMethod"/> 读写成对（对齐 Skia <c>EmueraConsole.AnimeTimer</c>）。
	/// </summary>
	private sealed class GetAnimeTimerMethod : FunctionMethod
	{
		public GetAnimeTimerMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> exm.Console.getRedrawTimer();
	}

	/// <summary>
	/// Skia 方言 P0：GETPLATFORM（实现票 28，spec §5 追加 2）。纯函数，平台编码：
	/// 0=Windows / 1=Android / 2=iOS / 3=macOS / 4=Linux / 5=Unknown，可常量折叠。
	/// </summary>
	private sealed class GetPlatformMethod : FunctionMethod
	{
		public GetPlatformMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			if (OperatingSystem.IsWindows()) return 0;
			if (OperatingSystem.IsAndroid()) return 1;
			if (OperatingSystem.IsIOS()) return 2;
			if (OperatingSystem.IsMacOS()) return 3;
			if (OperatingSystem.IsLinux()) return 4;
			return 5;
		}
	}

	private sealed class ExistSoundMethod : FunctionMethod
	{
		public ExistSoundMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string str = arguments[0].GetStrValue(exm);
			string filepath = SafCompat.CombinePath(Program.SoundDir, str);
			if (SafCompat.FileExists(filepath))
				return 1;
			return 0;
		}
	}

	public sealed class ExistFunctionMethod : FunctionMethod
	{

		public ExistFunctionMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int}, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string functionname = arguments[0].GetStrValue(exm);
			if (arguments.Count == 1 || arguments[1].GetIntValue(exm) == 0)
			{
				FunctionLabelLine func;
				if (Config.StringComparison == StringComparison.OrdinalIgnoreCase)
					func = GlobalStatic.LabelDictionary.GetNonEventLabel(functionname.ToUpper());
				else
					func = GlobalStatic.LabelDictionary.GetNonEventLabel(functionname);
				if (func == null)
					return 0;
				if (func.IsMethod)
				{
					if (func.MethodType == typeof(string))
						return 3;
					else if (func.MethodType == typeof(long))
						return 2;

				}
				return 1;
			}
			else
			{
				foreach (string funcname in GlobalStatic.Process.LabelDictionary.NoneventKeys)
				{
					if (string.Equals(funcname, functionname, StringComparison.OrdinalIgnoreCase))
					{
						FunctionLabelLine func = GlobalStatic.LabelDictionary.GetNonEventLabel(funcname);

						if (func.IsMethod)
						{
							if (func.MethodType == typeof(string))
								return 3;
							else if (func.MethodType == typeof(long))
								return 2;

						}
						return 1;
					}
				}
				return 0;
			}
		}
	}

	private sealed class GetUsingMemoryMethod : FunctionMethod
	{
		public GetUsingMemoryMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			using (System.Diagnostics.Process memory = System.Diagnostics.Process.GetCurrentProcess())
			{
				return memory.WorkingSet64;
			}
		}
	}

	private sealed class ClearMemoryMethod : FunctionMethod
	{
		public ClearMemoryMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			using (System.Diagnostics.Process destmemory = System.Diagnostics.Process.GetCurrentProcess())
			{
				long destmemorysize = destmemory.WorkingSet64;
				GC.Collect();
				using (System.Diagnostics.Process memory = System.Diagnostics.Process.GetCurrentProcess())
				{
					return destmemorysize - memory.WorkingSet64;
				}
			}
		}
	}

	private sealed class GetTextBoxMethod : FunctionMethod
	{
		public GetTextBoxMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return exm.Console.UIAdapter.TextBox.Text;
		}
	}

	private sealed class ChangeTextBoxMethod : FunctionMethod
	{
		public ChangeTextBoxMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			exm.Console.UIAdapter.ChangeTextBox(arguments[0].GetStrValue(exm));
			return 1;
		}
	}

	private sealed class ErdNameMethod : FunctionMethod
	{
		public ErdNameMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.RefAny | ArgType.AllowConstRef, ArgType.Int, ArgType.Int }, OmitStart = 2 },
				];
			CanRestructure = true;
			HasUniqueRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			VariableTerm vToken = (VariableTerm)arguments[0];
			string varname = "";
			if (arguments.Count > 2)
				varname = vToken.Identifier.Name + "@" + arguments[2].GetIntValue(exm);
			else
				varname = vToken.Identifier.Name;
			long value = arguments[1].GetIntValue(exm);
			if (exm.VEvaluator.Constant.TryIntegerToKeyword(out string ret, value, varname))
				return ret;
			else
				return "";
		}
		public override bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
		{
			arguments[1] = arguments[1].Restructure(exm);
			return arguments[1] is SingleTerm;
		}
	}

	private sealed class GetDisplayLineMethod : FunctionMethod
	{
		public GetDisplayLineMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int }},
				];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			//修正に失敗したので差し戻す
			long num = arguments[0].GetIntValue(exm);
			if (num < 0 || num >= exm.Console.DisplayLineList.Count)
				return "";
			else
				return exm.Console.DisplayLineList[(int)num].ToString();
		}
	}

	private sealed class GetDoingFunctionMethod : FunctionMethod
	{
		public GetDoingFunctionMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			// erb-loader-parallel 08：加载期读者统一经 GetLoadScaningLine
			LogicalLine line = LogicalLineParser.GetLoadScaningLine() ?? exm.Process.GetScaningLine();
			if ((line == null) || (line.ParentLabelLine == null))
				return "";//システム待機中のデバッグモードから呼び出し
			return line.ParentLabelLine.LabelName;
		}
	}

	private sealed class FlowInputMethod : FunctionMethod
	{
		public FlowInputMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = new ArgTypeList[] {
					new() { ArgTypes = { ArgType.Int, ArgType.Int, ArgType.Int, ArgType.Int }, OmitStart = 1 },
				};
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{

			exm.Process.executionState.flowinputDef = arguments[0].GetIntValue(exm);
			if (arguments.Count > 1)
				exm.Process.executionState.flowinput = arguments[1].GetIntValue(exm) != 0 ? true : false ;
			if (arguments.Count > 2)
				exm.Process.executionState.flowinputCanSkip = arguments[2].GetIntValue(exm) != 0 ? true : false ;
			if (arguments.Count > 3)
				exm.Process.executionState.flowinputForceSkip = arguments[3].GetIntValue(exm) != 0 ? true : false;
			return 0;
		}
	}

	private sealed class FlowInputsMethod : FunctionMethod
	{
		public FlowInputsMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = new ArgTypeList[] {
					new() { ArgTypes = { ArgType.Int, ArgType.String }, OmitStart = 1 },
				};
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{

			exm.Process.executionState.flowinputString = arguments[0].GetIntValue(exm) != 0 ? true : false ;
			if (arguments.Count > 1)
				exm.Process.executionState.flowinputDefString = arguments[1].GetStrValue(exm);
			return 0;
		}
	}

	private sealed class GetMethMethod : FunctionMethod
	{
		public GetMethMethod()
		{
			ReturnType = typeof(Int64);
			argumentTypeArrayEx = new ArgTypeList[] {
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.VariadicAny }, OmitStart = 1 },
				};
			CanRestructure = false;
		}

		public override Int64 GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string name = arguments[0].GetStrValue(exm);
			List<AExpression> methArgs = new List<AExpression>(arguments.Skip(2).ToArray());
			var term = GlobalStatic.IdentifierDictionary.GetFunctionMethod(GlobalStatic.LabelDictionary, name, methArgs, true);

			if (term == null)
			{
				if (arguments.Count < 2 || arguments[1] == null)
					throw new CodeEE(string.Format(trerror.NotDefinedUserFunc.Text, name));
				else
					return arguments[1].GetIntValue(exm);
			}
			else if (!term.IsInteger)
				throw new CodeEE(string.Format(trerror.IsNotInt.Text, name));
			else
				return term.GetIntValue(exm);
		}
	}

	private sealed class GetMethsMethod : FunctionMethod
	{
		public GetMethsMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = new ArgTypeList[] {
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.VariadicAny }, OmitStart = 1 },
				};
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string name = arguments[0].GetStrValue(exm);
			List<AExpression> methArgs = new List<AExpression>(arguments.Skip(2).ToArray());
			var term = GlobalStatic.IdentifierDictionary.GetFunctionMethod(GlobalStatic.LabelDictionary, name, methArgs, true);

			if (term == null)
			{
				if (arguments.Count < 2 || arguments[1] == null)
					throw new CodeEE(string.Format(trerror.NotDefinedUserFunc.Text, name));
				else
					return arguments[1].GetStrValue(exm);
			}
			else if (!term.IsString)
				throw new CodeEE(string.Format(trerror.IsNotStr.Text, name));
			else
				return term.GetStrValue(exm);
		}
	}

	private sealed class ExistMethMethod : FunctionMethod
	{
		public ExistMethMethod()
		{
			ReturnType = typeof(Int64);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}

		public override Int64 GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string name = arguments[0].GetStrValue(exm);
			AExpression term;
			try
			{
				term = GlobalStatic.IdentifierDictionary.GetFunctionMethod(GlobalStatic.LabelDictionary, name, new List<AExpression>(), true);
			}
			catch (CodeEE)
			{
				return 0;
			}

			if (term == null)
			{
				return 0;
			}
			else
			{
				Int64 res = 0;
				if (term.IsInteger) res |= 1;
				if (term.IsString) res |= 2;
				return res;
			}
		}
	}

	private sealed class BitmapCacheEnableMethod : FunctionMethod
	{
		public BitmapCacheEnableMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int }, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long argument0 = arguments[0].GetIntValue(exm);
			GlobalStatic.Console.bitmapCacheEnabledForNextLine = argument0 != 0;
			return 0;
		}
	}

	private sealed class HotkeyStateMethod : FunctionMethod
	{
		public HotkeyStateMethod()
		{
			ReturnType = typeof(Int64);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int}, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		public override Int64 GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			Int64 argument0 = arguments[0].GetIntValue(exm);
			Int64 argument1 = arguments[1].GetIntValue(exm);
#if !HEADLESS
			GlobalStatic.Console.Window.hotkeyState.HotkeyStateSet((nint)argument0, (nint)argument1);
#endif
			return 0;
		}
	}

	private sealed class HotkeyStateInitMethod : FunctionMethod
	{
		public HotkeyStateInitMethod()
		{
			ReturnType = typeof(Int64);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int}, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		public override Int64 GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			Int64 argument0 = arguments[0].GetIntValue(exm);
#if !HEADLESS
			GlobalStatic.Console.Window.hotkeyState.HotkeyStateInit((nint)argument0);
#endif
			return 0;
		}
	}

	private sealed class OutputlogMethod : FunctionMethod
	{
		public OutputlogMethod()
		{
			ReturnType = typeof(Int64);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int}, OmitStart = 0 },
				];
			CanRestructure = false;
		}
		public override Int64 GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string filename = "";
			if (arguments.Count > 0)
				filename = arguments[0].GetStrValue(exm);
			bool hideInfo = false;
			if (arguments.Count > 1)
				hideInfo = arguments[1].GetIntValue(exm) == 1;

	
			exm.Console.OutputLog(filename, hideInfo);
			return 1;
		}

	}
}
