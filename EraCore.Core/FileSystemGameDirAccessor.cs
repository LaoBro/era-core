using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

/// <summary>
/// <see cref="IGameDirAccessor"/> 的文件系统实现——封装 <see cref="File"/> / <see cref="Directory"/> / <see cref="Path"/>。
/// </summary>
public sealed class FileSystemGameDirAccessor : IGameDirAccessor
{
    public Task<string?> PickDirectoryAsync(CancellationToken ct = default)
        => throw new NotSupportedException("FileSystemGameDirAccessor does not implement a directory picker.");

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);

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

    public string ReadAllText(string path) => File.ReadAllText(path);
    public byte[]? ReadAllBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    public Stream? OpenRead(string path) => File.Exists(path) ? File.OpenRead(path) : null;

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public Stream OpenWrite(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
    }

    public void Delete(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
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
