#if WINDOWS
using EraCore.Maui;
using Xunit;

namespace EraCore.Maui.Tests;

/// <summary>
/// 方言兼容（MAUI 接入）桥接契约单测——设置页 <c>setDialectProfile</c> → Preferences 值域校验。
///
/// <para>
/// 证据边界：<see cref="BridgeHost.HandleSetDialectProfile"/> 直连平台静态 <c>Preferences</c>，
/// 单测宿主（非 MAUI app 进程）无替身可用，故本套件钉死**校验决策边界**（合法值原样保留 /
/// <c>""</c> = 清除 / 非法值拒绝），与既有 <see cref="DisplayReportOverrideMappingTests"/> 同款
/// 纯函数取证口径；实际 Preferences 写入 + config 回推由 UI 冒烟覆盖。
/// </para>
/// </summary>
public class DialectProfileSettingTests
{
	/// <summary>合法：<c>""</c>（清除 = v24pure）与三个已知 profile id 原样返回。</summary>
	[Theory]
	[InlineData("", "")]
	[InlineData("v24pure", "v24pure")]
	[InlineData("snake", "snake")]
	[InlineData("eratw", "eratw")]
	public void Normalize_keeps_clear_and_known_profiles(string value, string expected)
	{
		Assert.Equal(expected, BridgeHost.NormalizeDialectProfileSetting(value));
	}

	/// <summary>非法：缺省（null）/ 未知 id / 大小写不符（与 DialectRegistry 的 Ordinal 口径一致）一律拒绝写入。</summary>
	[Theory]
	[InlineData(null)]
	[InlineData("bogus")]
	[InlineData("V24PURE")]
	public void Normalize_rejects_unknown_profile(string? value)
	{
		Assert.Null(BridgeHost.NormalizeDialectProfileSetting(value));
	}

	/// <summary>Preferences key 契约——设置页与建会话读取点共用同一 key，改名会静默失联。</summary>
	[Fact]
	public void Dialect_profile_key_matches_wire_contract()
	{
		Assert.Equal("app.dialectProfile", BridgeHost.DialectProfileKey);
	}
}
#endif
