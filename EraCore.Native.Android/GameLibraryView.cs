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
/// 游戏库页：列出私有目录 <c>games/*</c>（已复制）与登记的原目录（零拷贝），显示来源/最后游玩时间，点击进入；
/// 支持系统目录选择器导入、zip 导入、内置示例入口。
///
/// <para>导入优先走「真实路径登记」：已授予全盘文件访问时零拷贝登记原目录；
/// 未授权时引导到系统开关页，并保留「先用复制模式导入」「稍后再说」两个出口，不卡死流程。</para>
/// </summary>
internal sealed class GameLibraryView : Border
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(12, 12, 20));
    private static readonly IBrush CardBrush = new SolidColorBrush(Color.FromRgb(28, 28, 42));
    private static readonly IBrush HighlightBrush = new SolidColorBrush(Color.FromRgb(38, 52, 82));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(150, 158, 180));
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.FromRgb(110, 170, 255));
    private static readonly IBrush WarnBrush = new SolidColorBrush(Color.FromRgb(255, 138, 128));

    private readonly StackPanel _listPanel;
    private readonly TextBlock _statusText;
    private readonly List<AvaloniaButton> _actionButtons = new();
    private readonly AvaloniaButton _cancelImportButton;
    private readonly StackPanel _permissionPanel;
    private readonly DispatcherTimer _readyTimer;

    private bool _busy;
    private CancellationTokenSource? _importCancellation;

    // 「已复制」游戏的两段式删除确认：第一次点击进入确认态，3 秒内再次点击才执行。
    private AvaloniaButton? _pendingDeleteButton;
    private string? _pendingDeletePath;
    private DispatcherTimer? _pendingDeleteTimer;

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

        _permissionPanel = BuildPermissionPanel();

        var footer = new StackPanel { Spacing = 8 };
        footer.Children.Add(_permissionPanel);
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
        ResetPendingDelete();
        _listPanel.Children.Clear();
        var context = NativeRuntime.AppContext;
        if (context is null)
        {
            _statusText.Text = "应用上下文尚未就绪…";
            return;
        }

        if (ExternalStorageAccess.IsGranted())
            _permissionPanel.IsVisible = false; // 返回系统开关页后自动收起引导

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
            _listPanel.Children.Add(BuildEntry(entry, entry.Name == highlight));

        var pathCount = entries.Count(e => e.Source == GameSourceKind.Path);
        var unavailable = entries.Count(e => !e.IsAvailable);
        var summary = pathCount == 0
            ? $"共 {entries.Count} 个游戏 · 私有目录 games/"
            : $"共 {entries.Count} 个游戏 · 原目录 {pathCount} 个";
        if (unavailable > 0)
            summary += $" · {unavailable} 个路径不可用";
        _statusText.Text = summary;
    }

    public void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        foreach (var button in _actionButtons)
            button.IsEnabled = !busy;
        if (message is not null)
            _statusText.Text = message;
    }

    /// <summary>
    /// 单个游戏卡片：名称 + 来源标签（原目录/已复制）+ 路径 + 最后游玩时间。
    /// 原目录条目右侧提供「移除记录」（只删登记，绝不动原目录）；已复制条目右侧提供「删除」
    /// （两段式确认后递归删除私有目录并清游玩记录）。路径不可用时点卡片只提示原因。
    /// </summary>
    private Control BuildEntry(GameLibraryEntry entry, bool highlighted = false)
    {
        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock
        {
            Text = entry.Name,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var badge = new TextBlock
        {
            Text = entry.SourceText,
            FontSize = 11,
            Foreground = entry.Source == GameSourceKind.Path ? AccentBrush : MutedBrush,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(name, 0);
        Grid.SetColumn(badge, 1);
        nameRow.Children.Add(name);
        nameRow.Children.Add(badge);

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
        content.Children.Add(nameRow);
        content.Children.Add(path);
        content.Children.Add(played);
        if (!entry.IsAvailable && entry.UnavailableReason is { } reason)
        {
            content.Children.Add(new TextBlock
            {
                Text = "路径不可用：" + reason,
                FontSize = 12,
                Foreground = WarnBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        var button = new AvaloniaButton
        {
            Content = content,
            Background = highlighted ? HighlightBrush : CardBrush,
            BorderBrush = highlighted ? AccentBrush : null,
            BorderThickness = highlighted ? new Thickness(1) : new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10),
            Opacity = entry.IsAvailable ? 1.0 : 0.75,
        };
        button.Click += (_, _) =>
        {
            if (_busy)
                return;
            if (!entry.IsAvailable)
            {
                _statusText.Text = $"{entry.Name} 不可用：{entry.UnavailableReason ?? "目录不可用"}";
                return;
            }
            GameSelected?.Invoke(entry);
        };

        // 右侧独立操作按钮（不嵌套在卡片按钮里，避免点操作时同时进游戏）。
        var action = entry.Source == GameSourceKind.Path
            ? BuildRemoveRecordButton(entry)
            : BuildDeleteButton(entry);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(button, 0);
        Grid.SetColumn(action, 1);
        row.Children.Add(button);
        row.Children.Add(action);
        return row;
    }

    /// <summary>原目录条目：只移除登记记录，绝不删除原目录。</summary>
    private AvaloniaButton BuildRemoveRecordButton(GameLibraryEntry entry)
    {
        var remove = new AvaloniaButton
        {
            Content = "移除记录",
            FontSize = 12,
            MinHeight = 40,
            Padding = new Thickness(10, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        remove.Click += (_, _) =>
        {
            if (_busy)
                return;
            var removed = GameLibrary.RemovePathSource(entry.RootPath);
            Refresh();
            _statusText.Text = removed
                ? $"已移除记录：{entry.Name}（原目录未删除）"
                : "移除失败：记录不存在。";
        };
        return remove;
    }

    /// <summary>
    /// 已复制条目：两段式确认后删除私有目录。
    /// 第一次点击把按钮文案改为「确认删除？」并在 3 秒后自动复位；再次点击才真正删除。
    /// 没有 MessageBox 依赖，也不会因列表中有多个删除按钮而误删别的条目。
    /// </summary>
    private AvaloniaButton BuildDeleteButton(GameLibraryEntry entry)
    {
        var delete = new AvaloniaButton
        {
            Content = "删除",
            FontSize = 12,
            MinHeight = 40,
            Padding = new Thickness(10, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        delete.Click += async (_, _) =>
        {
            if (_busy)
                return;
            if (!ArmDelete(delete, entry))
                return;

            ResetPendingDelete();
            var context = NativeRuntime.AppContext;
            if (context is null)
            {
                _statusText.Text = "应用上下文尚未就绪…";
                return;
            }

            delete.IsEnabled = false;
            SetBusy(true, $"正在删除 {entry.Name}…");
            try
            {
                var result = await Task.Run(
                    () => GameLibrary.DeleteInternalGame(context, entry)).ConfigureAwait(true);
                Refresh();
                if (result.Success)
                {
                    _statusText.Text = $"已删除：{entry.Name}（私有目录与游玩记录已清理）"
                        + (GameLibrary.IsBuiltInGame(entry.Name)
                            ? "；内置示例将在下次启动时重新解压"
                            : string.Empty);
                }
                else
                {
                    _statusText.Text = "删除失败：" + (result.Error ?? "未知错误");
                }
            }
            catch (Exception ex)
            {
                Refresh();
                _statusText.Text = "删除失败：" + ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        };
        return delete;
    }

    /// <summary>进入/切换删除确认态；同一按钮第二次点击返回 true。</summary>
    private bool ArmDelete(AvaloniaButton button, GameLibraryEntry entry)
    {
        if (_pendingDeletePath == entry.RootPath)
            return true;

        ResetPendingDelete();
        _pendingDeletePath = entry.RootPath;
        _pendingDeleteButton = button;
        button.Content = "确认删除？";
        _statusText.Text = $"再次点击「确认删除？」将删除 {entry.Name}（3 秒内有效）";
        _pendingDeleteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _pendingDeleteTimer.Tick += (_, _) => ResetPendingDelete();
        _pendingDeleteTimer.Start();
        return false;
    }

    /// <summary>复位确认态：恢复文案并停止计时器。</summary>
    private void ResetPendingDelete()
    {
        _pendingDeleteTimer?.Stop();
        _pendingDeleteTimer = null;
        if (_pendingDeleteButton is not null)
            _pendingDeleteButton.Content = "删除";
        _pendingDeleteButton = null;
        _pendingDeletePath = null;
    }

    /// <summary>
    /// 未授权时的引导面板：去系统开关页、先用复制模式、稍后再说。
    /// 面板常驻但在未点击「导入目录」前不主动弹出，避免每次刷新都打断。
    /// </summary>
    private StackPanel BuildPermissionPanel()
    {
        var panel = new StackPanel { Spacing = 8, IsVisible = false };
        panel.Children.Add(new TextBlock
        {
            Text = "直接登记原目录（零拷贝）需要「所有文件访问」权限。未授权时仍可用复制模式导入。",
            FontSize = 12,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        var grant = new AvaloniaButton
        {
            Content = "去系统设置授权",
            FontSize = 13,
            MinHeight = 40,
            Padding = new Thickness(14, 2),
            Margin = new Thickness(0, 0, 8, 0),
        };
        grant.Click += async (_, _) =>
        {
            if (_busy)
                return;
            await RunBusyAsync(async () =>
            {
                await ExternalStorageAccess.RequestAsync().ConfigureAwait(true);
                Refresh();
                _statusText.Text = ExternalStorageAccess.IsGranted()
                    ? "已授权：再次点「导入目录」即可零拷贝登记原目录。"
                    : "尚未授权：请开启「所有文件访问」后返回，或先用复制模式导入。";
            }).ConfigureAwait(true);
        };

        var copy = new AvaloniaButton
        {
            Content = "先用复制模式导入",
            FontSize = 13,
            MinHeight = 40,
            Padding = new Thickness(14, 2),
            Margin = new Thickness(0, 0, 8, 0),
        };
        copy.Click += async (_, _) =>
        {
            if (_busy)
                return;
            _permissionPanel.IsVisible = false;
            await RunBusyAsync(() => CopyFolderImportAsync()).ConfigureAwait(true);
        };

        var later = new AvaloniaButton
        {
            Content = "稍后再说",
            FontSize = 13,
            MinHeight = 40,
            Padding = new Thickness(14, 2),
        };
        later.Click += (_, _) =>
        {
            _permissionPanel.IsVisible = false;
            _statusText.Text = "已跳过授权：可随时再点「导入目录」或使用复制模式。";
        };

        row.Children.Add(grant);
        row.Children.Add(copy);
        row.Children.Add(later);
        panel.Children.Add(row);
        return panel;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        SetBusy(true, "处理中…");
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
    /// 「导入目录」入口：已授权全盘访问 → 系统选择器 + tree URI 映射为真实路径做零拷贝登记；
    /// 未授权 → 显示授权引导（去授权 / 先用复制模式 / 稍后再说），不阻塞流程。
    /// </summary>
    private async Task ImportFolderAsync()
    {
        if (!ExternalStorageAccess.IsGranted())
        {
            _permissionPanel.IsVisible = true;
            _statusText.Text = "未授予「所有文件访问」：可直接用复制模式导入，或先授权后登记原目录。";
            return;
        }

        _permissionPanel.IsVisible = false;
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

        if (!ExternalStorageAccess.IsGranted())
        {
            _permissionPanel.IsVisible = true;
            _statusText.Text = "授权状态已变化：请重新授权后再试，或使用复制模式。";
            return;
        }

        if (RealPathResolver.TryResolve(treeUri, out var realPath, out var reason))
        {
            _statusText.Text = "正在登记原目录…";
            var result = GameLibrary.RegisterPathSource(context, realPath);
            if (result.Entry is not null)
            {
                Refresh(result.Entry.Name);
                _statusText.Text = $"已登记原目录（零拷贝）：{realPath}";
                return;
            }

            Refresh();
            _statusText.Text = (result.Error ?? "登记失败。") + " 可改用复制模式导入。";
            return;
        }

        var shortReason = DescribeRealPathFailure(reason);
        global::Android.Util.Log.Info("EraCoreNative",
            $"real path unavailable ({reason}), fallback to SAF copy");
        _statusText.Text = $"该位置无法直接读取（{shortReason}），改用复制模式…";
        await CopyFolderImportAsync(treeUri, shortReason).ConfigureAwait(true);
    }

    /// <summary>把 <see cref="RealPathResolver"/> 的失败原因码翻成状态栏可读的短文案。</summary>
    private static string DescribeRealPathFailure(string reason)
    {
        if (reason.StartsWith("volume-unmapped:", StringComparison.Ordinal))
            return "存储卷无法映射为真实路径";
        if (reason.StartsWith("provider=", StringComparison.Ordinal))
            return "该来源不支持直接读取";
        return reason switch
        {
            "missing-or-forbidden" => "目录不可读或权限受限",
            "unreadable" => "目录无法枚举",
            "android-restricted-dir" => "Android/data 受系统保护",
            "bad-document-id" => "目录标识无法解析",
            "document-id-unavailable" => "目录标识缺失",
            _ => reason,
        };
    }

    /// <summary>
    /// SAF 复制模式：ActivityResult 回调 + DocumentsContract 递归复制到私有目录。
    /// 不用 Avalonia 的 IStorageProvider：其 Android 目录枚举会在部分设备的权限确认框后丢失回调。
    /// </summary>
    private async Task CopyFolderImportAsync(
        global::Android.Net.Uri? picked = null,
        string? fallbackReason = null)
    {
        var context = NativeRuntime.AppContext;
        if (context is null)
        {
            _statusText.Text = "应用上下文尚未就绪。";
            return;
        }

        var treeUri = picked;
        if (treeUri is null)
        {
            _statusText.Text = "正在打开系统目录选择器…";
            treeUri = await SafFolderPicker.PickAsync().ConfigureAwait(true);
            if (treeUri is null)
            {
                _statusText.Text = "已取消导入。";
                return;
            }
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
        if (result.Entry is not null)
        {
            var note = fallbackReason is null
                ? string.Empty
                : $"；该位置无法直接读取（{fallbackReason}），已复制";
            _statusText.Text = $"已复制导入 {result.Entry.Name}（{result.Entry.RootPath}）{note}";
        }
        else
        {
            _statusText.Text = result.Error ?? "导入失败。";
        }
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
