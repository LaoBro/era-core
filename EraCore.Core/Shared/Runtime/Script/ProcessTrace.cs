using System;
using MinorShift.Emuera.GameView;

namespace MinorShift.Emuera.Runtime.Script;

/// <summary>
/// 候选 4 · 第 1 刀：ProcessState 的调试 trace 通道（ADR-0011 决策三违规修复 + 调用栈抽出的前置）。
///
/// 形态取自 ADR-0012 的 sink 先例（<c>Action<LogicalLine></c> scaningLine），
/// 而非 ADR-0039 的「直接传目的地 List」——两者关键差异：dTraceLogList 挂在 ConsoleStateData 上，
/// 且受 <c>runningERBfromMemory</c> 这一<b>运行时可变</b>状态守卫（EmueraConsole.cs:409/414/420）。
/// 若改为传 <c>List<string></c>，该守卫会被静默丢弃。委托把守卫留在 console 侧，
/// ProcessState 因此不再知道 EmueraConsole / Program.DebugMode / GlobalStatic.Process 的存在。
///
/// <c>Enabled</c> 在构造时快照 <c>Program.DebugMode</c>：这是既有行为（原 ProcessState 构造函数
/// 在非 DebugMode 下丢弃 console 引用），不是本刀引入的策略快照化。调用点保留
/// <c>if (trace.Enabled)</c> 外层判断，用于在非 DebugMode 下跳过热路径（Return/IntoFunction）
/// 的 <c>string.Format</c> 开销——与原先的 <c>if (Program.DebugMode)</c> 逐行对应。
/// </summary>
internal readonly struct ProcessTrace
{
	readonly Action<string>? add;
	readonly Action? remove;
	readonly Action? clear;

	ProcessTrace(Action<string>? add, Action? remove, Action? clear)
	{
		this.add = add;
		this.remove = remove;
		this.clear = clear;
	}

	/// <summary>关闭的通道（非 DebugMode、测试默认）。</summary>
	public static ProcessTrace Disabled => default;

	/// <summary>通道是否开启。等价于原先在各调用点重复判断的 <c>Program.DebugMode</c>。</summary>
	public bool Enabled => add is not null;

	/// <summary>
	/// 从 console 取通道。非 DebugMode（或 console 为 null）返回 <see cref="Disabled"/>，
	/// 不持有 console 引用 —— 沿用原构造函数的既有行为。
	/// </summary>
	public static ProcessTrace For(EmueraConsole? console)
	{
		if (!Program.DebugMode || console is null)
			return Disabled;
		return new(console.DebugAddTraceLog, console.DebugRemoveTraceLog, console.DebugClearTraceLog);
	}

	/// <summary>测试用：注入记录型通道，使原先在 DebugMode=false 下不可观测的 trace 序列可被断言。</summary>
	internal static ProcessTrace FromDelegates(Action<string>? add, Action? remove = null, Action? clear = null)
		=> new(add, remove, clear);

	public void Add(string str)
	{
		if (add is null) return;
		add(str);
	}

	public void Remove()
	{
		if (remove is null) return;
		remove();
	}

	/// <summary>
	/// <paramref name="gate"/> 为 null 表示无条件清空。短路顺序与既有语义逐位等价：
	/// 通道关闭时<b>不求值</b> gate（原 <c>Program.DebugMode</c> 为 false 时不会访问
	/// <c>GlobalStatic.Process.MethodStack()</c>，测试环境下那是 NRE 源）。
	/// </summary>
	public void Clear(Func<bool>? gate)
	{
		if (clear is null) return;
		if (gate is not null && !gate()) return;
		clear();
	}
}
