using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Android.Util;
using EraCore.Hosting;
using EraCore.Native;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Sessions;
using AvaloniaButton = Avalonia.Controls.Button;

namespace EraCore.Native.Android;

/// <summary>
/// 游戏画面根视图：顶部工具条（游戏名 / 设置 / 游戏库）+ 全屏自绘画布 + 输入栏/倒计时。
///
/// <para>输入栏只由 <c>awaiting</c> 驱动（Hosting <see cref="AwaitingView"/>）：
/// Text 输入框 / AnyKey、EnterKey 继续按钮 / 终态隐藏。IME 经 <see cref="IInputPane"/>
/// 把键盘遮挡高度加入根 Padding；底部安全区与 IME 取 <c>max</c>，避免手势条 + 键盘重复叠加。</para>
/// </summary>
internal sealed class NativeRoot : Border
{
    private const string LogTag = "EraCoreNative";
    private const double ToolbarHeight = 36;

    private readonly Grid _grid;
    private readonly NativeGameView _view;
    private readonly TextBlock _gameLabel;
    private readonly TextBlock _status;
    private readonly Border _inputPanel;
    private readonly TextBox _inputBox;
    private readonly AvaloniaButton _sendButton;
    private readonly TextBlock _countdownText;
    private readonly DispatcherTimer _timer;

    private SystemBarInsets _systemInsets;
    private double _imeInset;
    private Avalonia.Size _lastInsetSize;
    private IInputPane? _inputPane;

    private NativeRenderStyle _lastStyle = NativeRenderStyle.From(NativeDefaults.Metrics, AppSettings.Default);
    private HostMetrics _lastMetrics = NativeDefaults.Metrics;
    private long _lastRevision = long.MinValue;
    private int _lastLineCount = -1;
    private bool _firstFrameLogged;
    private DisplaySnapshot? _snapshot;
    private AwaitingView? _awaitingView;
    private DateTimeOffset? _waitStartedAt;
    private string? _waitKey;
    private long _focusRevision = long.MinValue;
    private long _submittedRevision = long.MinValue;

    public NativeRoot()
    {
        Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));

        _grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        _gameLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 185)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 8, 0),
        };

        var settingsButton = new AvaloniaButton
        {
            Content = "设置",
            FontSize = 12,
            MinHeight = 28,
            Padding = new Thickness(10, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke();

        var libraryButton = new AvaloniaButton
        {
            Content = "游戏库",
            FontSize = 12,
            MinHeight = 28,
            Padding = new Thickness(10, 2),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        libraryButton.Click += (_, _) => LibraryRequested?.Invoke();

        var toolbarRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(_gameLabel, 0);
        Grid.SetColumn(settingsButton, 1);
        Grid.SetColumn(libraryButton, 2);
        toolbarRow.Children.Add(_gameLabel);
        toolbarRow.Children.Add(settingsButton);
        toolbarRow.Children.Add(libraryButton);

        var toolbar = new Border
        {
            Height = ToolbarHeight,
            Background = new SolidColorBrush(Color.FromRgb(16, 16, 24)),
            Child = toolbarRow,
        };
        Grid.SetRow(toolbar, 0);

        _view = new NativeGameView();
        _view.ButtonActivated += OnButtonActivated;
        Grid.SetRow(_view, 1);

        _countdownText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102)),
            IsVisible = false,
        };

        _inputBox = new TextBox
        {
            PlaceholderText = "输入…",
            MinHeight = 44,
            FontSize = 16,
            Margin = new Thickness(0, 0, 8, 0),
        };
        _inputBox.KeyDown += OnInputKeyDown;

        _sendButton = new AvaloniaButton
        {
            Content = "发送",
            MinWidth = 72,
            MinHeight = 44,
        };
        _sendButton.Click += (_, _) => SubmitInput();

        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_inputBox, 0);
        Grid.SetColumn(_sendButton, 1);
        inputRow.Children.Add(_inputBox);
        inputRow.Children.Add(_sendButton);

        var inputStack = new StackPanel { Spacing = 6 };
        inputStack.Children.Add(_countdownText);
        inputStack.Children.Add(inputRow);

        _inputPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 30)),
            Padding = new Thickness(12, 8, 12, 10),
            IsVisible = false,
            Child = inputStack,
        };
        Grid.SetRow(_inputPanel, 2);

        _status = new TextBlock
        {
            Text = "正在启动…",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 190)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = true,
        };
        Grid.SetRow(_status, 1);

        _grid.Children.Add(toolbar);
        _grid.Children.Add(_view);
        _grid.Children.Add(_inputPanel);
        _grid.Children.Add(_status);
        Child = _grid;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Poll();

        ApplyInsets(force: true);
    }

    public event Action? LibraryRequested;

    public event Action? SettingsRequested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var top = TopLevel.GetTopLevel(this);
        _inputPane = top?.PlatformImpl?.TryGetFeature(typeof(IInputPane)) as IInputPane;
        if (_inputPane is not null)
        {
            _inputPane.StateChanged += OnInputPaneStateChanged;
            ApplyImeInset(_inputPane.OccludedRect.Height);
        }

        ApplyInsets(force: true);
        _timer.Start();
        Poll();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        if (_inputPane is not null)
            _inputPane.StateChanged -= OnInputPaneStateChanged;
        _inputPane = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>游戏库/设置页返回后重新开始轮询（不创建新会话）。</summary>
    public void Resume()
    {
        _lastStyle = NativeRenderStyle.From(NativeDefaults.Metrics, AppSettings.Default);
        _lastRevision = long.MinValue;
        _lastLineCount = -1;
        _firstFrameLogged = false;
        ApplyInsets(force: true);
        if (!_timer.IsEnabled)
            _timer.Start();
        Poll();
    }

    /// <summary>
    /// 即将打开新游戏会话：清空上一会话的提交状态。
    /// 新会话的 revision 从 1 重新计数，若沿用旧值会误判「本帧已提交」而让发送按钮一直禁用。
    /// </summary>
    public void PrepareNewSession()
    {
        _snapshot = null;
        _submittedRevision = long.MinValue;
        _inputBox.Text = string.Empty;
    }

    /// <summary>离开游戏画面时停止轮询，避免后台定时器继续跑。</summary>
    public void Suspend() => _timer.Stop();

    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
        => ApplyImeInset(e.EndRect.Height);

    private void ApplyImeInset(double occludedHeight)
    {
        var inset = Math.Max(0, occludedHeight);
        if (Math.Abs(inset - _imeInset) < 0.5)
            return;
        _imeInset = inset;
        ApplyInsets(force: true);
        Log.Info(LogTag, $"ime inset={inset:F1}");
    }

    /// <summary>系统栏与 IME 取 max：键盘弹出时手势条区域通常已被键盘覆盖，相加会重复留白。</summary>
    private void ApplyInsets(bool force)
    {
        var size = Bounds.Size;
        if (force || size != _lastInsetSize)
        {
            _lastInsetSize = size;
            _systemInsets = AndroidInsets.SystemBars();
        }

        var bottom = Math.Max(_systemInsets.Bottom, _imeInset);
        var padding = new Thickness(
            _systemInsets.Left,
            _systemInsets.Top,
            _systemInsets.Right,
            bottom);
        if (padding == Padding)
            return;
        Padding = padding;
        Log.Info(LogTag,
            $"insets: left={_systemInsets.Left:F1} top={_systemInsets.Top:F1} " +
            $"right={_systemInsets.Right:F1} bottom={_systemInsets.Bottom:F1} ime={_imeInset:F1} -> {bottom:F1}");
    }

    private void Poll()
    {
        ApplyInsets(force: false);

        var snapshot = NativeRuntime.Snapshot;
        _gameLabel.Text = NativeRuntime.CurrentGameName;

        if (snapshot is null)
        {
            _status.IsVisible = true;
            _status.Text = NativeRuntime.Status;
            _view.IsVisible = false;
            _inputPanel.IsVisible = false;
            return;
        }

        _status.IsVisible = false;
        _view.IsVisible = true;
        _snapshot = snapshot;
        _awaitingView = ComputeAwaitingView(snapshot);

        var metrics = NativeRuntime.Metrics;
        var style = NativeRuntime.Style;
        var lineCount = snapshot.lines.Count;
        var changed = snapshot.revision != _lastRevision
                      || lineCount != _lastLineCount
                      || style != _lastStyle
                      || metrics != _lastMetrics;
        if (changed)
        {
            _lastRevision = snapshot.revision;
            _lastLineCount = lineCount;
            _lastStyle = style;
            _lastMetrics = metrics;

            var frame = NativeRenderModel.FromSnapshot(snapshot);
            _view.SetFrame(frame, style);

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                Log.Info(LogTag,
                    $"first frame: lines={frame.Lines.Count} generation={frame.Generation} " +
                    $"revision={frame.Revision} protocol={frame.ProtocolVersion} " +
                    $"bg={frame.BackgroundColor} font={style.FontName}/{style.FontSize:F0} " +
                    $"line={style.LineHeight:F1}");
            }
        }

        UpdateInputBar();
    }

    private AwaitingView ComputeAwaitingView(DisplaySnapshot snapshot)
    {
        var wait = snapshot.awaiting.wait;
        var key = wait is null
            ? null
            : $"{wait.durationMs}|{wait.displayTime}|{wait.timeUpMessage}";
        if (key != _waitKey)
        {
            _waitKey = key;
            _waitStartedAt = wait is null ? null : DateTimeOffset.UtcNow;
        }

        return AwaitingView.From(
            snapshot.awaiting,
            _waitStartedAt,
            DateTimeOffset.UtcNow,
            "TINPUT 超时，游戏以默认值继续");
    }

    private void UpdateInputBar()
    {
        var view = _awaitingView;
        if (view is null ||
            view.IsTerminal ||
            NativeRuntime.State is CoreSessionState.Completed or CoreSessionState.Stopped or CoreSessionState.Failed ||
            view.Mode is not (HostInputMode.Text or HostInputMode.AnyKey or HostInputMode.EnterKey))
        {
            _inputPanel.IsVisible = false;
            return;
        }

        _inputPanel.IsVisible = true;
        var isText = view.Mode == HostInputMode.Text;
        _inputBox.IsVisible = isText;
        _sendButton.Content = isText ? "发送" : "继续";
        _sendButton.IsEnabled = _submittedRevision != (_snapshot?.revision ?? 0);

        if (view.DisplayCountdown && view.RemainingMs is { } remaining)
        {
            _countdownText.IsVisible = true;
            _countdownText.Text = $"⏱ {remaining / 1000.0:F1}s";
        }
        else if (view.TimedOut)
        {
            _countdownText.IsVisible = true;
            _countdownText.Text = view.TimeUpMessage ?? "已超时";
        }
        else
        {
            _countdownText.IsVisible = false;
        }

        if (isText && _snapshot is { } snapshot && _focusRevision != snapshot.revision)
        {
            _focusRevision = snapshot.revision;
            Dispatcher.UIThread.Post(() => _inputBox.Focus(), DispatcherPriority.Loaded);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        SubmitInput();
        e.Handled = true;
    }

    private void SubmitInput()
    {
        var view = _awaitingView;
        var snapshot = _snapshot;
        if (view is null || snapshot is null || !view.IsWaitingForInput)
            return;
        if (_submittedRevision == snapshot.revision)
            return;

        string value;
        if (view.Mode == HostInputMode.Text)
        {
            value = _inputBox.Text ?? string.Empty;
            if (view.RequiresValue && value.Length == 0)
                return;
        }
        else
        {
            value = string.Empty; // AnyKey / EnterKey：推进
        }

        _submittedRevision = snapshot.revision;
        _sendButton.IsEnabled = false;
        NativeRuntime.Submit(new InputFrame(Value: value));
        Log.Info(LogTag, $"submit: mode={view.Mode} value='{value}' revision={snapshot.revision}");

        if (view.Mode == HostInputMode.Text)
            _inputBox.Text = string.Empty;
    }

    private void OnButtonActivated(NativeButton button)
    {
        var view = _awaitingView;
        var snapshot = _snapshot;
        if (view is null || snapshot is null || !view.CanSubmitButton(button.Generation, snapshot.generation))
            return;
        if (_submittedRevision == snapshot.revision)
            return;

        var value = button.Value?.ToString() ?? string.Empty;
        _submittedRevision = snapshot.revision;
        NativeRuntime.Submit(new InputFrame(Value: value));
        _sendButton.IsEnabled = false;
        Log.Info(LogTag,
            $"button: value='{value}' isInteger={button.IsInteger} generation={button.Generation} " +
            $"revision={snapshot.revision}");
    }
}
