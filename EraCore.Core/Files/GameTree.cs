using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MinorShift.Emuera.Files;

/// <summary>
/// 以游戏根为界的文件门面：宿主只用相对路径访问游戏目录，不再拼绝对路径或 SAF URI。
///
/// <para>所有 IO 经 <see cref="IGameDirAccessor"/>（不变量 F-1），路径语义不泄漏到宿主；
/// 列目录 / 列文件返回的相对路径可原样回传给本门面的读写方法。</para>
/// <para>相对路径逃逸（绝对路径、<c>..</c>、content URI）在 <see cref="Resolve"/> 处拒绝。</para>
/// </summary>
public sealed class GameTree
{
    private readonly IGameDirAccessor _accessor;

    /// <param name="root">游戏根——本地路径或平台访问实现认识的根 URI。</param>
    /// <param name="accessor">与 <paramref name="root"/> 形态匹配的目录访问实现。</param>
    public GameTree(string root, IGameDirAccessor accessor)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(accessor);
        Root = root;
        _accessor = accessor;
    }

    /// <summary>游戏根（本地绝对路径或 SAF 根 URI）——仅供宿主标识/转交，不能用于拼相对路径。</summary>
    public string Root { get; }

    /// <summary>本门面的目录访问实现——issue-008：无会话作用域的宿主请求流（HTTP /assets）经会话文件门面取用。</summary>
    internal IGameDirAccessor DirAccessor => _accessor;

    /// <summary>游戏目录名（UI 列表展示用）。</summary>
    public string Name => _accessor.GetFileName(Root);

    /// <summary>当前游戏根是否可写（SAF 缺写权限时为 false）。</summary>
    public bool CanWrite => _accessor.HasWriteAccess();

    /// <summary>相对路径对应的文件或目录是否存在。空路径 = 游戏根自身。</summary>
    public bool Exists(string relativePath = "")
    {
        var path = Resolve(relativePath);
        return _accessor.DirectoryExists(path) || _accessor.FileExists(path);
    }

    /// <summary>列直接子目录，返回相对路径（<c>/</c> 分隔，序数排序）。目录不存在返回空。</summary>
    public IReadOnlyList<string> ListDirectories(string relativePath = "")
        => List(relativePath, searchPattern: null);

    /// <summary>列直接子文件，返回相对路径（<c>/</c> 分隔，序数排序）。目录不存在返回空。</summary>
    public IReadOnlyList<string> ListFiles(string relativePath = "", string searchPattern = "*")
        => List(relativePath, searchPattern);

    public string ReadText(string relativePath) => _accessor.ReadAllText(Resolve(relativePath));

    public byte[]? ReadBytes(string relativePath) => _accessor.ReadAllBytes(Resolve(relativePath));

    public Stream? OpenRead(string relativePath) => _accessor.OpenRead(Resolve(relativePath));

    /// <summary>创建或截断文件并返回写流；父目录不存在时由访问实现补建。</summary>
    public Stream Create(string relativePath) => _accessor.OpenWrite(Resolve(relativePath));

    /// <summary>删除文件；不存在则 no-op。</summary>
    public void Delete(string relativePath) => _accessor.Delete(Resolve(relativePath));

    private string[] List(string relativePath, string? searchPattern)
    {
        var dir = Resolve(relativePath);
        if (!_accessor.DirectoryExists(dir))
            return [];

        string[] entries;
        try
        {
            entries = searchPattern == null
                ? _accessor.GetDirectories(dir)
                : _accessor.GetFiles(dir, searchPattern, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }

        var prefix = Normalize(relativePath);
        return entries
            .Select(entry => _accessor.GetFileName(entry))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => prefix.Length == 0 ? name : prefix + "/" + name)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 相对路径 → 访问实现路径。拒绝三类逃逸：content URI、绝对路径、<c>..</c> 段。
    /// </summary>
    private string Resolve(string relativePath)
    {
        var path = Root;
        foreach (var segment in Normalize(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries))
            path = _accessor.CombinePath(path, segment);
        return path;
    }

    /// <summary>校验并规整相对路径：反斜杠归一为 <c>/</c>，丢弃 <c>.</c> 与空段。</summary>
    private static string Normalize(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return string.Empty;

        if (relativePath.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("相对路径不能是 URI。", nameof(relativePath));
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("相对路径不能是绝对路径。", nameof(relativePath));

        var segments = new List<string>();
        foreach (var raw in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".")
                continue;
            if (raw == "..")
                throw new ArgumentException("相对路径不能越出游戏根（包含 '..'）。", nameof(relativePath));
            segments.Add(raw);
        }
        return string.Join('/', segments);
    }
}
