using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script;

internal sealed class ExecutionState
{
	public bool skipPrint { get; set; }
	public bool isCTrain { get; set; }
	public List<long> coms { get; set; } = [];
	public int count { get; set; }
	public long doTrainSelectCom { get; set; } = -1;
	public long systemResult { get; set; }
	public long flowinputDef { get; set; }
	public bool flowinput { get; set; }
	public bool flowinputCanSkip { get; set; }
	public string flowinputDefString { get; set; } = "";
	public bool flowinputString { get; set; }
	public bool flowinputForceSkip { get; set; }
	/// <summary>
	/// 票 42（Skia 对齐）：输入宏（<c>()</c> 重复宏 / <c>\e</c> MesSkip / 分隔符拆分）是否解析。
	/// 会话级，不入存档；DISABLE_INPUT_MACRO / ENABLE_INPUT_MACRO 写入，
	/// <c>ConsoleInputHandler.PressEnterKey</c> 消费。
	/// </summary>
	public bool inputMacroEnabled { get; set; } = true;
	/// <summary>票 42（Skia 对齐）：SEQUENCEINPUT 预置的下一段待消费输入；多次调用只保留最后一次。</summary>
	public string? sequenceInputValue { get; set; }
	/// <summary>票 42（Skia 对齐）：是否有待消费的序列输入。</summary>
	public bool hasSequenceInput { get; set; }
	/// <summary>shared: read by SystemProc.beginTitle to check load errors</summary>
	public bool noError { get; set; }
	public IProcessState CurrentState { get; set; } = null!;

	// ADR-0011 Phase 2：prev-state 栈原为 ScriptProc 私有，SystemProc 经 Process 反向引用触发。
	// 收掉该反向引用后迁入 ExecutionState——它本就是两模块共享的可变执行上下文。
	/// <summary>已保存的上一状态栈（SAVEGAME 取消 / LOADGAME 回 100 等场景回滚用）。</summary>
	public List<IProcessState> prevStateList { get; set; } = [];

	internal void DeletePrevState()
	{
		if (prevStateList.Count == 0)
			return;
		prevStateList.RemoveAt(prevStateList.Count - 1);
	}

	internal void DeleteAllPrevState()
	{
		foreach (IProcessState state in prevStateList)
			state.ClearFunctionList();
		prevStateList.Clear();
	}

	internal void SaveCurrentState(bool single)
	{
		if (CurrentState != null)
		{
			prevStateList.Add(CurrentState);
			CurrentState = CurrentState.Clone();
		}
	}

	internal void LoadPrevState()
	{
		if (CurrentState != null)
		{
			CurrentState.ClearFunctionList();
			CurrentState = prevStateList[prevStateList.Count - 1];
			DeletePrevState();
		}
	}
}
