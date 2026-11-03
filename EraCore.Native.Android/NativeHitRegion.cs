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
