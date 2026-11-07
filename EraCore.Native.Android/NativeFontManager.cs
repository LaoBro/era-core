using System;
using System.Collections.Concurrent;
using Android.Content;
using Android.Util;
using EraCore.Native;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// 内置字体加载与逐码位回退：<c>EraGridMono → EmueraBlock → EraCoreCJK → 系统字体</c>。
///
/// <para>Android 没有 <c>ＭＳ ゴシック</c>，系统字体也不保证覆盖游戏的日文/中文码位，且比例字体的
/// ASCII advance 会把字符画/分割线画出网格。这里复刻 Web 的逐字形回退链：网格字体提供 ASCII 与全角符号
/// （advance 严格 0.5em / 1em），EmueraBlock 补齐 IPAGothic 缺失的半角符号，中文继续用内置 CJK 子集；
/// 三者都缺字时才落到系统字体。资源来源与许可见 <c>Assets/fonts/NOTICE.md</c>。</para>
///
/// <para>每个字体源只加载一次，<see cref="Resolve"/> 返回<b>静态缓存的同一实例</b>，因此
/// <c>NativeLineLayoutBuilder</c> 的按字体切 run、<c>NativeFontPool</c> 的字体池键、
/// <c>NativeTextBlobKey</c> 的缓存键都能用对象身份区分字体源；换字体源必然换缓存键，不会命中旧 blob。</para>
///
/// <para><see cref="MinorShift.Emuera.Sessions.HostMetrics.FontName"/> 的原值不被改写，
/// 只用于设置页/日志展示。</para>
/// </summary>
internal static class NativeFontManager
{
    private const string LogTag = "EraCoreNative";

    private const string GridAssetPath = "fonts/EraGridMono.ttf";
    private const string BlockAssetPath = "fonts/EmueraBlock.ttf";
    private const string CjkAssetPath = "fonts/EraCoreCJK.ttf";

    private static SKTypeface? _grid;
    private static SKTypeface? _block;
    private static SKTypeface? _cjk;
    private static readonly ConcurrentDictionary<char, SKTypeface> Resolved = new();

    /// <summary>内置 CJK 字体（同时是行内纵向度量的基准字体；加载失败时退回系统默认）。</summary>
    public static SKTypeface Embedded => _cjk ?? SKTypeface.Default;

    public static void Initialize(Context context)
    {
        if (_cjk is not null)
            return;

        _cjk = Load(context, CjkAssetPath);
        _grid = Load(context, GridAssetPath);
        _block = Load(context, BlockAssetPath);

        _cjk ??= SKTypeface.Default;
        Resolved.Clear();

        Log.Info(LogTag,
            $"fonts: grid={Describe(_grid)} block={Describe(_block)} cjk={Describe(_cjk)}");
    }

    /// <summary>
    /// 按码位选字体源（结果按字符缓存）：网格字体 → EmueraBlock → 内置 CJK → 系统字体。
    /// 全部失败时退回内置 CJK（绘制为空白/方框，但至少保持宽度语义）。
    /// </summary>
    public static SKTypeface Resolve(char codePoint)
        => Resolved.GetOrAdd(codePoint, static c =>
        {
            var source = NativeFontFallback.Select(c, Covers);
            return source switch
            {
                NativeFontSource.Grid => _grid ?? Embedded,
                NativeFontSource.Block => _block ?? Embedded,
                NativeFontSource.Cjk => Embedded,
                _ => SKFontManager.Default.MatchCharacter(c) ?? Embedded,
            };
        });

    /// <summary>该字体源是否真的含该码位的字形（缺字返回 0；字体未加载时视为不覆盖）。</summary>
    private static bool Covers(NativeFontSource source, char codePoint)
    {
        var typeface = source switch
        {
            NativeFontSource.Grid => _grid,
            NativeFontSource.Block => _block,
            _ => _cjk,
        };
        return typeface is not null && typeface.GetGlyph(codePoint) != 0;
    }

    private static SKTypeface? Load(Context context, string path)
    {
        try
        {
            using var stream = context.Assets!.Open(path);
            var typeface = SKTypeface.FromStream(stream);
            if (typeface is null)
                Log.Warn(LogTag, $"font load failed: {path}");
            return typeface;
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"font unavailable ({path}): {ex.Message}");
            return null;
        }
    }

    private static string Describe(SKTypeface? typeface)
        => typeface is null ? "(none)" : $"{typeface.FamilyName}#{typeface.FontStyle}";
}
