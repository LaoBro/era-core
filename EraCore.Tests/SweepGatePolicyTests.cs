using MinorShift.Emuera.Files;
using System;
using System.IO;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0026 诊断旋钮一：平台门策略（SweepGatePolicy）的壳层词表契约与内存覆盖钉子。
/// <list type="bullet">
/// <item>Parse/ToConfigString 是 Preferences 值与 parsescript_policy 日志 policy= 字段的唯一映射点——非法值回退 auto。</item>
/// <item>旋钮无 ConfigCode 条目：游戏随包 emuera.config 写入词表条目无效、SaveConfig 不落盘——
/// 止血门只有 app 壳层（MAUI 设置页诊断分组）能拨动。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class SweepGatePolicyTests
{
	[Theory]
	[InlineData("auto", SweepGatePolicy.Auto)]
	[InlineData("forceon", SweepGatePolicy.ForceOn)]
	[InlineData("forceoff", SweepGatePolicy.ForceOff)]
	// 容错：null / 空 / 未知值 / 大小写或词表不符一律回退 auto（与既有三态解析风格一致）
	[InlineData(null, SweepGatePolicy.Auto)]
	[InlineData("", SweepGatePolicy.Auto)]
	[InlineData("bogus", SweepGatePolicy.Auto)]
	[InlineData("ForceOn", SweepGatePolicy.Auto)]
	[InlineData("force on", SweepGatePolicy.Auto)]
	[InlineData("on", SweepGatePolicy.Auto)]
	public void Parse_maps_wire_string_to_policy(string? value, SweepGatePolicy expected)
	{
		Assert.Equal(expected, SweepGatePolicyCodec.Parse(value));
	}

	[Theory]
	[InlineData(SweepGatePolicy.Auto, "auto")]
	[InlineData(SweepGatePolicy.ForceOn, "forceon")]
	[InlineData(SweepGatePolicy.ForceOff, "forceoff")]
	public void ToConfigString_maps_policy_to_wire_string(SweepGatePolicy policy, string expected)
	{
		Assert.Equal(expected, SweepGatePolicyCodec.ToConfigString(policy));
	}

	[Fact]
	public void Roundtrip_preserves_policy()
	{
		foreach (SweepGatePolicy policy in Enum.GetValues<SweepGatePolicy>())
			Assert.Equal(policy, SweepGatePolicyCodec.Parse(SweepGatePolicyCodec.ToConfigString(policy)));
	}

	[Fact]
	public void Game_config_entry_cannot_set_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-sweeppolicy-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			File.WriteAllText(Path.Combine(tempRoot, "emuera.config"), "; game-bundled knob attempt\nSweepGatePolicy:forceon\n");
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));

			// 无对应 ConfigCode，loadConfig 对未知键静默跳过——旋钮保持 null（=Auto 跟随平台门）
			Assert.Null(config.OverrideSweepGatePolicy);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveConfig_never_persists_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-sweeppolicy-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));
			config.OverrideSweepGatePolicy = SweepGatePolicy.ForceOn;

			Assert.True(config.SaveConfig());

			var saved = File.ReadAllText(Path.Combine(tempRoot, "emuera.config"));
			Assert.DoesNotContain("SweepGatePolicy", saved);
			Assert.DoesNotContain("forceon", saved);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}
}
