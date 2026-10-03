using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 跨 fork 同键冲突规则（实现票 26，spec §6.5 / 决策票 13）。
///
/// 可见键被多个 **owner** 声明时，构造 profile 必须显式裁决：override 或 priority；
/// 二者皆无 → 构造期报错（禁止静默合并）。单 fork 内「显式 Hide 胜出」不受影响。
/// 不做优先级代数：裁决只看 override 覆盖关系与数值 priority 的唯一最高者。
/// </summary>
public sealed class DialectCrossForkConflictTests
{
	private static DialectProfile Profile(params DialectModule[] modules)
		=> new("conflict-test", modules);

	[Fact]
	public void Undeclared_same_key_across_owners_throws_at_construction()
	{
		var skia = new DialectModule(Dialect.Snake, ["FOO"], owner: "skia");
		var eratw = new DialectModule(Dialect.Eratw, ["FOO"], owner: "eratw");

		var error = Assert.Throws<CodeEE>(() => Profile(skia, eratw));
		Assert.Contains("FOO", error.Message);
		Assert.Contains("priority/override", error.Message);
	}

	[Fact]
	public void Higher_priority_wins_deterministically_and_is_order_independent()
	{
		var skia = new DialectModule(Dialect.Snake, ["FOO"], owner: "skia", priority: 0);
		var eratw = new DialectModule(Dialect.Eratw, ["FOO"], owner: "eratw", priority: 5);

		Assert.Equal("eratw", Profile(skia, eratw).KeyOwners["FOO"]);
		Assert.Equal("eratw", Profile(eratw, skia).KeyOwners["FOO"]);
	}

	[Fact]
	public void Equal_priority_between_owners_is_still_a_conflict()
	{
		var skia = new DialectModule(Dialect.Snake, ["FOO"], owner: "skia", priority: 3);
		var eratw = new DialectModule(Dialect.Eratw, ["FOO"], owner: "eratw", priority: 3);

		Assert.Throws<CodeEE>(() => Profile(skia, eratw));
	}

	[Fact]
	public void Explicit_override_wins_over_higher_priority()
	{
		var skia = new DialectModule(Dialect.Snake, ["FOO"], owner: "skia", priority: 0, overrides: ["eratw"]);
		// eratw 数值更高，但 skia 显式覆盖 eratw → override 优先。
		var eratw = new DialectModule(Dialect.Eratw, ["FOO"], owner: "eratw", priority: 100);

		Assert.Equal("skia", Profile(skia, eratw).KeyOwners["FOO"]);
	}

	[Fact]
	public void Ambiguous_mutual_override_throws()
	{
		var skia = new DialectModule(Dialect.Snake, ["FOO"], owner: "skia", overrides: ["eratw"]);
		var eratw = new DialectModule(Dialect.Eratw, ["FOO"], owner: "eratw", overrides: ["skia"]);

		var error = Assert.Throws<CodeEE>(() => Profile(skia, eratw));
		Assert.Contains("歧义", error.Message);
	}

	[Fact]
	public void Same_owner_across_modules_is_not_a_conflict()
	{
		var a = new DialectModule(Dialect.Snake, ["FOO"], owner: "skia");
		var b = new DialectModule(Dialect.Eratw, ["FOO"], owner: "skia");

		DialectProfile profile = Profile(a, b);
		Assert.True(profile.IsVisible("FOO"));
		Assert.Equal("skia", profile.KeyOwners["FOO"]);
	}

	[Fact]
	public void Hidden_key_skips_conflict_resolution()
	{
		// 同键两个 owner，但 eratw 显式 Hide → 该键被剔除，不参与跨 fork 裁决。
		var skia = new DialectModule(Dialect.Snake, ["FOO"], owner: "skia");
		var eratw = new DialectModule(Dialect.Eratw, ["FOO"], hiddenKeys: ["FOO"], owner: "eratw");

		DialectProfile profile = Profile(skia, eratw);
		Assert.False(profile.IsVisible("FOO"));
		Assert.False(profile.KeyOwners.ContainsKey("FOO"));
	}

	[Fact]
	public void Single_owner_key_reports_its_owner()
	{
		var skia = new DialectModule(Dialect.Snake, ["FOO", "BAR"], owner: "skia");

		DialectProfile profile = Profile(skia);
		Assert.Equal("skia", profile.KeyOwners["FOO"]);
		Assert.Equal("skia", profile.KeyOwners["BAR"]);
	}

	[Fact]
	public void Module_owner_defaults_to_its_dialect_name()
	{
		var module = new DialectModule(Dialect.Snake, ["FOO"]);

		Assert.Equal("Snake", module.Owner);
	}

	[Fact]
	public void Real_profiles_construct_without_cross_fork_conflict()
	{
		// FunctionIdentifier 静态构造读 ambient Config，必须先开 scope（避免污染同进程其它测试）。
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		foreach (string profileId in new[] { DialectRegistry.V24PureId, DialectRegistry.SnakeId, DialectRegistry.EratwId })
		{
			DialectProfile profile = DialectRegistry.Resolve(profileId);
			Assert.NotEmpty(profile.KeyOwners);
		}
	}
}