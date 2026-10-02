using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
// 别名不能用 "Config"：本文件所在命名空间位于 MinorShift.Emuera.Runtime 之下，
// 名字 Config 会先命中兄弟命名空间 MinorShift.Emuera.Runtime.Config。
using EmueraConfig = MinorShift.Emuera.Runtime.Config.Config;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>
/// 方言模块：一份**不可变**的键声明（<see cref="DeclaredKeys"/> 增 + <see cref="HiddenKeys"/> 显式隐藏）。
/// 模块不知道自己被哪些 profile 选中，只表达「这些键归我」与「这些键我不给」。
/// </summary>
internal sealed class DialectModule
{
	internal DialectModule(Dialect id, IEnumerable<string> declaredKeys, IEnumerable<string>? hiddenKeys = null)
	{
		Id = id;
		DeclaredKeys = Normalize(declaredKeys);
		HiddenKeys = Normalize(hiddenKeys);
	}

	internal Dialect Id { get; }
	internal IReadOnlySet<string> DeclaredKeys { get; }
	internal IReadOnlySet<string> HiddenKeys { get; }

	/// <summary>
	/// 键归一化：解析期查表统一按大写比对（对齐 <c>GetFunctionIdentifier</c> 的 ToUpper 语义），
	/// 使可见面判定与 <see cref="EmueraConfig.IgnoreCase"/> 无关、可全局缓存。
	/// </summary>
	internal static string CanonicalKey(string name) => name.ToUpperInvariant();

	private static HashSet<string> Normalize(IEnumerable<string>? keys)
	{
		HashSet<string> set = new(StringComparer.Ordinal);
		if (keys == null)
			return set;
		foreach (string key in keys)
			set.Add(CanonicalKey(key));
		return set;
	}
}

/// <summary>
/// 会话级方言可见面（实现票 16）：profileId → 模块闭包 → 可见键集。
///
/// 规则（spec §4.2，与模块顺序无关）：
/// 可见面 = 所选模块 <see cref="DialectModule.DeclaredKeys"/> 的并集；
/// **未声明即隐藏**（不在并集里的键解析期不可见）；**显式 Hide 胜出**。
/// </summary>
internal sealed class DialectProfile
{
	internal DialectProfile(string profileId, IReadOnlyList<DialectModule> modules)
	{
		if (string.IsNullOrEmpty(profileId))
			throw new CodeEE("dialect profile id 不能为空。");
		if (modules == null || modules.Count == 0)
			throw new CodeEE($"dialect profile「{profileId}」的模块闭包为空。");
		HashSet<string> moduleIds = new(StringComparer.Ordinal);
		foreach (DialectModule module in modules)
		{
			if (module == null)
				throw new CodeEE($"dialect profile「{profileId}」的模块闭包含 null 项。");
			if (!moduleIds.Add(module.Id.ToString()))
				throw new CodeEE($"dialect profile「{profileId}」的模块闭包重复包含模块「{module.Id}」。");
		}

		ProfileId = profileId;
		Modules = modules;

		SortedSet<string> visible = new(StringComparer.Ordinal);
		HashSet<string> hidden = new(StringComparer.Ordinal);
		foreach (DialectModule module in modules)
		{
			visible.UnionWith(module.DeclaredKeys);
			hidden.UnionWith(module.HiddenKeys);
		}
		// 显式 Hide 胜出：任何被任一选中模块主张隐藏的键，即便同时被声明也不可见。
		visible.ExceptWith(hidden);
		visibleKeys = visible;
	}

	/// <summary>profile 标识（如 <c>v24pure</c> / <c>snake</c> / <c>eratw</c>）。</summary>
	internal string ProfileId { get; }

	/// <summary>模块闭包（顺序只用于依赖解析，不参与键裁决）。</summary>
	internal IReadOnlyList<DialectModule> Modules { get; }

	private readonly SortedSet<string> visibleKeys;

	/// <summary>可见键集，按序号排序（surface 快照需确定性遍历顺序，见实现票 17）。</summary>
	internal IReadOnlyCollection<string> VisibleKeys => visibleKeys;

	internal bool IsVisible(string name) => visibleKeys.Contains(DialectModule.CanonicalKey(name));
}

/// <summary>
/// 注册表 → 会话级表的投影（实现票 16 的落地动作）。
/// 解析/执行热路径只读投影结果，**不做任何 profile 判断**。
/// </summary>
internal static class DialectProjection
{
	internal static Dictionary<string, T> Filter<T>(IReadOnlyDictionary<string, T> source, DialectProfile profile)
	{
		Dictionary<string, T> filtered = new(EmueraConfig.StrComper);
		foreach (KeyValuePair<string, T> pair in source)
		{
			if (profile.IsVisible(pair.Key))
				filtered.Add(pair.Key, pair.Value);
		}
		return filtered;
	}
}