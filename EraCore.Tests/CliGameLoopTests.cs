using System;
using System.Threading;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal;
using MinorShift.Emuera.Terminal.Platform;
using Xunit;

namespace Emuera.Headless.Tests;

public class CliGameLoopTests
{
    private sealed class FakeConsoleStateView : IConsoleStateView
    {
        public bool NeedFullRefresh;
        public bool IsGameExitedValue;
        public int DisplayLineCountValue;
        bool IConsoleStateView.IsGameExited => IsGameExitedValue;
        int IConsoleStateView.DisplayLineCount => DisplayLineCountValue;
        public bool ConsumeNeedFullRefresh()
        {
            bool v = NeedFullRefresh;
            NeedFullRefresh = false;
            return v;
        }
    }

    private sealed class FakeInputTimer : IInputTimer
    {
        public long InputTimelimitValue;
        public bool IsWaitingInputValue;
        public string? TimeUpMessageValue;
        /// <summary>让路点（W1-6）：CLI 主循环据此在超时之后做同步 drain。</summary>
        public bool IsYieldPointValue;
        public int SubmitTimeoutCalls;
        public int ClearInputBufferCalls;
        public int EndYieldCalls;
        long IInputTimer.InputTimelimit => InputTimelimitValue;
        bool IInputTimer.IsWaitingInput => IsWaitingInputValue;
        string? IInputTimer.TimeUpMessage => TimeUpMessageValue;
        bool IInputTimer.IsYieldPoint => IsYieldPointValue;
        public void SubmitTimeout() => SubmitTimeoutCalls++;
        public void ClearInputBuffer() => ClearInputBufferCalls++;
        public void EndYield() => EndYieldCalls++;
    }

    private sealed class FakeWindowSize : IWindowSize
    {
        public int Width = 80;
        public int Height = 25;
        int IWindowSize.WindowWidth => Width;
        int IWindowSize.WindowHeight => Height;
    }

    private sealed class FakeRedrawRenderer : IRedrawRenderer
    {
        public int FullRefreshCalls;
        public string? LastReason;
        public int FlushBufferCalls;
        public bool FlushResult = true;
        public void FullRefresh(string reason = "?") { FullRefreshCalls++; LastReason = reason; }
        public bool FlushBuffer() { FlushBufferCalls++; return FlushResult; }
    }

    private sealed class FakeCountdownRenderer : ICountdownRenderer
    {
        public int UpdateCalls;
        public long LastElapsedMs;
        public int OverwriteCalls;
        public string? LastOverwriteText;
        public int ResetCalls;
        public void Update(long elapsedMs) { UpdateCalls++; LastElapsedMs = elapsedMs; }
        public void Overwrite(string text) { OverwriteCalls++; LastOverwriteText = text; }
        public void Reset() => ResetCalls++;
    }

    private sealed class FakeButtonSelection : IButtonSelection
    {
        public int SyncCalls;
        public int RefreshCalls;
        public void SyncButtonState() => SyncCalls++;
        public void RefreshButtonRegionsFromSnapshot(VtInputHandler vtInput, bool force = false) => RefreshCalls++;
    }

    private sealed class FakeTerminalInput : ITerminalInput
    {
        public int ReadByteResult = -1;
        public bool HasInput;
        public int ReadByteCalls;
        public bool HasInputAvailable() => HasInput;
        public int ReadByte() { ReadByteCalls++; return ReadByteResult; }
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
        // ADR-0031 M4：本 fake 不声明 Geometry 能力，位置上报无消费者。
        public void SubmitPointerGeometry(int row, int col) { }
        public void PressPrimitiveKey(int keycode, int keydata, int keymod) { }
        public void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5) { }
    }

    private sealed class FakeClock
    {
        public DateTime Base = DateTime.UtcNow;
        private int _callCount;
        public DateTime Provider()
        {
            _callCount++;
            return _callCount == 1 ? Base : Base.AddMilliseconds(200);
        }
    }

    private sealed class Harness
    {
        public readonly FakeConsoleStateView ExecState = new();
        public readonly FakeInputTimer InputTimer = new();
        public readonly FakeWindowSize WindowSize = new();
        public readonly FakeRedrawRenderer Renderer = new();
        public readonly FakeCountdownRenderer Countdown = new();
        public readonly FakeButtonSelection Buttons = new();
        public readonly FakeTerminalInput TerminalInput = new();
        public readonly FakeVtHost VtHost = new();
        public readonly FakeClock Clock = new();
        public readonly ScrollController Scroll = new(1);
        public readonly VtInputHandler VtInput;
        public readonly CliRedrawCoordinator RedrawCoordinator;
        public readonly CliGameLoop GameLoop;
        public readonly CancellationTokenSource Cts = new();

        public Harness()
        {
            VtInput = new VtInputHandler(VtHost, TerminalInput);
            RedrawCoordinator = new CliRedrawCoordinator(
                Renderer, Scroll, () => null, Countdown, Buttons, () => VtInput);
            GameLoop = new CliGameLoop(
                ExecState, InputTimer, WindowSize, Scroll, null,
                RedrawCoordinator, VtInput, Countdown,
                initialWindowWidth: 80, initialWindowHeight: 25,
                nowProvider: Clock.Provider);
        }
    }

    [Fact]
    public void FullRefresh_resets_scroll_and_triggers_FullRefresh_redraw()
    {
        var h = new Harness();
        h.ExecState.NeedFullRefresh = true;

        var state = h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(RunState.Running, state);
        Assert.Equal(1, h.Renderer.FullRefreshCalls);
        Assert.Equal("coordinator", h.Renderer.LastReason);
        Assert.Equal(0, h.Renderer.FlushBufferCalls);
    }

    [Fact]
    public void FullRefresh_resets_scroll_when_active()
    {
        var h = new Harness();
        h.Scroll.ScrollBy(5, 100);
        h.ExecState.NeedFullRefresh = true;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Scroll.ScrollOffset);
    }

    [Fact]
    public void FullRefresh_clears_waitInputEnteredAt()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 5000;
        h.ExecState.NeedFullRefresh = true;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.OverwriteCalls);
        Assert.Equal(0, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void FullRefresh_skips_input_and_countdown()
    {
        var h = new Harness();
        h.TerminalInput.HasInput = true;
        h.ExecState.NeedFullRefresh = true;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.UpdateCalls);
    }

    [Fact]
    public void Timeout_submits_timeup_message_and_clears_buffer()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 100;
        h.InputTimer.TimeUpMessageValue = "time up!";

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Countdown.OverwriteCalls);
        Assert.Equal("time up!", h.Countdown.LastOverwriteText);
        Assert.Equal(1, h.Countdown.ResetCalls);
        Assert.Equal(1, h.InputTimer.ClearInputBufferCalls);
        Assert.Equal(1, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void Timeout_redraws_on_flush()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 100;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Renderer.FlushBufferCalls);
    }

    [Fact]
    public void Time_not_yet_expired_does_not_trigger_timeout()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 1_000_000;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.OverwriteCalls);
        Assert.Equal(0, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void Not_waiting_input_clears_waitInputEnteredAt()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = false;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Countdown.UpdateCalls);
        Assert.Equal(0, h.Countdown.OverwriteCalls);
    }

    [Fact]
    public void Input_available_reads_byte_and_feeds()
    {
        var h = new Harness();
        h.TerminalInput.HasInput = true;
        h.TerminalInput.ReadByteResult = 0x61;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Countdown.UpdateCalls);
    }

    [Fact]
    public void No_input_updates_countdown()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 5000;
        h.TerminalInput.HasInput = false;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Countdown.UpdateCalls);
    }

    [Fact]
    public void Resize_detected_triggers_FullRefresh_when_offset_unchanged()
    {
        var h = new Harness();
        h.WindowSize.Width = 120;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.Renderer.FullRefreshCalls);
        Assert.Equal("coordinator", h.Renderer.LastReason);
    }

    [Fact]
    public void Resize_updates_visible_lines_and_clamps_offset()
    {
        var h = new Harness();
        h.Scroll.ScrollBy(60, 100);
        h.WindowSize.Height = 40;
        h.ExecState.DisplayLineCountValue = 100;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(60, h.Scroll.ScrollOffset);
    }

    [Fact]
    public void Resize_does_not_extra_redraw_when_offset_clamped()
    {
        var h = new Harness();
        h.Scroll.ScrollBy(60, 100);
        h.WindowSize.Height = 40;
        h.ExecState.DisplayLineCountValue = 10;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.Scroll.ScrollOffset);
    }

    [Fact]
    public void Resize_detected_clears_waitInputEnteredAt()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 1_000_000;
        h.WindowSize.Width = 120;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.InputTimer.SubmitTimeoutCalls);
    }

    [Fact]
    public void GameExited_returns_GameExited()
    {
        var h = new Harness();
        h.ExecState.IsGameExitedValue = true;

        var state = h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(RunState.GameExited, state);
    }

    [Fact]
    public void RunLoop_returns_when_GameExited()
    {
        var h = new Harness();
        h.ExecState.IsGameExitedValue = true;

        h.GameLoop.RunLoop(h.Cts.Token);

        Assert.Equal(1, h.Renderer.FlushBufferCalls);
    }

    [Fact]
    public void RunLoop_stops_on_cancellation()
    {
        var h = new Harness();
        h.Cts.Cancel();

        h.GameLoop.RunLoop(h.Cts.Token);

        Assert.Equal(0, h.Renderer.FlushBufferCalls);
    }

    // ===============================================================
    // 让路点 drain（spec v5 §5.1b W1-8 / §3.2 ④ CLI 段）
    //
    // CLI 的「交付一帧」= 一次 Flush 重绘，续跑经 IInputTimer.EndYield 转发到引擎。
    // 位置固定在 HandleTimeout 之后、读取终端输入之前 —— 缺了它无参 AWAIT 后 CLI 停住。
    // ===============================================================

    /// <summary>让路点 → 交付一帧（Flush）并续跑，且不沾染任何超时/倒计时语义。</summary>
    [Fact]
    public void Yield_point_flushes_a_frame_and_resumes()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.IsYieldPointValue = true;
        h.InputTimer.InputTimelimitValue = 0;

        var state = h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(RunState.Running, state);
        Assert.Equal(1, h.InputTimer.EndYieldCalls);
        Assert.Equal(1, h.Renderer.FlushBufferCalls);
        // 让路不走超时路径：不提交超时、不推超时文案、不清输入缓冲（§3.2 ③）。
        Assert.Equal(0, h.InputTimer.SubmitTimeoutCalls);
        Assert.Equal(0, h.InputTimer.ClearInputBufferCalls);
        Assert.Equal(0, h.Countdown.OverwriteCalls);
        // 让路没有倒计时语义 ⇒ 不动等待起始时间，倒计时既不更新也不重置。
        Assert.Equal(0, h.Countdown.UpdateCalls);
        Assert.Equal(0, h.Countdown.ResetCalls);
    }

    /// <summary>续跑优先于本次输入消费：让路帧不读终端输入（drain 在读取之前）。</summary>
    [Fact]
    public void Yield_point_resumes_before_reading_terminal_input()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.IsYieldPointValue = true;
        h.TerminalInput.HasInput = true;
        h.TerminalInput.ReadByteResult = 0x61;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.InputTimer.EndYieldCalls);
        Assert.Equal(0, h.TerminalInput.ReadByteCalls);
    }

    /// <summary>超时处理在 drain 之前：两者同时成立时走超时路径（位置保持为「超时之后」）。</summary>
    [Fact]
    public void Timeout_takes_precedence_over_yield_point()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        // 合成状态：只为钉住编排顺序（真实请求里 Timelimit&gt;0 不可能是让路点）。
        h.InputTimer.InputTimelimitValue = 100;
        h.InputTimer.IsYieldPointValue = true;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(1, h.InputTimer.SubmitTimeoutCalls);
        Assert.Equal(0, h.InputTimer.EndYieldCalls);
    }

    /// <summary>画面无变化（Flush 返回 false）时也必须续跑 —— 否则「只 AWAIT、无打印」的
    /// 让路循环会把 CLI 永久卡死（续跑不得挂在重绘成功上）。</summary>
    [Fact]
    public void Yield_point_resumes_even_when_frame_has_no_change()
    {
        var h = new Harness();
        h.Renderer.FlushResult = false;
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.IsYieldPointValue = true;

        var state = h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(RunState.Running, state);
        Assert.Equal(1, h.InputTimer.EndYieldCalls);
    }

    /// <summary>CLI 无让路回归：非让路帧一次续跑都不发生。</summary>
    [Fact]
    public void Non_yield_frame_never_resumes()
    {
        var h = new Harness();
        h.InputTimer.IsWaitingInputValue = true;
        h.InputTimer.InputTimelimitValue = 5000;

        h.GameLoop.RunOneFrame(h.Cts.Token);

        Assert.Equal(0, h.InputTimer.EndYieldCalls);
    }
}
