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
///
/// 跨 fork 维度（实现票 26）：<see cref="Owner"/> 标识键归属的 fork；
/// <see cref="Priority"/> / <see cref="Overrides"/> 是**显式**并存关系声明，供
/// <see cref="DialectProfile"/> 构造期裁决同键多归属；无声明即报冲突（不做优先级代数）。
/// </summary>
internal sealed class DialectModule
{
	internal DialectModule(
		Dialect id,
		IEnumerable<string> declaredKeys,
		IEnumerable<string>? hiddenKeys = null,
		string? owner = null,
		int priority = 0,
		IEnumerable<string>? overrides = null)
	{
		Id = id;
		Owner = string.IsNullOrEmpty(owner) ? id.ToString() : owner;
		Priority = priority;
		Overrides = NormalizeOwners(overrides);
		DeclaredKeys = Normalize(declaredKeys);
		HiddenKeys = Normalize(hiddenKeys);
	}

	internal Dialect Id { get; }

	/// <summary>归属 fork 标识（缺省 = <see cref="Id"/> 名），供跨 fork 冲突裁决消费。</summary>
	internal string Owner { get; }

	/// <summary>数值优先级，越大越优先；仅在同键多归属且无 override 时参与裁决。</summary>
	internal int Priority { get; }

	/// <summary>本模块显式覆盖的 owner 集合：若覆盖了同键的其余全部 owner，则本模块胜出。</summary>
	internal IReadOnlySet<string> Overrides { get; }

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

	private static HashSet<string> NormalizeOwners(IEnumerable<string>? owners)
	{
		HashSet<string> set = new(StringComparer.Ordinal);
		if (owners == null)
			return set;
		foreach (string owner in owners)
			if (!string.IsNullOrEmpty(owner))
				set.Add(owner);
		return set;
	}
}

/// <summary>
/// 会话级方言可见面（实现票 16）：profileId → 模块闭包 → 可见键集。
///
/// 规则（spec §4.2 / §6.5，与模块顺序无关）：
/// 可见面 = 所选模块 <see cref="DialectModule.DeclaredKeys"/> 的并集；
/// **未声明即隐藏**（不在并集里的键解析期不可见）；**显式 Hide 胜出**；
/// 可见键若被**多个 owner 声明**，按 override / priority 显式裁决，无声明即构造期报错（跨 fork 冲突，实现票 26）。
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
		Dictionary<string, List<DialectModule>> claimants = new(StringComparer.Ordinal);
		foreach (DialectModule module in modules)
		{
			visible.UnionWith(module.DeclaredKeys);
			hidden.UnionWith(module.HiddenKeys);
			foreach (string key in module.DeclaredKeys)
			{
				if (!claimants.TryGetValue(key, out List<DialectModule>? list))
				{
					list = [];
					claimants[key] = list;
				}
				list.Add(module);
			}
		}
		// 显式 Hide 胜出：任何被任一选中模块主张隐藏的键，即便同时被声明也不可见。
		// 隐藏键不参与跨 fork 归属裁决（已被本 profile 显式剔除）。
		visible.ExceptWith(hidden);

		// 跨 fork 同键冲突（实现票 26）：可见键若被多个 owner 声明，构造期显式裁决或报错，禁止静默合并。
		Dictionary<string, string> owners = new(StringComparer.Ordinal);
		foreach (KeyValuePair<string, List<DialectModule>> pair in claimants)
		{
			if (!visible.Contains(pair.Key))
				continue;
			List<DialectModule> distinctOwners = DeduplicateByOwner(pair.Value);
			owners[pair.Key] = distinctOwners.Count == 1
				? distinctOwners[0].Owner
				: ResolveCrossForkConflict(profileId, pair.Key, distinctOwners).Owner;
		}

		visibleKeys = visible;
		keyOwners = owners;
	}

	/// <summary>可见键 → 归属 fork（跨 fork 冲突时 = 裁决胜出者）。供测试与诊断消费。</summary>
	internal IReadOnlyDictionary<string, string> KeyOwners => keyOwners;

	/// <summary>同 owner 的多个模块声明同一键不构成冲突，按首次出现收敛为一个归属。</summary>
	private static List<DialectModule> DeduplicateByOwner(List<DialectModule> modules)
	{
		Dictionary<string, DialectModule> byOwner = new(StringComparer.Ordinal);
		List<DialectModule> ordered = [];
		foreach (DialectModule module in modules)
			if (byOwner.TryAdd(module.Owner, module))
				ordered.Add(module);
		return ordered;
	}

	/// <summary>
	/// 同键多归属裁决（不做优先级代数）：
	/// ① 恰有一个模块显式 override 其余全部 owner → 该模块胜出；
	/// ② 否则取 <see cref="DialectModule.Priority"/> 唯一最高者；
	/// ③ 都不可判 → 构造期 <see cref="CodeEE"/>（禁止静默合并）。
	/// </summary>
	private static DialectModule ResolveCrossForkConflict(string profileId, string key, IReadOnlyList<DialectModule> claimants)
	{
		List<DialectModule> overrideWinners = [];
		foreach (DialectModule candidate in claimants)
		{
			bool coversAll = true;
			foreach (DialectModule other in claimants)
			{
				if (ReferenceEquals(other, candidate))
					continue;
				if (!candidate.Overrides.Contains(other.Owner))
				{
					coversAll = false;
					break;
				}
			}
			if (coversAll)
				overrideWinners.Add(candidate);
		}
		if (overrideWinners.Count == 1)
			return overrideWinners[0];
		if (overrideWinners.Count > 1)
			throw new CodeEE($"dialect profile「{profileId}」的键「{key}」存在歧义的 override 声明（多个模块同时覆盖其余全部 owner）。");

		int maxPriority = int.MinValue;
		foreach (DialectModule candidate in claimants)
			if (candidate.Priority > maxPriority)
				maxPriority = candidate.Priority;
		List<DialectModule> top = [];
		foreach (DialectModule candidate in claimants)
			if (candidate.Priority == maxPriority)
				top.Add(candidate);
		if (top.Count == 1)
			return top[0];

		throw new CodeEE(
			$"dialect profile「{profileId}」的键「{key}」被多个 fork（{string.Join(", ", OwnerNames(claimants))}）声明且未声明 priority/override —— 禁止静默合并。");
	}

	private static IEnumerable<string> OwnerNames(IReadOnlyList<DialectModule> claimants)
	{
		foreach (DialectModule module in claimants)
			yield return module.Owner;
	}

	/// <summary>profile 标识（如 <c>v24pure</c> / <c>snake</c> / <c>eratw</c>）。</summary>
	internal string ProfileId { get; }

	/// <summary>模块闭包（顺序只用于依赖解析，不参与键裁决）。</summary>
	internal IReadOnlyList<DialectModule> Modules { get; }

	private readonly SortedSet<string> visibleKeys;
	private readonly Dictionary<string, string> keyOwners;

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