using EraCore.Native;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// tree URI 的 volumeId 与 <c>StorageVolume</c> 根路径的匹配规则。
/// 重点覆盖 MuMu 共享卷：根路径 <c>/mnt/shared/MuMuShared</c> 不含 UUID，只能靠 fsUuid 命中。
/// </summary>
public sealed class NativeRealPathVolumeMatcherTests
{
    [Fact]
    public void PrimaryVolumeMatchesEmulatedRoot()
    {
        Assert.True(RealPathVolumeMatcher.Matches("/storage/emulated/0", null, "primary"));
        Assert.False(RealPathVolumeMatcher.Matches("/storage/1234-5678", null, "primary"));
    }

    [Fact]
    public void HomeIsAlwaysRejected()
    {
        Assert.False(RealPathVolumeMatcher.Matches("/storage/emulated/0", null, "home"));
    }

    [Fact]
    public void SharedVolumeMatchesByFsUuid()
    {
        Assert.True(RealPathVolumeMatcher.Matches(
            "/mnt/shared/MuMuShared",
            "41217664-9172-527a-b3d5-edabb50a7d69",
            "41217664-9172-527a-b3d5-edabb50a7d69"));
    }

    [Fact]
    public void UuidComparisonIgnoresDashAndCase()
    {
        Assert.True(RealPathVolumeMatcher.Matches(
            "/mnt/shared/whatever",
            "41217664-9172-527A-B3D5-EDABB50A7D69",
            "412176649172527ab3d5edabb50a7d69"));
    }

    [Fact]
    public void RemovableVolumeMatchesByPathSuffix()
    {
        Assert.True(RealPathVolumeMatcher.Matches("/storage/1234-5678", "1234-5678", "1234-5678"));
        Assert.True(RealPathVolumeMatcher.Matches("/mnt/media_rw/1234-5678/", null, "1234-5678"));
        Assert.False(RealPathVolumeMatcher.Matches("/mnt/shared/MuMuShared", "abcdef", "1234-5678"));
    }
}
