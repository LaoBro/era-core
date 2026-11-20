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
/// 票 40（Tier 3b，Skia 对齐）：多边形 4 键 + SPRITECREATEFROMFILE，均为 no-op。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0GraphicsPolygonFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	[Theory]
	[InlineData("G_POLYGON_POINT_ADD")]
	[InlineData("G_POLYGON_POINT_CLEAR")]
	[InlineData("G_POLYGON_DRAW")]
	[InlineData("G_POLYGON_FILL")]
	[InlineData("SPRITECREATEFROMFILE")]
	public void Graphics_noop_functions_are_snake_only(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method(name);
		Assert.Equal(typeof(long), method.ReturnType);
		Assert.False(method.CanRestructure);
		Assert.Contains(name, DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain(name, DialectRegistry.Baseline.DeclaredKeys);
	}

	// ---------- 多边形：已创建 1 / 未创建 0 ----------

	[Theory]
	[InlineData("G_POLYGON_POINT_CLEAR")]
	[InlineData("G_POLYGON_DRAW")]
	[InlineData("G_POLYGON_FILL")]
	public async Task Polygon_operations_return_one_only_for_created_graphics(string name)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Method("GCREATE").GetIntValue(h.Exm, Args(new SingleLongTerm(0), new SingleLongTerm(8), new SingleLongTerm(8)));

		Assert.Equal(1, Method(name).GetIntValue(h.Exm, Args(new SingleLongTerm(0))));
		Assert.Equal(0, Method(name).GetIntValue(h.Exm, Args(new SingleLongTerm(9999))));
	}

	[Fact]
	public async Task Polygon_point_add_validates_id_and_point_range()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Method("GCREATE").GetIntValue(h.Exm, Args(new SingleLongTerm(1), new SingleLongTerm(8), new SingleLongTerm(8)));

		Assert.Equal(1, Method("G_POLYGON_POINT_ADD").GetIntValue(
			h.Exm, Args(new SingleLongTerm(1), new SingleLongTerm(2), new SingleLongTerm(3))));
		Assert.Equal(0, Method("G_POLYGON_POINT_ADD").GetIntValue(
			h.Exm, Args(new SingleLongTerm(9999), new SingleLongTerm(2), new SingleLongTerm(3))));

		Assert.Throws<CodeEE>(() => Method("G_POLYGON_POINT_ADD").GetIntValue(
			h.Exm, Args(new SingleLongTerm(1), new SingleLongTerm((long)int.MaxValue + 1), new SingleLongTerm(0))));
	}

	[Fact]
	public async Task Polygon_rejects_negative_graphics_id()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		var error = Assert.Throws<CodeEE>(() =>
			Method("G_POLYGON_DRAW").GetIntValue(h.Exm, Args(new SingleLongTerm(-1))));
		Assert.Contains("G_POLYGON_DRAW", error.Message);
	}

	// ---------- SPRITECREATEFROMFILE ----------

	[Theory]
	[InlineData("")]
	[InlineData("nonexistent.png")]
	public async Task Sprite_create_from_file_always_returns_zero(string file)
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Equal(0, Method("SPRITECREATEFROMFILE").GetIntValue(
			h.Exm, Args(new SingleStrTerm("spr"), new SingleStrTerm(file))));
		Assert.Equal(0, Method("SPRITECREATEFROMFILE").GetIntValue(
			h.Exm, Args(new SingleStrTerm("spr"), new SingleStrTerm(file), new SingleLongTerm(1))));
	}

	[Fact]
	public void Sprite_create_from_file_accepts_two_or_three_arguments()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		Assert.NotNull(Method("SPRITECREATEFROMFILE").CheckArgumentType(
			"SPRITECREATEFROMFILE", Args(new SingleStrTerm("a"))));
		Assert.Null(Method("SPRITECREATEFROMFILE").CheckArgumentType(
			"SPRITECREATEFROMFILE", Args(new SingleStrTerm("a"), new SingleStrTerm("b"))));
		Assert.Null(Method("SPRITECREATEFROMFILE").CheckArgumentType(
			"SPRITECREATEFROMFILE", Args(new SingleStrTerm("a"), new SingleStrTerm("b"), new SingleLongTerm(0))));
	}
}
