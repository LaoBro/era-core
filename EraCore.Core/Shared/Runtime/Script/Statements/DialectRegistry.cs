using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>
/// 三模块 + 三 profile 的方言注册表（实现票 16）。
///
/// 键来源：
/// - <c>snake</c> / <c>eratw</c> 的声明键来自 descriptor 的 <see cref="Dialect"/> 标签（实现票 15 的登记接缝）；
/// - <c>baseline</c> 的声明键 = 静态注册表中**未被任何方言模块登记**的键（隐式基线）。
///   这样 era-core 自身（含移植后新增）的项默认可见，结构上不可能把方言键泄漏进 v24pure。
///
/// surface 按 profileId 缓存：注册表在进程内静态不变 → 同 profileId 恒得同一 surface。
/// </summary>
internal static class DialectRegistry
{
	internal const string V24PureId = "v24pure";
	internal const string SnakeId = "snake";
	internal const string EratwId = "eratw";

	/// <summary>未显式绑定 profile 时的默认值 = 移植前行为。</summary>
	internal const string DefaultProfileId = V24PureId;

	/// <summary>profileId → 模块闭包（单一事实源；顺序 = 由窄到宽，与闭包单调性一致）。</summary>
	private static readonly Dictionary<string, Dialect[]> closures = new(StringComparer.Ordinal)
	{
		[V24PureId] = [Dialect.Baseline],
		[SnakeId] = [Dialect.Baseline, Dialect.Snake],
		[EratwId] = [Dialect.Baseline, Dialect.Snake, Dialect.Eratw],
	};

	private static readonly Lazy<DialectModule[]> modules =
		new(CreateModules, LazyThreadSafetyMode.ExecutionAndPublication);

	private static readonly ConcurrentDictionary<string, DialectProfile> surfaceCache = new(StringComparer.Ordinal);

	internal static DialectModule Baseline => modules.Value[0];
	internal static DialectModule Snake => modules.Value[1];
	internal static DialectModule Eratw => modules.Value[2];

	/// <summary>未显式绑定 profile 时的默认 surface（<c>v24pure</c>）。</summary>
	internal static DialectProfile Default => Resolve(DefaultProfileId);

	/// <summary>
	/// 解析 profileId → 不可变 surface（按 id 缓存）。未知 id → <see cref="CodeEE"/>（构造期 fail-fast）。
	/// </summary>
	internal static DialectProfile Resolve(string profileId)
	{
		return surfaceCache.GetOrAdd(profileId, Build);
	}

	private static DialectProfile Build(string profileId)
	{
		if (!closures.TryGetValue(profileId, out Dialect[]? closure))
			throw new CodeEE($"未知的 dialect profile「{profileId}」——模块闭包不匹配。已知：{string.Join(", ", closures.Keys)}。");
		return BuildFromClosure(profileId, closure);
	}

	/// <summary>
	/// 闭包 → 不可变 surface。引用了未注册模块在此**构造期**报错（验收：缺模块 → 构造期报错）。
	/// </summary>
	internal static DialectProfile BuildFromClosure(string profileId, IReadOnlyList<Dialect> closure)
	{
		List<DialectModule> selected = new(closure.Count);
		foreach (Dialect id in closure)
			selected.Add(ModuleOf(id));
		return new DialectProfile(profileId, selected);
	}

	private static DialectModule ModuleOf(Dialect id)
	{
		foreach (DialectModule module in modules.Value)
		{
			if (module.Id == id)
				return module;
		}
		throw new CodeEE($"dialect profile 引用了未注册的模块「{id}」——模块闭包不匹配。");
	}

	private static DialectModule[] CreateModules()
	{
		IReadOnlyDictionary<string, FunctionIdentifier> instructions = FunctionIdentifier.GetInstructionNameDic();
		IReadOnlyDictionary<string, FunctionMethod> methods = FunctionMethodCreator.GetMethodList();
		IReadOnlyList<InstructionDescriptor> instructionDescriptors = BuiltInRegistry.Instructions;
		IReadOnlyList<FunctionMethodDescriptor> methodDescriptors = BuiltInRegistry.Methods;

		SortedSet<string> snake = new(StringComparer.Ordinal);
		SortedSet<string> eratw = new(StringComparer.Ordinal);

		foreach (InstructionDescriptor descriptor in instructionDescriptors)
			RouteByDialect(descriptor.Dialect, descriptor.Name);
		foreach (FunctionMethodDescriptor descriptor in methodDescriptors)
			RouteByDialect(descriptor.Dialect, descriptor.Name);

		void RouteByDialect(Dialect dialect, string name)
		{
			switch (dialect)
			{
				case Dialect.Snake: snake.Add(DialectModule.CanonicalKey(name)); break;
				case Dialect.Eratw: eratw.Add(DialectModule.CanonicalKey(name)); break;
				// Baseline：默认即可见，无需显式登记（增量收编原则，见实现票 15）。
				case Dialect.Baseline: break;
			}
		}

		// 隐式基线 = 注册表全键 − 方言模块声明键。
		SortedSet<string> baseline = new(StringComparer.Ordinal);
		baseline.UnionWith(instructions.Keys);
		baseline.UnionWith(methods.Keys);
		baseline.ExceptWith(snake);
		baseline.ExceptWith(eratw);

		return
		[
			new DialectModule(Dialect.Baseline, baseline),
			new DialectModule(Dialect.Snake, snake),
			new DialectModule(Dialect.Eratw, eratw),
		];
	}
}