using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;
using Xunit.Abstractions;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue 13 的<b>端到端动画回归防护</b>：真实脚本 + 真实引擎跑一支「多行段落动画」，
/// 逐帧断言搬运的显示行数与内容。
///
/// <para>为什么既有的 <see cref="RedrawAnimFrameTests"/>（V0–V4）不够：它们全是
/// <b>1:1 行</b>（一个 <c>PRINTL</c> = 1 逻辑行 = 1 显示行），<c>CLEARLINE n</c> 的两个口径
/// 永远相等 ⇒ 逻辑行/显示行的分歧在那些帧流里<b>根本不存在</b>，全绿也发现不了 issue 13。</para>
///
/// <para>本探针（<c>test_game/erb/ANIMPARA.ERB</c>）刻意造出 eraTW <c>@BADAPPLE</c> 的形状：
/// <c>HTML_PRINT</c> 一段 <c>&lt;br&gt;</c> 分隔的多行文本 = <b>1 逻辑行 / 20 显示行</b>，
/// 再打 5 行 UI ⇒ 每帧 <b>6 逻辑行 / 25 显示行</b>，然后
/// <c>CLEARLINE LINECOUNT - BASELINE</c>（参数 6）整段擦掉重印。</para>
///
/// <para><b>防假绿</b>：断言不是「收到帧」「行数不变」——那在缺陷下同样成立。
/// 而是两个具体判据：
/// <list type="bullet">
///   <item>每帧 <c>clear_line_diff</c> == <c>append</c> == <b>25</b>（缺陷下是 6/6）；</item>
///   <item>每帧 append 的<b>首行</b>必须是本帧段落的<b>第 0 行</b> <c>F{IDX}-0</c>
///         —— 缺陷下只有尾部 6 行被替换，首行会是 <c>F{IDX}-19</c>，
///         即「画面只有下边一小截在动」的直接编码。</item>
/// </list></para>
/// </summary>
[Collection("ServerState")]
public class AnimParagraphFrameTests
{
    private readonly ITestOutputHelper _output;

    public AnimParagraphFrameTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 期望的「带 clear 的循环帧」下界。ERB 跑 10 帧，其中首帧被并进入场那一帧
    /// （纯 append、无 clear）、末帧常与 <c>QUIT</c> 帧合并 ⇒ 实测稳定拿到 8 帧。
    /// 取下界 6 留余量：既要求多帧一致（单帧误判不足以通过），又不锁死精确帧数
    /// （空转帧抑制的合法演进不该让它变红）。
    /// </summary>
    private const int LoopFrames = 6;

    /// <summary>每帧搬运行数：1 行帧序号 + 20 行段落 + 5 行 UI。</summary>
    private const int RowsPerFrame = 26;

    private static readonly TimeSpan CollectWindow = TimeSpan.FromSeconds(15);
    private const int TurnPollMs = 300;
    private const int MaxTurns = 40;

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
            throw new Xunit.Sdk.XunitException(
                $"游戏未走到输入等待点，StateString={session.StateString}。\nLastError={session.LastError}");
        return session;
    }

    private static void SendInput(Session session, string value)
        => session.IO.EnqueueInput($"{{\"type\":\"input\",\"value\":\"{value}\"}}");

    private static async Task<List<string>> CollectTurnsAsync(Session session)
    {
        var turns = new List<string>();
        var deadline = DateTime.UtcNow.Add(CollectWindow);
        while (turns.Count < MaxTurns && DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
            {
                turns.Add(result.Turn.Wire);
                if (TurnTerminal(result.Turn.Wire) == "Quit")
                    break;
                continue;
            }
            if (result.Status is TurnWaitStatus.Closed or TurnWaitStatus.ControlLost)
                break;
        }
        return turns;
    }

    private static string TurnTerminal(string turnJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(turnJson);
            if (doc.RootElement.TryGetProperty("awaiting", out var a)
                && a.TryGetProperty("ended", out var ended)
                && ended.TryGetProperty("terminal", out var t))
                return t.GetString() ?? "";
        }
        catch { /* 摘要用 */ }
        return "";
    }

    // ------------------------------------------------------------------
    // 帧解析：只取「判断需要的那两个数」——清了几行、补进来的首行长什么样
    // ------------------------------------------------------------------

    private sealed record FrameOps(int Clear, List<string> Appended);

    private static FrameOps? ParseFrameOps(string turnJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(turnJson);
            if (!doc.RootElement.TryGetProperty("diff", out var diff)
                || diff.ValueKind != JsonValueKind.Object
                || !diff.TryGetProperty("lineOps", out var lineOps)
                || lineOps.ValueKind != JsonValueKind.Array)
                return null;

            int clear = 0;
            var appended = new List<string>();
            foreach (var op in lineOps.EnumerateArray())
            {
                var t = op.TryGetProperty("type", out var tv) ? tv.GetString() : null;
                if (t == "clear_line_diff")
                    clear += op.TryGetProperty("clearCount", out var cc) ? cc.GetInt32() : 0;
                else if (t == "append" && op.TryGetProperty("newLines", out var nl)
                         && nl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var line in nl.EnumerateArray())
                        appended.Add(FirstLineText(line));
                }
            }
            return new FrameOps(clear, appended);
        }
        catch { return null; }
    }

    /// <summary>一行 = 其全部 segment 文本拼接（够区分本探针用到的行）。</summary>
    private static string FirstLineText(JsonElement line)
    {
        var sb = new StringBuilder();
        if (!line.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("segments", out var segs)
                || segs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var seg in segs.EnumerateArray())
                if (seg.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                    sb.Append(tx.GetString());
        }
        return sb.ToString();
    }

    /// <summary>动画帧 = 补进来的行里含 <c>FRAME-{IDX}</c> 标记的帧。</summary>
    private static bool IsAnimFrame(FrameOps f)
        => f.Appended.Any(t => Regex.IsMatch(t, @"^FRAME-\d+$"));

    private static string Describe(List<string> turns)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < turns.Count; i++)
        {
            var f = ParseFrameOps(turns[i]);
            sb.Append($"  #{i:D2} terminal={TurnTerminal(turns[i]),-6} ");
            if (f == null)
                sb.Append("ops=[no-diff]\n");
            else
            {
                sb.Append($"clear={f.Clear,-3} append={f.Appended.Count,-3} " +
                          $"head={Trunc(f.Appended.Count > 0 ? f.Appended[0] : "")} " +
                          $"tail={Trunc(f.Appended.Count > 0 ? f.Appended[^1] : "")}\n");
                if (TurnTerminal(turns[i]) == "Error")
                    sb.Append("   全文: " + string.Join(" ⏎ ", f.Appended) + "\n");
            }
        }
        return sb.ToString();
    }

    private static string Trunc(string s) => s.Length <= 10 ? s : s[..10];

    // ------------------------------------------------------------------
    // 判据
    // ------------------------------------------------------------------

    /// <summary>
    /// 主判据：每一循环帧都整段搬运 —— <c>clear 25 + append 25</c>，
    /// 且 append 的首行是本帧段落的第 0 行（<c>F{IDX}-0</c>）。
    /// </summary>
    [Fact]
    public async Task Every_frame_carries_the_whole_multi_row_paragraph()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);

            SendInput(session, "17");   // → @TEST_ANIMPARA
            var turns = await CollectTurnsAsync(session);

            _output.WriteLine("=== @TEST_ANIMPARA 帧流 ===");
            _output.WriteLine(Describe(turns));

            var frames = turns.Select(ParseFrameOps)
                              .Where(f => f != null && IsAnimFrame(f!))
                              .Select(f => f!)
                              .ToList();

            Assert.Contains(turns, t => t.Contains("ANIM_DONE", StringComparison.Ordinal));

            // 循环帧：带 clear 的那些（首帧是纯 append，不计）
            var loopFrames = frames.Where(f => f.Clear > 0).ToList();
            Assert.True(loopFrames.Count >= LoopFrames,
                $"带 clear 的循环帧只有 {loopFrames.Count} 帧，少于 {LoopFrames}。\n{Describe(turns)}");

            foreach (var f in loopFrames)
            {
                Assert.Equal(RowsPerFrame, f.Clear);
                Assert.Equal(RowsPerFrame, f.Appended.Count);

                // 内容判据一：帧序号在最前 ⇒ 内容确实在推进（不是同一帧反复重印）。
                Assert.Matches(@"^FRAME-\d+$", f.Appended[0]);
                // 内容判据二：整段被换掉 ⇒ 段落**首行** P-0 必须出现在补进来的第 2 行。
                // 缺陷下只有尾部 7 行被替换 ⇒ 首行会变成 P-18（"只有下边一小截在动"）⇒ 必红。
                Assert.Equal("P-0", f.Appended[1]);
                Assert.Equal("P-19", f.Appended[20]);
                Assert.Equal("UI-4", f.Appended[25]);
            }

            // 各帧必须是**不同**的帧（内容在推进），而不是同一帧反复重印
            var heads = loopFrames.Select(f => f.Appended[0]).ToList();
            Assert.Equal(heads.Count, heads.Distinct().Count());
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    /// <summary>
    /// 对照：既有的 1:1 行动画（<c>REDRAWANIM</c> V1，菜单 [9]）每帧只搬 1 行 ——
    /// 证明上面那条不是把期望值写大、也不是「所有帧都变成 25」。
    /// </summary>
    [Fact]
    public async Task One_to_one_animation_still_carries_one_row()
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);

            SendInput(session, "9");    // → @TEST_REDRAW_V1（PRINT → TWAIT → CLEARLINE 1）
            var turns = await CollectTurnsAsync(session);

            var loopFrames = turns.Select(ParseFrameOps)
                                  .Where(f => f != null && f!.Clear > 0)
                                  .Select(f => f!)
                                  .ToList();

            Assert.NotEmpty(loopFrames);
            Assert.All(loopFrames, f => Assert.Equal(1, f.Clear));
        }
        finally
        {
            registry.DisposeAll();
        }
    }
}
