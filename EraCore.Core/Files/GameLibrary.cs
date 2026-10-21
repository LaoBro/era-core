using System;
using System.Collections.Generic;
using System.Linq;

namespace MinorShift.Emuera.Files;

/// <summary>
/// 游戏库：扫描主目录下的有效游戏（同时含 <c>csv/</c> 与 <c>erb/</c> 的直接子目录）。
/// 结果按游戏目录名序数排序，不递归。
/// </summary>
public static class GameLibrary
{
    /// <summary>判断主目录是否存在（经访问实现，SAF 的 content URI 不交给本地文件 API）。</summary>
    public static bool RootExists(string? rootDir, IGameDirAccessor dirAccessor)
    {
        ArgumentNullException.ThrowIfNull(dirAccessor);
        if (string.IsNullOrEmpty(rootDir))
            return false;
        return dirAccessor.DirectoryExists(rootDir);
    }

    /// <param name="rootDir">主目录路径。null / 空字符串 / 不存在 → 空列表。</param>
    /// <param name="dirAccessor">目录访问抽象。</param>
    public static IReadOnlyList<GameTree> Scan(string? rootDir, IGameDirAccessor dirAccessor)
    {
        if (!RootExists(rootDir, dirAccessor))
            return Array.Empty<GameTree>();

        string[] subDirs;
        try
        {
            subDirs = dirAccessor.GetDirectories(rootDir!);
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<GameTree>();
        }

        var games = new List<GameTree>();
        foreach (var subDir in subDirs)
        {
            if (!dirAccessor.DirectoryExists(dirAccessor.CombinePath(subDir, "csv")) ||
                !dirAccessor.DirectoryExists(dirAccessor.CombinePath(subDir, "erb")))
                continue;
            games.Add(new GameTree(subDir, dirAccessor));
        }

        return games.OrderBy(game => game.Name, StringComparer.Ordinal).ToList();
    }
}
