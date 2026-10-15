using System;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0026 诊断旋钮三（06-K1）：并行生成度上限（闭集词表 auto/4/8/16）的壳层词表契约、
/// 决策点真值表与内存覆盖钉子。
/// <list type="bullet">
/// <item>Parse/ToConfigString 是 Preferences 值与 LoadPendingParallel/parsescript_policy 日志
/// knob= 字段的唯一映射点——"auto"/非法/词表外回退 null(auto)；闭集精确匹配（"08" 不算 8）。</item>
/// <item>度数决策点已随第一刀（ADR-0027 决策二.1）迁入 <c>ErbLoadStrategy</c>（FileWindow / SweepDegree
/// 同源同值，真值表穷举于 <c>ErbLoadStrategyTests</c>）——本类只保留壳层词表契约与内存覆盖钉子。</item>
/// <item>旋钮无 ConfigCode 条目：游戏随包 emuera.config 写入条目无效、SaveConfig 不落盘——
/// 只有 app 壳层（MAUI 设置页诊断分组）能拨动。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class MaxParallelErbFilesTests
{
	[Theory]
	[InlineData("auto", null)]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("bogus", null)]
	[InlineData("AUTO", null)]
	[InlineData("on", null)]
	[InlineData("2", null)] // 词表外数值回退 auto（闭集词表，非自由数值）
	[InlineData("3", null)]
	[InlineData("32", null)]
	[InlineData("08", null)] // 闭集精确匹配，前导零不算 8
	[InlineData(" 8", null)]
	[InlineData("4", 4)]
	[InlineData("8", 8)]
	[InlineData("16", 16)]
	public void Parse_maps_wire_string_to_cap(string? value, int? expected)
	{
		Assert.Equal(expected, MaxParallelErbFilesCodec.Parse(value));
	}

	[Theory]
	[InlineData(null, "auto")]
	[InlineData(4, "4")]
	[InlineData(8, "8")]
	[InlineData(16, "16")]
	[InlineData(2, "auto")] // 词表外值规范化输出 auto（闭集）
	[InlineData(17, "auto")]
	[InlineData(0, "auto")]
	public void ToConfigString_maps_cap_to_wire_string(int? cap, string expected)
	{
		Assert.Equal(expected, MaxParallelErbFilesCodec.ToConfigString(cap));
	}

	[Fact]
	public void Roundtrip_preserves_cap()
	{
		foreach (var n in new[] { 4, 8, 16 })
			Assert.Equal(n, MaxParallelErbFilesCodec.Parse(MaxParallelErbFilesCodec.ToConfigString(n)));
		Assert.Null(MaxParallelErbFilesCodec.Parse(MaxParallelErbFilesCodec.ToConfigString(null)));
	}

	[Fact]
	public void Game_config_entry_cannot_set_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-erbcap-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			File.WriteAllText(Path.Combine(tempRoot, "emuera.config"),
				"; game-bundled knob attempt\nMaxParallelErbFiles:16\nDimLockPolicy:nolock\n");
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));

			// 无对应 ConfigCode，loadConfig 对未知键静默跳过——旋钮保持 null（=auto）
			Assert.Null(config.OverrideMaxParallelErbFiles);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}

	[Fact]
	public void SaveConfig_never_persists_the_knob()
	{
		var tempRoot = Path.Combine(Path.GetTempPath(), "emuera-erbcap-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempRoot);
		try
		{
			GamePaths.Resolve(tempRoot, new FileSystemGameDirAccessor());

			var config = new ConfigData();
			using var globals = GlobalStatic.OpenScope(config);
			Assert.True(config.LoadConfig(tempRoot));
			config.OverrideMaxParallelErbFiles = 16;

			Assert.True(config.SaveConfig());

			var saved = File.ReadAllText(Path.Combine(tempRoot, "emuera.config"));
			Assert.DoesNotContain("MaxParallelErbFiles", saved);
			Assert.DoesNotContain("maxParallelErbFiles", saved);
		}
		finally
		{
			Directory.Delete(tempRoot, recursive: true);
		}
	}
}
