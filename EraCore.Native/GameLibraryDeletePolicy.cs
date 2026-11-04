using System.Text.Json;

namespace EraCore.Native;

/// <summary>条目来源：原目录（只登记）或已复制到私有目录。</summary>
public enum GameDeleteSource
{
    Path,
    Internal,
}

/// <summary>删除判定结果的原因码；<see cref="None"/> 表示允许。</summary>
public enum GameDeleteRejectReason
{
    None,
    NotInternal,
    InvalidName,
    InvalidPath,
    OutsideRoot,
    NotDirectChild,
    Missing,
}

/// <summary>删除判定：允许时带规范化后的目标目录；拒绝时带原因码与可展示消息。</summary>
public sealed record GameDeleteDecision(
    bool Allowed,
    GameDeleteRejectReason Reason,
    string? Message,
    string? DirectoryName,
    string? TargetFullPath)
{
    public static GameDeleteDecision Allow(string directoryName, string targetFullPath) =>
        new(true, GameDeleteRejectReason.None, null, directoryName, targetFullPath);

    public static GameDeleteDecision Reject(GameDeleteRejectReason reason, string message) =>
        new(false, reason, message, null, null);
}

/// <summary>删除执行结果：失败时 <see cref="Error"/> 可展示；成功时记录是否清掉游玩键。</summary>
public sealed record GameDeleteOutcome(bool Success, string? Error, bool PlayedRecordRemoved)
{
    public static GameDeleteOutcome Failure(string error) => new(false, error, false);
}

/// <summary>
/// 游戏库删除的平台无关判定与执行：只接受路径字符串、来源枚举与条目名，
/// 不依赖 Android API，供 Android 宿主与 Windows 单测共用。
///
/// <para>安全边界：目标名先经 <see cref="SanitizeName"/> 校验；目标由 gamesRoot 拼出后
/// 用 <c>Path.GetFullPath</c> 规范化，要求以 gamesRoot 前缀开头、父目录恰为 gamesRoot；
/// 若目标本身是符号链接，还需解析真实路径确认仍在 gamesRoot 内。原目录来源一律拒绝。</para>
/// </summary>
public static class GameLibraryDeletePolicy
{
    /// <summary>内置示例目录名：删除后由启动时的 EnsureBuiltInGames 重新解压。</summary>
    public static readonly IReadOnlyList<string> BuiltInGameNames = new[] { "test_game", "test_game_b" };

    /// <summary>目录名净化：替换非法文件名字符与分隔符；空名回落为 <c>game</c>。</summary>
    public static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "game";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "game" : sanitized;
    }

    /// <summary>是否内置示例目录名（精确匹配，大小写敏感）。</summary>
    public static bool IsBuiltInGame(string? name)
    {
        if (name is null)
            return false;
        foreach (var builtIn in BuiltInGameNames)
        {
            if (string.Equals(builtIn, name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>按条目名判定能否删除；仅 <see cref="GameDeleteSource.Internal"/> 且名字合法、路径在 gamesRoot 内才允许。</summary>
    public static GameDeleteDecision Evaluate(string gamesRoot, GameDeleteSource source, string name)
    {
        if (source != GameDeleteSource.Internal)
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.NotInternal,
                "只有「已复制」游戏可以删除；原目录请用「移除记录」。");

        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || !string.Equals(SanitizeName(name), name, StringComparison.Ordinal))
        {
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.InvalidName,
                "目录名不合法，已拒绝删除。");
        }

        string root;
        string target;
        try
        {
            root = Normalize(gamesRoot);
            target = Path.GetFullPath(Path.Combine(root, name));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.InvalidPath,
                "路径解析失败：" + ex.Message);
        }

        return EvaluateTarget(root, source, target, name);
    }

    /// <summary>
    /// 直接校验一个已拼好的目标路径（测试与防御性调用用）；允许时 <see cref="GameDeleteDecision.DirectoryName"/>
    /// 取目标目录的末段名。
    /// </summary>
    public static GameDeleteDecision EvaluateTarget(string gamesRoot, GameDeleteSource source, string targetPath)
    {
        if (source != GameDeleteSource.Internal)
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.NotInternal,
                "只有「已复制」游戏可以删除；原目录请用「移除记录」。");

        string root;
        string target;
        try
        {
            root = Normalize(gamesRoot);
            target = Path.GetFullPath(targetPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.InvalidPath,
                "路径解析失败：" + ex.Message);
        }

        return EvaluateTarget(root, source, target, Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar)));
    }

    /// <summary>
    /// 真删：递归删除目标目录，再清 library.json 的 <c>dir:&lt;name&gt;</c> 键。
    /// 目录内的 <c>.extracted-v1</c> 随递归删除一并消失。
    /// </summary>
    public static GameDeleteOutcome Delete(GameDeleteDecision decision, string libraryJsonPath)
    {
        if (!decision.Allowed || decision.TargetFullPath is null || decision.DirectoryName is null)
            return GameDeleteOutcome.Failure(decision.Message ?? "删除被拒绝。");

        var target = decision.TargetFullPath;
        if (!Directory.Exists(target))
            return GameDeleteOutcome.Failure("目录不存在：" + decision.DirectoryName);

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return GameDeleteOutcome.Failure("删除目录失败：" + ex.Message);
        }

        var recordRemoved = RemovePlayed(libraryJsonPath, "dir:" + decision.DirectoryName);
        return new GameDeleteOutcome(true, null, recordRemoved);
    }

    /// <summary>只删除 library.json 中的指定键；其他键（含旧版裸名字键）原样保留。</summary>
    public static bool RemovePlayed(string libraryJsonPath, string key)
    {
        if (!File.Exists(libraryJsonPath))
            return false;

        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(libraryJsonPath));
            if (raw is null || !raw.Remove(key))
                return false;
            File.WriteAllText(libraryJsonPath, JsonSerializer.Serialize(raw));
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static GameDeleteDecision EvaluateTarget(
        string root,
        GameDeleteSource source,
        string target,
        string directoryName)
    {
        if (!IsStrictlyInside(root, target))
        {
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.OutsideRoot,
                "目标路径越界，已拒绝删除。");
        }

        var parent = Path.GetDirectoryName(target);
        if (parent is null || !string.Equals(Path.GetFullPath(parent), root, StringComparison.Ordinal))
        {
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.NotDirectChild,
                "目标不是 games/ 的直接子目录，已拒绝删除。");
        }

        if (!Directory.Exists(target))
        {
            return GameDeleteDecision.Reject(
                GameDeleteRejectReason.Missing,
                "目录不存在：" + directoryName);
        }

        // 符号链接的规范化路径仍在 gamesRoot 内，但真实目标可能在外面；解析后必须再校验。
        if (IsReparsePoint(target))
        {
            string? resolved;
            try
            {
                resolved = Directory.ResolveLinkTarget(target, returnFinalTarget: true)?.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return GameDeleteDecision.Reject(
                    GameDeleteRejectReason.InvalidPath,
                    "无法校验目标真实路径，已拒绝删除。");
            }

            if (resolved is not null && !IsStrictlyInside(root, Path.GetFullPath(resolved)))
            {
                return GameDeleteDecision.Reject(
                    GameDeleteRejectReason.OutsideRoot,
                    "目标路径越界，已拒绝删除。");
            }
        }

        return GameDeleteDecision.Allow(directoryName, target);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <param name="root">已规范化的 gamesRoot。</param>
    /// <param name="path">已规范化的候选路径。</param>
    private static bool IsStrictlyInside(string root, string path)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length
            && path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
