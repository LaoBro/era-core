	using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameProc;

internal sealed class ScriptProc
{
	readonly EmueraConsole console;
	readonly IVariableEvaluator vEvaluator;
	readonly ExpressionMediator exm;
	readonly IdentifierDictionary idDic;
	readonly LabelDictionary labelDic;
	readonly ExecutionState executionState;
	readonly GameBase gameBase;
	readonly string[] trainName;
	IProcessState state => executionState.CurrentState;
	bool saveSkip;
	bool userDefinedSkip;

	internal ScriptProc(EmueraConsole console, IVariableEvaluator vEvaluator,
		ExpressionMediator exm, IdentifierDictionary idDic, ExecutionState executionState,
		GameBase gameBase, string[] trainName, LabelDictionary labelDic)
	{
		this.console = console;
		this.vEvaluator = vEvaluator;
		this.exm = exm;
		this.idDic = idDic;
		this.executionState = executionState;
		this.gameBase = gameBase;
		this.trainName = trainName;
		this.labelDic = labelDic;
	}

	/// <summary>
	/// 将 ERB FONTSTYLE 位掩码转换为无头核心样式。
	/// ERB 位契约（与原版 Emuera 一致，见 emuera.em Process.ScriptProc.cs）：
	/// 位1=加粗、位2=斜体、位4=删除线（取消線）、位8=下划线（下線）。
	/// 注意：与内部 EmuFontStyle 枚举（.NET 契约 Underline=4、Strikeout=8）恰好相反，
	/// 因此 4/8 必须用 ERB 位字面量做转换，不能引用 EmuFontStyle.Underline/Strikeout.Value。
	/// </summary>
	internal static EmuFontStyle ParseFontStyle(long value)
	{
		var style = EmuFontStyle.Regular;
		if ((value & 1) != 0)
			style |= EmuFontStyle.Bold;
		if ((value & 2) != 0)
			style |= EmuFontStyle.Italic;
		if ((value & 4) != 0)
			style |= EmuFontStyle.Strikeout;
		if ((value & 8) != 0)
			style |= EmuFontStyle.Underline;
		return style;
	}

	public void Run()
	{
		while (true)
		{
			// issue-002：每条指令执行前的中断检查点 —— 会话拆除（RequestStop → IsRunning
			// 变假）后脚本在此停下，而不是把剩余循环跑完。
			//
			// 位置为何必须是「每条指令之前」而不是循环体尾部：
			//   1. 票面要求的语义，亦与参考实现同位置；
			//   2. 尾部位置会被本方法内的三处 continue（Debug 指令跳过 / skipPrint 跳过 /
			//      行标签）整体绕过 —— 那些路径下永远轮不到检查。
			// 与旧的尾部检查落在同一组检查点上：稳定执行流里「下一行执行前」等价于
			// 「本行执行后的下一次检查」，故既有的让路语义不变（由回归测试守住）。
			//
			// ScriptEnd 必须与 IsRunning 一并留在此判断里：函数全部返回后帧栈为空，
			// 若推迟到 ShiftNextLine 之后再判，会对着空栈取下一行（NRE）。
			if (!console.IsRunning || state.ScriptEnd)
				return;
			state.ShiftNextLine();
			LogicalLine line = state.CurrentLine;
			if (line.IsError)
				throw new CodeEE(line.ErrMes);
			else if (line is InstructionLine func)
			{
				if (!Program.DebugMode && func.Function.IsDebug())
				{
					continue;
				}
				if (func.Argument == null)
				{
					ArgumentParser.SetArgumentTo(func);
					if (func.IsError)
						throw new CodeEE(func.ErrMes);
				}
				if (executionState.skipPrint && func.Function.IsPrint())
				{
					if (userDefinedSkip && func.Function.IsInput())
					{
						console.PrintError(trerror.SkipdispInputError1.Text);
						console.PrintError(trerror.SkipdispInputError2.Text);
						throw new CodeEE(trerror.SkipdispInputError3.Text);
					}
					continue;
				}
				if (func.Function.Instruction != null)
					func.Function.Instruction.DoInstruction(exm, func, executionState.CurrentState);
				else if (func.Function.IsFlowContorol())
					doFlowControlFunction(func);
				else
					doNormalFunction(func);
			}
			else if ((line is NullLine) || (line is FunctionLabelLine))
			{
				if (!state.IsFunctionMethod)
					vEvaluator.RESULT = 0;
				state.Return(0);
			}
			else if (line is GotoLabelLine)
				continue;
			else if (line is InvalidLine)
			{
				if (string.IsNullOrEmpty(line.ErrMes))
					throw new CodeEE(trerror.DoFailedLine.Text);
				else
					throw new CodeEE(line.ErrMes);
			}
			// 运行检查已上移到循环体首部（issue-002），此处不再重复。
		}
	}

	public void DoDebugNormalFunction(InstructionLine func, bool munchkin)
	{
		if (func.Function.Instruction != null)
			func.Function.Instruction.DoInstruction(exm, func, executionState.CurrentState);
		else
			doNormalFunction(func);
		if (munchkin)
			vEvaluator.IamaMunchkin();
	}

	internal void SetCommnds(long count)
	{
		executionState.coms = new List<long>((int)count);
		executionState.isCTrain = true;
		long[] selectcom = vEvaluator.SELECTCOM_ARRAY;
		if (count >= selectcom.Length)
		{
			throw new CodeEE(trerror.CalltrainArgMoreThanSelectcom.Text);
		}
		for (int i = 0; i < (int)count; i++)
		{
			executionState.coms.Add(selectcom[i + 1]);
		}
	}

	void doNormalFunction(InstructionLine func)
	{
		long iValue = 0;
		string str = null!;
		AExpression term = null!;
		switch (func.FunctionCode)
		{

			case FunctionCode.PRINTBUTTON:
				{
					if (executionState.skipPrint)
						break;
					exm.Console.UseUserStyle = true;
					exm.Console.UseSetColorStyle = true;
					SpButtonArgument bArg = (SpButtonArgument)func.Argument;
					str = bArg.PrintStrTerm.GetStrValue(exm);
					str = str.Replace("\n", "");
					if (bArg.ButtonWord.GetOperandType() == typeof(long))
						exm.Console.PrintButton(str, bArg.ButtonWord.GetIntValue(exm));
					else
						exm.Console.PrintButton(str, bArg.ButtonWord.GetStrValue(exm));
				}
				break;
			case FunctionCode.PRINTBUTTONC:
			case FunctionCode.PRINTBUTTONLC:
				{
					if (executionState.skipPrint)
						break;
					exm.Console.UseUserStyle = true;
					exm.Console.UseSetColorStyle = true;
					SpButtonArgument bArg = (SpButtonArgument)func.Argument;
					str = bArg.PrintStrTerm.GetStrValue(exm);
					str = str.Replace("\n", "");
					bool isRight = (func.FunctionCode == FunctionCode.PRINTBUTTONC);
					if (bArg.ButtonWord.GetOperandType() == typeof(long))
						exm.Console.PrintButtonC(str, bArg.ButtonWord.GetIntValue(exm), isRight);
					else
						exm.Console.PrintButtonC(str, bArg.ButtonWord.GetStrValue(exm), isRight);
				}
				break;
			case FunctionCode.PRINTPLAIN:
			case FunctionCode.PRINTPLAINFORM:
				{
					if (executionState.skipPrint)
						break;
					exm.Console.UseUserStyle = true;
					exm.Console.UseSetColorStyle = true;
					term = ((ExpressionArgument)func.Argument).Term;
					exm.Console.PrintPlain(term.GetStrValue(exm));
				}
				break;
			case FunctionCode.DRAWLINE:
				if (executionState.skipPrint)
					break;
				exm.Console.PrintBar();
				exm.Console.NewLine();
				break;
			case FunctionCode.DRAWLINEFORM:
				{
					if (executionState.skipPrint)
						break;
					term = ((ExpressionArgument)func.Argument).Term;
					str = term.GetStrValue(exm);
					exm.Console.printCustomBar(str, false);
					exm.Console.NewLine();
				}
				break;
			case FunctionCode.PRINT_ABL:
			case FunctionCode.PRINT_TALENT:
			case FunctionCode.PRINT_MARK:
			case FunctionCode.PRINT_EXP:
				{
					if (executionState.skipPrint)
						break;
					ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
					long target = intExpArg.Term.GetIntValue(exm);
					exm.Console.Print(vEvaluator.GetCharacterDataString(target, func.FunctionCode));
					exm.Console.NewLine();
				}
				break;
			case FunctionCode.PRINT_PALAM:
				{
					if (executionState.skipPrint)
						break;
					ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
					long target = intExpArg.Term.GetIntValue(exm);
					int count = 0;
					for (int i = 0; i < 100; i++)
					{
						string printStr = vEvaluator.GetCharacterParamString(target, i);
						if (printStr != null)
						{
							exm.Console.PrintC(printStr, true);
							count++;
							if ((Config.PrintCPerLine > 0) && (count % Config.PrintCPerLine == 0))
								exm.Console.PrintFlush(false);
						}
					}
					exm.Console.PrintFlush(false);
					exm.Console.RefreshStrings(false);
				}
				break;
			case FunctionCode.PRINT_ITEM:
				if (executionState.skipPrint)
					break;
				exm.Console.Print(vEvaluator.GetHavingItemsString());
				exm.Console.NewLine();
				break;
			case FunctionCode.PRINT_SHOPITEM:
				{
					if (executionState.skipPrint)
						break;
					int length = Math.Min(vEvaluator.ITEMSALES.Length, vEvaluator.ITEMNAME.Length);
					if (length > vEvaluator.ITEMPRICE.Length)
						length = vEvaluator.ITEMPRICE.Length;
					int count = 0;
					for (int i = 0; i < length; i++)
					{
						if (vEvaluator.ItemSales(i))
						{
							string printStr = vEvaluator.ITEMNAME[i];
							if (printStr == null)
								printStr = "";
							long price = vEvaluator.ITEMPRICE[i];
							if (Config.MoneyFirst)
								exm.Console.PrintC(string.Format("[{2}] {0}({3}{1})", printStr, price, i, Config.MoneyLabel), false);
							else
								exm.Console.PrintC(string.Format("[{2}] {0}({1}{3})", printStr, price, i, Config.MoneyLabel), false);
							count++;
							if ((Config.PrintCPerLine > 0) && (count % Config.PrintCPerLine == 0))
								exm.Console.PrintFlush(false);
						}
					}
					exm.Console.PrintFlush(false);
					exm.Console.RefreshStrings(false);
				}
				break;
			case FunctionCode.UPCHECK:
				vEvaluator.UpdateInUpcheck(exm.Console, executionState.skipPrint);
				break;
			case FunctionCode.CUPCHECK:
				{
					ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
					long target = intExpArg.Term.GetIntValue(exm);
					vEvaluator.CUpdateInUpcheck(exm.Console, target, executionState.skipPrint);
				}
				break;
			case FunctionCode.DELALLCHARA:
				{
					vEvaluator.DelAllCharacter();
					break;
				}
			case FunctionCode.PICKUPCHARA:
				{
					ExpressionArrayArgument intExpArg = (ExpressionArrayArgument)func.Argument;
					long[] NoList = new long[intExpArg.TermList.Length];
					long charaNum = vEvaluator.CHARANUM;
					for (int i = 0; i < intExpArg.TermList.Length; i++)
					{
						AExpression term_i = intExpArg.TermList[i];
						NoList[i] = term_i.GetIntValue(exm);
						if (!(term_i is VariableTerm) || ((((VariableTerm)term_i).Identifier.Code != VariableCode.MASTER) && (((VariableTerm)term_i).Identifier.Code != VariableCode.ASSI) && (((VariableTerm)term_i).Identifier.Code != VariableCode.TARGET)))
							if (NoList[i] < 0 || NoList[i] >= charaNum)
								throw new CodeEE(string.Format(trerror.OoRPickupcharaArg.Text, (i + 1).ToString(), NoList[i].ToString()));
					}
					vEvaluator.PickUpChara(NoList);
				}
				break;
			case FunctionCode.ADDDEFCHARA:
				{
					if ((func.ParentLabelLine != null) && (func.ParentLabelLine.LabelName != "SYSTEM_TITLE"))
						throw new CodeEE(trerror.CanNotUseOutsideSystemtitle.Text);
					vEvaluator.AddCharacterFromCsvNo(0);
					if (gameBase.DefaultCharacter > 0)
						vEvaluator.AddCharacterFromCsvNo(gameBase.DefaultCharacter);
					break;
				}
			case FunctionCode.PUTFORM:
				{
					term = ((ExpressionArgument)func.Argument).Term;
					str = term.GetStrValue(exm);
					if (vEvaluator.SAVEDATA_TEXT != null)
						vEvaluator.SAVEDATA_TEXT += str;
					else
						vEvaluator.SAVEDATA_TEXT = str;
					break;
				}
			case FunctionCode.QUIT:
				exm.Console.Quit();
				break;
			case FunctionCode.QUIT_AND_RESTART:
				Program.rebootFlag = true;
				exm.Console.Quit();
				break;
			case FunctionCode.FORCE_QUIT:
				exm.Console.ForceQuit();
				break;
			case FunctionCode.FORCE_QUIT_AND_RESTART:
				Program.rebootFlag = true;
				exm.Console.ForceQuit();
				break;

			case FunctionCode.VARSIZE:
				{
					SpVarsizeArgument versizeArg = (SpVarsizeArgument)func.Argument;
					VariableToken varID = versizeArg.VariableID;
					vEvaluator.VarSize(varID);
				}
				break;
			case FunctionCode.SAVEDATA:
				{
					SpSaveDataArgument spSavedataArg = (SpSaveDataArgument)func.Argument;
					long target = spSavedataArg.Target.GetIntValue(exm);
					if (target < 0)
						throw new CodeEE(string.Format(trerror.SavedataArgIsNegative.Text, target.ToString()));
					else if (target > int.MaxValue)
						throw new CodeEE(string.Format(trerror.TooLargeSavedataArg.Text, target.ToString()));
					string savemes = spSavedataArg.StrExpression.GetStrValue(exm);
					if (savemes.Contains('\n'))
						throw new CodeEE(trerror.SavetextContainNewLineCharacter.Text);
					if (!vEvaluator.SaveTo((int)target, savemes))
					{
						console.PrintError(trerror.UnexpectedErrorInSavedata.Text);
					}
				}
				break;

			case FunctionCode.POWER:
				{
					SpPowerArgument powerArg = (SpPowerArgument)func.Argument;
					double x = powerArg.X.GetIntValue(exm);
					double y = powerArg.Y.GetIntValue(exm);
					double pow = Math.Pow(x, y);
					if (double.IsNaN(pow))
						throw new CodeEE(trerror.PowerResultNonNumeric.Text);
					else if (double.IsInfinity(pow))
						throw new CodeEE(trerror.PowerResultInfinite.Text);
					else if ((pow >= long.MaxValue) || (pow <= long.MinValue))
						throw new CodeEE(string.Format(trerror.PowerResultOverflow.Text, pow.ToString()));
					powerArg.VariableDest.SetValue((long)pow, exm);
					break;
				}
			case FunctionCode.SWAP:
				{
					SpSwapVarArgument arg = (SpSwapVarArgument)func.Argument;
					FixedVariableTerm vTerm1 = arg.var1.GetFixedVariableTerm(exm);
					FixedVariableTerm vTerm2 = arg.var2.GetFixedVariableTerm(exm);
					if (vTerm1.GetOperandType() != vTerm2.GetOperandType())
						throw new CodeEE(trerror.VarsTypeDifferent.Text);
					if (vTerm1.GetOperandType() == typeof(long))
					{
						long temp = vTerm1.GetIntValue(exm);
						vTerm1.SetValue(vTerm2.GetIntValue(exm), exm);
						vTerm2.SetValue(temp, exm);
					}
					else if (arg.var1.GetOperandType() == typeof(string))
					{
						string temps = vTerm1.GetStrValue(exm);
						vTerm1.SetValue(vTerm2.GetStrValue(exm), exm);
						vTerm2.SetValue(temps, exm);
					}
					else
					{
						throw new CodeEE(trerror.UnknownVarType.Text);
					}
					break;
				}
			case FunctionCode.GETTIME:
				{
					long date = DateTime.Now.Year;
					date = date * 100 + DateTime.Now.Month;
					date = date * 100 + DateTime.Now.Day;
					date = date * 100 + DateTime.Now.Hour;
					date = date * 100 + DateTime.Now.Minute;
					date = date * 100 + DateTime.Now.Second;
					date = date * 1000 + DateTime.Now.Millisecond;
					vEvaluator.RESULT = date;
					vEvaluator.RESULTS = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
				}
				break;
			case FunctionCode.SETCOLOR:
				{
					SpColorArgument colorArg = (SpColorArgument)func.Argument;
					long colorR;
					long colorG;
					long colorB;
					if (colorArg.RGB != null)
					{
						long colorRGB = colorArg.RGB.GetIntValue(exm);
						colorR = (colorRGB & 0xFF0000) >> 16;
						colorG = (colorRGB & 0x00FF00) >> 8;
						colorB = colorRGB & 0x0000FF;
					}
					else
					{
						colorR = colorArg.R.GetIntValue(exm);
						colorG = colorArg.G.GetIntValue(exm);
						colorB = colorArg.B.GetIntValue(exm);
						if ((colorR < 0) || (colorG < 0) || (colorB < 0))
							throw new CodeEE(trerror.SetcolorArgLessThan0.Text);
						if ((colorR > 255) || (colorG > 255) || (colorB > 255))
							throw new CodeEE(trerror.SetcolorArgOver255.Text);
					}
					EmuColor c = EmuColor.FromArgb((int)colorR, (int)colorG, (int)colorB);
					exm.Console.SetStringStyle(c);
				}
				break;
			case FunctionCode.SETCOLORBYNAME:
				{
					string colorName = func.Argument.ConstStr;
					EmuColor c = EmuColor.FromName(colorName);
					exm.Console.SetStringStyle(c);
				}
				break;
			case FunctionCode.SETBGCOLOR:
				{
					SpColorArgument colorArg = (SpColorArgument)func.Argument;
					long colorR;
					long colorG;
					long colorB;
					if (colorArg.IsConst)
					{
						long colorRGB = colorArg.ConstInt;
						colorR = (colorRGB & 0xFF0000) >> 16;
						colorG = (colorRGB & 0x00FF00) >> 8;
						colorB = colorRGB & 0x0000FF;
					}
					else if (colorArg.RGB != null)
					{
						long colorRGB = colorArg.RGB.GetIntValue(exm);
						colorR = (colorRGB & 0xFF0000) >> 16;
						colorG = (colorRGB & 0x00FF00) >> 8;
						colorB = colorRGB & 0x0000FF;
					}
					else
					{
						colorR = colorArg.R.GetIntValue(exm);
						colorG = colorArg.G.GetIntValue(exm);
						colorB = colorArg.B.GetIntValue(exm);
						if ((colorR < 0) || (colorG < 0) || (colorB < 0))
							throw new CodeEE(trerror.SetcolorArgLessThan0.Text);
						if ((colorR > 255) || (colorG > 255) || (colorB > 255))
							throw new CodeEE(trerror.SetcolorArgOver255.Text);
					}
					EmuColor c = EmuColor.FromArgb((int)colorR, (int)colorG, (int)colorB);
					exm.Console.SetBgColor(c);
				}
				break;
			case FunctionCode.SETBGCOLORBYNAME:
				{
					string colorName = func.Argument.ConstStr;
					EmuColor c = EmuColor.FromName(colorName);
					exm.Console.SetBgColor(c);
				}
				break;
			case FunctionCode.FONTSTYLE:
				{
					if (func.Argument.IsConst)
						iValue = func.Argument.ConstInt;
					else
						iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
					exm.Console.SetStringStyle(ParseFontStyle(iValue));
				}
				break;
			case FunctionCode.SETFONT:
				if (func.Argument.IsConst)
					str = func.Argument.ConstStr;
				else
					str = ((ExpressionArgument)func.Argument).Term.GetStrValue(exm);
				exm.Console.SetFont(str);
				break;
			case FunctionCode.ALIGNMENT:
				str = func.Argument.ConstStr;
				if (str.Equals("LEFT", Config.StringComparison))
					exm.Console.Alignment = DisplayLineAlignment.LEFT;
				else if (str.Equals("CENTER", Config.StringComparison))
					exm.Console.Alignment = DisplayLineAlignment.CENTER;
				else if (str.Equals("RIGHT", Config.StringComparison))
					exm.Console.Alignment = DisplayLineAlignment.RIGHT;
				else
					throw new CodeEE(string.Format(trerror.InvalidAlignment.Text, str));
				break;

			case FunctionCode.REDRAW:
				if (func.Argument.IsConst)
					iValue = func.Argument.ConstInt;
				else
					iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
				exm.Console.SetRedraw(iValue);
				break;

			case FunctionCode.RESET_STAIN:
				{
					if (func.Argument.IsConst)
						iValue = func.Argument.ConstInt;
					else
						iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
					vEvaluator.SetDefaultStain(iValue);
				}
				break;
			case FunctionCode.SPLIT:
				{
					SpSplitArgument spSplitArg = (SpSplitArgument)func.Argument;
					string target = spSplitArg.TargetStr.GetStrValue(exm);
					string[] split = [spSplitArg.Split.GetStrValue(exm)];
					string[] retStr = target.Split(split, StringSplitOptions.None);
					spSplitArg.Num.SetValue(retStr.Length, exm);
					if (retStr.Length > spSplitArg.Var.GetLength(0))
					{
						string[] temp = retStr;
						retStr = new string[spSplitArg.Var.GetLength(0)];
						Array.Copy(temp, retStr, retStr.Length);
					}
					spSplitArg.Var.SetValue(retStr, [0, 0, 0]);
				}
				break;
			case FunctionCode.PRINTCPERLINE:
				{
					SpGetIntArgument spGetintArg = (SpGetIntArgument)func.Argument;
					spGetintArg.VarToken.SetValue(Config.PrintCPerLine, exm);
				}
				break;
			case FunctionCode.SAVENOS:
				{
					SpGetIntArgument spGetintArg = (SpGetIntArgument)func.Argument;
					spGetintArg.VarToken.SetValue(Config.SaveDataNos, exm);
				}
				break;
			case FunctionCode.FORCEKANA:
				if (func.Argument.IsConst)
					iValue = func.Argument.ConstInt;
				else
					iValue = ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
				exm.ForceKana(iValue);
				break;
			case FunctionCode.SKIPDISP:
				{
					iValue = func.Argument.IsConst ? func.Argument.ConstInt : ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
					executionState.skipPrint = iValue != 0;
					userDefinedSkip = iValue != 0;
					vEvaluator.RESULT = executionState.skipPrint ? 1L : 0L;
				}
				break;
			case FunctionCode.NOSKIP:
				{
					if (func.JumpTo == null)
						throw new CodeEE(trerror.MissingEndnoskip.Text);
					saveSkip = executionState.skipPrint;
					if (executionState.skipPrint)
						executionState.skipPrint = false;
				}
				break;
			case FunctionCode.ENDNOSKIP:
				{
					if (func.JumpTo == null)
						throw new CodeEE(string.Format(trerror.MissingNoskip.Text, "ENDNOSKIP"));
					if (saveSkip)
						executionState.skipPrint = true;
				}
				break;
			case FunctionCode.ARRAYSHIFT:
				{
					SpArrayShiftArgument arrayArg = (SpArrayShiftArgument)func.Argument;
					if (!arrayArg.VarToken.Identifier.IsArray1D)
						throw new CodeEE(string.Format(trerror.IsUsableOnly1DVar.Text, "ARRAYSHIFT"));
					FixedVariableTerm dest = arrayArg.VarToken.GetFixedVariableTerm(exm);
					int shift = (int)arrayArg.Num1.GetIntValue(exm);
					if (shift == 0)
						break;
					int start = (int)arrayArg.Num3.GetIntValue(exm);
					if (start < 0)
						throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSHIFT", "4", start.ToString()));
					int num;
					if (arrayArg.Num4 != null)
					{
						num = (int)arrayArg.Num4.GetIntValue(exm);
						if (num < 0)
							throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSHIFT", "5", num.ToString()));
						if (num == 0)
							break;
					}
					else
						num = -1;
					if (dest.Identifier.IsInteger)
					{
						long def = arrayArg.Num2.GetIntValue(exm);
						VariableEvaluator.ShiftArray(dest, shift, def, start, num);
					}
					else
					{
						string defs = arrayArg.Num2.GetStrValue(exm);
						VariableEvaluator.ShiftArray(dest, shift, defs, start, num);
					}
					break;
				}
			case FunctionCode.ARRAYREMOVE:
				{
					SpArrayControlArgument arrayArg = (SpArrayControlArgument)func.Argument;
					if (!arrayArg.VarToken.Identifier.IsArray1D)
						throw new CodeEE(string.Format(trerror.IsUsableOnly1DVar.Text, "ARRAYREMOVE"));
					FixedVariableTerm p = arrayArg.VarToken.GetFixedVariableTerm(exm);
					int start = (int)arrayArg.Num1.GetIntValue(exm);
					int num = (int)arrayArg.Num2.GetIntValue(exm);
					if (start < 0)
						throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYREMOVE", "2", start.ToString()));
					VariableEvaluator.RemoveArray(p, start, num);
					break;
				}
			case FunctionCode.ARRAYSORT:
				{
					SpArraySortArgument arrayArg = (SpArraySortArgument)func.Argument;
					if (!arrayArg.VarToken.Identifier.IsArray1D)
						throw new CodeEE(string.Format(trerror.IsUsableOnly1DVar.Text, "ARRAYSORT"));
					FixedVariableTerm p = arrayArg.VarToken.GetFixedVariableTerm(exm);
					int start = (int)arrayArg.Num1.GetIntValue(exm);
					if (start < 0)
						throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSORT", "3", start.ToString()));
					int num = 0;
					if (arrayArg.Num2 != null)
					{
						num = (int)arrayArg.Num2.GetIntValue(exm);
						if (num < 0)
							throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, "ARRAYSORT", "4", start.ToString()));
						if (num == 0)
							break;
					}
					else
						num = -1;
					VariableEvaluator.SortArray(p, arrayArg.Order, start, num);
					break;
				}
			case FunctionCode.ARRAYCOPY:
				{
					SpCopyArrayArgument arrayArg = (SpCopyArrayArgument)func.Argument;
					AExpression varName1 = arrayArg.VarName1;
					AExpression varName2 = arrayArg.VarName2;
					VariableToken[] vars = [null!, null!];
					if (!(varName1 is SingleTerm) || !(varName2 is SingleTerm))
					{
						string[] names = [null!, null!];
						names[0] = varName1.GetStrValue(exm);
						names[1] = varName2.GetStrValue(exm);
						if ((vars[0] = idDic.GetVariableToken(names[0], null!, true)) == null)
							throw new CodeEE(string.Format(trerror.NotVariableName.Text, "ARRAYCOPY", "1", names[0]));
						if (!vars[0].IsArray1D && !vars[0].IsArray2D && !vars[0].IsArray3D)
							throw new CodeEE(string.Format(trerror.ArraycopyArgIsNotArray.Text, "1", names[0]));
						if (vars[0].IsCharacterData)
							throw new CodeEE(string.Format(trerror.ArraycopyArgIsCharaVar.Text, "1", names[0]));
						if ((vars[1] = idDic.GetVariableToken(names[1], null!, true)) == null)
							throw new CodeEE(string.Format(trerror.NotVariableName.Text, "ARRAYCOPY", "2", names[1]));
						if (!vars[1].IsArray1D && !vars[1].IsArray2D && !vars[1].IsArray3D)
							throw new CodeEE(string.Format(trerror.ArraycopyArgIsNotArray.Text, "2", names[1]));
						if (vars[1].IsCharacterData)
							throw new CodeEE(string.Format(trerror.ArraycopyArgIsCharaVar.Text, "2", names[1]));
						if (vars[1].IsConst)
							throw new CodeEE(string.Format(trerror.ArraycopyArgIsConst.Text, "2", names[1]));
						if ((vars[0].IsArray1D && !vars[1].IsArray1D) || (vars[0].IsArray2D && !vars[1].IsArray2D) || (vars[0].IsArray3D && !vars[1].IsArray3D))
							throw new CodeEE(trerror.DifferentArraycopyArgsDim.Text);
						if ((vars[0].IsInteger && vars[1].IsString) || (vars[0].IsString && vars[1].IsInteger))
							throw new CodeEE(trerror.DifferentArraycopyArgsType.Text);
					}
					else
					{
						vars[0] = idDic.GetVariableToken(((SingleStrTerm)varName1).Str, null!, true);
						vars[1] = idDic.GetVariableToken(((SingleStrTerm)varName2).Str, null!, true);
						if ((vars[0].IsInteger && vars[1].IsString) || (vars[0].IsString && vars[1].IsInteger))
							throw new CodeEE(trerror.DifferentArraycopyArgsType.Text);
					}
					VariableEvaluator.CopyArray(vars[0], vars[1]);
				}
				break;
			case FunctionCode.ENCODETOUNI:
				{
					term = ((ExpressionArgument)func.Argument).Term;
					string target = term.GetStrValue(exm);

					int length = vEvaluator.RESULT_ARRAY.Length;
					if (target.Length > length - 1)
						throw new CodeEE(string.Format(trerror.tooLongEncodetouniArg.Text, target.Length, length - 1));

					int[] ary = new int[target.Length];
					for (int i = 0; i < target.Length; i++)
						ary[i] = char.ConvertToUtf32(target, i);
					vEvaluator.SetEncodingResult(ary);
				}
				break;
			case FunctionCode.ASSERT:
				if (((ExpressionArgument)func.Argument).Term.GetIntValue(exm) == 0)
					throw new CodeEE(trerror.AssertArgIs0.Text);
				break;
			case FunctionCode.THROW:
				throw new CodeEE(((ExpressionArgument)func.Argument).Term.GetStrValue(exm));
			case FunctionCode.CLEARTEXTBOX:
				console.ClearText();
				break;
			case FunctionCode.STRDATA:
				{
					if (func.dataList.Count == 0)
					{
						state.JumpTo(func.JumpTo);
						return;
					}
					int count = func.dataList.Count;
					int choice = (int)vEvaluator.GetNextRand(count);
					List<InstructionLine> iList = func.dataList[choice];
					int i = 0;
					foreach (InstructionLine selectedLine in iList)
					{
						state.CurrentLine = selectedLine;
						if (selectedLine.Argument == null)
							ArgumentParser.SetArgumentTo(selectedLine);
						term = ((ExpressionArgument)selectedLine.Argument!).Term;
						str += term.GetStrValue(exm);
						if (++i < iList.Count)
							str += "\n";
					}
					((StrDataArgument)func.Argument).Var.SetValue(str!, exm);
					state.JumpTo(func.JumpTo);
					break;
				}
			case FunctionCode.SKIPLOG:
				{
					iValue = func.Argument.IsConst ? func.Argument.ConstInt : ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
					console.MesSkip = iValue != 0;
					break;
				}
#if DEBUG
			default:
				throw new ExeEE(trerror.UndefinedFunc.Text);
#endif
		}
		return;
	}


	bool doFlowControlFunction(InstructionLine func)
	{
		switch (func.FunctionCode)
		{
			case FunctionCode.LOADDATA:
				{
					ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
					long target = intExpArg.Term.GetIntValue(exm);
					if (target < 0)
						throw new CodeEE(string.Format(trerror.LoaddataArgIsNegative.Text, target.ToString()));
					else if (target > int.MaxValue)
						throw new CodeEE(string.Format(trerror.TooLargeLoaddataArg.Text, target.ToString()));
					EraDataResult result = vEvaluator.CheckData((int)target, EraSaveFileType.Normal);
					if (result.State != EraDataState.OK)
						throw new CodeEE(trerror.LoadCorruptedData.Text);

					if (!vEvaluator.LoadFrom((int)target))
						throw new ExeEE(trerror.UnexpectedErrorInLoaddata.Text);
					state.ClearFunctionList();
					state.SystemState = SystemStateCode.LoadData_DataLoaded;
					return false;
				}

			case FunctionCode.TRYCALLLIST:
			case FunctionCode.TRYJUMPLIST:
				{
					string funcName = "";
					CalledFunction callto = null!;
					SpCallArgment cfa = null!;
					foreach (InstructionLine iLine in func.callList)
					{

						cfa = (SpCallArgment)iLine.Argument;
						funcName = cfa.FuncnameTerm.GetStrValue(exm);
						callto = CalledFunction.CallFunction(labelDic, funcName, func.JumpTo);
						if (callto == null)
							continue;
						callto.IsJump = func.Function.IsJump();
						UserDefinedFunctionArgument args = callto.ConvertArg(cfa.RowArgs, out string errMes);
						if (args == null)
							throw new CodeEE(errMes);
						state.IntoFunction(callto, args, exm);
						return true;
					}
					state.JumpTo(func.JumpTo);
				}
				break;
			case FunctionCode.TRYGOTOLIST:
				{
					string funcName = "";
					LogicalLine jumpto = null!;
					foreach (InstructionLine iLine in func.callList)
					{
						if (iLine.Argument == null)
							ArgumentParser.SetArgumentTo(iLine);
						funcName = ((SpCallArgment)iLine.Argument!).FuncnameTerm.GetStrValue(exm);
						jumpto = state.CurrentCalled.CallLabel(labelDic, funcName);
						if (jumpto != null)
							break;
					}
					if (jumpto == null)
						state.JumpTo(func.JumpTo);
					else
						state.JumpTo(jumpto);
				}
				break;
			case FunctionCode.CALLTRAIN:
				{
					ExpressionArgument intExpArg = (ExpressionArgument)func.Argument;
					long count = intExpArg.Term.GetIntValue(exm);
					SetCommnds(count);
					return false;
				}
			case FunctionCode.STOPCALLTRAIN:
				{
					if (executionState.isCTrain)
					{
						executionState.coms.Clear();
						executionState.count = 0;
						executionState.isCTrain = false;
						executionState.skipPrint = false;
					}
					return false;
				}
			case FunctionCode.DOTRAIN:
				{
					switch (state.SystemState)
					{
						case SystemStateCode.Train_CallEventTrain:
						case SystemStateCode.Train_CallShowStatus:
						case SystemStateCode.Train_CallEventComEnd:
							break;
						default:
							exm.Console.PrintSystemLine(state.SystemState.ToString());
							throw new CodeEE(trerror.CanNotUseDotrainHere.Text);
					}
					executionState.coms.Clear();
					executionState.isCTrain = false;
					executionState.count = 0;

					long train = ((ExpressionArgument)func.Argument).Term.GetIntValue(exm);
					if (train < 0)
						throw new CodeEE(trerror.DotrainArgLessThan0.Text);
					if (train >= trainName.Length)
						throw new CodeEE(trerror.DotrainArgOverTrainnameArray.Text);
					executionState.doTrainSelectCom = train;
					state.SystemState = SystemStateCode.Train_DoTrain;
					return false;
				}
#if DEBUG
		default:
			throw new ExeEE(trerror.UndefinedFunc.Text);
#endif
		}
		return true;
	}
}
