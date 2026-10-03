using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言 P0：SQL_* 函数族 + GETPLATFORM / GETANIMETIMER 的签名与 profile 可见性（实现票 28）。
/// 值级 SQLite 语义见 <see cref="P0SqlFunctionIntegrationTests"/>。
/// </summary>
public sealed class P0SqlFunctionRegistryTests : IDisposable
{
	// 注册表静态构造读 ambient Config：每个用例先开 scope，避免污染同进程其它测试。
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

	// ArgType 数值：Int=2, String=4, VariadicString=Variadic(256)|String(4)=260。
	private const int Int = 2;
	private const int Str = 4;
	private const int VariadicStr = 260;

	public static IEnumerable<object[]> ReflectionExpectations()
	{
		// name, returnIsString, argTypes, omitStart, canRestructure
		yield return ["SQL_CONNECTION_OPEN", false, new[] { Str }, -1, false];
		yield return ["SQL_CONNECT", false, new[] { Str, Str }, 1, false];
		yield return ["SQL_DISCONNECT", false, new[] { Str }, -1, false];
		yield return ["SQL_ESCAPE", true, new[] { Str }, -1, true];
		yield return ["SQL_EXECUTE_NONQUERY", false, new[] { Str, Str }, -1, false];
		yield return ["SQL_EXECUTE_READER", false, new[] { Str, Str }, -1, false];
		yield return ["SQL_EXECUTE_SCALAR_LONG", false, new[] { Str, Str }, -1, false];
		yield return ["SQL_EXECUTE_SCALAR_STRING", true, new[] { Str, Str }, -1, false];
		yield return ["SQL_P_EXECUTE_NONQUERY", false, new[] { Str, Str, VariadicStr }, 2, false];
		yield return ["SQL_P_EXECUTE_READER", false, new[] { Str, Str, VariadicStr }, 2, false];
		yield return ["SQL_P_EXECUTE_SCALAR_LONG", false, new[] { Str, Str, VariadicStr }, 2, false];
		yield return ["SQL_P_EXECUTE_SCALAR_STRING", true, new[] { Str, Str, VariadicStr }, 2, false];
		yield return ["SQL_READER_READ", false, new[] { Int }, -1, false];
		yield return ["SQL_READER_GET_LONG", false, new[] { Int, Int }, -1, false];
		yield return ["SQL_READER_GET_STRING", true, new[] { Int, Int }, -1, false];
		yield return ["SQL_READER_ISNULL", false, new[] { Int, Int }, -1, false];
		yield return ["SQL_READER_CLOSE", false, new[] { Int }, -1, false];
		yield return ["SQL_IMPORT_MAP_XML", false, new[] { Str, Str, Str }, -1, false];
		yield return ["SQL_IMPORT_DT_XML", false, new[] { Str, Str, Str, Str }, -1, false];
		yield return ["SQL_EXPORT_MAP_XML", false, new[] { Str, Str, Str }, -1, false];
		yield return ["SQL_EXPORT_DT_XML", false, new[] { Str, Str, Str, Str }, -1, false];
		yield return ["SQL_IMPORT_XML_CUSTOM", false, new[] { Str, Str, Str, Str, Str }, -1, false];
		yield return ["GETPLATFORM", false, Array.Empty<int>(), -1, true];
		yield return ["GETANIMETIMER", false, Array.Empty<int>(), -1, false];
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

	[Fact]
	public void Dialect_functions_are_visible_only_when_snake_module_is_selected()
	{
		var methods = FunctionMethodCreator.GetMethodList();
		var v24 = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snake = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in (string[])["SQL_CONNECT", "SQL_EXECUTE_READER", "SQL_P_EXECUTE_SCALAR_LONG",
			"SQL_READER_GET_STRING", "SQL_IMPORT_MAP_XML", "SQL_EXPORT_DT_XML", "GETPLATFORM", "GETANIMETIMER"])
		{
			Assert.False(v24.ContainsKey(name), name);
			Assert.True(snake.ContainsKey(name), name);
		}
	}

	/// <summary>命令形可解析的结构性依据：methodList 每项被并入 funcDic（methodInstruction）——见 FunctionIdentifier。</summary>
	[Fact]
	public void Dialect_functions_are_also_registered_as_command_instructions()
	{
		var instructions = FunctionIdentifier.GetInstructionNameDic();
		Assert.True(instructions.ContainsKey("SQL_CONNECT"));
		Assert.True(instructions.ContainsKey("GETPLATFORM"));
	}
}

/// <summary>
/// Skia 方言 P0：SQL_* 的真实 SQLite 值级语义（实现票 28）。走 <see cref="LoaderTestHarness"/>
/// （真实 GamePaths + scope + 临时游戏目录），与既有一致性套件同 Collection，避免并行共享引擎状态。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0SqlFunctionIntegrationTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static long Int(string name, ExpressionMediator exm, params AExpression[] args)
		=> Method(name).GetIntValue(exm, Args(args));

	private static string Str(string name, ExpressionMediator exm, params AExpression[] args)
		=> Method(name).GetStrValue(exm, Args(args));

	private static string SqlPath(LoaderTestHarness harness, string name)
		=> "Data Source=" + Path.Combine(harness.TempRoot, name);

	[Fact]
	public async Task Connect_nonquery_scalar_and_disconnect_roundtrip()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Assert.Equal(1, Int("SQL_CONNECT", exm, new SingleStrTerm("t"), new SingleStrTerm(SqlPath(harness, "a.db"))));
		Assert.Equal(0, Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("t"), new SingleStrTerm("CREATE TABLE x(k TEXT, v INTEGER)")));
		Assert.Equal(1, Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("t"), new SingleStrTerm("INSERT INTO x VALUES('a', 7)")));
		Assert.Equal(7, Int("SQL_EXECUTE_SCALAR_LONG", exm, new SingleStrTerm("t"), new SingleStrTerm("SELECT v FROM x WHERE k='a'")));
		Assert.Equal("a", Str("SQL_EXECUTE_SCALAR_STRING", exm, new SingleStrTerm("t"), new SingleStrTerm("SELECT k FROM x WHERE v=7")));
		Assert.Equal(1, Int("SQL_DISCONNECT", exm, new SingleStrTerm("t")));
	}

	[Fact]
	public async Task Parameterized_variants_bind_at0_dot_dot_atn()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Assert.Equal(1, Int("SQL_CONNECT", exm, new SingleStrTerm("p"), new SingleStrTerm(SqlPath(harness, "p.db"))));
		Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("p"), new SingleStrTerm("CREATE TABLE t(k TEXT, v INTEGER)"));

		Assert.Equal(1, Int("SQL_P_EXECUTE_NONQUERY", exm, new SingleStrTerm("p"),
			new SingleStrTerm("INSERT INTO t VALUES(@0, @1)"), new SingleStrTerm("b"), new SingleStrTerm("9")));
		Assert.Equal(9, Int("SQL_P_EXECUTE_SCALAR_LONG", exm, new SingleStrTerm("p"),
			new SingleStrTerm("SELECT v FROM t WHERE k = @0"), new SingleStrTerm("b")));
		Assert.Equal("b", Str("SQL_P_EXECUTE_SCALAR_STRING", exm, new SingleStrTerm("p"),
			new SingleStrTerm("SELECT k FROM t WHERE v = @0"), new SingleStrTerm("9")));
	}

	[Fact]
	public async Task Reader_lifecycle_reads_rows_then_reports_end()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Int("SQL_CONNECT", exm, new SingleStrTerm("r"), new SingleStrTerm(SqlPath(harness, "r.db")));
		Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("r"), new SingleStrTerm("CREATE TABLE t(k TEXT, v INTEGER)"));
		Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("r"), new SingleStrTerm("INSERT INTO t VALUES('a', 1),('b', 2)"));

		long id = Int("SQL_EXECUTE_READER", exm, new SingleStrTerm("r"), new SingleStrTerm("SELECT k, v FROM t ORDER BY v"));
		Assert.True(id > 0);

		Assert.Equal(1, Int("SQL_READER_READ", exm, new SingleLongTerm(id)));
		Assert.Equal("a", Str("SQL_READER_GET_STRING", exm, new SingleLongTerm(id), new SingleLongTerm(0)));
		Assert.Equal(1, Int("SQL_READER_GET_LONG", exm, new SingleLongTerm(id), new SingleLongTerm(1)));

		Assert.Equal(1, Int("SQL_READER_READ", exm, new SingleLongTerm(id)));
		Assert.Equal("b", Str("SQL_READER_GET_STRING", exm, new SingleLongTerm(id), new SingleLongTerm(0)));

		Assert.Equal(0, Int("SQL_READER_READ", exm, new SingleLongTerm(id)));
		Assert.Equal(1, Int("SQL_READER_CLOSE", exm, new SingleLongTerm(id)));
	}

	[Fact]
	public async Task Reader_reports_null_and_tolerates_mismatched_column_types()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Int("SQL_CONNECT", exm, new SingleStrTerm("n"), new SingleStrTerm(SqlPath(harness, "n.db")));
		Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("n"), new SingleStrTerm("CREATE TABLE t(k TEXT, v TEXT)"));
		Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("n"), new SingleStrTerm("INSERT INTO t VALUES('a', NULL)"));

		long id = Int("SQL_EXECUTE_READER", exm, new SingleStrTerm("n"), new SingleStrTerm("SELECT k, v FROM t"));
		Int("SQL_READER_READ", exm, new SingleLongTerm(id));

		Assert.Equal(1, Int("SQL_READER_ISNULL", exm, new SingleLongTerm(id), new SingleLongTerm(1)));
		Assert.Equal(0, Int("SQL_READER_GET_LONG", exm, new SingleLongTerm(id), new SingleLongTerm(1)));
		Assert.Equal("", Str("SQL_READER_GET_STRING", exm, new SingleLongTerm(id), new SingleLongTerm(1)));
		// 宽容列型：TEXT 列含数字串时 GetLong 走 Convert 而非抛。
		Assert.Equal(0, Int("SQL_READER_GET_LONG", exm, new SingleLongTerm(id), new SingleLongTerm(1)));
		Int("SQL_READER_CLOSE", exm, new SingleLongTerm(id));
	}

	[Fact]
	public async Task Disconnect_also_closes_readers_for_that_database()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Int("SQL_CONNECT", exm, new SingleStrTerm("d"), new SingleStrTerm(SqlPath(harness, "d.db")));
		Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("d"), new SingleStrTerm("CREATE TABLE t(v INTEGER)"));
		Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("d"), new SingleStrTerm("INSERT INTO t VALUES(1),(2)"));

		long id = Int("SQL_EXECUTE_READER", exm, new SingleStrTerm("d"), new SingleStrTerm("SELECT v FROM t"));
		Assert.Equal(1, Int("SQL_READER_READ", exm, new SingleLongTerm(id)));

		Assert.Equal(1, Int("SQL_DISCONNECT", exm, new SingleStrTerm("d")));
		// reader 已随断开关闭（原版 Skia 会悬空）——此后读取返回 0 而非抛。
		Assert.Equal(0, Int("SQL_READER_READ", exm, new SingleLongTerm(id)));
	}

	[Fact]
	public async Task Escape_doubles_single_quotes()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Assert.Equal("a''b", Str("SQL_ESCAPE", exm, new SingleStrTerm("a'b")));
		Assert.Equal("", Str("SQL_ESCAPE", exm, new SingleStrTerm("")));
	}

	[Fact]
	public async Task Relative_data_source_is_normalized_against_exe_dir_not_cwd()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Directory.CreateDirectory(Path.Combine(harness.TempRoot, "plugins"));
		Assert.Equal(1, Int("SQL_CONNECT", exm, new SingleStrTerm("rel"), new SingleStrTerm("Data Source=plugins/rel.db")));

		// headless 的 CWD ≠ 游戏目录：归一必须以 ExeDir 为基。
		Assert.True(File.Exists(Path.Combine(harness.TempRoot, "plugins", "rel.db")));
	}

	[Fact]
	public async Task ConnectionOpen_creates_database_under_sav_dir()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		Assert.Equal(1, Int("SQL_CONNECTION_OPEN", exm, new SingleStrTerm("mydb")));
		Assert.True(File.Exists(Path.Combine(Config.SavDir, "sql", "mydb.db")));
		// 重建等价（replaceExisting）：再次打开同 name 不抛。
		Assert.Equal(1, Int("SQL_CONNECTION_OPEN", exm, new SingleStrTerm("mydb")));
	}

	[Fact]
	public async Task Import_and_export_map_xml_roundtrip()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		File.WriteAllText(Path.Combine(harness.TempRoot, "in.xml"),
			"<map><p><k>x</k><v>42</v></p><p><k>y</k><v>7</v></p></map>");

		Int("SQL_CONNECT", exm, new SingleStrTerm("x"), new SingleStrTerm(SqlPath(harness, "x.db")));
		Assert.Equal(1, Int("SQL_IMPORT_MAP_XML", exm, new SingleStrTerm("x"), new SingleStrTerm("m"), new SingleStrTerm("in.xml")));
		Assert.Equal(42, Int("SQL_EXECUTE_SCALAR_LONG", exm, new SingleStrTerm("x"), new SingleStrTerm("SELECT v FROM m WHERE k='x'")));

		Assert.Equal(1, Int("SQL_EXPORT_MAP_XML", exm, new SingleStrTerm("x"), new SingleStrTerm("m"), new SingleStrTerm("out.xml")));
		Assert.True(File.Exists(Path.Combine(harness.TempRoot, "out.xml")));
	}

	[Fact]
	public async Task Errors_are_surfaced_as_CodeEE_with_layered_messages()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		var notConnected = Assert.Throws<CodeEE>(() =>
			Int("SQL_EXECUTE_NONQUERY", exm, new SingleStrTerm("missing"), new SingleStrTerm("SELECT 1")));
		Assert.Contains("not connected", notConnected.Message);

		var badReader = Assert.Throws<CodeEE>(() =>
			Int("SQL_READER_GET_LONG", exm, new SingleLongTerm(999999), new SingleLongTerm(0)));
		Assert.Contains("reader", badReader.Message, StringComparison.OrdinalIgnoreCase);
	}

	// ---------- GETPLATFORM / GETANIMETIMER ----------

	[Fact]
	public async Task GetPlatform_returns_windows_code_on_windows_desktop()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		long expected = OperatingSystem.IsWindows() ? 0 : OperatingSystem.IsLinux() ? 4 : 5;
		Assert.Equal(expected, Int("GETPLATFORM", exm));
	}

	[Fact]
	public async Task GetAnimeTimer_mirrors_the_existing_set_instruction()
	{
		using var harness = new LoaderTestHarness();
		await harness.InitializeAsync();
		var exm = harness.Exm;

		// 未设置 → 0。
		Assert.Equal(0, Int("GETANIMETIMER", exm));
		// SETANIMETIMER 是既有 baseline 函数（写入 console redraw timer）；GETANIMETIMER 读回同值。
		Method("SETANIMETIMER").GetIntValue(exm, Args(new SingleLongTerm(120)));
		Assert.Equal(120, Int("GETANIMETIMER", exm));
	}
}
