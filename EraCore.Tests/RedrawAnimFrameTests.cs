using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;
using Xunit.Abstractions;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 回合内动画帧的交付基线（spec v3 §10-A 的实测结论，固化为断言）。
///
/// <para><b>起因</b>：v2 spec 曾把「<c>REDRAW 0</c> 会不会吞掉动画中间帧」列为阻塞项。
/// 四个变体的实测（V0 基线 / V1 净值 / V2 <c>REDRAW 0</c> / V3 不抵消）全部
/// 10 步 → 11 帧，marker 首现帧号严格 <c>0,1,…,9</c>，V1 与 V2 <b>逐帧相同</b>
/// ⇒ 该阻塞项是误判，已撤销。本文件把这几条结论钉住防回归。</para>
///
/// <para><b>防假绿</b>：核心断言不是「最终能收到帧」（那在中间态整块出现时也成立），
/// 而是<b>各中间帧落在不同的 turn 里且序号递增</b> —— 与
/// <see cref="ScriptAwaitFrameTests"/> 同一手法。</para>
///
/// <para><b>V2 的回归防护价值</b>：V2 与 V1 断言完全相同的期望，逐字相同。
/// 一旦有人把 <c>ComputeDiff</c> 改成经 <c>ConsoleRefreshHandler</c>
/// （受 <c>redraw == None</c> 早退门影响），V2 会红而 V1 保持绿 —— 差异即定位。</para>
///
/// <para>构造：<c>test_game/erb/REDRAWANIM.ERB</c>，菜单 <c>[8]</c>–<c>[12]</c>。
/// V4 为基线测量，标 <c>Trait("Category","Probe")</c>，常规套件用
/// <c>--filter "Category!=Probe"</c> 排除。</para>
/// </summary>
[Collection("ServerState")]
public class RedrawAnimFrameTests
{
    private readonly ITestOutputHelper _output;

    public RedrawAnimFrameTests(ITestOutputHelper output) => _output = output;

    /// <summary>变体的循环步数（与 ERB 里的 10 次循环对应）。</summary>
    private const int Steps = 10;

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

    private static async Task<List<string>> CollectTurnsAsync(
        Session session, int maxTurns = MaxTurns, TimeSpan? window = null)
    {
        var turns = new List<string>();
        var deadline = DateTime.UtcNow.Add(window ?? CollectWindow);
        while (turns.Count < maxTurns && DateTime.UtcNow < deadline)
        {
            var result = await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);
            if (result.Status == TurnWaitStatus.Turn && result.Turn != null)
            {
                turns.Add(result.Turn);
                if (TurnTerminal(result.Turn) == "Quit")
                    break;
                continue;
            }
            if (result.Status is TurnWaitStatus.Closed or TurnWaitStatus.ControlLost)
                break;
        }
        return turns;
    }

    private static string TurnField(string turnJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(turnJson);
            if (doc.RootElement.TryGetProperty(name, out var v))
                return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
        }
        catch { /* 摘要用，不因解析失败中断 */ }
        return "";
    }

    /// <summary>
    /// v12：帧不再带顶层 <c>state</c>——从契约 <c>awaiting.ended.terminal</c> 取终态值
    /// （非终态帧返回空串）。
    /// </summary>
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

    /// <summary>v12：把 <c>awaiting</c> 压成一行可读摘要（替代旧 state/inputType 两列）。</summary>
    private static string AwaitingShape(string turnJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(turnJson);
            if (!doc.RootElement.TryGetProperty("awaiting", out var a))
                return "";
            if (a.TryGetProperty("ended", out var ended))
                return "ended:" + ended.GetProperty("terminal").GetString();
            var s = a.TryGetProperty("input", out var input)
                ? "input:" + input.GetProperty("type").GetString()
                : "";
            if (a.TryGetProperty("wait", out var wait))
                s += (s.Length > 0 ? "+" : "") + "wait:" + wait.GetProperty("durationMs").GetInt64();
            return s.Length > 0 ? s : "{}";
        }
        catch { return ""; }
    }

    /// <summary>只取 lineOps 的形状（什么操作、几行），不取整段 JSON。</summary>
    private static string OpsShape(string turnJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(turnJson);
            if (!doc.RootElement.TryGetProperty("diff", out var diff)
                || diff.ValueKind != JsonValueKind.Object
                || !diff.TryGetProperty("lineOps", out var lineOps)
                || lineOps.ValueKind != JsonValueKind.Array)
                return "no-diff";

            var parts = new List<string>();
            foreach (var op in lineOps.EnumerateArray())
            {
                var t = op.TryGetProperty("type", out var tv) ? tv.GetString() : "?";
                string n = t switch
                {
                    "append" => op.TryGetProperty("newLines", out var nl) ? $"x{nl.GetArrayLength()}" : "x?",
                    "clear_line_diff" => op.TryGetProperty("clearCount", out var cc) ? $"-{cc}" : "-?",
                    "shift_head" => op.TryGetProperty("count", out var sh) ? $"^{sh}" : "^?",
                    "clear_screen" => "",
                    _ => "?",
                };
                parts.Add($"{t}{n}");
            }
            return string.Join(",", parts);
        }
        catch { return "?"; }
    }

    /// <summary>
    /// 核心断言：每个中间态都落在<b>不同</b>的 turn 里且序号递增。
    /// 只断言「最终收到帧」在中间态整块出现时也成立（假绿），故必须逐 marker 比对。
    /// </summary>
    private static void AssertEveryStepVisible(
        List<string> turns, string markerPrefix, string doneMarker, string label)
    {
        Assert.NotEmpty(turns);

        int prev = -1;
        for (int i = 0; i < Steps; i++)
        {
            var marker = $"{markerPrefix}-{i}";
            int idx = turns.FindIndex(t => t.Contains(marker, StringComparison.Ordinal));

            Assert.True(idx >= 0,
                $"{label}：未观察到 {marker} —— 该中间态从未被交付。\n{Describe(turns)}");
            Assert.True(idx > prev,
                $"{label}：{marker} 未落在上一帧之后（前一个首现={prev}，本帧首现={idx}）" +
                $" —— 中间态没有逐帧交付，画面是整块出现的。\n{Describe(turns)}");
            prev = idx;
        }

        // 至少每步一帧（不锁死精确帧数——空转帧抑制的合法演进不应让它变红）
        Assert.True(turns.Count >= Steps,
            $"{label}：帧数 {turns.Count} 少于循环步数 {Steps} —— 有中间态被吞。\n{Describe(turns)}");

        Assert.Contains(turns, t => t.Contains(doneMarker, StringComparison.Ordinal));
        Assert.Equal("Quit", TurnTerminal(turns[^1]));
    }

    private static async Task RunVariantAsync(
        string menuValue, string label, string markerPrefix, string doneMarker)
    {
        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);

            SendInput(session, menuValue);
            var turns = await CollectTurnsAsync(session);

            AssertEveryStepVisible(turns, markerPrefix, doneMarker, label);
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    // ---------------- 基线断言（进常规套件） ----------------

    /// <summary>V0 基线：<c>TWAIT</c> 推帧本身正常（无 <c>CLEARLINE</c>，画面单调增长）。</summary>
    [Fact]
    public async Task V0_baseline_delivers_one_frame_per_step()
        => await RunVariantAsync("8", "V0 基线", "BASE", "V0_DONE");

    /// <summary>
    /// V1：eraTW <c>KEYTYPING</c> 形状（<c>PRINT → TWAIT 1,1 → CLEARLINE</c>），
    /// 画面净值回到原状。实测 10 步 → 11 帧，中间态未丢、未被时序意外合并。
    /// </summary>
    [Fact]
    public async Task V1_keytyping_shape_delivers_every_intermediate_frame()
        => await RunVariantAsync("9", "V1 净值（KEYTYPING 形状）", "STEP", "V1_DONE");

    /// <summary>
    /// V2：与 V1 完全相同的循环外包 <c>REDRAW 0</c>。<b>断言与 V1 逐字相同</b> ——
    /// 这是「<c>REDRAW 0</c> 不影响帧交付」这条结论的回归防护（见类注释）。
    /// </summary>
    [Fact]
    public async Task V2_redraw_zero_does_not_suppress_frames()
        => await RunVariantAsync("10", "V2（REDRAW 0）", "STEP", "V2_DONE");

    /// <summary>
    /// V3：与 V1 同形但每步额外留一行永久递增计数 —— 画面净值不可能为空。
    /// 用于区分「产出丢帧」与「抑制丢帧」，两者都成立时它应保持绿。
    /// </summary>
    [Fact]
    public async Task V3_non_cancelling_counter_delivers_every_frame()
        => await RunVariantAsync("11", "V3 不抵消", "TICK", "V3_DONE");

    // ---------------- 基线测量（不进常规套件） ----------------

    /// <summary>
    /// V4 真实长度：100 步 / 末行 200 字，写法照抄 eraTW <c>KEYTYPING</c>。
    ///
    /// <para>目的不是数帧，而是<b>量字节与计时</b> —— 给 spec v3 §4.5 提供
    /// 「每帧常量 214B 占 68%、单步 11.5ms」这两个锚点。第二阶段启用帧合并时，
    /// 用同一构造复测即得 before/after 对照。</para>
    ///
    /// <para>标 <c>Trait("Category","Probe")</c>：跑一次要真实启引擎约 1.2s，
    /// 且它主要是测量而非回归防护。常规跑法：<c>dotnet test --filter "Category!=Probe"</c>。</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Probe")]
    public async Task V4_realistic_length_measures_bytes()
    {
        const int steps = 100;
        const int finalLen = 200;

        var registry = CreateRegistry();
        try
        {
            var session = await StartAtTitleAsync(registry);
            await session.WaitForTurnAsync(TurnPollMs, CancellationToken.None);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            SendInput(session, "12");
            var turns = await CollectTurnsAsync(
                session, maxTurns: steps + 20, window: TimeSpan.FromSeconds(30));
            sw.Stop();

            Assert.NotEmpty(turns);

            long total = 0;
            var sizes = new List<int>();
            foreach (var t in turns)
            {
                int b = Encoding.UTF8.GetByteCount(t);
                sizes.Add(b);
                total += b;
            }

            // 唯一的行为断言：真实规模下中间态同样不丢（比 10 步版本更有说服力）
            Assert.True(turns.Count >= steps,
                $"V4：帧数 {turns.Count} 少于循环步数 {steps}。\n{Describe(turns)}");

            _output.WriteLine($"=== V4 真实长度：KEYTYPING 逐字打印（{steps} 步，末行 {finalLen} 字）===");
            _output.WriteLine($"产出帧数: {turns.Count}");
            _output.WriteLine($"全程耗时: {sw.Elapsed.TotalMilliseconds:F0} ms  " +
                $"=> 单步约 {sw.Elapsed.TotalMilliseconds / turns.Count:F1} ms");
            _output.WriteLine($"总字节数: {total.ToString("N0", CultureInfo.InvariantCulture)}");
            if (sizes.Count > 0)
            {
                long avg = total / sizes.Count;
                _output.WriteLine(
                    $"每帧字节: 首={sizes[0]} 中位={sizes[sizes.Count / 2]} 末={sizes[^1]} 平均={avg}");
                var sample = new List<string>();
                for (int i = 0; i < sizes.Count; i += 10)
                    sample.Add($"#{i}:{sizes[i]}[{OpsShape(turns[i])}]");
                _output.WriteLine("帧大小采样: " + string.Join(" ", sample));
            }
        }
        finally
        {
            registry.DisposeAll();
        }
    }

    /// <summary>把收集到的帧摘要成可读文本（断言失败时定位用）。</summary>
    private static string Describe(List<string> turns)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < turns.Count; i++)
        {
            sb.Append($"  #{i:D2} awaiting={AwaitingShape(turns[i]),-24}" +
                      $" ops=[{OpsShape(turns[i])}]\n");
        }
        return sb.ToString();
    }
}
