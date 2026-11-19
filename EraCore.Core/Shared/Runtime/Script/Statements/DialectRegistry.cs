using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>
/// 单核心 + 两个 canonical profile 的方言注册表（实现票 16 / ADR-0055）。
///
/// 键来源：
/// - <c>snake</c> 的声明键来自 descriptor 的 <see cref="Dialect"/> 标签（实现票 15 的登记接缝）；
/// - <c>baseline</c> 的声明键 = 静态注册表中**未被任何方言模块登记**的键（隐式基线）。
///   这样 era-core 自身（含移植后新增）的项默认可见，结构上不可能把方言键泄漏进 v24pure。
///
/// profile：
/// - canonical：<c>v24pure</c>（默认）/ <c>snake</c>；
/// - <c>eratw</c> 仅为别名（ADR-0055）：<see cref="Resolve"/> 归一为 <c>snake</c> 并复用同一缓存对象，
///   不列 <see cref="KnownProfileIds"/>、不单独维护快照。
///
/// surface 按 canonical profileId 缓存：注册表在进程内静态不变 → 同 profileId 恒得同一 surface。
/// </summary>
internal static class DialectRegistry
{
	internal const string V24PureId = "v24pure";
	internal const string SnakeId = "snake";
	/// <summary>兼容别名（非 canonical）：旧脚本 / 旧设置 / 旧 server 记录里的 <c>eratw</c>。</summary>
	internal const string EratwAliasId = "eratw";

	/// <summary>未显式绑定 profile 时的默认值 = 移植前行为（<c>v24pure</c>）。</summary>
	internal const string DefaultProfileId = V24PureId;

	private static readonly Dictionary<string, string> ProfileAliases =
		new(StringComparer.Ordinal)
		{
			[EratwAliasId] = SnakeId,
		};

	/// <summary>
	/// 模块的静态声明（纯数据）：键面 + 隐藏键 + 语言能力位。
	/// 跨 fork 的 owner / priority / overrides 已移除（ADR-0055）。
	/// </summary>
	internal sealed record ModuleDeclaration(
		Dialect Id,
		IEnumerable<string> DeclaredKeys,
		IEnumerable<string>? HiddenKeys = null,
		LanguageCapability Capabilities = LanguageCapability.None);

	/// <summary>声明 → 不可变模块（纯函数，无 ambient 依赖）。</summary>
	internal static DialectModule BuildModule(ModuleDeclaration declaration)
	{
		ArgumentNullException.ThrowIfNull(declaration);
		return new DialectModule(
			id: declaration.Id,
			declaredKeys: declaration.DeclaredKeys,
			hiddenKeys: declaration.HiddenKeys,
			capabilities: declaration.Capabilities);
	}

	/// <summary>
	/// 隐式基线 = 注册表全键 − 各方言模块的 <see cref="ModuleDeclaration.DeclaredKeys"/>。
	/// <see cref="ModuleDeclaration.HiddenKeys"/> **不参与**：它只在 <see cref="DialectProfile"/>
	/// 构造期对可见面做减法（spec §4.3 不变量 1/2）。
	/// </summary>
	internal static SortedSet<string> DeriveBaseline(
		IEnumerable<string> registeredKeys,
		IEnumerable<ModuleDeclaration> dialectDeclarations)
	{
		ArgumentNullException.ThrowIfNull(registeredKeys);
		ArgumentNullException.ThrowIfNull(dialectDeclarations);
		SortedSet<string> baseline = new(registeredKeys, StringComparer.Ordinal);
		foreach (ModuleDeclaration declaration in dialectDeclarations)
		{
			foreach (string key in declaration.DeclaredKeys)
				baseline.Remove(DialectModule.CanonicalKey(key));
		}
		return baseline;
	}

	/// <summary>profileId → 模块闭包（单一事实源；顺序 = 由窄到宽，与闭包单调性一致）。</summary>
	private static readonly Dictionary<string, Dialect[]> closures = new(StringComparer.Ordinal)
	{
		[V24PureId] = [Dialect.Baseline],
		[SnakeId] = [Dialect.Baseline, Dialect.Snake],
	};

	/// <summary>模块 id → 模块（P3a：按 id 查找，杜绝位置式契约）。</summary>
	private static readonly Lazy<Dictionary<Dialect, DialectModule>> modules =
		new(CreateModules, LazyThreadSafetyMode.ExecutionAndPublication);

	private static readonly ConcurrentDictionary<string, DialectProfile> surfaceCache = new(StringComparer.Ordinal);

	internal static DialectModule Baseline => ModuleOf(Dialect.Baseline);
	internal static DialectModule Snake => ModuleOf(Dialect.Snake);

	/// <summary>未显式绑定 profile 时的默认 surface（<c>v24pure</c>）。</summary>
	internal static DialectProfile Default => Resolve(DefaultProfileId);

	/// <summary>
	/// canonical profile id 集合（实现票 25）。**不含别名**——UI 下拉与启动期错误提示使用它。
	/// 不构建 surface，故无 Config ambient 依赖，可在 scope 之外用于 fail-fast 校验。
	/// </summary>
	internal static IReadOnlyCollection<string> KnownProfileIds => closures.Keys;

	/// <summary>
	/// 别名 → canonical id（<c>eratw</c> → <c>snake</c>）；canonical / 未知 id 原样返回。
	/// 供 CLI / Server / Agent / MAUI 持久化与记录归一（ADR-0055）。
	/// </summary>
	internal static string CanonicalizeProfileId(string profileId)
		=> ProfileAliases.TryGetValue(profileId, out string? canonical) ? canonical : profileId;

	/// <summary>profile id 是否已注册（含别名；启动期校验用，不触发 surface 构建）。</summary>
	internal static bool IsKnownProfile(string profileId)
		=> !string.IsNullOrEmpty(profileId) && closures.ContainsKey(CanonicalizeProfileId(profileId));

	/// <summary>
	/// 解析 profileId → 不可变 surface（按 canonical id 缓存）。未知 id → <see cref="CodeEE"/>（构造期 fail-fast）。
	/// 别名（<c>eratw</c>）归一到 <c>snake</c>：返回同一缓存对象，且 <see cref="DialectProfile.ProfileId"/> 为 canonical id。
	/// </summary>
	internal static DialectProfile Resolve(string profileId)
	{
		string canonical = CanonicalizeProfileId(profileId);
		if (!string.Equals(canonical, profileId, StringComparison.Ordinal))
			EmueraLog.Info("dialect", $"profile「{profileId}」已废弃，按「{canonical}」处理。");
		return surfaceCache.GetOrAdd(canonical, Build);
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
		if (modules.Value.TryGetValue(id, out DialectModule? module))
			return module;
		throw new CodeEE($"dialect profile 引用了未注册的模块「{id}」——模块闭包不匹配。");
	}

	private static Dictionary<Dialect, DialectModule> CreateModules()
	{
		IReadOnlyDictionary<string, FunctionIdentifier> instructions = FunctionIdentifier.GetInstructionNameDic();
		IReadOnlyDictionary<string, FunctionMethod> methods = FunctionMethodCreator.GetMethodList();
		IReadOnlyList<InstructionDescriptor> instructionDescriptors = BuiltInRegistry.Instructions;
		IReadOnlyList<FunctionMethodDescriptor> methodDescriptors = BuiltInRegistry.Methods;

		SortedSet<string> snake = new(StringComparer.Ordinal);

		foreach (InstructionDescriptor descriptor in instructionDescriptors)
			RouteByDialect(descriptor.Dialect, descriptor.Name);
		foreach (FunctionMethodDescriptor descriptor in methodDescriptors)
			RouteByDialect(descriptor.Dialect, descriptor.Name);

		void RouteByDialect(Dialect dialect, string name)
		{
			switch (dialect)
			{
				case Dialect.Snake: snake.Add(DialectModule.CanonicalKey(name)); break;
				// Baseline：默认即可见，无需显式登记（增量收编原则，见实现票 15）。
				case Dialect.Baseline: break;
			}
		}

		// baseline 永远推导、不在声明表里显式登记（spec §4.3 不变量 1）。
		// snake 是唯一方言模块；语言能力 Float 由 snake 声明（ADR-0055 / 票 27）。
		ModuleDeclaration[] dialectDeclarations =
		[
			new(Dialect.Snake, snake, Capabilities: LanguageCapability.Float),
		];

		SortedSet<string> registeredKeys = new(StringComparer.Ordinal);
		registeredKeys.UnionWith(instructions.Keys);
		registeredKeys.UnionWith(methods.Keys);
		SortedSet<string> baseline = DeriveBaseline(registeredKeys, dialectDeclarations);

		Dictionary<Dialect, DialectModule> modulesById = new(dialectDeclarations.Length + 1);
		AddModule(modulesById, new(Dialect.Baseline, baseline));
		foreach (ModuleDeclaration declaration in dialectDeclarations)
			AddModule(modulesById, declaration);
		return modulesById;

		static void AddModule(Dictionary<Dialect, DialectModule> target, ModuleDeclaration declaration)
		{
			DialectModule module = BuildModule(declaration);
			if (!target.TryAdd(module.Id, module))
				throw new CodeEE($"方言模块声明重复：「{module.Id}」。");
		}
	}
}
