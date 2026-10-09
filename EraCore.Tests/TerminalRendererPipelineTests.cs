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
        new(new List<DisplayLine>(lines), null, "WaitInput", null, false,
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
        Assert.Contains("\x1b[1;1H", output);
        Assert.Contains("\x1b[2;1H", output);
        Assert.Contains("\x1b[3;1H", output);
        Assert.Contains("\x1b[4;1H", output);
        Assert.Equal(3, renderer.LastDrawnRows);
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
        Assert.Contains("\x1b[1;1H", output);
        Assert.Contains("\x1b[2;1H", output);
        Assert.Equal(2, renderer.LastDrawnRows);
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
        Assert.Contains("\x1b[1;1H", output);
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

        Assert.Contains("\x1b[1;1H", output);
        Assert.Equal(8, renderer.LastDrawnRows);
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
