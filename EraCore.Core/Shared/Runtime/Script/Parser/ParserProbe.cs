using System;

namespace MinorShift.Emuera.Runtime.Script.Parser;

/// <summary>探针归属段（issue 07 P1b：分账必须知道「哪一段在花钱」，否则不知道序列化能省哪一块）。</summary>
internal enum ParserProbeScope
{
	/// <summary>不在任何计费段内（窗口内但不属于四段；正常应接近 0）。</summary>
	None = 0,
	/// <summary>段② 建树：物化循环里的 ParseLine / ParseLabelLine / ParseSharpLine。</summary>
	Tree = 1,
	/// <summary>段③ 签名重算：MaterializeFunction 的 parseLabel 分支。</summary>
	Label = 2,
	/// <summary>段④ 三步：setArgument / nestCheck / setJumpTo（arg 转换、式中函数解析都在这里）。</summary>
	Three = 3,
	/// <summary>签名预算：ComputeSignature（被 setArgument 触发的嵌套区，墙钟落在段④内）。</summary>
	Signature = 4,
}

/// <summary>
/// issue 07 P1 的解析期分账探针：按 <see cref="ParserProbeScope"/> 给两类子成本分桶。
/// <list type="bullet">
/// <item><b>词法</b>：<see cref="LexicalAnalyzer.Analyse"/>（唯一分词咽喉）。第一版只累计总量，实测出现
/// <c>lex_ms &gt; tree_ms</c> 的悖论——因为 <c>Analyse</c> 在段④（arg 转换 / 签名预算）被高频调用。</item>
/// <item><b>变量解析</b>：<see cref="IdentifierDictionary.GetVariableToken"/>（唯一变量解析咽喉）。
/// 这是 issue 07 代价 #2 点名的格式难点（表达式树绑定 <c>VariableToken</c> 实例），故单列。</item>
/// </list>
/// <para>
/// 生命周期：只由 <c>ErbLoader.ParseScript 主循环</c> 在窗口内 Activate/Deactivate（单线程），
/// 故非采集期开销 = 一次静态 bool 读；genmerge（并行 worker 亦会调这两者）在窗口之前，不计入。
/// </para>
/// </summary>
internal static class ParserProbe
{
	private const int ScopeCount = 5;

	/// <summary>非采集期恒 false。裸字段（非属性）以保证热点路径只花一条静态读。</summary>
	internal static bool Active;

	/// <summary>当前归属段。窗口内由 ErbLoader 进入/离开各段时保存-设置-恢复（单线程，无需栈）。</summary>
	internal static ParserProbeScope Scope;

	/// <summary>词法段累计（索引 = <see cref="ParserProbeScope"/>）。</summary>
	internal static readonly long[] LexTicks = new long[ScopeCount];
	internal static readonly long[] LexCalls = new long[ScopeCount];

	/// <summary>变量解析段累计（索引 = <see cref="ParserProbeScope"/>）。</summary>
	internal static readonly long[] VarTicks = new long[ScopeCount];
	internal static readonly long[] VarCalls = new long[ScopeCount];

	/// <summary>开窗并清零（在 <c>ParseScript</c> 主循环前调用）。</summary>
	internal static void Activate()
	{
		Scope = ParserProbeScope.None;
		Array.Clear(LexTicks);
		Array.Clear(LexCalls);
		Array.Clear(VarTicks);
		Array.Clear(VarCalls);
		Active = true;
	}

	/// <summary>关窗（主循环结束后立即调用；累计值保留待发射）。</summary>
	internal static void Deactivate() => Active = false;

	/// <summary>累加一次词法耗时到当前归属段。</summary>
	internal static void AddLex(long ticks)
	{
		int i = (int)Scope;
		LexTicks[i] += ticks;
		LexCalls[i]++;
	}

	/// <summary>累加一次变量解析耗时到当前归属段。</summary>
	internal static void AddVar(long ticks)
	{
		int i = (int)Scope;
		VarTicks[i] += ticks;
		VarCalls[i]++;
	}
}
