using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace EraCore.Native.Android;

/// <summary>
/// 原生前端导航壳：游戏库（默认页） ↔ 游戏画面 ↔ 设置覆盖层。
/// 切换游戏时由 <see cref="NativeRuntime"/> 串行地停止旧会话、启动新会话，避免并行游戏循环。
/// </summary>
internal sealed class NativeShell : Panel
{
    private readonly GameLibraryView _library;
    private readonly NativeRoot _game;
    private readonly SettingsView _settings;
    private readonly DispatcherTimer _insetsTimer;

    private bool _opening;

    public NativeShell()
    {
        _settings = new SettingsView();
        _library = new GameLibraryView();
        _game = new NativeRoot();
        _game.IsVisible = false;

        Children.Add(_library);
        Children.Add(_game);
        Children.Add(_settings);

        _library.GameSelected += OnGameSelected;
        _library.SettingsRequested += ShowSettings;
        _game.SettingsRequested += ShowSettings;
        _game.LibraryRequested += ShowLibrary;
        _settings.CloseRequested += HideSettings;

        NativeSettings.Changed += OnSettingsChanged;
        NativeSettings.SourcesChanged += OnSourcesChanged;
        ApplyTheme(NativeSettings.Current.Theme);
        MainActivity.ApplyOrientation(NativeSettings.Current.Orientation);

        // 转屏时窗口 inset 不会立刻稳定（Activity 甚至可能还没创建）：低频轮询补上
        // 游戏库/设置页的安全区（游戏页自己轮询）。必须持有引用，否则计时器会被 GC。
        _insetsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _insetsTimer.Tick += (_, _) =>
        {
            _library.RefreshInsets();
            if (_settings.IsVisible)
                _settings.RefreshInsets();
        };
        _insetsTimer.Start();
    }

    /// <summary>返回键：设置页 → 关闭；游戏页 → 回游戏库；游戏库 → 交还系统（退出）。</summary>
    public bool HandleBack()
    {
        if (_settings.IsVisible)
        {
            HideSettings();
            return true;
        }

        if (_game.IsVisible)
        {
            ShowLibrary();
            return true;
        }

        return false;
    }

    public void ShowLibrary()
    {
        if (_settings.IsVisible)
            _settings.Hide();
        if (_game.IsVisible)
            _game.Suspend();
        _game.IsVisible = false;
        _library.IsVisible = true;
        _library.Refresh();

        // 回库即释放会话；即使玩家立刻点另一个游戏，OpenGameAsync 还会先关闭当前会话。
        _ = NativeRuntime.CloseGameAsync();
    }

    /// <summary>
    /// 从系统设置页/目录选择器返回：权限可能刚被授予或收回，刷新库页让原目录条目即时变为可用/不可用。
    /// </summary>
    public void OnResume()
    {
        _library.RefreshInsets();
        if (_library.IsVisible)
            _library.Refresh();
        if (_settings.IsVisible)
            _settings.RefreshInsets();
    }

    private void OnSourcesChanged()
    {
        if (_library.IsVisible)
            _library.Refresh();
    }

    private async void OnGameSelected(GameLibraryEntry entry)
    {
        if (_opening)
            return;

        _opening = true;
        _library.SetBusy(true, $"正在启动 {entry.Name}…");

        _library.IsVisible = false;
        _game.IsVisible = true;
        _game.Resume();

        var opened = await NativeRuntime.OpenGameAsync(entry).ConfigureAwait(true);
        _opening = false;
        _library.SetBusy(false);
        if (!opened)
            _library.Refresh();
    }

    private void ShowSettings()
    {
        var gameName = NativeRuntime.CurrentRoot is null ? null : NativeRuntime.CurrentGameName;
        var fontName = NativeRuntime.CurrentRoot is null ? null : NativeRuntime.Metrics.FontName;
        _settings.Show(gameName, fontName);
    }

    private void HideSettings() => _settings.Hide();

    private void OnSettingsChanged(AppSettings settings)
    {
        ApplyTheme(settings.Theme);
        MainActivity.ApplyOrientation(settings.Orientation);
        if (_game.IsVisible)
            _game.Resume();
    }

    private static void ApplyTheme(AppTheme theme)
        => NativeApp.ApplyTheme(theme);
}
