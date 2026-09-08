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
    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
        => Directory.GetDirectories(path, searchPattern, searchOption);

    public string[] GetFiles(string path) => Directory.GetFiles(path);
    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        => Directory.GetFiles(path, searchPattern, searchOption);

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
