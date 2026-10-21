using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Files;

/// <summary>
/// <see cref="IGameDirAccessor"/> 的文件系统实现——封装 <see cref="File"/> / <see cref="Directory"/> / <see cref="Path"/>。
/// issues/20：精确名读取在大小写敏感挂载上容错——引擎对固定名资源用硬编码大写（
/// <c>GAMEBASE.CSV</c>/<c>ABL.CSV</c>…），磁盘真实名任意（<c>Abl.csv</c>），Windows/FUSE 挂载
/// 不区分大小写掩盖了这一点；对齐 SAF accessor 的大小写不敏感解析语义（契约两端一致，LESSONS #24）。
/// </summary>
public sealed class FileSystemGameDirAccessor : IGameDirAccessor
{
    public Task<string?> PickDirectoryAsync(CancellationToken ct = default)
        => throw new NotSupportedException("FileSystemGameDirAccessor does not implement a directory picker.");

    // ===== issues/20：精确名读取的大小写容错 =====

    /// <summary>
    /// 精确路径 miss 时按父目录名映射重试（<see cref="FileExists"/> / <see cref="DirectoryExists"/> /
    /// <see cref="OpenRead"/> / <see cref="ReadAllText"/> / <see cref="ReadAllBytes"/> 共用）。
    /// 精确命中返回 null（调用方沿用原路径）——Windows/FUSE 等大小写不敏感挂载恒走这条零开销路径，
    /// 容错分支只在大小写敏感挂载（app 私有 /data、部分 SD/OTG、桌面 Linux）的 miss 上触发。
    /// </summary>
    private static string? ResolveExactMiss(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            return null;
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
            return null;
        if (!GetNameMap(dir).TryGetValue(name, out var real))
            return null;
        if (real.Equals(name, StringComparison.Ordinal))
            return null; // 映射与请求同形——OS 精确 miss 另有原因（竞态/删除），重试无意义
        return Path.Combine(dir, real);
    }

    /// <summary>
    /// 父目录「逻辑名（忽略大小写）→ 磁盘真实名」映射，惰性构建、进程内缓存。
    /// 只在精确 miss 后访问：加载期 ~20 个固定名 CSV 共享一次目录枚举，其余全是字典命中。
    /// 同目录存在仅大小写不同的条目时按 Ordinal 序取一（确定性；精确命中永远优先，不受影响）。
    /// 写路径（<see cref="CreateDirectory"/> / <see cref="OpenWrite"/> / <see cref="Delete"/>）失效父目录映射，
    /// 保证运行期新建/删除的文件对大小写失配的读取可见（镜像 SAF 缓存失效语义，LESSONS #17/#18）。
    /// </summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> NameMaps = new(StringComparer.Ordinal);

    private static Dictionary<string, string> GetNameMap(string dir)
        => NameMaps.GetOrAdd(dir, static d =>
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(d))
                {
                    var name = Path.GetFileName(entry);
                    if (string.IsNullOrEmpty(name))
                        continue;
                    if (map.TryGetValue(name, out var existing))
                    {
                        if (string.CompareOrdinal(name, existing) < 0)
                            map[name] = name;
                    }
                    else
                    {
                        map.Add(name, name);
                    }
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                // 目录不可达/不可枚举：留空映射，精确名读取按原语义失败
            }
            return map;
        });

    private static void InvalidateNameMapOf(string? path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            NameMaps.TryRemove(dir, out _);
    }

    public bool DirectoryExists(string path)
        => Directory.Exists(path) || ResolveExactMiss(path) is { } dir && Directory.Exists(dir);
    public bool FileExists(string path)
        => File.Exists(path) || ResolveExactMiss(path) is { } file && File.Exists(file);

    public string[] GetDirectories(string path) => Directory.GetDirectories(path);

    /// <summary>
    /// 镜像 SAF 语义（<c>SafGameDirAccessor.EnumerateUri</c> 用 <see cref="Wildcard.Matches"/>）——
    /// 大小写不敏感、按「条目名」匹配。绝不能把 <paramref name="searchPattern"/> 直接透传给
    /// <c>Directory.GetDirectories(path, pattern, option)</c>：.NET 在 Unix/Android 上由托管代码做
    /// glob，<c>MatchCasing.PlatformDefault</c> 解析为大小写敏感，会把引擎的固定大写模式（如
    /// <c>CHARA*.CSV</c>）挡在磁盘真实名（如 <c>Chara1 霊夢.csv</c>）之外，与 Windows/SAF 分叉。
    /// </summary>
    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
        => FilterByPattern(Directory.GetDirectories(path, "*", searchOption), searchPattern);

    public string[] GetFiles(string path) => Directory.GetFiles(path);

    /// <inheritdoc cref="GetDirectories(string, string, SearchOption)"/>
    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        => FilterByPattern(Directory.GetFiles(path, "*", searchOption), searchPattern);

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
        InvalidateNameMapOf(path);
    }

    public Stream OpenWrite(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        InvalidateNameMapOf(path);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            InvalidateNameMapOf(path);
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
