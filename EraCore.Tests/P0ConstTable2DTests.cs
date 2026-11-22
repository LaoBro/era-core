using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 48（Skia 18.0.0）：CONSTTABLE 二维常数表。
/// 覆盖 @1/@2 定位、两种摆放（互为转置）、维度名称表与别名、转置补充表合并、
/// 主表/补充表的名称冲突口径，以及 #DIMF / #DIMS 二维取值与默认值回填。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0ConstTable2DTests
{
	private static VariableToken Token(LoaderTestHarness h, string name)
	{
		VariableToken? token = h.IdDic.GetVariableToken(name, null, false);
		Assert.NotNull(token);
		return token!;
	}

	private static Dictionary<string, int> Names(LoaderTestHarness h, string varName, int dim)
	{
		return h.Process.ConstantData.GetKeywordDictionary(out _, VariableCode.VAR2D, dim, varName);
	}

	private static ConstArrayTable.Result Load(LoaderTestHarness h, string name, int[] lengths, int kind)
	{
		ConstArrayTable.ClearFileIndex();
		ConstArrayTable.Result? result = ConstArrayTable.Load(name, lengths, kind, out string error);
		Assert.NotNull(result);
		_ = error;
		return result!;
	}

	private static string LoadError(LoaderTestHarness h, string name, int[] lengths, int kind)
	{
		ConstArrayTable.ClearFileIndex();
		ConstArrayTable.Result? result = ConstArrayTable.Load(name, lengths, kind, out string error);
		Assert.Null(result);
		return error;
	}

	[Fact]
	public async Task Two_dimensional_layout_values_and_dimension_names()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("ct2.ERH", "#DIM CONSTTABLE CT2, 3, 3\n");
		h.WriteCsv("CT2.csv",
			",@2,0,1,2\n" +
			"@1,,零列,一列,二列\n" +
			"0,零行,1,2,3\n" +
			"1,一行,4,,6\n" +
			"2,二行,7,8,9\n");
		await h.InitializeAsync();

		VariableToken token = Token(h, "CT2");
		Assert.Equal(1L, token.GetIntValue(null!, [0L, 0L]));
		Assert.Equal(0L, token.GetIntValue(null!, [1L, 1L])); // 留空 = 默认值
		Assert.Equal(9L, token.GetIntValue(null!, [2L, 2L]));

		Dictionary<string, int> dim1 = Names(h, "CT2", 0);
		Assert.Equal(0, dim1["零行"]);
		Assert.Equal(2, dim1["二行"]);
		Dictionary<string, int> dim2 = Names(h, "CT2", 1);
		Assert.Equal(0, dim2["零列"]);
		Assert.Equal(1, dim2["一列"]);
	}

	[Fact]
	public void Marker_swap_is_the_transposed_layout()
	{
		using var h = new LoaderTestHarness();
		// 同一份逻辑数组的两种写法：标记互换即行列转置。
		h.WriteCsv("TR.csv",
			",@2,0,1\n" +
			"@1,,列0,列1\n" +
			"0,行0,10,20\n" +
			"1,行1,30,40\n");
		ConstArrayTable.Result normal = Load(h, "TR", [2, 2], ConstArrayTable.KindInt);
		long[] normalValues = (long[])normal.Values;
		Assert.Equal(10L, normalValues[0]);
		Assert.Equal(20L, normalValues[1]);
		Assert.Equal(30L, normalValues[2]);
		Assert.Equal(40L, normalValues[3]);

		h.WriteCsv("TT.csv",
			",@1,0,1\n" +
			"@2,,行0,行1\n" +
			"0,列0,10,30\n" +
			"1,列1,20,40\n");
		ConstArrayTable.Result transposed = Load(h, "TT", [2, 2], ConstArrayTable.KindInt);
		long[] transposedValues = (long[])transposed.Values;
		Assert.Equal(10L, transposedValues[0]);
		Assert.Equal(20L, transposedValues[1]);
		Assert.Equal(30L, transposedValues[2]);
		Assert.Equal(40L, transposedValues[3]);
		// 转置写法里纵向标记决定第 2 维、横向决定第 1 维。
		Assert.Equal(0, transposed.Names[1]["列0"]);
		Assert.Equal(1, transposed.Names[1]["列1"]);
		Assert.Equal(0, transposed.Names[0]["行0"]);
		Assert.Equal(1, transposed.Names[0]["行1"]);
	}

	[Fact]
	public void Transposed_extension_fills_values_and_names()
	{
		using var h = new LoaderTestHarness();
		// 主表只写 (0,0)；方向相反的补充表补其余三格。
		h.WriteCsv("MX.csv",
			",@2,0,1\n" +
			"@1,,列0,列1\n" +
			"0,行0,10,\n" +
			"1,行1,,\n");
		h.WriteCsv("MX.erd",
			",@1,0,1\n" +
			"@2,,行0,行1\n" +
			"0,列0,,20\n" +
			"1,列1,30,40\n");

		ConstArrayTable.Result r = Load(h, "MX", [2, 2], ConstArrayTable.KindInt);
		long[] values = (long[])r.Values;
		Assert.Equal(10L, values[0]);
		Assert.Equal(30L, values[1]);
		Assert.Equal(20L, values[2]);
		Assert.Equal(40L, values[3]);
		Assert.Equal(0, r.Names[0]["行0"]);
		Assert.Equal(1, r.Names[0]["行1"]);
		Assert.Equal(0, r.Names[1]["列0"]);
		Assert.Equal(1, r.Names[1]["列1"]);
	}

	[Fact]
	public async Task Dimension_name_files_and_aliases_apply()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("nt2.ERH", "#DIM CONSTTABLE NT2, 2, 2\n");
		h.WriteCsv("NT2.csv",
			",@2,0,1\n" +
			"@1,,,\n" +
			"0,,5,6\n" +
			"1,,7,8\n");
		h.WriteErd("NT2@1.ERD", "0,行零\n1,行一\n");
		h.WriteErd("NT2@2.ERD", "0,列零\n");
		h.WriteErd("NT2@1.als", "0,行零别名\n");
		await h.InitializeAsync();

		VariableToken token = Token(h, "NT2");
		Assert.Equal(5L, token.GetIntValue(null!, [0L, 0L]));
		Assert.Equal(8L, token.GetIntValue(null!, [1L, 1L]));

		Dictionary<string, int> dim1 = Names(h, "NT2", 0);
		Assert.Equal(0, dim1["行零"]);
		Assert.Equal(0, dim1["行零别名"]);
		Assert.Equal(1, dim1["行一"]);
		Dictionary<string, int> dim2 = Names(h, "NT2", 1);
		Assert.Equal(0, dim2["列零"]);
		Assert.False(dim2.ContainsKey("行零"));
	}

	[Fact]
	public void Misplaced_layout_markers_report_anchor_positions()
	{
		using var h = new LoaderTestHarness();
		// @1 放在 (2,3) 而不是 (2,1)。
		h.WriteCsv("BADM.csv",
			",@2,0\n" +
			",0,@1\n" +
			"0,,1\n");
		string error = LoadError(h, "BADM", [2, 2], ConstArrayTable.KindInt);
		Assert.Contains("(2,1) 与 (1,2) 两处", error);
	}

	[Fact]
	public void Two_dimensional_layout_is_rejected_for_one_dimensional_declaration()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("ONE.csv",
			",@2,0,1\n" +
			"@1,,列0,列1\n" +
			"0,行0,10,20\n");
		string error = LoadError(h, "ONE", [2], ConstArrayTable.KindInt);
		Assert.Contains("只能用于二维数组", error);
	}

	[Fact]
	public void One_dimensional_candidate_falls_back_to_two_dimensional_table()
	{
		using var h = new LoaderTestHarness();
		// CSV/*.csv 是一维表，挡住二维声明 → 按候选顺序退到 CSV/*.erd。
		h.WriteCsv("FB2.csv", "0,五,5\n");
		h.WriteCsv("FB2.erd",
			",@2,0,1\n" +
			"@1,,列0,列1\n" +
			"0,行0,10,20\n" +
			"1,行1,30,40\n");

		ConstArrayTable.Result r = Load(h, "FB2", [2, 2], ConstArrayTable.KindInt);
		Assert.Contains("FB2.erd", r.Path);
		Assert.Equal(40L, ((long[])r.Values)[3]);
		Assert.Equal(0, r.Names[0]["行0"]);
	}

	[Fact]
	public void Extension_duplicate_index_name_warns_and_keeps_main()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("CN2.csv",
			",@2,0,1\n" +
			"@1,,列0,列1\n" +
			"0,甲,1,2\n" +
			"1,乙,3,4\n");
		h.WriteCsv("CN2.erd",
			",@2,0,1\n" +
			"@1,,列0,列1\n" +
			"0,丙,9,9\n");

		ConstArrayTable.Result r = Load(h, "CN2", [2, 2], ConstArrayTable.KindInt);
		Assert.Equal(0, r.Names[0]["甲"]);
		Assert.False(r.Names[0].ContainsKey("丙"));
		Assert.Equal(1L, ((long[])r.Values)[0]);
		Assert.Contains(r.Warnings, w => w.Contains("与主表不同"));
	}

	[Fact]
	public void Main_table_duplicate_index_two_names_is_an_error()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("DUP2.csv",
			",@2,0,1\n" +
			"@1,,列0,列1\n" +
			"0,甲,1,2\n" +
			"0,乙,3,4\n");
		string error = LoadError(h, "DUP2", [2, 2], ConstArrayTable.KindInt);
		Assert.Contains("已有名字", error);
	}

	[Fact]
	public async Task Float_two_dimensional_const_table_fills_row_major()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("ft2.ERH", "#DIMF CONSTTABLE FT2, 2, 3\n");
		h.WriteCsv("FT2.csv",
			",@2,0,1,2\n" +
			"@1,,c0,c1,c2\n" +
			"0,r0,1.5,2.5,3.5\n" +
			"1,r1,4.5,,6.5\n");
		await h.InitializeAsync();

		VariableToken token = Token(h, "FT2");
		Assert.Equal(1.5, token.GetFloatValue(null!, [0L, 0L]), 10);
		Assert.Equal(3.5, token.GetFloatValue(null!, [0L, 2L]), 10);
		Assert.Equal(0.0, token.GetFloatValue(null!, [1L, 1L]), 10); // 留空 = 默认值
		Assert.Equal(6.5, token.GetFloatValue(null!, [1L, 2L]), 10);

		// 常数表的值就是默认值：复位后依旧按行列回填。
		UserDefinedVariableToken ft2 = Assert.IsAssignableFrom<UserDefinedVariableToken>(Token(h, "FT2"));
		ft2.SetValue(99.0, [1L, 2L]);
		ft2.SetDefault();
		Assert.Equal(6.5, token.GetFloatValue(null!, [1L, 2L]), 10);
		Assert.Equal(2, ((double[,])token.GetArray()).GetLength(0));
		Assert.Equal(3, ((double[,])token.GetArray()).GetLength(1));
	}

	[Fact]
	public async Task String_two_dimensional_const_table_applies_quoting()
	{
		using var h = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		h.WriteErh("st2.ERH", "#DIMS CONSTTABLE ST2, 2, 2\n");
		h.WriteCsv("ST2.csv",
			",@2,0,1\n" +
			"@1,,col0,col1\n" +
			"0,row0,\"a,b\",x\n" +
			"1,row1,y,\"\"\"q\"\"\"\n");
		await h.InitializeAsync();

		VariableToken token = Token(h, "ST2");
		Assert.Equal("a,b", token.GetStrValue(null!, [0L, 0L]));
		Assert.Equal("x", token.GetStrValue(null!, [0L, 1L]));
		Assert.Equal("y", token.GetStrValue(null!, [1L, 0L]));
		Assert.Equal("\"q\"", token.GetStrValue(null!, [1L, 1L]));
	}
}
