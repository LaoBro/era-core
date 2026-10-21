using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 05（v12 展开：服务端产出 <c>awaiting</c>）的验收闸门 —— spec v5 §1 / §2.2 / §5.4 / §7.4。
///
/// <para><b>接缝（最高：真实脚本 + 真实产出者 + 真实序列化）</b>：<see cref="LoaderTestHarness"/>
/// 起最小真实引擎跑真实 ERB，帧由生产类 <see cref="AgentJsonlProtocol"/> 产出
/// （<c>GetInitialTurnAsync</c> / <c>StepAsync</c> / <c>SubmitTimeoutAsync</c>），
/// 断言全部落在 <b>JSON 形状</b>上（不是 C# 对象内部字段）。</para>
///
/// <para><b>展开阶段</b>：七个旧顶层字段保留（前端保持全绿），本票只要求 <c>awaiting</c> 出现且形状正确。
/// 删除旧字段是 09。</para>
///
/// <para><b>防假绿</b>：核心断言不是「有 awaiting 这个键」，而是<b>形状的判别力</b> ——
/// 零时长让路与定时等待同形只差一个数字（<c>durationMs:0</c> vs <c>>0</c>）、
/// 不可打断的键<b>不出现</b>（而不是 <c>false</c>）、终态只剩 <c>ended</c>。
/// 改动前无 <c>awaiting</c> ⇒ 本文件全红（已实测）。</para>
/// </summary>
[Collection("LoaderTests")]
public sealed class TurnAwaitingV12Tests
{
    private const string ErbFileName = "AWAITING.ERB";

    /// <summary>v12 的协议版本号（spec §1.4）。</summary>
    private const int V12 = 12;

    // ------------------------------------------------------------------
    // 装配
    // ------------------------------------------------------------------

    /// <summary>只记录、不阻塞的 SessionIO（帧产出不依赖读取）。</summary>
    private sealed class RecordingSessionIO : SessionIO
    {
        public readonly List<string> Written = new();
        public override Task<string?> ReadLineAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public override void WriteLine(string text) => Written.Add(text);
        public override void Close() { }
        public override bool IsConnected => true;
    }

    private sealed class Host
    {
        public LoaderTestHarness Loader = null!;
        public RecordingSessionIO Io = null!;
        public DisplayState Display = null!;
        public AgentJsonlProtocol Protocol = null!;
    }

    /// <summary>建 harness 并写脚本（必须在测试方法体内同步调用：AsyncLocal 不跨 await 上浮）。</summary>
    private static LoaderTestHarness NewHarness(string erb)
    {
        var harness = new LoaderTestHarness();
        harness.WriteErb(ErbFileName, erb);
        return harness;
    }

    /// <summary>启动引擎 → 脚本跑到<b>首个停稳点</b> → 装上真实产出者（不改动引擎状态）。</summary>
    private static async Task<Host> StartAsync(LoaderTestHarness harness)
    {
        await harness.InitializeAsync();
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");

        var io = new RecordingSessionIO();
        var display = new DisplayState(harness.Console, "MS Gothic");
        return new Host
        {
            Loader = harness,
            Io = io,
            Display = display,
            Protocol = new AgentJsonlProtocol(harness.Console, new HeadlessConsole(), io, display),
        };
    }

    // ------------------------------------------------------------------
    // JSON 断言工具
    // ------------------------------------------------------------------

    /// <summary>解析帧 JSON。JsonDocument 故意不释放——JsonElement 依赖其生命周期（测试内短命）。</summary>
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static JsonElement Awaiting(JsonElement frame) => frame.GetProperty("awaiting");

    /// <summary>契约不变式（§1.2 规则 1）：<c>ended</c> 与 <c>input</c>/<c>wait</c> 互斥；
    /// 非 ended 时二者至少其一必现（都空 = 没有任何东西能推进这个等待 ⇒ 服务端挂死）。</summary>
    private static void AssertContract(JsonElement frame)
    {
        var a = Awaiting(frame);
        bool hasInput = a.TryGetProperty("input", out _);
        bool hasWait = a.TryGetProperty("wait", out _);
        bool hasEnded = a.TryGetProperty("ended", out var ended);

        Assert.False(hasEnded && (hasInput || hasWait),
            $"ended 与 input/wait 同时出现（互斥性破坏）：{a}");
        Assert.True(hasEnded || hasInput || hasWait,
            $"非终态帧既无 input 又无 wait —— 这个等待没有任何东西能推进它：{a}");
        if (hasEnded)
        {
            Assert.True(ended.TryGetProperty("terminal", out var terminal) && terminal.ValueKind == JsonValueKind.String,
                $"ended 必带 terminal：{a}");
        }
    }

    /// <summary>一帧里「两个维度」的期望形状断言。</summary>
    private static void AssertShape(JsonElement frame,
        string? inputType = null, long? waitDurationMs = null,
        bool? displayTime = null,
        bool? timedOut = null, string? terminal = null)
    {
        AssertContract(frame);
        var a = Awaiting(frame);

        bool hasInput = a.TryGetProperty("input", out var input);
        Assert.Equal(inputType != null, hasInput);
        if (inputType != null)
            Assert.Equal(inputType, input.GetProperty("type").GetString());

        bool hasWait = a.TryGetProperty("wait", out var wait);
        Assert.Equal(waitDurationMs != null, hasWait);
        if (waitDurationMs != null)
            Assert.Equal(waitDurationMs.Value, wait.GetProperty("durationMs").GetInt64());

        // displayTime：仅在 true 时出现（缺省即 false），绝不出现 false。
        AssertFlagKey(wait, hasWait, "displayTime", displayTime);
        AssertFlagKey(wait, hasWait, "timedOut", timedOut);

        bool hasEnded = a.TryGetProperty("ended", out var ended);
        Assert.Equal(terminal != null, hasEnded);
        if (terminal != null)
            Assert.Equal(terminal, ended.GetProperty("terminal").GetString());
    }

    private static void AssertFlagKey(JsonElement wait, bool hasWait, string key, bool? expected)
    {
        if (!hasWait || expected == null)
            return;
        Assert.True(wait.TryGetProperty(key, out var el), $"wait.{key} 缺省（应为 true）：{wait}");
        Assert.Equal(expected.Value, el.GetBoolean());
    }

    /// <summary>
    /// 某个 <c>wait</c> 可选键<b>必须不出现</b>（不是 false）。
    ///
    /// <para>issue 10 后 <c>interruptible</c> 已从契约里彻底撤回 —— 那条「可打断 ⇒ 键出现」
    /// 的判别力也随之消失：现在<b>任何</b>帧上它都必须不出现。</para>
    /// </summary>
    private static void AssertKeyAbsent(JsonElement frame, string key)
    {
        var a = Awaiting(frame);
        Assert.False(a.TryGetProperty("wait", out var wait) && wait.TryGetProperty(key, out _),
            $"wait.{key} 不应出现（不是 false，而是缺省）：{a}");
    }

    // ------------------------------------------------------------------
    // ① 纯输入等待（WAIT）：只有 input，无 wait
    // ------------------------------------------------------------------
    [Fact]
    public async Task Plain_input_wait_has_input_only()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL WAIT_HEAD\n" +
            "WAIT\n" +
            "PRINTL WAIT_AFTER\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frame = Parse((await host.Protocol.GetInitialTurnAsync())!);

        Assert.Equal(V12, frame.GetProperty("protocolVersion").GetInt32());
        AssertShape(frame, inputType: "EnterKey");
    }

    // ------------------------------------------------------------------
    // ② 定时等待：AWAIT t 与 TWAIT t,1 在 wire 上逐字同形（issue 10 撤回）
    // ------------------------------------------------------------------
    [Fact]
    public async Task Timed_await_omits_interruptible_key_entirely()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL TIMED_HEAD\n" +
            "AWAIT 8000\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frame = Parse((await host.Protocol.GetInitialTurnAsync())!);

        AssertShape(frame, waitDurationMs: 8000);
        // 撤回后 AWAIT 与 TWAIT t,1 同待遇：定时等待一律不可打断 ⇒ 该键不出现。
        AssertKeyAbsent(frame, "interruptible");
    }

    [Fact]
    public async Task Timed_twait_omits_interruptible_key_entirely()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL TWAIT_HEAD\n" +
            "TWAIT 8000, 1\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frame = Parse((await host.Protocol.GetInitialTurnAsync())!);

        AssertShape(frame, waitDurationMs: 8000);
        // 不可打断 ⇒ 该键「不出现」，而不是 false。
        AssertKeyAbsent(frame, "interruptible");
    }

    /// <summary>
    /// 同形闸门（issue 10 的核心断言）：<c>AWAIT 8000</c> 与 <c>TWAIT 8000,1</c> 产出的
    /// <c>awaiting</c> <b>逐字相同</b>——定时等待在 wire 上不再有任何「可打断」标记。
    ///
    /// <para>撤回前必然红：<c>AWAIT</c> 会多出 <c>"interruptible":true</c>，
    /// <see cref="JsonElement.DeepEquals"/> 立即判否。</para>
    /// </summary>
    [Fact]
    public async Task Await_and_twait_produce_identical_awaiting()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL TIMED_HEAD\n" +
            "AWAIT 8000\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frame = Awaiting(Parse((await host.Protocol.GetInitialTurnAsync())!));

        // 逐字钉死：就是 TWAIT 8000,1 那一帧的形状，一个字节都不许多。
        // 用同一字面量与 TWAIT 用例对照 ⇒ 「同形」不靠两边各自通过来间接证明。
        Assert.Equal("{\"wait\":{\"durationMs\":8000}}", frame.GetRawText());
    }

    // ------------------------------------------------------------------
    // ③ 零时长让路：wait{durationMs:0}，无 input
    //    —— 与定时等待同形，只差一个数字
    // ------------------------------------------------------------------
    [Fact]
    public async Task Bare_await_is_zero_duration_yield()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL YIELD_HEAD\n" +
            "AWAIT\n" +
            "PRINTL YIELD_AFTER\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frame = Parse((await host.Protocol.GetInitialTurnAsync())!);

        AssertShape(frame, waitDurationMs: 0);
        AssertKeyAbsent(frame, "interruptible");
    }

    [Fact]
    public async Task Await_zero_is_zero_duration_yield()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL ZERO_HEAD\n" +
            "AWAIT 0\n" +
            "PRINTL ZERO_AFTER\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frame = Parse((await host.Protocol.GetInitialTurnAsync())!);

        AssertShape(frame, waitDurationMs: 0);
    }

    // ------------------------------------------------------------------
    // ④ TINPUT：两个维度同时出现；超时续跑时 timedOut 挂在 wait 上
    // ------------------------------------------------------------------
    private static string TinputTwiceErb =>
        "@SYSTEM_TITLE\n" +
        "PRINTL TINPUT_HEAD\n" +
        "TINPUT 3000, 5\n" +
        "PRINTFORML TINPUT_FIRST={RESULT}\n" +
        "TINPUT 3000, 5\n" +
        "PRINTFORML TINPUT_SECOND={RESULT}\n" +
        "QUIT\n";

    [Fact]
    public async Task Tinput_shows_input_and_wait_together()
    {
        using var harness = NewHarness(TinputTwiceErb);
        var host = await StartAsync(harness);

        var frame = Parse((await host.Protocol.GetInitialTurnAsync())!);

        // 既等输入、又有期限 —— v4 里这一组合无法表达。
        AssertShape(frame, inputType: "IntValue", waitDurationMs: 3000, displayTime: true);
        // 尚未超时 ⇒ timedOut 键不出现。
        AssertKeyAbsent(frame, "timedOut");
    }

    [Fact]
    public async Task Timeout_resume_puts_timed_out_on_wait()
    {
        using var harness = NewHarness(TinputTwiceErb);
        var host = await StartAsync(harness);

        AssertShape(Parse((await host.Protocol.GetInitialTurnAsync())!),
            inputType: "IntValue", waitDurationMs: 3000);

        // 真实超时续跑：EndTimerCore 用默认值推进脚本 → 停在下一个 TINPUT 上。
        var frame = Parse((await host.Protocol.SubmitTimeoutAsync())!);

        AssertShape(frame, inputType: "IntValue", waitDurationMs: 3000, timedOut: true);
        // v12（收缩完成）：顶层 timedOut 已退场——该旗标只挂在 awaiting.wait 上。
        Assert.False(frame.TryGetProperty("timedOut", out _));
    }

    // ------------------------------------------------------------------
    // ⑤ 终结帧：只有 ended，input / wait 均不出现
    // ------------------------------------------------------------------
    [Fact]
    public async Task Quit_frame_has_ended_only()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL QUIT_HEAD\n" +
            "WAIT\n" +
            "PRINTL QUIT_AFTER\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        AssertShape(Parse((await host.Protocol.GetInitialTurnAsync())!), inputType: "EnterKey");

        // 输入驱动到 QUIT（GameExitException 路径）。
        var frame = Parse((await host.Protocol.StepAsync(""))!);

        AssertShape(frame, terminal: "Quit");
    }

    // ------------------------------------------------------------------
    // ⑥ 首帧：两个维度照常判定 + protocolVersion 标识版本（没有「init 吞掉首帧语义」）
    // ------------------------------------------------------------------
    [Fact]
    public async Task First_turn_carries_both_dimension_and_version()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL FIRST_HEAD\n" +
            "AWAIT 8000\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frame = Parse((await host.Protocol.GetInitialTurnAsync())!);

        Assert.Equal(V12, frame.GetProperty("protocolVersion").GetInt32());
        AssertShape(frame, waitDurationMs: 8000);
    }

    [Fact]
    public async Task Non_initial_turn_omits_protocol_version()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL NONINIT_HEAD\n" +
            "WAIT\n" +
            "WAIT\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        await host.Protocol.GetInitialTurnAsync();
        var frame = Parse((await host.Protocol.StepAsync(""))!);

        Assert.False(frame.TryGetProperty("protocolVersion", out _),
            "非首帧不得携带 protocolVersion（WhenWritingNull）");
        AssertShape(frame, inputType: "EnterKey");
    }

    // ------------------------------------------------------------------
    // ⑦ 互斥性：整条链上每一帧都满足契约不变式（加断言级验收）
    // ------------------------------------------------------------------
    [Fact]
    public async Task Every_frame_on_a_chain_satisfies_mutual_exclusion()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL CHAIN_HEAD\n" +
            "WAIT\n" +
            "PRINTL CHAIN_1\n" +
            "AWAIT 5000\n" +
            "PRINTL CHAIN_2\n" +
            "TINPUT 5000, 5\n" +
            "PRINTL CHAIN_3\n" +
            "INPUT\n" +
            "PRINTL CHAIN_4\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var frames = new List<string> { (await host.Protocol.GetInitialTurnAsync())! };
        frames.Add((await host.Protocol.StepAsync(""))!);      // WAIT → AWAIT 5000
        frames.Add((await host.Protocol.SubmitTimeoutAsync())!); // AWAIT 5000 超时 → TINPUT
        frames.Add((await host.Protocol.StepAsync("3"))!);     // TINPUT → INPUT
        frames.Add((await host.Protocol.StepAsync("9"))!);     // INPUT → QUIT

        foreach (var json in frames)
        {
            var frame = Parse(json);
            Assert.Equal(JsonValueKind.Object, Awaiting(frame).ValueKind);
            AssertContract(frame);
        }
        // 终帧确实是 ended{Quit}
        AssertShape(Parse(frames[^1]), terminal: "Quit");
    }

    // ------------------------------------------------------------------
    // ⑧ GET /snapshot 与流上帧同源（针对 spec §5.2 第 4 项）
    // ------------------------------------------------------------------
    [Fact]
    public async Task Snapshot_awaiting_matches_stream_shape()
    {
        using var harness = NewHarness(TinputTwiceErb);
        var host = await StartAsync(harness);

        var streamFrame = Parse((await host.Protocol.GetInitialTurnAsync())!);
        var snapshot = Parse(host.Display.GetSnapshotJson());

        var streamAwaiting = Awaiting(streamFrame);
        var snapshotAwaiting = Awaiting(snapshot);

        Assert.True(JsonElement.DeepEquals(streamAwaiting, snapshotAwaiting),
            $"快照与流上帧的 awaiting 形状不一致。\n流：{streamAwaiting}\n快照：{snapshotAwaiting}");
        AssertShape(snapshot, inputType: "IntValue", waitDurationMs: 3000, displayTime: true);
    }

    // ------------------------------------------------------------------
    // ⑨ 输入被拒：帧必须真的发出，error 非空，awaiting 与上一帧相同
    // ------------------------------------------------------------------
    [Fact]
    public async Task Rejected_input_keeps_awaiting_and_reports_error()
    {
        using var harness = NewHarness(
            "@SYSTEM_TITLE\n" +
            "PRINTL REJECT_HEAD\n" +
            "INPUT\n" +
            "PRINTFORML REJECT_RESULT={RESULT}\n" +
            "QUIT\n");
        var host = await StartAsync(harness);

        var before = Parse((await host.Protocol.GetInitialTurnAsync())!);
        AssertShape(before, inputType: "IntValue");

        // 等整数时投非整数 ⇒ 被拒：没有新停稳点，但必须有一帧回音。
        var rejected = Parse((await host.Protocol.StepAsync("abc"))!);

        Assert.True(rejected.TryGetProperty("error", out var err) && !string.IsNullOrEmpty(err.GetString()),
            "被拒帧必须携带非空 error");
        Assert.True(JsonElement.DeepEquals(Awaiting(before), Awaiting(rejected)),
            $"被拒帧的 awaiting 必须与上一帧完全相同。\n前：{Awaiting(before)}\n后：{Awaiting(rejected)}");
        Assert.Equal(before.GetProperty("generation").GetInt64(), rejected.GetProperty("generation").GetInt64());
    }
}
