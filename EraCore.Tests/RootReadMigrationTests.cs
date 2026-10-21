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
/// issue-008：根选择回归 + 剩余直读点守卫。
///
/// <para>钉住两件事：
/// <list type="bullet">
/// <item><b>会话作用域优先</b>——构造期捕获的 <see cref="ConfigData"/> 重载不跟随装配期视图漂移；
/// <see cref="Program.DirAccessor"/>、语言资源列表与 <see cref="SafCompat"/> 均读会话根。</item>
/// <item><b>直读点只减不增</b>——引擎侧剩余 <c>GamePaths.Current</c> 代码引用只允许出现在
/// 未迁移批次与兼容垫片文件里；每批迁移后从白名单移除对应文件。</item>
/// </list></para>
///
/// <para>与 <c>ServerState</c> 同 collection：本文件提交装配期根（进程级视图），须与其他
/// 改写根的测试串行。</para>
/// </summary>
[Collection("ServerState")]
public sealed class RootReadMigrationTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly GamePaths? _previousRoot;

    public RootReadMigrationTests()
    {
        _previousRoot = GameRootEnvironment.Current;
        _rootA = CreateGameRoot("a", ignoreCase: "NO");
        _rootB = CreateGameRoot("b", ignoreCase: "YES");
    }

    [Fact]
    public void ConfigData_reloads_from_the_root_captured_at_construction()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);
        var configA = new ConfigData();
        Assert.Same(rootA, configA.Root);

        // 装配期视图漂移到 B——旧实现的重载会跟着 B 的 csv 走。
        GamePaths.Resolve(_rootB, accessor);

        Assert.True(configA.LoadConfig());
        Assert.False(configA.GetConfigValue<bool>(ConfigCode.IgnoreCase));

        var configB = new ConfigData();
        Assert.True(configB.LoadConfig());
        Assert.True(configB.GetConfigValue<bool>(ConfigCode.IgnoreCase));
    }

    [Fact]
    public void Compatibility_shim_accessor_prefers_the_session_scope()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.ResolvePreview(_rootA, accessor);
        var rootB = GamePaths.Resolve(_rootB, accessor);

        using (GameRootEnvironment.Establish(rootA))
            Assert.Same(rootA.DirAccessor, Program.DirAccessor);

        Assert.Same(rootB.DirAccessor, Program.DirAccessor);
    }

    [Fact]
    public void Compatibility_shim_paths_prefer_the_session_scope()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.ResolvePreview(_rootA, accessor);
        var rootB = GamePaths.Resolve(_rootB, accessor);

        using (GameRootEnvironment.Establish(rootA))
        {
            Assert.Equal(rootA.ExeDir, Program.ExeDir);
            Assert.Equal(rootA.CsvDir, Program.CsvDir);
            Assert.Equal(rootA.FontDir, Program.FontDir);
        }

        Assert.Equal(rootB.ExeDir, Program.ExeDir);
        Assert.Equal(rootB.CsvDir, Program.CsvDir);
        Assert.Equal(rootB.FontDir, Program.FontDir);
    }

    [Fact]
    public void Language_files_are_listed_from_the_explicit_root()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);
        GamePaths.Resolve(_rootB, accessor);
        WriteLangFile(_rootA, "alpha-lang", "en-GB");
        WriteLangFile(_rootB, "beta-lang", "de-DE");

        try
        {
            // 装配期视图 = B，列表必须来自显式传入的 A。
            Lang.LoadLanguageFiles(rootA);
            Assert.Contains("alpha-lang", Lang.GetLangList());
            Assert.DoesNotContain("beta-lang", Lang.GetLangList());
        }
        finally
        {
            // 语言表是静态的：删掉夹具后按原根重载，只留嵌入资源。
            File.Delete(Path.Combine(_rootA, "lang", "emuera.en-GB.xml"));
            File.Delete(Path.Combine(_rootB, "lang", "emuera.de-DE.xml"));
            Lang.LoadLanguageFiles(_previousRoot ?? rootA);
        }
    }

    [Fact]
    public void SafCompat_content_reads_follow_the_session_scope()
    {
        const string contentRootA = InMemoryContentDirAccessor.DefaultRoot + "-a";
        const string contentRootB = InMemoryContentDirAccessor.DefaultRoot + "-b";
        var scopedAccessor = new InMemoryContentDirAccessor(contentRootA);
        var committedAccessor = new InMemoryContentDirAccessor(contentRootB);
        var scopedRoot = GamePaths.ResolvePreview(contentRootA, scopedAccessor);
        GameRootEnvironment.Commit(GamePaths.ResolvePreview(contentRootB, committedAccessor));

        var savDir = scopedAccessor.ResolveSubPath(contentRootA, "sav");
        var scopedFile = scopedAccessor.CombinePath(savDir, "slot.sav");
        scopedAccessor.Seed(scopedFile, [1]);

        // 装配期视图是 B：只有会话作用域生效时才看得到 A 的文件。
        using (GameRootEnvironment.Establish(scopedRoot))
            Assert.True(SafCompat.FileExists(scopedFile));

        Assert.False(SafCompat.FileExists(scopedFile));
    }

    [Fact]
    public void SafCompat_game_relative_paths_use_the_session_exe_dir()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.ResolvePreview(_rootA, accessor);
        GamePaths.Resolve(_rootB, accessor);

        using (GameRootEnvironment.Establish(rootA))
        {
            Assert.True(SafCompat.TryResolvePathUnderGameRoot("sav/slot.sav", false, out var scopedPath));
            Assert.Equal(Path.Combine(_rootA, "sav", "slot.sav"), Path.GetFullPath(scopedPath));
        }

        Assert.True(SafCompat.TryResolvePathUnderGameRoot("sav/slot.sav", false, out var committedPath));
        Assert.Equal(Path.Combine(_rootB, "sav", "slot.sav"), Path.GetFullPath(committedPath));
    }

    /// <summary>
    /// issue-008 的逐批验收信号：四个批次完成后，引擎与服务端不得再出现对根视图的<b>代码</b>引用
    /// （注释/文档里的历史说明不算）。<c>GamePaths.Current</c> 属性本身留给 issue-009 删除，
    /// MAUI 宿主与测试夹具的剩余读点也由 009 迁移；本守卫防止迁移回退。
    /// </summary>
    [Fact]
    public void Engine_and_server_sources_do_not_read_the_process_root_view()
    {
        var root = FindRepositoryRoot();
        var hits = new[] { "EraCore.Core", "EraCore.Server" }
            .SelectMany(dir => Directory.EnumerateFiles(
                Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(HasCodeReference)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(hits);
    }

    private static bool HasCodeReference(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal))
                continue;
            if (line.Contains("GamePaths.Current", StringComparison.Ordinal))
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
        if (_previousRoot != null)
            GameRootEnvironment.Commit(_previousRoot);

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
