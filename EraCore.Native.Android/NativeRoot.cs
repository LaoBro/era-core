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
/// 游戏画面根视图：全屏自绘画布 + 右上角两个悬浮按钮（输入 ⌨ / 菜单 ☰）+ 底部浮层输入栏。
///
/// <para>画面铺满整个安全区，输入栏、倒计时、菜单都是覆盖层，不占布局高度，因此唤出/收起
/// 输入框不会改变 <see cref="NativeGameView"/> 的可绘区域（画面不会重排或跳动）。</para>
///
/// <para>输入栏只由用户点击悬浮输入按钮打开（<see cref="_inputVisible"/>），不再随回合推进
/// 自动聚焦。<see cref="HostInputMode.Text"/> 模式下打开时聚焦输入框弹出 IME；提交/按钮推进
/// 或切走模式时把焦点交还画布，IME 随之收起。</para>
/// </summary>
internal sealed class NativeRoot : Border
{
    private const string LogTag = "EraCoreNative";
    private const double FloatingButtonSize = 48;
    private const double FloatingMargin = 8;

    private static readonly IBrush IdleButtonBrush =
        new SolidColorBrush(Color.FromArgb(0x66, 0x10, 0x10, 0x18));

    private static readonly IBrush ActiveButtonBrush =
        new SolidColorBrush(Color.FromArgb(0xB0, 0x2E, 0x6B, 0xC0));

    private readonly Panel _layers;
    private readonly NativeGameView _view;
    private readonly TextBlock _gameLabel;
    private readonly TextBlock _status;
    private readonly Border _inputPanel;
    private readonly TextBox _inputBox;
    private readonly AvaloniaButton _sendButton;
    private readonly TextBlock _countdownText;
    private readonly Border _countdownPill;
    private readonly AvaloniaButton _inputToggle;
    private readonly Border _menuScrim;
    private readonly Border _menuPanel;
    private readonly DispatcherTimer _timer;

    private SystemBarInsets _systemInsets;
    private double _imeInset;
    private DateTimeOffset _immersiveReapplyAt = DateTimeOffset.MinValue;
    private Avalonia.Size _lastInsetSize;
    private SystemBarInsets _lastLoggedInsets;
    private double _lastLoggedIme;
    private bool _hasLoggedInsets;
    private IInputPane? _inputPane;

    private bool _inputVisible;
    private bool _menuVisible;

    private NativeRenderStyle _lastStyle = NativeRenderStyle.From(NativeDefaults.Metrics, AppSettings.Default);
    private HostMetrics _lastMetrics = NativeDefaults.Metrics;
    private long _lastRevision = long.MinValue;
    private int _lastLineCount = -1;
    private bool _firstFrameLogged;
    private DisplaySnapshot? _snapshot;
    private AwaitingView? _awaitingView;
    private DateTimeOffset? _waitStartedAt;
    private string? _waitKey;
    private long _submittedRevision = long.MinValue;

    public NativeRoot()
    {
        Background = new SolidColorBrush(Color.FromRgb(0, 0, 0));

        _layers = new Panel();

        _view = new NativeGameView();
        _view.ButtonActivated += OnButtonActivated;
        _view.ImageActivated += OnImageActivated;
        _layers.Children.Add(_view);

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
        _layers.Children.Add(_status);

        _countdownText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102)),
        };
        _countdownPill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x10, 0x18)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0),
            IsVisible = false,
            Child = _countdownText,
        };
        _layers.Children.Add(_countdownPill);

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

        _inputPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 20, 30)),
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Padding = new Thickness(12, 8, 12, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsVisible = false,
            Child = inputRow,
        };
        _layers.Children.Add(_inputPanel);

        _inputToggle = CreateFloatingButton("⌨", "输入");
        _inputToggle.Click += (_, _) => ToggleInput();

        var menuToggle = CreateFloatingButton("☰", "菜单");
        menuToggle.Click += (_, _) => SetMenuVisible(!_menuVisible);

        var floatingControls = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, FloatingMargin, FloatingMargin, 0),
        };
        floatingControls.Children.Add(_inputToggle);
        floatingControls.Children.Add(menuToggle);
        _layers.Children.Add(floatingControls);

        _menuScrim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x44, 0, 0, 0)),
            IsVisible = false,
        };
        _menuScrim.PointerPressed += (_, e) =>
        {
            SetMenuVisible(false);
            e.Handled = true;
        };
        _layers.Children.Add(_menuScrim);

        _gameLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 185)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(4, 4, 4, 6),
        };

        var menuStack = new StackPanel { Spacing = 6 };
        menuStack.Children.Add(_gameLabel);
        menuStack.Children.Add(CreateMenuItem("设置", () =>
        {
            SetMenuVisible(false);
            SettingsRequested?.Invoke();
        }));
        menuStack.Children.Add(CreateMenuItem("游戏库", () =>
        {
            SetMenuVisible(false);
            LibraryRequested?.Invoke();
        }));

        _menuPanel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x10, 0x10, 0x18)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 6, 8, 8),
            Width = 200,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, FloatingMargin + FloatingButtonSize + 8, FloatingMargin, 0),
            IsVisible = false,
            Child = menuStack,
        };
        _layers.Children.Add(_menuPanel);

        Child = _layers;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => Poll();

        ApplyInsets(force: true);
        UpdateInputToggle(canInput: false);
    }

    public event Action? LibraryRequested;

    public event Action? SettingsRequested;

    /// <summary>返回键：菜单打开时先关闭菜单，返回 true 表示已消费。</summary>
    public bool HandleBack()
    {
        if (!_menuVisible)
            return false;
        SetMenuVisible(false);
        return true;
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

        ApplyInsets(force: true);
        if (IsVisible)
        {
            _timer.Start();
            Poll();
        }
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
        CloseOverlays();
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
        CloseOverlays();
    }

    /// <summary>离开游戏画面时停止轮询，避免后台定时器继续跑。</summary>
    public void Suspend()
    {
        _timer.Stop();
        CloseOverlays();
    }

    private void CloseOverlays()
    {
        DismissInput();
        SetMenuVisible(false);
        UpdateInputToggle(canInput: false);
    }

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

        // 部分 ROM 会在 IME 布局完成后才重新显示状态栏：立即压一次，并在 500ms 后再补一次。
        // 只改系统栏可见性，不碰 Avalonia 布局，因此不会让画布跳动。
        if (inset > 0)
        {
            MainActivity.EnsureImmersiveMode();
            _immersiveReapplyAt = DateTimeOffset.UtcNow.AddMilliseconds(500);
        }
    }

    /// <summary>
    /// 系统栏安全区进根 Padding；IME 不进 Padding——键盘弹出时画布尺寸保持不变，
    /// 只把底部输入浮层抬到键盘上方（<see cref="UpdateImeLift"/>）。
    /// </summary>
    private void ApplyInsets(bool force)
    {
        var size = Bounds.Size;
        if (force || size != _lastInsetSize)
        {
            _lastInsetSize = size;
            _systemInsets = AndroidInsets.SystemBars();
        }

        var padding = new Thickness(
            _systemInsets.Left,
            _systemInsets.Top,
            _systemInsets.Right,
            _systemInsets.Bottom);
        if (padding != Padding)
            Padding = padding;

        UpdateImeLift();

        // 轮询每 150ms 都会走到这里，只在 inset 真正变化时写日志，避免刷屏拖慢主线程。
        if (!_hasLoggedInsets
            || _systemInsets != _lastLoggedInsets
            || Math.Abs(_imeInset - _lastLoggedIme) > 0.5)
        {
            _hasLoggedInsets = true;
            _lastLoggedInsets = _systemInsets;
            _lastLoggedIme = _imeInset;
            Log.Info(LogTag,
                $"insets: left={_systemInsets.Left:F1} top={_systemInsets.Top:F1} " +
                $"right={_systemInsets.Right:F1} bottom={_systemInsets.Bottom:F1} ime={_imeInset:F1}");
        }
    }

    /// <summary>底部浮层让位键盘：面板本就位于底部安全区之上，IME 遮挡高度已按同一口径测量。</summary>
    private void UpdateImeLift()
    {
        // 用 IME 遮挡高度直接抬高：键盘顶边到窗口底部的距离 = 遮挡高度（Avalonia 上报值不含底部安全区）。
        var lift = Math.Max(0, _imeInset);
        var margin = new Thickness(0, 0, 0, lift);
        if (_inputPanel.Margin != margin)
            _inputPanel.Margin = margin;
    }

    private void Poll()
    {
        using var tracePoll = NativeTrace.Section("era:poll");
        ApplyInsets(force: false);
        if (_immersiveReapplyAt != DateTimeOffset.MinValue
            && DateTimeOffset.UtcNow >= _immersiveReapplyAt)
        {
            _immersiveReapplyAt = DateTimeOffset.MinValue;
            MainActivity.EnsureImmersiveMode();
        }

        var snapshot = NativeRuntime.Snapshot;
        _gameLabel.Text = NativeRuntime.CurrentGameName;

        if (snapshot is null)
        {
            _status.IsVisible = true;
            _status.Text = NativeRuntime.Status;
            _view.IsVisible = false;
            DismissInput();
            _countdownPill.IsVisible = false;
            UpdateInputToggle(canInput: false);
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

            NativeFrame frame;
            using (NativeTrace.Section("era:poll.frame"))
            {
                frame = NativeRenderModel.FromSnapshot(snapshot);
                _view.SetFrame(frame, style);
            }

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

        using (NativeTrace.Section("era:poll.input"))
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

    /// <summary>当前回合是否允许唤出输入浮层（文本 / AnyKey / EnterKey）。</summary>
    private bool CanShowInput()
    {
        var view = _awaitingView;
        return view is not null
               && view.IsWaitingForInput
               && !view.IsTerminal
               && NativeRuntime.State is not (CoreSessionState.Completed or CoreSessionState.Stopped or CoreSessionState.Failed)
               && view.Mode is HostInputMode.Text or HostInputMode.AnyKey or HostInputMode.EnterKey;
    }

    private void ToggleInput()
    {
        if (_inputVisible)
            DismissInput();
        else
            OpenInput();
    }

    /// <summary>仅由悬浮输入按钮触发：打开浮层，文本模式才聚焦输入框并弹出 IME。</summary>
    private void OpenInput()
    {
        if (!CanShowInput())
            return;

        _inputVisible = true;
        UpdateInputBar();

        if (_awaitingView is { Mode: HostInputMode.Text })
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_inputVisible)
                    _inputBox.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>收起输入浮层、焦点交还画布并显式请求系统收起 IME。</summary>
    private void DismissInput()
    {
        if (!_inputVisible && !_inputPanel.IsVisible)
            return;

        _inputVisible = false;
        _view.Focus();
        HideAndroidKeyboard();
        _inputPanel.IsVisible = false;
        UpdateInputToggle(CanShowInput());
    }

    /// <summary>
    /// 焦点移出文本控件后 Avalonia 的 IME 收尾在不同设备上可能滞后，这里再向
    /// InputMethodManager 显式请求收起，避免键盘残留在画面上。
    /// </summary>
    private static void HideAndroidKeyboard()
    {
        try
        {
            var activity = MainActivity.Instance;
            if (activity is null)
                return;
            var imm = activity.GetSystemService(global::Android.Content.Context.InputMethodService)
                as global::Android.Views.InputMethods.InputMethodManager;
            imm?.HideSoftInputFromWindow(activity.Window?.DecorView?.WindowToken, 0);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, "hide ime failed: " + ex.Message);
        }
    }

    private void SetMenuVisible(bool visible)
    {
        _menuVisible = visible;
        _menuScrim.IsVisible = visible;
        _menuPanel.IsVisible = visible;
    }

    private void UpdateInputBar()
    {
        UpdateCountdown();

        if (!CanShowInput())
        {
            DismissInput();
            _inputPanel.IsVisible = false;
            UpdateInputToggle(canInput: false);
            return;
        }

        var view = _awaitingView!;
        var isText = view.Mode == HostInputMode.Text;
        _inputBox.IsVisible = isText;
        _sendButton.Content = isText ? "发送" : "继续";
        _sendButton.IsEnabled = _submittedRevision != (_snapshot?.revision ?? 0);
        _inputPanel.IsVisible = _inputVisible;
        UpdateInputToggle(canInput: true);
    }

    /// <summary>TINPUT 倒计时独立于输入浮层显示（时间关键，不要求先唤出输入栏）。</summary>
    private void UpdateCountdown()
    {
        var view = _awaitingView;
        if (view is null)
        {
            _countdownPill.IsVisible = false;
            return;
        }

        if (view.DisplayCountdown && view.RemainingMs is { } remaining)
        {
            _countdownPill.IsVisible = true;
            _countdownText.Text = $"⏱ {remaining / 1000.0:F1}s";
        }
        else if (view.TimedOut)
        {
            _countdownPill.IsVisible = true;
            _countdownText.Text = view.TimeUpMessage ?? "已超时";
        }
        else
        {
            _countdownPill.IsVisible = false;
        }
    }

    private void UpdateInputToggle(bool canInput)
    {
        _inputToggle.IsEnabled = canInput;
        _inputToggle.Background = _inputVisible && canInput ? ActiveButtonBrush : IdleButtonBrush;
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

        // 提交后收起键盘：后续回合不再自动聚焦输入框。
        DismissInput();
    }

    private void OnButtonActivated(NativeButton button)
    {
        var view = _awaitingView;
        var snapshot = _snapshot;
        if (view is null || snapshot is null || !view.CanSubmitButton(button.Generation, snapshot.generation))
            return;
        if (_submittedRevision == snapshot.revision)
            return;

        if (button.Value is null)
        {
            // srcm 图片热区应走 OnImageActivated；这里只兜底协议缺值的普通按钮，
            // 显式拒绝并留下日志，避免把空字符串当成 AnyKey 推进。
            Log.Warn(LogTag, "button: value missing and no srcm hot zone hit; click ignored");
            return;
        }

        var value = button.Value.ToString() ?? string.Empty;
        _submittedRevision = snapshot.revision;
        NativeRuntime.Submit(new InputFrame(Value: value));
        _sendButton.IsEnabled = false;
        DismissInput();
        Log.Info(LogTag,
            $"button: value='{value}' isInteger={button.IsInteger} generation={button.Generation} " +
            $"revision={snapshot.revision}");
    }

    /// <summary>
    /// srcm 图片热区点击：按点击处像素色（0xRRGGBB 的十进制）提交按钮输入值。
    /// 取不到（未解码 / 越界 / 透明）时拒绝提交，并按原因写不同日志。
    /// </summary>
    private void OnImageActivated(NativeImageHitRegion region, Point point)
    {
        var view = _awaitingView;
        var snapshot = _snapshot;
        if (view is null || snapshot is null || !view.CanSubmitButton(region.Button.Generation, snapshot.generation))
            return;
        if (_submittedRevision == snapshot.revision)
            return;

        if (!_view.TryReadSrcmPixel(region, point, out var rgb, out var failure))
        {
            Log.Warn(LogTag,
                $"button: srcm rejected ({failure}) srcm='{region.Srcm}' " +
                $"at=({point.X:F1},{point.Y:F1})");
            return;
        }

        var value = rgb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _submittedRevision = snapshot.revision;
        NativeRuntime.Submit(new InputFrame(Value: value));
        _sendButton.IsEnabled = false;
        DismissInput();
        Log.Info(LogTag,
            $"button: srcm value='{value}' rgb=0x{rgb:X6} at=({point.X:F1},{point.Y:F1}) " +
            $"srcm='{region.Srcm}' generation={region.Button.Generation} revision={snapshot.revision}");
    }

    private static AvaloniaButton CreateFloatingButton(string glyph, string tip)
    {
        var button = new AvaloniaButton
        {
            Content = glyph,
            Width = FloatingButtonSize,
            Height = FloatingButtonSize,
            MinWidth = 0,
            MinHeight = 0,
            FontSize = 20,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = IdleButtonBrush,
            Foreground = new SolidColorBrush(Color.FromRgb(232, 234, 248)),
        };
        ToolTip.SetTip(button, tip);
        return button;
    }

    private static AvaloniaButton CreateMenuItem(string text, Action onClick)
    {
        var button = new AvaloniaButton
        {
            Content = text,
            FontSize = 14,
            MinHeight = 44,
            Padding = new Thickness(12, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}
