using System;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 方言 profile + 模块闭包 + 投影（实现票 16）。
///
/// 证据边界：P0 数学/位/求值函数（实现票 18）已作为 snake 声明落地，其余 P0 键（票 19–24）尚未；
/// 「v24pure 隐藏方言键、snake 显示」既用真实 P0 键（SIN 等）也用**替身方言键**（把真实基线键 PRINT
/// 挪进方言模块）表达，前者见 <see cref="P0MathBitEvalFunctionTests"/>。逐位面快照门禁由实现票 17 承担。
/// </summary>
public sealed class DialectProfileTests
{
	private static DialectProfile ProfileOf(string id, params DialectModule[] modules) => new(id, modules);

	// ---------- 可见面规则 ----------

	[Fact]
	public void Surface_is_the_union_of_selected_module_declarations()
	{
		var snake = new DialectModule(Dialect.Snake, ["ALPHA", "BETA"]);
		var eratw = new DialectModule(Dialect.Eratw, ["GAMMA"]);
		var profile = ProfileOf("test", snake, eratw);

		Assert.True(profile.IsVisible("ALPHA"));
		Assert.True(profile.IsVisible("BETA"));
		Assert.True(profile.IsVisible("GAMMA"));
		Assert.Equal(["ALPHA", "BETA", "GAMMA"], profile.VisibleKeys.ToArray());
	}

	[Fact]
	public void Key_not_declared_by_any_selected_module_is_invisible()
	{
		// 只选 snake 模块 → baseline 的键未被任何所选模块声明 → 全部隐藏。
		var snake = new DialectModule(Dialect.Snake, ["ALPHA"]);
		var profile = ProfileOf("test", snake);

		Assert.True(profile.IsVisible("ALPHA"));
		Assert.False(profile.IsVisible("BASELINE_ONLY"));
		Assert.False(profile.IsVisible("DELTA"));
	}

	[Fact]
	public void Explicit_hide_wins_over_declaration()
	{
		var declares = new DialectModule(Dialect.Snake, ["ALPHA", "BETA"]);
		var hides = new DialectModule(Dialect.Eratw, declaredKeys: [], hiddenKeys: ["ALPHA"]);
		var profile = ProfileOf("test", declares, hides);

		Assert.False(profile.IsVisible("ALPHA"));
		Assert.True(profile.IsVisible("BETA"));
	}

	[Fact]
	public void Module_order_does_not_affect_the_surface()
	{
		var a = new DialectModule(Dialect.Baseline, ["ALPHA"]);
		var b = new DialectModule(Dialect.Snake, ["BETA"], ["ALPHA"]);

		Assert.Equal(ProfileOf("t", a, b).VisibleKeys.ToArray(), ProfileOf("t", b, a).VisibleKeys.ToArray());
	}

	[Fact]
	public void Visibility_is_case_insensitive()
	{
		var module = new DialectModule(Dialect.Snake, ["MixedCase"]);
		var profile = ProfileOf("test", module);

		Assert.True(profile.IsVisible("mixedcase"));
		Assert.True(profile.IsVisible("MIXEDCASE"));
	}

	// ---------- 闭包校验 ----------

	[Fact]
	public void Profile_rejects_empty_or_duplicate_or_null_module_closure()
	{
		Assert.Throws<CodeEE>(() => ProfileOf("test"));
		Assert.Throws<CodeEE>(() => ProfileOf("test", null!));
		Assert.Throws<CodeEE>(() => ProfileOf("test",
			new DialectModule(Dialect.Snake, []),
			new DialectModule(Dialect.Snake, [])));
	}

	[Fact]
	public void Unknown_profile_id_throws_at_resolution()
	{
		Assert.Throws<CodeEE>(() => DialectRegistry.Resolve("v99unknown"));
	}

	[Fact]
	public void Closure_referencing_an_unregistered_module_throws_at_construction()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		// 验收「缺模块 → 构造期报错」：闭包引用未注册模块，构造 surface 即失败（不静默降级）。
		Assert.Throws<CodeEE>(() => DialectRegistry.BuildFromClosure("test-missing-module", [(Dialect)999]));
	}

	// ---------- 真实三 profile ----------

	[Fact]
	public void Resolved_surface_is_cached_by_profile_id()
	{
		Assert.Same(
			DialectRegistry.Resolve(DialectRegistry.SnakeId),
			DialectRegistry.Resolve(DialectRegistry.SnakeId));
	}

	[Fact]
	public void Real_profiles_form_a_monotone_closure_chain()
	{
		DialectProfile v24 = DialectRegistry.Resolve(DialectRegistry.V24PureId);
		DialectProfile snake = DialectRegistry.Resolve(DialectRegistry.SnakeId);
		DialectProfile eratw = DialectRegistry.Resolve(DialectRegistry.EratwId);

		Assert.All(v24.VisibleKeys, key => Assert.True(snake.IsVisible(key)));
		Assert.All(snake.VisibleKeys, key => Assert.True(eratw.IsVisible(key)));
	}

	[Fact]
	public void Baseline_holds_the_untagged_registry_surface()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		// 方言声明：snake 已登记 Skia P0 数学/取整/无检查/位/求值 19 键（票 18）+ 集合 MAP_* 6 键（票 19）
		// + 动态调用指令 5 键（票 20）+ NF 输入指令 4 键（票 21）；eratw 暂无游戏适配键。
		Assert.Equal(34, DialectRegistry.Snake.DeclaredKeys.Count);
		Assert.Empty(DialectRegistry.Eratw.DeclaredKeys);

		// baseline = 注册表全键 − 方言声明键：方言键不落入 v24pure，既有键仍在。
		Assert.Contains("SIN", DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain("SIN", DialectRegistry.Baseline.DeclaredKeys);
		Assert.Contains("PRINT", DialectRegistry.Baseline.DeclaredKeys);
		Assert.DoesNotContain("PRINT", DialectRegistry.Snake.DeclaredKeys);
	}

	// ---------- 投影 ----------

	[Fact]
	public void Projection_hides_undeclared_dialect_key_and_shows_it_when_module_selected()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		// 替身：把真实基线键 PRINT 划归 snake 方言模块。
		var baselineWithoutPrint = new DialectModule(
			Dialect.Baseline,
			DialectRegistry.Baseline.DeclaredKeys.Where(key => key != "PRINT"));
		var dialectWithPrint = new DialectModule(Dialect.Snake, ["PRINT"]);

		var v24Like = new DialectProfile("test-v24", [baselineWithoutPrint]);
		var snakeLike = new DialectProfile("test-snake", [baselineWithoutPrint, dialectWithPrint]);

		var instructions = FunctionIdentifier.GetInstructionNameDic();
		var hidden = DialectProjection.Filter(instructions, v24Like);
		var shown = DialectProjection.Filter(instructions, snakeLike);

		Assert.False(hidden.ContainsKey("PRINT"));
		Assert.True(hidden.ContainsKey("PRINTL"));
		Assert.True(shown.ContainsKey("PRINT"));
		Assert.True(shown.ContainsKey("PRINTL"));
		// 投影只筛键，不换实例。
		Assert.Same(instructions["PRINT"], shown["PRINT"]);
	}

	// ---------- 会话隔离 ----------

	[Fact]
	public async Task Concurrent_scopes_keep_their_own_profile()
	{
		var profileA = new DialectProfile("iso-a", [new DialectModule(Dialect.Snake, ["ALPHA"])]);
		var profileB = new DialectProfile("iso-b", [new DialectModule(Dialect.Eratw, ["BETA"])]);

		var taskA = Task.Run(() =>
		{
			using var scope = GlobalStatic.OpenScope(new ConfigData(), profileA);
			return GlobalStatic.DialectProfile;
		});
		var taskB = Task.Run(() =>
		{
			using var scope = GlobalStatic.OpenScope(new ConfigData(), profileB);
			return GlobalStatic.DialectProfile;
		});

		DialectProfile[] results = await Task.WhenAll(taskA, taskB);
		Assert.Same(profileA, results[0]);
		Assert.Same(profileB, results[1]);
	}

	[Fact]
	public void Unbound_scope_defaults_to_v24pure()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		Assert.Same(DialectRegistry.Resolve(DialectRegistry.V24PureId), GlobalStatic.DialectProfile);
	}
}

/// <summary>
/// 投影接缝落到 <see cref="IdentifierDictionary"/> 的端到端证据（实现票 16）：
/// 被隐藏的键在**解析期**不可见——命中既有「識別できない識別子」错误路径。
/// </summary>
[Collection("LoaderTests")]
public class DialectProfileProjectionIntegrationTests
{
	[Fact]
	public async Task Hidden_key_is_invisible_at_parse_time_while_kept_keys_resolve()
	{
		var baselineWithoutPrint = new DialectModule(
			Dialect.Baseline,
			DialectRegistry.Baseline.DeclaredKeys.Where(key => key != "PRINT"));
		var profile = new DialectProfile("test-hide-print", [baselineWithoutPrint]);

		using var harness = new LoaderTestHarness(dialectProfile: profile);
		await harness.InitializeAsync();

		Assert.Null(harness.IdDic.GetFunctionIdentifier("PRINT"));
		Assert.NotNull(harness.IdDic.GetFunctionIdentifier("PRINTL"));
		// nameDic 与 instructionDic 一致过滤：被隐藏键走「識別できない識別子」而非「命令名として使われています」。
		Assert.Throws<IdentifierNotFoundCodeEE>(() => harness.IdDic.ThrowException("PRINT", isFunc: false));
		var keptError = Record.Exception(() => harness.IdDic.ThrowException("PRINTL", isFunc: false));
		Assert.NotNull(keptError);
		Assert.IsNotType<IdentifierNotFoundCodeEE>(keptError);
	}

	[Fact]
	public async Task Two_projections_in_one_session_scope_keep_separate_surfaces()
	{
		var baselineWithoutPrint = new DialectModule(
			Dialect.Baseline,
			DialectRegistry.Baseline.DeclaredKeys.Where(key => key != "PRINT"));

		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var varData = harness.VEvaluator.VariableData;

		// 同一 scope 内两次投影：隐藏 PRINT 的会话 vs 全基线的会话，互不影响。
		var restricted = new IdentifierDictionary(
			varData, new DialectProfile("test-restricted", [baselineWithoutPrint]));
		var full = new IdentifierDictionary(
			varData, new DialectProfile("test-full", [DialectRegistry.Baseline]));

		Assert.Null(restricted.GetFunctionIdentifier("PRINT"));
		Assert.NotNull(restricted.GetFunctionIdentifier("PRINTL"));
		Assert.NotNull(full.GetFunctionIdentifier("PRINT"));
	}
}