using System;
using System.Collections.Generic;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// v13 revision 语义锁：
/// - 每次画面重建 +1（初始构建 / 追加 / 等长行替换）；no-op 帧不变；
/// - revision 与按钮代号 generation 解耦；
/// - diff 与 snapshot 的 revision 同源。
/// </summary>
public sealed class DisplayStateRevisionTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _displayState;

    public DisplayStateRevisionTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        _console = new EmueraConsole(new HeadlessConsole(), new NullTerminalSetup());
        _displayState = new DisplayState(_console, "MS Gothic");
    }

    public void Dispose() => _scope.Dispose();

    private static ConsoleDisplayLine Line(string text) =>
        new(new[] { TestButtonFactory.CreateNonButton(text) }, isLogical: true, temporary: false);

    private void PrintLine(string text)
    {
        _console.DisplayLineList.Add(Line(text));
        _console._state._pendingOps.Enqueue(new PrintOp(
            new List<PrintSegment> { new(text, null, null, null, null) },
            button: null));
    }

    [Fact]
    public void Revision_increments_on_each_rebuild_and_stays_on_noop()
    {
        Assert.True(_displayState.TryUpdate());          // 初始构建（空屏）
        var first = _displayState.Current.revision;
        Assert.True(first > 0, "初始构建后 revision 必须 > 0");

        Assert.False(_displayState.TryUpdate());         // no-op
        Assert.Equal(first, _displayState.Current.revision);

        PrintLine("a");
        Assert.True(_displayState.TryUpdate());
        Assert.Equal(first + 1, _displayState.Current.revision);

        Assert.False(_displayState.TryUpdate());         // 再次 no-op
        Assert.Equal(first + 1, _displayState.Current.revision);
    }

    [Fact]
    public void Equal_length_line_replacement_increments_revision_without_generation_change()
    {
        PrintLine("aaaa");
        PrintLine("bbbb");
        Assert.True(_displayState.TryUpdate());

        var beforeRevision = _displayState.Current.revision;
        var beforeGeneration = _displayState.Current.generation;

        // 等长替换：行数不变、按钮代不变、文本长度不变，但行对象被替换。
        _console.DisplayLineList[0] = Line("cccc");

        Assert.True(_displayState.TryUpdate());
        var after = _displayState.Current;

        Assert.Equal(beforeRevision + 1, after.revision);
        Assert.Equal(beforeGeneration, after.generation);
        Assert.Equal("cccc", after.lines[0].entries[0].segments[0].text);
    }

    [Fact]
    public void Diff_and_snapshot_share_same_revision_source()
    {
        PrintLine("a");
        Assert.Null(_displayState.ComputeDiff()); // 首回合只设基线

        PrintLine("b");
        var diff = _displayState.ComputeDiff();

        Assert.NotNull(diff);
        Assert.Equal(_displayState.Revision, _displayState.Current.revision);
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
