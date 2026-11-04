using System;
using EraCore.Native;
using MinorShift.Emuera.Sessions;

namespace EraCore.Native.Android;

/// <summary>
/// 渲染参数：把游戏 <see cref="HostMetrics"/> 与全局 <see cref="AppSettings"/> 合成为
/// <see cref="NativeSkiaRenderer"/> 实际消费的字号/行高/前景色/内容宽度。
///
/// <para>字号默认跟随游戏（<see cref="AppSettings.FollowGameFontSize"/>），关闭跟随后才用用户绝对值；
/// 行高严格 = 游戏行高 × 字号同比，没有行距倍率。可绘制宽度 =
/// <c>WindowX - ShapePositionShift × 2</c>（WinForms <c>Config.DrawableWidth</c>），
/// 文本/图像/形状统一起点右移同一 shift，保证对齐基准是 game width 而不是视口宽度。</para>
/// </summary>
internal sealed record NativeRenderStyle(
    double FontSize,
    double LineHeight,
    string ForeColor,
    string? BackgroundOverride,
    string FontName,
    double ContentWidth,
    double Inset,
    double Scale)
{
    /// <summary>对齐可用宽度：WinForms <c>Config.DrawableWidth</c>（已含字号同比）。</summary>
    public double DrawableWidth => Math.Max(1, ContentWidth - Inset);

    public static NativeRenderStyle From(HostMetrics metrics, AppSettings settings)
    {
        var userFontSize = Math.Clamp((double)settings.FontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize);
        var fontSize = NativeTextLayout.EffectiveFontSize(
            settings.FollowGameFontSize,
            userFontSize,
            metrics.FontSize);
        var scale = NativeTextLayout.FontScale(fontSize, metrics.FontSize);
        var lineHeight = NativeTextLayout.LineHeight(metrics.LineHeight, scale);
        var inset = NativeTextLayout.HorizontalShift(metrics.FontSize) * scale;
        var light = settings.Theme == AppTheme.Light;

        return new NativeRenderStyle(
            fontSize,
            lineHeight,
            light ? "#1B1B1B" : metrics.ForeColor,
            light ? "#FFFFFF" : null,
            metrics.FontName,
            Math.Max(1, metrics.WindowWidth * scale),
            Math.Max(0, inset),
            scale);
    }
}
