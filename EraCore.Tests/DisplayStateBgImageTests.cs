using System;
using System.Collections.Generic;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 02 — DisplayState 背景图快照/diff（spec 测试 seam ①/②）。
/// BuildSnapshot 读 bgImages 进快照；ComputeDiff 用快照比较推断背景图变更
/// （与 bgColor 同模式：state 全量字段，变化时携带，无变化 null）。
/// </summary>
public class DisplayStateBgImageTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _state;

    public DisplayStateBgImageTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        _console = new EmueraConsole(new HeadlessConsole(), new NullTerminalSetup());
        _state = new DisplayState(_console, "MS Gothic", fullDiffOnFirstTurn: true);
    }

    public void Dispose()
    {
        _scope.Dispose();
        _console.Dispose();
    }

    [Fact]
    public void BuildSnapshot_includes_bg_images()
    {
        var snap = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, null, "TestFont",
            bgImages: [new BgImageState("bg/a.png", 0, 1f), new BgImageState("bg/b.png", 1, 0.5f)]);

        Assert.Equal(2, snap.bgImages!.Count);
        Assert.Equal("bg/a.png", snap.bgImages[0].src);
        Assert.Equal(0, snap.bgImages[0].depth);
        Assert.Equal(1f, snap.bgImages[0].opacity);
        Assert.Equal("bg/b.png", snap.bgImages[1].src);
    }

    [Fact]
    public void BuildSnapshot_empty_bg_images_omits_field()
    {
        var snap = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, null, "TestFont");

        Assert.Null(snap.bgImages);
    }

    [Fact]
    public void ComputeDiff_includes_bg_image_added_this_turn()
    {
        _state.ComputeDiff(); // 回合 1：初始化（无背景）

        _console.SetBgImage("bg/forest.png", 1, 0.5f);
        var diff = _state.ComputeDiff(); // 回合 2：背景图变更

        Assert.NotNull(diff);
        Assert.NotNull(diff!.bgImages);
        var bg = Assert.Single(diff.bgImages!);
        Assert.Equal("bg/forest.png", bg.src);
        Assert.Equal(1, bg.depth);
        Assert.Equal(0.5f, bg.opacity);
    }

    [Fact]
    public void ComputeDiff_includes_bg_image_cleared_this_turn()
    {
        _state.ComputeDiff(); // 回合 1

        _console.SetBgImage("bg/forest.png", 1, 1f);
        _state.ComputeDiff(); // 回合 2：加图

        _console.ClearBgImage();
        var diff = _state.ComputeDiff(); // 回合 3：清图

        Assert.NotNull(diff);
        Assert.NotNull(diff!.bgImages);
        Assert.Empty(diff.bgImages!); // 清空后状态 = 空列表（非 null：代表"有变更"）
    }

    [Fact]
    public void ComputeDiff_includes_consecutive_bg_image_changes()
    {
        // 连续两轮非空变更：快照必须浅拷贝状态，否则同 List 引用 SequenceEqual 恒真 → diff 丢失
        _state.ComputeDiff(); // 回合 1

        _console.SetBgImage("bg/a.png", 0, 1f);
        _state.ComputeDiff(); // 回合 2：加 a

        _console.SetBgImage("bg/b.png", 1, 0.5f);
        var diff = _state.ComputeDiff(); // 回合 3：再加 b（非空→非空变更）

        Assert.NotNull(diff);
        Assert.NotNull(diff!.bgImages);
        Assert.Equal(2, diff.bgImages!.Count);
        Assert.Equal("bg/b.png", diff.bgImages[1].src);
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
