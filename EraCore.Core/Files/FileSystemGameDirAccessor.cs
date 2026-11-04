using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace MinorShift.Emuera.Files;

/// <summary>
/// <see cref="IGameDirAccessor"/> 的文件系统实现——封装 <see cref="File"/> / <see cref="Directory"/> / <see cref="Path"/>。
/// issues/20：精确名读取在大小写敏感挂载上容错——引擎对固定名资源用硬编码大写（
/// <c>GAMEBASE.CSV</c>/<c>ABL.CSV</c>…），磁盘真实名任意（<c>Abl.csv</c>），Windows/FUSE 挂载
/// 不区分大小写掩盖了这一点；本实现**逐段**回退（不止末段），对齐 SAF accessor 的大小写不敏感解析语义
/// （契约两端一致，LESSONS #24）。
/// <para>
/// 语义边界：只做「条目名映射」，不做通配；每段先精确名，其次忽略大小写唯一匹配，
/// 多候选（同目录存在仅大小写不同的多个条目）时<b>放弃容错</b>；<c>.</c>/<c>..</c> 段拒绝。
/// 回退仅在精确 miss 时触发，精确命中保持零额外枚举。
/// </para>
/// </summary>
public sealed class FileSystemGameDirAccessor : IGameDirAccessor
{
    // ===== issues/20：精确名读取的大小写容错（逐段） =====

    /// <summary>
    /// 精确路径 miss 时从「最近的可信前缀」逐段向下解析（<see cref="FileExists"/> /
    /// <see cref="DirectoryExists"/> / <see cref="OpenRead"/> / <see cref="ReadAllText"/> /
    /// <see cref="ReadAllBytes"/> 与目录枚举共用）。精确命中返回 null（调用方沿用原路径）——
    /// Windows/FUSE 等大小写不敏感挂载恒走这条零开销路径，容错只在大小写敏感挂载
    /// （app 私有 /data、部分 SD/OTG、桌面 Linux）的 miss 上触发。
    /// <para>
    /// <b>尾分隔符</b>：<c>.../csv/</c> 先裁尾再逐段解析，结果重新拼回分隔符——不是把原字符串
    /// trim 掉糊过去；中间各级都被替换为真实名，后续 <c>.../csv/xxx.csv</c> 的读取同样受益。
    /// </para>
    /// <para>
    /// <b>歧义</b>：每段先精确名；否则忽略大小写唯一匹配；同一目录存在多个仅大小写不同的条目时
    /// 放弃容错（见 <see cref="CaseInsensitivePathResolver.MatchSegment"/>）。每段真实名都取自其
    /// 父目录的枚举结果，解析结果恒在原父级之下，不越界、不扩张符号链接。
    /// </para>
    /// </summary>
    private static string? ResolveExactMiss(string path)
    {
        var hadTrailingSeparator = HasTrailingSeparator(path);
        var normalized = TrimTrailingSeparators(path);
        if (string.IsNullOrEmpty(normalized) || Exists(normalized))
            return null; // 精确命中（含仅裁尾分隔符后的命中）→ 调用方沿用原路径

        // 1) 从末段向上收集缺失段，直到找到已存在的可信前缀（最多到卷根）。
        var missing = new List<string>();
        var probe = normalized;
        while (!Exists(probe))
        {
            var parent = Path.GetDirectoryName(probe);
            var name = Path.GetFileName(probe);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                return null;
            missing.Add(name);
            probe = parent;
        }
        if (missing.Count == 0)
            return null;

        // 2) 从可信前缀逐段解析（missing 是自下而上收集的，故逆序）。
        var resolved = probe;
        for (var i = missing.Count - 1; i >= 0; i--)
        {
            var entries = EnumerateEntryNames(resolved);
            var real = entries == null ? null : CaseInsensitivePathResolver.MatchSegment(entries, missing[i]);
            if (real == null)
                return null; // 任一段无唯一匹配 → 放弃容错，如实 miss
            resolved = Path.Combine(resolved, real);
        }
        return hadTrailingSeparator ? resolved + Path.DirectorySeparatorChar : resolved;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool HasTrailingSeparator(string path)
        => path.Length > 0
           && (path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar);

    /// <summary>去掉尾部分隔符但保留卷根（<c>/</c>、<c>C:\</c>）——否则 <c>Path.GetFileName</c> 恒为空。</summary>
    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var end = path.Length;
        while (end > root.Length
               && (path[end - 1] == Path.DirectorySeparatorChar || path[end - 1] == Path.AltDirectorySeparatorChar))
            end--;
        return path[..end];
    }

    /// <summary>
    /// 目录条目名缓存：键为目录路径，值带短 TTL 且有容量上限。
    /// 逐段解析一次 miss 可能枚举多级目录；加载期大量固定名 CSV 读取靠它把枚举摊薄到
    /// 「每目录每 TTL 一次」，其余全是内存命中。
    /// <para>
    /// 失效策略：<b>短 TTL（1s）+ 经 accessor 的写操作后清父目录</b>。不设永久全局缓存——
    /// 导入/解压等 <b>绕过 accessor</b> 的外部写入无法主动失效，TTL 是这类 stale 的上界；
    /// 同一物理目录经不同大小写路径访问时缓存键不同，也由 TTL 兜底。
    /// </para>
    /// </summary>
    private const int DirectoryCacheTtlMs = 1000;
    private const int DirectoryCacheLimit = 512;

    private sealed class DirectoryEntryNames
    {
        public required string[] Names { get; init; }
        public required long ExpiresAtTicks { get; init; }
    }

    private static readonly ConcurrentDictionary<string, DirectoryEntryNames> DirectoryCaches = new(StringComparer.Ordinal);

    private static string[]? EnumerateEntryNames(string dir)
    {
        var now = Environment.TickCount64;
        if (DirectoryCaches.TryGetValue(dir, out var cached) && cached.ExpiresAtTicks > now)
            return cached.Names;

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(dir);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            // 不可达/不可枚举：不缓存失败结果，下次仍如实失败（避免把瞬时状态固化 1s）
            return null;
        }

        var names = new string[entries.Length];
        for (var i = 0; i < entries.Length; i++)
            names[i] = Path.GetFileName(entries[i]);

        DirectoryCaches[dir] = new DirectoryEntryNames { Names = names, ExpiresAtTicks = now + DirectoryCacheTtlMs };
        TrimDirectoryCaches(now);
        return names;
    }

    private static void TrimDirectoryCaches(long now)
    {
        if (DirectoryCaches.Count < DirectoryCacheLimit)
            return;
        foreach (var entry in DirectoryCaches)
        {
            if (entry.Value.ExpiresAtTicks <= now)
                DirectoryCaches.TryRemove(entry.Key, out _);
        }
        if (DirectoryCaches.Count >= DirectoryCacheLimit)
            DirectoryCaches.Clear();
    }

    private static void InvalidateDirectoryCacheOf(string? path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            DirectoryCaches.TryRemove(dir, out _);
    }

    public bool DirectoryExists(string path)
        => Directory.Exists(path) || ResolveExactMiss(path) is { } dir && Directory.Exists(dir);
    public bool FileExists(string path)
        => File.Exists(path) || ResolveExactMiss(path) is { } file && File.Exists(file);

    public string[] GetDirectories(string path) => Directory.GetDirectories(ResolveDirectoryArgument(path));

    /// <summary>
    /// 镜像 SAF 语义（<c>SafGameDirAccessor.EnumerateUri</c> 用 <see cref="Wildcard.Matches"/>）——
    /// 大小写不敏感、按「条目名」匹配。绝不能把 <paramref name="searchPattern"/> 直接透传给
    /// <c>Directory.GetDirectories(path, pattern, option)</c>：.NET 在 Unix/Android 上由托管代码做
    /// glob，<c>MatchCasing.PlatformDefault</c> 解析为大小写敏感，会把引擎的固定大写模式（如
    /// <c>CHARA*.CSV</c>）挡在磁盘真实名（如 <c>Chara1 霊夢.csv</c>）之外，与 Windows/SAF 分叉。
    /// </summary>
    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
        => FilterByPattern(Directory.GetDirectories(ResolveDirectoryArgument(path), "*", searchOption), searchPattern);

    public string[] GetFiles(string path) => Directory.GetFiles(ResolveDirectoryArgument(path));

    /// <inheritdoc cref="GetDirectories(string, string, SearchOption)"/>
    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        => FilterByPattern(Directory.GetFiles(ResolveDirectoryArgument(path), "*", searchOption), searchPattern);

    /// <summary>
    /// 枚举入参也走 miss 回退解析：引擎用 <c>.../csv/</c> 这类理论小写路径枚举，若磁盘只有
    /// <c>CSV</c>，不解析会在枚举入口直接抛 <see cref="DirectoryNotFoundException"/>（<see cref="DirectoryExists"/>
    /// 已能容忍、但枚举仍会炸）。精确命中时首个 <see cref="Directory.Exists(string)"/> 即短路，
    /// 不进入逐段解析。
    /// </summary>
    private static string ResolveDirectoryArgument(string path)
        => Directory.Exists(path) ? path : ResolveExactMiss(path) ?? path;

    /// <summary>
    /// OS 层用 catch-all 枚举（递归交给 <c>Directory.*</c>，保持 <c>FromSearchOption</c> 语义：
    /// <c>AttributesToSkip=0</c>、目录不存在仍抛 <see cref="DirectoryNotFoundException"/>），
    /// 托管侧按条目名用大小写不敏感的 <see cref="Wildcard.Matches"/> 过滤。
    /// </summary>
    private static string[] FilterByPattern(string[] entries, string searchPattern)
    {
        if (searchPattern == "*")
            return entries; // 全匹配快路径：Preload 全树枚举零额外开销
        var result = new List<string>(entries.Length);
        foreach (var entry in entries)
            if (Wildcard.Matches(Path.GetFileName(entry), searchPattern))
                result.Add(entry);
        return result.ToArray();
    }

    public string ReadAllText(string path) => File.ReadAllText(ResolveExactMiss(path) ?? path);
    public byte[]? ReadAllBytes(string path)
    {
        var resolved = ResolveExactMiss(path) ?? path;
        return File.Exists(resolved) ? File.ReadAllBytes(resolved) : null;
    }
    public Stream? OpenRead(string path)
    {
        var resolved = ResolveExactMiss(path) ?? path;
        return File.Exists(resolved) ? File.OpenRead(resolved) : null;
    }

    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        InvalidateDirectoryCacheOf(path);
    }

    public Stream OpenWrite(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        InvalidateDirectoryCacheOf(path);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            InvalidateDirectoryCacheOf(path);
        }
    }

    public bool HasWriteAccess() => true;

    public string ResolveSubPath(string basePath, string subDir)
        => Path.Combine(basePath, subDir) + Path.DirectorySeparatorChar;
    public string CombinePath(string basePath, string filename)
        => Path.Combine(basePath, filename);
    public string GetParentPath(string path)
        => Path.GetDirectoryName(path) ?? path;
    public string GetFileName(string path) => Path.GetFileName(path);
}
