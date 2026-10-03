using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 面快照门禁（实现票 17，spec §7 静态层）：把每个 profile 的**运行期可见面**序列化后与仓库内
/// 冻结快照逐位比对。快照变更 = 显式评审动作，必须重写文件才放行。
///
/// 重写快照：设环境变量 <c>ERACORE_DIALECT_SURFACE_UPDATE=1</c> 运行本测试（等价于票面的 <c>--update</c>）；
/// 常规运行（无该变量）执行比对。快照文件位于 <c>tools/dialect-inventory/surface.&lt;profile&gt;.txt</c>。
///
/// 证据边界：此刻 P0 方言键（票 18–24）尚未落地，snake/eratw 的快照与 v24pure 相同是**正确**状态；
/// 待 P0 落地时其差异会作为显式快照变更被评审，正是本门禁的价值。
/// </summary>
public sealed class DialectSurfaceSnapshotTests
{
	private const string UpdateEnvVar = "ERACORE_DIALECT_SURFACE_UPDATE";

	private static readonly string[] Profiles =
	[
		DialectRegistry.V24PureId,
		DialectRegistry.SnakeId,
		DialectRegistry.EratwId,
	];

	[Fact]
	public void Runtime_surface_matches_frozen_snapshot_for_every_profile()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		foreach (string profileId in Profiles)
			AssertProfileSnapshot(profileId);
	}

	[Fact]
	public void Surface_serialization_is_deterministic()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		DialectProfile profile = DialectRegistry.Resolve(DialectRegistry.EratwId);
		Assert.Equal(DialectSurface.Serialize(profile), DialectSurface.Serialize(profile));
	}

	[Fact]
	public void Profile_surfaces_are_monotone_under_module_closure()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		IReadOnlySet<SurfaceEntry> v24 = DialectSurface.EntrySet(DialectRegistry.Resolve(DialectRegistry.V24PureId));
		IReadOnlySet<SurfaceEntry> snake = DialectSurface.EntrySet(DialectRegistry.Resolve(DialectRegistry.SnakeId));
		IReadOnlySet<SurfaceEntry> eratw = DialectSurface.EntrySet(DialectRegistry.Resolve(DialectRegistry.EratwId));

		Assert.True(v24.IsSubsetOf(snake), "snake 面必须是 v24pure 面的超集。");
		Assert.True(snake.IsSubsetOf(eratw), "eratw 面必须是 snake 面的超集。");
	}

	private static void AssertProfileSnapshot(string profileId)
	{
		string actual = DialectSurface.Serialize(DialectRegistry.Resolve(profileId));
		string path = SnapshotPath(profileId);

		if (string.Equals(Environment.GetEnvironmentVariable(UpdateEnvVar), "1", StringComparison.Ordinal))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			return;
		}

		Assert.True(File.Exists(path),
			$"缺少面快照 {path}。首次生成请设 {UpdateEnvVar}=1 运行本测试。");
		Assert.Equal(Normalize(File.ReadAllText(path)), Normalize(actual));
	}

	private static string SnapshotPath(string profileId)
		=> Path.Combine(RepositoryRoot(), "tools", "dialect-inventory", $"surface.{profileId}.txt");

	/// <summary>比对前归一化行尾与 BOM，使门禁不受 git autocrlf 影响（口径仍为 UTF-8 / LF）。</summary>
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
}