#if WINDOWS
using EraCore.Maui;
using Xunit;

namespace EraCore.Maui.Tests;

/// <summary>
/// 启动日志覆盖三态映射纯函数单测——协议字符串（"auto"/"off"/"on"）与
/// ConfigData.OverrideDisplayReport（bool?：null=不覆盖/false=强制关/true=强制开）互转。
/// 设置页分段控件 ↔ BridgeHost.HandleSetDisplayReportOverride ↔ Preferences 存储共用此映射，
/// 曾因 bool 直塞三态字段导致开关无法关闭（false 语义反转），故钉死映射契约。
/// </summary>
public class DisplayReportOverrideMappingTests
{
	[Theory]
	[InlineData("auto", null)]
	[InlineData("off", false)]
	[InlineData("on", true)]
	[InlineData(null, null)]
	[InlineData("", null)]
	[InlineData("bogus", null)] // 未知值兜底为 auto——与 ParseLevel 等既有容错风格一致
	public void Parse_maps_protocol_string_to_tri_state(string? value, bool? expected)
	{
		Assert.Equal(expected, BridgeHost.ParseDisplayReportOverride(value));
	}

	[Theory]
	[InlineData(null, "auto")]
	[InlineData(false, "off")]
	[InlineData(true, "on")]
	public void ToString_maps_tri_state_to_protocol_string(bool? value, string expected)
	{
		Assert.Equal(expected, BridgeHost.DisplayReportOverrideToString(value));
	}

	[Fact]
	public void Roundtrip_preserves_tri_state()
	{
		foreach (bool? value in new bool?[] { null, false, true })
		{
			Assert.Equal(value, BridgeHost.ParseDisplayReportOverride(
				BridgeHost.DisplayReportOverrideToString(value)));
		}
	}
}
#endif
