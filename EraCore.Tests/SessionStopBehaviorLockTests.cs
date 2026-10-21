using System;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using Session = MinorShift.Emuera.Sessions.CoreSession;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-006 步骤 0 行为锁：会话停止的幂等性。
///
/// <para><b>被钉住的不变量</b>：<see cref="Session.RequestTeardown"/> 与 <see cref="Session.Dispose"/>
/// 重复调用不得抛异常、不得把同一个循环拆两次，且拆除后 <see cref="Session.IsRunning"/> 必须为假。
/// 拆除保序（置旗标 → 控制权 End → Close）由 <see cref="TeardownControlOrderTests"/> 锁定；
/// 单活跃会话由 <see cref="SessionRegistryLifecycleTests"/> / <see cref="SessionRegistryTests"/> 锁定。</para>
/// </summary>
public class SessionStopBehaviorLockTests
{
    /// <summary>用未完成的任务代替真实游戏循环（与 ADR-0049 D4 的接缝同一手法）。</summary>
    private static Session CreateSession(Task loop, out HttpSessionIO io)
    {
        io = new HttpSessionIO(new OutputHub());
        return new Session(
            io,
            new SessionRegistryTests.NullTerminalSetup(),
            new ConfigData(),
            agentLease: null,
            fullDiffOnFirstTurn: false,
            dialectProfileId: null,
            loop: loop);
    }

    [Fact]
    public async Task Teardown_and_dispose_are_idempotent()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(loop.Task, out var io);
        session.Start();

        session.RequestTeardown();
        session.RequestTeardown();
        session.RequestTeardown("session_replaced");

        Assert.False(io.IsConnected);

        loop.SetResult();
        Assert.True(await session.WaitForLoopExitAsync(TimeSpan.FromSeconds(5)));

        session.Dispose();
        session.Dispose();

        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task Dispose_after_natural_end_is_safe()
    {
        var loop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = CreateSession(loop.Task, out _);
        session.Start();

        loop.SetResult();
        Assert.True(await session.WaitForLoopExitAsync(TimeSpan.FromSeconds(5)));

        session.Dispose();
        session.Dispose();

        Assert.False(session.IsRunning);
    }
}
