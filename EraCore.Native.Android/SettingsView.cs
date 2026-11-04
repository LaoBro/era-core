using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EraCore.Native;
using MinorShift.Emuera.Sessions;
using AvaloniaButton = Avalonia.Controls.Button;

namespace EraCore.Native.Android;

/// <summary>
/// 最小设置页（覆盖层）：字号（默认跟随游戏）、主题（暗/亮）、屏幕方向。
///
/// <para>字号有两种状态：跟随游戏（默认）或用户绝对值。跟随状态下实际字号取
/// <see cref="HostMetrics.FontSize"/>，界面显示「跟随游戏（16）」；拖动滑块才写入绝对值并关闭跟随，
/// 「恢复跟随」按钮可一键回到游戏字号。WinForms 没有行距倍率，因此本页不提供行距控件。</para>
///
/// <para>所有改动立即写入 <c>settings.json</c>，并通过 <see cref="NativeSettings.Changed"/>
/// 驱动 <see cref="NativeRenderStyle"/> / 主题 / Activity 方向，重启后保持。</para>
/// </summary>
internal sealed class SettingsView : Border
{
    private readonly Slider _fontSlider;
    private readonly TextBlock _fontValue;
    private readonly AvaloniaButton _followButton;
    private readonly ComboBox _themeBox;
    private readonly ComboBox _orientationBox;
    private readonly TextBlock _gameInfo;

    private bool _applying;
    private HostMetrics? _metrics;

    public SettingsView()
    {
        Background = new SolidColorBrush(Color.FromArgb(220, 0, 0, 0));
        IsVisible = false;
        ApplyInsets();

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 42)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14),
            Margin = new Thickness(12),
            MaxWidth = 460,
            MaxHeight = 400,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var stack = new StackPanel { Spacing = 10 };

        var title = new TextBlock
        {
            Text = "设置",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
        };
        stack.Children.Add(title);

        _fontValue = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(150, 158, 180)), FontSize = 12 };
        _fontSlider = new Slider
        {
            Minimum = AppSettings.MinFontSize,
            Maximum = AppSettings.MaxFontSize,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _followButton = new AvaloniaButton
        {
            Content = "恢复跟随",
            FontSize = 12,
            MinHeight = 32,
            Padding = new Thickness(10, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(BuildFontRow());

        _themeBox = new ComboBox
        {
            ItemsSource = new[] { "暗色", "亮色" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        stack.Children.Add(BuildComboRow("主题", _themeBox));

        _orientationBox = new ComboBox
        {
            ItemsSource = new[] { "跟随系统", "竖屏", "横屏" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        stack.Children.Add(BuildComboRow("屏幕方向", _orientationBox));

        _gameInfo = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 148, 172)),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(_gameInfo);

        var closeButton = new AvaloniaButton
        {
            Content = "关闭",
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 10, 0, 0),
        };
        closeButton.Click += (_, _) => CloseRequested?.Invoke();

        // 横屏时可视高度有限：内容区滚动，关闭按钮固定在卡片底部。
        var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(closeButton, 1);
        layout.Children.Add(scroll);
        layout.Children.Add(closeButton);

        card.Child = layout;
        Child = card;

        ApplyCurrent();
        _fontSlider.ValueChanged += (_, _) => OnFontSliderChanged();
        _followButton.Click += (_, _) => ReturnToFollow();
        _themeBox.SelectionChanged += (_, _) => SaveThemeAndOrientation();
        _orientationBox.SelectionChanged += (_, _) => SaveThemeAndOrientation();

        NativeSettings.Changed += _ => ApplyCurrent();
    }

    public event Action? CloseRequested;

    /// <summary>打开设置页；<paramref name="metrics"/> 为当前游戏的 <c>emuera.config</c> 度量（未开游戏为 null）。</summary>
    public void Show(string? gameName, string? fontName, HostMetrics? metrics)
    {
        _metrics = metrics;
        _gameInfo.Text = gameName is null
            ? "未打开游戏。字号默认跟随游戏配置，设置会跨游戏生效。"
            : $"当前游戏：{gameName}\n" +
              $"游戏配置：字号 {metrics?.FontSize ?? 0} / 行高 {metrics?.LineHeight ?? 0}（{fontName ?? "未知"}）\n" +
              "Android 渲染统一使用内置 CJK 字体；行高严格等于游戏下发行高。";
        ApplyCurrent();
        IsVisible = true;
    }

    public void Hide() => IsVisible = false;

    /// <summary>覆盖层让开状态栏/导航栏；转屏后导航栏换边时也要更新。</summary>
    private void ApplyInsets()
    {
        var insets = AndroidInsets.SystemBars();
        var padding = new Thickness(
            8 + insets.Left,
            8 + insets.Top,
            8 + insets.Right,
            8 + insets.Bottom);
        if (Padding != padding)
            Padding = padding;
    }

    /// <summary>由 <see cref="NativeShell"/> 的低频轮询调用。</summary>
    internal void RefreshInsets() => ApplyInsets();

    private void ApplyCurrent()
    {
        _applying = true;
        var current = NativeSettings.Current;
        var gameFont = _metrics?.FontSize ?? 0;
        var effective = NativeTextLayout.EffectiveFontSize(
            current.FollowGameFontSize,
            current.FontSize,
            gameFont);

        // 跟随状态下界面展示的是「游戏实际字号」，不是存下来的绝对值；
        // 滑块只钳到控件范围，文字标签始终显示真实状态，避免「显示 18、实际 16」。
        _fontSlider.Value = Math.Clamp(effective, AppSettings.MinFontSize, AppSettings.MaxFontSize);
        _themeBox.SelectedIndex = current.Theme == AppTheme.Light ? 1 : 0;
        _orientationBox.SelectedIndex = current.Orientation switch
        {
            AppOrientation.Portrait => 1,
            AppOrientation.Landscape => 2,
            _ => 0,
        };

        if (current.FollowGameFontSize)
        {
            _fontValue.Text = gameFont > 0 ? $"跟随游戏（{gameFont}）" : "跟随游戏（未打开）";
            _followButton.Content = "已跟随";
            _followButton.IsEnabled = false;
        }
        else
        {
            _fontValue.Text = $"{current.FontSize} px（行高同比）";
            _followButton.Content = "恢复跟随";
            _followButton.IsEnabled = true;
        }

        _applying = false;
    }

    /// <summary>用户拖动字号 = 明确写入绝对值，同时关闭「跟随游戏」。</summary>
    private void OnFontSliderChanged()
    {
        if (_applying)
            return;

        var current = NativeSettings.Current;
        var fontSize = (int)Math.Round(_fontSlider.Value);
        if (!current.FollowGameFontSize && current.FontSize == fontSize)
            return;

        NativeSettings.Update(current with
        {
            FollowGameFontSize = false,
            FontSize = fontSize,
        });
    }

    private void ReturnToFollow()
    {
        if (_applying)
            return;

        var current = NativeSettings.Current;
        if (current.FollowGameFontSize)
            return;

        NativeSettings.Update(current with { FollowGameFontSize = true });
    }

    /// <summary>主题/方向只改对应字段，保留字号状态（不能因为切换主题而退出跟随）。</summary>
    private void SaveThemeAndOrientation()
    {
        if (_applying)
            return;

        var theme = _themeBox.SelectedIndex == 1 ? AppTheme.Light : AppTheme.Dark;
        var orientation = _orientationBox.SelectedIndex switch
        {
            1 => AppOrientation.Portrait,
            2 => AppOrientation.Landscape,
            _ => AppOrientation.Follow,
        };

        var current = NativeSettings.Current;
        if (current.Theme == theme && current.Orientation == orientation)
            return;

        NativeSettings.Update(current with { Theme = theme, Orientation = orientation });
    }

    private Control BuildFontRow()
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var labelText = new TextBlock
        {
            Text = "字号",
            FontSize = 13,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(_fontValue, 1);
        header.Children.Add(labelText);
        header.Children.Add(_fontValue);

        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_fontSlider, 0);
        Grid.SetColumn(_followButton, 1);
        controls.Children.Add(_fontSlider);
        controls.Children.Add(_followButton);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(header);
        stack.Children.Add(controls);
        return stack;
    }

    private static Control BuildComboRow(string label, ComboBox box)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = Brushes.White,
        });
        stack.Children.Add(box);
        return stack;
    }
}
