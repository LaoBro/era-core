using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.UI.Game.Image;

/// <summary>
/// sprite 名 → 相对游戏根文件路径 + 可选裁切矩形 映射表。
/// <para>
/// era 游戏 HTML <c>&lt;img src='Face_1'&gt;</c> 的 src 是 sprite 名（如 <c>FACE_1</c>），真实文件是
/// <c>resources/1_Face.png</c>——映射在 <c>resources/*.csv</c> 第一、二列，第 3-6 列（<c>tokens[2..5]</c>）是
/// 裁切矩形 <c>FACE_1,1_Face.png,0,0,180,180</c>（从图集裁切 (0,0,180,180)）。WinForms 的
/// <see cref="AppContents.LoadContents"/> 扫描 resources csv 构建 sprite 表，无头下该函数是 stub——
/// 本表只解析「名→文件路径 + 裁切矩形」（不加载位图），供尺寸探针、GetSprite 替身与资源通道复用。
/// </para>
/// <para>
/// 懒加载 + 按游戏根缓存：<c>TryResolve</c> 首次调用（或游戏根变化）时扫描重建。
/// 扫描经 <see cref="IGameDirAccessor.GetFiles"/> 枚举（SAF/文件系统统一抽象）。
/// 2026-08-08 修复：<see cref="GetRelativeDir"/> 改用 <see cref="SafPath.GetRelativePathFromRoot"/>，
/// content:// URI 走 documentId 前缀比较（Android SAF），普通路径走 <see cref="Path.GetRelativePath"/>
/// 等价语义（Windows）。之前 <c>Path.GetRelativePath</c> 对 content:// 无效导致 Android sprite 名
/// 解析完全失败，所有无扩展名 sprite（如 <c>Border_Normal</c>、<c>Image_F女</c>）查不到 fallback
/// 路径 → <see cref="MinorShift.Emuera.Assets.AssetChannel.TryGetImage"/> false → PathHandler
/// reject → WebView 走默认网络 → ERR_NAME_NOT_RESOLVED（图片空）。
/// </para>
/// </summary>
internal static class ImageNameTable
{
    private static readonly object _sync = new();
    private static string? _loadedRoot;
    private static Dictionary<string, SpriteEntry> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析 sprite 名 → 相对游戏根的文件路径（正斜杠分隔）。失败返回 false。</summary>
    public static bool TryResolve(IGameDirAccessor accessor, string gameRoot, string name, out string? relativePath)
    {
        if (TryResolveEntry(accessor, gameRoot, name, out var entry) && entry != null)
        {
            relativePath = entry.RelativePath;
            return true;
        }
        relativePath = null;
        return false;
    }

    /// <summary>
    /// 解析 sprite 名 → 完整条目（文件路径 + 可选裁切矩形）。失败返回 false。
    /// issue 07：裁切矩形来自 csv 第 3-6 列（tokens[2..5]）——无矩形（列不足/非整数/宽高≤0）= 全图。
    /// </summary>
    public static bool TryResolveEntry(IGameDirAccessor accessor, string gameRoot, string name, out SpriteEntry? entry)
    {
        entry = null;
        if (accessor == null || string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(name))
            return false;
        EnsureLoaded(accessor, gameRoot);
        lock (_sync)
        {
            return _map.TryGetValue(name, out entry) && entry != null;
        }
    }

    private static void EnsureLoaded(IGameDirAccessor accessor, string gameRoot)
    {
        lock (_sync)
        {
            if (_loadedRoot == gameRoot)
                return; // 已为当前游戏根加载（空表也缓存，避免每请求重扫）
            var map = new Dictionary<string, SpriteEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // ContentDir = <root>/resources（与 GamePaths.ContentDir 同规则）
                var contentDir = accessor.ResolveSubPath(gameRoot, "resources");
                string[] csvFiles = accessor.GetFiles(contentDir, "*.csv", SearchOption.AllDirectories);
                foreach (var csvPath in csvFiles)
                {
                    var bytes = accessor.ReadAllBytes(csvPath);
                    if (bytes == null || bytes.Length == 0)
                        continue;
                    var text = EncodingHandler.DetectEncoding(bytes).GetString(bytes);
                    // BOM（U+FEFF）在 GetString 中保留、Trim 不保证剥离——显式剥除，否则首行 name 前缀 BOM 永 miss
                    text = text.TrimStart('\uFEFF');
                    var csvDirRel = GetRelativeDir(gameRoot, csvPath);
                    foreach (var line in text.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith(';'))
                            continue;
                        var tokens = trimmed.Split(',');
                        if (tokens.Length < 2)
                            continue;
                        var name = tokens[0].Trim();
                        var file = tokens[1].Trim();
                        // 无头下动画 sprite（ANIME 行）无单一文件可解析——按 spec 决定跳过；
                        // 第二列必须含扩展名（WinForms 同款校验）
                        if (name.Length == 0 || file.Length == 0 || file.IndexOf('.') < 0)
                            continue;
                        if (file.Equals("ANIME", StringComparison.OrdinalIgnoreCase))
                            continue;
                        // issue 07：第 3-6 列（tokens[2..5]）裁切矩形 (x,y,w,h)——全部可解析且 w/h>0
                        // 才视为裁切（WinForms CreateFromCsv 同款列位与正性校验；OoR 越界由调用方宽容处理）
                        SpriteCrop? crop = null;
                        if (tokens.Length >= 6
                            && int.TryParse(tokens[2].Trim(), out var cropX)
                            && int.TryParse(tokens[3].Trim(), out var cropY)
                            && int.TryParse(tokens[4].Trim(), out var cropW)
                            && int.TryParse(tokens[5].Trim(), out var cropH)
                            && cropW > 0 && cropH > 0)
                        {
                            crop = new SpriteCrop(cropX, cropY, cropW, cropH);
                        }
                        var rel = csvDirRel.Length == 0 ? file : csvDirRel + "/" + file;
                        if (!map.ContainsKey(name))
                            map[name] = new SpriteEntry(rel, crop); // 重复名 first-wins（WinForms 打 SpriteNameAlreadyUsed 告警，无头静默）
                    }
                }
            }
            catch
            {
                // 扫描失败（目录不存在/IO）→ 空表，查询自然失败
            }
            _loadedRoot = gameRoot;
            _map = map;
        }
    }

    /// <summary>
    /// csv 文件所在目录相对游戏根（正斜杠）。
    /// 2026-08-08 修复：content:// URI 走 documentId 前缀比较 + Unescape（Android SAF），
    /// 普通路径走 <see cref="Path.GetFullPath"/> + 手动前缀剥离等价语义（Windows）。
    /// 返回值去掉路径末段（csv 文件名本身），保留目录前缀——后续拼 csv 第二列文件名（issue 07）。
    /// 之前 <c>Path.GetDirectoryName</c> + <c>Path.GetRelativePath</c> 对 content:// 抛或返回乱码，
    /// 导致 Android ImageNameTable 整个 sprite 表加载为空，所有无扩展名 sprite 解析失败。
    /// </summary>
    private static string GetRelativeDir(string gameRoot, string csvPath)
    {
        string rel;
        if (SafPath.IsContentUri(gameRoot) && SafPath.IsContentUri(csvPath))
        {
            // SAF：documentId 前缀比较（含 Unescape，正确处理 %3A/%2F 编码的 tree URI）。
            var rootId = SafPath.TryGetDocumentId(gameRoot);
            var pathId = SafPath.TryGetDocumentId(csvPath);
            if (rootId == null || pathId == null) return "";
            rootId = rootId.TrimEnd('/');
            if (pathId.Equals(rootId, StringComparison.OrdinalIgnoreCase)) return "";
            if (!pathId.StartsWith(rootId + "/", StringComparison.OrdinalIgnoreCase)) return "";
            rel = pathId[(rootId.Length + 1)..]; // csvPath 相对 gameRoot（documentId 段已 Unescape，正斜杠分隔）
        }
        else
        {
            try
            {
                var root = Path.GetFullPath(gameRoot);
                var full = Path.GetFullPath(csvPath);
                if (!root.EndsWith(Path.DirectorySeparatorChar)
                    && !root.EndsWith(Path.AltDirectorySeparatorChar))
                    root += Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return "";
                rel = full[root.Length..]; // 反斜杠分隔（Path.GetFullPath 标准化输出）
            }
            catch
            {
                return "";
            }
        }
        // 统一正斜杠后去掉末段（csv 文件名），保留目录相对路径
        rel = rel.Replace('\\', '/');
        var lastSlash = rel.LastIndexOf('/');
        return lastSlash < 0 ? "" : rel[..lastSlash];
    }
}

/// <summary>
/// issue 07：sprite 裁切矩形——csv 第 3-6 列 (x,y,w,h)，图集内像素坐标。
/// 无头不加载位图，越界（x+w&gt;整图宽等）由调用方（GetSprite 替身）按探测尺寸宽容处理。
/// </summary>
internal readonly record struct SpriteCrop(int X, int Y, int Width, int Height);

/// <summary>
/// issue 07：sprite 名解析条目——相对游戏根文件路径 + 可选裁切矩形（无裁切 = 全图）。
/// </summary>
internal sealed record SpriteEntry(string RelativePath, SpriteCrop? Crop);
