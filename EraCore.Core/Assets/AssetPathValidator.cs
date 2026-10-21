using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Assets;

/// <summary>
/// /assets 资源通道的路径消毒与扩展名白名单（issue 03，spec Q5 安全决策）。
/// C# 侧单点实现：Web 端点（KestrelGameServer）与安卓 PathHandler（issue 05）复用。
///
/// 输入契约：URL 已解码的相对路径（Kestrel 路由参数 / WebView URL 均解码后传入）；
/// 函数内仍主动 <see cref="Uri.UnescapeDataString"/> 一次，防调用方漏解码（%2e%2e 绕过）。
///
/// 校验规则（spec L117）：
/// - 只接受相对路径：拒绝对路径、以 <c>/</c> 开头、空段、<c>.</c>/<c>..</c> 段（含 <c>\</c> 变体）
/// - 规范化（<see cref="Path.GetFullPath"/>）后必须仍落在游戏目录根内（<see cref="SafPath.IsUnderRoot"/>，
///   同时处理普通路径与 content:// 两种根）
/// </summary>
internal static class AssetPathValidator
{
    /// <summary>图片扩展名白名单（spec Q5）——同时堵住 ERB/CSV/HTML 源文件经 /assets 泄出。</summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
    };

    /// <summary>
    /// 解析并校验资源请求路径。成功时 <paramref name="fullPath"/> 为游戏根下的完整路径
    /// （普通路径为规范化后的文件系统路径；content:// 根为拼接后的 content URI）。
    /// </summary>
    public static bool TryResolve(string gameRoot, string urlPath, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrEmpty(gameRoot) || string.IsNullOrWhiteSpace(urlPath))
            return false;

        // 调用方契约是已解码路径，但主动再解码防漏解码调用方（%2e%2e / %2f 变体）。
        // 循环解码到稳定（上限 3 次）：content 分支的 documentId 前缀比较在
        // SafPath.IsUnderRoot 内部还有一次 Unescape——双重编码（%252e）若不在此
        // 消化，会在那里重组出 `..` 绕过段校验。解码失败视为非法输入。
        string decoded;
        try
        {
            decoded = urlPath.Trim();
            for (int i = 0; i < 3 && decoded.Contains('%'); i++)
                decoded = Uri.UnescapeDataString(decoded);
        }
        catch (UriFormatException)
        {
            return false;
        }

        // 段级校验（在 Unescape 之后、路径拼接之前）：
        // 统一分隔符为平台分隔符，逐段拒绝空段 / "." / ".." / 绝对路径。
        var candidate = decoded.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(candidate))
            return false;
        foreach (var seg in candidate.Split(Path.DirectorySeparatorChar))
        {
            if (seg.Length == 0 || seg == "." || seg == "..")
                return false;
        }

        if (SafPath.IsContentUri(gameRoot))
        {
            // SAF：content:// 根不是文件系统路径，不能经 Path.GetFullPath 规范化
            // （会破坏 URI）。段校验已做，直接拼接后按 documentId 前缀确认落根内。
            // 注意：本函数只做消毒判定；拼出的 ".../tree/<id>/img/x.png" 不是合法
            // document URI（真 SAF 消费需经 BuildDocumentUriUsingTree 重建）——
            // 05 安卓 PathHandler 读取字节时必须先重建再喂 IGameDirAccessor。
            var resolved = gameRoot.TrimEnd('/') + "/" + decoded;
            if (!SafPath.IsUnderRoot(gameRoot, resolved))
                return false;
            fullPath = resolved;
            return true;
        }

        var full = Path.GetFullPath(Path.Combine(gameRoot, candidate));
        if (!SafPath.IsUnderRoot(gameRoot, full))
            return false;
        fullPath = full;
        return true;
    }

    /// <summary>扩展名是否在白名单内（大小写不敏感）。</summary>
    public static bool IsAllowedImage(string fullPath)
    {
        return AllowedExtensions.Contains(Path.GetExtension(fullPath));
    }

    /// <summary>扩展名 → MIME（白名单外回退 application/octet-stream）。</summary>
    public static string GetMimeType(string fullPath)
    {
        return Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
    }
}
