using System;
using System.Collections.Generic;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

public class TerminalRendererPipelineTests
{
    private sealed class FakeButtonContext : IButtonDisplayContext
    {
        public ConsoleButtonString? SelectingButton => null;
        public TerminalCharWidthConfig CharWidthConfig => TerminalCharWidthConfig.Default;
    }

    private sealed class FakeDisplayState : IDisplayState
    {
        private readonly IReadOnlyList<DisplaySnapshot> _snapshots;
        private int _index = -1;

        internal FakeDisplayState(params DisplaySnapshot[] snapshots)
        {
            _snapshots = snapshots;
        }

        public bool TryUpdate()
        {
            if (_index >= _snapshots.Count - 1) return false;
            _index++;
            return true;
        }

        public DisplaySnapshot Current => _snapshots[_index >= 0 ? _index : 0];
    }

    private static ConsoleDisplayLine MakeSource() =>
        new(Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false);

    private static DisplayLine MakeLine(int lineNo, ConsoleDisplayLine? source = null, bool isLineEnd = true)
    {
        var src = source ?? MakeSource();
        var line = new DisplayLine(new List<DisplayEntry>(), "left", isLineEnd: isLineEnd);
        line.LineNo = lineNo;
        line.SourceLine = src;
        return line;
    }

    private static DisplaySnapshot Snapshot(params DisplayLine[] lines) =>
        new(new List<DisplayLine>(lines), null,
            AwaitInfo.Of(new InputRequest { InputType = InputType.EnterKey }, ConsoleState.WaitInput, timedOut: false),
            5, 0);

    private static string CaptureOutput(Action action, BufferedVtScreen screen)
    {
        screen.Clear();
        action();
        return screen.GetOutput();
    }

    [Fact]
    public void FullRefresh_empty_produces_only_clearscreen()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 25);
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen,
            new FakeDisplayState(Snapshot()));

        string output = CaptureOutput(() => renderer.FullRefresh("test"), screen);

        Assert.Contains("\x1b[2J\x1b[H", output);
        Assert.Equal(-1, renderer.LastDrawnRows);
    }

    [Fact]
    public void FullRefresh_with_content_writes_all_lines()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 25);
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen,
            new FakeDisplayState(Snapshot(MakeLine(0), MakeLine(1), MakeLine(2))));

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
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 25);
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen,
            new FakeDisplayState(Snapshot(MakeLine(0), MakeLine(1))));

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
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 25);
        var src0 = MakeSource();
        var src1 = MakeSource();
        var src2 = MakeSource();
        var displayState = new FakeDisplayState(
            Snapshot(MakeLine(0, src0), MakeLine(1, src1)),
            Snapshot(MakeLine(0, src0), MakeLine(1, src1), MakeLine(2, src2)));
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen, displayState);

        renderer.FlushBuffer();
        screen.Clear();

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
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var displayState = new FakeDisplayState(Snapshot(MakeLine(0), MakeLine(1)));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen, displayState);

        renderer.FlushBuffer();
        screen.Clear();

        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Empty(output);
    }

    [Fact]
    public void FlushBuffer_clear_after_content_emits_clearscreen()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var displayState = new FakeDisplayState(
            Snapshot(MakeLine(0), MakeLine(1), MakeLine(2)),
            Snapshot());
        var screen = new BufferedVtScreen(80, 25);
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen, displayState);

        renderer.FlushBuffer();
        screen.Clear();

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
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var displayState = new FakeDisplayState(
            Snapshot(MakeLine(0, MakeSource()), MakeLine(1, MakeSource()), MakeLine(2, MakeSource())),
            Snapshot(MakeLine(1, MakeSource())));
        var screen = new BufferedVtScreen(80, 25);
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen, displayState);

        renderer.FlushBuffer();
        screen.Clear();

        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Contains("\x1b[2J\x1b[H", output);
        // 贴底：CLEARLINE 后仅剩 1 行 ⇒ 锚末行 23（0-based）= 1-based 24
        Assert.Contains("\x1b[24;1H", output);
    }

    [Fact]
    public void FullRefresh_with_offset_shows_correct_start_line()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 10);
        var scroll = new ScrollController(scrollVisibleLines: 1);
        var lines = new List<DisplayLine>();
        for (int i = 0; i < 15; i++)
            lines.Add(MakeLine(i));
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), scroll, screen,
            new FakeDisplayState(Snapshot(lines.ToArray())));

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
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 25);
        var scroll = new ScrollController(scrollVisibleLines: 1);
        var autoFollowCalled = false;
        // Share same SourceLine references to avoid spurious CLEARLINE detection
        var src0 = MakeSource();
        var src1 = MakeSource();
        var src2 = MakeSource();
        var src3 = MakeSource();
        var displayState = new FakeDisplayState(
            Snapshot(MakeLine(0, src0), MakeLine(1, src1), MakeLine(2, src2)),
            Snapshot(MakeLine(0, src0), MakeLine(1, src1), MakeLine(2, src2), MakeLine(3, src3)));
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), scroll, screen, displayState);
        renderer.OnScrollAutoFollow = () => autoFollowCalled = true;

        renderer.FlushBuffer();
        scroll.ScrollBy(1, 3);
        screen.Clear();

        renderer.FlushBuffer();

        Assert.True(autoFollowCalled);
        Assert.Equal(0, scroll.ScrollOffset);
    }

    [Fact]
    public void FlushBuffer_lastLineNotEnd_triggers_fullrefresh()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 25);
        var src0 = MakeSource();
        var displayState = new FakeDisplayState(
            Snapshot(MakeLine(0, src0, isLineEnd: false)),
            Snapshot(MakeLine(1, src0)));
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen, displayState);

        renderer.FlushBuffer();
        screen.Clear();

        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Contains("\x1b[2J\x1b[H", output);
    }

    [Fact]
    public void FlushBuffer_sourceLine_change_detects_clearline()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var screen = new BufferedVtScreen(80, 25);
        var srcOld = MakeSource();
        var srcNew = MakeSource();
        var displayState = new FakeDisplayState(
            Snapshot(MakeLine(0, srcOld), MakeLine(1, srcOld), MakeLine(2, srcOld)),
            Snapshot(MakeLine(0, srcOld), MakeLine(1, srcOld), MakeLine(2, srcNew)));
        var renderer = new TerminalRenderer(
            new FakeButtonContext(), new ScrollController(1), screen, displayState);

        renderer.FlushBuffer();
        screen.Clear();

        renderer.FlushBuffer();
        string output = screen.GetOutput();

        Assert.Contains("\x1b[2J\x1b[H", output);
    }
}
