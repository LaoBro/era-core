using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;

namespace MinorShift.Emuera.Runtime.Script;

/// <summary>
/// ADR-0011 Phase 3：ProcessState 的接口 seam，供 SystemProc/ScriptProc 注入解耦。
///
/// 候选 4 · 第 3 刀：本接口推广到<b>指令层</b>（原 <c>DoInstruction</c> 第三参数取具体
/// <c>ProcessState</c>，118 个 override 直连实现类）。成员面由 15 增至 18 —— 增加的 3 个
/// （<see cref="Scope"/> / <see cref="SetBegin"/> / <see cref="ReturnF"/>）正是指令层实际调用、
/// 而 SystemProc/ScriptProc 从不调用的成员，即"接口此前未覆盖的部分"。
///
/// 对指令层而言可见面反而<b>收窄</b>：27 个 public 成员 → 本接口 18 个。
/// </summary>
internal interface IProcessState
{
	SystemStateCode SystemState { get; set; }
	LogicalLine CurrentLine { get; set; }
	bool ScriptEnd { get; }
	bool IsFunctionMethod { get; }
	bool isBegun { get; }
	bool calledWhenNormal { get; set; }
	CalledFunction CurrentCalled { get; }
	void ShiftNextLine();
	void JumpTo(LogicalLine line);
	void Return(long ret);
	void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm);
	void Begin();
	int lineCount { get; set; }
	void ClearFunctionList();
	IProcessState Clone();

	// 候选 4 · 第 3 刀新增：以下 3 个成员此前只存在于 ProcessState 的具体面上，
	// 指令层经具体类直连调用。提升至接口后，指令层不再需要知道 ProcessState。

	/// <summary>当前执行中的函数名（空栈时为 null）。指令层用于报错文案。</summary>
	string Scope { get; }

	/// <summary>BEGIN 指令的入口（keyword 已 Trim + ToUpper）。</summary>
	void SetBegin(string keyword, bool force);

	/// <summary>#FUNCTION（式中関数）からの RETURN。</summary>
	void ReturnF(SingleTerm ret);
}
