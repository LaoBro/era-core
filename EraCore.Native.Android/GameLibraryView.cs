using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaButton = Avalonia.Controls.Button;

namespace EraCore.Native.Android;

/// <summary>
/// 游戏库页：列出私有目录 <c>games/*</c>（名称 / 路径 / 最后游玩时间），点击进入；
/// 支持 Avalonia <see cref="IStorageProvider"/> 导入目录或 zip，以及「内置示例」入口。
///
/// <para>SAF/系统文件选择器在模拟器上可能不可用：此时按钮禁用并提示，仍可用内置示例。
/// 导入采用「复制到私有目录」，保证引擎有可写存档路径。</para>
/// </summary>
internal sealed class GameLibraryView : Border
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(12, 12, 20));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.FromRgb(28, 28, 42));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(38, 52, 82));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(150, 158, 180));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(110, 170, 255));

    private readonly StackPanel _listPanel;
    private readonly TextBlock _statusText;
    private readonly List<AvaloniaButton> _actionButtons = new();
    private readonly AvaloniaButton _cancelImportButton;
    private readonly DispatcherTimer _readyTimer;

    private bool _busy;
    private CancellationTokenSource? _importCancellation;

    public GameLibraryView()
    {
        Background = BackgroundBrush;
        ApplyInsets();

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };

        var title = new TextBlock
        {
            Text = "era-core 游戏库",
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var settingsButton = new AvaloniaButton
        {
            Content = "设置",
            FontSize = 12,
            MinHeight = 32,
            Padding = new Thickness(12, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        settingsButton.Click += (_, _) => SettingsRequested?.Invoke();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(title, 0);
        Grid.SetColumn(settingsButton, 1);
        header.Children.Add(title);
        header.Children.Add(settingsButton);
        Grid.SetRow(header, 0);

        _listPanel = new StackPanel { Spacing = 8 };
        var scroll = new ScrollViewer
        {
            Content = _listPanel,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 12, 0, 12),
        };
        Grid.SetRow(scroll, 1);

        _statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 18,
        };

        var buttons = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        AddAction(buttons, "导入目录", ImportFolderAsync);
        AddAction(buttons, "导入压缩包", ImportZipAsync);
        AddAction(buttons, "内置示例", ImportBuiltInAsync);
        AddAction(buttons, "刷新", () => { Refresh(); return Task.CompletedTask; });

        // 取消按钮只在导入期间可见且保持可点（不放进 _actionButtons，避免被 SetBusy 禁用）。
        _cancelImportButton = new AvaloniaButton
        {
            Content = "取消导入",
            FontSize = 13,
            MinHeight = 40,
            Padding = new Thickness(14, 2),
            Margin = new Thickness(0, 0, 8, 0),
            IsVisible = false,
        };
        _cancelImportButton.Click += (_, _) =>
        {
            var cancellation = _importCancellation;
            if (cancellation is null || cancellation.IsCancellationRequested)
                return;
            cancellation.Cancel();
            _cancelImportButton.IsEnabled = false;
            _statusText.Text = "正在取消导入…";
        };
        buttons.Children.Add(_cancelImportButton);

        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(buttons);
        footer.Children.Add(_statusText);
        Grid.SetRow(footer, 2);

        grid.Children.Add(header);
        grid.Children.Add(scroll);
        grid.Children.Add(footer);
        Child = grid;

        _readyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _readyTimer.Tick += (_, _) =>
        {
            if (!IsVisible)
                return;
            if (NativeRuntime.IsReady)
            {
                _readyTimer.Stop();
                Refresh();
            }
            else
            {
                _statusText.Text = NativeRuntime.Status;
            }
        };

        Refresh();
        _readyTimer.Start();
    }

    public event Action<GameLibraryEntry>? GameSelected;

    public event Action? SettingsRequested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyInsets();
    }

    /// <summary>状态栏/导航栏让位（横屏时右侧导航栏也要避开）。</summary>
    private void ApplyInsets()
    {
        var insets = AndroidInsets.SystemBars();
        var padding = new Thickness(
            16 + insets.Left,
            12 + insets.Top,
            16 + insets.Right,
            12 + insets.Bottom);
        if (Padding != padding)
            Padding = padding;
    }

    /// <summary>由 <see cref="NativeShell"/> 的低频轮询调用：转屏后导航栏换边/显隐时重新让位。</summary>
    internal void RefreshInsets() => ApplyInsets();

    /// <param name="highlight">导入成功后高亮的条目名（让新游戏在列表中一眼可见）。</param>
    public void Refresh(string? highlight = null)
    {
        _listPanel.Children.Clear();
        var context = NativeRuntime.AppContext;
        if (context is null)
        {
            _statusText.Text = "应用上下文尚未就绪…";
            return;
        }

        var entries = GameLibrary.Scan(context);
        if (entries.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = NativeRuntime.IsReady
                    ? "还没有游戏。用下方「导入目录 / 导入压缩包」添加，或点「内置示例」。"
                : "正在解压内置示例…",
                Foreground = MutedBrush,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 20, 0, 0),
            };
            _listPanel.Children.Add(empty);
            _statusText.Text = "共 0 个游戏";
            return;
        }

        foreach (var entry in entries)
            _listPanel.Children.Add(BuildEntryButton(entry, entry.Name == highlight));

        _statusText.Text = $"共 {entries.Count} 个游戏 · 私有目录 games/";
    }

    public void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        foreach (var button in _actionButtons)
            button.IsEnabled = !busy;
        if (message is not null)
            _statusText.Text = message;
    }

    private AvaloniaButton BuildEntryButton(GameLibraryEntry entry, bool highlighted = false)
    {
        var name = new TextBlock
        {
            Text = entry.Name,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
        };
        var path = new TextBlock
        {
            Text = entry.RootPath,
            FontSize = 11,
            Foreground = MutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };
        var played = new TextBlock
        {
            Text = "最后游玩：" + entry.LastPlayedText,
            FontSize = 12,
            Foreground = AccentBrush,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var content = new StackPanel { Spacing = 0 };
        content.Children.Add(name);
        content.Children.Add(path);
        content.Children.Add(played);

        var button = new AvaloniaButton
        {
            Content = content,
            Background = highlighted ? HighlightBrush : CardBrush,
            BorderBrush = highlighted ? AccentBrush : null,
            BorderThickness = highlighted ? new Thickness(1) : new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10),
        };
        button.Click += (_, _) =>
        {
            if (_busy)
                return;
            GameSelected?.Invoke(entry);
        };
        return button;
    }

    private void AddAction(WrapPanel panel, string text, Func<Task> action)
    {
        var button = new AvaloniaButton
        {
            Content = text,
            FontSize = 13,
            MinHeight = 40,
            Padding = new Thickness(14, 2),
            Margin = new Thickness(0, 0, 8, 0),
        };
        button.Click += async (_, _) =>
        {
            if (_busy)
                return;
            SetBusy(true, text + "…");
            try
            {
                await action().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _statusText.Text = "操作失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        };
        _actionButtons.Add(button);
        panel.Children.Add(button);
    }

    /// <summary>
    /// 原生 SAF 导入：ActivityResult 回调 + DocumentsContract 递归复制。
    /// 不用 Avalonia 的 IStorageProvider：其 Android 目录枚举会在部分设备的权限确认框后丢失回调。
    /// </summary>
    private async Task ImportFolderAsync()
    {
        var context = NativeRuntime.AppContext;
        if (context is null)
        {
            _statusText.Text = "应用上下文尚未就绪。";
            return;
        }

        _statusText.Text = "正在打开系统目录选择器…";
        var treeUri = await SafFolderPicker.PickAsync().ConfigureAwait(true);
        if (treeUri is null)
        {
            _statusText.Text = "已取消导入。";
            return;
        }

        var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation;
        _cancelImportButton.IsVisible = true;
        _cancelImportButton.IsEnabled = true;
        _statusText.Text = "正在扫描所选目录…";

        GameImportResult result;
        try
        {
            result = await GameLibrary.ImportSafTreeAsync(
                context,
                treeUri,
                progress => Dispatcher.UIThread.Post(() => UpdateImportProgress(progress)),
                cancellation.Token).ConfigureAwait(true);
        }
        finally
        {
            // 先置空再改结果文字：迟到的进度 Post 看到空 CTS 会丢弃，不会覆盖最终状态。
            _importCancellation = null;
            cancellation.Dispose();
            _cancelImportButton.IsVisible = false;
        }

        Refresh(result.Entry?.Name);
        _statusText.Text = result.Entry is not null
            ? $"已导入 {result.Entry.Name}（{result.Entry.RootPath}）"
            : result.Error ?? "导入失败。";
    }

    /// <summary>进度回调（UI 线程）：合并为「文件数 / 速率 / ETA / 当前文件」。</summary>
    private void UpdateImportProgress(GameLibrary.SafImportProgress progress)
    {
        if (_importCancellation is null)
            return; // 导入已结束：丢弃迟到进度
        var speed = progress.BytesPerSecond >= 1024 * 1024
            ? $"{progress.BytesPerSecond / (1024 * 1024):F1} MB/s"
            : $"{progress.BytesPerSecond / 1024:F0} KB/s";
        var remaining = progress.Remaining is { } left
            ? $"剩余约 {Math.Max(0, (int)Math.Ceiling(left.TotalSeconds))}s"
            : "剩余计算中";
        var name = progress.CurrentName.Length == 0 ? string.Empty : "：" + progress.CurrentName;
        _statusText.Text = $"复制中 {progress.CopiedFiles}/{progress.TotalFiles}（{speed}，{remaining}）{name}";
    }

    private async Task ImportZipAsync()
    {
        var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (provider is null || !provider.CanOpen)
        {
            _statusText.Text = "当前设备不支持系统文件选择；请用「内置示例」，或稍后在真机验证 SAF 导入。";
            return;
        }

        global::Android.Util.Log.Info("EraCoreNative", "storage picker: OpenFilePickerAsync begin (zip)");
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 emuera 游戏压缩包",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("压缩包")
                {
                    Patterns = new[] { "*.zip" },
                    MimeTypes = new[] { "application/zip", "application/x-zip-compressed" },
                },
            },
        }).ConfigureAwait(true);
        global::Android.Util.Log.Info("EraCoreNative", $"storage picker: returned {files.Count} file(s) (zip)");
        var file = files.FirstOrDefault();
        if (file is null)
        {
            global::Android.Util.Log.Info("EraCoreNative", "storage picker: cancelled (no zip)");
            return;
        }
        try
        {
            global::Android.Util.Log.Info("EraCoreNative", $"storage picker: zip name={file.Name}");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("EraCoreNative", "storage picker: zip name failed: " + ex.Message);
        }

        var context = NativeRuntime.AppContext;
        if (context is null)
        {
            _statusText.Text = "应用上下文尚未就绪。";
            return;
        }

        _statusText.Text = "正在解压…";
        var result = await GameLibrary.ImportZipAsync(context, file).ConfigureAwait(true);
        Refresh(result.Entry?.Name);
        _statusText.Text = result.Entry is not null
            ? $"已导入 {result.Entry.Name}（{result.Entry.RootPath}）"
            : result.Error ?? "导入失败。";
    }

    private async Task ImportBuiltInAsync()
    {
        var context = NativeRuntime.AppContext;
        if (context is null)
        {
            _statusText.Text = "应用上下文尚未就绪。";
            return;
        }

        await GameLibrary.EnsureBuiltInGamesAsync(context).ConfigureAwait(true);
        _statusText.Text = "内置示例已解压到 games/。";
        Refresh();
    }
}
