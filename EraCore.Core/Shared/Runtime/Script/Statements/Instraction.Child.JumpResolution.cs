using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameProc.Function;

/// <summary>
/// <b>加载期跳转解析</b>（ADR-0027 第三刀）：<see cref="AInstruction.SetJumpTo"/> 的全部 override
/// 分居本文件，运行期实现（ctor / <see cref="AInstruction.DoInstruction"/> 等）留在
/// <c>Instraction.Child.cs</c>——改加载语义时不必再穿过 4132 行的指令实现海洋找接缝。
/// <para>
/// <b>可核验契约</b>：本文件恰好承载 <b>9</b> 个 override（宿主类依次为
/// <c>CALLF</c> / <c>CALLSHARP</c> / <c>TRYCALLF</c> / <c>SIF</c> / <c>RETURNF</c> /
/// <c>CALL</c> / <c>CALLS</c> / <c>CALLEVENT</c> / <c>GOTO</c>），
/// 且 <c>Instraction.Child.cs</c> 内 <c>override bool SetJumpTo</c> 为 <b>0</b> 处。
/// 新增/移动 <see cref="AInstruction.SetJumpTo"/> override 时，两处必须同步——否则本契约失真。
/// </para>
/// <para>
/// <b>为什么用 partial 而非整类搬迁</b>：9 个宿主类各自都有运行期成员，<c>SetJumpTo</c> 是每类唯一的
/// 加载期成员；整类搬走会把运行期实现搬进"加载期"文件，与分居目的相反。partial 同类型共享
/// 私有成员（<c>isJump</c> / <c>isTry</c> readonly 字段、<c>SpCallFArgment</c> 等参数类型的缓存写回），
/// 故本次迁移是<b>纯移动、零行为改动</b>。
/// </para>
/// </summary>
internal sealed partial class FunctionIdentifier
{
	private sealed partial class CALLF_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			if (!func.Argument.IsConst)
			{
				return true;
			}
			SpCallFArgment callfArg = (SpCallFArgment)func.Argument;
			try
			{
				callfArg.FuncTerm = GlobalStatic.IdentifierDictionary.GetFunctionMethod(GlobalStatic.LabelDictionary, callfArg.ConstStr, callfArg.RowArgs, true);
			}
			catch (CodeEE e)
			{
				ParserMediator.Warn(e.Message, func, 2, true, false);
				return false;
			}
			if (callfArg.FuncTerm == null)
			{
				if (!Program.AnalysisMode)
					ParserMediator.Warn(string.Format(trerror.NotDefinedFunc.Text, callfArg.ConstStr), func, 2, true, false);
				else
					ParserMediator.Warn(callfArg.ConstStr, func, 2, true, false);
				return false;
			}
			return false;
		}
	}

	private sealed partial class CALLSHARP_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			if (!func.Argument.IsConst)
			{
				return true;
			}

			SpCallSharpArgment arg = (SpCallSharpArgment)func.Argument;
			var manager = PluginManager.GetInstance();
			if (!manager.HasMethod(arg.ConstStr))
			{
				ParserMediator.Warn(string.Format(trerror.MethodNotFound.Text, arg.ConstStr), func, 2, true, false);
				return false;
			}

			arg.CallFunc = manager.GetMethod(func.Argument.ConstStr);
			return false;
		}
	}

	private sealed partial class TRYCALLF_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			if (!func.Argument.IsConst)
			{
				return true;
			}
			SpCallFArgment callfArg = (SpCallFArgment)func.Argument;
			//if (Config.Config.IgnoreCase)
			//	callfArg.ConstStr = callfArg.ConstStr.ToUpper();
			try
			{
				callfArg.FuncTerm = GlobalStatic.IdentifierDictionary.GetFunctionMethod(GlobalStatic.LabelDictionary, callfArg.ConstStr, callfArg.RowArgs, true);
			}
			catch
			{
				return false;
			}
			if (callfArg.FuncTerm == null)
			{
				return false;
			}
			return false;
		}
	}

	private sealed partial class SIF_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			LogicalLine jumpto = func.NextLine;
			if ((jumpto == null) || (jumpto.NextLine == null) ||
				(jumpto is FunctionLabelLine) || (jumpto is NullLine))
			{
				ParserMediator.Warn(trerror.NothingAfterSif.Text, func, 2, true, false);
				return false;
			}
			else if (jumpto is InstructionLine)
			{
				InstructionLine sifFunc = (InstructionLine)jumpto;
				if (sifFunc.Function.IsPartial())
					ParserMediator.Warn(string.Format(trerror.FuncCanNotAfterSif.Text, sifFunc.Function.Name), func, 2, true, false);
				else
					func.JumpTo = func.NextLine.NextLine;
			}
			else if (jumpto is GotoLabelLine)
				ParserMediator.Warn(trerror.LabelCanNotAfterSif.Text, func, 2, true, false);
			else
				func.JumpTo = func.NextLine.NextLine;

			if ((func.JumpTo != null) && (func.Position!.Value.LineNo + 1 != func.NextLine.Position!.Value.LineNo))
				ParserMediator.Warn(trerror.EmptyAfterSif.Text, func, 0, false, true);
			return false;
		}
	}

	private sealed partial class RETURNF_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			FunctionLabelLine label = func.ParentLabelLine;
			if (!label.IsMethod)
			{
				ParserMediator.Warn(trerror.CanNotUseReturnf.Text, func, 2, true, false);
			}
			if (func.Argument != null)
			{
				AExpression term = ((ExpressionArgument)func.Argument).Term;
				if (term != null)
				{
					if (label.MethodType != term.GetOperandType())
					{
						if (label.MethodType == typeof(long))
							ParserMediator.Warn(trerror.ReturnfStrInIntFunc.Text, func, 2, true, false);
						else if (label.MethodType == typeof(string))
							ParserMediator.Warn(trerror.ReturnfIntInStrFunc.Text, func, 2, true, false);
					}
				}
			}
			return false;
		}
	}

	private sealed partial class CALL_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			if (!func.Argument.IsConst)
			{
				return true;
			}
			SpCallArgment callArg = (SpCallArgment)func.Argument;
			string labelName = callArg.ConstStr;
			CalledFunction call = CalledFunction.CallFunction(GlobalStatic.LabelDictionary, labelName, func);
			if ((call == null) && (!func.Function.IsTry()))
			{
				FunctionoNotFoundName = labelName;
				return false;
			}
			if (call != null)
			{
				func.JumpTo = call.TopLabel;
				if (call.TopLabel.Depth < 0)
					call.TopLabel.Depth = currentDepth + 1;
				if (call.TopLabel.IsError)
				{
					func.IsError = true;
					func.ErrMes = call.TopLabel.ErrMes;
					return false;
				}
				string errMes;
				callArg.UDFArgument = call.ConvertArg(callArg.RowArgs, out errMes);
				if (callArg.UDFArgument == null)
				{
					ParserMediator.Warn(errMes, func, 2, true, false);
					return false;
				}
			}
			callArg.CallFunc = call!;
			return false;
		}
	}

	private sealed partial class CALLS_Instruction
	{
		// 动态目标只能在运行时确定 → 标记「用到 callform 系」，使加载期跳过未调用函数告警。
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName) => true;
	}

	private sealed partial class CALLEVENT_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			//EVENT関数からCALLされた先でCALLEVENTされるようなパターンはIntoFunctionで捕まえる
			FunctionLabelLine label = func.ParentLabelLine;
			if (label.IsEvent)
			{
				ParserMediator.Warn(trerror.CanNotUseCallevent.Text, func, 2, true, false);
			}
			return false;
		}
	}

	private sealed partial class GOTO_Instruction
	{
		public override bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName)
		{
			GotoLabelLine jumpto;
			func.JumpTo = null!;
			if (func.Argument.IsConst)
			{
				string labelName = func.Argument.ConstStr;
				jumpto = GlobalStatic.LabelDictionary.GetLabelDollar(labelName, func.ParentLabelLine);
				if (jumpto == null)
				{
					if (!func.Function.IsTry())
						ParserMediator.Warn(string.Format(trerror.NotDefinedLabelName.Text, labelName), func, 2, true, false);
					else
						return false;
				}
				else if (jumpto.IsError)
					ParserMediator.Warn(string.Format(trerror.InvalidLabelName.Text, labelName), func, 2, true, false);
				else if (jumpto != null)
				{
					func.JumpTo = jumpto;
				}
			}
			return false;
		}
	}
}
