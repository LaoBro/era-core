using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言 P0：数学 / 取整 / 无检查算术 / 位运算（实现票 18，spec §5）。
///
/// 证据：
/// - 值级 fixture：正常值、越界、溢出回绕；
/// - 反射差异：arg / return / flag 对齐 asset §4.1.2（era-core 无 Float，取 Integer 分支）；
/// - profile 可见性：方言键仅在选中 snake 模块的会话可见。
///
/// EVAL / EVALS 需要完整会话（词法期读 Process.IdentifierDictionary），单列于
/// <see cref="P0EvalFunctionTests"/>。
/// </summary>
public sealed class P0MathBitEvalFunctionTests : IDisposable
{
	// 注册表静态构造读 ambient Config：本类每个用例都先开 scope，避免污染同进程其它测试。
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	// ---------- 取值助手 ----------

	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	// ---------- 值级 fixture ----------

	[Theory]
	[InlineData("SIN", 0, 0)]
	[InlineData("COS", 0, 1)]
	[InlineData("TAN", 0, 0)]
	[InlineData("ASIN", 1, 1)]
	[InlineData("ACOS", 1, 0)]
	[InlineData("ATAN", 0, 0)]
	[InlineData("FLOOR", 7, 7)]
	[InlineData("CEIL", 7, 7)]
	[InlineData("ROUND", 7, 7)]
	public void Trig_and_rounding_return_expected_integer(string name, long input, long expected)
	{
		Assert.Equal(expected, Method(name).GetIntValue(null!, Args(new SingleLongTerm(input))));
	}

	[Theory]
	[InlineData("ASIN", 2)]
	[InlineData("ACOS", -5)]
	public void Inverse_trig_rejects_out_of_range_argument(string name, long input)
	{
		var error = Assert.Throws<CodeEE>(() => Method(name).GetIntValue(null!, Args(new SingleLongTerm(input))));
		Assert.Contains(name, error.Message);
	}

	[Fact]
	public void Unchecked_arithmetic_wraps_without_overflow_error()
	{
		Assert.Equal(3, Method("UNCHECKED_ADD").GetIntValue(null!, Args(new SingleLongTerm(1), new SingleLongTerm(2))));
		Assert.Equal(-1, Method("UNCHECKED_SUB").GetIntValue(null!, Args(new SingleLongTerm(0), new SingleLongTerm(1))));
		Assert.Equal(6, Method("UNCHECKED_MUL").GetIntValue(null!, Args(new SingleLongTerm(2), new SingleLongTerm(3))));
		Assert.Equal(long.MinValue, Method("UNCHECKED_NEG").GetIntValue(null!, Args(new SingleLongTerm(long.MinValue))));
		// 溢出回绕而非抛错——这是 UNCHECKED 与普通算术的核心差异。
		Assert.Equal(long.MinValue, Method("UNCHECKED_ADD").GetIntValue(null!, Args(new SingleLongTerm(long.MaxValue), new SingleLongTerm(1))));
	}

	// ---------- 位运算 ----------

	private sealed class StubArrayVariableToken(VariableCode code, object array) : VariableToken(code, null!)
	{
		private readonly object _array = array;
		public override object GetArray() => _array;
	}

	private static VariableTerm ArrayVar(object array)
		=> new(new StubArrayVariableToken(VariableCode.__INTEGER__ | VariableCode.__ARRAY_1D__, array), Array.Empty<AExpression>());

	[Fact]
	public void Bit_set_get_toggle_and_index_of_first_roundtrip()
	{
		var array = new long[2]; // 128 位

		Assert.Equal(1, Method("BITSET").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(5))));
		Assert.Equal(1, Method("BITGET").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(5))));
		Assert.Equal(0, Method("BITGET").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(6))));

		Assert.Equal(1, Method("BITTOGGLE").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(5))));
		Assert.Equal(0, Method("BITGET").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(5))));

		// 越界位读返回 -1（对齐 Skia）。
		Assert.Equal(-1, Method("BITGET").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(999))));

		Assert.Equal(1, Method("BITSET").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(0))));
		Assert.Equal(0, Method("BITINDEXOFFIRST").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(1))));
		// 缺省第二参 = 0 → 找首个清位。
		Assert.Equal(1, Method("BITINDEXOFFIRST").GetIntValue(null!, Args(ArrayVar(array))));
	}

	[Fact]
	public void Bit_set_accepts_multi_bit_write()
	{
		var array = new long[1];
		// BITSET arr, 0, 1, 3 → 低 3 位置位。
		Method("BITSET").GetIntValue(null!, Args(ArrayVar(array), new SingleLongTerm(0), new SingleLongTerm(1), new SingleLongTerm(3)));
		Assert.Equal(0b111, array[0]);
	}

	// ---------- 反射差异（arg / return / flag） ----------

	private static readonly FieldInfo ArgRulesField =
		typeof(FunctionMethod).GetField("argumentTypeArrayEx", BindingFlags.Instance | BindingFlags.NonPublic)!;

	private static (int[] ArgTypes, int OmitStart)[] ArgRules(FunctionMethod method)
	{
		var lists = (Array)ArgRulesField.GetValue(method)!;
		var rules = new (int[], int)[lists.Length];
		for (int i = 0; i < lists.Length; i++)
		{
			object list = lists.GetValue(i)!;
			Type listType = list.GetType();
			var argTypes = (IEnumerable)listType.GetProperty("ArgTypes")!.GetValue(list)!;
			List<int> types = [];
			foreach (object argType in argTypes)
				types.Add(Convert.ToInt32(argType.GetType().GetField("type")!.GetValue(argType)));
			int omitStart = (int)listType.GetProperty("OmitStart")!.GetValue(list)!;
			rules[i] = (types.ToArray(), omitStart);
		}
		return rules;
	}

	// ArgType 枚举的数值（见 FunctionMethod.ArgType）：Any=1, Int=2, String=4, RefInt1D=Ref|Int|Array1D=42。
	private const int Any = 1;
	private const int Int = 2;
	private const int Str = 4;
	private const int RefInt1D = 42;

	public static IEnumerable<object[]> ReflectionExpectations()
	{
		// name, returnIsString, argTypes, omitStart, canRestructure
		foreach (string name in new[] { "SIN", "COS", "TAN", "ASIN", "ACOS", "ATAN", "FLOOR", "CEIL", "ROUND" })
			yield return [name, false, new[] { Any }, -1, true];
		yield return ["UNCHECKED_ADD", false, new[] { Int, Int }, -1, true];
		yield return ["UNCHECKED_SUB", false, new[] { Int, Int }, -1, true];
		yield return ["UNCHECKED_MUL", false, new[] { Int, Int }, -1, true];
		yield return ["UNCHECKED_NEG", false, new[] { Int }, -1, true];
		yield return ["BITSET", false, new[] { RefInt1D, Int, Int, Int }, 2, false];
		yield return ["BITGET", false, new[] { RefInt1D, Int }, 1, false];
		yield return ["BITTOGGLE", false, new[] { RefInt1D, Int }, 1, false];
		yield return ["BITINDEXOFFIRST", false, new[] { RefInt1D, Int }, 1, false];
		yield return ["EVAL", false, new[] { Str, Int }, 1, false];
		yield return ["EVALS", true, new[] { Str, Str }, 1, false];
	}

	[Theory]
	[MemberData(nameof(ReflectionExpectations))]
	public void Registered_reflection_matches_asset_signature(string name, bool returnIsString, int[] argTypes, int omitStart, bool canRestructure)
	{
		FunctionMethod method = Method(name);

		Assert.Equal(returnIsString ? typeof(string) : typeof(long), method.ReturnType);
		Assert.Equal(canRestructure, method.CanRestructure);

		var rules = ArgRules(method);
		Assert.Single(rules);
		Assert.Equal(argTypes, rules[0].ArgTypes);
		Assert.Equal(omitStart, rules[0].OmitStart);
	}

	// ---------- profile 可见性 ----------

	[Fact]
	public void Dialect_functions_are_visible_only_when_snake_module_is_selected()
	{
		var methods = FunctionMethodCreator.GetMethodList();

		var v24 = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snake = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in new[] { "SIN", "ROUND", "UNCHECKED_ADD", "BITSET", "BITGET", "EVAL", "EVALS" })
		{
			Assert.False(v24.ContainsKey(name), name);
			Assert.True(snake.ContainsKey(name), name);
		}
		// 既有基线函数在 v24pure 下不受影响。
		Assert.True(v24.ContainsKey("ABS"));
	}
}

/// <summary>
/// Skia 方言 P0：字符串动态求值 EVAL / EVALS（实现票 18）。需要完整会话才能词法/求值，故走
/// <see cref="LoaderTestHarness"/>（与既有一致性套件同 Collection，避免并行共享引擎状态）。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0EvalFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	[Fact]
	public async Task Eval_returns_default_when_expression_missing_or_broken()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		// 正常值：常量表达式。
		Assert.Equal(3, Method("EVAL").GetIntValue(exm, Args(new SingleStrTerm("1+2"))));
		// 缺省参：空表达式 → 第二参。
		Assert.Equal(42, Method("EVAL").GetIntValue(exm, Args(new SingleStrTerm(""), new SingleLongTerm(42))));
		// 失败：未定义标识符 → 缺省值。
		Assert.Equal(7, Method("EVAL").GetIntValue(exm, Args(new SingleStrTerm("NO_SUCH_IDENTIFIER_XYZ"), new SingleLongTerm(7))));
		Assert.Equal(0, Method("EVAL").GetIntValue(exm, Args(new SingleStrTerm("NO_SUCH_IDENTIFIER_XYZ"))));
	}

	[Fact]
	public async Task EvalS_returns_default_for_non_string_result()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		// 正常值：字符串字面量。
		Assert.Equal("abc", Method("EVALS").GetStrValue(exm, Args(new SingleStrTerm("\"abc\""))));
		// 缺省参：省略第二参 → 空串。
		Assert.Equal("", Method("EVALS").GetStrValue(exm, Args(new SingleStrTerm(""))));
		// 整数表达式在 EVALS 下返回缺省值（对齐 Skia：仅 String/Float 分支）。
		Assert.Equal("d", Method("EVALS").GetStrValue(exm, Args(new SingleStrTerm("1+2"), new SingleStrTerm("d"))));
		// 失败：未定义标识符 → 缺省值。
		Assert.Equal("", Method("EVALS").GetStrValue(exm, Args(new SingleStrTerm("NO_SUCH_IDENTIFIER_XYZ"))));
	}
}