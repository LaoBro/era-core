using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Statements.Function;

internal sealed class FunctionMethodTerm : AExpression
{
	public FunctionMethodTerm(FunctionMethod meth, List<AExpression> args)
		: base(ResolveType(meth, args))
	{
		method = meth;
		arguments = args;
	}

	/// <summary>
	/// 票 27：CanReturnFloat 的函数按实参类型解析表达式操作数类型——
	/// 任一实参为 Float → Float，否则 Integer（对齐 Skia 的 FunctionMethodTerm）。
	/// </summary>
	private static Type ResolveType(FunctionMethod meth, List<AExpression> args)
	{
		if (!meth.CanReturnFloat)
			return meth.ReturnType;
		for (int i = 0; i < args.Count; i++)
		{
			if (args[i] != null && args[i].GetOperandType() == typeof(double))
				return typeof(double);
		}
		return typeof(long);
	}

	private FunctionMethod method;
	private List<AExpression> arguments;

	public override long GetIntValue(ExpressionMediator exm)
	{
		return method.GetIntValue(exm, arguments);
	}
	public override string GetStrValue(ExpressionMediator exm)
	{
		return method.GetStrValue(exm, arguments);
	}
	public override double GetFloatValue(ExpressionMediator exm)
	{
		return method.GetFloatValue(exm, arguments);
	}
	public override SingleTerm GetValue(ExpressionMediator exm)
	{
		return method.GetReturnValue(exm, arguments);
	}

	public override AExpression Restructure(ExpressionMediator exm)
	{
		if (method.HasUniqueRestructure)
		{
			if (method.UniqueRestructure(exm, [.. arguments]) && method.CanRestructure)
				return GetValue(exm);
			return this;
		}
		bool argIsConst = true;
		for (int i = 0; i < arguments.Count; i++)
		{
			if (arguments[i] == null)
				continue;
			arguments[i] = arguments[i].Restructure(exm);
			argIsConst &= arguments[i] is SingleTerm;
		}
		if (method.CanRestructure && argIsConst)
			return GetValue(exm);
		return this;

	}

}
