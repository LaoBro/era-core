using System;
using System.Text.Json;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using Session = MinorShift.Emuera.Sessions.CoreSession;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 候选 3 / ADR-0049 行为锁 4~7：<b>Retiring 相位</b>的迁移与可见性。
///
/// <para><b>为什么这组能跑在毫秒级</b>：接缝只有「一个 <see cref="Task"/>」（D4）——
/// 会话工厂交回一个把给定任务当游戏循环的 <see cref="Session"/>，于是
/// 「循环永不退出」用一个未完成的 <c>TaskCompletionSource</c> 即可确定性构造，
/// 不必真加载游戏。这正是原先「只能靠真会话测」的那部分。</para>
///
/// <para>四条用例对应 spec §4 的 4、5、6、7 号锁：
/// DELETE 幂等、create 被拒、WS 订阅有界返回、<c>/state</c> 透出 <c>retiring</c>。</para>
/// </summary>
public class SessionRegistryLifecycleTests
{
    /// <summary>缩短上界，使「超时」在用例里只花百毫秒级。</summary>
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>用给定 loop 任务造 registry；工厂只替换 Session 本体（hub/io 仍由 registry 建）。</summary>
    private static SessionRegistry CreateRegistry(Task loop, TimeSpan? timeout = null)
    {
        var setup = new SessionRegistryTests.NullTerminalSetup();
        return new SessionRegistry(new SessionRegistryTests.NullTerminalSetup(), new GameConfigService(new ConfigData()))
        {
            TeardownTimeout = timeout ?? ShortTimeout,
            SessionFactory = (io, cfg) => new Session(
                io, setup, cfg,
                agentLease: null,
                fullDiffOnFirstTurn: false,
                dialectProfileId: null,
                loop: loop),
        };
    }

    /// <summary>造一个 Live 会话（MAUI 路径允许 <c>_session == null</c> 时首次建会话）。</summary>
    private static async Task CreateLiveSessionAsync(SessionRegistry registry)
    {
        var created = await registry.CreateMauiSessionAsync(new ConfigData(), fullDiffOnFirstTurn: false);
        Assert.Equal(SessionCreateStatus.Created, created.Status);
    }

    /// <summary>
    /// 行为锁 4：<c>retiring</c> 期间 DELETE 幂等 —— 超时期间有界失败（503），
    /// 旧循环退出后再 DELETE 必须拿到 <c>Removed</c>（而不是 404「本来就没有」）。
    /// </summary>
    [Fact]
    public async Task Delete_while_retiring_waits_then_removes()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = CreateRegistry(loop.Task);
        try
        {
            await CreateLiveSessionAsync(registry);

            var first = await registry.DeleteSessionAsync();
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, first.Status);
            Assert.True(registry.IsRetiring, "拆除超时后必须进入 Retiring 相位");
            Assert.Null(registry.CurrentSession);

            // 重试：仍然超时，但必须有界返回（挂起即回归 issue-001）。
            var second = await registry.DeleteSessionAsync();
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, second.Status);

            // 旧循环退出后，DELETE 才名副其实。
            loop.SetResult();
            var third = await registry.DeleteSessionAsync();
            Assert.Equal(SessionDeleteStatus.Removed, third.Status);
            Assert.False(registry.IsRetiring);
            Assert.Null(registry.CurrentSession);
        }
        finally
        {
            loop.TrySetResult();
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 行为锁 5：create 必须与旧循环串行（spec 决策四）—— Retiring 期间拒绝建会话；
    /// 旧循环退出后回到「未加载游戏」，而不是静默建出一个新会话。
    /// </summary>
    [Fact]
    public async Task Create_refused_while_retiring()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = CreateRegistry(loop.Task);
        try
        {
            await CreateLiveSessionAsync(registry);
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, (await registry.DeleteSessionAsync()).Status);

            var during = await registry.CreateNewSessionAsync();
            Assert.Equal(SessionCreateStatus.PreviousSessionStillRunning, during.Status);
            Assert.Null(registry.CurrentSession);

            loop.SetResult();
            var after = await registry.CreateNewSessionAsync();
            // 关键：不是 Created。POST /session 只负责「已结束会话重建」，
            // 旧循环退出后没有 Live 会话 → NoGameLoaded，须重新 /load-game。
            Assert.Equal(SessionCreateStatus.NoGameLoaded, after.Status);
            Assert.False(registry.IsRetiring);
        }
        finally
        {
            loop.TrySetResult();
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 行为锁 6：Retiring 期间 WS 订阅有界返回 null —— 拆除期读端点不得被会话锁冻住。
    /// </summary>
    [Fact]
    public async Task Ws_subscription_is_null_while_retiring()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = CreateRegistry(loop.Task);
        try
        {
            await CreateLiveSessionAsync(registry);
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, (await registry.DeleteSessionAsync()).Status);

            var subscribe = registry.TryGetWsSubscriptionAsync();
            var completed = await Task.WhenAny(subscribe, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(subscribe, completed);
            Assert.Null(await subscribe);
        }
        finally
        {
            loop.TrySetResult();
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// ADR-0049 D5：原子重开 —— 一次调用完成「拆旧 → 一次有界等待 → 建新」。
    /// 旧循环未退出时只返回<b>一个</b> <see cref="SessionCreateStatus.PreviousSessionStillRunning"/>，
    /// 而不是「Delete 吞掉超时 + Create 再等一遍」的双次上界。
    /// </summary>
    [Fact]
    public async Task ReplaceMaui_is_atomic_and_reports_single_status()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = CreateRegistry(loop.Task);
        try
        {
            await CreateLiveSessionAsync(registry);

            var stuck = await registry.ReplaceMauiSessionAsync(new ConfigData(), fullDiffOnFirstTurn: false);
            Assert.Equal(SessionCreateStatus.PreviousSessionStillRunning, stuck.Status);
            Assert.Null(registry.CurrentSession);

            loop.SetResult();
            var replaced = await registry.ReplaceMauiSessionAsync(new ConfigData(), fullDiffOnFirstTurn: false);
            Assert.Equal(SessionCreateStatus.Created, replaced.Status);
            Assert.NotNull(registry.CurrentSession);
        }
        finally
        {
            loop.TrySetResult();
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 行为锁 7：<c>/state</c> 在 Retiring 期间透出 <c>retiring:true</c>；
    /// 正常态响应体不得出现该键（纯增量字段，老客户端零感知）。
    /// </summary>
    [Fact]
    public async Task GetState_includes_retiring_flag()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = CreateRegistry(loop.Task);
        var protocol = new GameServerProtocol(registry, new GameConfigService(new ConfigData()));
        try
        {
            // 正常态（无会话、无 retiring）：不带 retiring 键。
            using (var idle = JsonDocument.Parse(protocol.GetState().Body!))
            {
                Assert.False(idle.RootElement.TryGetProperty("retiring", out _));
                Assert.Equal("Idle", idle.RootElement.GetProperty("state").GetString());
            }

            await CreateLiveSessionAsync(registry);
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, (await registry.DeleteSessionAsync()).Status);

            using (var retiring = JsonDocument.Parse(protocol.GetState().Body!))
            {
                Assert.True(retiring.RootElement.TryGetProperty("retiring", out var flag));
                Assert.True(flag.GetBoolean());
                // 相位仍报 Idle：不扩状态词表（D6），retiring 是附加事实。
                Assert.Equal("Idle", retiring.RootElement.GetProperty("state").GetString());
            }
        }
        finally
        {
            loop.TrySetResult();
            registry.DisposeAll();
        }
    }
}
