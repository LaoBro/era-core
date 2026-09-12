using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 3.1 CSV 查找优化：GetCharacterTemplate / GetCharacterTemplate_UseSp / GetCharacterTemplateFromCsvNo
/// 由 O(n) 线性扫描（/ BinarySearch+每次 Comparer 分配）改为 O(1) 字典索引后的语义等价回归。
/// 注意：CSV 必须在 InitializeAsync 之前写入（Initialize 内加载 CHARA*.CSV）。
/// </summary>
[Collection("LoaderTests")]
public sealed class CsvLookupTests
{
	/// <summary>两个角色各一个文件：CHARA001.CSV（csvNo=1, No=1）与 CHARA002.CSV（csvNo=2, No=2）。</summary>
	private static void WriteTwoChara(LoaderTestHarness h)
	{
		h.WriteCsv("CHARA001.CSV", "NO,1\nNAME,Alice\nCALLNAME,Alice\n");
		h.WriteCsv("CHARA002.CSV", "NO,2\nNAME,Bob\nCALLNAME,Bob\n");
	}

	[Fact]
	public async Task GetCharacterTemplate_hit_returns_template_by_no()
	{
		using var h = new LoaderTestHarness();
		WriteTwoChara(h);
		await h.InitializeAsync();

		var t = h.VEvaluator.Constant.GetCharacterTemplate(1);
		Assert.NotNull(t);
		Assert.Equal(1L, t.No);
		Assert.Equal("Alice", t.Name);
	}

	[Fact]
	public async Task GetCharacterTemplate_miss_returns_null()
	{
		using var h = new LoaderTestHarness();
		WriteTwoChara(h);
		await h.InitializeAsync();

		Assert.Null(h.VEvaluator.Constant.GetCharacterTemplate(999));
	}

	[Fact]
	public async Task AddCharacter_success_adds_character()
	{
		using var h = new LoaderTestHarness();
		WriteTwoChara(h);
		await h.InitializeAsync();

		h.VEvaluator.AddCharacter(1);
		var list = h.VEvaluator.VariableData.CharacterList;
		Assert.Single(list);
		Assert.Equal(1L, list[0].NO);
	}

	[Fact]
	public async Task AddCharacter_miss_throws()
	{
		using var h = new LoaderTestHarness();
		WriteTwoChara(h);
		await h.InitializeAsync();

		Assert.Throws<CodeEE>(() => h.VEvaluator.AddCharacter(999));
	}

	[Fact]
	public async Task GetCharacterTemplateFromCsvNo_shared_csvno_returns_min_no()
	{
		// 同 csvNo（1）跨文件：CHARA001.CSV 与 CHARA001X.CSV 均解析 csvNo=1。
		// GetFiles 按文件名排序 → CHARA001.CSV（NO,10）先加载，TryAdd 保留列表序第一个。
		using var h = new LoaderTestHarness();
		h.WriteCsv("CHARA001.CSV", "NO,10\nNAME,Ten\n");
		h.WriteCsv("CHARA001X.CSV", "NO,20\nNAME,Twenty\n");
		await h.InitializeAsync();

		var t = h.VEvaluator.Constant.GetCharacterTemplateFromCsvNo(1);
		Assert.NotNull(t);
		Assert.Equal(10L, t.No);
	}

	[Fact]
	public async Task GetCharacterTemplateFromCsvNo_zero_csvno_hit()
	{
		// 无数字后缀文件名 → csvNo=0；ADDDEFCHARA/系统初始化默认查 0。
		using var h = new LoaderTestHarness();
		h.WriteCsv("CHARA.CSV", "NO,50\nNAME,Fifty\n");
		await h.InitializeAsync();

		var t = h.VEvaluator.Constant.GetCharacterTemplateFromCsvNo(0);
		Assert.NotNull(t);
		Assert.Equal(50L, t.No);
	}

	[Fact]
	public async Task GetCharacterTemplateFromCsvNo_chara_in_subdirectory_hit()
	{
		// issues/18：角色 CSV 位于子目录（TK1.29.3 的 csv/Chara/CharaN <名>.csv）时，eReader.Filename 是
		// Config.GetFiles 的相对路径 Key（`Chara\Chara14 アリス.csv`）——若直接按第一个 "CHARA" 取数字
		// 会命中目录段 `Chara`，csvNo 恒 0（_csvNoMap 除 0 外全 miss，静默回退伪角色）。须先取末段短名。
		using var h = new LoaderTestHarness();
		h.WriteCsv(Path.Combine("Chara", "Chara14 アリス.csv"), "NO,14\nNAME,Alice\n");
		ConfigData.Current!.GetConfigItem(ConfigCode.SearchSubdirectory).SetValue(true);
		await h.InitializeAsync();

		var t = h.VEvaluator.Constant.GetCharacterTemplateFromCsvNo(14);
		Assert.NotNull(t);
		Assert.Equal(14L, t.No);
		Assert.Equal(14L, t.csvNo);

		// 反向：不再有任何模板落到 csvNo=0（目录段误匹配的旧症状）
		Assert.Null(h.VEvaluator.Constant.GetCharacterTemplateFromCsvNo(0));
	}

	[Fact]
	public async Task AddCharacterFromCsvNo_hit_and_miss_fallback()
	{
		using var h = new LoaderTestHarness();
		WriteTwoChara(h);
		await h.InitializeAsync();

		h.VEvaluator.AddCharacterFromCsvNo(1); // 命中 csvNo=1 → No=1 角色
		var list = h.VEvaluator.VariableData.CharacterList;
		Assert.Single(list);
		Assert.Equal(1L, list[0].NO);

		h.VEvaluator.AddCharacterFromCsvNo(999); // 未命中 → 不抛，回退伪角色（No=0）
		Assert.Equal(2, list.Count);
		Assert.Equal(0L, list[1].NO);
	}

	[Fact]
	public async Task GetCharacterTemplate_UseSp_ignores_sp()
	{
		using var h = new LoaderTestHarness();
		WriteTwoChara(h);
		await h.InitializeAsync();

		// sp 参数被忽略为既有语义：同一 No 的 true/false 查询返回同一实例。
		var tTrue = h.VEvaluator.Constant.GetCharacterTemplate_UseSp(1, true);
		var tFalse = h.VEvaluator.Constant.GetCharacterTemplate_UseSp(1, false);
		Assert.NotNull(tTrue);
		Assert.Same(tTrue, tFalse);
		Assert.Null(h.VEvaluator.Constant.GetCharacterTemplate_UseSp(999, true));

		// ADDSPCHARA 路径同样命中（不经指令层，无 CompatSPChara 检查）。
		h.VEvaluator.AddCharacter_UseSp(1, true);
		var list = h.VEvaluator.VariableData.CharacterList;
		Assert.Single(list);
		Assert.Equal(1L, list[0].NO);
	}

	[Fact]
	public async Task ExistCsv_and_chrfamily_via_useSp()
	{
		using var h = new LoaderTestHarness();
		WriteTwoChara(h);
		await h.InitializeAsync();

		Assert.Equal(1L, h.VEvaluator.ExistCsv(1, false));
		Assert.Equal(1L, h.VEvaluator.ExistCsv(1, true)); // sp 忽略：同结果
		Assert.Equal(0L, h.VEvaluator.ExistCsv(999, false));
		Assert.Equal(0L, h.VEvaluator.ExistCsv(0, false)); // 无 No==0 模板：不存在
		Assert.Equal("Alice", h.VEvaluator.GetCharacterStrfromCSVData(1, CharacterStrData.NAME, false, 0));
	}

	[Fact]
	public async Task duplicate_no_across_files_resolves_consistently()
	{
		// 跨文件重复 No（数据错误场景，加载期 Warn）：查询仍返回确定实例。
		using var h = new LoaderTestHarness();
		h.WriteCsv("CHARA100.CSV", "NO,7\nNAME,Seven\n");
		h.WriteCsv("CHARA200.CSV", "NO,7\nNAME,SevenB\n");
		await h.InitializeAsync();

		var t1 = h.VEvaluator.Constant.GetCharacterTemplate(7);
		var t2 = h.VEvaluator.Constant.GetCharacterTemplate(7);
		Assert.NotNull(t1);
		Assert.Same(t1, t2);

		// UseSp 与 GetCharacterTemplate 共享同一 _noMap 索引：重复 No 下返回同实例（行为漂移守护）。
		var s1 = h.VEvaluator.Constant.GetCharacterTemplate_UseSp(7, true);
		var s2 = h.VEvaluator.Constant.GetCharacterTemplate_UseSp(7, false);
		Assert.Same(t1, s1);
		Assert.Same(t1, s2);
	}
}
