using System;

namespace EraCore.Native;

/// <summary>
/// 行内视觉片段在「流」方向占用的宽度（显示像素，已含缩放）。
///
/// <para>这是占位宽度与绘制宽度的唯一口径：纯文本宿主按同一数值补空白，
/// 原生渲染按同一数值推进绘制游标，因此加图前后行宽不会跳动。</para>
/// </summary>
public static class NativeGeometry
{
    /// <summary>图片占 <see cref="NativeImage.Width"/>；非法负宽按 0。</summary>
    public static float ImageWidth(NativeImage image, float scale)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Math.Max(0f, image.Width * scale);
    }

    /// <summary>
    /// 矩形占 <c>x + width</c>（与 Web 的 flow width、CLI 的 <c>WidthF = Rect.X + Rect.Width</c> 一致）；
    /// 起笔位置仍是 <c>x</c>。
    /// </summary>
    public static float ShapeWidth(NativeShape shape, float scale)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return Math.Max(0f, (shape.X + shape.Width) * scale);
    }
}
