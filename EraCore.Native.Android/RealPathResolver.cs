using System;
using System.IO;
using Android.OS;
using Android.OS.Storage;
using Android.Provider;
using Android.Util;
using EraCore.Native;

namespace EraCore.Native.Android;

/// <summary>
/// 把 <c>ACTION_OPEN_DOCUMENT_TREE</c> 的 tree URI 还原为真实文件系统路径
/// （<c>primary:Download/game</c> → <c>/storage/emulated/0/Download/game</c>）。
///
/// <para>Android 没有「返回文件系统路径」的官方目录选择器，这里复用系统选择器再映射：
/// 映射失败（第三方 provider、SD 卡未挂载、<c>Android/data</c> 受限目录）时返回 false，
/// 由调用方回退 SAF 复制模式。映射本身只做路径换算与存在性探测，不读写文件内容。</para>
/// </summary>
internal static class RealPathResolver
{
    private const string LogTag = "EraCoreNative";
    private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    /// <summary>尝试把 tree URI 映射为可读的真实目录。</summary>
    /// <param name="treeUri">系统目录选择器返回的 tree URI。</param>
    /// <param name="localPath">映射成功时的绝对路径。</param>
    /// <param name="reason">失败原因（用于日志与提示，形如 <c>provider=com.google.android.apps.docs</c>）。</param>
    public static bool TryResolve(global::Android.Net.Uri treeUri, out string localPath, out string reason)
    {
        localPath = string.Empty;
        reason = string.Empty;

        var authority = treeUri.Authority;
        if (!string.Equals(authority, ExternalStorageAuthority, StringComparison.Ordinal))
        {
            reason = "provider=" + authority;
            Log.Info(LogTag, $"real path: unsupported provider {authority}");
            return false;
        }

        string documentId;
        try
        {
            documentId = DocumentsContract.GetTreeDocumentId(treeUri) ?? string.Empty;
        }
        catch (Exception ex)
        {
            reason = "document-id-unavailable";
            Log.Warn(LogTag, "real path: GetTreeDocumentId failed: " + ex.Message);
            return false;
        }

        var colon = documentId.IndexOf(':');
        if (colon <= 0)
        {
            reason = "bad-document-id";
            Log.Warn(LogTag, "real path: unexpected document id " + documentId);
            return false;
        }

        var volumeId = documentId[..colon];
        var relative = documentId[(colon + 1)..];

        if (!IsRelativePathAllowed(relative, out reason))
        {
            Log.Warn(LogTag, $"real path: blocked path {documentId} ({reason})");
            return false;
        }

        var volumeRoot = ResolveVolumeRoot(volumeId);
        if (volumeRoot is null)
        {
            reason = "volume-unmapped:" + volumeId;
            Log.Warn(LogTag, $"real path: no volume root for {volumeId}");
            return false;
        }

        var candidate = relative.Length == 0
            ? volumeRoot
            : Path.Combine(volumeRoot, relative.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(candidate))
        {
            reason = "missing-or-forbidden";
            Log.Warn(LogTag, $"real path: directory not accessible {candidate}");
            return false;
        }

        // 目录存在但不可枚举（权限不足 / 已被系统限制）时同样视为映射失败。
        try
        {
            using var enumerator = Directory.EnumerateFileSystemEntries(candidate).GetEnumerator();
            enumerator.MoveNext();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            reason = "unreadable";
            Log.Warn(LogTag, $"real path: cannot enumerate {candidate}: {ex.Message}");
            return false;
        }

        localPath = candidate;
        reason = "ok";
        Log.Info(LogTag, $"real path resolved: {documentId} -> {candidate}");
        return true;
    }

    /// <summary>
    /// <c>Android/data</c>、<c>Android/obb</c> 即使有全盘权限也无法稳定遍历（受分区存储保护），
    /// 提前拒绝以给出明确提示。
    /// </summary>
    private static bool IsRelativePathAllowed(string relative, out string reason)
    {
        reason = string.Empty;
        var normalized = relative.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (!string.Equals(segments[i], "Android", StringComparison.Ordinal))
                continue;
            if (string.Equals(segments[i + 1], "data", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segments[i + 1], "obb", StringComparison.OrdinalIgnoreCase))
            {
                reason = "android-restricted-dir";
                return false;
            }
        }
        return true;
    }

    /// <summary>枚举已挂载存储卷，按 volumeId（<c>primary</c> / <c>1234-5678</c>）匹配根路径。</summary>
    private static string? ResolveVolumeRoot(string volumeId)
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var storageManager = global::Android.App.Application.Context
                    .GetSystemService(global::Android.Content.Context.StorageService) as StorageManager;
                var volumes = storageManager?.StorageVolumes;
                if (volumes is not null)
                {
                    foreach (var volume in volumes)
                    {
                        var dir = volume.Directory?.AbsolutePath;
                        // 诊断：厂商共享卷（MuMu /mnt/shared/…）的 path 不含 UUID，
                        // 只能靠 fsUuid 匹配；把每个卷的 uuid/state/dir 打出来便于现场取证。
                        Log.Info(LogTag,
                            $"real path: volume uuid={volume.Uuid} primary={volume.IsPrimary} " +
                            $"state={volume.State} dir={dir}");
                        if (dir is null)
                            continue;
                        if (RealPathVolumeMatcher.Matches(dir, volume.Uuid, volumeId))
                            return dir;
                    }
                }
            }

            // API 29 及以下 / 厂商未返回 StorageVolume 时，用应用可见的外部目录兜底。
            var externalRoots = global::Android.App.Application.Context.GetExternalFilesDirs(null);
            if (externalRoots is not null)
            {
                foreach (var dir in externalRoots)
                {
                    var path = dir?.AbsolutePath;
                    if (path is null)
                        continue;
                    // <root>/Android/data/<pkg>/files → <root>
                    var root = path;
                    for (var i = 0; i < 3; i++)
                    {
                        var parent = Path.GetDirectoryName(root);
                        if (parent is null)
                            break;
                        root = parent;
                    }
                    if (root.Length == 0)
                        continue;
                    if (RealPathVolumeMatcher.Matches(root, volumeUuid: null, volumeId))
                        return root;
                }
            }

            // 最后兜底：UUID 形态的卷在部分厂商实现里 StorageVolume.Directory 为 null，
            // 按系统约定路径探测（存在即用，绝不强塞不存在的路径）。
            if (!string.Equals(volumeId, "primary", StringComparison.Ordinal)
                && !string.Equals(volumeId, "home", StringComparison.Ordinal))
            {
                foreach (var candidate in new[] { "/storage/" + volumeId, "/mnt/media_rw/" + volumeId })
                {
                    if (Directory.Exists(candidate))
                    {
                        Log.Info(LogTag, $"real path: volume {volumeId} resolved via fallback {candidate}");
                        return candidate;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, "real path: volume lookup failed: " + ex.Message);
        }

        return null;
    }
}
