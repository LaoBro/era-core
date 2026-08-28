using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Runtime.Script.Statements;

internal abstract class AInstruction
{
	protected int flag;
	public int Flag { get { return flag; } }

	public ArgumentBuilder ArgBuilder { get; protected set; } = null!;
	/// <summary>
	/// 设置本指令的跳转目标。返回值表示是否用到了 callform 系（动态调用）——
	/// erb-loader-parallel 08 D5#3：原先以 `ref bool useCallForm` 出参直写 ErbLoader 实例字段，
	/// 并行未调用清扫期间构成共享写；改为返回值后由调用方决定落点（主循环直写 / sweep 写 per-label 产物）。
	/// </summary>
	public virtual bool SetJumpTo(InstructionLine func, int currentDepth, ref string FunctionoNotFoundName) => false;
	public virtual void DoInstruction(ExpressionMediator exm, InstructionLine func, ProcessState state)
	{ throw new ExeEE("未実装 or 呼び出しミス"); }

	public virtual Argument CreateArgument(InstructionLine line, ExpressionMediator exm)
	{
		throw new ExeEE("実装されていない");
	}
}
