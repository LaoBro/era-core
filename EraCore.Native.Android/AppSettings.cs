using System;
using System.IO;
using System.Text.Json;
using Android.Content;
using Android.Util;

namespace EraCore.Native.Android;

internal enum AppTheme
{
    Dark,
    Light,
}

internal enum AppOrientation
{
    Follow,
    Portrait,
    Landscape,
}

/// <summary>原生前端的全局显示设置（与游戏 <c>emuera.config</c> 解耦，跨游戏共用）。</summary>
internal sealed record AppSettings(
    int FontSize,
    double LineSpacing,
    AppTheme Theme,
    AppOrientation Orientation)
{
    public const int MinFontSize = 10;
    public const int MaxFontSize = 36;
    public const double MinLineSpacing = 0.8;
    public const double MaxLineSpacing = 2.0;

    public static AppSettings Default { get; } = new(18, 1.0, AppTheme.Dark, AppOrientation.Follow);

    public AppSettings Normalize() => new(
        Math.Clamp(FontSize, MinFontSize, MaxFontSize),
        Math.Clamp(LineSpacing, MinLineSpacing, MaxLineSpacing),
        Theme,
        Orientation);

}

/// <summary>settings.json 的持久化形状（字段名小写，缺失字段回退默认值）。</summary>
internal sealed record SettingsDto(
    int? fontSize,
    double? lineSpacing,
    string? theme,
    string? orientation);

/// <summary>
/// 设置读写（应用私有目录 <c>settings.json</c>）。改动立即落盘，进程重启后恢复；
/// 变更通过 <see cref="Changed"/> 通知 UI 重新应用渲染参数与主题/方向。
/// </summary>
internal static class NativeSettings
{
    private const string LogTag = "EraCoreNative";
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static string? _path;
    private static AppSettings _current = AppSettings.Default;

    public static event Action<AppSettings>? Changed;

    public static AppSettings Current => _current;

    public static void Initialize(Context context)
    {
        var filesDir = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android FilesDir 不可用。");
        _path = Path.Combine(filesDir, FileName);
        _current = Load();
        Log.Info(LogTag,
            $"settings loaded: font={_current.FontSize} spacing={_current.LineSpacing:F2} " +
            $"theme={_current.Theme} orientation={_current.Orientation}");
    }

    public static void Update(AppSettings value)
    {
        var normalized = value.Normalize();
        if (normalized == _current)
            return;
        _current = normalized;
        Save();
        Log.Info(LogTag,
            $"settings changed: font={normalized.FontSize} spacing={normalized.LineSpacing:F2} " +
            $"theme={normalized.Theme} orientation={normalized.Orientation}");
        Changed?.Invoke(normalized);
    }

    private static AppSettings Load()
    {
        if (_path is null || !File.Exists(_path))
            return AppSettings.Default;
        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(_path), JsonOptions);
            if (dto is null)
                return AppSettings.Default;

            var theme = Enum.TryParse<AppTheme>(dto.theme, ignoreCase: true, out var parsedTheme)
                ? parsedTheme
                : AppSettings.Default.Theme;
            var orientation = Enum.TryParse<AppOrientation>(dto.orientation, ignoreCase: true, out var parsedOrientation)
                ? parsedOrientation
                : AppSettings.Default.Orientation;

            return new AppSettings(
                dto.fontSize ?? AppSettings.Default.FontSize,
                dto.lineSpacing ?? AppSettings.Default.LineSpacing,
                theme,
                orientation).Normalize();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "settings read failed, using defaults: " + ex.Message);
            return AppSettings.Default;
        }
    }

    private static void Save()
    {
        if (_path is null)
            return;
        try
        {
            var dto = new SettingsDto(
                _current.FontSize,
                _current.LineSpacing,
                _current.Theme.ToString(),
                _current.Orientation.ToString());
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "settings write failed: " + ex.Message);
        }
    }
}
