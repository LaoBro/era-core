using System;
using System.Collections.Concurrent;
using Android.Content;
using Android.Util;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// 内置 CJK 字体与按码点回退。
///
/// <para>Android 没有 <c>ＭＳ ゴシック</c>，且系统字体并不保证覆盖游戏的日文/中文码位。
/// 这里加载 APK 内置的子集字体（Noto Sans SC，按 JIS X 0208 Level-1 + 实际用字裁剪，
/// 见 <c>tools/fonts</c> 或 M3 汇报的体积方案）；内置字体缺字时按码点回退到系统字体。</para>
///
/// <para><see cref="MinorShift.Emuera.Sessions.HostMetrics.FontName"/> 的原值不被改写，
/// 只用于设置页/日志展示。</para>
/// </summary>
internal static class NativeFontManager
{
    private const string LogTag = "EraCoreNative";
    private const string AssetPath = "fonts/EraCoreCJK.ttf";

    private static SKTypeface? _embedded;
    private static readonly ConcurrentDictionary<char, SKTypeface> Resolved = new();

    /// <summary>内置字体（加载失败时退回系统默认，但仍保证非 null）。</summary>
    public static SKTypeface Embedded => _embedded ?? SKTypeface.Default;

    public static void Initialize(Context context)
    {
        if (_embedded is not null)
            return;

        try
        {
            using var stream = context.Assets!.Open(AssetPath);
            _embedded = SKTypeface.FromStream(stream);
            Log.Info(LogTag,
                _embedded is null
                    ? $"embedded font load failed: {AssetPath}"
                    : $"embedded font: {_embedded.FamilyName} family={_embedded.FamilyName}#{_embedded.FontStyle}");
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"embedded font unavailable ({AssetPath}): {ex.Message}");
        }

        _embedded ??= SKTypeface.Default;
        Resolved.Clear();
    }

    /// <summary>
    /// 该码位优先用内置字体；缺字时用系统字体回退（结果按字符缓存）。
    /// 全部失败时退回内置字体（绘制为空白/方框，但至少保持宽度语义）。
    /// </summary>
    public static SKTypeface Resolve(char codePoint)
        => Resolved.GetOrAdd(codePoint, static c =>
        {
            var embedded = Embedded;
            if (embedded.GetGlyph(c) != 0)
                return embedded;
            return SKFontManager.Default.MatchCharacter(c) ?? embedded;
        });
}
