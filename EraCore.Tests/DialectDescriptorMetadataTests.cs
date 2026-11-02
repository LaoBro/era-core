using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 方言 descriptor 移植元数据的反漂移门禁（spec §4.1 / §7，ADR-0053）。
///
/// 三条纪律：
/// - <c>Family</c> 非空时必须来自 canonical 集合（族是检索分组，新增家族 = 改这里的列表一行）；
/// - <c>Status != Semantic</c> ⇔ <c>docs/reference/dialect-surface.md</c> 的「已知差异」区点名该键；
/// - <c>Status != Semantic</c> 或 <c>Dialect.Eratw</c> 的键必须有 <c>Note</c>。
///
/// 「点名」的约定：已知差异区内用反引号书写的全大写标识符，且该标识符是已登记的 descriptor。
/// 因此 baseline 键（如 `EXISTVAR`）与本区散文里的文件名不会误入判定；反向亦成立——已登记
/// descriptor 一旦出现在该区，就必须是非 <see cref="PortStatus.Semantic"/> 状态。T2 回填时
/// 需在该区逐键点名全部非真语义键（通配符不算点名）。
///
/// 证据边界：本票（T1）只加字段与门禁，不做回填；因此三条断言在空集合上放行，
/// 真正的约束力在 T2 回填后生效。
/// </summary>
public sealed class DialectDescriptorMetadataTests
{
	/// <summary>canonical Family 集合（spec §4.2 初值 10）。</summary>
	private static readonly IReadOnlySet<string> CanonicalFamilies = new HashSet<string>(StringComparer.Ordinal)
	{
		"NF 输入",
		"动态调用",
		"图像层",
		"HTML 文本",
		"数学",
		"位运算",
		"字符串求值",
		"MAP",
		"数组搜索",
		"平台定时器",
		"SQL",
	};

	[Fact]
	public void Family_is_null_or_canonical()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		foreach (DescriptorView descriptor in AllDescriptors())
		{
			if (descriptor.Family is null)
				continue;
			Assert.False(string.IsNullOrWhiteSpace(descriptor.Family),
				$"{descriptor.Kind}「{descriptor.Name}」的 Family 为空白；未归类请留 null。");
			Assert.Contains(descriptor.Family, CanonicalFamilies);
		}
	}

	[Fact]
	public void Non_semantic_status_matches_known_differences_section()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		List<DescriptorView> descriptors = AllDescriptors();
		HashSet<string> descriptorNames = new(descriptors.Select(d => d.Name), StringComparer.Ordinal);

		string[] nonSemantic = descriptors
			.Where(d => d.Status != PortStatus.Semantic)
			.Select(d => d.Name)
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();
		string[] documented = DocumentedKnownDifferenceKeys()
			.Where(descriptorNames.Contains)
			.OrderBy(name => name, StringComparer.Ordinal)
			.ToArray();

		Assert.True(documented.SequenceEqual(nonSemantic),
			$"「已知差异」区点名的方言键 [{string.Join(", ", documented)}] 必须恰好等于" +
			$"非 Semantic 状态键 [{string.Join(", ", nonSemantic)}]。");
	}

	[Fact]
	public void Non_semantic_and_eratw_descriptors_carry_a_note()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		foreach (DescriptorView descriptor in AllDescriptors())
		{
			if (descriptor.Status == PortStatus.Semantic && descriptor.Dialect != Dialect.Eratw)
				continue;
			Assert.False(string.IsNullOrWhiteSpace(descriptor.Note),
				$"{descriptor.Kind}「{descriptor.Name}」的状态是 {descriptor.Status}（dialect={descriptor.Dialect}），必须写 Note。");
		}
	}

	private readonly record struct DescriptorView(
		string Name,
		string Kind,
		Dialect Dialect,
		string? Family,
		PortStatus Status,
		string? Note);

	private static List<DescriptorView> AllDescriptors()
	{
		List<DescriptorView> descriptors = [];
		foreach (InstructionDescriptor descriptor in BuiltInRegistry.Instructions)
		{
			descriptors.Add(new DescriptorView(
				descriptor.Name,
				"instruction",
				descriptor.Dialect,
				descriptor.Family,
				descriptor.Status,
				descriptor.Note));
		}
		foreach (FunctionMethodDescriptor descriptor in BuiltInRegistry.Methods)
		{
			descriptors.Add(new DescriptorView(
				descriptor.Name,
				"function",
				descriptor.Dialect,
				descriptor.Family,
				descriptor.Status,
				descriptor.Note));
		}
		return descriptors;
	}

	/// <summary>
	/// 从 dialect-surface.md 的「已知差异」小节收集反引号里的全大写标识符。
	/// 调用方再与已登记 descriptor 名称求交，避免把 baseline 键（如 EXISTVAR）与文件路径误算进来。
	/// </summary>
	private static IReadOnlySet<string> DocumentedKnownDifferenceKeys()
	{
		string path = Path.Combine(RepositoryRoot(), "docs", "reference", "dialect-surface.md");
		Assert.True(File.Exists(path), $"缺少文档 {path}。");

		Regex heading = new(@"^(#+)\s", RegexOptions.CultureInvariant);
		Regex identifier = new(@"`([A-Z][A-Z0-9_]*)`", RegexOptions.CultureInvariant);
		HashSet<string> keys = new(StringComparer.Ordinal);

		int sectionLevel = -1;
		foreach (string line in File.ReadAllLines(path))
		{
			Match match = heading.Match(line);
			if (sectionLevel < 0)
			{
				if (match.Success && line.Contains("已知差异", StringComparison.Ordinal))
					sectionLevel = match.Groups[1].Length;
				continue;
			}
			if (match.Success && match.Groups[1].Length <= sectionLevel)
				break;
			foreach (Match key in identifier.Matches(line))
				keys.Add(key.Groups[1].Value);
		}

		Assert.True(sectionLevel > 0, "dialect-surface.md 缺少「已知差异」区。");
		return keys;
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
