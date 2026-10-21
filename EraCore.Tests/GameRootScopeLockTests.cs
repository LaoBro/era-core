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
/// issue-007 步骤 0：游戏根的作用域行为锁。
///
/// <para><b>要钉住的行为</b>：一个正在运行的游戏循环，读到的始终是<b>它自己那一局</b>的根；
/// 之后的装配（<c>/load-game</c> 校验、新会话提交新根）不得改写它看到的根。
/// 这不是理论风险——issue-001 的故障站点正是这个形状：旧循环在拆除超时期间去新目录找 ERB/CSV。</para>
///
/// <para><b>三条用例的分工</b>：
/// <list type="number">
/// <item><see cref="Load_game_assembly_keeps_the_root_seen_by_the_running_loop"/>——走真实
/// <c>/load-game</c> 路径，钉住「装配期不提交」这条顺序。改动前必须为绿（现状靠
/// <see cref="GamePaths.ResolvePreview"/> 守住），把校验改成即时提交根后必须转红。</item>
/// <item><see cref="Later_root_commit_keeps_the_running_loop_on_its_own_root"/>——钉住机制本身：
/// 新根已提交时，仍在跑的循环读到的仍是旧根。改动前为<b>红</b>（根是进程级可变单例），
/// 会话作用域根环境落地后转绿。</item>
/// <item><see cref="CoreSession_loop_reads_its_own_root_after_a_newer_commit"/>——同上，但读者是
/// 真实 <see cref="Session"/> 的游戏循环流（不是测试自己造的 Task）。</item>
/// </list>
/// </para>
///
/// <para>与 <c>SessionRegistryTests</c> / 端点测试同 collection：都会改写进程级根视图，须串行。</para>
/// </summary>
[Collection("ServerState")]
public sealed class GameRootScopeLockTests : IDisposable
{
    /// <summary>拆除等待上界——伪造循环永不退出，用它确定性进入「旧循环仍在跑」相位。</summary>
    private static readonly TimeSpan ShortTeardown = TimeSpan.FromMilliseconds(150);

    private readonly string _rootA = CreateGameRoot("a");
    private readonly string _rootB = CreateGameRoot("b");

    /// <summary>装配一份根并提交（旧世界的写法：提交即改写进程级根）。</summary>
    private string CommitRoot(string root) => GamePaths.Resolve(root, new FileSystemGameDirAccessor()).ExeDir;

    /// <summary>
    /// 用例 1（改动前为绿）：<c>/load-game</c> 到另一目录时，校验与拆除等待期间
    /// <b>不得</b>提交新根——旧循环读到的、以及进程视图里的根都还是旧目录。
    /// </summary>
    [Fact]
    public async Task Load_game_assembly_keeps_the_root_seen_by_the_running_loop()
    {
        var exeDirA = CommitRoot(_rootA);

        // 旧循环：一个卡在门闩上的任务，等价于「拆除超时期间仍在跑长回合」的循环流。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rootSeenByOldLoop = null;
        var oldLoop = Task.Run(async () =>
        {
            await gate.Task;
            rootSeenByOldLoop = GamePaths.Current.ExeDir;
        });

        var registry = CreateRegistry(oldLoop);
        try
        {
            Assert.Equal(SessionCreateStatus.Created,
                (await registry.CreateMauiSessionAsync(new ConfigData(), fullDiffOnFirstTurn: false)).Status);

            var reload = await registry.ReplaceForLoadGameAsync(_rootB);

            // 旧循环没退出 → 必须拒绝，且不得留下半个新根。
            Assert.Equal(LoadGameStatus.PreviousSessionStillRunning, reload.Status);
            gate.SetResult();
            await oldLoop;

            Assert.Equal(exeDirA, rootSeenByOldLoop);
            Assert.Equal(exeDirA, GamePaths.Current.ExeDir);
        }
        finally
        {
            gate.TrySetResult();
            await oldLoop;
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 2（改动前为红）：新会话装配<b>已经</b>把新根提交了——旧循环仍必须读到自己那一局的根。
    /// 「不提交」只是时序上的侥幸，本用例钉的是作用域本身。
    /// </summary>
    [Fact]
    public async Task Later_root_commit_keeps_the_running_loop_on_its_own_root()
    {
        var exeDirA = CommitRoot(_rootA);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rootSeenByOldLoop = null;
        var oldLoop = Task.Run(async () =>
        {
            await gate.Task;
            rootSeenByOldLoop = GamePaths.Current.ExeDir;
        });

        // 新会话开始装配：提交新根（此刻旧循环仍在跑）。
        var exeDirB = CommitRoot(_rootB);
        gate.SetResult();
        await oldLoop;

        Assert.Equal(exeDirB, GamePaths.Current.ExeDir);          // 新装配确实提交了
        Assert.Equal(exeDirA, rootSeenByOldLoop);                  // 旧循环不受影响
    }

    /// <summary>
    /// 用例 3（改动前为红）：读者换成真实 <see cref="Session"/> 的游戏循环流——
    /// 组装根建立的作用域内，脚本引擎（<c>Program.*</c> 同源）读到的必须是本会话的根。
    /// </summary>
    [Fact]
    public async Task CoreSession_loop_reads_its_own_root_after_a_newer_commit()
    {
        var exeDirA = CommitRoot(_rootA);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? rootSeenByLoop = null;
        var session = new Session(
            new HttpSessionIO(new OutputHub()),
            new SessionRegistryTests.NullTerminalSetup(),
            new ConfigData(),
            protocolFactory: (console, ui, terminalSetup, displayState, input, output) =>
            {
                // 本回调运行在游戏循环流内（GameLoopComposer.RunAsync 之内）。
                // 返回 null = 循环在 console.Initialize 之前干净终结，不需要真实游戏数据。
                gate.Task.Wait();
                rootSeenByLoop = GamePaths.Current.ExeDir;
                return null;
            });

        try
        {
            session.Start();

            var exeDirB = CommitRoot(_rootB);
            gate.SetResult();

            Assert.True(await session.WaitForLoopExitAsync(TimeSpan.FromSeconds(30)),
                "游戏循环未在窗口内退出");
            Assert.Equal(exeDirB, GamePaths.Current.ExeDir);
            Assert.Equal(exeDirA, rootSeenByLoop);
        }
        finally
        {
            gate.TrySetResult();
            session.Dispose();
        }
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
