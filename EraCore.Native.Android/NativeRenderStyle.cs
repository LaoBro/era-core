using System;
using MinorShift.Emuera.Sessions;

namespace EraCore.Native.Android;

/// <summary>
/// 渲染参数：把游戏 <see cref="HostMetrics"/> 与全局 <see cref="AppSettings"/> 合成为
/// <see cref="NativeSkiaRenderer"/> 实际消费的字号/行高/前景色/内容宽度。
///
/// <para>游戏原始 <see cref="HostMetrics.FontName"/>/<see cref="HostMetrics.FontSize"/> 保留不变，
/// 只在这里做「同比缩放 + 行距」派生；行高变化同时驱动 <see cref="EraCore.Hosting.VirtualWindow"/>
/// 的可见行数。</para>
/// </summary>
internal sealed record NativeRenderStyle(
    double FontSize,
    double LineHeight,
    string ForeColor,
    string? BackgroundOverride,
    string FontName,
    double ContentWidth,
    double Scale)
{
    public static NativeRenderStyle From(HostMetrics metrics, AppSettings settings)
    {
        var fontSize = Math.Clamp((double)settings.FontSize, AppSettings.MinFontSize, AppSettings.MaxFontSize);
        var scale = fontSize / Math.Max(1, metrics.FontSize);
        var lineHeight = Math.Max(
            1,
            metrics.LineHeight * scale * Math.Clamp(settings.LineSpacing, AppSettings.MinLineSpacing, AppSettings.MaxLineSpacing));
        var light = settings.Theme == AppTheme.Light;

        return new NativeRenderStyle(
            fontSize,
            lineHeight,
            light ? "#1B1B1B" : metrics.ForeColor,
            light ? "#FFFFFF" : null,
            metrics.FontName,
            Math.Max(1, metrics.WindowWidth * scale),
            scale);
    }
}
