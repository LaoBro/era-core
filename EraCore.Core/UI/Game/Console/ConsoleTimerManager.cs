using System;
using System.Diagnostics;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// TINPUT timer management: presetTimer, setTimer, tickTimer, stopTimer, endTimer,
/// SubmitTimeout, InputTimeoutMs, and related countdown display helpers.
/// </summary>
internal sealed class ConsoleTimerManager
{
    private readonly ConsoleStateData _state;
    private readonly EmueraConsole _console;
    private readonly IConsoleUI _ui;

    internal ConsoleTimerManager(ConsoleStateData state, EmueraConsole console, IConsoleUI ui)
    {
        _state = state;
        _console = console;
        _ui = ui;

        _state.genericTimer.Elapsed += TickTimer;
        _state.redrawTimer.Elapsed += TickRedrawTimer;
    }

    internal void PresetTimer()
    {
        _state.need_settimer = true;
        if (_state.inputReq!.DisplayTime)
        {
            var remainingMs = _state.inputReq.Timelimit - _state._genericTimerStopwatch.ElapsedMilliseconds;
            _console.PrintSingleLine(trsl.Remaining.Text + $"{remainingMs / 1000.0f:0.0}");
            _state.timeDisplayCount = 0;
            _state.inputed = false;
        }
    }

    internal void SetTimer()
    {
        _state.isTimeout = false;
        _state.timerID = _state.inputReq!.ID;
        _state._genericTimerStopwatch.Restart();
        _state.timer_endTime = _state.inputReq.Timelimit;
        _state.need_settimer = false;
    }

    private void TickTimer(object? sender, EventArgs e)
    {
        if (!_state.genericTimer.Enabled)
            return;
        if (_state.State != ConsoleState.WaitInput || _state.inputReq!.Timelimit <= 0 || _state.timerID != _state.inputReq!.ID)
        {
            StopTimer();
            return;
        }
        var elapsedMs = _state._genericTimerStopwatch.ElapsedMilliseconds;
        if (elapsedMs >= _state.timer_endTime)
        {
            EndTimer();
            return;
        }

        if (_state.inputReq!.DisplayTime)
        {
            var remainingMs = _state.inputReq!.Timelimit - _state._genericTimerStopwatch.ElapsedMilliseconds;
            _state.timeDisplayCount++;
            if (_state.timeDisplayCount % 10 == 0 && !_state.inputed)
                _ui.Invoke(() => _console.ChangeLastLine(trsl.Remaining.Text + $"{remainingMs / 1000.0f:0.0}"));
        }
    }

    private void TickRedrawTimer(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!_state.redrawTimer.Enabled)
            return;
        if (_state.State != ConsoleState.WaitInput || _state.genericTimer.Enabled)
            return;
        _ui.Refresh();
    }

    internal void StopTimer()
    {
        _state.genericTimer.Enabled = false;
    }

    internal void SetRedrawTimer(int tickcount)
    {
        if (tickcount <= 0)
        {
            _state.redrawTimer.Enabled = false;
            return;
        }
        if (tickcount < 10)
            tickcount = 10;
        _state.redrawTimer.Interval = tickcount;
        _state.redrawTimer.Enabled = true;
    }

    /// <summary>当前重绘定时器周期；未启用返回 0（对齐 Skia 的 <c>EmueraConsole.AnimeTimer</c>，票 28）。</summary>
    internal int GetRedrawTimer()
        => _state.redrawTimer.Enabled ? (int)_state.redrawTimer.Interval : 0;

    internal void ForceStopTimer()
    {
        if (_state.genericTimer.Enabled)
            _state.genericTimer.Enabled = false;
    }

    private void EndTimer()
    {
        EndTimerCore();
    }

    internal void EndTimerCore()
    {
        StopTimer();
        _state.isTimeout = true;
        if (_console.IsWaitingPrimitive)
        {
            _console.InputMouseKey(4, 0, 0, 0, 0, 0);
            _console.RefreshStrings(true);
            return;
        }
        if (_state.inputReq!.DisplayTime)
            _console.ChangeLastLine(_state.inputReq!.TimeUpMes);
        else if (_state.inputReq!.TimeUpMes != null)
            _console.PrintSingleLine(_state.inputReq!.TimeUpMes);

        _console.RunEmueraProgram("");
        _console.RefreshStrings(true);
    }

    internal void SubmitTimeout()
    {
        if (_state.State != ConsoleState.WaitInput || _state.inputReq == null || _state.inputReq.Timelimit <= 0)
            return;
        EndTimerCore();
    }

    internal long? InputTimeoutMs
    {
        get
        {
            if (_state.State != ConsoleState.WaitInput || _state.inputReq == null || _state.inputReq.Timelimit <= 0)
                return null;
            var remaining = _state.inputReq.Timelimit - _state._genericTimerStopwatch.ElapsedMilliseconds;
            return remaining <= 0 ? 0 : remaining;
        }
    }

    /// <summary>当前 WaitInput 的超时阈值（毫秒）。0 表示无超时。ADR-0007：CLI 模式读取此值配合 WaitInputEnteredAt 挂钟计时。</summary>
    internal long InputTimelimit =>
        (_state.State == ConsoleState.WaitInput && _state.inputReq != null) ? _state.inputReq.Timelimit : 0;

    internal bool IsDisplayTimeActive =>
        _state.State == ConsoleState.WaitInput && _state.inputReq != null && _state.inputReq.DisplayTime && _state.inputReq.Timelimit > 0;

    internal string? TimeUpMessage => _state.inputReq?.TimeUpMes;

    /// <summary>基于外部传入的 elapsed 计算倒计时文本。ADR-0007：CLI 模式用挂钟 elapsed，绕过 stopwatch。</summary>
    internal string BuildCountdownText(long elapsedMs)
    {
        if (_state.inputReq == null) return "";
        long remainingMs = _state.inputReq.Timelimit - elapsedMs;
        return FormatRemaining(remainingMs);
    }

    /// <summary>格式化剩余毫秒为倒计时文本，负值钳零。</summary>
    private static string FormatRemaining(long remainingMs)
    {
        if (remainingMs < 0) remainingMs = 0;
        return trsl.Remaining.Text + $"{remainingMs / 1000.0f:0.0}";
    }
}