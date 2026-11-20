using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 39（Tier 3a，Skia 对齐）：音频查询 / 控制 5 键，确定性 no-op。
/// era-core 音频为空实现，故只验证返回码矩阵与 RESULT 写入。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0AudioControlFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	[Theory]
	[InlineData("ISPLAYINGSOUND")]
	[InlineData("ISPLAYINGBGM")]
	[InlineData("GETSOUNDORBGMINFO")]
	[InlineData("SOUNDCONTROL")]
	[InlineData("BGMCONTROL")]
	public void Audio_functions_are_snake_only_and_not_restructured(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method(name);
		Assert.Equal(typeof(long), method.ReturnType);
		Assert.False(method.CanRestructure);
		Assert.Contains(name, DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain(name, DialectRegistry.Baseline.DeclaredKeys);
	}

	[Theory]
	[InlineData(0)]
	[InlineData(9)]
	[InlineData(-1)]
	[InlineData(99)]
	public async Task Is_playing_sound_always_returns_minus_one(long channel)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(-1, Method("ISPLAYINGSOUND").GetIntValue(h.Exm, Args(new SingleLongTerm(channel))));
	}

	[Fact]
	public async Task Is_playing_bgm_always_returns_zero()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(0, Method("ISPLAYINGBGM").GetIntValue(h.Exm, Args()));
	}

	[Fact]
	public async Task Get_sound_or_bgm_info_writes_zeroed_result_when_info_omitted()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		long[] result = h.Exm.VEvaluator.RESULT_ARRAY;
		for (int i = 0; i < 5; i++)
			result[i] = 7 + i;

		Assert.Equal(0, Method("GETSOUNDORBGMINFO").GetIntValue(h.Exm, Args(new SingleLongTerm(0))));
		Assert.All(result.Take(5), value => Assert.Equal(0, value));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(5)]
	[InlineData(9)]
	public async Task Get_sound_or_bgm_info_returns_zero_for_any_info_type(long info)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(0, Method("GETSOUNDORBGMINFO").GetIntValue(
			h.Exm, Args(new SingleLongTerm(0), new SingleLongTerm(info))));
	}

	// ---------- SOUNDCONTROL ----------

	[Fact]
	public async Task Sound_control_rejects_invalid_channel()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(-1, Method("SOUNDCONTROL").GetIntValue(
			h.Exm, Args(new SingleLongTerm(-1), new SingleLongTerm(0))));
		Assert.Equal(-1, Method("SOUNDCONTROL").GetIntValue(
			h.Exm, Args(new SingleLongTerm(10), new SingleLongTerm(0))));
	}

	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 1)]
	[InlineData(2, 1)]
	[InlineData(3, -2)]
	[InlineData(9, -2)]
	public async Task Sound_control_two_argument_form_returns_action_codes(long action, long expected)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(expected, Method("SOUNDCONTROL").GetIntValue(
			h.Exm, Args(new SingleLongTerm(0), new SingleLongTerm(action))));
	}

	[Theory]
	[InlineData(3, 1)]
	[InlineData(0, -2)]
	[InlineData(2, -2)]
	public async Task Sound_control_four_argument_form_only_accepts_speed_action(long action, long expected)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(expected, Method("SOUNDCONTROL").GetIntValue(
			h.Exm, Args(
				new SingleLongTerm(0), new SingleLongTerm(action),
				new SingleLongTerm(150), new SingleLongTerm(1))));
	}

	// ---------- BGMCONTROL ----------

	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 1)]
	[InlineData(2, 1)]
	[InlineData(3, -2)]
	[InlineData(9, -2)]
	public async Task Bgm_control_one_argument_form_returns_action_codes(long action, long expected)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(expected, Method("BGMCONTROL").GetIntValue(h.Exm, Args(new SingleLongTerm(action))));
	}

	[Theory]
	[InlineData(3, 1)]
	[InlineData(0, -2)]
	public async Task Bgm_control_three_argument_form_only_accepts_speed_action(long action, long expected)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(expected, Method("BGMCONTROL").GetIntValue(
			h.Exm, Args(new SingleLongTerm(action), new SingleLongTerm(80), new SingleLongTerm(0))));
	}

	// ---------- 参数个数门禁 ----------

	[Fact]
	public void Argument_counts_match_skia_signatures()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		Assert.NotNull(Method("SOUNDCONTROL").CheckArgumentType("SOUNDCONTROL", Args(new SingleLongTerm(0))));
		Assert.NotNull(Method("SOUNDCONTROL").CheckArgumentType("SOUNDCONTROL",
			Args(new SingleLongTerm(0), new SingleLongTerm(0), new SingleLongTerm(0), new SingleLongTerm(0), new SingleLongTerm(0))));
		Assert.Null(Method("SOUNDCONTROL").CheckArgumentType("SOUNDCONTROL",
			Args(new SingleLongTerm(0), new SingleLongTerm(0))));
		Assert.Null(Method("SOUNDCONTROL").CheckArgumentType("SOUNDCONTROL",
			Args(new SingleLongTerm(0), new SingleLongTerm(0), new SingleLongTerm(0))));

		Assert.NotNull(Method("BGMCONTROL").CheckArgumentType("BGMCONTROL", Args()));
		Assert.Null(Method("BGMCONTROL").CheckArgumentType("BGMCONTROL", Args(new SingleLongTerm(0))));
		Assert.Null(Method("BGMCONTROL").CheckArgumentType("BGMCONTROL",
			Args(new SingleLongTerm(0), new SingleLongTerm(0), new SingleLongTerm(0))));
	}
}
