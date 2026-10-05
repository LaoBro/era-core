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
/// <para><b>被测的不变量</b>：会话销毁必须拆成两段 —— 锁内只做原子摘引用 + 发拆除信号
/// （微秒级，让其他端点立即恢复可响应），锁外做<b>有界</b>等待游戏循环退出；超时返回
/// <see cref="SessionDeleteStatus.TeardownTimeout"/> 这一明确失败，而不是静默挂起。
/// 修复前是「锁内同步 join」：长回合期间所有需要会话锁的端点全部永久阻塞，
/// 用户失去整个应用（只能杀进程）。</para>
///
/// <para><b>构造纪律（改本文件前必读）</b>：全部经 test_game 菜单 <c>[2]</c>
/// → <c>@TEST_LONGLOOP(8000)</c>（墙钟有界的纯计算长循环）进入长回合态。
/// 该形状等价于真实故障站点（eraTW TITLE.ERB 的 @BADAPPLE），是 issue-001 的原始构造。</para>
///
/// <para><b>「超时」分支不在这里测</b>（口径变更史，务必先读完）：
/// 要在集成层测出超时，必须让游戏循环在<b>拆除上界到期时仍然活着</b> ——
/// 即卡在一个「外部插不上手」的窗口里。这类窗口随引擎演进被逐个堵掉：
/// <list type="number">
/// <item><c>@TEST_LONGLOOP</c>（纯计算长循环）→ issue-002 给脚本层加了每条指令前的
/// 检查点，它变得可中断，失效。</item>
/// <item><c>@TEST_AWAITBLOCK</c>（<c>AWAIT</c> 走 Thread.Sleep，等待在单条指令内部）→
/// issue-003 把 <c>AWAIT</c> 对齐成等待输入态，等待落到回合边界上，失效。</item>
/// <item>票面给的替代候选「单指令内部重计算」→ <b>实测否掉</b>：引擎把用户自定义数组尺寸
/// 硬性限制在 100 万以内，而 .NET 的排序比较约 6ns/次且与元素长度无关 ——
/// 单条 <c>ARRAYSORT</c> 最多约 0.2 秒，撑不起任何有意义的上界。</item>
/// <item>票面另一个候选「初始化期」→ <b>实测否掉</b>：把初始化拉长到秒级需海量小 ERB 文件
/// （3 个用例实测耗时 8 分 24 秒），且拆除动作会<b>竞态地</b>把初始化提前收束 ——
/// 该窗口不具备「外部插不上手」的定性（实测出现 <c>Success</c> 而非
/// <c>PreviousSessionStillRunning</c>）。</item>
/// </list>
/// 结论：<b>引擎里已不存在脚本层可达的不可中断窗口</b>，超时分支在集成层不可达。
/// 该分支改由 <see cref="BoundedLoopWaitTests"/> 用永不完成的任务<b>确定性</b>覆盖
/// （等待逻辑已抽成窄接缝 <see cref="BoundedLoopWait"/>，接缝只是一个 Task 引用）。
/// 本文件保留的是<b>可达</b>的那些面：有界性（永不挂起）、结果明确、拆除摘除引用后
/// 其他端点可响应、以及「新会话必须与旧循环串行」这条不变量。</para>
/// </summary>
[Collection("ServerState")]
public class SessionTeardownTimeoutTests
{
    /// <summary>测试注入的拆除超时。须大于长循环被中断所需的时间（实测 ~0.8s），否则正常路径会误报超时。</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>断言窗口：须大于 <see cref="TeardownTimeout"/>（超时路径要等满上界才返回）。</summary>
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

    /// <summary>注入输入 <c>2</c> 并等脚本进入 @TEST_LONGLOOP（State 由 WaitInput 回到 Running）。</summary>
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
    /// 必须用 <see cref="Session"/> 引用而非 <c>registry.CurrentSession</c>：拆除后
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
    /// 用例 1：长回合进行中拆除会话 → <b>在有界时间内返回明确结果，绝不挂起</b>。
    ///
    /// 修复前这里是「永久挂起」（测试线程会一直卡在 delete 上）。
    /// 两种合法结果：<c>Removed</c>（脚本在下一个指令边界被中断 → 循环及时退出）或
    /// <c>TeardownTimeout</c>（上界内没能退出 → 明确失败）。<b>不允许</b>的是不返回。
    /// </summary>
    [Fact]
    public async Task Delete_during_long_turn_returns_bounded_explicit_result()
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
            Assert.Contains(result.Status,
                new[] { SessionDeleteStatus.Removed, SessionDeleteStatus.TeardownTimeout });
            Assert.True(watch.Elapsed < AssertWindow,
                $"拆除耗时 {watch.Elapsed.TotalSeconds:F1}s 超出上界 {AssertWindow.TotalSeconds:F0}s");

            // 结果与循环状态必须自洽：报超时就意味着循环仍活着，报成功就意味着它已退出。
            if (result.Status == SessionDeleteStatus.TeardownTimeout)
                Assert.True(session.IsRunning, "报 TeardownTimeout 但游戏循环其实已退出 —— 结果不自洽");
            else
                Assert.False(session.IsRunning, "报 Removed 但游戏循环仍在跑 —— 结果不自洽");
        }
        finally
        {
            await WaitForLoopExitAsync(session);
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 2：拆除后立刻重载 → 同样有界返回，且守住 spec 决策四的<b>串行化不变量</b>：
    /// 「旧循环仍在运行」时<b>不得</b>建立新会话（新循环初始化会清掉旧循环正在用的
    /// 11 项进程级静态，其中至少一处的内部字典非并发安全 → 结构损坏）。
    /// 旧循环退出后重载必须成功（用户故事「退出游戏后能加载其他游戏」）。
    /// </summary>
    [Fact]
    public async Task Reload_after_teardown_is_bounded_and_serialized()
    {
        var registry = CreateRegistry();
        Session? session = null;
        try
        {
            session = await StartAtTitleAsync(registry);
            await EnterLongLoopAsync(session);

            // 首次拆除：有界返回。
            var firstDelete = Task.Run(async () => await registry.DeleteSessionAsync());
            Assert.True(await CompletesAsync(firstDelete, AssertWindow), "首次拆除在窗口内未返回");
            await firstDelete;

            // 立刻重载：有界返回；若旧循环尚未退出，必须是明确失败且**不建新会话**。
            var during = Task.Run(async () => await registry.ReplaceForLoadGameAsync(SessionRegistryTests.FindTestGameDir()));
            Assert.True(await CompletesAsync(during, AssertWindow), "重载在窗口内未返回");
            var duringResult = await during;
            Assert.Contains(duringResult.Status,
                new[] { LoadGameStatus.Success, LoadGameStatus.PreviousSessionStillRunning });
            if (duringResult.Status == LoadGameStatus.PreviousSessionStillRunning)
                Assert.Null(registry.CurrentSession);   // ← 串行化不变量

            // 旧循环退出后重载 → 必须成功拿到新会话。
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
    /// 用例 3：拆除摘除会话引用后，需获取会话锁的端点仍可响应。
    /// 覆盖验收项「其他端点在拆除进行中仍可响应」——修复前它们排在同一把锁后面，
    /// 会被同步 join 一起冻住。
    /// </summary>
    [Fact]
    public async Task Endpoints_respond_after_teardown_detaches_session()
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
            // 那测的是"调用时机"而非"摘除后端点是否可响应"，属竞态假失败。
            // （修复前此处永远等不到摘除：锁被 join 持有，DetachSessionLocked 根本执行不到。）
            var detachDeadline = DateTime.UtcNow.AddMilliseconds(AssertWindow.TotalMilliseconds);
            while (DateTime.UtcNow < detachDeadline && registry.CurrentSession != null)
                await Task.Delay(20);
            Assert.True(registry.CurrentSession == null,
                $"拆除未在 {AssertWindow.TotalSeconds:F0}s 内摘除会话引用（改动前行为：永久挂起）");

            // 该端点会取 _sessionLock——修复前该锁被 join 长期持有，此调用会一起挂住。
            var subscribeTask = Task.Run(async () => await registry.TryGetWsSubscriptionAsync());
            Assert.True(await CompletesAsync(subscribeTask, AssertWindow),
                $"拆除后 WS 订阅端点在 {AssertWindow.TotalSeconds:F0}s 内未响应");
            // 已摘除 → 无活跃会话可订阅，返回 null 是正确语义（此时是确定性的）。
            Assert.Null(await subscribeTask);

            // 拆除本身最终仍要有界结束。
            Assert.True(await CompletesAsync(deleteTask, AssertWindow), "拆除在窗口内未返回");
            var result = await deleteTask;
            Assert.Contains(result.Status,
                new[] { SessionDeleteStatus.Removed, SessionDeleteStatus.TeardownTimeout });
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
