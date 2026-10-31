using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// P3a 模块注册接缝（spec §4.3）：把「能力已存在但不可达」的断点接上。
///
/// - <see cref="DialectRegistry.BuildModule"/> 是纯函数，声明里的
///   <c>priority</c> / <c>overrides</c> / <c>hiddenKeys</c> 必须逐字段透传——
///   真实表数据当前全空，只有直接喂声明才能证明字段没有丢失；
/// - 模块容器按 id 查找，生产源码不得再出现 <c>modules.Value[</c> 位置式访问；
/// - <c>HiddenKeys</c> 只在 profile 构造期做可见面减法，**不参与 baseline 推导**。
/// </summary>
public sealed class DialectModuleBuildTests
{
	[Fact]
	public void BuildModule_passes_every_declaration_field()
	{
		var declaration = new DialectRegistry.ModuleDeclaration(
			Dialect.Snake,
			DeclaredKeys: ["FOO", "BAR"],
			HiddenKeys: ["FOO"],
			Owner: "skia",
			Priority: 5,
			Overrides: ["其它"]);

		DialectModule module = DialectRegistry.BuildModule(declaration);

		Assert.Equal(Dialect.Snake, module.Id);
		Assert.Equal("skia", module.Owner);
		Assert.Equal(5, module.Priority);
		Assert.True(module.Overrides.SetEquals(["其它"]));
		Assert.True(module.HiddenKeys.SetEquals(["FOO"]));
		Assert.True(module.DeclaredKeys.SetEquals(["FOO", "BAR"]));
	}

	[Fact]
	public void BuildModule_defaults_owner_and_optional_fields()
	{
		DialectModule module = DialectRegistry.BuildModule(new(Dialect.Eratw, ["FOO"]));

		Assert.Equal("Eratw", module.Owner);
		Assert.Equal(0, module.Priority);
		Assert.Empty(module.Overrides);
		Assert.Empty(module.HiddenKeys);
	}

	[Fact]
	public void Baseline_derivation_ignores_hidden_keys()
	{
		var declarations = new[]
		{
			new DialectRegistry.ModuleDeclaration(Dialect.Snake, ["foo"], HiddenKeys: ["base_key"]),
		};

		SortedSet<string> baseline = DialectRegistry.DeriveBaseline(["BASE_KEY", "FOO"], declarations);

		// HiddenKeys 不参与基线推导：命中基线的隐藏键仍在 baseline 中。
		Assert.Contains("BASE_KEY", baseline);
		// 只有 DeclaredKeys 参与减法。
		Assert.DoesNotContain("FOO", baseline);
	}

	[Fact]
	public void Hidden_keys_subtract_from_profile_without_mutating_declarations()
	{
		DialectModule baseline = DialectRegistry.BuildModule(new(Dialect.Baseline, ["BASE_KEY"]));
		DialectModule snake = DialectRegistry.BuildModule(new(
			Dialect.Snake,
			DeclaredKeys: ["FOO"],
			HiddenKeys: ["BASE_KEY"]));

		var profile = new DialectProfile("hidden-test", [baseline, snake]);

		Assert.Contains("BASE_KEY", baseline.DeclaredKeys);
		// 可见面按「显式 Hide 胜出」剔除。
		Assert.False(profile.IsVisible("BASE_KEY"));
		Assert.True(profile.IsVisible("FOO"));
	}

	[Fact]
	public void Production_sources_have_no_positional_module_access()
	{
		string root = RepositoryRoot();
		string[] hits = Directory
			.EnumerateFiles(Path.Combine(root, "EraCore.Core"), "*.cs", SearchOption.AllDirectories)
			.Where(HasCodeReference)
			.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToArray();

		Assert.Empty(hits);
	}

	private static readonly string[] ForbiddenTokens = ["modules.Value["];

	private static bool HasCodeReference(string path)
	{
		foreach (string line in File.ReadLines(path))
		{
			string trimmed = line.TrimStart();
			if (trimmed.StartsWith("//", StringComparison.Ordinal)
				|| trimmed.StartsWith("*", StringComparison.Ordinal)
				|| trimmed.StartsWith("/*", StringComparison.Ordinal))
				continue;
			if (ForbiddenTokens.Any(token => line.Contains(token, StringComparison.Ordinal)))
				return true;
		}
		return false;
	}

	private static string RepositoryRoot()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
				return dir.FullName;
		}
		throw new DirectoryNotFoundException("无法从测试输出目录定位仓库根（未找到 EraCore.sln）。");
	}
}
