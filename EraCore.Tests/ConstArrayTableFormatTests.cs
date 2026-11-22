using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Script.Data;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 47：常数表一维解析/合并的格式规则（值表、CSV 引号、主表/补充表、名称表、.als）。
/// 直接调用 ConstArrayTable.Load，不经过 ERH 声明。
/// </summary>
[Collection("LoaderTests")]
public sealed class ConstArrayTableFormatTests
{
	private static ConstArrayTable.Result Load(LoaderTestHarness h, string name, int length, int kind)
	{
		ConstArrayTable.ClearFileIndex();
		ConstArrayTable.Result? result = ConstArrayTable.Load(name, [length], kind, out string error);
		Assert.NotNull(result);
		_ = error;
		return result!;
	}

	[Fact]
	public void Csv_main_and_erd_extension_merge_values_and_names()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("T.csv", "0,零,10\n1,一,\n");
		h.WriteCsv("T.erd", "1,,20\n2,二,30\n");

		ConstArrayTable.Result r = Load(h, "T", 3, ConstArrayTable.KindInt);
		long[] values = (long[])r.Values;
		Assert.Equal(10L, values[0]);
		Assert.Equal(20L, values[1]); // 主表留空 → 补充表填
		Assert.Equal(30L, values[2]);
		Assert.Equal(0, r.Names[0]["零"]);
		Assert.Equal(1, r.Names[0]["一"]);
		Assert.Equal(2, r.Names[0]["二"]);
		Assert.Equal(2, r.ConsumedPaths.Count);
	}

	[Fact]
	public void Csv_quoting_rules_apply_to_values()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("S.csv",
			"0,甲,\"a,b\"\n" +
			"1,乙,\"\"\"quoted\"\"\"\n" +
			"; comment line\n" +
			"2,丙,  spaced  \n");

		ConstArrayTable.Result r = Load(h, "S", 3, ConstArrayTable.KindStr);
		string[] values = (string[])r.Values;
		Assert.Equal("a,b", values[0]);
		Assert.Equal("\"quoted\"", values[1]);
		Assert.Equal("spaced", values[2]);
	}

	[Fact]
	public void Strict_main_content_error_falls_back_to_next_candidate()
	{
		using var h = new LoaderTestHarness();
		// 主表同一索引两个名字 → 严格校验失败，整份作废，退回 CSV/*.erd。
		h.WriteCsv("T.csv", "0,甲,1\n0,乙,2\n");
		h.WriteCsv("T.erd", "0,零,9\n");

		ConstArrayTable.Result r = Load(h, "T", 3, ConstArrayTable.KindInt);
		Assert.Contains("T.erd", r.Path);
		Assert.Equal(9L, ((long[])r.Values)[0]);
		Assert.Equal(0, r.Names[0]["零"]);
		Assert.Contains(r.Warnings, w => w.Contains("未通过严格校验"));
	}

	[Fact]
	public void Extension_bad_cell_warns_and_keeps_other_rows()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("T.csv", "0,主,1\n");
		h.WriteCsv("T.erd", "1,补,oops\n2,三,5\n");

		ConstArrayTable.Result r = Load(h, "T", 3, ConstArrayTable.KindInt);
		long[] values = (long[])r.Values;
		Assert.Equal(1L, values[0]);
		Assert.Equal(0L, values[1]); // 坏格跳过 → 保持默认值
		Assert.Equal(5L, values[2]); // 后续行照常
		Assert.Equal(1, r.Names[0]["补"]); // 名字在取值之前登记
		Assert.Contains(r.Warnings, w => w.Contains("不是有效的整数"));
	}

	[Fact]
	public void Extension_duplicate_index_name_conflict_warns_and_keeps_main()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("T.csv", "0,甲,1\n");
		h.WriteCsv("T.erd", "0,乙,2\n");

		ConstArrayTable.Result r = Load(h, "T", 3, ConstArrayTable.KindInt);
		Assert.Equal(0, r.Names[0]["甲"]);
		Assert.False(r.Names[0].ContainsKey("乙"));
		Assert.Equal(1L, ((long[])r.Values)[0]);
		Assert.Contains(r.Warnings, w => w.Contains("与主表不同"));
	}

	[Fact]
	public void Name_table_and_als_are_consumed()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("T.csv", "0,,5\n1,,6\n");
		h.WriteErd("T.ERD", "0,零\n1,一\n");
		h.WriteErd("T.als", "0,零别名\n");

		ConstArrayTable.Result r = Load(h, "T", 2, ConstArrayTable.KindInt);
		Assert.True(r.Names[0].ContainsKey("零"));
		Assert.Equal(0, r.Names[0]["零别名"]);
		Assert.Equal(1, r.Names[0]["一"]);
		Assert.Equal(5L, ((long[])r.Values)[0]);
		Assert.Contains(r.ConsumedPaths, p => p.EndsWith("T.ERD", System.StringComparison.OrdinalIgnoreCase));
		Assert.Contains(r.ConsumedPaths, p => p.EndsWith("T.csv", System.StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void Missing_table_reports_error()
	{
		using var h = new LoaderTestHarness();
		ConstArrayTable.ClearFileIndex();
		ConstArrayTable.Result? r = ConstArrayTable.Load("无此表", [2], ConstArrayTable.KindInt, out string error);
		Assert.Null(r);
		Assert.Contains("找不到同名文件", error);
	}
}
