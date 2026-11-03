using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Android.Content;
using Android.Util;

namespace EraCore.Native.Android;

/// <summary>游戏来源：<see cref="Internal"/> 复制/解压到私有目录；<see cref="Path"/> 登记的原目录（零拷贝）。</summary>
internal enum GameSourceKind
{
    Internal,
    Path,
}

/// <summary>settings.json 中持久化的游戏来源记录（仅原目录需要登记；私有目录条目由扫描推导）。</summary>
internal sealed record GameSourceRecord(string RootPath, GameSourceKind Kind);

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
    string? orientation,
    List<GameSourceDto>? gameSources);

/// <summary>来源记录的持久化形状。</summary>
internal sealed record GameSourceDto(string? path, string? kind);

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
    private static readonly object SourcesGate = new();
    private static readonly List<GameSourceRecord> Sources = new();

    public static event Action<AppSettings>? Changed;

    /// <summary>来源登记变化（登记/移除原目录），库页据此刷新。</summary>
    public static event Action? SourcesChanged;

    public static AppSettings Current => _current;

    /// <summary>已登记的原目录（私有目录来源不在此列，由 <see cref="GameLibrary.Scan"/> 扫描得到）。</summary>
    public static IReadOnlyList<GameSourceRecord> GameSources
    {
        get
        {
            lock (SourcesGate)
                return Sources.ToArray();
        }
    }

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

    public static bool AddPathSource(string rootPath)
    {
        lock (SourcesGate)
        {
            if (Sources.Any(s => string.Equals(s.RootPath, rootPath, StringComparison.Ordinal)))
                return false;
            Sources.Add(new GameSourceRecord(rootPath, GameSourceKind.Path));
            Save();
        }
        Log.Info(LogTag, $"game source added: kind=path path={rootPath}");
        SourcesChanged?.Invoke();
        return true;
    }

    public static bool RemovePathSource(string rootPath)
    {
        lock (SourcesGate)
        {
            var removed = Sources.RemoveAll(s => string.Equals(s.RootPath, rootPath, StringComparison.Ordinal)) > 0;
            if (!removed)
                return false;
            Save();
        }
        Log.Info(LogTag, $"game source removed: kind=path path={rootPath}");
        SourcesChanged?.Invoke();
        return true;
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

            lock (SourcesGate)
            {
                Sources.Clear();
                foreach (var source in dto.gameSources ?? [])
                {
                    var path = source.path;
                    if (string.IsNullOrWhiteSpace(path))
                        continue;
                    if (Enum.TryParse<GameSourceKind>(source.kind, ignoreCase: true, out var kind)
                        && kind != GameSourceKind.Path)
                        continue; // 私有目录来源不落盘，扫描时推导
                    Sources.Add(new GameSourceRecord(path, GameSourceKind.Path));
                }
                Log.Info(LogTag, $"game sources loaded: path={Sources.Count}");
            }

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
            List<GameSourceDto> sources;
            lock (SourcesGate)
                sources = Sources.Select(s => new GameSourceDto(s.RootPath, s.Kind.ToString())).ToList();

            var dto = new SettingsDto(
                _current.FontSize,
                _current.LineSpacing,
                _current.Theme.ToString(),
                _current.Orientation.ToString(),
                sources);
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "settings write failed: " + ex.Message);
        }
    }
}
