using System;
using System.IO;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0026 诊断旋钮四（06-K2）：privateDimList 生成期全局锁策略（auto/nolock）的壳层词表契约、
/// 锁启用决策真值表与内存覆盖钉子。
/// <list type="bullet">
/// <item>Parse/ToConfigString 是 Preferences 值与 parsescript_policy 日志 dimlock= 字段的唯一映射点——
/// "auto"/null/空/未知值一律回退 Auto（恒加锁，与 771708b 止血逐位一致）。</item>
/// <item>IsDimLockEnabled 是锁启用唯一决策点：仅 NoLock 关锁——纯取证档，关锁重开它防的
/// List 并发损坏竞态（issue 04-H1 候选判别），可致状态损坏。</item>
/// <item>旋钮无 ConfigCode 条目：游戏随包 emuera.config 写入条目无效、SaveConfig 不落盘——
/// 只有 app 壳层（MAUI 设置页诊断分组）能拨动。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class DimLockPolicyTests
{
	[Theory]
	[InlineData("auto", DimLockPolicy.Auto)]
	[InlineData(null, DimLockPolicy.Auto)]
	[InlineData("", DimLockPolicy.Auto)]
	[InlineData("bogus", DimLockPolicy.Auto)]
	[InlineData("NOLOCK", DimLockPolicy.Auto)]
	[InlineData(" nolock", DimLockPolicy.Auto)] // 词表精确匹配，带空白不算
	[InlineData("forceon", DimLockPolicy.Auto)] // 其他旋钮词表不是本旋钮词表
	[InlineData("nolock", DimLockPolicy.NoLock)]
	public void Parse_maps_wire_string_to_policy(string? value, DimLockPolicy expected)
	{
		Assert.Equal(expected, DimLockPolicyCodec.Parse(value));
	}

	[Theory]
	[InlineData(DimLockPolicy.Auto, "auto")]
	[InlineData(DimLockPolicy.NoLock, "nolock")]
	[InlineData((DimLockPolicy)99, "auto")] // 越界枚举值规范化输出 auto
	public void ToConfigString_maps_policy_to_wire_string(DimLockPolicy policy, string expected)
	{
		Assert.Equal(expected, DimLockPolicyCodec.ToConfigString(policy));
	}

	[Fact]
	public void Roundtrip_preserves_policy()
	{
		foreach (var policy in new[] { DimLockPolicy.Auto, DimLockPolicy.NoLock })
			Assert.Equal(policy, DimLockPolicyCodec.Parse(DimLockPolicyCodec.ToConfigString(policy)));
	}

	/// <summary>锁启用真值表：仅 NoLock 关锁；null/Auto（含未覆盖）恒加锁。</summary>
	[Theory]
	[InlineData(null, true)]
	[InlineData(DimLockPolicy.Auto, true)]
	[InlineData(DimLockPolicy.NoLock, false)]
	public void IsDimLockEnabled_truth_table(DimLockPolicy? policy, bool expected)
	{
		Assert.Equal(expected, DimLockPolicyCodec.IsDimLockEnabled(policy));
	}

	[Fact]
	public void Game_config_entry_cannot_set_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-dimlock-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			File.WriteAllText(Path.Combine(tempRoot, "emuera.config"), "; game-bundled knob attempt\nDimLockPolicy:nolock\n");
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));

			// 无对应 ConfigCode，loadConfig 对该键只登记告警（UnknownConfigKeys）、不改任何项——旋钮保持 null（=Auto 恒加锁）
			Assert.Null(config.OverrideDimLockPolicy);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveConfig_never_persists_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-dimlock-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));
			config.OverrideDimLockPolicy = DimLockPolicy.NoLock;

			Assert.True(config.SaveConfig());

			var saved = File.ReadAllText(Path.Combine(tempRoot, "emuera.config"));
			Assert.DoesNotContain("DimLockPolicy", saved);
			Assert.DoesNotContain("dimLockPolicy", saved);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}
}
