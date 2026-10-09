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
/// 03（server / WS 宿主的让路 drain，B4 / spec v5 §3.2 ④）的验收闸门。
///
/// <para><b>构造（最高接缝：真实脚本 + 真实会话生命周期）</b>：test_game 菜单
/// <c>[13]</c>/<c>[14]</c>/<c>[15]</c>/<c>[16]</c> → <c>erb/YIELDDRAIN.ERB</c>。
/// 无参 <c>AWAIT</c>（零时长让路点）不再是「只刷新就卡住」的不可续跑状态 ——
/// 宿主 drain 在交付一帧后同线程续跑。</para>
///
/// <para><b>防假绿</b>：核心断言<b>不是</b>「脚本最终能跑完」，而是各步落在<b>不同</b>的
/// 回合里且顺序递增，<b>且</b>画面没变的那一轮不产回合（两条同时约束才是 drain 的正确形态）。
/// 改动前无参 <c>AWAIT</c> 挂起后无人续跑 ⇒ 脚本卡在第一个让路点 ⇒ 必然红（已实测）。</para>
///
/// <para>⚠️ <b>断言锚点选择</b>：被断言的 marker 全部落在<b>普通帧</b>（应答帧 / drain 帧）里，
/// 不落在 QUIT 的终态帧里 —— 帧洪峰 + 立即 QUIT 与既有 <c>TurnDelivery</c> 的 final-turn
/// 一次性交付之间存在投递竞态，终态帧偶发被吞（与本次改动无关）。</para>
/// </summary>
[Collection("ServerState")]
public class YieldDrainTests
{
    /// <summary>收集回合的耐心上限（含终态）。</summary>
    private static readonly TimeSpan CollectWindow = TimeSpan.FromSeconds(8);

    /// <summary>「循环期间零帧」的采样窗口：GREEN 下让路 drain 同步跑完，帧若存在必然立刻到齐。</summary>
    private static readonly TimeSpan IdleSampleWindow = TimeSpan.FromMilliseconds(1500);

    /// <summary>单次等待一个回合的轮询间隔。</summary>
    private const int TurnPollMs = 200;

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

    /// <summary>在 <paramref name="window"/> 内持续取回合，直到取满 / Quit / 关闭 / 窗口耗尽。</summary>
    private static async Task<List<string>> CollectTurnsAsync(Session session, TimeSpan window, int maxTurns)
    {
        var turns = new List<string>();
        var deadline = DateTime.UtcNow.Add(window);
        while (turns.Count < maxTurns && DateTime.UtcNow < deadline)
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

    private static int FirstTurnContaining(List<string> turns, string marker)
        => turns.FindIndex(t => t.Contains(marker, StringComparison.Ordinal));

    /// <summary>持续取回合直到出现包含 <paramref name="marker"/> 的回合（按序累积进 <paramref name="collected"/>）。</summary>
    private static async Task<string> WaitTurnContainingAsync(Session session, string marker, List<string> collected)
    {
        var deadline = DateTime.UtcNow.Add(CollectWindow);
        while (DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
            {
                collected.Add(result.Turn);
                if (result.Turn.Contains(marker, StringComparison.Ordinal))
                    return result.Turn;
                continue;
            }
            if (result.Status is TurnWaitStatus.Closed or TurnWaitStatus.ControlLost)
                break;
        }
        throw new Xunit.Sdk.XunitException(
            $"在 {CollectWindow.TotalSeconds:F0}s 内未取到包含 \"{marker}\" 的回合。\n实际回合：\n{Describe(collected)}");
    }

    /// <summary>断言一串递增 marker 各自落在不同回合里。</summary>
    private static void AssertFramesInDistinctTurns(List<string> turns, string prefix, int count)
    {
        var sb = new StringBuilder();
        int previous = -1;
        for (int n = 0; n < count; n++)
        {
            var marker = prefix + "-" + n;
            var idx = FirstTurnContaining(turns, marker);
            sb.Append($"{marker} → 回合 #{idx}\n");
            Assert.True(idx >= 0, $"{marker} 未被推送（跳帧）。\n{sb}\n实际回合：\n{Describe(turns)}");
            Assert.True(idx > previous,
                $"{marker} 未落在前一步之后的回合（跳帧/乱序）。\n{sb}\n实际回合：\n{Describe(turns)}");
            previous = idx;
        }
    }

    // ---------------------------------------------------------------
    // 用例 1：无参 AWAIT 产帧 —— 每一步一帧，帧落在脚本走完该步之后
    // ---------------------------------------------------------------
    [Fact]
    public async Task Bare_await_delivers_one_frame_per_step()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            SendInput(session, "13");   // @TEST_YIELDFRAMES：5 步「打印 → AWAIT → 擦除」+ 完成标记
            var turns = new List<string>();
            await WaitTurnContainingAsync(session, "YFRAMES_DONE", turns);

            AssertFramesInDistinctTurns(turns, "YFRAME", 5);
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    // ---------------------------------------------------------------
    // 用例 2：AWAIT 0 同上（同一零时长让路点）
    // ---------------------------------------------------------------
    [Fact]
    public async Task Await_zero_delivers_one_frame_per_step()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            SendInput(session, "14");   // @TEST_YIELDFRAMES0
            var turns = new List<string>();
            await WaitTurnContainingAsync(session, "ZFRAMES_DONE", turns);

            AssertFramesInDistinctTurns(turns, "ZFRAME", 5);
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    // ---------------------------------------------------------------
    // 用例 3：画面无变化时不发帧 —— 无重绘的让路循环 ⇒ 零帧
    //        （且必须能推进到真 WAIT，排除「卡在第一个让路点」造成的平凡零帧）
    // ---------------------------------------------------------------
    [Fact]
    public async Task No_redraw_yield_loop_produces_no_turns()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            SendInput(session, "15");   // @TEST_YIELDIDLE：200 轮「只 AWAIT、无打印」
            var enteredTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(enteredTurn));

            // 采样窗口：让路 drain 同步跑完，期间画面没变 ⇒ 一个回合都不该有。
            var idleTurns = await CollectTurnsAsync(session, IdleSampleWindow, maxTurns: 50);
            Assert.True(idleTurns.Count == 0,
                $"无重绘让路循环期间产出了 {idleTurns.Count} 个回合（应为 0）。\n{Describe(idleTurns)}");

            // 但脚本确实被 drain 推到了循环之后（否则「零帧」是卡死的平凡结果）：
            // 投输入能推进到循环后的标记。
            SendInput(session, "7");
            await WaitTurnContainingAsync(session, "YIELDIDLE_INPUT=7", new List<string>());
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    // ---------------------------------------------------------------
    // 用例 4：drain 终止性 —— 脚本到达真 WAIT 时 drain 退出，且不吞本次输入的接收能力
    // ---------------------------------------------------------------
    [Fact]
    public async Task Drain_stops_at_real_wait_and_leaves_input_usable()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            SendInput(session, "16");   // @TEST_YIELDWAIT：5 步让路 → 真 WAIT（INPUT）
            var frames = new List<string> { await WaitTurnAsync(session) };

            // 让路帧先到齐（WAITFRAME-0 在应答帧里，1..4 由 drain 交付）。
            var deadline = DateTime.UtcNow.Add(CollectWindow);
            while (frames.Count < 5 && DateTime.UtcNow < deadline)
            {
                var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
                if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
                    frames.Add(result.Turn);
                else if (result.Status is TurnWaitStatus.Closed or TurnWaitStatus.ControlLost)
                    break;
            }
            AssertFramesInDistinctTurns(frames, "WAITFRAME", 5);

            // 真 WAIT 的输入能力未被吞：投输入后脚本继续推进。
            SendInput(session, "7");
            var turns = await CollectTurnsAsync(session, CollectWindow, maxTurns: 10);

            Assert.Contains(turns, t => t.Contains("YIELDWAIT_INPUT=7", StringComparison.Ordinal));
            Assert.Contains(turns, t => TurnState(t) == "Quit");
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    /// <summary>把收集到的回合摘要成可读文本（断言失败时定位用）。</summary>
    private static string Describe(List<string> turns)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < turns.Count; i++)
        {
            string state = "";
            try { state = TurnState(turns[i]); } catch { /* 摘要用 */ }
            var markers = new List<string>();
            foreach (var prefix in new[] { "YFRAME-", "ZFRAME-", "WAITFRAME-" })
                for (int n = 0; n < 6; n++)
                    if (turns[i].Contains(prefix + n, StringComparison.Ordinal))
                        markers.Add(prefix + n);
            foreach (var done in new[] { "YFRAMES_DONE", "ZFRAMES_DONE", "YIELDIDLE_DONE", "YIELDWAIT_INPUT" })
                if (turns[i].Contains(done, StringComparison.Ordinal))
                    markers.Add(done);
            sb.Append($"  #{i} state={state} markers=[{string.Join(",", markers)}]\n");
        }
        return sb.ToString();
    }
}
