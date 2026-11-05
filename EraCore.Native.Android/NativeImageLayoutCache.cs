using System;
using System.Collections.Generic;
using EraCore.Native;

namespace EraCore.Native.Android;

/// <summary>行内某张图片的水平前缀宽度（显示像素，已含缩放；不含 startX/对齐偏移/滚动）。</summary>
internal readonly record struct NativeLineImageOffset(int EntryIndex, int SegmentIndex, float PrefixWidth);

/// <summary>一行的按行布局结果：整行总宽 + 每张图片的 op 定位与前缀宽度。</summary>
internal sealed record NativeLineImageLayout(float TotalWidth, IReadOnlyList<NativeLineImageOffset> Images)
{
    /// <summary>按 (EntryIndex, SegmentIndex) 找图片的前缀宽度；找不到返回 null。</summary>
    public float? PrefixOf(int entryIndex, int segmentIndex)
    {
        foreach (var image in Images)
        {
            if (image.EntryIndex == entryIndex && image.SegmentIndex == segmentIndex)
                return image.PrefixWidth;
        }
        return null;
    }

    public static NativeLineImageLayout Empty { get; } =
        new(0f, Array.Empty<NativeLineImageOffset>());
}

/// <summary>
/// 按行缓存的图片水平布局（只存数值，不持有 <c>SKFont</c>/<c>Run</c>——字体在每次绘制结束时释放）。
///
/// <para>图片的 <c>x</c> 依赖「该行前序 op 的实测宽度 + 对齐偏移 + 起点 shift」，无法在平台无关的
/// <see cref="NativeFrame"/> 预计算；窗口外的锚定行本帧不画文本，但仍需为跨界图片布局一次整行。
/// 本缓存把「整行总宽 + 各图片前缀宽」按行记住，滚动帧不再重复量测。</para>
///
/// <para>失效键必须覆盖所有影响度量的 style 字段：
/// <c>FontSize / LineHeight / FontName / Scale / ContentWidth / Inset</c>
/// （<see cref="NativeRenderStyle"/> 是 record，Equals 覆盖全部字段，额外包含前景色等无害字段）。
/// 换帧（引用变化）或换 style 时整表清空，绝不跨 frame 复用。</para>
/// </summary>
internal sealed class NativeImageLayoutCache
{
    private NativeFrame? _frame;
    private NativeRenderStyle? _style;
    private readonly Dictionary<int, NativeLineImageLayout> _lines = new();

    public NativeLineImageLayout GetOrCreate(
        NativeFrame frame,
        NativeRenderStyle style,
        int lineIndex,
        Func<NativeLineImageLayout> factory)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(factory);

        if (!ReferenceEquals(_frame, frame) || _style is null || !_style.Equals(style))
        {
            _lines.Clear();
            _frame = frame;
            _style = style;
        }

        if (_lines.TryGetValue(lineIndex, out var cached))
            return cached;

        var built = factory();
        _lines[lineIndex] = built;
        return built;
    }
}
