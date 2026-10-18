using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinorShift.Emuera;

/// <summary>
/// game-library spec ID8：目录列举工具——逐层列出子目录，供 Vue DirectoryBrowser 弹窗渲染。
/// ADR-0019：Android 路径已移除（SAF 原生选择器替代），Windows 保留。
/// </summary>
internal static class DirectoryLister
{
    /// <param name="dirPath">要列举的目录绝对路径。null / 空字符串 → 返空结果。</param>
    /// <param name="dirAccessor">目录访问抽象。</param>
    public static DirectoryListResult ListDirectories(string? dirPath, IGameDirAccessor dirAccessor)
    {
        if (string.IsNullOrEmpty(dirPath))
            return new DirectoryListResult(string.Empty, null, new List<string>());

        string normalized;
        try
        {
            normalized = Path.GetFullPath(dirPath);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or System.Security.SecurityException)
        {
            return new DirectoryListResult(dirPath, null, new List<string>());
        }

        var parent = ComputeParentPath(normalized, dirAccessor);

        if (!dirAccessor.DirectoryExists(normalized))
            return new DirectoryListResult(normalized, parent, new List<string>());

        string[] subDirs;
        try
        {
            subDirs = dirAccessor.GetDirectories(normalized);
        }
        catch (UnauthorizedAccessException)
        {
            return new DirectoryListResult(normalized, parent, new List<string>());
        }

        var names = subDirs
            .Select(dirAccessor.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Order(StringComparer.Ordinal)
            .ToList();
        return new DirectoryListResult(normalized, parent, names);
    }

    /// <summary>
    /// 父目录。<b>经 accessor</b>（不变量 F-1）——原实现裸调 <c>Directory.GetParent</c>，
    /// 该项已收敛，<b>不再</b>计入 ARCHITECTURE.md §4.3 例外清单。
    /// <para>
    /// <see cref="IGameDirAccessor.GetParentPath"/> = <c>Path.GetDirectoryName</c> 语义，
    /// 与 <c>Directory.GetParent</c> 有一处差异：后者对带尾部分隔符的路径（<c>/a/b/</c>）
    /// 仍返回 <c>/a</c>，前者返回 <c>/a/b</c>（尾部计数为一个空段）。故先去掉非根尾部分隔符，
    /// 把语义对齐回调用方既有行为。
    /// </para>
    /// </summary>
    private static string? ComputeParentPath(string normalizedPath, IGameDirAccessor dirAccessor)
    {
        try
        {
            var last = normalizedPath[^1];
            var probe = normalizedPath.Length > 1
                && (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                && !IsRoot(normalizedPath)
                    ? normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    : normalizedPath;
            var parent = dirAccessor.GetParentPath(probe);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probe, StringComparison.Ordinal))
                return null; // 根：GetParentPath 回落为自身
            return parent;
        }
        catch { return null; }
    }

    private static bool IsRoot(string path)
    {
        try { return string.Equals(Path.GetPathRoot(path), path, StringComparison.Ordinal); }
        catch { return false; }
    }
}

internal readonly record struct DirectoryListResult(string CurrentPath, string? ParentPath, List<string> SubDirectories);
