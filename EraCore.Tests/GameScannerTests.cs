using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Linq;
using MinorShift.Emuera;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// game-library spec Seam 1 + Seam 2：GameScanner / DirectoryLister 单测。
///
/// <para>测试哲学：只测外部可观察行为，不测实现细节。两个模块都是纯 C# 无平台依赖，
/// 用临时目录构造目录树后断言返回值。</para>
///
/// <para><see cref="GameScanner"/> 用例（spec ID1）：</para>
/// <list type="number">
///   <item>rootDir 不存在 → 空列表</item>
///   <item>rootDir 为 null/空 → 空列表</item>
///   <item>有效游戏（csv+erb）→ 条目正确</item>
///   <item>缺 csv → 不列入</item>
///   <item>缺 erb → 不列入</item>
///   <item>csv/erb 是文件而非目录 → 不列入</item>
///   <item>多个游戏 → 按名称排序</item>
///   <item>子目录有非法字符 → Directory.GetDirectories 返回正常，不抛</item>
/// </list>
///
/// <para><see cref="DirectoryLister"/> 用例（spec ID2）：</para>
/// <list type="number">
///   <item>不存在路径 → 空列表 + parent = null（rootDir 本身已无效）</item>
///   <item>根路径（根保护）→ parent = null</item>
///   <item>混合文件和目录 → 只列目录</item>
///   <item>无子目录 → 空列表 + parent 不为 null</item>
///   <item>子目录按名称排序</item>
/// </list>
/// </summary>
public class GameScannerTests
{
    // ==================== GameScanner.Scan ====================

    [Fact]
    public void Scan_returns_empty_when_rootDir_does_not_exist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "emuera-scan-missing-" + Guid.NewGuid().ToString("N"));
        var result = GameScanner.Scan(missing, new FileSystemGameDirAccessor());
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_returns_empty_when_rootDir_is_null_or_empty()
    {
        Assert.Empty(GameScanner.Scan(null, new FileSystemGameDirAccessor()));
        Assert.Empty(GameScanner.Scan("", new FileSystemGameDirAccessor()));
        Assert.Empty(GameScanner.Scan("   ", new FileSystemGameDirAccessor()));
    }

    [Fact]
    public void Scan_returns_entry_for_valid_game_with_csv_and_erb()
    {
        using var tmp = new TempRoot();
        var gameDir = Path.Combine(tmp.Root, "game1");
        Directory.CreateDirectory(Path.Combine(gameDir, "csv"));
        Directory.CreateDirectory(Path.Combine(gameDir, "erb"));

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());

        var entry = Assert.Single(result);
        Assert.Equal("game1", entry.Name);
        Assert.Equal(gameDir, entry.FullPath);
    }

    [Fact]
    public void Scan_skips_dir_missing_csv()
    {
        using var tmp = new TempRoot();
        var gameDir = Path.Combine(tmp.Root, "not-a-game");
        Directory.CreateDirectory(Path.Combine(gameDir, "erb"));
        // 缺 csv/

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_skips_dir_missing_erb()
    {
        using var tmp = new TempRoot();
        var gameDir = Path.Combine(tmp.Root, "not-a-game");
        Directory.CreateDirectory(Path.Combine(gameDir, "csv"));
        // 缺 erb/

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_skips_when_csv_or_erb_is_file_not_directory()
    {
        using var tmp = new TempRoot();
        var gameDir = Path.Combine(tmp.Root, "fake-game");
        Directory.CreateDirectory(gameDir);
        // csv 是文件而非目录——Directory.Exists 返 false 跳过
        File.WriteAllText(Path.Combine(gameDir, "csv"), "not a dir");
        Directory.CreateDirectory(Path.Combine(gameDir, "erb"));

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_returns_multiple_games_sorted_by_name()
    {
        using var tmp = new TempRoot();
        CreateGame(tmp.Root, "zelda");
        CreateGame(tmp.Root, "aaa-game");
        CreateGame(tmp.Root, "mid-game");

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "aaa-game", "mid-game", "zelda" }, result.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Scan_does_not_recurse_into_subdirectories()
    {
        using var tmp = new TempRoot();
        // 在 root 下创建一个非游戏子目录，其内部嵌套一个有效游戏——不应被扫描到
        var outer = Path.Combine(tmp.Root, "outer");
        Directory.CreateDirectory(outer);
        CreateGame(outer, "nested-game"); // 直接在 outer 下创建 csv+erb

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());
        // outer 自身无 csv/erb → 不列入；nested-game 在 outer 下 → 不递归发现
        Assert.Empty(result);
    }

    [Fact]
    public void Scan_includes_game_with_empty_csv_and_erb_directories()
    {
        // 空目录也是合法目录——Directory.Exists 返 true，扫描器不读文件内容
        using var tmp = new TempRoot();
        CreateGame(tmp.Root, "empty-game");

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());
        var entry = Assert.Single(result);
        Assert.Equal("empty-game", entry.Name);
    }

    [Fact]
    public void Scan_handles_subdirectory_with_unusual_names_without_throwing()
    {
        // 子目录名带空格 / 数字 / Unicode 字符——Directory.GetDirectories 返回正常
        using var tmp = new TempRoot();
        CreateGame(tmp.Root, "game with space");
        CreateGame(tmp.Root, "游戏1");
        CreateGame(tmp.Root, "123");

        var result = GameScanner.Scan(tmp.Root, new FileSystemGameDirAccessor());
        Assert.Equal(3, result.Count);
    }

    // ==================== DirectoryLister.ListDirectories ====================

    [Fact]
    public void ListDirectories_returns_empty_for_null_or_empty_path()
    {
        var r1 = DirectoryLister.ListDirectories(null, new FileSystemGameDirAccessor());
        Assert.Equal(string.Empty, r1.CurrentPath);
        Assert.Null(r1.ParentPath);
        Assert.Empty(r1.SubDirectories);

        var r2 = DirectoryLister.ListDirectories("", new FileSystemGameDirAccessor());
        Assert.Empty(r2.SubDirectories);
    }

    [Fact]
    public void ListDirectories_returns_empty_subDirs_and_null_parent_for_nonexistent_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), "emuera-lister-missing-" + Guid.NewGuid().ToString("N"));
        // 注意：missing 的父（Path.GetTempPath）存在——parent 不为 null
        var result = DirectoryLister.ListDirectories(missing, new FileSystemGameDirAccessor());

        Assert.Equal(missing, result.CurrentPath);
        Assert.Empty(result.SubDirectories);
        // parent 应为 Path.GetTempPath（存在），不为 null
        Assert.NotNull(result.ParentPath);
    }

    [Fact]
    public void ListDirectories_returns_only_subdirectories_excluding_files()
    {
        using var tmp = new TempRoot();
        var sub1 = Path.Combine(tmp.Root, "sub1");
        var sub2 = Path.Combine(tmp.Root, "sub2");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);
        // 在 tmp.Root 下混入文件——不应列入 SubDirectories
        File.WriteAllText(Path.Combine(tmp.Root, "file1.txt"), "x");
        File.WriteAllText(Path.Combine(tmp.Root, "file2.txt"), "x");

        var result = DirectoryLister.ListDirectories(tmp.Root, new FileSystemGameDirAccessor());

        Assert.Equal(tmp.Root, result.CurrentPath);
        Assert.Equal(new[] { "sub1", "sub2" }, result.SubDirectories.ToArray());
    }

    [Fact]
    public void ListDirectories_returns_empty_subDirs_when_no_subdirectories()
    {
        using var tmp = new TempRoot();
        // tmp.Root 存在但无子目录
        var result = DirectoryLister.ListDirectories(tmp.Root, new FileSystemGameDirAccessor());

        Assert.Equal(tmp.Root, result.CurrentPath);
        Assert.Empty(result.SubDirectories);
        // tmp.Root 的父（Path.GetTempPath）存在——parent 不为 null
        Assert.NotNull(result.ParentPath);
    }

    [Fact]
    public void ListDirectories_returns_parent_for_nested_path()
    {
        using var tmp = new TempRoot();
        var sub1 = Path.Combine(tmp.Root, "sub1");
        var sub2 = Path.Combine(sub1, "sub2");
        Directory.CreateDirectory(sub2);

        var result = DirectoryLister.ListDirectories(sub2, new FileSystemGameDirAccessor());

        Assert.Equal(sub2, result.CurrentPath);
        Assert.Equal(sub1, result.ParentPath);
        Assert.Empty(result.SubDirectories);
    }

    [Fact]
    public void ListDirectories_returns_null_parent_at_root()
    {
        // 选用系统根——Path.GetPathRoot 得到的根路径
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        // 在 Windows 上是 "C:\"，Linux 上是 "/"
        var result = DirectoryLister.ListDirectories(root, new FileSystemGameDirAccessor());

        Assert.Null(result.ParentPath);
    }

    [Fact]
    public void ListDirectories_sorts_subDirs_by_name()
    {
        using var tmp = new TempRoot();
        // 故意以非字母序创建
        Directory.CreateDirectory(Path.Combine(tmp.Root, "zebra"));
        Directory.CreateDirectory(Path.Combine(tmp.Root, "alpha"));
        Directory.CreateDirectory(Path.Combine(tmp.Root, "mid"));

        var result = DirectoryLister.ListDirectories(tmp.Root, new FileSystemGameDirAccessor());
        Assert.Equal(new[] { "alpha", "mid", "zebra" }, result.SubDirectories.ToArray());
    }

    // ==================== 辅助方法 ====================

    private static void CreateGame(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "csv"));
        Directory.CreateDirectory(Path.Combine(dir, "erb"));
    }

    private sealed class TempRoot : IDisposable
    {
        public string Root { get; }
        private bool _disposed;

        public TempRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "emuera-scanner-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* 测试临时目录清理失败不致测试失败 */ }
        }
    }
}
