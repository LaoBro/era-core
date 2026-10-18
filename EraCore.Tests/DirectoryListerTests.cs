using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 目录列举的外侧行为锁——重点盯 <c>ComputeParentPath</c>：
/// 该实现从裸 <c>Directory.GetParent</c> 换成经 <see cref="IGameDirAccessor.GetParentPath"/>（不变量 F-1）。
/// 二者的计算语义有一处差异（尾部分隔符处理），故以「与 <c>Directory.GetParent</c> 等价」作为回归锚点。
/// </summary>
public class DirectoryListerTests : IDisposable
{
    private readonly string _root;
    private readonly RecordingAccessor _accessor = new();

    public DirectoryListerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "emuera-dirlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* 临时目录清理失败不致测试失败 */ }
    }

    [Fact]
    public void ParentPath_matches_the_old_GetParent_semantics()
    {
        var nested = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(nested);

        // 不带尾部分隔符
        AssertParent(nested);
        // 带尾部分隔符——旧实现 Directory.GetParent 仍返上层，换 GetParentPath 后必须同样如此
        AssertParent(nested + Path.DirectorySeparatorChar);
        // 不存在的目录：父路径照旧算出（结果体仍为空列表）
        AssertParent(Path.Combine(_root, "missing", "deep"));
    }

    [Fact]
    public void ListDirectories_lists_child_directory_names_sorted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zulu"));
        Directory.CreateDirectory(Path.Combine(_root, "alpha"));

        var result = DirectoryLister.ListDirectories(_root, _accessor);

        Assert.Equal(["alpha", "zulu"], result.SubDirectories);
    }

    [Fact]
    public void ListDirectories_returns_empty_for_null_or_blank_path()
    {
        var result = DirectoryLister.ListDirectories(null, _accessor);
        Assert.Equal(string.Empty, result.CurrentPath);
        Assert.Null(result.ParentPath);
        Assert.Empty(result.SubDirectories);
    }

    /// <summary>
    /// F-1 的可断言形态：父路径必须经访问器求得，而不是径直回到 <c>Directory.GetParent</c>。
    /// 否则「接口层面改了、实现层面照旧直连文件系统」这种假收敛无法被源码扫描发现。
    /// </summary>
    [Fact]
    public void ParentPath_is_obtained_through_the_accessor()
    {
        var nested = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(nested);

        DirectoryLister.ListDirectories(nested, _accessor);

        Assert.Contains("GetParentPath", _accessor.Calls);
    }

    private static void AssertParent(string path)
    {
        var expected = Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        // 旧语义：已是文件系统根时 GetParent 返回自身的对比结果为 null（彼时也如此）。
        var actual = DirectoryLister.ListDirectories(path, new RecordingAccessor()).ParentPath;
        Assert.Equal(expected, actual);
    }

    /// <summary>记录被调 method 的访问器替身（其余成员退化为「本地文件系统」语义）。</summary>
    private sealed class RecordingAccessor : IGameDirAccessor
    {
        public List<string> Calls { get; } = [];

        public Task<string?> PickDirectoryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public bool DirectoryExists(string path) => Directory.Exists(path);
        public string[] GetDirectories(string path) => Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        public string[] GetDirectories(string path, string searchPattern, SearchOption searchOption) => GetDirectories(path);
        public bool FileExists(string path) => File.Exists(path);
        public string[] GetFiles(string path) => [];
        public string[] GetFiles(string path, string searchPattern, SearchOption searchOption) => [];
        public string ReadAllText(string path) => throw new NotSupportedException();
        public byte[]? ReadAllBytes(string path) => throw new NotSupportedException();
        public Stream? OpenRead(string path) => throw new NotSupportedException();
        public void CreateDirectory(string path) => throw new NotSupportedException();
        public Stream OpenWrite(string path) => throw new NotSupportedException();
        public void Delete(string path) => throw new NotSupportedException();
        public bool HasWriteAccess() => true;
        public string ResolveSubPath(string basePath, string subDir) => Path.Combine(basePath, subDir);
        public string CombinePath(string basePath, string filename) => Path.Combine(basePath, filename);
        public string GetParentPath(string path)
        {
            Calls.Add("GetParentPath");
            return Path.GetDirectoryName(path) ?? path;
        }
        public string GetFileName(string path) => Path.GetFileName(path);
    }
}
