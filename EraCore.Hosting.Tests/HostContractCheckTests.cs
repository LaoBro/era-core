using EraCore.Hosting;
using Xunit;

namespace EraCore.Hosting.Tests;

public sealed class HostContractCheckTests
{
    [Fact]
    public void SameVersions_IsOk()
    {
        var result = HostContractCheck.Check(
            HostContract.DataVersion,
            HostContract.ApiVersion,
            HostContract.DataVersion,
            HostContract.ApiVersion);

        Assert.Equal(HostContractStatus.Ok, result.Status);
        Assert.True(result.IsCompatible);
        Assert.False(result.MustRefuseStart);
    }

    [Fact]
    public void HostBehind_MustRefuseStart()
    {
        var result = HostContractCheck.Check(
            HostContract.DataVersion - 1,
            HostContract.ApiVersion,
            HostContract.DataVersion,
            HostContract.ApiVersion);

        Assert.Equal(HostContractStatus.HostBehind, result.Status);
        Assert.True(result.MustRefuseStart);
        Assert.Contains("拒绝启动", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HostAhead_AllowedWithConservativeFallback()
    {
        var result = HostContractCheck.Check(
            HostContract.DataVersion + 1,
            HostContract.ApiVersion + 1,
            HostContract.DataVersion,
            HostContract.ApiVersion);

        Assert.Equal(HostContractStatus.HostAhead, result.Status);
        Assert.True(result.IsCompatible);
        Assert.False(result.MustRefuseStart);
        Assert.Contains("未知 awaiting 键回退快照", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IllegalVersion_IsUnknown()
    {
        var result = HostContractCheck.Check(
            0,
            HostContract.ApiVersion,
            HostContract.DataVersion,
            HostContract.ApiVersion);

        Assert.Equal(HostContractStatus.Unknown, result.Status);
        Assert.False(result.IsCompatible);
        Assert.False(result.MustRefuseStart);
    }
}
