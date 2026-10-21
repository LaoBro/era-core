using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinorShift.Emuera.Files;

/// <summary>
/// game-library spec ID1：扫描主目录下的有效游戏（含 csv+erb 子目录的文件夹）。
/// </summary>
internal static class GameScanner
{
    /// <summary>
    /// 判断主目录是否存在。
    ///
    /// <para>必须通过 <see cref="IGameDirAccessor"/> 判断；SAF 的 <c>content://</c>
    /// URI 不是本地文件系统路径，不能调用 <see cref="Directory.Exists(string)"/>。</para>
    /// </summary>
    public static bool RootDirectoryExists(string? rootDir, IGameDirAccessor dirAccessor)
    {
        if (string.IsNullOrEmpty(rootDir))
            return false;
        return dirAccessor.DirectoryExists(rootDir);
    }

    /// <summary>
    /// 扫描 rootDir 下所有子目录，检出含 <c>csv/</c> + <c>erb/</c> 的有效游戏。
    /// </summary>
    /// <param name="rootDir">主目录路径。null / 空字符串 → 返空列表。</param>
    /// <param name="dirAccessor">目录访问抽象。</param>
    /// <returns>合法游戏列表，按 <see cref="GameEntry.Name"/> 升序排序。</returns>
    public static IReadOnlyList<GameEntry> Scan(string? rootDir, IGameDirAccessor dirAccessor)
    {
        if (string.IsNullOrEmpty(rootDir))
            return Array.Empty<GameEntry>();
        if (!RootDirectoryExists(rootDir, dirAccessor))
            return Array.Empty<GameEntry>();

        string[] subDirs;
        try
        {
            subDirs = dirAccessor.GetDirectories(rootDir);
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<GameEntry>();
        }

        var entries = new List<GameEntry>();
        foreach (var subDir in subDirs)
        {
            var csvDir = dirAccessor.CombinePath(subDir, "csv");
            var erbDir = dirAccessor.CombinePath(subDir, "erb");
            if (!dirAccessor.DirectoryExists(csvDir) || !dirAccessor.DirectoryExists(erbDir))
                continue;
            var rawName = dirAccessor.GetFileName(subDir);
            var name = rawName;
            entries.Add(new GameEntry(name, subDir));
        }

        return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// 扫描结果中的单条游戏条目——game-library spec ID1。
/// </summary>
/// <param name="Name">游戏目录名，UI 列表展示用。</param>
/// <param name="FullPath">游戏目录绝对路径（或 SAF URI），传给 loadGameFromPath 加载。</param>
internal readonly record struct GameEntry(string Name, string FullPath);
