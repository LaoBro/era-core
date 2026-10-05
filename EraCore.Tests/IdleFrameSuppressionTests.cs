using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-004：空转帧抑制与画面判据对齐。
///
/// <para><b>构造（最高接缝：真实脚本 + 真实会话生命周期）</b>：
/// <list type="bullet">
/// <item><c>[6]</c> → <c>erb/AWAITIDLE.ERB</c>：循环体内<b>只有 AWAIT、无任何打印</b>，
/// 画全程不变 —— 空转开销的构造（2000ms）。</item>
/// <item><c>[5]</c> → <c>erb/AWAITFRAMES.ERB</c>：循环体内<b>每轮打印</b>（5 帧）——
/// 防跳帧的构造，用来钉住「画面变化判据」不会把本该推送的帧误判为无变化。</item>
/// </list></para>
///
/// <para><b>防假绿</b>：本测试的构造只有在 issue-003 落地后才有意义 —— 改动前 AWAIT 根本
/// 不产回合，「循环期间零回合」会平凡成立。故红测必须在 003 之上做：未加抑制时，
/// <c>[6]</c> 的 20 轮等待会产出约 12 个空帧，用例 1 应当失败。</para>
/// </summary>
[Collection("ServerState")]
public class IdleFrameSuppressionTests
{
    /// <summary>收集回合的耐心上限。</summary>
    private static readonly TimeSpan CollectWindow = TimeSpan.FromSeconds(20);

    /// <summary>单次等待一个回合的轮询间隔。</summary>
    private const int TurnPollMs = 200;

    /// <summary>
    /// 循环进行中的采样窗口。须落在 @TEST_AWAITIDLE 的 2000ms 循环之内，
    /// 且留足余量不撞上循环结束（结束时的状态转换是应当被推送的）。
    /// </summary>
    private static readonly TimeSpan IdleSampleWindow = TimeSpan.FromMilliseconds(1200);

    private static SessionRegistry CreateRegistry()
        => new(new SessionRegistryTests.NullTerminalSetup(), new GameConfigService(new ConfigData()));

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
            throw new Xunit.Sdk.XunitException(
                $"游戏未走到输入等待点，StateString={session.StateString}。LastError={session.LastError}");
        }
        return session;
    }

    private static void SendInput(Session session, string value)
        => session.IO.EnqueueInput($"{{\"type\":\"input\",\"value\":\"{value}\"}}");

    private static string TurnState(string turnJson)
    {
        using var doc = JsonDocument.Parse(turnJson);
        return doc.RootElement.GetProperty("state").GetString() ?? "";
    }

    private static async Task<string> WaitTurnAsync(Session session)
    {
        var deadline = DateTime.UtcNow.Add(CollectWindow);
        while (DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
                return result.Turn;
            if (result.Status == TurnWaitStatus.Closed)
                throw new Xunit.Sdk.XunitException("会话 IO 已关闭，但期望还有 turn 产出（脚本是否提前终止了？）");
            if (result.Status == TurnWaitStatus.ControlLost)
                throw new Xunit.Sdk.XunitException("等待 turn 期间控制权变更：" + result.Reason);
        }
        throw new Xunit.Sdk.XunitException($"在 {CollectWindow.TotalSeconds:F0}s 内未取到 turn");
    }

    /// <summary>在给定窗口内持续取回合，返回取到的回合列表（超时/关闭即停止）。</summary>
    private static async Task<List<string>> CollectTurnsAsync(Session session, TimeSpan window)
    {
        var turns = new List<string>();
        var deadline = DateTime.UtcNow.Add(window);
        while (DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
            {
                turns.Add(result.Turn);
                if (TurnState(result.Turn) == "Quit")
                    break;
                continue;
            }
            if (result.Status is TurnWaitStatus.Closed or TurnWaitStatus.ControlLost)
                break;
        }
        return turns;
    }

    /// <summary>
    /// 用例 1（主验收）：零输出的等待循环期间<b>不产出回合</b> —— 画面没变，
    /// 状态与输入需求也没变，序列化 / 传输 / 前端解析的开销就不用付。
    ///
    /// 未加抑制时的行为：每次等待都产出一个空帧，采样窗口内约 12 个。
    /// </summary>
    [Fact]
    public async Task Zero_output_wait_loop_produces_no_turns()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);

            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            // 选 [6] → @TEST_AWAITIDLE：20 轮「只 AWAIT、无打印」，共 2000ms
            SendInput(session, "6");
            var enteredTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(enteredTurn));

            // 采样窗口落在循环进行中
            var idleTurns = await CollectTurnsAsync(session, IdleSampleWindow);

            Assert.Empty(idleTurns);   // ← 核心断言：循环期间一个回合都不该有
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 2（本 issue 最容易写错的一条）：上述循环结束时的<b>状态转换必须被推送出去</b>。
    ///
    /// 抑制条件若只看 diff 是否为空，这段无输出的等待会一路被抑制到底 —— 连结尾的
    /// 「等待 → 结束」也一起吞掉，客户端会永远显示「运行中」，看起来像卡死。
    /// 这正是 spec 把「状态未变化」写进抑制条件的原因。
    /// </summary>
    [Fact]
    public async Task State_transition_at_end_of_idle_loop_is_still_delivered()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);

            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            SendInput(session, "6");
            var enteredTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(enteredTurn));

            // 循环跑完（2000ms）后打印 IDLE_DONE 并 QUIT —— 这一帧必须送出来
            var turns = await CollectTurnsAsync(session, CollectWindow);

            Assert.Contains(turns, t => t.Contains("IDLE_DONE", StringComparison.Ordinal));
            Assert.Contains(turns, t => TurnState(t) == "Quit");
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 3（防跳帧 / 画面判据回归）：循环内每轮<b>打印</b>时，每一帧都必须被推送 ——
    /// 画面变化判据不得把本该推送的帧误判为「无变化」而跳过。
    ///
    /// 断言 5 帧各自落在<b>不同</b>的回合且序号递增：任何一帧被跳过都会断链。
    /// </summary>
    [Fact]
    public async Task Every_printed_frame_is_delivered()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);

            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            SendInput(session, "5");   // @TEST_AWAITFRAMES：5 轮「打印 + AWAIT」
            var turns = await CollectTurnsAsync(session, CollectWindow);

            var sb = new StringBuilder();
            int previous = -1;
            for (int n = 0; n < 5; n++)
            {
                var idx = turns.FindIndex(t => t.Contains("FRAME-" + n, StringComparison.Ordinal));
                sb.Append($"FRAME-{n} → 回合 #{idx}\n");
                Assert.True(idx >= 0, $"FRAME-{n} 未被推送（跳帧）。\n{sb}");
                Assert.True(idx > previous, $"FRAME-{n} 未落在 FRAME-{n - 1} 之后的回合（跳帧/乱序）。\n{sb}");
                previous = idx;
            }
        }
        finally
        {
            registry.DisposeAll();
        }
    }
}
