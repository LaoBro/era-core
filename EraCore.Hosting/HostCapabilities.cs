using System;
using MinorShift.Emuera.Input;

namespace EraCore.Hosting;

/// <summary>宿主可声明的渲染能力位（增量协商；Core 始终输出完整 DTO，不因能力位改变 wire）。</summary>
[Flags]
public enum RenderCapability
{
    None = 0,

    /// <summary>文本、样式、颜色、对齐（必需；未声明时按 Text 处理）。</summary>
    Text = 1 << 0,

    /// <summary>能绘制 <c>PrintSegment.image</c>。</summary>
    Images = 1 << 1,

    /// <summary>能绘制 <c>PrintSegment.shape</c>。</summary>
    Shapes = 1 << 2,

    /// <summary>能绘制背景图 <c>BgImageState</c>。</summary>
    BackgroundImages = 1 << 3,

    /// <summary>能解析 <c>PrintSegment.fontname</c> 的命名字体；否则统一回退默认字体。</summary>
    NamedFonts = 1 << 4,

    /// <summary>能消费动画/AWAIT 中间帧；否则可合并到稳定停稳点。</summary>
    AnimatedFrames = 1 << 5,
}

/// <summary>宿主能力声明：输入能力（沿用 Core）+ 渲染能力（Hosting 侧投影策略）。</summary>
public sealed record HostCapabilities(
    InputCapabilities Input,
    RenderCapability Render)
{
    public bool Has(RenderCapability capability) => (Render & capability) != 0;
}
