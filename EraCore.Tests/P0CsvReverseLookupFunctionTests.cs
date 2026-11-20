using System;
using System.Collections.Generic;
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
/// 票 37（Tier 2a，Skia 对齐）：GETCSVNOBY* 四键 + EXIST_IN_CSV（CSV 来源追踪）。
/// EXIST_IN_ERD 随「ERD 预置名合并」另开票，本票不登记。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0CsvReverseLookupFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	[Theory]
	[InlineData("GETCSVNOBYNAME")]
	[InlineData("GETCSVNOBYNICKNAME")]
	[InlineData("GETCSVNOBYCALLNAME")]
	[InlineData("GETCSVNOBYMASTERNAME")]
	[InlineData("EXIST_IN_CSV")]
	public void Reverse_lookup_functions_are_pure_queries(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method(name);
		Assert.Equal(typeof(long), method.ReturnType);
		Assert.True(method.CanRestructure);
	}

	[Theory]
	[InlineData("GETCSVNOBYNAME")]
	[InlineData("GETCSVNOBYNICKNAME")]
	[InlineData("GETCSVNOBYCALLNAME")]
	[InlineData("GETCSVNOBYMASTERNAME")]
	[InlineData("EXIST_IN_CSV")]
	public void Reverse_lookup_functions_are_snake_only(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		Assert.Contains(name, DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain(name, DialectRegistry.Baseline.DeclaredKeys);
	}

	// ---------- GETCSVNOBY* ----------

	private static void WriteChara(LoaderTestHarness h)
	{
		h.WriteCsv("CHARA001.CSV", "NO,11\nNAME,Alice\nCALLNAME,Alicia\nNICKNAME,Al\nMASTERNAME,Master\n");
		h.WriteCsv("CHARA002.CSV", "NO,22\nNAME,Bob\nCALLNAME,Bobby\nNICKNAME,Rob\nMASTERNAME,Lord\n");
	}

	[Theory]
	[InlineData("GETCSVNOBYNAME", "Alice", 11)]
	[InlineData("GETCSVNOBYNAME", "Bob", 22)]
	[InlineData("GETCSVNOBYNICKNAME", "Al", 11)]
	[InlineData("GETCSVNOBYCALLNAME", "Bobby", 22)]
	[InlineData("GETCSVNOBYMASTERNAME", "Lord", 22)]
	[InlineData("GETCSVNOBYNAME", "Nobody", -1)]
	[InlineData("GETCSVNOBYNICKNAME", "", -1)]
	public async Task Character_name_reverse_lookup_returns_no_or_minus_one(string function, string target, long expected)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		WriteChara(h);
		await h.InitializeAsync();

		Assert.Equal(expected, Method(function).GetIntValue(h.Exm, Args(new SingleStrTerm(target))));
	}

	[Fact]
	public async Task Duplicate_names_resolve_to_the_smallest_no()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteCsv("CHARA001.CSV", "NO,5\nNAME,Same\n");
		h.WriteCsv("CHARA002.CSV", "NO,3\nNAME,Same\n");
		await h.InitializeAsync();

		Assert.Equal(3, Method("GETCSVNOBYNAME").GetIntValue(h.Exm, Args(new SingleStrTerm("Same"))));
	}

	// ---------- EXIST_IN_CSV ----------

	[Fact]
	public async Task Exist_in_csv_reports_csv_sourced_preset_slots()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteCsv("ABL.CSV", "0,体力\n1,気力\n");
		h.WriteCsv("PALAM.CSV", "0,体力\n");
		await h.InitializeAsync();

		static List<AExpression> Probe(string name, long index) => [new SingleStrTerm(name), new SingleLongTerm(index)];

		// CSV 提供 → 1（含同一槽的别名）
		Assert.Equal(1, Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe("ABL", 0)));
		Assert.Equal(1, Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe("ABL", 1)));
		Assert.Equal(1, Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe("UP", 0)));
		// 空槽 / 越界 / 未知变量名 / 负索引 → 0
		Assert.Equal(0, Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe("ABL", 2)));
		Assert.Equal(0, Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe("ABL", 9999)));
		Assert.Equal(0, Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe("UNKNOWN", 0)));
		Assert.Equal(0, Method("EXIST_IN_CSV").GetIntValue(h.Exm, Probe("ABL", -1)));
	}
}
