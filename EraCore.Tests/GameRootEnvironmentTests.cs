using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MinorShift.Emuera.Files;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-009 contract：根只剩会话作用域一种形态。
///
/// <para>钉住四件事：
/// <list type="bullet">
/// <item><b>单例已删除</b>——<c>GamePaths.Current</c> / <c>SetCurrent</c> / <c>ResolvePreview</c> /
/// <c>GameRootEnvironment.Commit</c> 都不存在，<c>GamePaths</c> / <c>Program</c> 也没有任何
/// 进程级 <c>GamePaths</c> 字段；没人能把单例顺手加回来。</item>
/// <item><b>构造是纯的</b>——<c>GamePaths.Resolve</c> 只返回实例，不写任何作用域状态。</item>
/// <item><b>唯一建立者</b>——<c>GameRootEnvironment.Establish</c> 在生产代码里只允许出现在
/// 组装根 <c>CoreSession</c>。别处建立就等于开出第二个根生命周期。</item>
/// <item><b>建立即销毁</b>——作用域退出后回到外层（无外层则为 null），不残留给下一次启动；Dispose 幂等。</item>
/// </list></para>
///
/// <para>作用域是 AsyncLocal，随每个测试的 ExecutionContext 隔离——本类不需要串行集合。</para>
/// </summary>
public sealed class GameRootEnvironmentTests : IDisposable
{
    private readonly string _rootA = CreateGameRoot("a");
    private readonly string _rootB = CreateGameRoot("b");

    [Fact]
    public void Process_level_root_api_is_gone()
    {
        Assert.Null(typeof(GamePaths).GetProperty("Current",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(typeof(GamePaths).GetMethod("SetCurrent",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(typeof(GamePaths).GetMethod("ResolvePreview",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Null(typeof(GameRootEnvironment).GetMethod("Commit",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic));

        // 进程级根槽位必须不存在：根只能由会话作用域承载。
        Assert.DoesNotContain(
            typeof(GamePaths).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            f => typeof(GamePaths).IsAssignableFrom(f.FieldType));
        Assert.DoesNotContain(
            typeof(Program).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            f => typeof(GamePaths).IsAssignableFrom(f.FieldType));
    }

    [Fact]
    public void Resolve_constructs_only_and_never_commits()
    {
        var accessor = new FileSystemGameDirAccessor();

        var root = GamePaths.Resolve(_rootA, accessor);

        Assert.NotNull(root);
        Assert.Null(GameRootEnvironment.Current);
    }

    [Fact]
    public void Program_shim_requires_the_session_scope()
    {
        var error = Assert.Throws<InvalidOperationException>(() => _ = Program.ExeDir);
        Assert.Contains("CoreSession", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_composition_root_establishes_a_root_scope()
    {
        var root = FindRepositoryRoot();
        var producers = new[] { "EraCore.Core", "EraCore.Server", "EraCore.Cli", "EraCore.Maui" };
        var sites = producers
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => File.ReadAllText(f).Contains("GameRootEnvironment.Establish(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToArray();

        Assert.Equal(new[] { "EraCore.Core/Sessions/CoreSession.cs" }, sites);
    }

    [Fact]
    public void Nested_scope_restores_the_previous_root_on_exit()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);
        var rootB = GamePaths.Resolve(_rootB, accessor);

        using (GameRootEnvironment.Establish(rootA))
        {
            Assert.Equal(rootA.ExeDir, GameRootEnvironment.Current!.ExeDir);

            using (GameRootEnvironment.Establish(rootB))
            {
                Assert.Equal(rootB.ExeDir, GameRootEnvironment.Current!.ExeDir);
            }

            // 内层退出回到外层，而不是回到 null。
            Assert.Equal(rootA.ExeDir, GameRootEnvironment.Current!.ExeDir);
        }

        Assert.Null(GameRootEnvironment.Current);
    }

    [Fact]
    public async Task Disposed_root_scope_leaves_nothing_for_the_next_launch()
    {
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.Resolve(_rootA, accessor);

        var inside = await Task.Run(async () =>
        {
            var scope = GameRootEnvironment.Establish(rootA);
            var seenInScope = GameRootEnvironment.Current!.ExeDir;
            scope.Dispose();
            scope.Dispose();      // 幂等：重复释放不改变结论
            var afterDispose = GameRootEnvironment.Current?.ExeDir;
            var descendant = await Task.Run(() => GameRootEnvironment.Current?.ExeDir);
            Assert.Equal(rootA.ExeDir, seenInScope);
            return (afterDispose, descendant);
        });

        Assert.Null(inside.afterDispose);
        Assert.Null(inside.descendant);
    }

    /// <summary>最小可用游戏根：目录 + csv + erb。</summary>
    private static string CreateGameRoot(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), $"emuera_root_env_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "csv"));
        Directory.CreateDirectory(Path.Combine(root, "erb"));
        return root;
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
                Directory.Delete(root, true);
            }
            catch (IOException)
            {
                // 尽力清理。
            }
        }
    }
}
