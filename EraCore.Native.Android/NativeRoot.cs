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
/// Native M2 根视图：全屏自绘画布 + 输入栏/倒计时 + 加载状态覆盖层。
///
/// <para>输入栏只由 <c>awaiting</c> 驱动（Hosting <see cref="AwaitingView"/>）：
/// Text 输入框 / AnyKey、EnterKey 继续按钮 / 终态隐藏。IME 经 <see cref="IInputPane"/>
/// 把键盘遮挡高度加到根 Padding，配合 Android <c>AdjustResize</c>。</para>
/// </summary>
public sealed class NativeRoot : Border
{
    private const string LogTag = "EraCoreNative";

    private readonly Grid _grid;
    private readonly NativeGameView _view;
    private readonly TextBlock _status;
    private readonly Border _inputPanel;
    private readonly TextBox _inputBox;
    private readonly AvaloniaButton _sendButton;
    private readonly TextBlock _countdownText;
    private readonly DispatcherTimer _timer;

    private readonly double _topInset;
    private IInputPane? _inputPane;

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
        _topInset = StatusBarHeightDp();
        Padding = new Thickness(0, _topInset, 0, 0);

        _grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };

        _view = new NativeGameView();
        _view.ButtonActivated += OnButtonActivated;
        Grid.SetRow(_view, 0);

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
        Grid.SetRow(_inputPanel, 1);

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
        };
        Grid.SetRow(_status, 0);
        Grid.SetRowSpan(_status, 2);

        _grid.Children.Add(_view);
        _grid.Children.Add(_inputPanel);
        _grid.Children.Add(_status);
        Child = _grid;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Poll();
    }

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

    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
        => ApplyImeInset(e.EndRect.Height);

    private void ApplyImeInset(double occludedHeight)
    {
        var bottom = Math.Max(0, occludedHeight);
        Padding = new Thickness(0, _topInset, 0, bottom);
        Log.Info(LogTag, $"ime inset={bottom}");
    }

    private void Poll()
    {
        var snapshot = NativeRuntime.Snapshot;
        if (snapshot is null)
        {
            _status.IsVisible = true;
            _status.Text = NativeRuntime.Status;
            return;
        }

        _status.IsVisible = false;
        _snapshot = snapshot;
        _awaitingView = ComputeAwaitingView(snapshot);

        var metrics = NativeRuntime.Metrics;
        var lineCount = snapshot.lines.Count;
        var changed = snapshot.revision != _lastRevision
                      || lineCount != _lastLineCount
                      || metrics != _lastMetrics;
        if (changed)
        {
            _lastRevision = snapshot.revision;
            _lastLineCount = lineCount;
            _lastMetrics = metrics;

            var frame = NativeRenderModel.FromSnapshot(snapshot);
            _view.SetFrame(frame, metrics);

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                Log.Info(LogTag,
                    $"first frame: lines={frame.Lines.Count} generation={frame.Generation} " +
                    $"revision={frame.Revision} protocol={frame.ProtocolVersion} bg={frame.BackgroundColor}");
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

    /// <summary>
    /// Android 状态栏高度（Avalonia 逻辑像素）。Avalonia 在 Android 上边到边绘制，
    /// 不留安全区会让首行文本被状态栏时钟/图标压住。
    /// </summary>
    private static double StatusBarHeightDp()
    {
        try
        {
            var resources = global::Android.App.Application.Context.Resources;
            if (resources is null)
                return 0;
            var id = resources.GetIdentifier("status_bar_height", "dimen", "android");
            if (id <= 0)
                return 0;
            var px = resources.GetDimensionPixelSize(id);
            var density = resources.DisplayMetrics?.Density ?? 1f;
            return density > 0 ? px / density : px;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
