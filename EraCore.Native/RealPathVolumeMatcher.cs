using System;

namespace EraCore.Native;

/// <summary>
/// tree URI 的 volumeId 与 Android 存储卷根路径的匹配规则（平台无关，供 Android 宿主与单测共用）。
///
/// <para>标准外部存储：<c>primary:</c> → <c>/storage/emulated/0</c>；可移动卷的 volumeId 是
/// fsUuid（如 <c>1234-5678</c>），根路径通常以该 UUID 结尾。</para>
///
/// <para>厂商共享卷（如 MuMu 的 <c>/mnt/shared/MuMuShared</c>）：根路径不含 UUID，
/// 只能靠 <c>StorageVolume.Uuid</c>（fsUuid）匹配；过去这里只按路径后缀匹配，
/// 导致整类共享卷被判定为 <c>volume-unmapped</c> 并静默降级为复制。</para>
/// </summary>
public static class RealPathVolumeMatcher
{
    /// <summary>
    /// 判断某个卷是否对应 <paramref name="volumeId"/>。
    /// </summary>
    /// <param name="volumeRoot">卷根的真实路径（<c>StorageVolume.Directory</c>）。</param>
    /// <param name="volumeUuid">卷的 fsUuid（<c>StorageVolume.Uuid</c>，可能为 null）。</param>
    /// <param name="volumeId">tree URI documentId 冒号前的部分（<c>primary</c> / <c>home</c> / UUID）。</param>
    public static bool Matches(string volumeRoot, string? volumeUuid, string volumeId)
    {
        if (string.IsNullOrEmpty(volumeRoot) || string.IsNullOrEmpty(volumeId))
            return false;

        // home: 是应用私有目录视图，映射到真实路径后语义会错位，始终拒绝。
        if (string.Equals(volumeId, "home", StringComparison.Ordinal))
            return false;

        if (string.Equals(volumeId, "primary", StringComparison.Ordinal))
            return HasPathSuffix(volumeRoot, "/emulated/0");

        // fsUuid 匹配覆盖「根路径不含 UUID」的厂商共享卷。
        if (!string.IsNullOrEmpty(volumeUuid) && SameUuid(volumeId, volumeUuid))
            return true;

        // 兜底：可移动卷根路径以 UUID 结尾（含 OBB / 部分厂商路径命名差异）。
        var normalizedId = StripDashes(volumeId);
        var normalizedRoot = StripDashes(volumeRoot);
        return HasPathSuffix(normalizedRoot, "/" + normalizedId);
    }

    private static bool SameUuid(string a, string b)
    {
        var left = StripDashes(a);
        var right = StripDashes(b);
        return left.Length > 0 && right.Length > 0
            && left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDashes(string value) => value.Replace("-", string.Empty);

    private static bool HasPathSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.Ordinal)
           || value.EndsWith(suffix + "/", StringComparison.Ordinal);
}
