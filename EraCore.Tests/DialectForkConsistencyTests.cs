using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// fork 键决策与运行期面快照的一致性硬门禁（spec §6.1 / issue 03）。
///
/// fork JSON 是数据：已进面快照的键再标 <c>pending</c> / <c>excluded</c> 就是双真相源矛盾，
/// 本测试让 <c>dotnet test</c> 变红。反过来，已 ported 的键**不写** <c>status</c> 是常态——
/// ported 由面快照推导（ADR-0053），任何文件不得显式书写。
/// </summary>
public sealed class DialectForkConsistencyTests
{
	private static readonly string[] AllowedStatuses = ["pending", "excluded", "out-of-scope"];
	private static readonly string[] SurfaceFiles = ["surface.snake.txt", "surface.eratw.txt"];

	[Fact]
	public void Ported_keys_are_not_still_marked_pending_or_excluded()
	{
		HashSet<(string Name, string Kind)> surface = ReadSurfaceKeys();
		List<string> contradictions = [];

		foreach (ForkAddition addition in AllAdditions())
		{
			if (!surface.Contains((addition.Name, addition.Kind)))
				continue;
			if (addition.Status is "pending" or "excluded")
				contradictions.Add($"{addition.ForkId}: {addition.Name} ({addition.Kind}) status={addition.Status}");
		}

		Assert.True(contradictions.Count == 0,
			"已进面快照的键不能再标 pending/excluded（ported 由面快照推导）：\n  "
			+ string.Join("\n  ", contradictions.OrderBy(line => line, StringComparer.Ordinal)));
	}

	[Fact]
	public void Fork_status_values_use_the_known_vocabulary()
	{
		List<string> invalid = [];

		foreach (ForkAddition addition in AllAdditions())
		{
			if (addition.Status is not null && !AllowedStatuses.Contains(addition.Status))
				invalid.Add($"{addition.ForkId}: {addition.Name} status={addition.Status}");
			if (addition.Status == "excluded" && string.IsNullOrWhiteSpace(addition.Note))
				invalid.Add($"{addition.ForkId}: {addition.Name} status=excluded 缺 note");
		}

		Assert.True(invalid.Count == 0,
			$"fork status 只允许 {string.Join(" / ", AllowedStatuses)}（ported 不得显式书写），"
			+ "且 excluded 必须在 note 写明理由：\n  "
			+ string.Join("\n  ", invalid.OrderBy(line => line, StringComparer.Ordinal)));
	}

	private readonly record struct ForkAddition(string ForkId, string Name, string Kind, string? Status, string? Note);

	private static List<ForkAddition> AllAdditions()
	{
		List<ForkAddition> additions = [];
		string directory = Path.Combine(RepositoryRoot(), "dialects");
		Assert.True(Directory.Exists(directory), $"缺少 dialects 目录 {directory}。");

		foreach (string path in Directory.EnumerateFiles(directory, "*.json").OrderBy(p => p, StringComparer.Ordinal))
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
			JsonElement root = document.RootElement;
			string forkId = root.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(path);
			if (!root.TryGetProperty("additions", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
				continue;

			foreach (JsonElement item in items.EnumerateArray())
			{
				additions.Add(new ForkAddition(
					forkId,
					RequiredString(item, "name", forkId),
					RequiredString(item, "kind", forkId),
					OptionalStatus(item, forkId),
					OptionalNote(item, forkId)));
			}
		}
		return additions;
	}

	private static string RequiredString(JsonElement element, string property, string forkId)
	{
		if (!element.TryGetProperty(property, out JsonElement value) || value.ValueKind != JsonValueKind.String)
			throw new XunitException($"fork「{forkId}」的 additions 项缺字符串字段 {property}：{element}");
		return value.GetString()!;
	}

	private static string? OptionalStatus(JsonElement element, string forkId)
	{
		if (!element.TryGetProperty("status", out JsonElement value))
			return null;
		if (value.ValueKind != JsonValueKind.String)
			throw new XunitException($"fork「{forkId}」的 additions 项 status 不是字符串：{element}");
		return value.GetString();
	}

	private static string? OptionalNote(JsonElement element, string forkId)
	{
		if (!element.TryGetProperty("note", out JsonElement value) || value.ValueKind == JsonValueKind.Null)
			return null;
		if (value.ValueKind != JsonValueKind.String)
			throw new XunitException($"fork「{forkId}」的 additions 项 note 不是字符串：{element}");
		return value.GetString();
	}

	private static HashSet<(string Name, string Kind)> ReadSurfaceKeys()
	{
		HashSet<(string Name, string Kind)> keys = [];

		foreach (string file in SurfaceFiles)
		{
			string path = Path.Combine(RepositoryRoot(), "tools", "dialect-inventory", file);
			Assert.True(File.Exists(path), $"缺少面快照 {path}。");

			foreach (string line in File.ReadLines(path))
			{
				if (string.IsNullOrWhiteSpace(line))
					continue;
				string[] parts = line.Split('\t');
				Assert.True(parts.Length == 2 && parts[1] is ("directive" or "function"),
					$"面快照 {file} 行格式非法：{line}");
				keys.Add((parts[0], parts[1]));
			}
		}
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
