using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Runtime.Utils;

/// <summary>ADR-0019：运行时脚本 File.*/Directory.* 调用的 SAF 兼容包装。
/// <para>SAF content URI 路径走 DirAccessor，本地路径走原始 System.IO。</para>
/// <para>方案 B：写路径（CreateDirectory / OpenWrite / Delete / WriteAllText）已接通，不再静默跳过。</para>
/// </summary>
internal static class SafCompat
{
    private static bool IsContentUri(string path) =>
        path != null && path.StartsWith("content://", System.StringComparison.Ordinal);

    private static IGameDirAccessor RequireAccessor(string op)
    {
        var acc = GamePaths.Current?.DirAccessor;
        if (acc == null)
            throw new IOException($"SAF {op} requires GamePaths.DirAccessor");
        return acc;
    }

    internal static bool FileExists(string path)
    {
        if (!IsContentUri(path)) return File.Exists(path);
        return GamePaths.Current?.DirAccessor?.FileExists(path) ?? false;
    }

    internal static bool DirectoryExists(string path)
    {
        if (!IsContentUri(path)) return Directory.Exists(path);
        return GamePaths.Current?.DirAccessor?.DirectoryExists(path) ?? false;
    }

    internal static string ReadAllText(string path)
    {
        if (!IsContentUri(path)) return File.ReadAllText(path);
        return GamePaths.Current?.DirAccessor?.ReadAllText(path) ?? "";
    }

    internal static string ReadAllText(string path, Encoding encoding)
    {
        if (!IsContentUri(path)) return File.ReadAllText(path, encoding);
        // DirAccessor 当前按字节读；编码由调用方已 Detect 时仍走文本流
        using var stream = OpenRead(path)
            ?? throw new FileNotFoundException($"SAF file not found: {path}", path);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static void WriteAllText(string path, string contents, Encoding encoding)
    {
        using var stream = OpenWrite(path);
        using var writer = new StreamWriter(stream, encoding);
        writer.Write(contents);
    }

    internal static void CreateDirectory(string path)
    {
        if (!IsContentUri(path))
        {
            Directory.CreateDirectory(path);
            return;
        }
        RequireAccessor("CreateDirectory").CreateDirectory(path);
    }

    /// <summary>创建或截断写入（对齐 FileMode.Create）。调用方负责 Dispose。</summary>
    internal static Stream OpenWrite(string path)
    {
        if (!IsContentUri(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        return RequireAccessor("OpenWrite").OpenWrite(path);
    }

    /// <summary>打开只读流；不存在返回 null。</summary>
    internal static Stream? OpenRead(string path)
    {
        if (!IsContentUri(path))
            return File.Exists(path) ? File.OpenRead(path) : null;
        return GamePaths.Current?.DirAccessor?.OpenRead(path);
    }

    /// <summary>
    /// issue 21：<b>两种路径形态都经 accessor</b>——本地分支若直连
    /// <see cref="Directory.GetFiles(string, string, SearchOption)"/>，会绕过通配的大小写不敏感语义
    /// （.NET 在 Unix/Android 由托管代码做 glob，<c>MatchCasing.PlatformDefault</c> = 大小写敏感），
    /// 与 content 分支（<see cref="Wildcard"/>）在 legacy 下分叉——实例：<c>update key</c>、
    /// <c>ENUMFILES</c>、<c>GetDatFiles</c> 少列文件。accessor 由 <see cref="GamePaths"/> 按路径形态注入。
    /// </summary>
    internal static string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        => GamePaths.Current?.DirAccessor?.GetFiles(path, searchPattern, searchOption) ?? [];

    internal static byte[]? ReadAllBytes(string path)
    {
        if (!IsContentUri(path)) return File.ReadAllBytes(path);
        return GamePaths.Current?.DirAccessor?.ReadAllBytes(path);
    }

    internal static string[] ReadAllLines(string path)
    {
        if (!IsContentUri(path)) return File.ReadAllLines(path);
        return EncodingHandler.ReadAllLinesFromBytes(
            GamePaths.Current?.DirAccessor?.ReadAllBytes(path) ?? []);
    }

    internal static FileAttributes GetAttributes(string path)
    {
        if (!IsContentUri(path)) return File.GetAttributes(path);
        return FileAttributes.Normal;
    }

    internal static void Delete(string path)
    {
        if (!IsContentUri(path))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }
        RequireAccessor("Delete").Delete(path);
    }

    /// <summary>目录 + 文件名组合；content URI 必须走 DirAccessor.CombinePath。</summary>
    internal static string CombinePath(string basePath, string fileName)
    {
        if (IsContentUri(basePath))
            return RequireAccessor("CombinePath").CombinePath(basePath, fileName);

        var trimmed = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(trimmed, fileName);
    }

    /// <summary>解析子目录；content 走 ResolveSubPath，本地 Path.Combine + 尾部分隔符。</summary>
    internal static string ResolveSubPath(string basePath, string subDir)
    {
        if (IsContentUri(basePath))
            return RequireAccessor("ResolveSubPath").ResolveSubPath(basePath, subDir);

        return Path.Combine(basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), subDir)
            + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Resolves a script-supplied text filename beneath the game root. Only files at the root
    /// and descendants of sav are allowed, so the same logical path works for filesystem and SAF roots.
    /// </summary>
    internal static bool TryResolveGameRelativePath(string relativePath, bool createParentDirectories, out string path)
        => TryResolvePathUnderGameRoot(relativePath, createParentDirectories, out path, onlySavTree: true);

    /// <summary>
    /// Resolves a relative path beneath the game root for engine-owned files such as logs.
    /// Unlike script text paths, descendants are not restricted to <c>sav</c>.
    /// </summary>
    internal static bool TryResolvePathUnderGameRoot(string relativePath, bool createParentDirectories, out string path)
        => TryResolvePathUnderGameRoot(relativePath, createParentDirectories, out path, onlySavTree: false);

    private static bool TryResolvePathUnderGameRoot(string relativePath, bool createParentDirectories, out string path, bool onlySavTree)
    {
        path = null!;
        if (string.IsNullOrWhiteSpace(relativePath)
            || SafPath.IsContentUri(relativePath)
            || Path.IsPathRooted(relativePath))
            return false;

        var segments = relativePath.Split(['/', '\\'], System.StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => string.IsNullOrEmpty(segment)
            || segment == "." || segment == ".." || segment.Contains(':')))
            return false;
        if (onlySavTree && segments.Length > 1 && !segments[0].Equals("sav", System.StringComparison.OrdinalIgnoreCase))
            return false;

        var directory = GamePaths.Current?.ExeDir;
        if (string.IsNullOrEmpty(directory))
            return false;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            directory = ResolveSubPath(directory, segments[index]);
            if (createParentDirectories)
                CreateDirectory(directory);
        }

        path = CombinePath(directory, segments[^1]);
        return true;
    }

    /// <summary>
    /// b2-irdiskcache：文件长度 + 修改时间戳。content URI（无 stat 语义）返回 false，由调用方按安全降级处理
    /// （指纹降级：本地路径才计入长度/mtime 指纹）。本地读取异常同样返回 false（文件随后打不开会在加载期警告）。
    /// </summary>
    internal static bool TryGetFileStats(string path, out long length, out long lastWriteUtcTicks)
    {
        length = 0;
        lastWriteUtcTicks = 0;
        if (IsContentUri(path))
            return false;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
                return false;
            length = fi.Length;
            lastWriteUtcTicks = fi.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// b2-irdiskcache：本地路径的临时文件原子替换（delete-dest + move），供 IR 磁盘缓存防半截文件用。
    /// content URI 无原子 rename 语义，返回 false（调用方走删除+重写路径）。失败返回 false（残留 tmp 无害）。
    /// </summary>
    internal static bool TryAtomicReplaceLocal(string tmpPath, string destPath)
    {
        if (IsContentUri(tmpPath) || IsContentUri(destPath))
            return false;
        try
        {
            if (File.Exists(destPath))
                File.Delete(destPath);
            File.Move(tmpPath, destPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
