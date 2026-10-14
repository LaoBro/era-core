using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// VtInputHandler 路由单元测试（ADR-0009 C4/C7b）。
/// 用 FakeVtHost 隔离 VtInputHandler，Feed SGR mouse / 键盘热键字节，
/// 断言 host 收到预期的 DispatchWheel/DispatchScroll/ProcessKeyFromVt 调用。
/// 不依赖真实 EmueraConsole / TerminalCursor / VT I/O。
/// </summary>
public class VtInputHandlerTests
{
    // ---------- ADR-0031 M4：鼠标位置上报（Geometry 能力的入口） ----------

    [Fact]
    public void M4_mouse_event_reports_pointer_geometry()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnMouseEvent(row: 5, col: 3, buttonCode: 0, isPress: true);

        Assert.Equal(1, host.SubmitPointerGeometryCalls);
        Assert.Equal(5, host.LastPointerRow);
        Assert.Equal(3, host.LastPointerCol);
    }

    /// <summary>
    /// 位置上报必须覆盖**所有**鼠标事件，包括被路由丢弃的那些：松开（isPress=false）、
    /// 非左键、滚轮。它们的共同点是「鼠标确实在那儿」，只是不该提交值 ——
    /// 漏掉任何一种都会让 <c>MOUSEX/MOUSEY</c> 失真。
    /// </summary>
    [Fact]
    public void M4_pointer_geometry_is_reported_even_when_click_is_dropped()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnMouseEvent(row: 1, col: 2, buttonCode: 0, isPress: false);   // 松开
        handler.OnMouseEvent(row: 3, col: 4, buttonCode: 1, isPress: true);    // 非左键
        handler.OnMouseEvent(row: 5, col: 6, buttonCode: 64, isPress: true);   // 滚轮

        Assert.Equal(3, host.SubmitPointerGeometryCalls);
        Assert.Equal(5, host.LastPointerRow);
        Assert.Equal(6, host.LastPointerCol);
        Assert.Equal(0, host.DispatchMouseClickCalls);
    }

    /// <summary>primitive 等待态下脚本同样能读 <c>MOUSEX/MOUSEY</c> ⇒ 上报必须在 primitive 分支<b>之前</b>。</summary>
    [Fact]
    public void M4_primitive_mouse_event_reports_geometry()
    {
        var host = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnMouseEvent(row: 7, col: 8, buttonCode: 0, isPress: true);

        Assert.Equal(1, host.SubmitPointerGeometryCalls);
        Assert.Equal(7, host.LastPointerRow);
        Assert.Equal(8, host.LastPointerCol);
    }

    // ---------- ADR-0031 / spec-v3 M4b：鼠标键电平上报（KeyboardState 能力的入口） ----------

    /// <summary>
    /// SGR <c>cb</c> → Win32 VK：0=左(<c>0x01</c>) / 1=中(<c>0x04</c>) / 2=右(<c>0x02</c>)，
    /// 与原版 WinForms <c>MouseButtons</c> 口径一致（spec §9 缺口 4）。
    /// </summary>
    [Theory]
    [InlineData(0, 0x01)]
    [InlineData(1, 0x04)]
    [InlineData(2, 0x02)]
    public void M4b_mouse_button_is_reported_as_win32_vk(int buttonCode, int expectedVk)
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnMouseEvent(row: 0, col: 0, buttonCode: buttonCode, isPress: true);
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: buttonCode, isPress: false);

        Assert.Equal([(expectedVk, true), (expectedVk, false)], host.SubmitPointerKeyCalls);
    }

    /// <summary>带修饰位的 <c>cb</c>（如 Ctrl+左键 = 16）仍取低两位 ⇒ 映射到左键。</summary>
    [Fact]
    public void M4b_modifier_bits_do_not_break_button_mapping()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 4 | 0, isPress: true);   // Shift+左键
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 16 | 2, isPress: true); // Ctrl+右键

        Assert.Equal([(0x01, true), (0x02, true)], host.SubmitPointerKeyCalls);
    }

    /// <summary>
    /// 滚轮（<c>cb=64/65</c>）与 X10 的「释放」标记（<c>cb&amp;3==3</c>）**不**合成键 ——
    /// 滚轮有独立路径，若不排除会被 <c>&amp;3==0</c> 误判成左键电平。
    /// </summary>
    [Fact]
    public void M4b_wheel_and_release_marker_do_not_produce_keys()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 64, isPress: true);
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 65, isPress: true);
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 64 | 4, isPress: true);  // Shift+滚轮
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 3, isPress: false);

        Assert.Empty(host.SubmitPointerKeyCalls);
    }

    /// <summary>
    /// 键电平必须覆盖被路由丢弃的四类事件，且发生在 primitive / Scroll 门卫**之前**：
    /// 原版任何时刻都能读到真实键状态，而 <c>GETKEYTRIGGERED</c> 要求「此刻按着」——
    /// 只在「会被提交」的路径上报会让按下跨越门卫时丢失触发。
    /// </summary>
    [Fact]
    public void M4b_key_state_is_reported_before_gates()
    {
        var primitive = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink primitiveHandler = new VtInputHandler(primitive, new FakeTerminalInput());
        primitiveHandler.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: true);
        Assert.Equal([(0x01, true)], primitive.SubmitPointerKeyCalls);

        var scrolled = new FakeVtHost(scrollOffset: 3);
        IVtEventSink scrolledHandler = new VtInputHandler(scrolled, new FakeTerminalInput());
        scrolledHandler.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: true);
        scrolledHandler.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: false);
        Assert.Equal([(0x01, true), (0x01, false)], scrolled.SubmitPointerKeyCalls);
    }

    // ---------- C4：Scroll Mode 只读门卫 ----------

    [Fact]
    public void C4_scroll_mode_key_is_dropped()
    {
        var host = new FakeVtHost(scrollOffset: 5);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // Scroll Mode（offset>0）下普通键应被丢弃
        handler.OnKeyEvent(ConsoleKey.A, 'a');

        Assert.Equal(0, host.ProcessKeyFromVtCalls);
        Assert.Equal(0, host.DispatchMouseClickCalls);
    }

    [Fact]
    public void C4_scroll_mode_scroll_hotkeys_still_route()
    {
        var host = new FakeVtHost(scrollOffset: 5);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 滚动热键在 Scroll Mode 下仍路由
        handler.OnKeyEvent(ConsoleKey.PageUp, '\0');
        handler.OnKeyEvent(ConsoleKey.PageDown, '\0');
        handler.OnKeyEvent(ConsoleKey.Home, '\0');
        handler.OnKeyEvent(ConsoleKey.End, '\0');

        Assert.Equal(4, host.DispatchScrollCalls.Count);
        Assert.Equal(ScrollAction.PageUp, host.DispatchScrollCalls[0]);
        Assert.Equal(ScrollAction.PageDown, host.DispatchScrollCalls[1]);
        Assert.Equal(ScrollAction.Home, host.DispatchScrollCalls[2]);
        Assert.Equal(ScrollAction.End, host.DispatchScrollCalls[3]);
    }

    [Fact]
    public void C4_normal_mode_key_routes_to_ProcessKeyFromVt()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.A, 'a');

        Assert.Equal(1, host.ProcessKeyFromVtCalls);
        Assert.Equal('a', host.LastKey.KeyChar);
        Assert.Equal(ConsoleKey.A, host.LastKey.Key);
    }

    [Fact]
    public void C4_scroll_mode_mouse_click_ignored()
    {
        var host = new FakeVtHost(scrollOffset: 5);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // Scroll Mode 下鼠标左键点击应被忽略
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: true);

        Assert.Equal(0, host.DispatchMouseClickCalls);
        Assert.Equal(0, host.DispatchMouseMissCalls);
    }

    // ---------- C7b：滚轮事件路由 ----------

    [Fact]
    public void C7b_wheel_up_routes_to_DispatchWheel_positive_delta()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // cb=64 → 滚轮上 → delta=+3
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 64, isPress: false);

        Assert.Equal(3, Assert.Single(host.DispatchWheelCalls));
    }

    [Fact]
    public void C7b_wheel_down_routes_to_DispatchWheel_negative_delta()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // cb=65 → 滚轮下 → delta=-3
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 65, isPress: false);

        Assert.Equal(-3, Assert.Single(host.DispatchWheelCalls));
    }

    [Fact]
    public void C7b_wheel_routes_even_in_scroll_mode()
    {
        var host = new FakeVtHost(scrollOffset: 10);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 滚轮事件在 Scroll Mode 下仍路由（cb=64/65 始终处理）
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 64, isPress: false);
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 65, isPress: false);

        Assert.Equal(2, host.DispatchWheelCalls.Count);
        Assert.Equal(3, host.DispatchWheelCalls[0]);
        Assert.Equal(-3, host.DispatchWheelCalls[1]);
    }

    [Fact]
    public void C7b_wheel_ignores_isPress_flag()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 滚轮事件不依赖 isPress（SGR mouse 滚轮无 press/release 语义）
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 64, isPress: false);
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 64, isPress: true);

        Assert.Equal(2, host.DispatchWheelCalls.Count);
    }

    // ---------- Ctrl+C 退出 ----------

    [Fact]
    public void Ctrl_C_requests_exit()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.C, '\x03');

        Assert.Equal(1, host.RequestExitCalls);
        Assert.Equal(0, host.ProcessKeyFromVtCalls);
    }

    // ---------- 键盘热键在正常模式下的行为 ----------

    [Fact]
    public void PageUp_in_normal_mode_routes_to_DispatchScroll()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.PageUp, '\0');

        Assert.Equal(ScrollAction.PageUp, Assert.Single(host.DispatchScrollCalls));
        Assert.Equal(0, host.ProcessKeyFromVtCalls);
    }

    [Fact]
    public void Enter_key_routes_to_ProcessKeyFromVt()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.Enter, '\r');

        Assert.Equal(1, host.ProcessKeyFromVtCalls);
        Assert.Equal(ConsoleKey.Enter, host.LastKey.Key);
    }

    // ---------- C5：DispatchMouseClick 分发（正常模式，按钮命中） ----------

    [Fact]
    public void C5_mouse_click_hitting_button_routes_to_DispatchMouseClick()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());
        IVtEventSink sink = handler;

        // 注册按钮 "[OK]" 在 row=5, col=[0,3]
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        handler.Tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 0);

        // 点击 col=0（在 [0,3] 区域内）
        sink.OnMouseEvent(row: 5, col: 0, buttonCode: 0, isPress: true);

        Assert.Equal(1, host.DispatchMouseClickCalls);
        Assert.Equal(0, host.DispatchMouseMissCalls);
        // Phase 3-3：value-based dispatch，1L 装箱 + IsInteger=true
        Assert.Equal(1L, host.LastClickValue);
        Assert.True(host.LastClickIsInteger);
    }

    [Fact]
    public void C5_mouse_click_missing_button_routes_to_DispatchMouseMiss()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());
        IVtEventSink sink = handler;

        // 注册按钮 "[OK]" 在 row=5, col=[0,3]
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        handler.Tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 0);

        // 点击 col=5（在 [0,3] 区域外）
        sink.OnMouseEvent(row: 5, col: 5, buttonCode: 0, isPress: true);

        Assert.Equal(0, host.DispatchMouseClickCalls);
        Assert.Equal(1, host.DispatchMouseMissCalls);
    }

    [Fact]
    public void C5_mouse_click_on_wrong_row_misses()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());
        IVtEventSink sink = handler;

        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        handler.Tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 0);

        // 点击 row=4（按钮在 row=5）
        sink.OnMouseEvent(row: 4, col: 0, buttonCode: 0, isPress: true);

        Assert.Equal(0, host.DispatchMouseClickCalls);
        Assert.Equal(1, host.DispatchMouseMissCalls);
    }

    [Fact]
    public void C5_multiple_buttons_hit_correct_one()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());
        IVtEventSink sink = handler;

        // "[OK]" col=[0,3], "[Cancel]" col=[4,11]
        var buttons = TestButtonFactory.CreateButtons(("[OK]", 1), ("[Cancel]", 2));
        handler.Tracker.RecordLineRegions("X", bufferRow: 0, buttons, currentGeneration: 0);

        // 点击 col=7（在 "[Cancel]" 区域内）
        sink.OnMouseEvent(row: 0, col: 7, buttonCode: 0, isPress: true);

        Assert.Equal(1, host.DispatchMouseClickCalls);
        // Phase 3-3：value-based dispatch，"[Cancel]" 按钮 input=2
        Assert.Equal(2L, host.LastClickValue);
        Assert.True(host.LastClickIsInteger);
    }

    [Fact]
    public void C5_mouse_release_does_not_dispatch_click()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        var handler = new VtInputHandler(host, new FakeTerminalInput());
        IVtEventSink sink = handler;

        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        handler.Tracker.RecordLineRegions("X", bufferRow: 0, buttons, currentGeneration: 0);

        // 释放事件（isPress=false）即使命中按钮也不应触发 DispatchMouseClick
        sink.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: false);

        Assert.Equal(0, host.DispatchMouseClickCalls);
        Assert.Equal(0, host.DispatchMouseMissCalls);
    }

    // ---------- C6：DispatchMouseMiss 分发（正常模式，无按钮命中） ----------

    [Fact]
    public void C6_mouse_click_miss_routes_to_DispatchMouseMiss()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 无按钮注册时，任何点击都应未命中
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: true);

        Assert.Equal(1, host.DispatchMouseMissCalls);
        Assert.Equal(0, host.DispatchMouseClickCalls);
    }

    [Fact]
    public void C6_mouse_release_does_not_dispatch()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 释放事件（isPress=false）不应触发 DispatchMouseClick 或 DispatchMouseMiss
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 0, isPress: false);

        Assert.Equal(0, host.DispatchMouseMissCalls);
        Assert.Equal(0, host.DispatchMouseClickCalls);
    }

    [Fact]
    public void C6_non_left_click_ignored()
    {
        var host = new FakeVtHost(scrollOffset: 0);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // buttonCode=1（右键）/2（中键）在非 primitive 模式下不处理
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 1, isPress: true);
        handler.OnMouseEvent(row: 0, col: 0, buttonCode: 2, isPress: true);

        Assert.Equal(0, host.DispatchMouseMissCalls);
        Assert.Equal(0, host.DispatchMouseClickCalls);
    }

    // ---------- C8：primitive 模式鼠标/键盘 ----------

    [Fact]
    public void C8_primitive_key_routes_to_PressPrimitiveKey()
    {
        var host = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        handler.OnKeyEvent(ConsoleKey.A, 'a');

        var keyCall = Assert.Single(host.PressPrimitiveKeyCalls);
        Assert.Equal((int)ConsoleKey.A, keyCall.keycode);
        Assert.Equal((int)'a', keyCall.keydata);
        Assert.Equal(0, keyCall.keymod);
        // 不应进入 ProcessKeyFromVt
        Assert.Equal(0, host.ProcessKeyFromVtCalls);
    }

    [Fact]
    public void C8_primitive_ctrl_c_still_requests_exit()
    {
        var host = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // Ctrl+C 在 primitive 模式下仍应触发 RequestExit（ch=='\x03' 提前返回）
        handler.OnKeyEvent(ConsoleKey.C, '\x03');

        Assert.Equal(1, host.RequestExitCalls);
        Assert.Empty(host.PressPrimitiveKeyCalls);
    }

    [Fact]
    public void C8_primitive_mouse_click_routes_to_InputMouseKey()
    {
        var host = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 左键按下 → InputMouseKey(type=1, windowsButton=0x100000, col, row, 0, 0)
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 0, isPress: true);

        var call = Assert.Single(host.InputMouseKeyCalls);
        Assert.Equal(1, call.type);
        Assert.Equal(0x100000, call.result1);
        Assert.Equal(10, call.result2); // col
        Assert.Equal(5, call.result3);  // row
    }

    [Fact]
    public void C8_primitive_mouse_release_ignored()
    {
        var host = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 释放事件（isPress=false）不应触发 InputMouseKey
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 0, isPress: false);

        Assert.Empty(host.InputMouseKeyCalls);
    }

    [Fact]
    public void C8_primitive_wheel_routes_to_InputMouseKey_type2()
    {
        var host = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // cb=64 滚轮上 → delta=-120（primitive 路径的 delta 与正常路径相反）
        handler.OnMouseEvent(row: 5, col: 10, buttonCode: 64, isPress: false);

        var call = Assert.Single(host.InputMouseKeyCalls);
        Assert.Equal(2, call.type);
        Assert.Equal(-120, call.result1);
    }

    [Fact]
    public void C8_primitive_right_click_routes_to_InputMouseKey()
    {
        var host = new FakeVtHost(scrollOffset: 0, isWaitingPrimitive: true);
        IVtEventSink handler = new VtInputHandler(host, new FakeTerminalInput());

        // 右键（buttonCode=1）→ windowsButton=0x400000
        handler.OnMouseEvent(row: 3, col: 7, buttonCode: 1, isPress: true);

        var call = Assert.Single(host.InputMouseKeyCalls);
        Assert.Equal(1, call.type);
        Assert.Equal(0x400000, call.result1);
    }

    // ---------- Fakes ----------

    private sealed class FakeVtHost : IVtHost
    {
        private readonly int _scrollOffset;
        private readonly bool _isWaitingPrimitive;

        public FakeVtHost(int scrollOffset = 0, bool isWaitingPrimitive = false)
        {
            _scrollOffset = scrollOffset;
            _isWaitingPrimitive = isWaitingPrimitive;
        }

        public int RequestExitCalls;
        public int ProcessKeyFromVtCalls;
        public int DispatchMouseClickCalls;
        public int DispatchMouseMissCalls;
        public object? LastClickValue;
        public bool LastClickIsInteger;
        public ConsoleKeyInfo LastKey;
        public readonly List<int> DispatchWheelCalls = new();
        public readonly List<ScrollAction> DispatchScrollCalls = new();
        public readonly List<(int keycode, int keydata, int keymod)> PressPrimitiveKeyCalls = new();
        public readonly List<(int type, int result1, int result2, int result3, int result4, long result5)> InputMouseKeyCalls = new();

        public bool IsWaitingPrimitive => _isWaitingPrimitive;

        public int ScrollOffset => _scrollOffset;

        public void RequestExit() => RequestExitCalls++;

        public void DispatchWheel(int delta) => DispatchWheelCalls.Add(delta);

        public void DispatchScroll(ScrollAction action) => DispatchScrollCalls.Add(action);

        public void ProcessKeyFromVt(ConsoleKeyInfo key)
        {
            ProcessKeyFromVtCalls++;
            LastKey = key;
        }

        public void DispatchMouseClick(object value, bool isInteger)
        {
            DispatchMouseClickCalls++;
            LastClickValue = value;
            LastClickIsInteger = isInteger;
        }

        public void DispatchMouseMiss() => DispatchMouseMissCalls++;

        // ADR-0031 / spec-v3 M4：鼠标位置上报。记录调用次数与最后一次坐标 ——
        // 用于断言「每个鼠标事件（含 primitive / 滚轮 / Scroll Mode 下被丢弃的）都先更新位置」。
        public int SubmitPointerGeometryCalls;
        public int LastPointerRow = -1;
        public int LastPointerCol = -1;

        public void SubmitPointerGeometry(int row, int col)
        {
            SubmitPointerGeometryCalls++;
            LastPointerRow = row;
            LastPointerCol = col;
        }

        // ADR-0031 / spec-v3 M4b：鼠标键电平上报。记录全部 (vk, down) 对 ——
        // 用于断言映射正确、且上报发生在 primitive / Scroll 门卫**之前**。
        public readonly List<(int virtualKey, bool down)> SubmitPointerKeyCalls = new();

        public void SubmitPointerKey(int virtualKey, bool down)
            => SubmitPointerKeyCalls.Add((virtualKey, down));

        public void PressPrimitiveKey(int keycode, int keydata, int keymod)
            => PressPrimitiveKeyCalls.Add((keycode, keydata, keymod));

        public void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5)
            => InputMouseKeyCalls.Add((type, result1, result2, result3, result4, result5));
    }

    private sealed class FakeTerminalInput : ITerminalInput
    {
        public bool HasInputAvailable() => false;
        public int ReadByte() => -1;
        public void EnableSgrMouse() { }
        public void DisableSgrMouse() { }
        public void Dispose() { }
    }
}
