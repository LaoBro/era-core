using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaButton = Avalonia.Controls.Button;

namespace EraCore.Native.Android;

/// <summary>
/// 最小设置页（覆盖层）：字号（行高同比）、行距、主题（暗/亮）、屏幕方向。
/// 所有改动立即写入 <c>settings.json</c>，并通过 <see cref="NativeSettings.Changed"/>
/// 驱动 <see cref="NativeRenderStyle"/> / 主题 / Activity 方向，重启后保持。
/// </summary>
internal sealed class SettingsView : Border
{
    private readonly Slider _fontSlider;
    private readonly TextBlock _fontValue;
    private readonly Slider _spacingSlider;
    private readonly TextBlock _spacingValue;
    private readonly ComboBox _themeBox;
    private readonly ComboBox _orientationBox;
    private readonly TextBlock _gameInfo;

    private bool _applying;

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
        };
        stack.Children.Add(BuildSliderRow("字号", _fontSlider, _fontValue));

        _spacingValue = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(150, 158, 180)), FontSize = 12 };
        _spacingSlider = new Slider
        {
            Minimum = AppSettings.MinLineSpacing,
            Maximum = AppSettings.MaxLineSpacing,
            TickFrequency = 0.05,
            IsSnapToTickEnabled = true,
        };
        stack.Children.Add(BuildSliderRow("行距", _spacingSlider, _spacingValue));

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
        _fontSlider.ValueChanged += (_, _) => SaveFromControls();
        _spacingSlider.ValueChanged += (_, _) => SaveFromControls();
        _themeBox.SelectionChanged += (_, _) => SaveFromControls();
        _orientationBox.SelectionChanged += (_, _) => SaveFromControls();

        NativeSettings.Changed += _ => ApplyCurrent();
    }

    public event Action? CloseRequested;

    public void Show(string? gameName, string? fontName)
    {
        _gameInfo.Text = gameName is null
            ? "未打开游戏。设置会跨游戏生效。"
            : $"当前游戏：{gameName}\n配置字体名（原值）：{fontName ?? "未知"}（Android 渲染统一使用内置 CJK 字体）";
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
        _fontSlider.Value = current.FontSize;
        _spacingSlider.Value = current.LineSpacing;
        _themeBox.SelectedIndex = current.Theme == AppTheme.Light ? 1 : 0;
        _orientationBox.SelectedIndex = current.Orientation switch
        {
            AppOrientation.Portrait => 1,
            AppOrientation.Landscape => 2,
            _ => 0,
        };
        _fontValue.Text = $"{current.FontSize} px（行高同比）";
        _spacingValue.Text = $"{current.LineSpacing:F2}×";
        _applying = false;
    }

    private void SaveFromControls()
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

        NativeSettings.Update(new AppSettings(
            (int)Math.Round(_fontSlider.Value),
            _spacingSlider.Value,
            theme,
            orientation));
    }

    private static Control BuildSliderRow(string label, Slider slider, TextBlock value)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(value, 1);
        header.Children.Add(labelText);
        header.Children.Add(value);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(header);
        stack.Children.Add(slider);
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
