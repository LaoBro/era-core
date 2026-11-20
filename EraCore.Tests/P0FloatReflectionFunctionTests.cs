using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 36（Tier 1，Skia 对齐）：EVALF / GETVARF / GETMETHF / DT_CELL_GETF。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0FloatReflectionFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	// ---------- 反射签名 ----------

	[Theory]
	[InlineData("EVALF")]
	[InlineData("GETVARF")]
	[InlineData("GETMETHF")]
	[InlineData("DT_CELL_GETF")]
	public void Float_reflection_functions_return_double_and_are_not_restructured(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method(name);
		Assert.Equal(typeof(double), method.ReturnType);
		Assert.False(method.CanRestructure);
	}

	[Theory]
	[InlineData("EVALF")]
	[InlineData("GETVARF")]
	[InlineData("GETMETHF")]
	[InlineData("DT_CELL_GETF")]
	public void Float_reflection_functions_are_snake_only(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		Assert.Contains(name, DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain(name, DialectRegistry.Baseline.DeclaredKeys);
	}

	// ---------- EVALF ----------

	[Fact]
	public void Evalf_evaluates_float_and_widens_integer_results()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), DialectRegistry.SnakeId);

		Assert.Equal(2.5, Method("EVALF").GetFloatValue(null!, Args(new SingleStrTerm("1.5+1"))), 10);
		Assert.Equal(3.0, Method("EVALF").GetFloatValue(null!, Args(new SingleStrTerm("1+2"))), 10);
		Assert.Equal(4.0, Method("EVALF").GetFloatValue(null!, Args(new SingleStrTerm("2*2"))), 10);
	}

	[Fact]
	public void Evalf_falls_back_on_empty_invalid_or_non_numeric_expression()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		Assert.Equal(0.0, Method("EVALF").GetFloatValue(null!, Args(new SingleStrTerm(""))), 10);
		Assert.Equal(0.0, Method("EVALF").GetFloatValue(null!, Args(new SingleStrTerm("("))), 10);
		Assert.Equal(0.0, Method("EVALF").GetFloatValue(null!, Args(new SingleStrTerm("\"abc\""))), 10);
	}

	// ---------- GETVARF ----------

	private const string VariableErh = "#DIMF FARR, 2\n";

	[Fact]
	public async Task Getvarf_reads_float_variable_by_name()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("float.ERH", VariableErh);
		await h.InitializeAsync();

		VariableToken token = h.IdDic.GetVariableToken("FARR", null, false)!;
		token.SetValue(2.5, [1L]);

		Assert.Equal(2.5, Method("GETVARF").GetFloatValue(h.Exm, Args(new SingleStrTerm("FARR:1"))), 10);
	}

	[Fact]
	public async Task Getvarf_rejects_non_float_or_non_variable_unless_default_given()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		// 非浮点变量：无缺省时抛 IsNotFloat，有缺省时回缺省。
		Assert.Throws<CodeEE>(() => Method("GETVARF").GetFloatValue(h.Exm, Args(new SingleStrTerm("FLAG:0"))));
		Assert.Equal(9.5, Method("GETVARF").GetFloatValue(h.Exm, Args(new SingleStrTerm("FLAG:0"), new SingleFloatTerm(9.5))), 10);

		// 非变量：无缺省时抛 IsNotVar。
		Assert.Throws<CodeEE>(() => Method("GETVARF").GetFloatValue(h.Exm, Args(new SingleStrTerm("1+1"))));
	}

	// ---------- GETMETHF ----------

	private static async Task LoadErbAsync(LoaderTestHarness h, string content)
	{
		h.WriteErb("func.ERB", content);
		await h.PreloadAsync();
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		bool ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
	}

	private const string FuncErb = """
@FLT(ARGF:0, ARGF:1 = 2.5)
#FUNCTIONF
	RETURNF ARGF:0 + ARGF:1

@INTF(ARG:0)
#FUNCTION
	RETURNF ARG:0
""";

	[Fact]
	public async Task Getmethf_calls_float_user_function()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		await LoadErbAsync(h, FuncErb);

		Assert.Equal(3.75, Method("GETMETHF").GetFloatValue(h.Exm, Args(new SingleStrTerm("FLT"), null!, new SingleFloatTerm(1.25))), 10);
	}

	[Fact]
	public async Task Getmethf_rejects_non_float_return_and_falls_back_on_undefined()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		await LoadErbAsync(h, FuncErb);

		// 返回类型非 Float → IsNotFloat。
		Assert.Throws<CodeEE>(() => Method("GETMETHF").GetFloatValue(h.Exm, Args(new SingleStrTerm("INTF"), null!, new SingleLongTerm(3))));

		// 未定义：有缺省 → 缺省；无缺省 → NotDefinedUserFunc。
		Assert.Equal(1.5, Method("GETMETHF").GetFloatValue(h.Exm, Args(new SingleStrTerm("NOPE"), new SingleFloatTerm(1.5))), 10);
		Assert.Throws<CodeEE>(() => Method("GETMETHF").GetFloatValue(h.Exm, Args(new SingleStrTerm("NOPE"))));
	}

	// ---------- DT_CELL_GETF ----------

	[Fact]
	public async Task Dt_cell_getf_reads_real_integer_and_null_cells()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		var dt = new DataTable("T");
		var id = dt.Columns.Add("id", typeof(long));
		dt.Columns.Add("val", typeof(double));
		dt.Columns.Add("n", typeof(long));
		dt.PrimaryKey = [id];
		dt.Rows.Add(1L, 1.25, 7L);
		dt.Rows.Add(2L, DBNull.Value, 0L);
		h.VEvaluator.VariableData.DataDataTables["T"] = dt;

		static List<AExpression> Cell(string table, long row, string col) =>
			[new SingleStrTerm(table), new SingleLongTerm(row), new SingleStrTerm(col)];

		Assert.Equal(1.25, Method("DT_CELL_GETF").GetFloatValue(h.Exm, Cell("T", 0, "val")), 10);
		Assert.Equal(7.0, Method("DT_CELL_GETF").GetFloatValue(h.Exm, Cell("T", 0, "n")), 10);
		Assert.Equal(0.0, Method("DT_CELL_GETF").GetFloatValue(h.Exm, Cell("T", 1, "val")), 10);
		Assert.Equal(0.0, Method("DT_CELL_GETF").GetFloatValue(h.Exm, Cell("T", 0, "missing")), 10);
		Assert.Equal(0.0, Method("DT_CELL_GETF").GetFloatValue(h.Exm, Cell("NOPE", 0, "val")), 10);
	}
}
