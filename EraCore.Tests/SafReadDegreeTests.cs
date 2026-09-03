using System;
using System.IO;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0026 诊断旋钮二：SAF content-URI 读取并发度（SafReadDegree）的壳层词表契约、
/// 读取并发决策点真值表与内存覆盖钉子。
/// <list type="bullet">
/// <item>Parse/ToConfigString 是 Preferences 值与 preload_policy 日志 requested= 字段的唯一映射点——非法值回退 auto。</item>
/// <item>ResolveReadDegree 是读取并发度唯一决策点：Auto 与止血值逐位一致（content-URI 每树 1）、
/// 桌面文件系统路径恒不受限（-1）；requested × isContentUri 全组合穷举。</item>
/// <item>旋钮无 ConfigCode 条目：游戏随包 emuera.config 写入词表条目无效、SaveConfig 不落盘——
/// 并发度只有 app 壳层（MAUI 设置页诊断分组）能拨动。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class SafReadDegreeTests
{
	[Theory]
	[InlineData("auto", SafReadDegree.Auto)]
	[InlineData("1", SafReadDegree.One)]
	[InlineData("2", SafReadDegree.Two)]
	[InlineData("4", SafReadDegree.Four)]
	// 容错：null / 空 / 未知值 / 大小写或词表不符一律回退 auto（与旋钮一三态解析风格一致）
	[InlineData(null, SafReadDegree.Auto)]
	[InlineData("", SafReadDegree.Auto)]
	[InlineData("0", SafReadDegree.Auto)]
	[InlineData("3", SafReadDegree.Auto)]
	[InlineData("8", SafReadDegree.Auto)]
	[InlineData("bogus", SafReadDegree.Auto)]
	[InlineData("AUTO", SafReadDegree.Auto)]
	[InlineData("auto ", SafReadDegree.Auto)]
	[InlineData("on", SafReadDegree.Auto)]
	public void Parse_maps_wire_string_to_degree(string? value, SafReadDegree expected)
	{
		Assert.Equal(expected, SafReadDegreeCodec.Parse(value));
	}

	[Theory]
	[InlineData(SafReadDegree.Auto, "auto")]
	[InlineData(SafReadDegree.One, "1")]
	[InlineData(SafReadDegree.Two, "2")]
	[InlineData(SafReadDegree.Four, "4")]
	public void ToConfigString_maps_degree_to_wire_string(SafReadDegree degree, string expected)
	{
		Assert.Equal(expected, SafReadDegreeCodec.ToConfigString(degree));
	}

	[Fact]
	public void Roundtrip_preserves_degree()
	{
		foreach (SafReadDegree degree in Enum.GetValues<SafReadDegree>())
			Assert.Equal(degree, SafReadDegreeCodec.Parse(SafReadDegreeCodec.ToConfigString(degree)));
	}

	/// <summary>决策点真值表：requested（4 态）× isContentUri（2 态）全组合穷举。
	/// content-URI：Auto≡1（止血值逐位一致）、显式档位原样生效；桌面路径恒 -1（不受限）。</summary>
	[Theory]
	[InlineData(SafReadDegree.Auto, true, 1)]
	[InlineData(SafReadDegree.One, true, 1)]
	[InlineData(SafReadDegree.Two, true, 2)]
	[InlineData(SafReadDegree.Four, true, 4)]
	[InlineData(SafReadDegree.Auto, false, -1)]
	[InlineData(SafReadDegree.One, false, -1)]
	[InlineData(SafReadDegree.Two, false, -1)]
	[InlineData(SafReadDegree.Four, false, -1)]
	public void ResolveReadDegree_truth_table(SafReadDegree requested, bool isContentUri, int expected)
	{
		Assert.Equal(expected, Preload.ResolveReadDegree(requested, isContentUri));
	}

	[Fact]
	public void Game_config_entry_cannot_set_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-safdegree-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			File.WriteAllText(Path.Combine(tempRoot, "emuera.config"), "; game-bundled knob attempt\nSafReadDegree:4\n");
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));

			// 无对应 ConfigCode，loadConfig 对未知键静默跳过——旋钮保持 null（=Auto 每树 1 并发）
			Assert.Null(config.OverrideSafReadDegree);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveConfig_never_persists_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-safdegree-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));
			config.OverrideSafReadDegree = SafReadDegree.Four;

			Assert.True(config.SaveConfig());

			var saved = File.ReadAllText(Path.Combine(tempRoot, "emuera.config"));
			Assert.DoesNotContain("SafReadDegree", saved);
			Assert.DoesNotContain("safReadDegree", saved);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}
}
