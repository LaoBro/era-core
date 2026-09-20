#if ANDROID
using System;
using System.Collections.Generic;
using Android.Content;
using Android.OS.Storage;

namespace EraCore.Maui.Services;

/// <summary>
/// issues/15（方案 B）：Android 存储卷根枚举的**单一真相源**。
/// <para>
/// 两个调用方共用这一份枚举：<c>BridgeHost.CollectStorageRoots</c>（页内浏览器的起点清单）
/// 与 <see cref="SafLocalPathResolver"/>（把 <c>content://</c> 的卷前缀映射成挂载点）。
/// 各写一份必然漂移——尤其是低版本的兜底裁剪，很容易只修一边。
/// </para>
/// <para>
/// <c>StorageVolume.Directory</c>（卷根，非 app 私有子目录）是 API 30+；低版本没有"卷根"概念
/// （scoped storage 之前），改用 <c>GetExternalFilesDirs</c> 的 app 私有目录裁到挂载点。
/// </para>
/// </summary>
internal static class AndroidStorageVolumes
{
    /// <summary>
    /// 一个可映射的卷。<see cref="VolumeId"/> 与 documentId 的卷前缀**同源**：主卷恒
    /// <c>primary</c>，可移动卷是卷 UUID（如 <c>1AEF-3B2C</c>）——这正是反推能把
    /// <c>1AEF-3B2C:games</c> 映射回挂载点的依据。
    /// </summary>
    internal sealed record Volume(string VolumeId, string Path, string Label, bool Primary, bool Removable);

    /// <summary>app 私有目录里标记"以下是 Android/ 子树"的分段——裁卷根就靠它。</summary>
    private const string StorageDirMarker = "/Android/";

    /// <summary>
    /// 枚举当前挂载的卷。平台异常一律吞掉并返回已收集部分——
    /// 调用方各有兜底（根清单空了给授权提示，反推失败退回 SAF），此处不该让上层挂住。
    /// </summary>
    internal static List<Volume> Enumerate()
    {
        var volumes = new List<Volume>();
        try
        {
            var context = Android.App.Application.Context;

            // 单一 30 守卫同时覆盖 StorageVolumes(24+)/IsPrimary(24+)/IsRemovable(24+)/Directory(30+)。
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var storageManager = context.GetSystemService(Context.StorageService)
                    as Android.OS.Storage.StorageManager;
                if (storageManager is not null)
                {
                    foreach (var volume in storageManager.StorageVolumes)
                    {
                        var path = volume.Directory?.AbsolutePath;
                        if (string.IsNullOrEmpty(path))
                            continue;

                        // 主卷 Uuid 恒为 null → 用 primary；可移动卷用 UUID，与 documentId 前缀一致。
                        var volumeId = volume.IsPrimary ? "primary" : volume.Uuid;
                        if (string.IsNullOrEmpty(volumeId))
                            continue;

                        volumes.Add(new Volume(
                            volumeId!, path!, Describe(volume.IsPrimary),
                            volume.IsPrimary, volume.IsRemovable));
                    }
                }
            }

            if (volumes.Count == 0)
                volumes.AddRange(FromExternalFilesDirs(context.GetExternalFilesDirs(null)));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"AndroidStorageVolumes.Enumerate failed: {ex.Message}");
        }
        return volumes;
    }

    /// <summary>卷标识 → 挂载点。未挂载 / 不认识（含空标识）返回 null。</summary>
    internal static string? TryResolveVolumeRoot(string? volumeId)
    {
        if (string.IsNullOrEmpty(volumeId)) return null;

        foreach (var volume in Enumerate())
        {
            if (volume.VolumeId.Equals(volumeId, StringComparison.OrdinalIgnoreCase))
                return volume.Path;
        }
        return null;
    }

    private static string Describe(bool primary) => primary ? "内部存储" : "可移动存储";

    /// <summary>
    /// 兜底（API 24~29）：从 <c>GetExternalFilesDirs</c> 的 app 私有目录裁出卷根。
    /// 形如 <c>/storage/emulated/0/Android/data/&lt;pkg&gt;/files</c> → <c>/storage/emulated/0</c>；
    /// 裁不出（形态意外）的条目直接丢弃，绝不把 app 私有目录当卷根给出。
    /// 索引 0 恒为主卷——这是 <c>GetExternalFilesDirs</c> 的契约。
    /// </summary>
    private static IEnumerable<Volume> FromExternalFilesDirs(Java.IO.File[]? dirs)
    {
        if (dirs is null) yield break;

        for (int i = 0; i < dirs.Length; i++)
        {
            var full = dirs[i]?.AbsolutePath;
            if (string.IsNullOrEmpty(full))
                continue;

            int cut = full!.IndexOf(StorageDirMarker, StringComparison.Ordinal);
            if (cut <= 0)
                continue;

            var root = full.Substring(0, cut);
            // 可移动卷的标识 = 挂载点末段（/storage/<uuid>），与 documentId 前缀同源。
            var volumeId = i == 0 ? "primary" : LastSegment(root);
            if (volumeId.Length == 0)
                continue;

            yield return new Volume(volumeId, root, Describe(i == 0), i == 0, i != 0);
        }
    }

    /// <summary>路径末段（<c>/storage/1AEF-3B2C</c> → <c>1AEF-3B2C</c>）。</summary>
    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
#endif
