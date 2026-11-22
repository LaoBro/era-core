using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Emuera.Headless.Tests;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game;
using Xunit;
using Cfg = MinorShift.Emuera.Runtime.Config.Config;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 45（Skia 12.2.0 / 12.1.0 / 6.2.0）：零散引擎语义对齐。
/// RAND 钳制、CHKDATA 版本、GETDISPLAYLINE 负数索引、LangManager 非日文编码往返。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0MiscEngineAlignmentTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	// ---------- 1. RAND ----------

	[Fact]
	public async Task Rand_does_not_throw_on_non_positive_or_empty_range()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		FunctionMethod rand = Method("RAND");
		Assert.Equal(0L, rand.GetIntValue(h.Exm, [new SingleLongTerm(0)]));
		Assert.Equal(0L, rand.GetIntValue(h.Exm, [new SingleLongTerm(-5)]));
		Assert.Equal(5L, rand.GetIntValue(h.Exm, [new SingleLongTerm(5), new SingleLongTerm(5)]));
		// 下界优先：RAND(3.5, 1.5) → 3.5（不是 max）。
		Assert.Equal(3.5, rand.GetFloatValue(h.Exm, [new SingleFloatTerm(3.5), new SingleFloatTerm(1.5)]), 10);
	}

	[Fact]
	public async Task Rand_token_clamps_to_zero()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		VariableToken rand = h.VEvaluator.VariableData.GetVarTokenDic()["RAND"];
		Assert.Equal(0L, rand.GetIntValue(h.Exm, [0L]));
		Assert.Equal(0L, rand.GetIntValue(h.Exm, [-3L]));
	}

	[Fact]
	public async Task Rand_float_overload_returns_value_in_range()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		FunctionMethod rand = Method("RAND");
		for (int i = 0; i < 64; i++)
		{
			double v = rand.GetFloatValue(h.Exm, [new SingleFloatTerm(1.0), new SingleFloatTerm(2.0)]);
			Assert.InRange(v, 1.0, 2.0);
			Assert.True(v < 2.0);
		}
		// 整数实参仍走整数路径。
		long i2 = rand.GetIntValue(h.Exm, [new SingleLongTerm(10)]);
		Assert.InRange(i2, 0L, 9L);
	}

	// ---------- 2. CHKDATA 版本 ----------

	/// <summary>写一个 eramaker 格式存档：0=UniqueCode（0 视为通配）、version、保存文本。</summary>
	private static string WriteEramakerSave(string path, long version)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, $"0\n{version}\nsave message\n", new UTF8Encoding(false));
		return path;
	}

	[Fact]
	public async Task Check_data_reports_save_version()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		string path = WriteEramakerSave(Path.Combine(Cfg.SavDir, "save01.sav"), 1234);

		EraDataResult result = h.VEvaluator.CheckDataByFilename(path, EraSaveFileType.Normal);
		Assert.Equal(EraDataState.OK, result.State);
		Assert.Equal("save message", result.DataMes);
		Assert.Equal(1234, result.Version);
	}

	[Fact]
	public async Task Check_data_resets_version_when_file_missing()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		EraDataResult result = h.VEvaluator.CheckDataByFilename(
			Path.Combine(Cfg.SavDir, "save99.sav"), EraSaveFileType.Normal);
		Assert.Equal(EraDataState.FILENOTFOUND, result.State);
		Assert.Equal(0, result.Version);
	}

	[Fact]
	public async Task Check_data_reports_actual_version_on_mismatch()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// 当前脚本版本 100，且不允许 9999 以下的存档 → 1234 必然不匹配。
		h.Process.GameBaseData.ScriptVersionDefined = true;
		h.Process.GameBaseData.ScriptVersion = 100;
		h.Process.GameBaseData.ScriptCompatibleMinVersion = 9999;
		string path = WriteEramakerSave(Path.Combine(Cfg.SavDir, "save02.sav"), 1234);

		EraDataResult result = h.VEvaluator.CheckDataByFilename(path, EraSaveFileType.Normal);
		Assert.Equal(EraDataState.VIRSION_ERROR, result.State);
		Assert.Equal(1234, result.Version);
	}

	[Fact]
	public async Task Chkdata_writes_version_into_result_array()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		WriteEramakerSave(Path.Combine(Cfg.SavDir, "save01.sav"), 4321);

		FunctionMethod chkdata = Method("CHKDATA");
		long state = chkdata.GetIntValue(h.Exm, [new SingleLongTerm(1)]);
		Assert.Equal((long)EraDataState.OK, state);
		Assert.Equal("save message", h.VEvaluator.RESULTS);
		Assert.Equal(4321, h.VEvaluator.RESULT_ARRAY[1]);

		// 文件不存在：状态 1，RESULT:1 保持 0。
		long missing = chkdata.GetIntValue(h.Exm, [new SingleLongTerm(9)]);
		Assert.Equal((long)EraDataState.FILENOTFOUND, missing);
		Assert.Equal(0, h.VEvaluator.RESULT_ARRAY[1]);
	}

	// ---------- 3. GETDISPLAYLINE 负数索引 ----------

	private static void FillLines(LoaderTestHarness h, int count)
	{
		h.Console.DisplayLineList.Clear();
		for (int i = 0; i < count; i++)
		{
			h.Console.DisplayLineList.Add(new ConsoleDisplayLine(
				[TestButtonFactory.CreateNonButton($"L{i}")], isLogical: true, temporary: false));
		}
	}

	[Fact]
	public async Task Get_display_line_supports_negative_index()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		FillLines(h, 3);

		FunctionMethod get = Method("GETDISPLAYLINE");
		Assert.Equal("L2", get.GetStrValue(h.Exm, [new SingleLongTerm(-1)]));
		Assert.Equal("L1", get.GetStrValue(h.Exm, [new SingleLongTerm(-2)]));
		Assert.Equal("L0", get.GetStrValue(h.Exm, [new SingleLongTerm(-3)]));
		Assert.Equal("", get.GetStrValue(h.Exm, [new SingleLongTerm(-4)]));
		Assert.Equal("", get.GetStrValue(h.Exm, [new SingleLongTerm(long.MinValue)]));
	}

	[Fact]
	public async Task Get_display_line_positive_index_is_unchanged()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		FillLines(h, 3);

		FunctionMethod get = Method("GETDISPLAYLINE");
		Assert.Equal("L0", get.GetStrValue(h.Exm, [new SingleLongTerm(0)]));
		Assert.Equal("L2", get.GetStrValue(h.Exm, [new SingleLongTerm(2)]));
		Assert.Equal("", get.GetStrValue(h.Exm, [new SingleLongTerm(3)]));

		h.Console.DisplayLineList.Clear();
		Assert.Equal("", get.GetStrValue(h.Exm, [new SingleLongTerm(0)]));
		Assert.Equal("", get.GetStrValue(h.Exm, [new SingleLongTerm(-1)]));
	}

	// ---------- 4. LangManager 非日文编码 ----------

	[Fact]
	public void Non_japanese_encoding_falls_back_to_cp932_byte_count()
	{
		LangManager.setEncode(28591); // ISO-8859-1：CJK 字符无法往返
		try
		{
			Assert.Equal(2, LangManager.GetStrlenLang("あ"));
			Assert.Equal(1, LangManager.GetUFTIndex("あい", 2));
			Assert.Equal("あ", LangManager.GetSubStringLang("あい", 0, 2));
			Assert.Equal(3, LangManager.GetStrlenLang("abc"));
		}
		finally
		{
			LangManager.setEncode(932);
		}

		Assert.Equal(2, LangManager.GetStrlenLang("あ"));
	}
}
