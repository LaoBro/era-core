using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc.Function;
using System;
using System.Collections.Generic;
using System.Text;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>面快照的一行：一个可见键及其角色（指令 / 式中函数）。</summary>
internal readonly record struct SurfaceEntry(string Name, string Kind);

/// <summary>
/// 方言可见面（实现票 17）：把某个 profile 投影出的**运行期表**序列化成稳定的
/// <c>name&lt;TAB&gt;kind</c> 行集，作为静态门禁（面快照）与工具的统一真相源。
///
/// 面 = 解析器实际读取的两张表：
/// - <c>directive</c>：<c>funcDic</c> 中的**本征指令**（<see cref="FunctionIdentifier.IsMethod"/> 为假）；
/// - <c>function</c>：<c>methodList</c> 中的式中函数。
/// <c>funcDic</c> 会在静态构造末尾把式中函数以 METHOD 指令形式合并进来，那些项按 <c>function</c>
/// 计（由 methodList 侧产出），不重复计为指令——与「指令 / 函数」两分口径（asset 01 §1/§2）一致。
///
/// 序列化口径（spec §7）：每行 <c>name&lt;TAB&gt;kind</c>，按行做序号排序，UTF-8 / LF，末尾换行。
/// </summary>
internal static class DialectSurface
{
	internal const string DirectiveKind = "directive";
	internal const string FunctionKind = "function";

	/// <summary>按 profile 计算可见面条目（已排序、确定性）。</summary>
	internal static IReadOnlyList<SurfaceEntry> Entries(DialectProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);

		List<SurfaceEntry> entries = [];
		foreach (KeyValuePair<string, FunctionIdentifier> pair in FunctionIdentifier.GetInstructionNameDic())
		{
			if (!pair.Value.IsMethod() && profile.IsVisible(pair.Key))
				entries.Add(new SurfaceEntry(pair.Key, DirectiveKind));
		}
		foreach (string key in FunctionMethodCreator.GetMethodList().Keys)
		{
			if (profile.IsVisible(key))
				entries.Add(new SurfaceEntry(key, FunctionKind));
		}

		// 排序口径：整行序号排序 → 主键 name、次键 kind。同一名字兼具两角色时 directive 在前。
		entries.Sort(static (a, b) =>
		{
			int byName = string.CompareOrdinal(a.Name, b.Name);
			return byName != 0 ? byName : string.CompareOrdinal(a.Kind, b.Kind);
		});
		return entries;
	}

	/// <summary>序列化条目为规范文本（UTF-8 / LF，每行 <c>name&lt;TAB&gt;kind</c>，末尾换行）。</summary>
	internal static string Serialize(IReadOnlyList<SurfaceEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		StringBuilder builder = new();
		foreach (SurfaceEntry entry in entries)
			builder.Append(entry.Name).Append('\t').Append(entry.Kind).Append('\n');
		return builder.ToString();
	}

	/// <summary><see cref="Entries"/> → <see cref="Serialize"/> 的便捷入口。</summary>
	internal static string Serialize(DialectProfile profile) => Serialize(Entries(profile));

	/// <summary>可见面条目集（用于闭包单调性等集合断言）。</summary>
	internal static IReadOnlySet<SurfaceEntry> EntrySet(DialectProfile profile)
		=> new HashSet<SurfaceEntry>(Entries(profile));
}