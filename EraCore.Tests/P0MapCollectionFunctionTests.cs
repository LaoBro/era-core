using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言 P0：集合函数 MAP_*（实现票 19，spec §5）。
///
/// 证据：
/// - 互操作 fixture：create → set → 新函数，复用既有 <c>DataStringMaps</c> 数据模型；
/// - 值级 fixture：MAP_VALUES 各形态、MAP_MERGE / MAP_REMOVEIF / MAP_FINDKEY 各模式、
///   MAP_TOSTRING / MAP_FROMSTRING 往返；
/// - 反射差异：arg / return / flag 对齐 asset §4.1.2。
///
/// 需要完整会话（MAP_* 读 <c>exm.VEvaluator.VariableData</c>），走 <see cref="LoaderTestHarness"/>，
/// 与既有一致性套件同 Collection。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0MapCollectionFunctionTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static SingleStrTerm S(string v) => new(v);
	private static SingleLongTerm I(long v) => new(v);

	// 数组引用参数：仅需 VariableTerm.Identifier.GetArray() 返回底层 string[]。
	private sealed class StubArrayVariableToken(VariableCode code, object array) : VariableToken(code, null!)
	{
		private readonly object _array = array;
		public override object GetArray() => _array;
	}

	private static VariableTerm StrArrayVar(string[] array)
		=> new(new StubArrayVariableToken(VariableCode.__STRING__ | VariableCode.__ARRAY_1D__, array), Array.Empty<AExpression>());

	private static void CreateMap(ExpressionMediator exm, string map)
		=> Assert.Equal(1, Method("MAP_CREATE").GetIntValue(exm, Args(S(map))));

	private static void Set(ExpressionMediator exm, string map, string key, string value)
		=> Assert.Equal(1, Method("MAP_SET").GetIntValue(exm, Args(S(map), S(key), S(value))));

	private static string Get(ExpressionMediator exm, string map, string key)
		=> Method("MAP_GET").GetStrValue(exm, Args(S(map), S(key)));

	// ---------- 互操作：既有 MAP_* 建表 → 新函数消费 ----------

	[Fact]
	public async Task New_functions_interoperate_with_existing_map_family()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		CreateMap(exm, "m");
		Set(exm, "m", "k", "v");

		// MAP_VALUES 单参形态：逗号连接值。
		Assert.Equal("v", Method("MAP_VALUES").GetStrValue(exm, Args(S("m"))));
		// MAP_TOSTRING 单参形态：键=值。
		Assert.Equal("k=v", Method("MAP_TOSTRING").GetStrValue(exm, Args(S("m"))));
		// MAP_SIZE 既有函数仍一致。
		Assert.Equal(1, Method("MAP_SIZE").GetIntValue(exm, Args(S("m"))));
	}

	// ---------- MAP_VALUES ----------

	[Fact]
	public async Task Map_values_forms_write_into_array_and_results()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		CreateMap(exm, "m");
		Set(exm, "m", "a", "1");
		Set(exm, "m", "b", "2");

		// 多参（>1）形态返回空串，仅写数组。
		var array = new string[8];
		Assert.Equal("", Method("MAP_VALUES").GetStrValue(exm, Args(S("m"), StrArrayVar(array), I(1))));
		Assert.Equal(new[] { "1", "2" }, array.Take(2).ToArray());
		Assert.Equal(2, harness.VEvaluator.RESULT);

		// 长度 0 参数 → 不写、返回空串。
		var empty = new string[8];
		Assert.Equal("", Method("MAP_VALUES").GetStrValue(exm, Args(S("m"), StrArrayVar(empty), I(0))));
		Assert.All(empty, v => Assert.Null(v));
	}

	// ---------- MAP_MERGE ----------

	[Fact]
	public async Task Map_merge_overwrites_destination_and_reports_missing_map()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		CreateMap(exm, "dst");
		Set(exm, "dst", "a", "1");
		Set(exm, "dst", "b", "2");
		CreateMap(exm, "src");
		Set(exm, "src", "b", "9");
		Set(exm, "src", "c", "3");

		Assert.Equal(1, Method("MAP_MERGE").GetIntValue(exm, Args(S("dst"), S("src"))));
		Assert.Equal("1", Get(exm, "dst", "a"));
		Assert.Equal("9", Get(exm, "dst", "b")); // 覆盖
		Assert.Equal("3", Get(exm, "dst", "c"));
		Assert.Equal(3, Method("MAP_SIZE").GetIntValue(exm, Args(S("dst"))));

		// 源 map 不存在 → 0，目标不变。
		Assert.Equal(0, Method("MAP_MERGE").GetIntValue(exm, Args(S("dst"), S("no_such_map"))));
		Assert.Equal(3, Method("MAP_SIZE").GetIntValue(exm, Args(S("dst"))));
	}

	// ---------- MAP_REMOVEIF ----------

	[Fact]
	public async Task Map_removeif_modes_and_unknown_mode()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		CreateMap(exm, "m");
		Set(exm, "m", "pre_a", "same");
		Set(exm, "m", "pre_b", "same");
		Set(exm, "m", "other", "diff");

		// KEY_PREFIX 删除 2 条。
		Assert.Equal(2, Method("MAP_REMOVEIF").GetIntValue(exm, Args(S("m"), S("pre_"), S("KEY_PREFIX"))));
		Assert.Equal(1, Method("MAP_SIZE").GetIntValue(exm, Args(S("m"))));

		// VAL_NE：removes 值不等于 match 的键 → 剩 other=diff 被删。
		Assert.Equal(1, Method("MAP_REMOVEIF").GetIntValue(exm, Args(S("m"), S("same"), S("VAL_NE"))));
		Assert.Equal(0, Method("MAP_SIZE").GetIntValue(exm, Args(S("m"))));

		// 未知 mode → -1。
		CreateMap(exm, "m2");
		Set(exm, "m2", "k", "v");
		Assert.Equal(-1, Method("MAP_REMOVEIF").GetIntValue(exm, Args(S("m2"), S("k"), S("NOPE"))));
		Assert.Equal(1, Method("MAP_SIZE").GetIntValue(exm, Args(S("m2"))));
	}

	// ---------- MAP_FINDKEY ----------

	[Fact]
	public async Task Map_findkey_returns_matching_keys_and_sets_result()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		CreateMap(exm, "m");
		Set(exm, "m", "alpha", "x");
		Set(exm, "m", "beta", "y");
		Set(exm, "m", "alpine", "x");

		string found = Method("MAP_FINDKEY").GetStrValue(exm, Args(S("m"), S("alp"), S("KEY_PREFIX")));
		Assert.Equal(new[] { "alpha", "alpine" }, found.Split(',').OrderBy(s => s).ToArray());
		Assert.Equal(2, harness.VEvaluator.RESULT);

		// VAL_EQ 命中 2 条（值为 x 的键）。
		string byVal = Method("MAP_FINDKEY").GetStrValue(exm, Args(S("m"), S("x"), S("VAL_EQ")));
		Assert.Equal(new[] { "alpha", "alpine" }, byVal.Split(',').OrderBy(s => s).ToArray());

		// 无匹配 → 空串、RESULT=0。
		Assert.Equal("", Method("MAP_FINDKEY").GetStrValue(exm, Args(S("m"), S("zzz"), S("KEY_CONTAINS"))));
		Assert.Equal(0, harness.VEvaluator.RESULT);
	}

	// ---------- MAP_TOSTRING / MAP_FROMSTRING ----------

	[Fact]
	public async Task Map_tostring_and_fromstring_roundtrip_and_separators()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		CreateMap(exm, "m");
		Set(exm, "m", "k", "v");
		Assert.Equal("k=v", Method("MAP_TOSTRING").GetStrValue(exm, Args(S("m"))));
		Assert.Equal("k:v", Method("MAP_TOSTRING").GetStrValue(exm, Args(S("m"), S(";"), S(":"))));

		// 多条目往返：TOSTRING → FROMSTRING 到新 map。
		Set(exm, "m", "k2", "v2");
		string serialized = Method("MAP_TOSTRING").GetStrValue(exm, Args(S("m")));
		CreateMap(exm, "m2");
		Assert.Equal(2, Method("MAP_FROMSTRING").GetIntValue(exm, Args(S("m2"), S(serialized))));
		Assert.Equal("v", Get(exm, "m2", "k"));
		Assert.Equal("v2", Get(exm, "m2", "k2"));

		// 自定义分隔符。
		CreateMap(exm, "m3");
		Assert.Equal(2, Method("MAP_FROMSTRING").GetIntValue(exm, Args(S("m3"), S("a:1;b:2"), S(";"), S(":"))));
		Assert.Equal("1", Get(exm, "m3", "a"));
		Assert.Equal("2", Get(exm, "m3", "b"));

		// 空数据 → 0。
		Assert.Equal(0, Method("MAP_FROMSTRING").GetIntValue(exm, Args(S("m3"), S(""))));
		// 无键值分隔符的条目被跳过：仅 c=3 与 e=5 写入。
		CreateMap(exm, "m4");
		Assert.Equal(2, Method("MAP_FROMSTRING").GetIntValue(exm, Args(S("m4"), S("c=3,d,e=5"))));
		Assert.Equal(2, Method("MAP_SIZE").GetIntValue(exm, Args(S("m4"))));
	}
}

/// <summary>
/// Skia 方言 P0：MAP_* 的注册签名与 profile 可见性（实现票 19）。
/// 只需注册表与 Config scope，不需要完整会话。
/// </summary>
public sealed class P0MapCollectionRegistrationTests : IDisposable
{
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

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

	// ArgType：String=4, Int=2, RefString1D=Ref|String|Array1D=8|4|32=44。
	private const int Int = 2;
	private const int Str = 4;
	private const int RefStr1D = 44;

	public static IEnumerable<object[]> ReflectionExpectations()
	{
		// name, returnIsString, argTypes[]（每项一条规则）, omitStart[]（按规则）, canRestructure
		yield return ["MAP_VALUES", true, new[] { new[] { Str, Int }, new[] { Str, RefStr1D, Int } }, new[] { 1, -1 }, false];
		yield return ["MAP_MERGE", false, new[] { new[] { Str, Str } }, new[] { -1 }, false];
		yield return ["MAP_REMOVEIF", false, new[] { new[] { Str, Str, Str } }, new[] { -1 }, false];
		yield return ["MAP_FINDKEY", true, new[] { new[] { Str, Str, Str } }, new[] { -1 }, false];
		yield return ["MAP_TOSTRING", true, new[] { new[] { Str, Str, Str } }, new[] { 1 }, false];
		yield return ["MAP_FROMSTRING", false, new[] { new[] { Str, Str, Str, Str } }, new[] { 2 }, false];
	}

	[Theory]
	[MemberData(nameof(ReflectionExpectations))]
	public void Registered_reflection_matches_asset_signature(string name, bool returnIsString, int[][] argTypes, int[] omitStarts, bool canRestructure)
	{
		FunctionMethod method = Method(name);

		Assert.Equal(returnIsString ? typeof(string) : typeof(long), method.ReturnType);
		Assert.Equal(canRestructure, method.CanRestructure);

		var rules = ArgRules(method);
		Assert.Equal(argTypes.Length, rules.Length);
		for (int i = 0; i < rules.Length; i++)
		{
			Assert.Equal(argTypes[i], rules[i].ArgTypes);
			Assert.Equal(omitStarts[i], rules[i].OmitStart);
		}
	}

	[Fact]
	public void Dialect_map_functions_are_visible_only_when_snake_module_is_selected()
	{
		var methods = FunctionMethodCreator.GetMethodList();

		var v24 = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snake = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in new[] { "MAP_VALUES", "MAP_MERGE", "MAP_REMOVEIF", "MAP_FINDKEY", "MAP_TOSTRING", "MAP_FROMSTRING" })
		{
			Assert.False(v24.ContainsKey(name), name);
			Assert.True(snake.ContainsKey(name), name);
		}
		// 既有基线 MAP_* 在 v24pure 下不受影响。
		Assert.True(v24.ContainsKey("MAP_CREATE"));
	}
}