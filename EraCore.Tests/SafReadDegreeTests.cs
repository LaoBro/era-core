using System;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0026 诊断旋钮二：SAF content-URI 读取并发度（int? 自由数值，钳制 1–64）的壳层词表契约、
/// 读取并发决策点数值域语义与内存覆盖钉子。
/// <list type="bullet">
/// <item>Parse/ToConfigString 是 Preferences 值与 preload_policy 日志 requested= 字段的唯一映射点——
/// "auto"/非法回退 null(auto)；数值钳制 [1,64]（下界防 0/负数，上界防手滑巨值内存爆炸）。</item>
/// <item>ResolveReadDegree 是读取并发度唯一决策点：auto 与止血值逐位一致（content-URI 每树 1）、
/// 显式值原样生效（越界钳制）、桌面文件系统路径恒不受限（-1）。</item>
/// <item>旋钮无 ConfigCode 条目：游戏随包 emuera.config 写入条目无效、SaveConfig 不落盘——
/// 并发度只有 app 壳层（MAUI 设置页诊断分组）能拨动。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class SafReadDegreeTests
{
	[Theory]
	[InlineData("auto", null)]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("bogus", null)]
	[InlineData("AUTO", null)]
	[InlineData("auto ", null)] // 词表精确匹配，带空白不算 auto → 非数值 → 回退 null
	[InlineData("on", null)]
	[InlineData("1", 1)]
	[InlineData("2", 2)]
	[InlineData("4", 4)]
	[InlineData("8", 8)]
	[InlineData("08", 8)]
	[InlineData(" 2", 2)] // int.TryParse 容忍首尾空白
	[InlineData("0", 1)] // 下界钳制
	[InlineData("-3", 1)]
	[InlineData("64", 64)]
	[InlineData("65", 64)] // 上界钳制
	[InlineData("10000", 64)]
	public void Parse_maps_wire_string_to_degree(string? value, int? expected)
	{
		Assert.Equal(expected, SafReadDegreeCodec.Parse(value));
	}

	[Theory]
	[InlineData(null, "auto")]
	[InlineData(1, "1")]
	[InlineData(8, "8")]
	[InlineData(64, "64")]
	[InlineData(0, "1")] // 规范化输出同样钳制
	[InlineData(100, "64")]
	public void ToConfigString_maps_degree_to_wire_string(int? degree, string expected)
	{
		Assert.Equal(expected, SafReadDegreeCodec.ToConfigString(degree));
	}

	[Fact]
	public void Roundtrip_preserves_degree()
	{
		foreach (var n in Enumerable.Range(SafReadDegreeCodec.MinExplicitDegree, SafReadDegreeCodec.MaxExplicitDegree))
			Assert.Equal(n, SafReadDegreeCodec.Parse(SafReadDegreeCodec.ToConfigString(n)));
		Assert.Null(SafReadDegreeCodec.Parse(SafReadDegreeCodec.ToConfigString(null)));
	}

	/// <summary>决策点数值域：auto≡1（止血值逐位一致）；显式值越界钳制；桌面路径恒 -1（不受限）。</summary>
	[Theory]
	[InlineData(null, true, 1)]
	[InlineData(1, true, 1)]
	[InlineData(3, true, 3)]
	[InlineData(8, true, 8)]
	[InlineData(64, true, 64)]
	[InlineData(0, true, 1)]
	[InlineData(100, true, 64)]
	[InlineData(null, false, -1)]
	[InlineData(3, false, -1)]
	[InlineData(64, false, -1)]
	public void ResolveReadDegree_numeric_domain(int? requested, bool isContentUri, int expected)
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
			File.WriteAllText(Path.Combine(tempRoot, "emuera.config"), "; game-bundled knob attempt\nSafReadDegree:16\n");
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));

			// 无对应 ConfigCode，loadConfig 对未知键静默跳过——旋钮保持 null（=auto 每树 1 并发）
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
			config.OverrideSafReadDegree = 16;

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
