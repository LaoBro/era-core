using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 模拟 SAF <c>content://</c> 的目录访问器：路径是 document URI，而非本地文件系统路径。
///
/// <para>契约测试用它证明「同一套访问器行为在两种路径语义下一致」——本地实现封装
/// <see cref="File"/> / <see cref="Directory"/>，本实现只用内存字典与 URI 拼接。
/// 两者都不允许调用方假设路径能被 <see cref="Path"/> 解析。</para>
/// </summary>
internal sealed class InMemoryContentDirAccessor : IGameDirAccessor
{
    public const string DefaultRoot =
        "content://com.example.documents/tree/primary%3Agames/document/primary%3Agames";

    private const string DocumentMarker = "/document/";

    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public InMemoryContentDirAccessor(string root = DefaultRoot)
    {
        Root = root;
        _directories.Add(root);
    }

    public string Root { get; }

    public int OpenReadCalls { get; private set; }
    public int OpenWriteCalls { get; private set; }
    public int DeleteCalls { get; private set; }

    /// <summary>模拟 SAF 的写权限状态（缺写时宿主应引导重新选目录）。</summary>
    public bool WriteAccess { get; set; } = true;

    // ── 目录枚举 ──────────────────────────────────

    public bool DirectoryExists(string path) => _directories.Contains(path);

    public string[] GetDirectories(string path) => Enumerate(path, isDir: true, pattern: null, recursive: false);

    public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption)
        => Enumerate(path, isDir: true, pattern: searchPattern, recursive: searchOption == SearchOption.AllDirectories);

    // ── 文件枚举 ──────────────────────────────────

    public bool FileExists(string path) => _files.ContainsKey(path);

    public string[] GetFiles(string path) => Enumerate(path, isDir: false, pattern: null, recursive: false);

    public string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
        => Enumerate(path, isDir: false, pattern: searchPattern, recursive: searchOption == SearchOption.AllDirectories);

    // ── 文件读取 ──────────────────────────────────

    public string ReadAllText(string path)
    {
        if (!_files.TryGetValue(path, out var bytes))
            throw new FileNotFoundException($"In-memory SAF file not found: {path}");
        return Encoding.UTF8.GetString(bytes);
    }

    public byte[]? ReadAllBytes(string path) => _files.TryGetValue(path, out var bytes) ? bytes : null;

    public Stream? OpenRead(string path)
    {
        OpenReadCalls++;
        return _files.TryGetValue(path, out var bytes) ? new MemoryStream(bytes, writable: false) : null;
    }

    // ── 文件/目录写入 ─────────────────────────────

    public void CreateDirectory(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            _directories.Add(current);
            if (string.Equals(current, Root, StringComparison.Ordinal))
                return;
            var parent = GetParentPath(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
                return;
            current = parent;
        }
    }

    public Stream OpenWrite(string path)
    {
        OpenWriteCalls++;
        var parent = GetParentPath(path);
        if (!string.Equals(parent, path, StringComparison.Ordinal))
            CreateDirectory(parent);
        return new CommitStream(bytes => _files[path] = bytes);
    }

    public void Delete(string path)
    {
        DeleteCalls++;
        _files.Remove(path);
    }

    public bool HasWriteAccess() => WriteAccess;

    // ── 路径操作 ──────────────────────────────────

    public string ResolveSubPath(string basePath, string subDir) => CombinePath(basePath, subDir);

    public string CombinePath(string basePath, string filename)
    {
        if (!basePath.Contains(DocumentMarker, StringComparison.Ordinal))
            throw new ArgumentException($"Not a content document URI: {basePath}", nameof(basePath));
        return basePath + "%2F" + Uri.EscapeDataString(filename);
    }

    public string GetParentPath(string path)
    {
        var markerPos = path.LastIndexOf(DocumentMarker, StringComparison.Ordinal);
        if (markerPos < 0)
            return path;
        var prefixLength = markerPos + DocumentMarker.Length;
        var tail = path[prefixLength..];
        var separator = tail.LastIndexOf("%2F", StringComparison.Ordinal);
        return separator < 0 ? path : path[..(prefixLength + separator)];
    }

    public string GetFileName(string path)
    {
        var docId = Runtime.Utils.SafPath.TryGetDocumentId(path);
        if (docId == null)
            return path;
        var slash = docId.LastIndexOf('/');
        return slash >= 0 ? docId[(slash + 1)..] : docId;
    }

    // ── 测试注入 ──────────────────────────────────

    public void Seed(string path, byte[] bytes)
    {
        var parent = GetParentPath(path);
        if (!string.Equals(parent, path, StringComparison.Ordinal))
            CreateDirectory(parent);
        _files[path] = bytes;
    }

    private string[] Enumerate(string path, bool isDir, string? pattern, bool recursive)
    {
        IEnumerable<string> candidates = isDir ? _directories : _files.Keys;

        if (recursive)
        {
            var prefix = path + "%2F";
            candidates = candidates.Where(item =>
                item.Length > prefix.Length && item.StartsWith(prefix, StringComparison.Ordinal));
        }
        else
        {
            candidates = candidates.Where(item =>
                item != path && string.Equals(GetParentPath(item), path, StringComparison.Ordinal));
        }

        if (pattern != null)
            candidates = candidates.Where(item => Wildcard.Matches(GetFileName(item), pattern));

        return candidates.ToArray();
    }

    private sealed class CommitStream(Action<byte[]> commit) : MemoryStream
    {
        private bool _committed;

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_committed)
            {
                _committed = true;
                commit(ToArray());
            }
            base.Dispose(disposing);
        }
    }
}
