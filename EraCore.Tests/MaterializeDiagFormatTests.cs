using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera.Runtime.Script.Loader;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0041：<c>materialize_diag</c> 行的<b>字段名与顺序</b>锁。
/// <para>
/// 这一行是 D 组里唯一没有测试保护的对外形状——真机排障按字段名 grep（如 <c>loop_overhead_ms</c>、
/// <c>fallbackSrc</c> 一类对账口径）。搬家时若误改一个字段名或调换顺序，编译与既有测试都不会失败，
/// 只有人肉读日志才会发现。故在 C1（诊断外移）之前先把形状钉住。
/// </para>
/// <para>
/// 断言只锁"token 序列"与"计数类字段的取值"，<b>不锁时间列的具体数字</b>——那些来自全局
/// <c>ParserProbe</c> 计数器与 <c>Stopwatch.Frequency</c>，跨测试不稳定。
/// </para>
/// </summary>
public class MaterializeDiagFormatTests
{
	[Fact]
	public void Diag_line_keeps_field_names_and_order()
	{
		var line = ErbParseDiagnostics.FormatMaterializeDiag(new MaterializeDiag(), 0, 0, 0, 0);
		var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Select(t => t.Contains('=') ? t[..t.IndexOf('=')] : t)
			.ToList();

		Assert.Equal(ExpectedTokens, tokens);
	}

	[Fact]
	public void Diag_line_carries_the_counts_it_was_given()
	{
		var diag = new MaterializeDiag
		{
			Labels = 3,
			SignatureLabels = 1,
			Lines = 10,
			LabelLines = 2,
			DollarLines = 4,
			SharpLines = 5,
			SigCalls = 7,
		};
		var line = ErbParseDiagnostics.FormatMaterializeDiag(diag, 0, 0, 0, 0);

		Assert.StartsWith("materialize_diag labels=3 sigLabels=1 lines=10 labelLines=2 dollarLines=4 sharpLines=5", line);
		Assert.Contains("sigCalls=7", line);
		Assert.Contains("blobReads=0 blobBytes=0", line);
	}

	/// <summary>
	/// 字段顺序的完整期望（含两个不带 <c>=</c> 的头 token：<c>materialize_diag</c> 与 <c>allocMB</c>）。
	/// 改这里等于改对外形状——必须同时在 ADR 与排障文档里登记。
	/// </summary>
	private static readonly List<string> ExpectedTokens =
	[
		"materialize_diag",
		"labels", "sigLabels",
		"lines", "labelLines", "dollarLines", "sharpLines",
		"total_ms", "read_ms", "tree_ms",
		"parseLabel_ms", "three_ms",
		"setarg_ms", "nest_ms", "jump_ms",
		"sig_ms", "sigCalls", "sigTotal_ms",
		"lexTree_ms", "lexThree_ms", "lexSig_ms", "lexLabel_ms", "lexNone_ms",
		"varTree_ms", "varThree_ms", "varSig_ms", "varLabel_ms", "varNone_ms",
		"allocMB",
		"read", "tree", "label", "three", "sigInThree",
		"blob_ms", "blobInSeg_ms", "blobReads", "blobBytes",
		"main_wall_ms", "loop_overhead_ms",
	];
}
