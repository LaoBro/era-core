using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MinorShift.Emuera.Files;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-007 步骤 1：会话作用域根环境的形状与生命周期。
///
/// <para>钉住三件事：
/// <list type="bullet">
/// <item><b>只读视图</b>——<c>GamePaths.Current</c> 不再有 setter，<c>GamePaths</c> 内不再有
/// 「进程级根」这种静态字段。宽重构的 contract 段（issue-009）要删的是装配期视图，
/// 这条守卫保证没人把单例字段顺手加回来。</item>
/// <item><b>唯一建立者</b>——<c>GameRootEnvironment.Establish</c> 在生产代码里只允许出现在
/// 组装根 <c>CoreSession</c>。别处建立就等于开出第二个根生命周期。</item>
/// <item><b>建立即销毁</b>——作用域退出后还原本流原先的根，不残留给下一次启动；Dispose 幂等。</item>
/// </list></para>
///
/// <para>与 <c>ServerState</c> 同 collection：本文件的用例提交装配期根（进程级视图），须与
/// 其他改写根的测试串行。</para>
/// </summary>
[Collection("ServerState")]
public sealed class GameRootEnvironmentTests : IDisposable
{
    private readonly string _rootA = CreateGameRoot("a");
    private readonly string _rootB = CreateGameRoot("b");

    [Fact]
    public void GamePaths_current_is_a_read_only_view()
    {
        var current = typeof(GamePaths).GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(current);
        Assert.Null(current!.GetSetMethod(nonPublic: true));

        // 进程级根单例字段必须不存在：根只能由 GameRootEnvironment 承载。
        var selfTypedStatics = typeof(GamePaths)
            .GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(GamePaths))
            .Select(f => f.Name)
            .ToArray();
        Assert.Empty(selfTypedStatics);
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
        var rootA = GamePaths.ResolvePreview(_rootA, accessor);
        var rootB = GamePaths.ResolvePreview(_rootB, accessor);
        var committed = GamePaths.Resolve(_rootB, accessor).ExeDir;

        using (GameRootEnvironment.Establish(rootA))
        {
            Assert.Equal(rootA.ExeDir, GameRootEnvironment.Current!.ExeDir);

            using (GameRootEnvironment.Establish(rootB))
            {
                Assert.Equal(rootB.ExeDir, GameRootEnvironment.Current!.ExeDir);
            }

            // 内层退出回到外层，而不是回到 null 或装配期视图。
            Assert.Equal(rootA.ExeDir, GameRootEnvironment.Current!.ExeDir);
        }

        Assert.Equal(committed, GameRootEnvironment.Current!.ExeDir);
    }

    [Fact]
    public async Task Disposed_root_scope_leaves_nothing_for_the_next_launch()
    {
        // 装配期视图指向 B（下一次启动会建立它自己的根）；本用例在一股独立的「循环流」里
        // 建立 A、销毁 A，然后断言该流及其下游都回到 B——A 不残留。
        var accessor = new FileSystemGameDirAccessor();
        var rootA = GamePaths.ResolvePreview(_rootA, accessor);
        var exeDirB = GamePaths.Resolve(_rootB, accessor).ExeDir;

        var inside = await Task.Run(async () =>
        {
            var scope = GameRootEnvironment.Establish(rootA);
            var seenInScope = GameRootEnvironment.Current!.ExeDir;
            scope.Dispose();
            scope.Dispose();      // 幂等：重复释放不改变结论
            var afterDispose = GameRootEnvironment.Current!.ExeDir;
            var descendant = await Task.Run(() => GameRootEnvironment.Current!.ExeDir);
            Assert.Equal(rootA.ExeDir, seenInScope);
            return (afterDispose, descendant);
        });

        Assert.Equal(exeDirB, inside.afterDispose);
        Assert.Equal(exeDirB, inside.descendant);
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
