using System;
using System.Linq;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 02 — 背景图状态与 op（spec 测试 seam ①/②）。
/// SETBGIMAGE/REMOVEBGIMAGE/CLEARBGIMAGE 在无头落地为真实状态（src/depth/opacity），
/// 并 enqueue 对应 TurnOp。语义与 WinForms 一致：set=追加、remove=移除首个同名、clear=全清。
/// </summary>
public class BackgroundImageTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;

    public BackgroundImageTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        _console = new EmueraConsole(new HeadlessConsole(), new NullTerminalSetup());
    }

    public void Dispose()
    {
        _scope.Dispose();
        _console.Dispose();
    }

    private static TurnOp[] Drain(EmueraConsole console)
        => console.DrainPendingOps().ToArray();

    [Fact]
    public void Set_bg_image_updates_state_and_enqueues_op()
    {
        _console.SetBgImage("bg/forest.png", 1, 0.5f);

        var bg = Assert.Single(_console.BgImages);
        Assert.Equal("bg/forest.png", bg.src);
        Assert.Equal(1, bg.depth);
        Assert.Equal(0.5f, bg.opacity);

        var op = Assert.IsType<SetBgImageOp>(Assert.Single(Drain(_console)));
        Assert.Equal("bg/forest.png", op.src);
        Assert.Equal(1, op.depth);
        Assert.Equal(0.5f, op.opacity);
    }

    [Fact]
    public void Set_bg_image_appends_without_replacing_same_src()
    {
        _console.SetBgImage("bg/forest.png", 1, 1.0f);
        _console.SetBgImage("bg/forest.png", 2, 0.5f);

        Assert.Equal(2, _console.BgImages.Count); // WinForms 追加语义
    }

    [Fact]
    public void Remove_bg_image_removes_first_match_only()
    {
        _console.SetBgImage("bg/forest.png", 1, 1.0f);
        _console.SetBgImage("bg/forest.png", 2, 0.5f);

        _console.RemoveBgImage("bg/forest.png");

        var bg = Assert.Single(_console.BgImages);
        Assert.Equal(2, bg.depth); // 移除第一份，保留 depth=2

        var ops = Drain(_console);
        Assert.IsType<RemoveBgImageOp>(ops[^1]); // 前两个是 set
        Assert.Equal(3, ops.Length);
    }

    [Fact]
    public void Clear_bg_image_clears_all_and_enqueues_op()
    {
        _console.SetBgImage("bg/a.png", 0, 1.0f);
        _console.SetBgImage("bg/b.png", 1, 0.5f);

        _console.ClearBgImage();

        Assert.Empty(_console.BgImages);
        var ops = Drain(_console);
        Assert.IsType<ClearBgImageOp>(ops[^1]); // 前两个是 set
        Assert.Equal(3, ops.Length);
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
