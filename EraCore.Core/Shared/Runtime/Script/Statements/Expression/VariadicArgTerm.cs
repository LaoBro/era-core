using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Statements.Expression;

/// <summary>
/// 票 41（Skia 对齐）：<c>VARIADIC</c> 形参在调用期打包的多余实参。
/// 元素类型由目标形参类型（<c>ARG</c> / <c>ARGS</c> / <c>ARGF</c>）决定；
/// 该 Term 只存在于调用帧的实参列表里，不进入存档 / 回放快照。
/// </summary>
internal sealed class VariadicArgTerm : AExpression
{
	private readonly List<AExpression> args;

	public VariadicArgTerm(List<AExpression> args, Type elementType)
		: base(elementType)
	{
		this.args = args;
	}

	public int Count => args.Count;

	public AExpression this[int index] => index >= 0 && index < args.Count ? args[index] : null!;

	public override AExpression Restructure(ExpressionMediator exm)
	{
		for (int i = 0; i < args.Count; i++)
			args[i] = args[i].Restructure(exm);
		return this;
	}
}
