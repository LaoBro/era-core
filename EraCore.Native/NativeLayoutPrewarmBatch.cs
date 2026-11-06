using System;
using System.Collections.Generic;

namespace EraCore.Native;

/// <summary>
/// 后台预热的只读批次：在预热线程上用 <see cref="NativeLineLayoutBuilder"/> 构建，
/// 渲染线程在 <c>Draw</c> 起点按「frame 引用 + style 值 + 行对象引用」三重校验后并入
/// <see cref="NativeLineLayoutCache"/>。
///
/// <para>设计要点：批次只持有纯托管数据（行布局不持有 <c>SKFont</c>），因此不需要任何原生资源
/// 生命周期管理；过期批次（frame 已换、style 已变、窗口已滚动）直接丢弃交给 GC，不 Dispose。</para>
///
/// <para>行布局按<b>行下标</b>存放，但并入时逐行校验 <c>ReferenceEquals(lines[index], line)</c>：
/// 只要同一个 <see cref="NativeFrame"/> 里该下标仍是同一行对象，预热结果就与现场构建等价
/// （同一 builder、同一 style 值、同一字体池）。窗口在预热与绘制之间发生偏移时，只会并入仍然对得上的行。</para>
/// </summary>
public sealed class NativeLayoutPrewarmBatch
{
    private readonly object _frame;
    private readonly object _style;
    private readonly int[] _indices;
    private readonly NativeLine[] _lines;
    private readonly NativeLineLayout[] _layouts;

    /// <summary>
    /// <paramref name="indices"/>/<paramref name="lines"/>/<paramref name="layouts"/> 为等长平行数组，
    /// 由预热线程一次性分配并只读发布；本对象不复制它们。
    /// </summary>
    public NativeLayoutPrewarmBatch(
        object frame,
        object style,
        int[] indices,
        NativeLine[] lines,
        NativeLineLayout[] layouts)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(layouts);
        if (indices.Length != lines.Length || indices.Length != layouts.Length)
            throw new ArgumentException("indices/lines/layouts 长度必须一致");

        _frame = frame;
        _style = style;
        _indices = indices;
        _lines = lines;
        _layouts = layouts;
    }

    /// <summary>批次覆盖的行数（可见窗口 + overscan）。</summary>
    public int Count => _indices.Length;

    /// <summary>与当前绘制的 frame 引用相同且 style 全字段相等时才允许并入。</summary>
    public bool Matches(object frame, object style)
        => ReferenceEquals(_frame, frame) && _style.Equals(style);

    /// <summary>
    /// 逐行校验行对象引用后写入缓存，返回实际并入的行数；下标越界或行对象已变化的行跳过。
    /// 只在渲染线程调用（缓存本身仍保持无锁前提）。
    /// </summary>
    public int AdoptInto(NativeLineLayoutCache cache, IReadOnlyList<NativeLine> lines)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(lines);

        var adopted = 0;
        for (var i = 0; i < _indices.Length; i++)
        {
            var index = _indices[i];
            if ((uint)index >= (uint)lines.Count)
                continue;
            if (!ReferenceEquals(lines[index], _lines[i]))
                continue;
            cache.Store(index, _layouts[i]);
            adopted++;
        }

        return adopted;
    }
}
