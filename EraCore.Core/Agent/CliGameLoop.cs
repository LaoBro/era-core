using System;
using System.Threading;
using MinorShift.Emuera.Terminal;

namespace MinorShift.Emuera.GameView;

internal enum RunState
{
    Running,
    GameExited,
}

internal sealed class CliGameLoop
{
    private const int PollIntervalMs = 50;

    private readonly IConsoleStateView _execState;
    private readonly IInputTimer _inputTimer;
    private readonly IWindowSize _windowSize;
    private readonly ScrollController _scroll;
    private readonly ScrollStatusBarRenderer? _scrollStatusBar;
    private readonly CliRedrawCoordinator _redraw;
    private readonly VtInputHandler _vtInput;
    private readonly ICountdownRenderer _countdown;
    private readonly Func<DateTime> _now;

    private DateTime? _waitInputEnteredAt;
    private int _lastWindowWidth = -1;
    private int _lastWindowHeight = -1;

    internal CliGameLoop(
        IConsoleStateView execState,
        IInputTimer inputTimer,
        IWindowSize windowSize,
        ScrollController scroll,
        ScrollStatusBarRenderer? scrollStatusBar,
        CliRedrawCoordinator redraw,
        VtInputHandler vtInput,
        ICountdownRenderer countdown,
        int initialWindowWidth = -1,
        int initialWindowHeight = -1,
        Func<DateTime>? nowProvider = null)
    {
        _execState = execState;
        _inputTimer = inputTimer;
        _windowSize = windowSize;
        _scroll = scroll;
        _scrollStatusBar = scrollStatusBar;
        _redraw = redraw;
        _vtInput = vtInput;
        _countdown = countdown;
        _now = nowProvider ?? (() => DateTime.UtcNow);
        _lastWindowWidth = initialWindowWidth;
        _lastWindowHeight = initialWindowHeight;
    }

    internal void RunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (RunOneFrame(token) == RunState.GameExited)
                break;
        }
    }

    internal RunState RunOneFrame(CancellationToken token)
    {
        if (_execState.ConsumeNeedFullRefresh())
        {
            ResetScrollIfActive();
            _waitInputEnteredAt = null;
            _redraw.Redraw(RedrawKind.FullRefresh, true);
            return RunState.Running;
        }

        if (!_inputTimer.IsWaitingInput)
        {
            _waitInputEnteredAt = null;
        }
        else if (!_waitInputEnteredAt.HasValue && _inputTimer.InputTimelimit > 0)
        {
            _waitInputEnteredAt = _now();
        }

        if (HandleTimeout())
            return RunState.Running;

        // spec v5 §5.1b W1-8 / §3.2 ④（CLI 段）：让路点同步 drain。
        //
        // 🔴 这是 <b>CLI 的存活条件</b>，不是「顺手也实现一下」：本类只经输入与超时两条路径推进
        // 脚本，而超时路径在上面的 HandleTimeout 里对 Timelimit<=0 直接早退。无参 AWAIT 改为
        // 挂起后，CLI 再无任何续跑入口 —— 必然死锁，而 server 路径全绿也发现不了。
        //
        // 位置固定：超时处理之后（真定时等待仍由超时路径负责）、读取终端输入之前
        // （续跑优先于本次输入消费）。CLI 的「交付一帧」是一次 Flush 重绘，不是写一行 IO。
        // 不动 _waitInputEnteredAt：让路没有倒计时语义（此处的 Timelimit<=0 本就不会武装起始时间）。
        if (_inputTimer.IsYieldPoint)
        {
            _redraw.Redraw(RedrawKind.Flush, false);
            _inputTimer.EndYield();   // 转发到引擎 EmueraConsole.EndYield：不置 isTimeout、不打印 TimeUpMes
            return RunState.Running;
        }

        if (_vtInput.HasInputAvailable())
        {
            int b = _vtInput.ReadByte();
            if (b >= 0)
            {
                _vtInput.Feed((byte)b);
            }
        }
        else
        {
            long elapsedMs = _waitInputEnteredAt.HasValue
                ? (long)(_now() - _waitInputEnteredAt.Value).TotalMilliseconds
                : 0;
            _countdown.Update(elapsedMs);
            token.WaitHandle.WaitOne(PollIntervalMs);
        }

        if (CheckResize())
        {
            _scroll.UpdateVisibleLines(Math.Max(1, _windowSize.WindowHeight - 2));
            int oldOffset = _scroll.ScrollOffset;
            _scroll.Clamp(_execState.DisplayLineCount);
            _waitInputEnteredAt = null;
            if (oldOffset == _scroll.ScrollOffset)
            {
                _redraw.Redraw(RedrawKind.FullRefresh, true);
            }
        }

        if (!_redraw.PumpPendingScrollRedraw())
            _redraw.Redraw(RedrawKind.Flush, false);

        return _execState.IsGameExited ? RunState.GameExited : RunState.Running;
    }

    private bool HandleTimeout()
    {
        if (!_waitInputEnteredAt.HasValue) return false;
        long timelimit = _inputTimer.InputTimelimit;
        if (timelimit <= 0) return false;
        var elapsed = (long)(_now() - _waitInputEnteredAt.Value).TotalMilliseconds;
        if (elapsed < timelimit) return false;

        _countdown.Overwrite(_inputTimer.TimeUpMessage ?? "");
        _countdown.Reset();
        _inputTimer.ClearInputBuffer();
        _inputTimer.SubmitTimeout();
        _waitInputEnteredAt = null;
        _redraw.Redraw(RedrawKind.Flush, false);
        return true;
    }

    private void ResetScrollIfActive()
    {
        if (_scroll.ScrollOffset > 0)
        {
            _scrollStatusBar?.ClearStatusBar();
            _scroll.Reset();
        }
    }

    private bool CheckResize()
    {
        int w = _windowSize.WindowWidth;
        int h = _windowSize.WindowHeight;
        if (w == _lastWindowWidth && h == _lastWindowHeight) return false;
        _lastWindowWidth = w;
        _lastWindowHeight = h;
        return true;
    }
}
