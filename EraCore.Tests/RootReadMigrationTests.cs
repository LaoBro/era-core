using System;
using System.IO;
using System.Linq;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-009：显式根读取 + 单例不复活守卫。
///
/// <para>钉住两件事：
/// <list type="bullet">
/// <item><b>显式根优先</b>——<see cref="ConfigData"/> 绑定构造时传入的根；
/// <see cref="Program.DirAccessor"/>、语言资源列表与 <see cref="SafCompat"/> 只读会话作用域，
/// 作用域外访问是显性错误（而不是回落到某个「最后一次提交」的目录）。</item>
/// <item><b>单例不复活</b>——生产代码里不得再出现 <c>GamePaths.Current</c> /
/// <c>SetCurrent</c> / <c>ResolvePreview</c> / <c>GameRootEnvironment.Commit</c> 的代码引用
/// （注释里的历史说明不算）。</item>
/// </list></para>
///
/// <para>作用域是 AsyncLocal，随每个测试的 ExecutionContext 隔离——本类不需要串行集合。</para>
/// </summary>
public sealed class RootReadMigrationTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;

    public RootReadMigrationTests()
    {
        _rootA = CreateGameRoot("a", ignoreCase: "NO");
        _rootB = CreateGameRoot("b", ignoreCase: "YES");
    }

    [Fact]
    public void ConfigData_binds_the_root_passed_at_construction()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);
        var configA = new ConfigData(rootA);
        Assert.Same(rootA, configA.Root);

        Assert.True(configA.LoadConfig());
        Assert.False(configA.GetConfigValue<bool>(ConfigCode.IgnoreCase));

        var configB = new ConfigData(GamePaths.Resolve(_rootB, accessor));
        Assert.True(configB.LoadConfig());
        Assert.True(configB.GetConfigValue<bool>(ConfigCode.IgnoreCase));
    }

    [Fact]
    public void Compatibility_shim_accessor_prefers_the_session_scope()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);

        using (GameRootEnvironment.Establish(rootA))
            Assert.Same(rootA.DirAccessor, Program.DirAccessor);

        Assert.Throws<InvalidOperationException>(() => _ = Program.DirAccessor);
    }

    [Fact]
    public void Compatibility_shim_paths_prefer_the_session_scope()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);

        using (GameRootEnvironment.Establish(rootA))
        {
            Assert.Equal(rootA.ExeDir, Program.ExeDir);
            Assert.Equal(rootA.CsvDir, Program.CsvDir);
            Assert.Equal(rootA.FontDir, Program.FontDir);
        }

        Assert.Throws<InvalidOperationException>(() => _ = Program.ExeDir);
    }

    [Fact]
    public void Language_files_are_listed_from_the_explicit_root()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);
        var rootB = GamePaths.Resolve(_rootB, accessor);
        WriteLangFile(_rootA, "alpha-lang", "en-GB");
        WriteLangFile(_rootB, "beta-lang", "de-DE");

        try
        {
            // 列表必须来自显式传入的 A——不读任何进程级/作用域状态。
            Lang.LoadLanguageFiles(rootA);
            Assert.Contains("alpha-lang", Lang.GetLangList());
            Assert.DoesNotContain("beta-lang", Lang.GetLangList());
        }
        finally
        {
            // 语言表是静态的：删掉夹具后按原根重载，只留嵌入资源。
            File.Delete(Path.Combine(_rootA, "lang", "emuera.en-GB.xml"));
            File.Delete(Path.Combine(_rootB, "lang", "emuera.de-DE.xml"));
            Lang.LoadLanguageFiles(rootA);
        }
    }

    [Fact]
    public void SafCompat_content_reads_follow_the_session_scope()
    {
        const string contentRootA = InMemoryContentDirAccessor.DefaultRoot + "-a";
        var scopedAccessor = new InMemoryContentDirAccessor(contentRootA);
        var scopedRoot = GamePaths.Resolve(contentRootA, scopedAccessor);

        var savDir = scopedAccessor.ResolveSubPath(contentRootA, "sav");
        var scopedFile = scopedAccessor.CombinePath(savDir, "slot.sav");
        scopedAccessor.Seed(scopedFile, [1]);

        // 只有会话作用域生效时才看得到 A 的文件；作用域外没有根可回落。
        using (GameRootEnvironment.Establish(scopedRoot))
            Assert.True(SafCompat.FileExists(scopedFile));

        Assert.Throws<InvalidOperationException>(() => SafCompat.FileExists(scopedFile));
    }

    [Fact]
    public void SafCompat_game_relative_paths_use_the_session_exe_dir()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);

        using (GameRootEnvironment.Establish(rootA))
        {
            Assert.True(SafCompat.TryResolvePathUnderGameRoot("sav/slot.sav", false, out var scopedPath));
            Assert.Equal(Path.Combine(_rootA, "sav", "slot.sav"), Path.GetFullPath(scopedPath));
        }

        Assert.Throws<InvalidOperationException>(
            () => SafCompat.TryResolvePathUnderGameRoot("sav/slot.sav", false, out _));
    }

    /// <summary>
    /// issue-009 的验收信号：生产代码不得再引用根单例的任何入口。
    /// 迁移完成后仍保留本守卫，防止有人把「最后一次提交」的形态加回来。
    /// </summary>
    [Fact]
    public void Production_sources_have_no_process_level_root_api()
    {
        var root = FindRepositoryRoot();
        var producers = new[] { "EraCore.Core", "EraCore.Server", "EraCore.Cli", "EraCore.Maui" };
        var hits = producers
            .SelectMany(dir => Directory.EnumerateFiles(
                Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(HasCodeReference)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(hits);
    }

    private static readonly string[] ForbiddenTokens =
    [
        "GamePaths.Current",
        "GamePaths.SetCurrent",
        "GamePaths.ResolvePreview",
        "GameRootEnvironment.Commit",
    ];

    private static bool HasCodeReference(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
                continue;
            if (ForbiddenTokens.Any(t => line.Contains(t, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    private static string CreateGameRoot(string tag, string ignoreCase)
    {
        var root = Path.Combine(Path.GetTempPath(), $"emuera_root_read_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "csv"));
        Directory.CreateDirectory(Path.Combine(root, "erb"));
        File.WriteAllText(Path.Combine(root, "csv", "_fixed.config"), $"Ignore case:{ignoreCase}\n");
        return root;
    }

    private static void WriteLangFile(string root, string name, string culture)
    {
        var langDir = Path.Combine(root, "lang");
        Directory.CreateDirectory(langDir);
        File.WriteAllText(
            Path.Combine(langDir, $"emuera.{culture}.xml"),
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><lang><name>{name}</name><culture>{culture}</culture></lang>");
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    public void Dispose()
    {
        foreach (var root in new[] { _rootA, _rootB })
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 尽力清理。
            }
        }
    }
}
