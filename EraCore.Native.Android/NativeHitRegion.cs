using Avalonia;
using EraCore.Native;

namespace EraCore.Native.Android;

/// <summary>一次绘制产出的按钮命中区（控件局部坐标，已含滚动/对齐偏移）。</summary>
internal sealed record NativeHitRegion(
    double X,
    double Y,
    double Width,
    double Height,
    NativeButton Button)
{
    public bool Contains(Point point)
        => point.X >= X && point.X < X + Width && point.Y >= Y && point.Y < Y + Height;
}

/// <summary>
/// 一次绘制产出的 srcm 图片热区（控件局部坐标，已含滚动/对齐偏移）。
///
/// <para>三块矩形与 Web 的 DOM 结构一一对应：</para>
/// <list type="bullet">
/// <item><description><c>X/Y/Width/Height</c> = 掩膜带（<c>.term-mask</c>，宽度=图片显示宽、高度=行高），
/// 决定「点击是否落在热区」——与 Web 一样只在图片锚定行的行高带内可点。</description></item>
/// <item><description><c>Element*</c> = <c>&lt;img&gt;</c> 元素矩形；有 <c>crop</c> 时是**整图**按
/// <c>crop.imgWidth/imgHeight</c> 缩放并加 <c>crop.x/y</c> 负偏移后的位置（Web 用
/// <c>img.getBoundingClientRect()</c> 取的就是这块），坐标反算以它为基准。</description></item>
/// <item><description><c>Visible*</c> = 裁切后的可见区域（无 <c>crop</c> 时等于 Element）；点击落在
/// 整图矩形内但落在可见区域外（被 <c>overflow:hidden</c> 裁掉的部分）按「裁剪外」拒绝。</description></item>
/// </list>
/// </summary>
internal sealed record NativeImageHitRegion(
    double X,
    double Y,
    double Width,
    double Height,
    double ElementX,
    double ElementY,
    double ElementWidth,
    double ElementHeight,
    double VisibleX,
    double VisibleY,
    double VisibleWidth,
    double VisibleHeight,
    string Src,
    string Srcm,
    NativeButton Button)
{
    public bool Contains(Point point)
        => point.X >= X && point.X < X + Width && point.Y >= Y && point.Y < Y + Height;

    public bool ContainsElement(Point point)
        => ElementWidth > 0 && ElementHeight > 0
           && point.X >= ElementX && point.X < ElementX + ElementWidth
           && point.Y >= ElementY && point.Y < ElementY + ElementHeight;

    public bool ContainsVisible(Point point)
        => VisibleWidth > 0 && VisibleHeight > 0
           && point.X >= VisibleX && point.X < VisibleX + VisibleWidth
           && point.Y >= VisibleY && point.Y < VisibleY + VisibleHeight;
}

/// <summary>一次绘制的全部命中数据。</summary>
internal sealed record NativeDrawHits(
    IReadOnlyList<NativeHitRegion> Buttons,
    IReadOnlyList<NativeImageHitRegion> Images)
{
    public static NativeDrawHits Empty { get; } =
        new(Array.Empty<NativeHitRegion>(), Array.Empty<NativeImageHitRegion>());
}
