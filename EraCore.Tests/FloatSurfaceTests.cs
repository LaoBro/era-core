using System;
using System.Collections.Generic;
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
/// 票 27c：TOFLOAT/TOSTRF、#REFF、RESULTF、浮点数组内建。
/// </summary>
[Collection("LoaderTests")]
public sealed class FloatSurfaceTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static VariableToken Token(LoaderTestHarness h, string name)
	{
		VariableToken? token = h.IdDic.GetVariableToken(name, null, false);
		Assert.NotNull(token);
		return token!;
	}

	// ---------- TOFLOAT / TOSTRF ----------

	[Fact]
	public void ToFloat_parses_strings_and_returns_zero_on_invalid_input()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		LangManager.setEncode(936);
		Assert.Equal(1.5, Method("TOFLOAT").GetFloatValue(null!, Args(new SingleStrTerm("1.5"))), 10);
		Assert.Equal(-2.25, Method("TOFLOAT").GetFloatValue(null!, Args(new SingleStrTerm("-2.25"))), 10);
		Assert.Equal(0.0, Method("TOFLOAT").GetFloatValue(null!, Args(new SingleStrTerm(""))), 10);
		Assert.Equal(0.0, Method("TOFLOAT").GetFloatValue(null!, Args(new SingleStrTerm("abc"))), 10);
		// 全角文字混入 → TOINT 同口径返回 0
		Assert.Equal(0.0, Method("TOFLOAT").GetFloatValue(null!, Args(new SingleStrTerm("１.５"))), 10);
	}

	[Fact]
	public void ToStrf_formats_with_optional_format_string()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		Assert.Equal("1.5", Method("TOSTRF").GetStrValue(null!, Args(new SingleFloatTerm(1.5))));
		Assert.Equal("1.50", Method("TOSTRF").GetStrValue(null!, Args(new SingleFloatTerm(1.5), new SingleStrTerm("F2"))));
		Assert.Throws<CodeEE>(() => Method("TOSTRF").GetStrValue(null!, Args(new SingleFloatTerm(1.5), new SingleStrTerm("X"))));
	}

	// ---------- #REFF / RESULTF ----------

	[Fact]
	public async Task Reff_declares_float_scalar_reference_and_type_checks_float()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("float.ERH", "#REFF R1\n");
		await h.InitializeAsync();

		VariableToken reff = Token(h, "R1");
		Assert.True(reff.IsReference);
		Assert.True(reff.IsFloat);
		Assert.Equal(0, reff.Dimension);

		var refToken = Assert.IsAssignableFrom<ReferenceToken>(reff);
		Assert.True(refToken.MatchType(Token(h, "RESULTF"), false, out _));
		Assert.False(refToken.MatchType(Token(h, "SAVEDATA_TEXT"), false, out _));
	}

	[Fact]
	public async Task Resultf_is_a_float_scalar_and_reads_back_set_value()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		VariableToken token = Token(h, "RESULTF");
		Assert.True(token.IsFloat);
		Assert.Equal(0, token.Dimension);

		token.SetValue(1.5, []);
		Assert.Equal(1.5, token.GetFloatValue(h.Exm, []), 10);
		token.SetValue(2.0, []);
		Assert.Equal(2.0, h.VEvaluator.RESULTF, 10);
	}

	// ---------- 浮点数组内建 ----------

	private const string ArrayErh = "#DIMF ARR, 3\n";

	private static void SetArithmeticValues(VariableToken arr)
	{
		arr.SetValue(1.5, [0L]);
		arr.SetValue(-2.0, [1L]);
		arr.SetValue(0.5, [2L]);
	}

	[Fact]
	public async Task Float_array_builtins_sum_max_min_and_match()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("float.ERH", ArrayErh);
		await h.InitializeAsync();
		VariableToken arr = Token(h, "ARR");
		SetArithmeticValues(arr);
		var vTerm = new VariableTerm(arr, []);

		Assert.Null(Method("SUMARRAY").CheckArgumentType("SUMARRAY", Args(vTerm)));
		Assert.Equal(0.0, Method("SUMARRAY").GetFloatValue(h.Exm, Args(vTerm)), 10);
		Assert.Equal(1.5, Method("MAXARRAY").GetFloatValue(h.Exm, Args(vTerm)), 10);
		Assert.Equal(-2.0, Method("MINARRAY").GetFloatValue(h.Exm, Args(vTerm)), 10);
		Assert.Equal(1, Method("MATCH").GetIntValue(h.Exm, Args(vTerm, new SingleFloatTerm(0.5))));
		Assert.Equal(0, Method("MATCH").GetIntValue(h.Exm, Args(vTerm, new SingleFloatTerm(9.9))));
	}

	[Fact]
	public async Task Float_array_find_element_returns_index()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("float.ERH", ArrayErh);
		await h.InitializeAsync();
		VariableToken arr = Token(h, "ARR");
		SetArithmeticValues(arr);
		var vTerm = new VariableTerm(arr, []);

		Assert.Equal(1, Method("FINDELEMENT").GetIntValue(h.Exm, Args(vTerm, new SingleFloatTerm(-2.0))));
		Assert.Equal(2, Method("FINDLASTELEMENT").GetIntValue(h.Exm, Args(vTerm, new SingleFloatTerm(0.5))));
		Assert.Equal(-1, Method("FINDELEMENT").GetIntValue(h.Exm, Args(vTerm, new SingleFloatTerm(9.9))));
	}

	[Fact]
	public async Task Integer_array_paths_are_unchanged()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("int.ERH", "#DIM IARR, 3\n");
		await h.InitializeAsync();

		VariableToken arr = Token(h, "IARR");
		arr.SetValue(1L, [0L]);
		arr.SetValue(2L, [1L]);
		arr.SetValue(3L, [2L]);
		var vTerm = new VariableTerm(arr, []);

		Assert.Equal(6, Method("SUMARRAY").GetIntValue(h.Exm, Args(vTerm)));
		Assert.Equal(3, Method("MAXARRAY").GetIntValue(h.Exm, Args(vTerm)));
		Assert.Equal(1, Method("MATCH").GetIntValue(h.Exm, Args(vTerm, new SingleLongTerm(2))));
	}

	// ---------- #FUNCTIONF / ARGF / RETURNF（27d）----------

	private static async Task LoadErbAsync(LoaderTestHarness h, string content)
	{
		h.WriteErb("func.ERB", content);
		await h.PreloadAsync();
		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		bool ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
	}

	private const string FloatFuncErb = """
@FLT(ARGF:0, ARGF:1 = 2.5)
#FUNCTIONF
	RETURNF ARGF:0 + ARGF:1
""";

	[Fact]
	public async Task Functionf_declares_float_return_and_argf_signature()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		await LoadErbAsync(h, FloatFuncErb);

		var func = h.LabelDic.GetNonEventLabel("FLT");
		Assert.NotNull(func);
		Assert.True(func.IsMethod);
		Assert.Equal(typeof(double), func.MethodType);
		Assert.Equal(2, func.ArgFloatLength);
		Assert.Equal(2, func.Arg.Length);
		Assert.Equal(0.0, Assert.IsType<SingleFloatTerm>(func.Def[0]).Float, 10);
		Assert.Equal(2.5, Assert.IsType<SingleFloatTerm>(func.Def[1]).Float, 10);
	}

	[Fact]
	public async Task Functionf_call_returns_float_and_widens_integer_argument()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		await LoadErbAsync(h, FloatFuncErb);

		var func = h.LabelDic.GetNonEventLabel("FLT");
		// Float 实参 + 缺省值
		var floatCall = UserDefinedMethodTerm.Create(func, [new SingleFloatTerm(1.25)], out var errMes);
		Assert.True(floatCall != null, errMes);
		Assert.Equal(3.75, floatCall!.GetFloatValue(h.Exm), 10);
		Assert.Equal(3.75, Assert.IsType<SingleFloatTerm>(h.Exm.Process.GetValue(floatCall)).Float, 10);

		// Int → Float 隐式拓宽
		var intCall = UserDefinedMethodTerm.Create(func, [new SingleLongTerm(3)], out errMes);
		Assert.True(intCall != null, errMes);
		Assert.Equal(5.5, intCall!.GetFloatValue(h.Exm), 10);
	}

	[Fact]
	public async Task Float_argument_to_integer_parameter_is_rejected()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		await LoadErbAsync(h, """
@INTF(ARG:0)
#FUNCTION
	RETURNF ARG:0
""");

		var func = h.LabelDic.GetNonEventLabel("INTF");
		var call = UserDefinedMethodTerm.Create(func, [new SingleFloatTerm(1.5)], out var errMes);
		Assert.Null(call);
		Assert.Contains("INTF", errMes);
	}

	[Fact]
	public async Task Functionf_and_argf_are_invisible_without_float_capability()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		await LoadErbAsync(h, FloatFuncErb);

		Assert.Null(h.IdDic.GetVariableToken("ARGF", null, false));
		var func = h.LabelDic.GetNonEventLabel("FLT");
		Assert.NotNull(func);
		Assert.False(func.IsMethod);
	}

	[Fact]
	public async Task Functionf_survives_lazy_ir_materialization()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(true);

		h.WriteErb("func.ERB", FloatFuncErb);
		await h.PreloadAsync();
		var loader1 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		Assert.True(await loader1.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic));
		h.Console.DisplayLineList.Clear();

		// 第二轮命中磁盘 IR：签名（含 ArgFloatLength）从 blob 重建。
		var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		Assert.True(await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic));

		var func = h.LabelDic.GetNonEventLabel("FLT");
		Assert.NotNull(func);
		Assert.True(func.IsStub && func.IrBacked);
		var call = UserDefinedMethodTerm.Create(func, [new SingleFloatTerm(1.25)], out var errMes);
		Assert.True(call != null, errMes);
		Assert.Equal(3.75, call!.GetFloatValue(h.Exm), 10);
		Assert.Equal(2, func.ArgFloatLength);
	}
}
