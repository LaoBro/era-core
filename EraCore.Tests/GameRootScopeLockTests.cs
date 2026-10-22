using System;
using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using Session = MinorShift.Emuera.Sessions.CoreSession;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-007 引入、issue-009 收口：游戏根的作用域行为锁。
///
/// <para><b>要钉住的行为</b>：一个正在运行的游戏循环，读到的始终是<b>它自己那一局</b>的根；
/// 别的流上建立的新根不得改写它看到的根。这不是理论风险——issue-001 的故障站点正是这个形状：
/// 旧循环在拆除超时期间去新目录找 ERB/CSV。</para>
///
/// <para><b>四条用例的分工</b>：
/// <list type="number">
/// <item><see cref="Load_game_assembly_keeps_the_root_seen_by_the_running_loop"/>——走真实
/// <c>/load-game</c> 路径，旧循环仍在跑时必须拒绝，且不改变旧循环的根。</item>
/// <item><see cref="Newer_root_scope_keeps_the_running_loop_on_its_own_root"/>——钉住机制本身：
/// 另一条流建立了新根，旧流读到的仍是自己的根。</item>
/// <item><see cref="CoreSession_loop_reads_its_own_root_after_a_newer_scope"/>——同上，但读者是
/// 真实 <see cref="Session"/> 的游戏循环流（<c>Program.*</c> 同源）。</item>
/// <item><see cref="Sequential_sessions_each_read_their_own_root"/>——验收口径：两局先后启动、
/// 指向不同目录，第二局没有残留的旧根读取。</item>
/// </list>
/// </para>
///
/// <para>作用域是 AsyncLocal，随每个测试的 ExecutionContext 隔离——本类不需要串行集合。</para>
/// </summary>
public sealed class GameRootScopeLockTests : IDisposable
{
    /// <summary>拆除等待上界——伪造循环永不退出，用它确定性进入「旧循环仍在跑」相位。</summary>
    private static readonly TimeSpan ShortTeardown = TimeSpan.FromMilliseconds(150);

    private readonly string _rootA = CreateGameRoot("a");
    private readonly string _rootB = CreateGameRoot("b");
    private readonly FileSystemGameDirAccessor _accessor = new();

    private GamePaths ResolveRoot(string root) => GamePaths.Resolve(root, _accessor);

    /// <summary>
    /// 用例 1：<c>/load-game</c> 到另一目录时，旧循环仍在跑 → 拒绝，且旧循环与测试流都没有被改写根。
    /// </summary>
    [Fact]
    public async Task Load_game_assembly_keeps_the_root_seen_by_the_running_loop()
    {
        var rootA = ResolveRoot(_rootA);

        // 旧循环：等价于「拆除超时期间仍在跑长回合」的循环流——自己的根环境由自己建立。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rootSeenByOldLoop = null;
        var oldLoop = Task.Run(async () =>
        {
            using var scope = GameRootEnvironment.Establish(rootA);
            await gate.Task;
            rootSeenByOldLoop = GameRootEnvironment.Current!.ExeDir;
        });

        var registry = CreateRegistry(oldLoop);
        try
        {
            Assert.Equal(SessionCreateStatus.Created,
                (await registry.CreateMauiSessionAsync(new ConfigData(), fullDiffOnFirstTurn: false)).Status);

            var reload = await registry.ReplaceForLoadGameAsync(_rootB);

            // 旧循环没退出 → 必须拒绝；不存在可被污染的进程级根。
            Assert.Equal(LoadGameStatus.PreviousSessionStillRunning, reload.Status);
            Assert.Null(GameRootEnvironment.Current);

            gate.SetResult();
            await oldLoop;

            Assert.Equal(rootA.ExeDir, rootSeenByOldLoop);
            Assert.Null(GameRootEnvironment.Current);
        }
        finally
        {
            gate.TrySetResult();
            await oldLoop;
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 2：另一条流建立了新根——旧循环流仍读到自己那一局的根（作用域随流，不随进程）。
    /// </summary>
    [Fact]
    public async Task Newer_root_scope_keeps_the_running_loop_on_its_own_root()
    {
        var rootA = ResolveRoot(_rootA);
        var rootB = ResolveRoot(_rootB);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rootSeenByOldLoop = null;
        var oldLoop = Task.Run(async () =>
        {
            using var scope = GameRootEnvironment.Establish(rootA);
            await gate.Task;
            rootSeenByOldLoop = GameRootEnvironment.Current!.ExeDir;
        });

        // 新装配在自己的流上建立新根（此刻旧循环仍在跑）。
        using (GameRootEnvironment.Establish(rootB))
        {
            gate.SetResult();
            await oldLoop;

            Assert.Equal(rootB.ExeDir, GameRootEnvironment.Current!.ExeDir);
            Assert.Equal(rootA.ExeDir, rootSeenByOldLoop);
        }

        Assert.Null(GameRootEnvironment.Current);
    }

    /// <summary>
    /// 用例 3：读者换成真实 <see cref="Session"/> 的游戏循环流——
    /// 组装根建立的作用域内，脚本引擎（<c>Program.*</c> 同源）读到的必须是本会话的根。
    /// </summary>
    [Fact]
    public async Task CoreSession_loop_reads_its_own_root_after_a_newer_scope()
    {
        var rootA = ResolveRoot(_rootA);
        var rootB = ResolveRoot(_rootB);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rootSeenByLoop = null;
        var session = new Session(
            new HttpSessionIO(new OutputHub()),
            new SessionRegistryTests.NullTerminalSetup(),
            new ConfigData(rootA),
            protocolFactory: (console, ui, terminalSetup, displayState, input, output) =>
            {
                // 本回调运行在游戏循环流内（GameLoopComposer.RunAsync 之内）。
                // 返回 null = 循环在 console.Initialize 之前干净终结，不需要真实游戏数据。
                gate.Task.Wait();
                rootSeenByLoop = Program.ExeDir;
                return null;
            });

        try
        {
            session.Start();

            using (GameRootEnvironment.Establish(rootB))
            {
                gate.SetResult();

                Assert.True(await session.WaitForLoopExitAsync(TimeSpan.FromSeconds(30)),
                    "游戏循环未在窗口内退出");
                Assert.Equal(rootB.ExeDir, GameRootEnvironment.Current!.ExeDir);
            }

            Assert.Equal(rootA.ExeDir, rootSeenByLoop);
            Assert.Null(GameRootEnvironment.Current);
        }
        finally
        {
            gate.TrySetResult();
            session.Dispose();
        }
    }

    /// <summary>
    /// 用例 4（验收口径）：两局先后启动、指向不同目录——第二局启动时不存在残留的旧根读取，
    /// 每局循环读到的都是自己的根，且循环退出后作用域不残留。
    /// </summary>
    [Fact]
    public async Task Sequential_sessions_each_read_their_own_root()
    {
        var rootA = ResolveRoot(_rootA);
        var rootB = ResolveRoot(_rootB);

        Assert.Equal(rootA.ExeDir, await RunSessionAndReadRootAsync(rootA));
        Assert.Null(GameRootEnvironment.Current);
        Assert.Equal(rootB.ExeDir, await RunSessionAndReadRootAsync(rootB));
        Assert.Null(GameRootEnvironment.Current);
    }

    private static async Task<string?> RunSessionAndReadRootAsync(GamePaths root)
    {
        string? rootSeen = null;
        var session = new Session(
            new HttpSessionIO(new OutputHub()),
            new SessionRegistryTests.NullTerminalSetup(),
            new ConfigData(root),
            protocolFactory: (_, _, _, _, _, _) =>
            {
                rootSeen = Program.ExeDir;
                return null;
            });
        try
        {
            session.Start();
            Assert.True(await session.WaitForLoopExitAsync(TimeSpan.FromSeconds(30)),
                "游戏循环未在窗口内退出");
        }
        finally
        {
            session.Dispose();
        }
        return rootSeen;
    }

    private static SessionRegistry CreateRegistry(Task loop) =>
        new(new SessionRegistryTests.NullTerminalSetup(), new GameConfigService(new ConfigData()))
        {
            TeardownTimeout = ShortTeardown,
            SessionFactory = (io, cfg) => new Session(
                io,
                new SessionRegistryTests.NullTerminalSetup(),
                cfg,
                agentLease: null,
                fullDiffOnFirstTurn: false,
                dialectProfileId: null,
                loop: loop),
        };

    /// <summary>最小可用游戏根：目录 + csv + erb（够 <c>Validate()</c> 通过，不含游戏内容）。</summary>
    private static string CreateGameRoot(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), $"emuera_root_scope_{tag}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "csv"));
        Directory.CreateDirectory(Path.Combine(root, "erb"));
        return root;
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
                // 临时目录残留不影响结论，尽力清理即可。
            }
        }
    }
}
