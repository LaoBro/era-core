using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 方言键索引生成物门禁（spec §5）。把**全部注册键**序列化为
/// <c>docs/reference/dialect-keys.md</c> 并与仓库内冻结产物逐位比对。
///
/// - 方言键行：键 / kind / 模块 / 家族 / 状态 / 说明，元数据来自 descriptor；
/// - baseline 行：键 / kind / baseline / 处理体类型名，无元数据（旧指令检索表 + 远期迁移清单）。
///
/// 更新复用面快照的单一开关：设 <c>ERACORE_DIALECT_SURFACE_UPDATE=1</c> 运行本测试即重写产物；
/// 常规运行执行比对，任何未授权改动都会红。
/// </summary>
public sealed class DialectKeyIndexTests
{
	private const string UpdateEnvVar = "ERACORE_DIALECT_SURFACE_UPDATE";
	private const string ArtifactRelativePath = "docs/reference/dialect-keys.md";
	private const string BaselineModule = "baseline";
	private static readonly string Placeholder = "—";

	[Fact]
	public void Index_covers_every_registered_key()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		List<KeyIndexRow> rows = BuildRows();
		IReadOnlyList<SurfaceEntry> surface = DialectSurface.Entries(DialectRegistry.Resolve(DialectRegistry.EratwId));

		Assert.Equal(surface.Count, rows.Count);
		int dialectRows = rows.Count(row => row.Module != BaselineModule);
		int baselineRows = rows.Count(row => row.Module == BaselineModule);
		Assert.True(dialectRows == 70, $"方言键应为 70 行，实际 {dialectRows}；新增/删除键时同步更新本断言与 dialect-surface.md §1。");
		Assert.True(baselineRows == 567, $"baseline 应为 567 行，实际 {baselineRows}；注册面变化时同步更新本断言与 dialect-surface.md §1。");
		Assert.DoesNotContain(rows.Where(row => row.Module != BaselineModule), row => row.Family == Placeholder);
	}

	[Fact]
	public void Key_index_matches_frozen_artifact()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		string actual = Render(BuildRows());
		string path = Path.Combine(RepositoryRoot(), ArtifactRelativePath);

		if (string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.Ordinal))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return;
		}

		Assert.True(File.Exists(path),
			$"缺少键索引 {path}。首次生成请设 {UpdateEnvVar}=1 运行本测试。");
		Assert.Equal(Normalize(File.ReadAllText(path)), Normalize(actual));
	}

	private static List<KeyIndexRow> BuildRows()
	{
		Dictionary<string, InstructionDescriptor> instructionMeta = BuiltInRegistry.Instructions
			.ToDictionary(descriptor => descriptor.Name, StringComparer.Ordinal);
		Dictionary<string, FunctionMethodDescriptor> methodMeta = BuiltInRegistry.Methods
			.ToDictionary(descriptor => descriptor.Name, StringComparer.Ordinal);
		Dictionary<string, FunctionIdentifier> instructions = FunctionIdentifier.GetInstructionNameDic();
		IReadOnlyDictionary<string, FunctionMethod> methods = FunctionMethodCreator.GetMethodList();

		List<KeyIndexRow> rows = [];
		foreach (SurfaceEntry entry in DialectSurface.Entries(DialectRegistry.Resolve(DialectRegistry.EratwId)))
		{
			if (entry.Kind == DialectSurface.DirectiveKind && instructionMeta.TryGetValue(entry.Name, out InstructionDescriptor? instruction))
			{
				rows.Add(DialectRow(instruction.Name, entry.Kind, instruction.Dialect, instruction.Family, instruction.Status, instruction.Note));
				continue;
			}
			if (entry.Kind == DialectSurface.FunctionKind && methodMeta.TryGetValue(entry.Name, out FunctionMethodDescriptor? method))
			{
				rows.Add(DialectRow(method.Name, entry.Kind, method.Dialect, method.Family, method.Status, method.Note));
				continue;
			}

			FunctionIdentifier registered = instructions[entry.Name];
			string handler = entry.Kind == DialectSurface.DirectiveKind
				? registered.Instruction is not null ? registered.Instruction.GetType().Name : $"FunctionCode.{registered.Code}"
				: methods[entry.Name].GetType().Name;
			rows.Add(new KeyIndexRow(entry.Name, entry.Kind, BaselineModule, Placeholder, Placeholder, Placeholder, handler));
		}
		return rows;
	}

	private static KeyIndexRow DialectRow(
		string name,
		string kind,
		Dialect dialect,
		string? family,
		PortStatus status,
		string? note)
		=> new(
			name,
			kind,
			ModuleId(dialect),
			family ?? Placeholder,
			status.ToString(),
			note ?? Placeholder,
			Placeholder);

	private static string ModuleId(Dialect dialect) => dialect switch
	{
		Dialect.Snake => DialectRegistry.SnakeId,
		Dialect.Eratw => DialectRegistry.EratwId,
		_ => BaselineModule,
	};

	private static string Render(IReadOnlyList<KeyIndexRow> rows)
	{
		StringBuilder builder = new();
		builder.Append("# 方言键索引（生成物）\n\n");
		builder.Append("> 本文件由 `EraCore.Tests/DialectKeyIndexTests.cs` 生成，请勿手工编辑。\n");
		builder.Append($"> 重新生成：设 `{UpdateEnvVar}=1` 运行该测试（与面快照共用开关）。\n\n");
		builder.Append("方言键行来自 descriptor 的 `Family` / `Status` / `Note`；baseline 行不含元数据，");
		builder.Append("处理体列给出运行期类型（结构化指令没有 `AInstruction` 实例时退化为 `FunctionCode` 派发名）。\n\n");

		builder.Append("## 方言键\n\n");
		builder.Append("| 键 | kind | 模块 | 家族 | 状态 | 说明 |\n");
		builder.Append("|---|---|---|---|---|---|\n");
		foreach (KeyIndexRow row in rows.Where(row => row.Module != BaselineModule))
			builder.Append($"| `{row.Name}` | {row.Kind} | {row.Module} | {row.Family} | {row.Status} | {row.Note} |\n");

		builder.Append("\n## baseline\n\n");
		builder.Append("| 键 | kind | 模块 | 处理体 |\n");
		builder.Append("|---|---|---|---|\n");
		foreach (KeyIndexRow row in rows.Where(row => row.Module == BaselineModule))
			builder.Append($"| `{row.Name}` | {row.Kind} | {row.Module} | {row.Handler} |\n");

		return builder.ToString();
	}

	private static string Normalize(string text)
		=> text.Replace("\r\n", "\n").TrimStart('\uFEFF');

	private static string RepositoryRoot()
	{
		for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
		{
			if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
				return dir.FullName;
		}
		throw new DirectoryNotFoundException("无法从测试输出目录定位仓库根（未找到 EraCore.sln）。");
	}

	private readonly record struct KeyIndexRow(
		string Name,
		string Kind,
		string Module,
		string Family,
		string Status,
		string Note,
		string Handler);
}
