using System;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

internal sealed class VtInputHandler : IDisposable, IVtEventSink
{
    private readonly IVtHost _host;
    private readonly ITerminalInput _input;
    private readonly ButtonRegionTracker _tracker = new();
    private readonly VtParser _parser;
    private bool _disposed;

    internal VtInputHandler(IVtHost host, ITerminalInput input)
    {
        _host = host;
        _input = input;
        _parser = new VtParser(this);
    }

    internal bool HasInputAvailable() => _input.HasInputAvailable();

    internal int ReadByte() => _input.ReadByte();

    internal void Feed(byte b) => _parser.Feed(b);

    internal void EnableSgrMouse() => _input.EnableSgrMouse();

    internal void DisableSgrMouse() => _input.DisableSgrMouse();

    #region Button regions

    internal void ClearRegions() => _tracker.Clear();

    /// <summary>
    /// Phase 3-3b：暴露 tracker 供 ButtonSelectionMode.RefreshButtonRegionsFromSnapshot
    /// 直接调 UpdateFromSnapshot（单次调用，替代旧 RecordLineRegions 按行循环）。
    /// </summary>
    internal ButtonRegionTracker Tracker => _tracker;

    #endregion

    #region Event dispatch (called by VtParser via IVtEventSink)

    void IVtEventSink.OnKeyEvent(ConsoleKey key, char ch)
    {
        if (ch == '\x03')
        {
            _host.RequestExit();
            return;
        }

        if (key == 0)
        {
            if (ch == '\r') key = ConsoleKey.Enter;
            else if (ch == '\b' || ch == '\x7F') key = ConsoleKey.Backspace;
            else if (ch == '\x1b') key = ConsoleKey.Escape;
        }

        if (_host.IsWaitingPrimitive)
        {
            int keycode = (int)key;
            int keydata = (int)ch;
            _host.PressPrimitiveKey(keycode, keydata, 0);
            return;
        }

        // ADR-0006：滚动热键（PgUp/PgDn/Home/End）始终路由到 DispatchScroll，不进入普通输入路径。
        // offset=0 时 PgUp/Home 进入 Scroll Mode；End/PgDn 在 offset=0 时为 no-op
        // （ScrollTo(0)/ScrollBy(-n) 钳到 0 → newOffset==oldOffset → DispatchScroll 提前返回）。
        switch (key)
        {
            case ConsoleKey.PageUp:
                _host.DispatchScroll(ScrollAction.PageUp);
                return;
            case ConsoleKey.PageDown:
                _host.DispatchScroll(ScrollAction.PageDown);
                return;
            case ConsoleKey.Home:
                _host.DispatchScroll(ScrollAction.Home);
                return;
            case ConsoleKey.End:
                _host.DispatchScroll(ScrollAction.End);
                return;
        }

        // ADR-0006：Scroll Mode 只读门卫。offset>0 时丢弃所有非滚动键，
        // 避免污染输入缓冲区（不进入 ProcessKey / ButtonSelectionMode.HandleKey）。
        if (_host.ScrollOffset > 0) return;

        var keyInfo = new ConsoleKeyInfo(ch, key, false, false, false);
        _host.ProcessKeyFromVt(keyInfo);
    }

    void IVtEventSink.OnMouseEvent(int row, int col, int buttonCode, bool isPress)
    {
        // ADR-0031 / spec-v3 M4：先更新鼠标位置（Geometry 能力）—— 与下述命中/提交路径正交。
        // 必须在 primitive 分支**之前**：primitive 等待态下脚本同样能读 MOUSEX/MOUSEY。
        _host.SubmitPointerGeometry(row, col);

        if (_host.IsWaitingPrimitive)
        {
            DispatchPrimitiveMouseKey(row, col, buttonCode, isPress);
            return;
        }

        // ADR-0006：滚轮事件（SGR mouse cb=64/65）始终处理，不依赖 isPress。
        // cb=64 → 滚轮上（delta=+3，回看历史）；cb=65 → 滚轮下（delta=-3，回底）。
        if (buttonCode == 64 || buttonCode == 65)
        {
            int delta = buttonCode == 64 ? 3 : -3;
            _host.DispatchWheel(delta);
            return;
        }

        // Scroll Mode 下忽略鼠标左键点击（cb=0）等所有其他鼠标事件，避免误触旧 Generation 按钮。
        if (_host.ScrollOffset > 0) return;

        if (!isPress || buttonCode != 0) return;

        // Phase 3-3：HitTest 返回 value-based Region?，从中取 Value/IsInteger 调 dispatch。
        var hit = _tracker.HitTest(row, col);
        if (hit.HasValue)
            _host.DispatchMouseClick(hit.Value.Value, hit.Value.IsInteger);
        else
            _host.DispatchMouseMiss();
    }

    private void DispatchPrimitiveMouseKey(int row, int col, int buttonCode, bool isPress)
    {
        if (buttonCode == 64 || buttonCode == 65)
        {
            int delta = buttonCode == 64 ? -120 : 120;
            _host.InputMouseKey(2, delta, col, row, 0, 0);
            return;
        }

        if (!isPress) return;

        int windowsButton = buttonCode switch
        {
            0 => 0x100000,
            1 => 0x400000,
            2 => 0x200000,
            _ => 0
        };

        if (windowsButton == 0) return;

        _host.InputMouseKey(1, windowsButton, col, row, 0, 0);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input.Dispose();
        ClearRegions();
    }
}
