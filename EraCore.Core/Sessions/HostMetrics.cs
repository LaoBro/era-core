using System;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Sessions;

/// <summary>
/// 宿主渲染所需的平台无关元信息（字号 / 行高 / 窗口尺寸 / 默认文字色 / 履历容量等）。
///
/// <para>来源与 <c>GET /state</c> 的字段同源，但这是 C# 宿主的公共面；Server 内部的
/// <c>WindowMetrics</c> 继续 internal，不对外暴露。仅 <see cref="CoreSession.StartAsync"/>
/// 成功后可用。</para>
/// </summary>
public sealed record HostMetrics(
    int WindowWidth,
    int WindowHeight,
    int FontSize,
    int LineHeight,
    string FontName,
    string ForeColor,
    int MaxLog,
    int GameColumns,
    int VisibleRows)
{
    /// <summary>从已加载的游戏配置构造（Core 内部使用；数值公式与 <c>GET /state</c> 一致）。</summary>
    internal static HostMetrics From(ConfigData config)
    {
        var windowWidth = config.GetConfigValue<int>(ConfigCode.WindowX);
        var windowHeight = config.GetConfigValue<int>(ConfigCode.WindowY);
        var fontSize = config.GetConfigValue<int>(ConfigCode.FontSize);
        var lineHeight = config.GetConfigValue<int>(ConfigCode.LineHeight);
        var fontName = config.GetConfigValue<string>(ConfigCode.FontName);
        var foreColor = config.GetConfigValue<EmuColor>(ConfigCode.ForeColor);
        var maxLog = config.GetConfigValue<int>(ConfigCode.MaxLog);

        var charWidth = Math.Max(fontSize / 2, 1);
        var shapeShift = Math.Max(2, fontSize / 6);
        var drawableWidth = windowWidth - shapeShift;
        var gameColumns = drawableWidth / charWidth;
        var visibleRows = Math.Max(1, windowHeight / lineHeight);

        return new HostMetrics(
            windowWidth,
            windowHeight,
            fontSize,
            lineHeight,
            fontName,
            foreColor.ToHex(),
            maxLog,
            gameColumns,
            visibleRows);
    }
}
