using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 边界不变量（ADR-0033/0040/0041~0048）：<c>Shared/Runtime/Script/Loader/</c> 下的**模块**不得
/// 引用 <c>ErbLoader</c> —— 依赖方向只能是 façade → 模块，反向即环。
/// <para>
/// 这是 2026-10-18 全轨边界机检的**机读形态**。机检当时是手工快照（矩阵见 handoff），
/// 没有守卫；本测试把那次的结论变成不变量，防止"某天有人图省事在模块里写一句
/// <c>ErbLoader.SomeStatic()</c>"把零反向依赖悄悄打破。
/// </para>
/// <para>
/// <b>实现要点：先剔注释与字符串字面量再匹配。</b>两处刻意保留的 <c>"ErbLoader"</c>
/// 日志标签（<c>ErbParseDiagnostics</c> 的 <c>materialize_diag</c>、排障脚本按它 grep）是字符串，
/// 不算类型引用；若不剔字符串会把它们误判为违规。
/// </para>
/// </summary>
public class ErbLoaderModuleBoundaryTests
{
	[Fact]
	public void Loader_modules_do_not_reference_the_ErbLoader_host_type()
	{
		var dir = Path.Combine(FindRepositoryRoot(), "EraCore.Core", "Shared", "Runtime", "Script", "Loader");
		Assert.True(Directory.Exists(dir), $"loader directory not found: {dir}");

		var offenders = new List<string>();
		foreach (var path in Directory.GetFiles(dir, "*.cs").OrderBy(p => p, StringComparer.Ordinal))
		{
			string name = Path.GetFileName(path);
			if (name == "ErbLoader.cs")
				continue; // façade 本身就是宿主，允许引用自己

			var code = StripCommentsAndStringLiterals(File.ReadAllText(path));
			if (Regex.IsMatch(code, @"\bErbLoader\b"))
				offenders.Add(name);
		}

		Assert.True(offenders.Count == 0,
			"以下模块引用了宿主类型 ErbLoader（依赖方向反了，会形成环）：" + string.Join(", ", offenders)
			+ "。模块应只经 readonly struct Context 或委托取得依赖——见 ADR-0033/0044/0047。");
	}

	[Fact]
	public void Guard_detects_a_deliberate_violation()
	{
		// 自检：确保剥离逻辑没有把规则弱化成"永远通过"
		var code = StripCommentsAndStringLiterals("var x = ErbLoader.TryGetLoadScanLine(); // ErbLoader in comment\n");
		Assert.Matches(@"\bErbLoader\b", code);

		var benign = StripCommentsAndStringLiterals("EmueraLog.Info(\"ErbLoader\", \"materialize_diag\"); // ErbLoader\n/// ErbLoader\n");
		Assert.DoesNotMatch(@"\bErbLoader\b", benign);
	}

	/// <summary>剔除注释行（`//` `///` `*` `/*`）与字符串字面量内容，只留代码骨架。</summary>
	private static string StripCommentsAndStringLiterals(string source)
	{
		var kept = source.Split('\n')
			.Select(l => l.TrimStart())
			.Where(l => !(l.StartsWith("//", StringComparison.Ordinal)
				|| l.StartsWith("///", StringComparison.Ordinal)
				|| l.StartsWith("*", StringComparison.Ordinal)
				|| l.StartsWith("/*", StringComparison.Ordinal)));
		var joined = string.Join("\n", kept);
		// 先剔字符串字面量，再剔行尾注释（顺序不能反：否则 `"http://…"` 之类会被当注释切开）
		var noStrings = Regex.Replace(joined, "\"[^\"\n]*\"", "\"\"");
		return Regex.Replace(noStrings, @"//.*$", "", RegexOptions.Multiline);
	}

	private static string FindRepositoryRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir != null)
		{
			if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
				return dir.FullName;
			dir = dir.Parent;
		}
		throw new InvalidOperationException("repository root (EraCore.sln) not found from " + AppContext.BaseDirectory);
	}
}
