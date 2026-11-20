using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script;

internal sealed class UserDefinedFunctionArgument
{
	public UserDefinedFunctionArgument(AExpression[] srcArgs, VariableTerm[] destArgs)
	{
		Arguments = srcArgs;
		TransporterInt = new long[Arguments.Length];
		TransporterStr = new string[Arguments.Length];
		TransporterFloat = new double[Arguments.Length];
		TransporterRef = new Array[Arguments.Length];
		isRef = new bool[Arguments.Length];
		for (int i = 0; i < Arguments.Length; i++)
		{
			isRef[i] = destArgs[i].Identifier.IsReference;
		}
	}
	public readonly AExpression[] Arguments;
	public readonly long[] TransporterInt;
	public readonly string[] TransporterStr;
	public readonly double[] TransporterFloat;
	public readonly Array[] TransporterRef;
	public readonly bool[] isRef;
	public void SetTransporter(ExpressionMediator exm)
	{
		for (int i = 0; i < Arguments.Length; i++)
		{
			if (Arguments[i] == null)
				continue;
			if (isRef[i])
			{
				VariableTerm vTerm = (VariableTerm)Arguments[i];
				if (vTerm.Identifier.IsCharacterData)
				{
					long charaNo = vTerm.GetElementInt(0, exm);
					if (charaNo < 0 || charaNo >= GlobalStatic.VariableData.CharacterList.Count)
						throw new CodeEE(string.Format(trerror.OoRCharaVarArg.Text, vTerm.Identifier.Name, "1", charaNo.ToString()));
					TransporterRef[i] = (Array)vTerm.Identifier.GetArrayChara((int)charaNo);
				}
				else
					TransporterRef[i] = (Array)vTerm.Identifier.GetArray();

			}
			else if (Arguments[i] is VariadicArgTerm)
				continue; // 票 41：变参槽由 IntoFunction 逐个写入，不走单值转运。
			else if (Arguments[i].GetOperandType() == typeof(long))
				TransporterInt[i] = Arguments[i].GetIntValue(exm);
			else if (Arguments[i].GetOperandType() == typeof(double))
				TransporterFloat[i] = Arguments[i].GetFloatValue(exm);
			else
				TransporterStr[i] = Arguments[i].GetStrValue(exm);
		}
	}
	public UserDefinedFunctionArgument Restructure(ExpressionMediator exm)
	{
		for (int i = 0; i < Arguments.Length; i++)
		{
			if (Arguments[i] == null)
				continue;
			if (isRef[i])
				Arguments[i].Restructure(exm);
			else
				Arguments[i] = Arguments[i].Restructure(exm);
		}
		return this;
	}
}

/// <summary>
/// 現在呼び出し中の関数
/// イベント関数を除いて実行中に内部状態は変化しないので使いまわしても良い
/// </summary>
internal sealed class CalledFunction
{
	private CalledFunction(string label) { FunctionName = label; }
	public static CalledFunction CallEventFunction(LabelDictionary labelDic, string label, LogicalLine retAddress)
	{
		CalledFunction called = new(label)
		{
			//List<FunctionLabelLine> newLabelList = new List<FunctionLabelLine>();
			Finished = false,
			eventLabelList = labelDic.GetEventLabels(label)
		};
		if (called.eventLabelList == null)
		{
			FunctionLabelLine line = labelDic.GetNonEventLabel(label);
			// lazy-function-load：若同名非事件函数是惰性桩，先物化再判定（保证 Position/IsMethod 为已解析真实态）。
			if (line != null && line.IsStub)
				ErbLoader.MaterializeOnRuntime(line);
			if (labelDic.GetNonEventLabel(label) is { } n)
			{
				throw new CodeEE(string.Format(trerror.CalleventToNonEventFunc.Text, label, n.Position!.Value.Filename, n.Position!.Value.LineNo));
			}
			return null!;
		}
		called.counter = -1;
		called.group = 0;
		called.ShiftNext();
		called.TopLabel = called.CurrentLabel;
		called.returnAddress = retAddress;
		called.IsEvent = true;
		return called;
	}

	public static CalledFunction CallFunction(LabelDictionary labelDic, string label, LogicalLine retAddress)
	{
		CalledFunction called = new(label)
		{
			Finished = false
		};
		FunctionLabelLine labelline = labelDic.GetNonEventLabel(label);
		// lazy-function-load（B 线 B1）：命中惰性桩 → 物化接缝。幂等；eager（无桩）恒 no-op；
		// 加载期 setJumpTo 也走此静态，但 GlobalStatic.Process.RuntimeLoader 在加载中为 null → 自动跳过。
		if (labelline != null && labelline.IsStub)
			ErbLoader.MaterializeOnRuntime(labelline);
		if (labelline == null)
		{
			if (labelDic.GetEventLabels(label) != null)
			{
				throw new CodeEE(string.Format(trerror.CallToEventFunc.Text, label, Config.Config.GetConfigName(ConfigCode.CompatiCallEvent)));
			}
			return null!;
		}
		else if (labelline.IsMethod)
		{
			throw new CodeEE(string.Format(trerror.CallToUserFunc.Text, labelline.LabelName, labelline.Position!.Value.Filename, labelline.Position!.Value.LineNo.ToString()));
		}
		called.TopLabel = labelline;
		called.CurrentLabel = labelline;
		called.returnAddress = retAddress;
		called.IsEvent = false;
		return called;
	}

	public static CalledFunction CreateCalledFunctionMethod(FunctionLabelLine labelline, string label)
	{
		CalledFunction called = new(label)
		{
			TopLabel = labelline,
			CurrentLabel = labelline,
			returnAddress = null!,
			IsEvent = false
		};
		return called;
	}


	static FunctionMethod tostrMethod = null!;
	/// <summary>
	/// 1803beta005 予め引数の数を合わせて規定値を代入しておく
	/// 1806+v6.99 式中関数の引数に無効な#DIM変数を与えている場合に例外になるのを修正
	/// 1808beta009 REF型に対応
	/// </summary>
	public UserDefinedFunctionArgument ConvertArg(List<AExpression> srcArgs, out string errMes)
	{
		errMes = null!;
		if (TopLabel.IsError)
		{
			errMes = TopLabel.ErrMes;
			return null!;
		}
		FunctionLabelLine func = TopLabel;
		// b2-irdiskcache：arg 转换读 func.Arg——IR 桩签名延后，必须先做非递归「签名物化」（否则 func.Arg 为 null → NRE）。
		// 幂等：已解析（Arg != null）或非桩 → no-op；签名物化不动函数体、不跑 setJumpTo → 无递归。
		ErbLoader.EnsureCalleeSignature(func);
		// 票 41：变参函数的多余实参打包进最后一个形参，故「实参过多」检查对它不适用。
		int variadicIndex = func.VariadicArgIndex;
		int fixedArgCount = variadicIndex >= 0 ? variadicIndex : func.Arg.Length;
		AExpression[] convertedArg = new AExpression[func.Arg.Length];
		if (variadicIndex < 0 && convertedArg.Length < srcArgs.Count)
		{
			errMes = string.Format(trerror.TooManyFuncArgs.Text, func.LabelName);
			return null!;
		}
		AExpression term;
		VariableTerm destArg;
		//bool isString = false;
		for (int i = 0; i < fixedArgCount; i++)
		{
			term = i < srcArgs.Count ? srcArgs[i] : null!;
			destArg = func.Arg[i];
			//isString = destArg.IsString;
			if (destArg.Identifier.IsReference)//参照渡しの場合
			{
				if (term == null)
				{
					errMes = string.Format(trerror.CanNotOmitRefArg.Text, func.LabelName, (i + 1).ToString());
					return null!;
				}
				VariableTerm vTerm = (term as VariableTerm)!;
				if (vTerm == null || vTerm.Identifier.Dimension == 0)
				{
					errMes = string.Format(trerror.RequireArrayBecauseRefArg.Text, func.LabelName, (i + 1).ToString());
					return null!;
				}
				//TODO 1810alpha007 キャラ型を認めるかどうかはっきりしたい 今のところ認めない方向
				//型チェック
				if (!((ReferenceToken)destArg.Identifier).MatchType(vTerm.Identifier, false, out errMes))
				{
					errMes = string.Format(trerror.NumberOfArg.Text, func.LabelName, (i + 1).ToString(), errMes);
					return null!;
				}
			}
			else if (term == null)//引数が省略されたとき
			{
				term = func.Def[i];//デフォルト値を代入
								   //1808beta001 デフォルト値がない場合はエラーにする
								   //一応逃がす
				if (term == null && !Config.Config.CompatiFuncArgOptional)
				{
					errMes = string.Format(trerror.CanNotOmitArgWithMessage.Text, func.LabelName, (i + 1).ToString(), Config.Config.GetConfigName(ConfigCode.CompatiFuncArgOptional));
					return null!;
				}
			}
			else
			{
				term = AdaptArgTerm(term, destArg, func.LabelName, i + 1, ref errMes);
				if (term == null)
					return null!;
			}
			convertedArg[i] = term!;
		}
		if (variadicIndex >= 0)
		{
			destArg = func.Arg[variadicIndex];
			List<AExpression> variadicArgs = [];
			for (int i = variadicIndex; i < srcArgs.Count; i++)
			{
				AExpression adapted = AdaptArgTerm(srcArgs[i], destArg, func.LabelName, i + 1, ref errMes);
				if (adapted == null)
					return null!;
				variadicArgs.Add(adapted);
			}
			convertedArg[variadicIndex] = new VariadicArgTerm(variadicArgs, destArg.Identifier.VariableType);
		}
		return new UserDefinedFunctionArgument(convertedArg, func.Arg);
	}

	/// <summary>
	/// 票 41：实参 → 形参类型适配（固定形参与变参共用）。类型一致原样返回；
	/// Str → Int 直接报错；Int → Float 隐式拓宽；Float → Int 报错；Int → Str 按兼容开关走 TOSTR。
	/// 失败时写 <paramref name="errMes"/> 并返回 null。
	/// </summary>
	private static AExpression AdaptArgTerm(AExpression term, VariableTerm destArg, string funcName, int argNo, ref string errMes)
	{
		if (term.GetOperandType() == destArg.GetOperandType())
			return term;
		if (term.GetOperandType() == typeof(string))
		{
			errMes = string.Format(trerror.CanNotConvertStrToInt.Text, funcName, argNo.ToString());
			return null!;
		}
		else if (destArg.GetOperandType() == typeof(double) && term.GetOperandType() == typeof(long))
		{
			// 票 27d / Skia 对齐：Int → Float 隐式拓宽。
		}
		else if (destArg.GetOperandType() == typeof(long) && term.GetOperandType() == typeof(double))
		{
			errMes = string.Format(trerror.CanNotConvertFloatToInt.Text, funcName, argNo.ToString());
			return null!;
		}
		else
		{
			if (!Config.Config.CompatiFuncArgAutoConvert)
			{
				errMes = string.Format(trerror.CanNotConvertIntToStr.Text, funcName, argNo.ToString(), Config.Config.GetConfigName(ConfigCode.CompatiFuncArgAutoConvert));
				return null!;
			}
			if (tostrMethod == null)
				tostrMethod = FunctionMethodCreator.GetMethodList()["TOSTR"];
			term = new FunctionMethodTerm(tostrMethod, [term]);
		}
		return term;
	}

	public LogicalLine CallLabel(LabelDictionary labelDic, string label)
	{
		return labelDic.GetLabelDollar(label, CurrentLabel);
	}

	public void updateRetAddress(LogicalLine line)
	{
		returnAddress = line;
	}

	public CalledFunction Clone()
	{
		CalledFunction called = new(FunctionName)
		{
			eventLabelList = eventLabelList,
			CurrentLabel = CurrentLabel,
			TopLabel = TopLabel,
			group = group,
			IsEvent = IsEvent,

			counter = counter,
			returnAddress = returnAddress
		};
		return called;
	}

	List<FunctionLabelLine>[] eventLabelList = null!;
	public FunctionLabelLine CurrentLabel { get; private set; } = null!;
	public FunctionLabelLine TopLabel { get; private set; } = null!;
	int counter = -1;
	int group;
	LogicalLine returnAddress = null!;
	public readonly string FunctionName = "";
	public bool IsJump { get; set; }
	public bool Finished { get; private set; }
	public LogicalLine ReturnAddress
	{
		get { return returnAddress; }
	}
	public bool IsEvent { get; private set; }

	/// <summary>票 41：本次调用收到的变参实参个数（0 = 非变参函数或未传多余实参）。</summary>
	public int VariadicArgCount { get; set; }

	public bool HasSingleFlag
	{
		get
		{
			if (CurrentLabel == null)
				return false;
			return CurrentLabel.IsSingle;
		}
	}


	#region イベント関数専用
	public void ShiftNext()
	{
		while (true)
		{
			counter++;
			if (eventLabelList[group].Count > counter)
			{
				CurrentLabel = eventLabelList[group][counter];
				return;
			}
			group++;
			counter = -1;
			if (group >= 4)
			{
				CurrentLabel = null!;
				return;
			}
		}
	}

	public void ShiftNextGroup()
	{
		counter = -1;
		group++;
		if (group >= 4)
		{
			CurrentLabel = null!;
			return;
		}
		ShiftNext();
	}

	public void FinishEvent()
	{
		group = 4;
		counter = -1;
		CurrentLabel = null!;
		return;
	}

	public bool IsOnly
	{
		get { return CurrentLabel.IsOnly; }
	}
	#endregion
}
