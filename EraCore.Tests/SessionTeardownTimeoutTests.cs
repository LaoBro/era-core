using System;
using System.Diagnostics;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-001：会话拆除的有界失败 —— 长回合进行中拆除/重建会话的行为。
///
/// <para><b>构造（最高接缝：真实脚本 + 真实会话生命周期）</b>：
/// test_game 的 @SYSTEM_TITLE 分支 [2] 进入 @TEST_LONGLOOP——一个墙钟有界的
/// 纯计算循环，循环体内无 AWAIT / 无 PRINT。该形状等价于真实故障站点
/// （eraTW TITLE.ERB @BADAPPLE：6573 轮 LOADTEXT + HTML_PRINT，同为单回合内
/// 长时间同步执行），因此能踩中「游戏循环线程被长时间占用 → 拆除方等待永不返回」
/// 这条路径。</para>
///
/// <para><b>防假绿（spec Testing Decisions）</b>：本测试先于生产代码改动落地，
/// 并已验证在改动前确实失败（拆除在 6s 窗口内不返回）。</para>
///
/// <para><b>测量纪律</b>：所有拆除/重载调用都经 <c>Task.Run</c> 包裹。原因：
/// <c>DeleteSessionAsync</c> 内首行<code>await _sessionLock.WaitAsync()</code>
/// 在锁空闲时是已完成 task，await 不挂起 → 整个方法（含其后的所有等待）同步跑在
/// 调用线程上。若直接在测试线程调用，耗时会消耗在「拿到 Task 之前」，
/// 使 <c>Task.WhenAny</c> 拿到已完成 task 而假绿（实测已踩过一次）。</para>
/// </summary>
[Collection("ServerState")]
public class SessionTeardownTimeoutTests
{
    /// <summary>测试注入的拆除超时。须显著小于 @TEST_LONGLOOP 的 8 秒，否则测不出超时。</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 拆除/重载的断言窗口。须大于 <see cref="TeardownTimeout"/>（超时路径要等满上界才返回）
    /// 且小于 8 秒（否则旧循环自然结束，测不出超时）。
    /// </summary>
    private static readonly TimeSpan AssertWindow = TimeSpan.FromSeconds(6);

    /// <summary>等旧循环自然退出（@TEST_LONGLOOP 墙钟 8 秒有界）——供测试清理，避免残留循环污染同 collection 的后续测试。</summary>
    private static readonly TimeSpan LoopExitWindow = TimeSpan.FromSeconds(30);

    private static SessionRegistry CreateRegistry()
        => new(new SessionRegistryTests.NullTerminalSetup(), new GameConfigService(new ConfigData()))
        {
            TeardownTimeout = TeardownTimeout,
        };

    /// <summary>进游戏并等到第一个输入点（标题界面 INPUT 等待中）。</summary>
    private static async Task<Session> StartAtTitleAsync(SessionRegistry registry)
    {
        var load = await registry.ReplaceForLoadGameAsync(SessionRegistryTests.FindTestGameDir());
        Assert.Equal(LoadGameStatus.Success, load.Status);

        var session = registry.CurrentSession!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && session.StateString != "WaitInput")
            await Task.Delay(50);
        Assert.Equal("WaitInput", session.StateString);
        return session;
    }

    /// <summary>注入输入 [2] 并等脚本进入 @TEST_LONGLOOP（State 由 WaitInput 回到 Running）。</summary>
    private static async Task EnterLongLoopAsync(Session session)
    {
        session.IO.EnqueueInput("{\"type\":\"input\",\"value\":\"2\"}");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && session.StateString != "Running")
            await Task.Delay(20);
        Assert.Equal("Running", session.StateString);
    }

    /// <summary>
    /// 在窗口内等待一个操作完成；超时返回 false。
    /// 用于区分「挂起」与「失败」——直接 await 会让改动前的死锁把测试挂到框架超时，
    /// 那样无法把「实现有 bug」和「测试环境慢」区分开。
    /// </summary>
    private static async Task<bool> CompletesAsync(Task task, TimeSpan window)
        => await Task.WhenAny(task, Task.Delay(window)) == task;

    /// <summary>
    /// 等游戏循环彻底退出（供清理）。
    ///
    /// 必须用 <see cref="Session"/> 引用而非 <c>registry.CurrentSession</c>：拆除超时后
    /// <c>_session</c> 已被摘除为 null，改看 CurrentSession 会立即返回而并未真的等到循环退出
    /// —— 那样后续重载会撞上仍在跑的旧循环，测不出"退出后可用"这一面。
    /// </summary>
    private static async Task WaitForLoopExitAsync(Session? session)
    {
        if (session == null)
            return;
        var deadline = DateTime.UtcNow.AddMilliseconds(LoopExitWindow.TotalMilliseconds);
        while (DateTime.UtcNow < deadline && session.IsRunning)
            await Task.Delay(50);
    }

    /// <summary>
    /// 用例 1：长回合进行中拆除会话 → 有界时间内返回明确失败，而非永久挂起。
    /// </summary>
    [Fact]
    public async Task Delete_during_long_turn_fails_within_bound()
    {
        var registry = CreateRegistry();
        // session 声明在 try 外——finally 块需要它来等旧循环退出（拆除后 _session 已为 null，
        // 不能再经 registry.CurrentSession 观察）。
        Session? session = null;
        try
        {
            session = await StartAtTitleAsync(registry);
            await EnterLongLoopAsync(session);

            var watch = Stopwatch.StartNew();
            var deleteTask = Task.Run(async () => await registry.DeleteSessionAsync());
            var completed = await CompletesAsync(deleteTask, AssertWindow);
            watch.Stop();

            Assert.True(completed,
                $"拆除会话在 {AssertWindow.TotalSeconds:F0}s 内未返回（改动前行为：永久挂起）");
            var result = await deleteTask;
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, result.Status);
            Assert.True(watch.Elapsed < AssertWindow,
                $"拆除耗时 {watch.Elapsed.TotalSeconds:F1}s 超出上界 {AssertWindow.TotalSeconds:F0}s");
        }
        finally
        {
            await WaitForLoopExitAsync(session);
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 2：拆除超时后重新加载游戏 → 旧循环存活期间明确失败，
    /// 旧循环退出后成功。覆盖用户故事「退出游戏后能加载其他游戏」
    /// （修复前该请求会永久挂起）。
    /// </summary>
    [Fact]
    public async Task Reload_after_long_turn_timeout_succeeds_once_loop_exits()
    {
        var registry = CreateRegistry();
        Session? session = null;
        try
        {
            session = await StartAtTitleAsync(registry);
            await EnterLongLoopAsync(session);

            // 首次拆除：有界失败。
            var firstDelete = Task.Run(async () => await registry.DeleteSessionAsync());
            Assert.True(await CompletesAsync(firstDelete, AssertWindow), "首次拆除在窗口内未返回");
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, (await firstDelete).Status);

            // 旧循环仍在跑时立刻重载 → 也应有界失败，且不建新会话
            //（守住 spec 决策四：静态隔离要求新会话与旧循环串行）。
            var during = Task.Run(async () => await registry.ReplaceForLoadGameAsync(SessionRegistryTests.FindTestGameDir()));
            Assert.True(await CompletesAsync(during, AssertWindow), "旧循环存活期间重载在窗口内未返回");
            Assert.Equal(LoadGameStatus.PreviousSessionStillRunning, (await during).Status);
            Assert.Null(registry.CurrentSession);

            // 旧循环自然退出后重载 → 成功拿到新会话。
            await WaitForLoopExitAsync(session);

            var after = Task.Run(async () => await registry.ReplaceForLoadGameAsync(SessionRegistryTests.FindTestGameDir()));
            Assert.True(await CompletesAsync(after, AssertWindow), "旧循环退出后重载在窗口内未返回");
            var ok = await after;
            Assert.Equal(LoadGameStatus.Success, ok.Status);
            Assert.NotNull(ok.SessionId);
        }
        finally
        {
            await WaitForLoopExitAsync(session);
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 3：拆除进行中，需获取会话锁的端点仍可响应。
    /// 覆盖验收项「其他端点在拆除进行中仍可响应」——修复前它们排在同一把锁后面，
    /// 会被同步 join 一起冻住。
    /// </summary>
    [Fact]
    public async Task Ws_subscription_responds_while_teardown_is_pending()
    {
        var registry = CreateRegistry();
        Session? session = null;
        try
        {
            session = await StartAtTitleAsync(registry);
            await EnterLongLoopAsync(session);

            // 拆除在后台进行中（尚未返回）。
            var deleteTask = Task.Run(async () => await registry.DeleteSessionAsync());

            // 先等拆除真正摘除 _session（DetachSessionLocked 的可见效果），再发起订阅。
            // 不做这一步则订阅调用可能抢在拆除之前拿到仍有效的会话，返回非 null——
            // 那测的是"调用时机"而非"拆除进行中端点是否可响应"，属竞态假失败。
            // （修复前此处永远等不到摘除：锁被 join 持有，DetachSessionLocked 根本执行不到。）
            var detachDeadline = DateTime.UtcNow.AddMilliseconds(AssertWindow.TotalMilliseconds);
            while (DateTime.UtcNow < detachDeadline && registry.CurrentSession != null)
                await Task.Delay(20);
            Assert.True(registry.CurrentSession == null,
                $"拆除未在 {AssertWindow.TotalSeconds:F0}s 内摘除会话引用（改动前行为：永久挂起）");

            // 该端点会取 _sessionLock——修复前该锁被 join 长期持有，此调用会一起挂住。
            // 只断言"在窗口内完成"，不断言返回值：超时与否由上面的独立标志判定。
            var subscribeTask = Task.Run(async () => await registry.TryGetWsSubscriptionAsync());
            Assert.True(await CompletesAsync(subscribeTask, AssertWindow),
                $"拆除进行中 WS 订阅端点在 {AssertWindow.TotalSeconds:F0}s 内未响应");
            // 已摘除 → 无活跃会话可订阅，返回 null 是正确语义（此时是确定性的）。
            Assert.Null(await subscribeTask);

            // 拆除本身最终仍要有界结束。
            Assert.True(await CompletesAsync(deleteTask, AssertWindow), "拆除在窗口内未返回");
            Assert.Equal(SessionDeleteStatus.TeardownTimeout, (await deleteTask).Status);
        }
        finally
        {
            await WaitForLoopExitAsync(session);
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 4（无回归）：正常流程中拆除会话——脚本已停在等待输入 → 立即成功，
    /// 不因有界等待机制而退化。这是绝大多数真实拆除的路径。
    /// </summary>
    [Fact]
    public async Task Delete_at_input_prompt_succeeds_immediately()
    {
        var registry = CreateRegistry();
        try
        {
            await StartAtTitleAsync(registry);

            var watch = Stopwatch.StartNew();
            var result = await registry.DeleteSessionAsync();
            watch.Stop();

            Assert.Equal(SessionDeleteStatus.Removed, result.Status);
            Assert.Null(registry.CurrentSession);
            // 正常路径是毫秒级（IO 关闭 → ReadLineAsync 返回 null → 循环退出）。
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2),
                $"正常拆除耗时 {watch.Elapsed.TotalSeconds:F1}s，疑似退化到等待超时");
        }
        finally
        {
            registry.DisposeAll();
        }
    }
}
