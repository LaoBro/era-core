using System;
using System.IO;
using Android.OS;
using Android.OS.Storage;
using Android.Provider;
using Android.Util;

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
                        if (dir is null)
                            continue;
                        if (MatchesVolume(dir, volumeId))
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
                    if (MatchesVolume(root, volumeId))
                        return root;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, "real path: volume lookup failed: " + ex.Message);
        }

        return null;
    }

    private static bool MatchesVolume(string root, string volumeId)
    {
        if (string.Equals(volumeId, "home", StringComparison.Ordinal))
            return false;
        if (string.Equals(volumeId, "primary", StringComparison.Ordinal))
            return root.EndsWith("/emulated/0", StringComparison.Ordinal)
                   || root.EndsWith("/emulated/0/", StringComparison.Ordinal);

        // 可移动卷（SD 卡）：volumeId 形如 "1234-5678"，根路径通常以该 id 结尾。
        var normalizedId = volumeId.Replace("-", string.Empty);
        var normalizedRoot = root.Replace("-", string.Empty);
        return normalizedRoot.EndsWith("/" + normalizedId, StringComparison.Ordinal)
               || normalizedRoot.EndsWith("/" + normalizedId + "/", StringComparison.Ordinal);
    }
}
