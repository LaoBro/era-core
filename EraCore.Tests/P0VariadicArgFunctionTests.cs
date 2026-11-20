using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 41（Tier 4a，Skia 对齐）：VARIADIC 形参协议 + ARGLEN。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0VariadicArgFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static async Task<LoaderTestHarness> LoadAsync(string erb)
	{
		var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		h.WriteErb("variadic.ERB", erb);
		await h.PreloadAsync();
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		bool ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		return h;
	}

	[Fact]
	public void ArgLen_is_snake_only_and_takes_no_arguments()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method("ARGLEN");
		Assert.Equal(typeof(long), method.ReturnType);
		Assert.False(method.CanRestructure);
		Assert.Contains("ARGLEN", DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain("ARGLEN", DialectRegistry.Baseline.DeclaredKeys);
		Assert.Null(method.CheckArgumentType("ARGLEN", Args()));
		Assert.NotNull(method.CheckArgumentType("ARGLEN", Args(new SingleLongTerm(0))));
	}

	// ---------- 声明 ----------

	[Fact]
	public async Task Variadic_on_last_arg_is_accepted_for_arg_and_args()
	{
		using var h = await LoadAsync("""
@F(ARG:0, VARIADIC ARGS:1)
	RETURN

@G(VARIADIC ARG:0)
	RETURN
""");

		var f = h.LabelDic.GetNonEventLabel("F");
		Assert.False(f.IsError);
		Assert.Equal(1, f.VariadicArgIndex);
		Assert.Equal(2, f.Arg.Length);

		var g = h.LabelDic.GetNonEventLabel("G");
		Assert.False(g.IsError);
		Assert.Equal(0, g.VariadicArgIndex);
	}

	[Fact]
	public async Task Variadic_on_argf_is_accepted_for_float_functions()
	{
		using var h = await LoadAsync("""
@FM(VARIADIC ARGF:0)
#FUNCTIONF
	RETURNF ARGF:0
""");

		var fm = h.LabelDic.GetNonEventLabel("FM");
		Assert.False(fm.IsError);
		Assert.True(fm.IsMethod);
		Assert.Equal(0, fm.VariadicArgIndex);
	}

	[Theory]
	[InlineData("VARIADIC ARG:0, ARG:1")]
	[InlineData("ARG:0, VARIADIC FLAG:0")]
	[InlineData("ARG:0, VARIADIC ARG:1")]
	public async Task Invalid_variadic_declarations_mark_label_as_error(string parameters)
	{
		using var h = await LoadAsync($"@BAD({parameters})\n\tRETURN\n");

		var bad = h.LabelDic.GetNonEventLabel("BAD");
		Assert.True(bad.IsError);
	}

	[Fact]
	public async Task Function_without_variadic_keeps_minus_one()
	{
		using var h = await LoadAsync("""
@N(ARG:0)
	RETURN
""");

		Assert.Equal(-1, h.LabelDic.GetNonEventLabel("N").VariadicArgIndex);
	}

	// ---------- 调用协议 ----------

	[Fact]
	public async Task Call_packs_extra_arguments_and_reports_arglen()
	{
		using var h = await LoadAsync("""
@F(ARG:0, VARIADIC ARGS:1)
	RETURN
""");

		var func = h.LabelDic.GetNonEventLabel("F");
		var call = CalledFunction.CreateCalledFunctionMethod(func, "F");
		var converted = call.ConvertArg(
			[new SingleLongTerm(1), new SingleStrTerm("a"), new SingleStrTerm("b")], out var errMes);
		Assert.True(converted != null, errMes);

		h.Process.getCurrentState.IntoFunction(call, converted, h.Exm);

		Assert.Equal(2, h.Process.getCurrentState.CurrentVariadicArgCount);
		Assert.Equal(2, Method("ARGLEN").GetIntValue(h.Exm, Args()));
		Assert.Equal(1, func.Arg[0].Identifier.GetIntValue(h.Exm, [0]));
		Assert.Equal("a", func.Arg[1].Identifier.GetStrValue(h.Exm, [1]));
		Assert.Equal("b", func.Arg[1].Identifier.GetStrValue(h.Exm, [2]));
	}

	[Fact]
	public async Task Call_with_no_extra_arguments_reports_zero()
	{
		using var h = await LoadAsync("""
@F(ARG:0, VARIADIC ARGS:1)
	RETURN

@N(ARG:0)
	RETURN
""");

		var func = h.LabelDic.GetNonEventLabel("F");
		var call = CalledFunction.CreateCalledFunctionMethod(func, "F");
		var converted = call.ConvertArg([new SingleLongTerm(7)], out var errMes);
		Assert.True(converted != null, errMes);
		h.Process.getCurrentState.IntoFunction(call, converted, h.Exm);
		Assert.Equal(0, Method("ARGLEN").GetIntValue(h.Exm, Args()));

		// 非变参函数恒 0。
		var n = h.LabelDic.GetNonEventLabel("N");
		var callN = CalledFunction.CreateCalledFunctionMethod(n, "N");
		var convertedN = callN.ConvertArg([new SingleLongTerm(1)], out errMes);
		Assert.True(convertedN != null, errMes);
		h.Process.getCurrentState.IntoFunction(callN, convertedN, h.Exm);
		Assert.Equal(0, Method("ARGLEN").GetIntValue(h.Exm, Args()));
	}

	[Fact]
	public async Task Float_variadic_widens_integer_arguments()
	{
		using var h = await LoadAsync("""
@FM(VARIADIC ARGF:0)
#FUNCTIONF
	RETURNF ARGF:0
""");

		var fm = h.LabelDic.GetNonEventLabel("FM");
		var call = CalledFunction.CreateCalledFunctionMethod(fm, "FM");
		var converted = call.ConvertArg([new SingleLongTerm(2), new SingleFloatTerm(0.5)], out var errMes);
		Assert.True(converted != null, errMes);

		h.Process.getCurrentState.IntoFunction(call, converted, h.Exm);

		Assert.Equal(2, h.Process.getCurrentState.CurrentVariadicArgCount);
		Assert.Equal(2.0, fm.Arg[0].Identifier.GetFloatValue(h.Exm, [0]), 10);
		Assert.Equal(0.5, fm.Arg[0].Identifier.GetFloatValue(h.Exm, [1]), 10);
	}
}
