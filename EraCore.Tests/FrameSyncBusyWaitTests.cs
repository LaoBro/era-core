using System;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// 2.1 帧同步忙等清理单测（current_plan/2026.8.3.android-perf.md）。
/// headless/移动端 IConsoleUI.ProcessEvents 为 no-op（HeadlessConsole.cs:44），msPerFrame 默认 0 = 禁用帧同步；
/// 验证 SetBgColor / RefreshStrings 的忙等循环（while elapsed &lt; msPerFrame ProcessEvents()）
/// 在 msPerFrame==0 时零调用 ProcessEvents——每回合最多 16ms 纯空转被移除。
/// </summary>
public class FrameSyncBusyWaitTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly CountingUI _ui;
    private readonly EmueraConsole _console;

    public FrameSyncBusyWaitTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        _ui = new CountingUI();
        _console = new EmueraConsole(_ui, new NullTerminalSetup());
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public void HeadlessDefault_DisablesFrameSync()
    {
        // headless 默认禁用帧同步（ProcessEvents 为 no-op，忙等无意义）
        Assert.Equal(0u, _console._state.msPerFrame);
    }

    [Fact]
    public void SetBgColor_WhenFrameSyncDisabled_DoesNotSpin()
    {
        // 滚动条不在底部（Value != Maximum）→ 不命中 SetBgColor 的快速 return，进入忙等逻辑
        _ui.ScrollBar.Value = 1;
        _ui.ScrollBar.Maximum = 10;

        // 连续多次调用：首次 _drawStopwatch 为 null，后续走 WaitFrameSyncIfEnabled 的 else if 分支
        for (int i = 0; i < 3; i++)
            _console.SetBgColor(EmuColor.FromArgb(10, 20, 30));

        // msPerFrame==0 → 忙等循环直接跳过，ProcessEvents 零调用
        Assert.Equal(0, _ui.ProcessEventsCount);
    }

    [Fact]
    public void SetBgColor_WhenFrameSyncEnabled_Spins()
    {
        // 正向路径证明：msPerFrame>0 时忙等确实调用 ProcessEvents——
        // 反向用例（断言零调用）的成立前提是「忙等被 msPerFrame==0 跳过」而非其他原因。
        _ui.ScrollBar.Value = 1;
        _ui.ScrollBar.Maximum = 10;
        _console._state.msPerFrame = 100;

        // 第一次：_drawStopwatch 为 null → StartNew，不忙等；第二次：elapsed < 100ms → 进入忙等
        _console.SetBgColor(EmuColor.FromArgb(10, 20, 30));
        _console.SetBgColor(EmuColor.FromArgb(10, 20, 30));

        Assert.True(_ui.ProcessEventsCount > 0);
    }

    [Fact]
    public void RefreshStrings_WhenFrameSyncDisabled_DoesNotSpin()
    {
        _ui.ScrollBar.Value = 1;
        _ui.ScrollBar.Maximum = 10;
        // 绕过 ConsoleRefreshHandler 的帧率节流（line 45：距上次绘制 < msPerFrame 时提前 return）——
        // 默认 State=Initializing 会先被节流拦截、忙等分支未触发，用例无法验证禁用路径。
        _console._state.State = ConsoleState.WaitInput;

        for (int i = 0; i < 3; i++)
        {
            // 每次重置 forceTextBoxColor，确保触发 RefreshStrings 内的忙等分支
            _console._state.forceTextBoxColor = true;
            _console._refresh.RefreshStrings(false);
        }

        Assert.Equal(0, _ui.ProcessEventsCount);
    }

    [Fact]
    public void RefreshStrings_WhenFrameSyncEnabled_Spins()
    {
        _ui.ScrollBar.Value = 1;
        _ui.ScrollBar.Maximum = 10;
        _console._state.State = ConsoleState.WaitInput;
        _console._state.msPerFrame = 100;

        _console._state.forceTextBoxColor = true;
        _console._refresh.RefreshStrings(false); // 第一次：_drawStopwatch 为 null → StartNew
        _console._state.forceTextBoxColor = true;
        _console._refresh.RefreshStrings(false); // 第二次：elapsed < 100ms → 进入忙等

        Assert.True(_ui.ProcessEventsCount > 0);
    }

    /// <summary>IConsoleUI 测试替身：委托 HeadlessConsole，ProcessEvents 计数，滚动条可配置。</summary>
    private sealed class CountingUI : IConsoleUI
    {
        private readonly HeadlessConsole _inner = new();

        public int ProcessEventsCount { get; private set; }
        public IScrollBar ScrollBar { get; } = new ConfigurableScrollBar();

        public bool Created => _inner.Created;
        public int ClientWidth => _inner.ClientWidth;
        public int ClientHeight => _inner.ClientHeight;
        public string Text { get => _inner.Text; set => _inner.Text = value; }
        public bool IsActive => _inner.IsActive;

        public void Refresh() => _inner.Refresh();
        public void Invoke(Action action) => _inner.Invoke(action);
        public void Focus() => _inner.Focus();
        public void Close() => _inner.Close();
        public void Reboot() => _inner.Reboot();
        public void ShowConfigDialog() => _inner.ShowConfigDialog();
        public void UpdateLastInput() => _inner.UpdateLastInput();
        public void ResetTextBoxPos() => _inner.ResetTextBoxPos();
        public void ClearRichText() => _inner.ClearRichText();
        public void ApplyTextBoxChanges() => _inner.ApplyTextBoxChanges();
        public void SetTextBoxPos(int xOffset, int yOffset, int width) => _inner.SetTextBoxPos(xOffset, yOffset, width);
        public void ChangeTextBox(string str) => _inner.ChangeTextBox(str);

        public bool TextBoxPosChanged => _inner.TextBoxPosChanged;
        public bool TextBoxIgnoreScrollBarChanges { get => _inner.TextBoxIgnoreScrollBarChanges; set => _inner.TextBoxIgnoreScrollBarChanges = value; }

        public EmuPoint GetMousePosition() => _inner.GetMousePosition();
        public EmuPoint GetCursorPosition() => _inner.GetCursorPosition();
        public void ExitApplication() => _inner.ExitApplication();
        public void ProcessEvents() => ProcessEventsCount++;

        public ITextBox TextBox => _inner.TextBox;
        public IPictureBox MainPicBox => _inner.MainPicBox;

        public IInputHost Input => _inner.Input;
        public IViewportProvider Viewport => _inner.Viewport;
    }

    private sealed class ConfigurableScrollBar : IScrollBar
    {
        public int Value { get; set; }
        public int Maximum { get; set; }
        public bool Enabled { get; set; }
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
