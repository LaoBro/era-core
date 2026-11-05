using MinorShift.Emuera.Output;

namespace EraCore.Native;

/// <summary>图像片段（由 <see cref="SegmentImage"/> 投影）；宽度/高度已是显示像素。</summary>
public sealed record NativeImage(
    string Src,
    string? Srcb,
    string? Srcm,
    int Width,
    int Height,
    int YPos,
    NativeCrop? Crop);

/// <summary><see cref="SegmentCrop"/>：从源图裁出 (X,Y,ImgWidth,ImgHeight)，贴到目标 (Width,Height)。</summary>
public sealed record NativeCrop(int X, int Y, int ImgWidth, int ImgHeight);

/// <summary>矩形填充（由 <see cref="SegmentShape"/> 投影）；X/Y 相对行内容起点。</summary>
public sealed record NativeShape(
    string Type,
    int X,
    int Y,
    int Width,
    int Height,
    string Color);

/// <summary>背景图层；Depth 升序绘制（小的在底层），Opacity 0.0-1.0。</summary>
public sealed record NativeBgImage(string Src, long Depth, float Opacity);

/// <summary>渲染用的只读片段；图像/形状保留完整几何，不再只给布尔标记。</summary>
public sealed record NativeSegment(
    string Text,
    string? Color,
    bool Bold,
    bool Italic,
    string? FontName,
    bool Underline,
    bool Strikeout,
    bool IsImage = false,
    bool IsShape = false,
    NativeImage? Image = null,
    NativeShape? Shape = null);

/// <summary>按钮引用（M1 仅保留，M2 用它做命中与提交）。</summary>
public sealed record NativeButton(
    object Value,
    bool IsInteger,
    long Generation,
    int? Col,
    int? Width);

public sealed record NativeEntry(IReadOnlyList<NativeSegment> Segments, NativeButton? Button);

public sealed record NativeLine(IReadOnlyList<NativeEntry> Entries, string? Align);

/// <summary>
/// 图片在帧内的垂直索引项：只记录「图片属于哪一行、行内哪个 op」，**不存绝对 top/bottom**。
/// 竖坐标由 <see cref="NativeImageViewport"/> 用 <see cref="NativeTextLayout.LineTop"/> 现算，
/// 因此滚动、贴底补偿与字号缩放变化都不需要重建索引；水平 <c>x</c> 依赖字体度量，留在渲染层。
/// </summary>
public sealed record NativeImageExtent(
    int LineIndex,
    int EntryIndex,
    int SegmentIndex,
    NativeImage Image,
    NativeButton? Button);

/// <summary>
/// 一帧可绘制数据：平台无关、不可变，供任意原生渲染后端消费。
/// 图像/形状保留几何，纯文本后端按宽度占位；背景层按 Depth 降序绘制（大 depth 在底层）。
/// </summary>
public sealed record NativeFrame(
    IReadOnlyList<NativeLine> Lines,
    string BackgroundColor,
    long Generation,
    int ProtocolVersion,
    long Revision,
    string? EndedTerminal,
    IReadOnlyList<NativeBgImage>? Backgrounds = null,
    IReadOnlyList<NativeImageExtent>? ImageExtents = null)
{
    public IReadOnlyList<NativeBgImage> BackgroundLayers => Backgrounds ?? Array.Empty<NativeBgImage>();

    /// <summary>按（锚定行索引, EntryIndex, SegmentIndex）升序排列的图片索引；空帧为空表。</summary>
    public IReadOnlyList<NativeImageExtent> ImageExtentList => ImageExtents ?? Array.Empty<NativeImageExtent>();

    public static NativeFrame Empty { get; } =
        new(Array.Empty<NativeLine>(), "#000000", 0, 0, 0, null);
}

/// <summary><see cref="DisplaySnapshot"/> → <see cref="NativeFrame"/> 的纯函数投影。</summary>
public static class NativeRenderModel
{
    public static NativeFrame FromSnapshot(DisplaySnapshot? snapshot)
    {
        if (snapshot is null)
            return NativeFrame.Empty;

        var lines = new List<NativeLine>(snapshot.lines.Count);
        var extents = new List<NativeImageExtent>();
        for (var i = 0; i < snapshot.lines.Count; i++)
            lines.Add(ProjectLine(snapshot.lines[i], i, extents));

        return new NativeFrame(
            lines,
            string.IsNullOrEmpty(snapshot.bgColor) ? "#000000" : snapshot.bgColor,
            snapshot.generation,
            snapshot.protocolVersion,
            snapshot.revision,
            snapshot.awaiting?.ended?.terminal,
            ProjectBackgrounds(snapshot.bgImages),
            extents);
    }

    public static NativeLine ProjectLine(DisplayLine line) => ProjectLine(line, 0, null);

    private static NativeLine ProjectLine(
        DisplayLine line,
        int lineIndex,
        List<NativeImageExtent>? extents)
    {
        var entries = new List<NativeEntry>(line.entries.Count);
        for (var entryIndex = 0; entryIndex < line.entries.Count; entryIndex++)
        {
            var entry = line.entries[entryIndex];
            var button = entry.button is { } b
                ? new NativeButton(b.value, b.isInteger, b.generation, b.col, b.width)
                : null;

            var segments = new List<NativeSegment>(entry.segments.Count);
            for (var segmentIndex = 0; segmentIndex < entry.segments.Count; segmentIndex++)
            {
                var segment = entry.segments[segmentIndex];
                var image = ProjectImage(segment.image);
                var shape = ProjectShape(segment.shape);

                segments.Add(new NativeSegment(
                    segment.text,
                    segment.color,
                    segment.bold == true,
                    segment.italic == true,
                    segment.fontname,
                    segment.underline == true,
                    segment.strikeout == true,
                    image is not null,
                    shape is not null,
                    image,
                    shape));

                // 逐行、逐 entry、逐 segment 顺序 append：extent 表天然按锚定行/op 升序，
                // 调用方无需再排序；竖坐标留到绘制时现算。
                if (image is not null && extents is not null)
                    extents.Add(new NativeImageExtent(lineIndex, entryIndex, segmentIndex, image, button));
            }

            entries.Add(new NativeEntry(segments, button));
        }

        return new NativeLine(entries, line.align);
    }

    public static IReadOnlyList<NativeBgImage> ProjectBackgrounds(List<BgImageState>? images)
    {
        if (images is not { Count: > 0 })
            return Array.Empty<NativeBgImage>();

        var result = new List<NativeBgImage>(images.Count);
        foreach (var image in images)
            result.Add(new NativeBgImage(image.src, image.depth, image.opacity));
        return result;
    }

    private static NativeImage? ProjectImage(SegmentImage? image)
    {
        if (image is null)
            return null;

        var crop = image.crop is { } c
            ? new NativeCrop(c.x, c.y, c.imgWidth, c.imgHeight)
            : null;

        return new NativeImage(
            image.src,
            image.srcb,
            image.srcm,
            Math.Max(0, image.width),
            Math.Max(0, image.height),
            image.ypos,
            crop);
    }

    private static NativeShape? ProjectShape(SegmentShape? shape)
        => shape is null
            ? null
            : new NativeShape(shape.type, shape.x, shape.y, shape.width, shape.height, shape.color);
}
