using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 47（Skia 18.0.0）：CONSTTABLE 一维常数表的声明、名称下标、候选回退、
/// USE ERD 独立性、声明约束与重新解析后的索引失效。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0ConstTable1DTests
{
	private static VariableToken Token(LoaderTestHarness h, string name)
	{
		VariableToken? token = h.IdDic.GetVariableToken(name, null, false);
		Assert.NotNull(token);
		return token!;
	}

	private static Dictionary<string, int> Names(LoaderTestHarness h, string varName)
	{
		return h.Process.ConstantData.GetKeywordDictionary(out _, VariableCode.VAR, -1, varName);
	}

	private static string ScreenText(EmueraConsole console)
		=> string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

	private static CodeEE DeclareError(LoaderTestHarness h, string line, bool isPrivate)
	{
		WordCollection wc = LexicalAnalyzer.Analyse(new CharStream(line), LexEndWith.EoL, LexAnalyzeFlag.AllowAssignment);
		return Assert.Throws<CodeEE>(() => UserDefinedVariableData.Create(wc, false, false, isPrivate, null));
	}

	[Fact]
	public async Task Int_const_table_provides_values_and_name_index()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("ct.ERH", "#DIM CONSTTABLE TEST7, 7\n");
		// 第 2 行只有注册数（值留空 = 默认 0）；第 3 行注册数+注册名都空 → 顺延。
		h.WriteCsv("TEST7.csv",
			"0,变量列名0,100\n" +
			"1,变量列名1,\n" +
			"2,,\n" +
			"3,变量列名3,300\n");
		await h.InitializeAsync();

		VariableToken token = Token(h, "TEST7");
		Assert.Equal(100L, token.GetIntValue(null!, [0L]));
		Assert.Equal(0L, token.GetIntValue(null!, [1L]));
		Assert.Equal(300L, token.GetIntValue(null!, [3L]));

		Dictionary<string, int> names = Names(h, "TEST7");
		Assert.Equal(0, names["变量列名0"]);
		Assert.Equal(1, names["变量列名1"]);
		Assert.Equal(3, names["变量列名3"]);
	}

	[Fact]
	public async Task String_and_float_const_tables_apply_csv_quoting()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("ct.ERH", "#DIMS CONSTTABLE S3, 3\n#DIMF CONSTTABLE F3, 3\n");
		h.WriteCsv("S3.csv",
			"0,甲,\"a,b\"\n" +
			"1,乙,\"\"\"q\"\"\"\n");
		h.WriteCsv("F3.csv",
			"0,零,1.5\n" +
			"1,一,\n" +
			"2,二,-2.25\n");
		await h.InitializeAsync();

		VariableToken s3 = Token(h, "S3");
		Assert.Equal("a,b", s3.GetStrValue(null!, [0L]));
		Assert.Equal("\"q\"", s3.GetStrValue(null!, [1L]));
		Assert.Equal("", s3.GetStrValue(null!, [2L]));

		VariableToken f3 = Token(h, "F3");
		Assert.Equal(1.5, f3.GetFloatValue(null!, [0L]), 10);
		Assert.Equal(0.0, f3.GetFloatValue(null!, [1L]), 10);
		Assert.Equal(-2.25, f3.GetFloatValue(null!, [2L]), 10);
	}

	[Fact]
	public async Task Name_table_and_als_are_consumed_and_resolvable()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("ct.ERH", "#DIM CONSTTABLE NT, 2\n");
		h.WriteCsv("NT.csv", "0,,5\n1,,6\n");
		h.WriteErd("NT.ERD", "0,零\n1,一\n");
		h.WriteErd("NT.als", "0,零别名\n");
		await h.InitializeAsync();

		Dictionary<string, int> names = Names(h, "NT");
		Assert.Equal(0, names["零"]);
		Assert.Equal(0, names["零别名"]);
		Assert.Equal(1, names["一"]);
		Assert.Equal(5L, Token(h, "NT").GetIntValue(null!, [0L]));
		Assert.Equal(6L, Token(h, "NT").GetIntValue(null!, [1L]));
	}

	[Fact]
	public async Task Main_table_missing_falls_back_to_erb_value_table()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("ct.ERH", "#DIM CONSTTABLE FB, 2\n");
		h.WriteErd("FB.ERD", "0,零,7\n1,一,8\n");
		await h.InitializeAsync();

		Assert.Equal(7L, Token(h, "FB").GetIntValue(null!, [0L]));
		Assert.Equal(8L, Token(h, "FB").GetIntValue(null!, [1L]));
		Assert.Equal(0, Names(h, "FB")["零"]);
	}

	[Fact]
	public async Task Use_erd_off_still_resolves_const_table_names()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		ConfigData.Current!.GetConfigItem(ConfigCode.UseERD).SetValue(false);
		h.WriteErh("ct.ERH", "#DIM CONSTTABLE OFF1, 2\n#DIM 普通, 2\n");
		h.WriteCsv("OFF1.csv", "0,零,7\n");
		h.WriteCsv("普通.csv", "0,正名,7\n");
		await h.InitializeAsync();

		Assert.Equal(0, Names(h, "OFF1")["零"]);
		// 普通用户变量在 USE ERD:NO 下仍不可用名称下标（与上游一致）。
		Assert.Null(h.Process.ConstantData.GetKeywordDictionary(out _, VariableCode.VAR, -1, "普通"));
	}

	[Fact]
	public async Task Declaration_constraints_are_rejected()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();

		Assert.Contains("ERH", DeclareError(h, "CONSTTABLE MYTAB, 3", isPrivate: true).Message);
		Assert.Contains("尺寸", DeclareError(h, "CONSTTABLE MYTAB", isPrivate: false).Message);
		Assert.Contains("内联初始值", DeclareError(h, "CONSTTABLE MYTAB, 3 = 1,2,3", isPrivate: false).Message);
		Assert.Contains("一维", DeclareError(h, "CONSTTABLE MYTAB, 2, 2", isPrivate: false).Message);
		Assert.Contains("CONSTTABLE", DeclareError(h, "CONST CONSTTABLE MYTAB, 3", isPrivate: false).Message);
	}

	[Fact]
	public async Task Reparsing_erh_sees_new_table_files()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("first.ERH", "#DIM PLAIN_VAR, 1\n");
		await h.InitializeAsync();

		// 第一次解析后新增表文件 + 声明：LoadHeaderFiles 入口必须让文件索引失效。
		File.Delete(Path.Combine(h.ErbDir, "first.ERH"));
		h.WriteErh("second.ERH", "#DIM CONSTTABLE SECOND, 2\n");
		h.WriteCsv("SECOND.csv", "0,乙,2\n");
		Preload.Clear();
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);
		bool ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);
		Assert.True(ok, ScreenText(h.Console));

		Assert.Equal(2L, Token(h, "SECOND").GetIntValue(null!, [0L]));
		Assert.Equal(0, Names(h, "SECOND")["乙"]);
	}

	[Fact]
	public async Task Bad_main_table_is_reported_as_load_error()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		await h.InitializeAsync();
		h.WriteErh("bad.ERH", "#DIM CONSTTABLE BAD, 3\n");
		h.WriteCsv("BAD.csv", "0,甲,1\n0,乙,2\n");
		Preload.Clear();
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);
		Assert.False(loader.LoadHeaderFiles(h.ErbDir, displayReport: false));
		Assert.Contains("常数表", ScreenText(h.Console));
	}
}
