using System;
using System.Linq;
using System.Threading;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 04（CLI 宿主的让路 drain，spec v5 §5.1b 的 W1-6 / W1-7 / W1-8）的验收闸门。
///
/// <para><b>构造（CLI 侧最高接缝）</b>：<see cref="LoaderTestHarness"/> 起一个最小真实引擎跑真实脚本，
/// 让路点由<b>生产协议类</b> <see cref="AgentCliProtocol"/> 的 <see cref="IInputTimer"/> 实现识别，
/// 宿主循环用<b>生产类</b> <see cref="CliGameLoop"/> 逐帧驱动（与 <c>RunCliLoop</c> 内同一个类、
/// 同一处调用点，仅省掉 VT 屏幕）。</para>
///
/// <para>🔴 <b>这不是「顺手也给 CLI 实现一份」，而是 CLI 的存活条件</b>：<see cref="CliGameLoop"/>
/// 只经输入与超时两条路径推进脚本，其中超时路径在「本次等待没有时限」时直接返回
/// （<c>HandleTimeout</c> 对 <c>Timelimit&lt;=0</c> 早退）。无参 <c>AWAIT</c> 一旦改为挂起，
/// CLI 就没有任何东西能把它续跑 —— 必然死锁。改动前：脚本停在第一个让路点，
/// 驱动 50 帧仍原地不动 ⇒ 必然红（已实测）。</para>
///
/// <para><b>防假绿</b>：核心断言不是「脚本最终能跑完」，而是「<b>每步一帧</b>」——
/// 5 个让路点恰需 5 次宿主帧推进，且每帧都交付了一次 Flush 重绘；这同时排除了
/// 「一帧跑完」（无挂起）与「原地卡死」（无 drain）两种平凡结果。</para>
///
/// <para>⚠️ <b>构造陷阱</b>：<see cref="LoaderTestHarness"/> 构造器同步开 <c>GlobalStatic</c> scope，
/// 而 AsyncLocal 不跨 await 上浮 —— harness 必须在测试方法体内 <c>new</c>，
/// 不能藏在被 <c>await</c> 的 helper 里。</para>
/// </summary>
[Collection("LoaderTests")]
public sealed class CliYieldDrainTests
{
    private const string ErbFileName = "CLIYIELD.ERB";

    /// <summary>让路步数（= 期望的宿主帧数 = 期望的 Flush 次数下界）。</summary>
    private const int Steps = 5;

    /// <summary>驱动上限：超过即视为 drain 未终止（改动前会精确命中这个上限）。</summary>
    private const int FrameBound = 50;

    // ------------------------------------------------------------------
    // CLI 宿主装配：真实 console + 生产 AgentCliProtocol（IInputTimer）+ 生产 CliGameLoop
    // ------------------------------------------------------------------

    /// <summary>把真实 <see cref="EmueraConsole"/> 接成 CLI 主循环要的形状（复刻 <c>CreateGameLoop</c> 的依赖集，
    /// 只把 VT 屏幕/渲染目标换成 recording fake）。</summary>
    private sealed class CliHost
    {
        public readonly IInputTimer Timer;
        public readonly FakeRedrawRenderer Renderer = new();
        public readonly FakeCountdownRenderer Countdown = new();
        public readonly FakeButtonSelection Buttons = new();
        public readonly FakeWindowSize WindowSize = new();
        public readonly FakeTerminalInput TerminalInput = new();
        public readonly CliGameLoop Loop;

        private readonly FakeVtHost _vtHost = new();
        private readonly VtInputHandler _vtInput;
        private readonly ScrollController _scroll = new(1);
        private readonly CliRedrawCoordinator _redraw;

        public CliHost(EmueraConsole console)
        {
            var ui = new HeadlessConsole();
            // 生产协议类：IInputTimer 的 IsYieldPoint / EndYield 是被测对象（转发到引擎，不自建一套）。
            var protocol = new AgentCliProtocol(
                console, ui, new NullTerminalSetup(), new NullTerminalInput(), ConfigData.Current!);
            Timer = protocol;

            _vtInput = new VtInputHandler(_vtHost, TerminalInput);
            _redraw = new CliRedrawCoordinator(Renderer, _scroll, () => null, Countdown, Buttons, () => _vtInput);
            Loop = new CliGameLoop(
                console, Timer, WindowSize, _scroll, null, _redraw, _vtInput, Countdown,
                initialWindowWidth: WindowSize.Width, initialWindowHeight: WindowSize.Height);
        }

        public bool IsYieldPoint => Timer.IsYieldPoint;
    }

    private sealed class FakeRedrawRenderer : IRedrawRenderer
    {
        public int FullRefreshCalls;
        public int FlushBufferCalls;
        public bool FlushResult = true;
        public void FullRefresh(string reason = "?") => FullRefreshCalls++;
        public bool FlushBuffer() { FlushBufferCalls++; return FlushResult; }
    }

    private sealed class FakeCountdownRenderer : ICountdownRenderer
    {
        public int UpdateCalls;
        public int OverwriteCalls;
        public int ResetCalls;
        public void Update(long elapsedMs) => UpdateCalls++;
        public void Overwrite(string newText) => OverwriteCalls++;
        public void Reset() => ResetCalls++;
    }

    private sealed class FakeButtonSelection : IButtonSelection
    {
        public void SyncButtonState() { }
        public void RefreshButtonRegionsFromSnapshot(VtInputHandler vtInput, bool force = false) { }
    }

    private sealed class FakeWindowSize : IWindowSize
    {
        public int Width = 80;
        public int Height = 25;
        int IWindowSize.WindowWidth => Width;
        int IWindowSize.WindowHeight => Height;
    }

    private sealed class FakeTerminalInput : ITerminalInput
    {
        public bool HasInput;
        public int ReadByteCalls;
        public bool HasInputAvailable() => HasInput;
        public int ReadByte() { ReadByteCalls++; return -1; }
        public void EnableSgrMouse() { }
        public void DisableSgrMouse() { }
        public void Dispose() { }
    }

    private sealed class FakeVtHost : IVtHost
    {
        public void RequestExit() { }
        public bool IsWaitingPrimitive => false;
        public int ScrollOffset => 0;
        public void DispatchWheel(int delta) { }
        public void DispatchScroll(ScrollAction action) { }
        public void ProcessKeyFromVt(ConsoleKeyInfo key) { }
        public void DispatchMouseClick(object value, bool isInteger) { }
        public void DispatchMouseMiss() { }
        // ADR-0031 M4/M4b：本 fake 不声明能力位，位置/键上报无消费者。
        public void SubmitPointerGeometry(int row, int col) { }
        public void SubmitPointerKey(int virtualKey, bool down) { }
        public void PressPrimitiveKey(int keycode, int keydata, int keymod) { }
        public void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5) { }
    }

    /// <summary>VT 准备永不成功（本票不跑 <c>RunCliLoop</c> 的 VT 前置，只驱动主循环帧）。</summary>
    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }

    private sealed class NullTerminalInput : ITerminalInput
    {
        public bool HasInputAvailable() => false;
        public int ReadByte() => -1;
        public void EnableSgrMouse() { }
        public void DisableSgrMouse() { }
        public void Dispose() { }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static string ScreenText(EmueraConsole console)
        => string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

    private static string RequestShape(InputRequest? req)
        => req == null
            ? "<null>"
            : $"{{ InputType={req.InputType}, Timelimit={req.Timelimit}, IsYield={req.IsYield} }}";

    private static string Snapshot(EmueraConsole console, int frames)
        => $"驱动 {frames} 帧；State={console.State}；Request={RequestShape(console.CurrentRequest)}；\n画面：\n{ScreenText(console)}";

    /// <summary>脚本：<paramref name="steps"/> 步「打印 → 无参 AWAIT → 擦除」后到达真 WAIT（INPUT），
    /// 再打印输入回执。（脚本里的 <c>{CIDX}</c> / <c>{RESULT}</c> 是 ERB 的变量插值，故用拼接而非内插字符串。）
    /// </summary>
    private static string Erb(int steps) =>
        "@SYSTEM_TITLE\n" +
        "#DIM CIDX\n" +
        "PRINTL CLIB_HEAD\n" +
        "FOR CIDX, 0, " + steps + "\n" +
        "\tPRINTFORML CLIBFRAME-{CIDX}\n" +
        "\tAWAIT\n" +
        "\tCLEARLINE 1\n" +
        "NEXT\n" +
        "PRINTFORML CLIB_ISTIMEOUT={ISTIMEOUT}\n" +
        "PRINTL CLIB_READY\n" +
        "INPUT\n" +
        "PRINTFORML CLIB_INPUT={RESULT}\n" +
        "QUIT\n";

    /// <summary>启动引擎并跑一次 <c>@SYSTEM_TITLE</c>，使脚本停在首个停稳点。</summary>
    private static async System.Threading.Tasks.Task StartAsync(LoaderTestHarness harness)
    {
        await harness.InitializeAsync();
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");
    }

    // ------------------------------------------------------------------
    // T1：drain 存活（CLI）—— 无参 AWAIT 在 CLI 下不停住，每步一帧
    // ------------------------------------------------------------------
    [Fact]
    public async System.Threading.Tasks.Task CLI_drain_advances_yield_script_to_real_wait_one_frame_per_step()
    {
        using var harness = new LoaderTestHarness();
        harness.WriteErb(ErbFileName, Erb(Steps));
        await StartAsync(harness);
        var console = harness.Console;
        var host = new CliHost(console);

        // 起点：停在第一个让路点（而不是跑到底）。
        Assert.True(host.IsYieldPoint,
            "无参 AWAIT 未形成让路点（脚本疑似一口气跑完）。\n" + Snapshot(console, 0));
        Assert.Contains("CLIB_HEAD", ScreenText(console));
        Assert.DoesNotContain("CLIB_READY", ScreenText(console));

        // 逐帧驱动主循环：每个让路点恰好消耗一帧（交付一次重绘 + 一次续跑）。
        // 让终端输入「有货」，以钉住续跑优先于本次输入消费（让路帧不读输入）。
        host.TerminalInput.HasInput = true;
        int frames = 0;
        while (host.IsYieldPoint && frames < FrameBound)
        {
            host.Loop.RunOneFrame(CancellationToken.None);
            frames++;
        }
        Assert.Equal(0, host.TerminalInput.ReadByteCalls);

        Assert.True(frames == Steps,
            $"让路 drain 未按「每步一帧」推进：消耗 {frames} 帧（期望 {Steps}）。" +
            (frames >= FrameBound ? "（疑似停在让路点无人续跑）" : "") + "\n" + Snapshot(console, frames));

        // 画面随让路刷新：每步都交付了一次 Flush 重绘（CLI 的「交付一帧」）。
        Assert.True(host.Renderer.FlushBufferCalls >= Steps,
            $"让路期间 Flush 重绘仅 {host.Renderer.FlushBufferCalls} 次（期望 >= {Steps}）。");

        // 走到真 WAIT，且没吞掉本次等待的输入能力。
        Assert.False(host.IsYieldPoint, "脚本仍停在让路点。\n" + Snapshot(console, frames));
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Contains("CLIB_READY", ScreenText(console));

        // 让路没有超时语义：ISTIMEOUT 仍为 0（误用 EndTimerCore 会让它变 1）。
        Assert.Contains("CLIB_ISTIMEOUT=0", ScreenText(console));
        Assert.False(console._state.isTimeout, "续跑把 isTimeout 置了（疑似复用了 EndTimerCore）。");

        // 真 WAIT 的输入能力未被吞：投输入后脚本继续推进到 QUIT。
        console.RunEmueraProgram("7");

        Assert.Contains("CLIB_INPUT=7", ScreenText(console));
        Assert.Equal(ConsoleState.Quit, console.State);
    }

    // ------------------------------------------------------------------
    // T2：窄接口的两个新成员确实转发到引擎（而非协议层自建一套）
    // ------------------------------------------------------------------
    [Fact]
    public async System.Threading.Tasks.Task CLI_timer_members_forward_to_engine_state()
    {
        using var harness = new LoaderTestHarness();
        harness.WriteErb(ErbFileName, Erb(Steps));
        await StartAsync(harness);
        var console = harness.Console;
        var host = new CliHost(console);

        // IsYieldPoint 读引擎状态：让路点为真。
        Assert.True(host.Timer.IsYieldPoint);
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(0L, console.InputTimelimit);

        // EndYield 转发到 EmueraConsole.EndYield：脚本向前推进一段（离开本让路点）。
        host.Timer.EndYield();

        Assert.Contains("CLIBFRAME-1", ScreenText(console));
        Assert.False(console._state.isTimeout, "EndYield 经超时路径转发（isTimeout 被置）。");
    }

    /// <summary>CLI 无让路回归：不含无参 AWAIT 的脚本，主循环帧不做任何续跑。</summary>
    [Fact]
    public async System.Threading.Tasks.Task CLI_frames_do_not_resume_when_script_has_no_bare_await()
    {
        using var harness = new LoaderTestHarness();
        harness.WriteErb(ErbFileName, """
            @SYSTEM_TITLE
            PRINTL NOYIELD_HEAD
            INPUT
            PRINTFORML NOYIELD_INPUT={RESULT}
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;
        var host = new CliHost(console);

        Assert.False(host.IsYieldPoint, "无让路脚本被误判为让路点。\n" + Snapshot(console, 0));

        for (int i = 0; i < 3; i++)
            host.Loop.RunOneFrame(CancellationToken.None);

        Assert.False(console._state.isTimeout);
        Assert.Contains("NOYIELD_HEAD", ScreenText(console));
        Assert.DoesNotContain("NOYIELD_INPUT", ScreenText(console));
    }
}
