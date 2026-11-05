using System;
using System.Collections.Generic;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// SKPaint 实例池：按 (颜色, 样式, 描边宽度, 抗锯齿) 复用，避免每帧/每 op 新建 <see cref="SKPaint"/>。
///
/// <para>只存「创建时配置好、之后不再修改」的 paint。所有调用方只读使用（<c>DrawText</c>/
/// <c>DrawImage</c>/<c>DrawRect</c>/<c>DrawLine</c> 均不改 paint 状态），因此跨帧复用不会带入
/// 上一帧的可变状态；若某个 draw 需要不同配置，必然落在不同 key 上，拿到另一支 paint。</para>
///
/// <para>容量上界：<see cref="MaxEntries"/>。颜色来自游戏文本（通常只有几种），超过上限说明
/// 颜色数量异常增长，此时在下一帧开始（<see cref="BeginFrame"/>）整体释放并按需重建。
/// 不能在 <see cref="Get"/> 内即时释放：本帧已取出的 paint 可能仍被调用栈持有（如默认文本画笔），
/// 帧内释放会造成「已释放对象仍被绘制」。</para>
///
/// <para>线程约束：与 <c>NativeFontPool</c> 相同，调用方必须保证串行（Android 侧
/// <c>ICustomDrawOperation.Render</c> 在同一渲染线程串行执行同一 view）；本类不加锁。</para>
/// </summary>
internal sealed class NativePaintPool
{
    /// <summary>池内 paint 数量上限；超过即在下一帧开始时整体释放重建。</summary>
    internal const int MaxEntries = 64;

    private readonly Dictionary<Key, SKPaint> _paints = new();

    public int Count => _paints.Count;

    /// <summary>帧起点调用：上一帧绘制已结束，此时超限可安全整体释放。</summary>
    public void BeginFrame()
    {
        if (_paints.Count > MaxEntries)
            DisposeAll();
    }

    /// <summary>取一支已配置的 paint；同 key 返回同一实例，调用方禁止修改其状态。</summary>
    public SKPaint Get(SKColor color, SKPaintStyle style = SKPaintStyle.Fill, float strokeWidth = 0f, bool antialias = false)
    {
        var key = new Key(Pack(color), style, strokeWidth, antialias);
        if (_paints.TryGetValue(key, out var cached))
            return cached;

        var paint = new SKPaint
        {
            Color = color,
            IsAntialias = antialias,
            Style = style,
        };
        if (style == SKPaintStyle.Stroke)
            paint.StrokeWidth = strokeWidth;

        _paints[key] = paint;
        return paint;
    }

    /// <summary>释放全部 paint（下次 <see cref="Get"/> 按需重建）。</summary>
    public void Dispose()
    {
        DisposeAll();
    }

    private void DisposeAll()
    {
        foreach (var paint in _paints.Values)
            paint.Dispose();
        _paints.Clear();
    }

    private static uint Pack(SKColor color)
        => ((uint)color.Alpha << 24) | ((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue;

    private readonly record struct Key(uint Color, SKPaintStyle Style, float StrokeWidth, bool Antialias);
}
