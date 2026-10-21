using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-002：脚本执行层中断检查点补齐 —— 长回合脚本能否在下一个指令边界停下，
/// 以及检查点移到「每条指令之前」后正常流程是否仍逐句执行。
///
/// <para><b>构造（最高接缝：真实脚本 + 真实会话生命周期）</b>：全部经 test_game 驱动。
/// <list type="bullet">
/// <item><c>[2]</c> → <c>@TEST_LONGLOOP(8000)</c>：循环体内每条语句都是执行检查点，
/// 用来验证「拆除信号到达后脚本在下一个指令边界停止」。</item>
/// <item><c>[4]</c> → <c>@TEST_FLOW</c>（<c>erb/FLOWCHECK.ERB</c>）：覆盖等待输入、
/// 两层嵌套调用、显式/隐式函数返回、IF/ELSEIF/ELSE、SIF、WHILE、FOR、SELECTCASE，
/// 用来验证没有「静默截断」（正常流程少执行语句）。</item>
/// </list></para>
///
/// <para><b>防假绿（spec Testing Decisions）</b>：本测试先于生产代码改动落地。改动前，
/// 用例 1 拿到的是 <c>TeardownTimeout</c>（脚本无中断检查点，必须自己跑完 8 秒），
/// 用例 2 应<b>绿</b>（基线正常）——两者组合才证明测到了东西。</para>
///
/// <para><b>测量纪律</b>：拆除调用经 <c>Task.Run</c> 包裹 —— 同 <c>SessionTeardownTimeoutTests</c>
/// 的原因：<c>DeleteSessionAsync</c> 首行的锁 await 在锁空闲时不挂起，直接调用会让整个方法
/// 同步跑在测试线程上，使 <c>Task.WhenAny</c> 拿到已完成 task 而假绿。</para>
///
/// <para><b>构造陷阱（改 test_game 前必读，已踩过一次）</b>：ERB 的局部变量名<b>不能取单个字母</b>
/// —— 例如 <c>#DIM I</c> 会被判为 Emuera 保留变量名，冷加载（走源码解析）时产生警告 Lv2，
/// 引擎的警告策略据此拒绝启动 → <c>State=Error</c>；而 IR 缓存命中时会跳过解析，症状消失，
/// 极易误判为偶发失败。其症状表面上看只是 <c>Expected:"WaitInput" Actual:"Error"</c>，
/// 与真正原因毫无字面关联。<c>StartAtTitleAsync</c> 已把画面与 <c>LastError</c> 一并抖出。</para>
/// </summary>
[Collection("ServerState")]
public class ScriptInterruptionTests
{
    /// <summary>测试注入的拆除超时。</summary>
    private static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>拆除的断言窗口：须大于 <see cref="TeardownTimeout"/>。</summary>
    private static readonly TimeSpan AssertWindow = TimeSpan.FromSeconds(6);

    /// <summary>@TEST_LONGLOOP 墙钟 8 秒。断言「拆除耗时远小于此」= 脚本没跑完就停了。</summary>
    private static readonly TimeSpan LongLoopWallClock = TimeSpan.FromSeconds(8);

    /// <summary>等旧循环彻底退出（供清理），避免残留循环污染同 collection 的后续测试。</summary>
    private static readonly TimeSpan LoopExitWindow = TimeSpan.FromSeconds(30);

    /// <summary>取一个 turn 的耐心上限。</summary>
    private static readonly TimeSpan TurnWindow = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <c>erb/FLOWCHECK.ERB</c> 的 marker 序列（顺序敏感）。每调用一次拿到的
    /// 第二个输入的实参是 <c>7</c>，故 frame 为 <c>FC-03-AFTER-INPUT-7</c>。
    /// 任何一条语句被静默跳过都会在序列上断链。
    /// </summary>
    private static readonly string[] ExpectedFlowMarkers =
    [
        "FC-01-START",
        "FC-11-ELSEIF-TAKEN",   // IF/ELSEIF/ELSE：命中 ELSEIF 分支
        "FC-13-SIF-TAKEN",      // SIF：单语句形式（无 ENDIF）
        "FC-20-OUTER-IN",       // 嵌套调用：外层
        "FC-22-INNER",          // 嵌套调用：内层（第二个 @ 标签处隐式返回）
        "FC-21-OUTER-OUT",
        "FC-30-WHILE-0",
        "FC-30-WHILE-1",
        "FC-31-FOR-0",
        "FC-31-FOR-1",
        "FC-31-FOR-2",
        "FC-41-CASE2",          // SELECTCASE：命中 CASE 2
        "FC-02-BEFORE-INPUT",
        "FC-03-AFTER-INPUT-7",  // 等待输入：第二次 INPUT 的返回值
        "FC-SUM n=14",
        "FC-04-END",
    ];

    private static SessionRegistry CreateRegistry()
        => new(new SessionRegistryTests.NullTerminalSetup(), new GameConfigService(new ConfigData()))
        {
            TeardownTimeout = TeardownTimeout,
        };

    private static async Task<Session> StartAtTitleAsync(SessionRegistry registry)
    {
        var load = await registry.ReplaceForLoadGameAsync(SessionRegistryTests.FindTestGameDir());
        Assert.Equal(LoadGameStatus.Success, load.Status);

        var session = registry.CurrentSession!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && session.StateString != "WaitInput")
            await Task.Delay(50);

        if (session.StateString != "WaitInput")
        {
            // 启动即失败时把画面与异常一并抖出来 —— 否则只能看到一个和真正原因无关的
            // Expected:"WaitInput" Actual:"Error"，排查全靠猜（已踩过一次：见类注释「构造陷阱」）。
            throw new Xunit.Sdk.XunitException(
                $"游戏未走到输入等待点，StateString={session.StateString}。\n" +
                $"LastError={session.LastError}\n画面：\n{SafeSnapshot(session)}");
        }
        return session;
    }

    /// <summary>取画面文本，失败时返回占位串（诊断用，不参与断言）。</summary>
    private static string SafeSnapshot(Session session)
    {
        try
        {
            var json = session.GetDisplaySnapshot();
            return json == null ? "(快照为 null)" : ExtractText(json);
        }
        catch (Exception ex)
        {
            return "(读取画面失败: " + ex.Message + ")";
        }
    }

    /// <summary>把菜单选项投进 IO —— 客户端真实投放输入的那条路径。</summary>
    private static void SendInput(Session session, string value)
        => session.IO.EnqueueInput($"{{\"type\":\"input\",\"value\":\"{value}\"}}");

    /// <summary>等待脚本进入执行态（State 离开输入等待）。仅供长时间运行的分枝使用。</summary>
    private static async Task WaitRunningAsync(Session session)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && session.StateString != "Running")
            await Task.Delay(20);
        Assert.Equal("Running", session.StateString);
    }

    private static async Task<bool> CompletesAsync(Task task, TimeSpan window)
        => await Task.WhenAny(task, Task.Delay(window)) == task;

    private static async Task WaitForLoopExitAsync(Session? session)
    {
        if (session == null)
            return;
        var deadline = DateTime.UtcNow.AddMilliseconds(LoopExitWindow.TotalMilliseconds);
        while (DateTime.UtcNow < deadline && session.IsRunning)
            await Task.Delay(50);
    }

    /// <summary>取下一个 turn 的 JSON 原文（超时轮询，直到拿到一个 turn 或报出明确原因）。</summary>
    private static async Task<string> WaitTurnAsync(Session session)
    {
        var deadline = DateTime.UtcNow.Add(TurnWindow);
        while (DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(500, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
                return result.Turn.Wire;
            if (result.Status == TurnWaitStatus.Closed)
                throw new Xunit.Sdk.XunitException("会话 IO 已关闭，但期望还有 turn 产出（脚本是否提前终止了？）");
            if (result.Status == TurnWaitStatus.ControlLost)
                throw new Xunit.Sdk.XunitException("等待 turn 期间控制权变更：" + result.Reason);
        }
        throw new Xunit.Sdk.XunitException($"在 {TurnWindow.TotalSeconds:F0}s 内未取到 turn");
    }

    /// <summary>
    /// v12：帧不再带顶层 <c>state</c>——本套用例只区分「终态」与「非终态停稳点」，
    /// 故从契约 <c>awaiting</c> 取终态值（无 <c>ended</c> 一律归为非终态 "WaitInput"）。
    /// </summary>
    private static string TurnState(string turnJson)
    {
        using var doc = JsonDocument.Parse(turnJson);
        var awaiting = doc.RootElement.GetProperty("awaiting");
        return awaiting.TryGetProperty("ended", out var ended)
            ? ended.GetProperty("terminal").GetString() ?? ""
            : "WaitInput";
    }

    /// <summary>
    /// 从画面快照提取拼接文本（口径同 <c>tests/emuera_server.py</c> 的 <c>snapshot_text</c>：
    /// lines[].entries[].segments[].text，行间换行符分隔）。
    /// </summary>
    private static string SnapshotText(Session session)
    {
        var json = session.GetDisplaySnapshot();
        Assert.NotNull(json);
        return ExtractText(json!);
    }

    /// <summary>快照 JSON → 拼接文本。</summary>
    private static string ExtractText(string snapshotJson)
    {
        using var doc = JsonDocument.Parse(snapshotJson);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("lines", out var lines))
        {
            foreach (var line in lines.EnumerateArray())
            {
                if (!line.TryGetProperty("entries", out var entries))
                    continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("segments", out var segments))
                        continue;
                    foreach (var seg in segments.EnumerateArray())
                        if (seg.TryGetProperty("text", out var text))
                            sb.Append(text.GetString());
                }
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 用例 1（主验收）：长回合进行中拆除会话 → 脚本在下一个指令边界停下，
    /// 拆除因此<b>成功且迅速完成</b>，而不是等脚本跑完 8 秒后超时。
    ///
    /// 改动前的行为：<see cref="Session.RequestTeardown"/> 不触碰脚本层的运行状态，
    /// <c>ScriptProc.Run</c> 唯一的检查点读到的一直是「运行中」→ 脚本把剩余循环跑完
    /// → 拆除等待超时，返回 <c>TeardownTimeout</c>（耗时等于 2s 上界）。
    /// </summary>
    [Fact]
    public async Task Long_turn_stops_at_next_instruction_boundary_on_teardown()
    {
        var registry = CreateRegistry();
        Session? session = null;
        try
        {
            session = await StartAtTitleAsync(registry);

            SendInput(session, "2");   // @TEST_LONGLOOP(8000)
            await WaitRunningAsync(session);

            var watch = Stopwatch.StartNew();
            var deleteTask = Task.Run(async () => await registry.DeleteSessionAsync());
            var completed = await CompletesAsync(deleteTask, AssertWindow);
            watch.Stop();

            Assert.True(completed, $"拆除会话在 {AssertWindow.TotalSeconds:F0}s 内未返回");
            var result = await deleteTask;

            // 核心断言：脚本被中断 → 游戏循环在超时之前就退出了 → 拆除成功。
            // 改动前这里是 TeardownTimeout（"脚本跑不完"，耗时 ≡ 2s 上界）。
            Assert.Equal(SessionDeleteStatus.Removed, result.Status);
            Assert.False(session.IsRunning, "游戏循环应已退出");

            // 停在下一个指令边界的证据：脚本墙钟 8 秒，若未中断则拆除至少要等到超时
            // 且循环仍活着。耗时须显著小于 8 秒。
            Assert.True(watch.Elapsed < LongLoopWallClock,
                $"拆除耗时 {watch.Elapsed.TotalSeconds:F1}s，未显著早于脚本自身墙钟 " +
                $"{LongLoopWallClock.TotalSeconds:F0}s —— 脚本可能没有被中断");
        }
        finally
        {
            await WaitForLoopExitAsync(session);
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 2（无回归，本 issue 的回归风险主体）：检查点移到「每条指令之前」后，
    /// 正常游戏流程必须仍逐句执行 —— 等待输入、两层嵌套调用、函数返回、
    /// IF/ELSEIF/ELSE、SIF、WHILE、FOR、SELECTCASE 全覆盖。
    ///
    /// 判定方式不是"看某个内部字段"，而是<b>回合的可观测产出</b>：marker 序列必须
    /// 完整且有序，SUMMARY 行锁定总次数。任何一条语句被静默截断都会在此断链。
    /// </summary>
    [Fact]
    public async Task Normal_flow_executes_every_statement_with_pre_instruction_check()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);

            // 回合 1：标题画面，等待输入
            var firstTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(firstTurn));

            // 回合 2：选 [4] → @TEST_FLOW 一路执行到自己的 INPUT
            SendInput(session, "4");
            var secondTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(secondTurn));

            // 回合 3：喂给 @TEST_FLOW 内部 INPUT 的值（7，与菜单选项区分开）
            SendInput(session, "7");
            var thirdTurn = await WaitTurnAsync(session);
            Assert.Equal("Quit", TurnState(thirdTurn));

            var text = SnapshotText(session);

            // 顺序敏感：cursor 单调右移，故「缺失」与「乱序」同构失败
            var cursor = 0;
            foreach (var marker in ExpectedFlowMarkers)
            {
                var idx = text.IndexOf(marker, cursor, StringComparison.Ordinal);
                Assert.True(idx >= 0,
                    $"marker {marker} 未出现在预期位置之后 —— 存在静默截断。\n实际画面：\n{text}");
                cursor = idx + marker.Length;
            }
        }
        finally
        {
            registry.DisposeAll();
        }
    }
}
