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
/// issue-003：AWAIT 语义对齐 —— 每次 <c>AWAIT</c> 是否产出一次回合交付（推帧）。
///
/// <para><b>构造（最高接缝：真实脚本 + 真实会话生命周期）</b>：test_game 菜单 <c>[5]</c>
/// → <c>erb/AWAITFRAMES.ERB</c>：循环内每轮「打印一行带编号的 marker + <c>AWAIT 100</c>」。
/// 帧内容递增（<c>FRAME-0</c> … <c>FRAME-4</c>），故可区分「每轮一帧」与「只有最后一帧」。</para>
///
/// <para><b>防假绿</b>：断言<b>不能</b>写成「最终能收到回合」——那在 AWAIT 完全不推帧时
/// 也成立（脚本跑完后照样交付一次）。必须断言各帧落在<b>不同</b>的回合里且顺序递增。</para>
/// </summary>
[Collection("ServerState")]
public class ScriptAwaitFrameTests
{
    /// <summary>收集回合的耐心上限。</summary>
    private static readonly TimeSpan CollectWindow = TimeSpan.FromSeconds(15);

    /// <summary>单次等待一个回合的轮询间隔。</summary>
    private const int TurnPollMs = 300;

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
            // 同 ScriptInterruptionTests：启动即失败时把画面与异常一并抖出来，
            // 否则只能看到一个与真正原因无关的 Expected:"WaitInput" Actual:"Error"。
            throw new Xunit.Sdk.XunitException(
                $"游戏未走到输入等待点，StateString={session.StateString}。\n" +
                $"LastError={session.LastError}\n画面：\n{SafeSnapshot(session)}");
        }
        return session;
    }

    private static string SafeSnapshot(Session session)
    {
        try
        {
            var json = session.GetDisplaySnapshot();
            return json ?? "(快照为 null)";
        }
        catch (Exception ex)
        {
            return "(读取画面失败: " + ex.Message + ")";
        }
    }

    private static void SendInput(Session session, string value)
        => session.IO.EnqueueInput($"{{\"type\":\"input\",\"value\":\"{value}\"}}");

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

    private static async Task<string> WaitTurnAsync(Session session)
    {
        var deadline = DateTime.UtcNow.Add(CollectWindow);
        while (DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
                return result.Turn.Wire;
            if (result.Status == TurnWaitStatus.Closed)
                throw new Xunit.Sdk.XunitException("会话 IO 已关闭，但期望还有 turn 产出（脚本是否提前终止了？）");
            if (result.Status == TurnWaitStatus.ControlLost)
                throw new Xunit.Sdk.XunitException("等待 turn 期间控制权变更：" + result.Reason);
        }
        throw new Xunit.Sdk.XunitException($"在 {CollectWindow.TotalSeconds:F0}s 内未取到 turn");
    }

    /// <summary>
    /// 连续收集回合，直到拿到终态回合（Quit）、IO 关闭、或窗口耗尽。
    /// 不把 Closed/ControlLost 当错误 —— 脚本 QUIT 后会话本就会关闭 IO。
    /// </summary>
    private static async Task<List<string>> CollectTurnsAsync(Session session, int maxTurns)
    {
        var turns = new List<string>();
        var deadline = DateTime.UtcNow.Add(CollectWindow);
        while (turns.Count < maxTurns && DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
            {
                turns.Add(result.Turn.Wire);
                if (TurnState(result.Turn.Wire) == "Quit")
                    break;
                continue;
            }
            if (result.Status is TurnWaitStatus.Closed or TurnWaitStatus.ControlLost)
                break;
        }
        return turns;
    }

    /// <summary>第一个包含指定文本的回合序号；未出现返回 -1。</summary>
    private static int FirstTurnContaining(List<string> turns, string marker)
        => turns.FindIndex(t => t.Contains(marker, StringComparison.Ordinal));

    /// <summary>
    /// 用例 1（主验收）：每次 <c>AWAIT</c> 产出一次回合 —— 各帧落在<b>不同</b>的回合里且递增。
    ///
    /// 改动前的行为：<c>AWAIT</c> 走 <c>Thread.Sleep</c>，等待期间状态是「休眠」而非
    /// 「等待输入」，不触发回合边界 —— 整个循环跑完才交付一次回合，所有 FRAME marker
    /// 挤在同一个回合里，<c>FRAME-0/1/2</c> 的首现回合序号相等 → 断言失败。
    /// </summary>
    [Fact]
    public async Task Await_delivers_one_turn_per_wait_with_increasing_frames()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);

            // 回合 1：标题画面，等待输入
            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            // 选 [5] → @TEST_AWAITFRAMES：5 轮「打印 + AWAIT」
            SendInput(session, "5");

            var turns = await CollectTurnsAsync(session, maxTurns: 12);
            Assert.NotEmpty(turns);

            var idx0 = FirstTurnContaining(turns, "FRAME-0");
            var idx1 = FirstTurnContaining(turns, "FRAME-1");
            var idx2 = FirstTurnContaining(turns, "FRAME-2");

            Assert.True(idx0 >= 0, $"未观察到 FRAME-0。\n实际回合：\n{Describe(turns)}");
            Assert.True(idx1 > idx0,
                $"FRAME-1 与 FRAME-0 落在同一个回合（序号 {idx0}）—— AWAIT 没有推帧，\n" +
                $"画面是整块出现的。\n实际回合：\n{Describe(turns)}");
            Assert.True(idx2 > idx1,
                $"FRAME-2 未落在 FRAME-1 之后的回合（FRAME-1={idx1}, FRAME-2={idx2}）—— \n" +
                $"逐帧交付不成立。\n实际回合：\n{Describe(turns)}");
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 用例 2（不可打断性验收，issue 10）：长 <c>AWAIT</c> 期间收到客户端输入应<b>一律吞掉</b>
    /// —— 不推进、不报错，等待只由定时器正常到期结束。
    ///
    /// <para>为什么反过来钉：<see cref="InputType.Void"/> 的参考实现语义是
    /// 「入力不能、待つしかない」（<c>PressEnterKey</c> 对 Void 直接 return）。era-core 曾为
    /// <c>AWAIT</c> 开过 <c>Skippable</c> 后门（issue-003），实测在 eraTW <c>@BADAPPLE</c>
    /// 上表现为顶部「跳过」图标逐帧闪烁；spec §10.1 的判据是
    /// 「参考实现不能打断的指令，era-core 也不得能打断」，故该后门撤回。</para>
    ///
    /// <para><b>防假绿</b>：断言不是「最终能拿到 SKIP_DONE」（脚本跑完照样有），而是
    /// 「从投输入到 SKIP_DONE 的耗时 ≥ 1.5s」——撤回前输入会走 <c>SubmitTimeout</c>
    /// 立即续跑，耗时近 0 ⇒ 必然红。</para>
    /// </summary>
    [Fact]
    public async Task Long_await_swallows_client_input()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);

            var titleTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(titleTurn));

            // 选 [7] → @TEST_AWAITSKIP：单个 3 秒的 AWAIT
            SendInput(session, "7");
            var enteredTurn = await WaitTurnAsync(session);
            Assert.Equal("WaitInput", TurnState(enteredTurn));

            // 让脚本确实进入等待（否则输入会落在标题菜单上，测的是另一件事）
            var settled = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < settled && session.StateString != "WaitInput")
                await Task.Delay(20);
            await Task.Delay(200);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            SendInput(session, "");   // 任意输入：空串即"按了个键"

            var turns = await CollectTurnsAsync(session, maxTurns: 4);
            watch.Stop();

            Assert.Contains(turns, t => t.Contains("SKIP_DONE", StringComparison.Ordinal));
            // AWAIT 自身 3 秒，投放输入时已过去约 0.2s。输入若被吞掉，剩余 ≈2.8s 只能靠定时器走完。
            Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5),
                $"从投输入到拿到 SKIP_DONE 只用了 {watch.Elapsed.TotalSeconds:F2}s —— " +
                "AWAIT 被客户端输入提前结束了（定时等待必须不可打断）。\n实际回合：\n" + Describe(turns));
            // 吞掉 ≠ 报错：不得回任何 error 帧。
            Assert.DoesNotContain(turns, t => t.Contains("\"error\"", StringComparison.Ordinal));
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
            var json = turns[i];
            string state = "";
            try { state = TurnState(json); } catch { /* 摘要用，不因解析失败中断 */ }

            // 只摘出 FRAME-/DONE 相关的 marker，避免整段 JSON 刷屏
            var markers = new List<string>();
            for (int n = 0; n < 8; n++)
                if (json.Contains("FRAME-" + n, StringComparison.Ordinal))
                    markers.Add("FRAME-" + n);
            if (json.Contains("FRAMES_DONE", StringComparison.Ordinal))
                markers.Add("FRAMES_DONE");

            sb.Append($"  #{i} state={state} markers=[{string.Join(",", markers)}]\n");
        }
        return sb.ToString();
    }
}
