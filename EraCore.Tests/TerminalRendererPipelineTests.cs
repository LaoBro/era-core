using System;
using System.Collections.Generic;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// TerminalRenderer 快照渲染回归（issue-004 起经 <see cref="OutputPort.Snapshot"/> 取画面）。
///
/// <para>原先注入 <c>IDisplayState</c> 假实现来喂合成快照；该假接缝已随 issue-004 删除
/// （单实现接口 = 假接缝）。现在用<b>真实</b> <c>EmueraConsole</c> + <c>DisplayState</c>：
/// 行的增删/替换就是引擎真实产生的画面变化，快照由同一套 <c>BuildSnapshot</c> 产出。</para>
/// </summary>
public class TerminalRendererPipelineTests
{
    private sealed class FakeButtonContext : IButtonDisplayContext
    {
        public ConsoleButtonString? SelectingButton => null;
        public TerminalCharWidthConfig CharWidthConfig => TerminalCharWidthConfig.Default;
    }

    /// <summary>真实引擎装配：console → DisplayState → OutputPort（CLI 的快照路径）。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;

        internal EmueraConsole Console { get; }
        internal OutputPort Output { get; }

        internal Harness()
        {
            _scope = GlobalStatic.OpenScope(new ConfigData());
            Console = new EmueraConsole(new HeadlessConsole(), new NullTerminalSetup());
            Output = new OutputPort(new DisplayState(Console, "MS Gothic"));
        }

        internal void Add(ConsoleDisplayLine line) => Console.DisplayLineList.Add(line);

        internal void RemoveAt(int index) => Console.DisplayLineList.RemoveAt(index);

        internal void Clear() => Console.DisplayLineList.Clear();

        public void Dispose() => _scope.Dispose();
    }

    private static ConsoleDisplayLine MakeSource(int lineNo, bool lineEnd = true)
    {
        var line = new ConsoleDisplayLine(
            Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false, lineEnd: lineEnd);
        line.LineNo = lineNo;
        return line;
    }

    private static TerminalRenderer NewRenderer(Harness harness, BufferedVtScreen screen, ScrollController? scroll = null) =>
        new(new FakeButtonContext(), scroll ?? new ScrollController(1), screen, harness.Output);

    private static string CaptureOutput(Action action, BufferedVtScreen screen)
    {
        screen.Clear();
        action();
        return screen.GetOutput();
    }

    [Fact]
    public void FullRefresh_empty_produces_only_clearscreen()
    {
        using var harness = new Harness();
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        string output = CaptureOutput(() => renderer.FullRefresh("test"), screen);

        Assert.Contains("\x1b[2J\x1b[H", output);
        Assert.Equal(-1, renderer.LastDrawnRows);
    }

    [Fact]
    public void FullRefresh_with_content_writes_all_lines()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        harness.Add(MakeSource(2));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        string output = CaptureOutput(() => renderer.FullRefresh("test"), screen);

        Assert.Contains("\x1b[2J\x1b[H", output);
        // 贴底（2026.10.14 spec §3.1）：H=25 ⇒ visibleLines=24，3 行内容锚末行 23（0-based），
        // 写屏行 21/22/23（1-based 22/23/24），光标恒置预留行 24（1-based 25）。
        Assert.Contains("\x1b[22;1H", output);
        Assert.Contains("\x1b[23;1H", output);
        Assert.Contains("\x1b[24;1H", output);
        Assert.Contains("\x1b[25;1H", output);
        Assert.Equal(3, renderer.LastDrawnRows);
        Assert.Equal(23, renderer.LastContentRow);   // visibleLines − 1，与内容量无关
    }

    [Fact]
    public void FlushBuffer_initial_content_renders_full()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        string output = CaptureOutput(() => renderer.FlushBuffer(), screen);

        Assert.Contains("\x1b[2J\x1b[H", output);
        Assert.Contains("\x1b[23;1H", output);
        Assert.Contains("\x1b[24;1H", output);
        Assert.Equal(2, renderer.LastDrawnRows);
        Assert.Equal(23, renderer.LastContentRow);
    }

    /// <summary>
    /// B3（spec §3.4）：新行到达一律 FullRefresh（绝对定位），不再走光标相对增量 ——
    /// 贴底后光标恒在预留行（末行），增量写在末行会触发终端滚动，ADR-0006 实测
    /// ConPTY 下滚出视口的字节不重发 master ⇒ 后续行永远渲染不出来。
    /// </summary>
    [Fact]
    public void FlushBuffer_new_lines_trigger_fullrefresh()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        renderer.FlushBuffer();
        screen.Clear();

        harness.Add(MakeSource(2));
        renderer.FlushBuffer();
        string output = screen.GetOutput();

        // 全刷信号：ClearScreen + 三行绝对定位（22/23/24）+ 光标 25
        Assert.Contains("\x1b[2J\x1b[H", output);
        Assert.Contains("\x1b[22;1H", output);
        Assert.Contains("\x1b[23;1H", output);
        Assert.Contains("\x1b[24;1H", output);
        Assert.Contains("\x1b[25;1H", output);
        Assert.Equal(3, renderer.LastDrawnRows);
    }

    [Fact]
    public void FlushBuffer_no_change_produces_no_output()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        renderer.FlushBuffer();
        screen.Clear();

        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Empty(output);
    }

    [Fact]
    public void FlushBuffer_clear_after_content_emits_clearscreen()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        harness.Add(MakeSource(2));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        renderer.FlushBuffer();
        screen.Clear();

        harness.Clear();
        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Contains("\x1b[2J\x1b[H", output);
    }

    // LastDrawnRows is not reset by CLEAR — only updated by FullRefresh.
    // CLEAR tracking reset is tested via ClearScreen emission (above) and
    // the internal _lastRenderedLineNo/_lastSnapshotLineCount reset, which
    // is verified by observing FullRefresh on next content flush.

    [Fact]
    public void FlushBuffer_clearline_triggers_fullrefresh()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        harness.Add(MakeSource(2));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        renderer.FlushBuffer();
        screen.Clear();

        harness.RemoveAt(0);
        harness.RemoveAt(0);
        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Contains("\x1b[2J\x1b[H", output);
        // 贴底：CLEARLINE 后仅剩 1 行 ⇒ 锚末行 23（0-based）= 1-based 24
        Assert.Contains("\x1b[24;1H", output);
    }

    [Fact]
    public void FullRefresh_with_offset_shows_correct_start_line()
    {
        using var harness = new Harness();
        var screen = new BufferedVtScreen(80, 10);
        var scroll = new ScrollController(scrollVisibleLines: 1);
        for (int i = 0; i < 15; i++)
            harness.Add(MakeSource(i));
        var renderer = NewRenderer(harness, screen, scroll);

        scroll.ScrollBy(3, 15);

        string output = CaptureOutput(() => renderer.FullRefresh("test"), screen);

        // Scroll Mode（offset>0）：visibleLines = 10−2 = 8，内容填满视口 ⇒ baseRow = 0
        // （贴底 = 顶部对齐），与改动前一致。
        Assert.Contains("\x1b[1;1H", output);
        Assert.Equal(8, renderer.LastDrawnRows);
        Assert.Equal(7, renderer.LastContentRow);   // visibleLines − 1
    }

    [Fact]
    public void FlushBuffer_auto_follow_resets_scroll_on_new_output()
    {
        using var harness = new Harness();
        var screen = new BufferedVtScreen(80, 25);
        var scroll = new ScrollController(scrollVisibleLines: 1);
        var autoFollowCalled = false;
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        harness.Add(MakeSource(2));
        var renderer = NewRenderer(harness, screen, scroll);
        renderer.OnScrollAutoFollow = () => autoFollowCalled = true;

        renderer.FlushBuffer();
        scroll.ScrollBy(1, 3);
        screen.Clear();

        harness.Add(MakeSource(3));
        renderer.FlushBuffer();

        Assert.True(autoFollowCalled);
        Assert.Equal(0, scroll.ScrollOffset);
    }

    [Fact]
    public void FlushBuffer_lastLineNotEnd_triggers_fullrefresh()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0, lineEnd: false));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        renderer.FlushBuffer();
        screen.Clear();

        harness.Add(MakeSource(1));
        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Contains("\x1b[2J\x1b[H", output);
    }

    [Fact]
    public void FlushBuffer_sourceLine_change_detects_clearline()
    {
        using var harness = new Harness();
        harness.Add(MakeSource(0));
        harness.Add(MakeSource(1));
        harness.Add(MakeSource(2));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = NewRenderer(harness, screen);

        renderer.FlushBuffer();
        screen.Clear();

        // 末行被新对象替换（LineNo 不变）——CLEARLINE + reprint 的反例。
        harness.RemoveAt(2);
        harness.Add(MakeSource(2));
        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Contains("\x1b[2J\x1b[H", output);
    }

    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
