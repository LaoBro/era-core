using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 43（Skia 对齐）：ERD 预置名合并子系统 + `EXIST_IN_ERD`。
/// 合并发生在全部 CSV 之后、逆引字典之前；CSV 优先，ERD 只补空槽；ITEM 第三列为价格。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0ErdPresetNameMergeTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Probe(string name, long index) => [new SingleStrTerm(name), new SingleLongTerm(index)];

	private static long ExistInErd(LoaderTestHarness h, string name, long index)
		=> Method("EXIST_IN_ERD").GetIntValue(h.Exm, Probe(name, index));

	private static long ExistInCsv(LoaderTestHarness h, string name, long index)
		=> Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe(name, index));

	private static string ScreenText(EmueraConsole console)
		=> string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

	[Fact]
	public void Exist_in_erd_is_snake_only_and_restructured()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method("EXIST_IN_ERD");
		Assert.Equal(typeof(long), method.ReturnType);
		Assert.True(method.CanRestructure);
		Assert.Contains("EXIST_IN_ERD", DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain("EXIST_IN_ERD", DialectRegistry.Baseline.DeclaredKeys);
	}

	[Fact]
	public async Task Erd_fills_empty_slot_and_marks_erd_source()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErd("ABL.erd", "0,ERD_体力\n");
		await h.InitializeAsync();

		Assert.Equal("ERD_体力", h.Process.ConstantData.GetCsvNameList(VariableCode.ABLNAME)[0]);
		Assert.Equal(1, ExistInErd(h, "ABL", 0));
		Assert.Equal(0, ExistInCsv(h, "ABL", 0));
	}

	[Fact]
	public async Task Csv_wins_over_erd_for_names()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteCsv("ABL.CSV", "0,CSV_体力\n");
		h.WriteErd("ABL.erd", "0,ERD_体力\n");
		await h.InitializeAsync();

		Assert.Equal("CSV_体力", h.Process.ConstantData.GetCsvNameList(VariableCode.ABLNAME)[0]);
		Assert.Equal(1, ExistInCsv(h, "ABL", 0));
		Assert.Equal(0, ExistInErd(h, "ABL", 0));
		// CSV 与 ERD 名称不一致 → 告警可见（默认日文文案）。
		Assert.Contains("CSVを優先します", ScreenText(h.Console));
	}

	[Fact]
	public async Task Without_erd_exists_in_erd_is_zero()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteCsv("ABL.CSV", "0,体力\n");
		await h.InitializeAsync();

		Assert.Equal(0, ExistInErd(h, "ABL", 0));
		// 未知变量名 / 负索引 / 越界同样返回 0（探针语义）。
		Assert.Equal(0, ExistInErd(h, "UNKNOWN", 0));
		Assert.Equal(0, ExistInErd(h, "ABL", -1));
		Assert.Equal(0, ExistInErd(h, "ABL", 9999));
	}

	[Fact]
	public async Task Erd_alias_resolves_to_canonical_slot()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErd("PALAM.erd", "0,ERD_能力\n");
		await h.InitializeAsync();

		// UP 是 PALAM 槽的别名；来源查询透明，返回槽正名的来源。
		Assert.Equal(1, ExistInErd(h, "UP", 0));
		Assert.Equal("ERD_能力", h.Process.ConstantData.GetCsvNameList(VariableCode.PALAMNAME)[0]);
	}

	[Fact]
	public async Task Erd_price_fills_unset_item_price()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteCsv("ITEM.CSV", "0,CSV道具\n");
		h.WriteErd("ITEM.erd", "0,ERD道具,500\n");
		await h.InitializeAsync();

		// 名字 CSV 优先（未冲突则静默），价格 CSV 未设 → ERD 填入。
		Assert.Equal("CSV道具", h.Process.ConstantData.GetCsvNameList(VariableCode.ITEMNAME)[0]);
		Assert.Equal(0, ExistInErd(h, "ITEM", 0));
		Assert.Equal(500, h.Process.ConstantData.ItemPrice[0]);
	}

	[Fact]
	public async Task Csv_price_wins_over_erd_and_warns()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteCsv("ITEM.CSV", "0,道具,300\n");
		h.WriteErd("ITEM.erd", "0,道具,500\n");
		await h.InitializeAsync();

		Assert.Equal(300, h.Process.ConstantData.ItemPrice[0]);
		// CSV 已设且不一致 → 价格冲突告警可见。
		Assert.Contains("CSVを優先します", ScreenText(h.Console));
	}

	[Fact]
	public async Task Cross_slot_duplicate_name_is_skipped()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		// 路径排序：ABL.erd 先于 EXP.erd ⇒ ABL 先占名，EXP 的同名条目跳过。
		h.WriteErd("ABL.erd", "0,同名\n");
		h.WriteErd("EXP.erd", "0,同名\n");
		await h.InitializeAsync();

		Assert.Equal(1, ExistInErd(h, "ABL", 0));
		Assert.Equal(0, ExistInErd(h, "EXP", 0));
		Assert.Equal("同名", h.Process.ConstantData.GetCsvNameList(VariableCode.ABLNAME)[0]);
		Assert.True(string.IsNullOrEmpty(h.Process.ConstantData.GetCsvNameList(VariableCode.EXPNAME)[0]));
		Assert.Contains("スキップします", ScreenText(h.Console));
	}
}
