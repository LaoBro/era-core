#if ANDROID
using System;
using System.IO;
using MinorShift.Emuera.Runtime.Utils;

namespace EraCore.Maui.Services;

/// <summary>
/// issues/15（方案 B）：把 SAF 树 URI 反推成本机真实路径。
/// <para>
/// 存在的理由：<c>ACTION_OPEN_DOCUMENT_TREE</c> 按平台契约只返回 <c>content://</c>，平台不给真实路径；
/// 但 externalstorage provider 的 documentId 里带人类可读的相对路径（<c>primary:games/eratoho</c>），
/// 而卷挂载点可以问系统（见 <see cref="AndroidStorageVolumes"/>）。两者一拼就是真实路径——
/// 于是"系统选择器"这个唯一入口也能落到快速轨。
/// </para>
/// <para>
/// <b>判定规则全在 Core 侧纯函数</b>（<see cref="SafPath"/> 的 TrySplitDocumentId /
/// IsDirectAccessBlocked / TryCombineVolumeRoot），本类只做两件必须有 Context 的事：
/// 读 authority、拿卷根，外加一次读探针。于是规则可单测，只有反推本身需要真机。
/// </para>
/// <para>
/// <b>任一条件不满足即返回 false</b>——调用方留在 SAF 轨。宁可慢，也不把"可能能读"的路径
/// 当成真实路径用：那会在加载期变成难诊断的空列表或读失败。
/// </para>
/// </summary>
internal static class SafLocalPathResolver
{
    /// <summary>唯一支持 documentId → 真实路径映射的 provider。</summary>
    private const string ExternalStorageAuthority = "com.android.externalstorage.documents";

    /// <summary>判定诊断前缀——真机统计反推命中率用（logcat / AgentLog 双向）。</summary>
    internal const string LogTag = "[saf] resolve";

    /// <summary>
    /// 尝试反推。失败时 <paramref name="reason"/> 给出可诊断的原因（进日志，不进 UI）。
    /// </summary>
    internal static bool TryResolve(string? treeUri, out string localPath, out string reason)
    {
        localPath = string.Empty;

        if (!SafPath.IsContentUri(treeUri))
        {
            reason = "not-content-uri";
            return false;
        }

        var authority = TryGetAuthority(treeUri!);
        if (authority is null
            || !authority.Equals(ExternalStorageAuthority, StringComparison.OrdinalIgnoreCase))
        {
            // 云盘 / 第三方 provider：documentId 是它自家的内部编码，映射不出本机路径
            reason = $"authority={authority ?? "unparsable"}";
            return false;
        }

        var documentId = SafPath.TryGetDocumentId(treeUri!);
        if (!SafPath.TrySplitDocumentId(documentId, out var volumeId, out var relativePath))
        {
            reason = "document-id-unparsable";
            return false;
        }

        if (SafPath.IsDirectAccessBlocked(relativePath))
        {
            // Android 11+ 的硬禁区：有权限也读不了，必须留在 SAF
            reason = "direct-access-blocked";
            return false;
        }

        var volumeRoot = AndroidStorageVolumes.TryResolveVolumeRoot(volumeId);
        if (volumeRoot is null)
        {
            reason = $"volume-unmapped:{volumeId}";
            return false;
        }

        if (!SafPath.TryCombineVolumeRoot(volumeRoot, relativePath, out var candidate))
        {
            reason = "combine-failed";
            return false;
        }

        if (!Directory.Exists(candidate))
        {
            // 权限被收回时 Directory.Exists 同样返回 false（不抛异常），与"目录真的不在"不可区分。
            // 两种都该退回 SAF，故不区分。
            reason = $"probe-missing:{candidate}";
            return false;
        }

        // 读探针：能枚举才算真读得动。空目录返回空枚举，与"被系统静默过滤成空"不可区分——
        // 授权闸门由调用方在判定前查过，此处只拦住会抛异常的情形。
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(candidate).GetEnumerator();
            _ = entries.MoveNext();
        }
        catch (Exception ex)
        {
            reason = $"probe-enumerate-failed:{ex.GetType().Name}";
            return false;
        }

        localPath = candidate;
        reason = "ok";
        return true;
    }

    /// <summary>从 content URI 取 authority；解析失败返回 null。</summary>
    private static string? TryGetAuthority(string treeUri)
    {
        try
        {
            return Android.Net.Uri.Parse(treeUri)?.Authority;
        }
        catch
        {
            return null;
        }
    }
}
#endif
