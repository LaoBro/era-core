using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 44（Skia 16.1.0 / 8.0.0）：用户变量 `.als` 别名按变量域隔离。
/// 别名必须写进该变量自己的逆引字典，正式名优先，越界索引 Lv1 警告后跳过。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0UserDefinedAlsAliasTests
{
	private static string ScreenText(EmueraConsole console)
		=> string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

	private static Dictionary<string, int> VarNames(LoaderTestHarness h, string varname)
		=> h.Process.ConstantData.GetKeywordDictionary(out _, VariableCode.VAR, -1, varname);

	[Fact]
	public async Task User_defined_als_alias_resolves_to_canonical_slot()
	{
		using var h = new LoaderTestHarness();
		h.WriteErh("def.ERH", "#DIM BUFF, 10\n");
		h.WriteCsv("BUFF.CSV", "0,正式名\n1,攻击\n");
		h.WriteCsv("BUFF.als", "0,旧译\n1,攻撃\n");
		await h.InitializeAsync();

		Dictionary<string, int> dic = VarNames(h, "BUFF");
		Assert.Equal(0, dic["正式名"]);
		Assert.Equal(0, dic["旧译"]);
		Assert.Equal(1, dic["攻撃"]);
		Assert.True(h.Process.ConstantData.isUserDefined("BUFF", "旧译", 1));
	}

	[Fact]
	public async Task Canonical_name_wins_over_alias()
	{
		using var h = new LoaderTestHarness();
		h.WriteErh("def.ERH", "#DIM BUFF, 10\n");
		h.WriteCsv("BUFF.CSV", "0,正式名\n");
		h.WriteCsv("BUFF.als", "7,正式名\n");
		await h.InitializeAsync();

		Assert.Equal(0, VarNames(h, "BUFF")["正式名"]);
	}

	[Fact]
	public async Task Same_alias_on_two_variables_stays_isolated()
	{
		using var h = new LoaderTestHarness();
		h.WriteErh("def.ERH", "#DIM BUFF, 10\n#DIM DISH, 10\n");
		h.WriteCsv("BUFF.CSV", "0,攻击\n");
		h.WriteCsv("BUFF.als", "0,攻击别名\n");
		h.WriteCsv("Dish.CSV", "0,料理\n");
		h.WriteCsv("Dish.als", "0,料理别名\n");
		await h.InitializeAsync();

		Dictionary<string, int> buff = VarNames(h, "BUFF");
		Dictionary<string, int> dish = VarNames(h, "DISH");
		Assert.Equal(0, buff["攻击别名"]);
		Assert.Equal(0, dish["料理别名"]);
		// 互不污染，也不产生「别名已被定义」类假警告。
		Assert.False(buff.ContainsKey("料理别名"));
		Assert.False(dish.ContainsKey("攻击别名"));
		Assert.DoesNotContain("料理别名", ScreenText(h.Console));
		Assert.DoesNotContain("攻击别名", ScreenText(h.Console));
	}

	[Fact]
	public async Task Variable_without_als_is_unchanged()
	{
		using var h = new LoaderTestHarness();
		h.WriteErh("def.ERH", "#DIM PLAIN, 5\n");
		h.WriteCsv("PLAIN.CSV", "0,名字\n");
		await h.InitializeAsync();

		Assert.Equal(0, VarNames(h, "PLAIN")["名字"]);
		Assert.True(h.Process.ConstantData.isUserDefined("PLAIN", "名字", 1));
	}

	[Fact]
	public async Task Out_of_range_alias_warns_and_is_skipped()
	{
		using var h = new LoaderTestHarness();
		h.WriteErh("def.ERH", "#DIM BUFF, 3\n");
		h.WriteCsv("BUFF.CSV", "0,正式名\n");
		h.WriteCsv("BUFF.als", "9,越界别名\n");
		await h.InitializeAsync();

		Assert.False(VarNames(h, "BUFF").ContainsKey("越界别名"));
		Assert.Contains("9は配列の範囲外です", ScreenText(h.Console));
	}

	[Fact]
	public async Task Multi_dimension_aliases_are_loaded_per_dimension()
	{
		using var h = new LoaderTestHarness();
		h.WriteErh("def.ERH", "#DIM MAT, 3, 3\n");
		h.WriteCsv("MAT@1.CSV", "0,行甲\n");
		h.WriteCsv("MAT@1.als", "0,行一\n");
		h.WriteCsv("MAT@2.CSV", "0,列甲\n");
		h.WriteCsv("MAT@2.als", "0,列一\n");
		await h.InitializeAsync();

		Assert.Equal(0, VarNames(h, "MAT@1")["行一"]);
		Assert.Equal(0, VarNames(h, "MAT@2")["列一"]);
	}
}
