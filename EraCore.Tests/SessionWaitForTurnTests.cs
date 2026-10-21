using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Session.WaitForTurnAsync 三分支单元测试（重构补测——原零覆盖）。
/// 不调用 Start()：WaitForTurnAsync 只依赖 HttpSessionIO 的 _output Channel，
/// 无需游戏循环；注入/关闭经真实 HttpSessionIO 完成。
/// 另含 GetDisplaySnapshot 的未初始化返回 null 断言（GET /snapshot 503 的触发条件）。
/// </summary>
public class SessionWaitForTurnTests
{
    private static (Session session, HttpSessionIO io) CreateSession()
    {
        var hub = new OutputHub();
        var io = new HttpSessionIO(hub);
        var session = new Session(io, new SessionRegistryTests.NullTerminalSetup(), new ConfigData());
        return (session, io);
    }

    [Fact]
    public async Task Wait_returns_turn_after_WriteLine()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;

        const string turn = "{\"type\":\"turn\",\"value\":\"Hello\"}";
        io.WriteLine(TestTurnFactory.Wire(turn));

        var result = await session.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Turn, result.Status);
        Assert.Equal(turn, result.Turn!.Wire);
    }

    [Fact]
    public async Task Wait_times_out_when_no_output()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;

        var result = await session.WaitForTurnAsync(50, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Timeout, result.Status);
        Assert.Null(result.Turn);
    }

    [Fact]
    public async Task Wait_returns_closed_after_io_close()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;

        io.Close();

        var result = await session.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Closed, result.Status);
        Assert.Null(result.Turn);
    }

    [Fact]
    public async Task Wait_consumes_turns_fifo()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;

        io.WriteLine(TestTurnFactory.Wire("turn-1"));
        io.WriteLine(TestTurnFactory.Wire("turn-2"));

        var first = await session.WaitForTurnAsync(1000, CancellationToken.None);
        var second = await session.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal("turn-1", first.Turn!.Wire);
        Assert.Equal("turn-2", second.Turn!.Wire);
    }

    /// <summary>GET /snapshot 503 的触发条件：session 未初始化（_displayState == null）时 GetDisplaySnapshot() 返回 null。</summary>
    [Fact]
    public void Uninitialized_session_snapshot_is_null()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;
        Assert.Null(session.GetDisplaySnapshot());
    }

    [Fact]
    public async Task Acquire_drains_backlog_so_next_wait_sees_only_new_turn()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;
        var agent = ControlIdentity.Agent("agent-1");

        Assert.Equal(ControlAcquireStatus.Acquired, session.AcquireControl(agent).Control.Status);
        io.WriteLine(TestTurnFactory.Wire("old-1"));
        io.WriteLine(TestTurnFactory.Wire("old-2"));

        var drained = session.AcquireControl(agent);
        Assert.Equal(ControlAcquireStatus.Acquired, drained.Control.Status);
        Assert.Equal(2, drained.TurnsAdvanced);
        Assert.Equal("old-2", drained.Turn!.Wire);

        io.WriteLine(TestTurnFactory.Wire("new-1"));
        var next = await session.WaitForTurnAsync(1000, agent, CancellationToken.None);
        Assert.Equal(TurnWaitStatus.Turn, next.Status);
        Assert.Equal("new-1", next.Turn!.Wire);
    }

    [Fact]
    public async Task In_flight_wait_returns_control_lost_immediately_on_steal()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;
        var agent = ControlIdentity.Agent("agent-1");
        session.AcquireControl(agent);

        var watch = Stopwatch.StartNew();
        var waitTask = session.WaitForTurnAsync(5000, agent, CancellationToken.None);
        await Task.Delay(80);
        session.AcquireControl(ControlIdentity.User);
        var result = await waitTask;

        Assert.Equal(TurnWaitStatus.ControlLost, result.Status);
        Assert.Equal("control_changed", result.Reason);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// <b>真实接线回归</b>：抢占恰好撞上一条在途回合时，**那个回合必须恰好被交付一次**。
    /// <para>
    /// 🔴 <b>三种交错都合法</b>（本条测试的价值就是钉住「都合法，但都不许丢/不许双交付」）：
    /// <list type="number">
    /// <item><b>A：交付先于抢占生效</b> —— 等待方已取出 turn 并确认未易主 ⇒ 返回
    /// <c>Turn</c>（此时 agent 仍是 controller，交给他是对的）⇒ 抢占 drain 到 0 条。</item>
    /// <item><b>B：抢占先生效、回滚先于 drain</b> —— 返回 <c>ControlLost</c> 且抢占当场 drain 到那一条。</item>
    /// <item><b>C：抢占先生效、回滚晚于 drain</b> —— 同 B 但 drain 那一刻回滚尚未执行 ⇒
    /// 抢占 drain 到 0 条，回合稍后由 <c>UnreadOutput</c> 放回，下一次 drain 拿得到。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 🔴 旧版断言「<c>ControlLost</c> 且 <c>TurnsAdvanced == 1</c>」只承认 B，赌了赛跑胜负：
    /// <c>AcquireControl</c> 内的 <c>DrainOnAcquire</c> 是<b>同步</b>的，而回滚要等等待线程被唤醒、
    /// 跑到 rollback 点（<c>TurnDelivery.cs:185-188</c> / <c>:225-229</c>）⇒ 时点无排序保证，
    /// 负载高时偶发假红（2026-10-15 实测，三种结局都出现过）。
    /// </para>
    /// <para>
    /// 回滚分支本身由 <c>TurnDeliveryTests.Turn_ready_concurrently_with_steal_is_rolled_back_for_next_drain</c>
    /// 用 TCS 确定性覆盖（fake sink 注入口）；本用例只做真实对象接线的兜底，故只断言不变式。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Steal_racing_a_queued_turn_delivers_it_exactly_once()
    {
        var (session, io) = CreateSession();
        using var sessionCleanup = session;
        var agent = ControlIdentity.Agent("agent-1");
        session.AcquireControl(agent);

        var waitTask = session.WaitForTurnAsync(5000, agent, CancellationToken.None);
        await Task.Delay(80);
        io.WriteLine(TestTurnFactory.Wire("raced-turn"));
        var stolen = session.AcquireControl(ControlIdentity.User);
        var waitResult = await waitTask;

        Assert.True(
            waitResult.Status is TurnWaitStatus.Turn or TurnWaitStatus.ControlLost,
            $"只可能是「抢占前已交付」(Turn) 或「抢占生效」(ControlLost)，实得 {waitResult.Status}");

        // 「raced-turn 交给了谁」：三种交错各自填一格，最终必须恰好一格有值。
        var toAgent = waitResult.Status == TurnWaitStatus.Turn ? waitResult.Turn : null;
        var toUser = stolen.TurnsAdvanced > 0 ? stolen.Turn : null;
        if (toAgent == null && toUser == null)
        {
            // 交错 C：回滚晚于 drain。`await waitTask` 之后回滚必然已完成
            // （UnreadOutput 在 return 之前执行）⇒ 同身份再 acquire 一次必能取回
            // （Controller.Acquire 对同身份幂等返回 Acquired，仍会走 drain）。
            var drained = session.AcquireControl(ControlIdentity.User);
            Assert.Equal(ControlAcquireStatus.Acquired, drained.Control.Status);
            toUser = drained.TurnsAdvanced > 0 ? drained.Turn : null;
        }

        var delivered = new[] { toAgent, toUser }.Where(t => t != null).Select(t => t!.Wire).ToArray();
        Assert.Equal(new[] { "raced-turn" }, delivered);   // 恰好一次：不丢、不双交付
    }

    [Fact]
    public async Task Dispose_mid_wait_returns_closed()
    {
        var (session, _) = CreateSession();
        using var sessionCleanup = session;

        var waitTask = session.WaitForTurnAsync(5000, CancellationToken.None);
        await Task.Delay(80);
        session.Dispose();
        var result = await waitTask;

        // 拆除期在途等待应稳定 Closed（404），而非与 ControlLost（409）竞争
        Assert.Equal(TurnWaitStatus.Closed, result.Status);
        Assert.Null(result.Turn);
    }
}
