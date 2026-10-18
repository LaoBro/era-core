using MinorShift.Emuera.GameData.Variable;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;
using System.Collections.Generic;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Runtime.Script;

/// <summary>
/// 候选 4 · 第 2 刀：执行游标 + 函数调用栈（自 <c>ProcessState</c> 劈出）。
///
/// 职责边界：程序计数器（PC）与调用栈<b>不可分割</b>——进栈即把 PC 设为函数头，
/// 出栈即把 PC 设为返回地址（<c>Return</c> 里二者交织在同一条语句序列中）。任何
/// 「PC 留在外面」的设计都会迫使本类返回复合结果，比劈分前更浅。故本类一并持有
/// PC 与执行步数计数器，对外只暴露窄面。
///
/// 生命周期依据：<c>ProcessState.Clone()</c> 历来只复制 currentLine / sysStateCode /
/// begintype，<b>不复制</b> functionList 与 MethodReturnValue —— 会话状态与调用栈本就是
/// 两种生命周期，这条既有边界就是劈分线。
///
/// 依赖：只经注入的 <see cref="ProcessTrace"/> 写调试 trace（第 1 刀成果），
/// 不依赖 EmueraConsole / Program / GlobalStatic。
/// </summary>
internal sealed class CallStack
{
	readonly ProcessTrace trace;
	readonly List<CalledFunction> functionList = [];

	public CallStack(ProcessTrace trace)
	{
		this.trace = trace;
	}

	#region 游标
	public LogicalLine CurrentLine { get; set; } = null!;

	/// <summary>执行步数计数器。ShiftNextLine / JumpTo / IntoFunction / Return 自增。</summary>
	public int LineCount { get; set; }

	public void ShiftNextLine()
	{
		CurrentLine = CurrentLine.NextLine;
		LineCount++;
	}

	/// <summary>
	/// 関数内の移動。JUMPではなくGOTOやIF文など
	/// </summary>
	public void JumpTo(LogicalLine line)
	{
		CurrentLine = line;
		LineCount++;
	}
	#endregion

	#region 栈查询
	/// <summary>式中函数用：栈底指针。抬到 <see cref="FunctionCount"/> 即在该子栈上判定。</summary>
	public int CurrentMin { get; set; }

	public bool ScriptEnd => functionList.Count == CurrentMin;

	public int FunctionCount => functionList.Count;

	public CalledFunction CurrentCalled => functionList[^1];

	public bool IsFunctionMethod => functionList[CurrentMin].TopLabel.IsMethod;

	/// <summary>式中函数返回值。与 functionList 同生命周期（Clone 历来不复制）。</summary>
	public SingleTerm MethodReturnValue { get; set; } = null!;

	public string Scope
	{
		get
		{
			//1756 デバッグコマンドから呼び出されるようになったので空栈返回 null
			if (functionList.Count == 0)
				return null!;
			return functionList[^1].FunctionName;
		}
	}

	/// <summary>栈底函数名（BEGIN 非法调用的错误文案）。保持既有行为：空栈时按既有索引访问。</summary>
	internal string BottomFunctionName => functionList[0].FunctionName;

	public LogicalLine GetCurrentReturnAddress
	{
		get
		{
			if (functionList.Count == CurrentMin)
				return null!;
			return functionList[^1].ReturnAddress;
		}
	}

	public LogicalLine GetReturnAddressSequensial(int currentDepth)
	{
		if (functionList.Count == CurrentMin)
			return null!;
		return functionList[functionList.Count - currentDepth - 1].ReturnAddress;
	}
	#endregion

	#region 栈操作
	/// <summary>作用域退栈 + 清空。供 <c>ProcessState.ClearFunctionList</c> 与 <c>Begin</c> 共用。</summary>
	public void Clear()
	{
		foreach (CalledFunction called in functionList)
			if (called.CurrentLabel.hasPrivDynamicVar)
				called.CurrentLabel.ScopeOut();
		functionList.Clear();
	}

	public void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm)
	{
		if (call.IsEvent)
		{
			foreach (CalledFunction called in functionList)
			{
				if (called.IsEvent)
					throw new CodeEE(trerror.CalleventBeforeFinishEvent.Text);
			}
		}
		if (trace.Enabled)
		{
			FunctionLabelLine label = call.CurrentLabel;
			if (exm != null)
			{
				long line = exm.Process.getCurrentLine.Position!.Value.LineNo;
				if (call.IsJump)
					trace.Add(string.Format(trsl.DebugTraceJump2.Text, label.LabelName, label.Position!.Value.Filename, label.Position!.Value.LineNo, line));
				else
					trace.Add(string.Format(trsl.DebugTraceCall2.Text, label.LabelName, label.Position!.Value.Filename, label.Position!.Value.LineNo, line));
			}
			else
			{
				if (call.IsJump)
					trace.Add(string.Format(trsl.DebugTraceJump.Text, label.LabelName, label.Position!.Value.Filename, label.Position!.Value.LineNo));
				else
				{
					string traceLine = $"CALL @{label.LabelName}:{label.Position!.Value.Filename}:{label.Position!.Value.LineNo}";
					if (call.ReturnAddress != null)
						traceLine += $" at @{call.ReturnAddress.ParentLabelLine.LabelName}:{call.ReturnAddress.ParentLabelLine.Position!.Value.Filename}:{call.ReturnAddress.Position!.Value.LineNo}";
					trace.Add(traceLine);
				}
			}
		}
		if (srcArgs != null)
		{
			//引数の値を確定させる
			srcArgs.SetTransporter(exm!);
			//プライベート変数更新
			if (call.TopLabel.hasPrivDynamicVar)
				call.TopLabel.ScopeIn();
			//更新した変数へ引数を代入
			for (int i = 0; i < call.TopLabel.Arg.Length; i++)
			{
				if (srcArgs.Arguments[i] != null)
				{
					if (call.TopLabel.Arg[i].Identifier.IsReference)
						((ReferenceToken)call.TopLabel.Arg[i].Identifier).SetRef(srcArgs.TransporterRef[i]);
					else if (srcArgs.Arguments[i].GetOperandType() == typeof(long))
						call.TopLabel.Arg[i].SetValue(srcArgs.TransporterInt[i], exm!);
					else
						call.TopLabel.Arg[i].SetValue(srcArgs.TransporterStr[i], exm!);
				}
			}
		}
		else//こっちに来るのはシステムからの呼び出し=引数は存在しない関数のみ
		{
			//プライベート変数更新
			if (call.TopLabel.hasPrivDynamicVar)
				call.TopLabel.ScopeIn();
		}
		functionList.Add(call);
		CurrentLine = call.CurrentLabel;
		LineCount++;
	}

	/// <summary>
	/// 通常関数の RETURN。
	/// </summary>
	/// <returns>
	/// true = 本次返回后调用栈<b>已清空且无处可回</b>（返回地址为空）。
	/// 调用方据此决定是否触发 BEGIN —— 这是 CallStack 与 BEGIN 状态机之间唯一的向上通知，
	/// 原实现是 ProcessState.Return 内部直接调 Begin()，劈分后改为返回值，避免本类反向依赖 BEGIN。
	/// </returns>
	public bool Return(long ret)
	{
		if (IsFunctionMethod)
		{
			ReturnF(null!);
			return false;
		}
		CalledFunction called = functionList[^1];
		if (called.IsJump)
		{//JUMPした場合。即座にRETURN RESULTする。
			if (called.TopLabel.hasPrivDynamicVar)
				called.TopLabel.ScopeOut();
			functionList.Remove(called);
			if (trace.Enabled)
				trace.Remove();
			return Return(ret);
		}
		if (!called.IsEvent)
		{
			if (called.TopLabel.hasPrivDynamicVar)
				called.TopLabel.ScopeOut();
			CurrentLine = null!;
		}
		else
		{
			if (called.CurrentLabel.hasPrivDynamicVar)
				called.CurrentLabel.ScopeOut();
			//#Singleフラグ付き関数で1が返された。
			//1752 非0ではなく1と等価であることを見るように修正
			//1756 全てを終了ではなく#PRIや#LATERのグループごとに修正
			if (called.IsOnly)
				called.FinishEvent();
			else if (called.HasSingleFlag && ret == 1)
				called.ShiftNextGroup();
			else
				called.ShiftNext();//次の同名関数に進む。
			CurrentLine = called.CurrentLabel;//関数の始点(@～～)へ移動。呼ぶべき関数が無ければnull
			if (called.CurrentLabel != null)
			{
				LineCount++;
				if (called.CurrentLabel.hasPrivDynamicVar)
					called.CurrentLabel.ScopeIn();
			}
		}
		if (trace.Enabled)
			trace.Remove();
		//関数終了
		if (CurrentLine == null)
		{
			CurrentLine = called.ReturnAddress;
			functionList.RemoveAt(functionList.Count - 1);
			if (CurrentLine == null)
			{
				//この時点でfunctionListは空のはず
				return true;
			}
			LineCount++;
			return false;
		}
		else if (trace.Enabled)
		{
			FunctionLabelLine label = called.CurrentLabel!;
			long line = CurrentLine.Position!.Value.LineNo;
			trace.Add(string.Format(trsl.DebugTraceCall.Text, label!.LabelName, label!.Position!.Value.Filename, label.Position!.Value.LineNo, line));
		}
		LineCount++;
		return false;
	}

	/// <summary>
	/// #FUNCTION（式中関数）からの RETURN。
	/// </summary>
	public void ReturnF(SingleTerm ret)
	{
		if (trace.Enabled)
		{
			trace.Remove();
		}
		//OutはGetValue側で行う
		CurrentLine = functionList[^1].ReturnAddress;
		functionList.RemoveAt(functionList.Count - 1);
		MethodReturnValue = ret;
		return;
	}
	#endregion
}
