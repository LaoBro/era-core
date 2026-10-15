using System;
using System.IO;
using System.Threading;

namespace MinorShift.Emuera;

/// <summary>应用私有文件根目录。游戏文件继续由 <see cref="GamePaths"/> 与 <see cref="IGameDirAccessor"/> 管理。</summary>
/// <para>
/// 不变量 F-4：本类是<see cref="IGameDirAccessor"/>「游戏树 IO 必走访问器」的<b>唯一豁免域</b>。
/// 豁免曾是无形的（开发者需记得「这是私有目录」才算数），现以 <see cref="IsUnder"/> 提供可机检判定：
/// 守卫测试（<c>SafIoPolicyTests</c>）用它核验「声明为应用私有域」的直连 IO 确实落在本目录之下，
/// 使豁免从约定变为断言。
/// </para>
public static class AppDataPaths
{
    private static string _directory = GetDefaultDirectory();

    public static string Directory => Volatile.Read(ref _directory);

    /// <summary>由平台入口注入应用私有目录；桌面和测试可使用默认目录或显式覆盖。</summary>
    public static void Configure(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("App data directory is required.", nameof(directory));
        Volatile.Write(ref _directory, Path.GetFullPath(directory));
    }

    public static string CombinePath(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>确保私有根存在（幂等）。替各调用点屏蔽裸 <c>Directory.CreateDirectory</c>。</summary>
    public static void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>
    /// 判定 <paramref name="path"/> 是否落在应用私有域内（F-4 豁免域的可机检形式）。
    /// <para>
    /// 判定对相对路径 / 未归一化输入同样成立：先归一化，再按「等于根 或 以根 + 分隔符开头」比较。
    /// 目录分隔符的比较在 Windows/macOS 上忽略大小写（这两类挂载默认大小写不敏感）。
    /// 相对路径与不可归一化输入（非法字符 / 超长）判 false——豁免域判定失败要<b>可见</b>，不能默认放行。
    /// </para>
    /// </summary>
    public static bool IsUnder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var root = TrimSeparators(Directory);
        string target;
        try
        {
            target = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }

        if (target.StartsWith("content://", StringComparison.Ordinal))
            return false; // SAF URI 永不属私有域（ exempt 域都是真实本地路径）

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!target.StartsWith(root, comparison))
            return false;
        return target.Length == root.Length
            || target[root.Length] == Path.DirectorySeparatorChar
            || target[root.Length] == Path.AltDirectorySeparatorChar;
    }

    private static string TrimSeparators(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? root : trimmed;
    }

    private static string GetDefaultDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = AppContext.BaseDirectory;
        return Path.Combine(localAppData, "Emuera");
    }
}
