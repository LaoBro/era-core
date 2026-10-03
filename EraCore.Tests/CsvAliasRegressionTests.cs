using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// CSV <c>.als</c> 别名表回归（29 号票）。
///
/// 背景：eraTW 画蛇添足版把中文关键字放在 <c>CSV/&lt;VAR&gt;.als</c> 里，且大量使用
/// **多个名字映射到同一索引**（日文/简体/繁体三名一索引）。此前 <c>ConstantData.loadAliases</c>
/// 在重复索引处用 <c>string.Format(VarKeyAreadyDefined, index)</c> 只传了一个参数，而模板含两个
/// 占位符 → 抛 <c>FormatException</c> → 被外层 catch 吞掉并 <c>return</c> → 该 .als 剩余别名全部
/// 丢失（整包中文关键字失解析，实测 746 条 L2 告警）。
///
/// 本测试锁定：含重复索引、且重复索引出现在文件前部的 .als，其**后续**别名仍必须全部生效。
/// </summary>
public sealed class CsvAliasRegressionTests
{
	[Fact]
	public async Task Aliases_with_duplicate_indices_all_resolve()
	{
		using var h = new LoaderTestHarness();
		// CSV 关键字表：只给日文名（与 eraTW 现状一致——中文名只在 .als 里）。
		h.WriteCsv("CFLAG.CSV", "1,既成事実\n12,デート中\n");
		// .als：前两条即重复索引（1 出现两次）——旧实现在此抛 FormatException 后中断，
		// 后续 index=12 的「约会中」会整条丢失。
		h.WriteCsv("CFLAG.als", "1,既成事实\n1,既成事實\n12,约会中\n12,约会中繁体\n");

		await h.InitializeAsync();

		var dic = GlobalStatic.ConstantData.GetKeywordDictionary(out _, VariableCode.CFLAG, -1, null!);

		Assert.NotNull(dic);
		// 重复索引处的两条别名（且位于中断点之前）
		Assert.Equal(1, dic["既成事实"]);
		Assert.Equal(1, dic["既成事實"]);
		// 关键：中断点**之后**的别名也必须存在（旧实现丢失的正是这些）
		Assert.Equal(12, dic["约会中"]);
		Assert.Equal(12, dic["约会中繁体"]);
		// CSV 原生关键字不受影响
		Assert.Equal(12, dic["デート中"]);
	}

	[Fact]
	public async Task Alias_file_with_duplicate_name_does_not_abort_loading()
	{
		using var h = new LoaderTestHarness();
		h.WriteCsv("CFLAG.CSV", "1,既成事実\n");
		// 同一名字重复（会让 Dictionary.Add 抛 ArgumentException）——必须跳过而非中断，
		// 后续别名仍要生效。
		h.WriteCsv("CFLAG.als", "1,既成事实\n1,既成事实\n2,信赖度\n");

		await h.InitializeAsync();

		var dic = GlobalStatic.ConstantData.GetKeywordDictionary(out _, VariableCode.CFLAG, -1, null!);

		Assert.NotNull(dic);
		Assert.Equal(1, dic["既成事实"]);
		Assert.Equal(2, dic["信赖度"]);
	}
}
