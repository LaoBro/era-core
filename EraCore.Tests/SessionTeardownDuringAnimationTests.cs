using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 候选 3 / ADR-0049 行为锁 1、2：拆除与重载发生在<b>回合内动画进行中</b>。
///
/// <para><b>为什么需要这一组</b>（用户故事的回归锁）：
/// eraTW 的回合内动画曾让「重开 / 切换游戏」直接报错。引擎层已修好
/// （issue-002 每指令检查点 + issue-003 把 <c>AWAIT</c> 对齐到回合边界），
/// 但<b>没有任何用例覆盖这个窗口</b>：既有的四条拆除用例全部用
/// <c>@TEST_LONGLOOP</c>（纯计算），而 <c>LONGLOOP.ERB</c> 的文件头明确
/// 禁止出现 <c>AWAIT</c>。于是「动画中拆除仍是绿的」只存在于回忆里——
/// 任何一次为了性能把某个等待指令改回同步阻塞，CI 都不会红。</para>
///
/// <para><b>构造</b>：菜单 <c>[18]</c> → <c>@TEST_ANIMLONG(60)</c>（60 帧 ×
/// <c>AWAIT 100</c> ≈ 6 秒，帧内容递增）。先读到含 <c>ANIMLONG-</c> 的回合
/// 确认动画已开始，再在同一窗口内发起拆除/重载。窗口 6 秒远长于注入的
/// 2 秒上界，故「拆除发生在动画中」是断言而非运气。</para>
///
/// <para><b>两条用例各自钉什么</b>：
/// ① 结果是 <see cref="SessionDeleteStatus.Removed"/> 而<b>不是</b>
/// <see cref="SessionDeleteStatus.TeardownTimeout"/> —— 后者正是「动画让拆除
/// 等不到头」的信号；
/// ② 重载返回 <see cref="LoadGameStatus.Success"/> 而不是
/// <see cref="LoadGameStatus.PreviousSessionStillRunning"/> —— 后者会变成
/// 用户可见的 503。</para>
/// </summary>
[Collection("ServerState")]
public class SessionTeardownDuringAnimationTests
{
    /// <summary>测试注入的拆除上界。正常拆除（有让路点）实测毫秒级。</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>断言窗口：重载包含建新会话，故比拆除上界宽。</summary>
    private static readonly TimeSpan AssertWindow = TimeSpan.FromSeconds(6);

    /// <summary>清理窗口：等旧循环自然退出，避免污染同 collection 的后续测试。</summary>
    private static readonly TimeSpan LoopExitWindow = TimeSpan.FromSeconds(30);

    private static SessionRegistry CreateRegistry()
        => new(new SessionRegistryTests.NullTerminalSetup(), new GameConfigService(new ConfigData()))
        {
            TeardownTimeout = TeardownTimeout,
        };

    /// <summary>进游戏 → 触发动画 → 等到第一帧动画回合。</summary>
    private static async Task<Session> StartAnimationAsync(SessionRegistry registry)
    {
        var load = await registry.ReplaceForLoadGameAsync(SessionRegistryTests.FindTestGameDir());
        Assert.Equal(LoadGameStatus.Success, load.Status);

        var session = registry.CurrentSession!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && session.StateString != "WaitInput")
            await Task.Delay(50);
        Assert.Equal("WaitInput", session.StateString);

        session.IO.EnqueueInput("{\"type\":\"input\",\"value\":\"18\"}");

        // 只等 StateString == "Running" 不够：动画每帧之间也会回到 WaitInput，
        // 状态无法区分「已进入动画」与「还没进去」。必须等帧内容本身。
        var frameDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < frameDeadline)
        {
            var wait = await session.WaitForTurnAsync(1000, CancellationToken.None);
            if (wait.Status == TurnWaitStatus.Turn && wait.Turn != null && wait.Turn.Contains("ANIMLONG-"))
                return session;
            if (wait.Status is TurnWaitStatus.Closed or TurnWaitStatus.ControlLost)
                break;
        }

        Assert.Fail("未在窗口内观察到动画帧（ANIMLONG-）——菜单入口或 ANIMLONG.ERB 未生效");
        return session;
    }

    /// <summary>
    /// 行为锁 1：动画进行中 DELETE → <c>Removed</c>，且在上界以内。
    /// 若某个等待指令退化成「指令内同步等待」，结果会翻成 <c>TeardownTimeout</c>。
    /// </summary>
    [Fact]
    public async Task Delete_mid_animation_returns_removed_within_bound()
    {
        var registry = CreateRegistry();
        Session? session = null;
        try
        {
            session = await StartAnimationAsync(registry);

            var watch = Stopwatch.StartNew();
            var result = await registry.DeleteSessionAsync();
            watch.Stop();

            Assert.Equal(SessionDeleteStatus.Removed, result.Status);
            Assert.True(watch.Elapsed < TeardownTimeout,
                $"动画中拆除耗时 {watch.Elapsed.TotalSeconds:F2}s，超过注入上界 {TeardownTimeout.TotalSeconds:F1}s");
            Assert.False(session.IsRunning, "报 Removed 但游戏循环仍在跑 —— 结果不自洽");
            Assert.Null(registry.CurrentSession);
        }
        finally
        {
            await WaitForLoopExitAsync(session);
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 行为锁 2：动画进行中 POST /load-game 同目录 → <c>Success</c>。
    /// 若旧循环不能在界内退出，/load-game 会返 <c>PreviousSessionStillRunning</c>（用户可见 503）。
    /// </summary>
    [Fact]
    public async Task Reload_mid_animation_returns_success()
    {
        var registry = CreateRegistry();
        Session? session = null;
        try
        {
            session = await StartAnimationAsync(registry);

            var watch = Stopwatch.StartNew();
            var result = await registry.ReplaceForLoadGameAsync(SessionRegistryTests.FindTestGameDir());
            watch.Stop();

            Assert.Equal(LoadGameStatus.Success, result.Status);
            Assert.NotNull(result.SessionId);
            Assert.NotEqual(session.Id, result.SessionId);
            Assert.Contains(result.State, new[] { "Loading", "Running", "WaitInput" });
            Assert.True(watch.Elapsed < AssertWindow,
                $"动画中重载耗时 {watch.Elapsed.TotalSeconds:F2}s，超过断言窗口 {AssertWindow.TotalSeconds:F0}s");
        }
        finally
        {
            await WaitForLoopExitAsync(session);
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 等游戏循环彻底退出（供清理）。必须用 <see cref="Session"/> 引用而非
    /// <c>registry.CurrentSession</c>：拆除后 <c>_session</c> 已为 null。
    /// </summary>
    private static async Task WaitForLoopExitAsync(Session? session)
    {
        if (session == null)
            return;
        var deadline = DateTime.UtcNow + LoopExitWindow;
        while (DateTime.UtcNow < deadline && session.IsRunning)
            await Task.Delay(50);
    }
}
