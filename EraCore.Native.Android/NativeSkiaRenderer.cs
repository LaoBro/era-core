using System;
using System.Collections.Generic;
using System.Linq;
using Android.Util;
using EraCore.Hosting;
using EraCore.Native;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// SKCanvas 终端绘制器：文本 + 样式 + 颜色 + 对齐 + 可见行 + 按钮命中区，
/// 以及图像片段、矩形形状与背景图层。
///
/// <para>字号/行高/前景色来自 <see cref="NativeRenderStyle"/>（设置页可覆盖游戏配置）；
/// 可见区间与滚动条比例来自 <see cref="VirtualWindow"/>。返回值是本次绘制的按钮命中区
/// （基于实际 SKFont 度量的 run 位置，不使用 col×FontSize/2）。</para>
///
/// <para>图像宽度既是占位宽度也是绘制宽度（经 <see cref="NativeRenderStyle.Scale"/> 同比缩放），
/// 纯文本后端按同一宽度占位，保证加图前后行宽不跳。背景层固定不随内容滚动，按 depth 升序绘制。</para>
/// </summary>
internal static class NativeSkiaRenderer
{
    private const string LogTag = "EraCoreNative";
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>最近一次已打印的文本布局，避免每帧刷屏；字号/行高/度量变化时重新打印。</summary>
    private static (float FontSize, float LineHeight, float Ascent, float Descent)? _lastLoggedLayout;

    /// <summary>segment 的字体变体（常规 / 粗体 / 斜体 / 粗斜体）。</summary>
    private enum Variant
    {
        Regular,
        Bold,
        Italic,
        BoldItalic,
    }

    private readonly record struct Run(
        string Text,
        SKFont Font,
        float Width,
        string? Color,
        bool Underline,
        bool Strikeout);

    private readonly record struct EntryLayout(float StartX, float Width, NativeButton? Button);

    private readonly record struct DrawOp(
        float Width,
        Run? Run,
        NativeImage? Image,
        NativeShape? Shape,
        NativeButton? Button);

    public static NativeDrawHits Draw(
        SKCanvas canvas,
        double viewportWidth,
        double viewportHeight,
        NativeFrame frame,
        NativeRenderStyle style,
        NativeImageCache images,
        double scrollX,
        double scrollY)
    {
        canvas.Clear(ParseColor(style.BackgroundOverride ?? frame.BackgroundColor, new SKColor(0, 0, 0)));
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return NativeDrawHits.Empty;

        var lineHeight = (float)Math.Max(1, style.LineHeight);
        var fontSize = (float)Math.Max(1, style.FontSize);
        var scale = (float)Math.Max(0.1, style.Scale);
        var maxScrollX = Math.Max(0f, (float)style.ContentWidth - (float)viewportWidth);
        var sx = Math.Clamp((float)scrollX, 0f, maxScrollX);

        var window = VirtualWindow.Compute(
            frame.Lines.Count,
            scrollY,
            viewportHeight,
            new VirtualWindowOptions(style.LineHeight));
        var sy = (float)window.ScrollY;

        var fonts = new FontCache(fontSize);
        using var defaultPaint = new SKPaint
        {
            Color = ParseColor(style.ForeColor, new SKColor(0xC0, 0xC0, 0xC0)),
            IsAntialias = true,
        };

        using var measureFont = new SKFont(NativeFontManager.Embedded, fontSize);
        var fontMetrics = measureFont.Metrics;
        // 行高只来自 style.LineHeight；字体度量只决定「行内」基线位置（WinForms 顶对齐 = -Ascent）。
        var baselineOffset = (float)NativeTextLayout.BaselineOffset(fontMetrics.Ascent);

        var hits = new List<NativeHitRegion>();
        var imageHits = new List<NativeImageHitRegion>();

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewportWidth, (float)viewportHeight));

        // 背景的烘焙画布与 WinForms 一致：宽 = 游戏画布宽（不是视口宽），高 = 视口高。
        DrawBackgrounds(canvas, frame, images, (float)style.ContentWidth, (float)viewportHeight, sx);

        float? previousBaseline = null;
        var previousIndex = 0;
        var measuredBaselineSpacing = lineHeight;

        for (var index = window.StartIndex; index < window.EndIndexExclusive; index++)
        {
            var line = frame.Lines[index];
            var ops = new List<DrawOp>(line.Entries.Count);
            var entries = new List<EntryLayout>(line.Entries.Count);
            var total = 0f;

            foreach (var entry in line.Entries)
            {
                var entryStart = total;
                foreach (var segment in entry.Segments)
                {
                    // 与 Web 一致：有 image/shape 时忽略 text（text 只是 CLI 调试标记，
                    // 缺图时 Protocol 仍会带 AltText）。否则会把占位框推到文字右侧，导致看不见。
                    var hasVisual = segment.Image is not null || segment.Shape is not null;
                    if (!hasVisual && !string.IsNullOrEmpty(segment.Text))
                    {
                        var variant = PickVariant(segment);
                        foreach (var run in SplitByTypeface(segment, variant, fonts))
                        {
                            ops.Add(new DrawOp(run.Width, run, null, null, entry.Button));
                            total += run.Width;
                        }
                    }

                    if (segment.Image is { } image)
                    {
                        var width = NativeGeometry.ImageWidth(image, scale);
                        ops.Add(new DrawOp(width, null, image, null, entry.Button));
                        total += width;
                    }

                    if (segment.Shape is { } shape)
                    {
                        // 与 Web 掩膜一致：形状占 x + width，起笔从 x 开始。
                        var width = NativeGeometry.ShapeWidth(shape, scale);
                        ops.Add(new DrawOp(width, null, null, shape, entry.Button));
                        total += width;
                    }
                }
                entries.Add(new EntryLayout(entryStart, total - entryStart, entry.Button));
            }

            if (ops.Count == 0)
                continue;

            // WinForms：文本/图像/形状统一起点右移 ShapePositionShift，
            // 对齐宽度 = Config.DrawableWidth（game width，不是视口宽度）。
            var startX = (float)style.Inset - sx
                + (float)AlignGeometry.OffsetPixels(line.Align, total, style.DrawableWidth);
            var lineTop = (float)NativeTextLayout.LineTop(
                index,
                lineHeight,
                sy,
                frame.Lines.Count,
                viewportHeight);
            var baseline = lineTop + baselineOffset;
            var x = startX;

            // 实测：相邻有内容的行之间，基线差必须正好是行高的整数倍（空行跳过）。
            if (previousBaseline.HasValue)
            {
                var indexGap = index - previousIndex;
                var expected = indexGap * lineHeight;
                var actual = baseline - previousBaseline.Value;
                if (Math.Abs(actual - expected) > 0.01f)
                {
                    Log.Warn(LogTag,
                        $"baseline spacing mismatch: index {previousIndex}->{index} " +
                        $"actual={actual:F2} expected={expected:F2} line={lineHeight:F2}");
                }

                if (indexGap == 1)
                    measuredBaselineSpacing = actual;
            }

            previousBaseline = baseline;
            previousIndex = index;

            foreach (var op in ops)
            {
                if (op.Run is { } run)
                {
                    DrawRun(canvas, run, defaultPaint, x, baseline, fontSize);
                }
                else if (op.Image is { } image)
                {
                    DrawImage(canvas, image, x, lineTop, scale, images);
                    if (op.Button is { } button && !string.IsNullOrEmpty(image.Srcm))
                        imageHits.Add(CreateImageHit(image, button, x, lineTop, lineHeight, scale));
                }
                else if (op.Shape is { } shape)
                {
                    DrawShape(canvas, shape, x, lineTop, scale);
                }

                x += op.Width;
            }

            foreach (var entry in entries)
            {
                if (entry.Button is null || entry.Width <= 0)
                    continue;
                hits.Add(new NativeHitRegion(
                    startX + entry.StartX,
                    lineTop,
                    entry.Width,
                    lineHeight,
                    entry.Button));
            }
        }

        // 回归诊断：打印字号/行高/字体度量/基线偏移，以及实际绘制到的相邻基线间距。
        LogLayoutOnce(fontSize, lineHeight, fontMetrics.Ascent, fontMetrics.Descent, baselineOffset, measuredBaselineSpacing);

        canvas.Restore();

        if (window.ThumbHeight > 0)
        {
            var trackWidth = 3f;
            var trackX = (float)viewportWidth - trackWidth - 2f;
            using var track = new SKPaint { Color = new SKColor(255, 255, 255, 24), IsAntialias = false };
            canvas.DrawRect(new SKRect(trackX, 0, trackX + trackWidth, (float)viewportHeight), track);

            using var thumb = new SKPaint { Color = new SKColor(255, 255, 255, 110), IsAntialias = true };
            canvas.DrawRoundRect(
                new SKRect(trackX, (float)window.ThumbY, trackX + trackWidth, (float)(window.ThumbY + window.ThumbHeight)),
                1.5f, 1.5f, thumb);
        }

        return new NativeDrawHits(hits, imageHits);
    }

    /// <summary>打印一次文本布局（字号/行高/字体度量/基线偏移/实测基线间距），数值不变时不重复。</summary>
    private static void LogLayoutOnce(
        float fontSize,
        float lineHeight,
        float ascent,
        float descent,
        float baselineOffset,
        float baselineSpacing)
    {
        var key = (fontSize, lineHeight, ascent, descent);
        if (_lastLoggedLayout == key)
            return;
        _lastLoggedLayout = key;
        Log.Info(LogTag,
            $"text layout: font={fontSize:F1} line={lineHeight:F1} " +
            $"ascent={ascent:F2} descent={descent:F2} baselineOffset={baselineOffset:F2} " +
            $"baselineSpacing={baselineSpacing:F2}");
    }

    /// <summary>
    /// srcm 热区几何：与 Web 的 <c>.term-mask</c> / <c>&lt;img&gt;</c> / <c>.term-crop</c> 三层对应。
    /// 带 <c>crop</c> 时 <c>&lt;img&gt;</c> 是整图（<c>crop.imgWidth/imgHeight</c>，已含显示缩放），
    /// 位置加负偏移 <c>crop.x/y</c>；可见区域仍是掩膜处的 <c>image.width/height</c>。
    /// </summary>
    private static NativeImageHitRegion CreateImageHit(
        NativeImage image,
        NativeButton button,
        float x,
        float lineTop,
        float lineHeight,
        float scale)
    {
        var visibleWidth = NativeGeometry.ImageWidth(image, scale);
        var visibleHeight = Math.Max(0f, image.Height * scale);
        var visibleTop = lineTop + image.YPos * scale;

        var elementX = x;
        var elementY = visibleTop;
        var elementWidth = visibleWidth;
        var elementHeight = visibleHeight;
        if (image.Crop is { } crop)
        {
            elementX = x + crop.X * scale;
            elementY = visibleTop + crop.Y * scale;
            elementWidth = Math.Max(0f, crop.ImgWidth * scale);
            elementHeight = Math.Max(0f, crop.ImgHeight * scale);
        }

        return new NativeImageHitRegion(
            x,
            lineTop,
            visibleWidth,
            lineHeight,
            elementX,
            elementY,
            elementWidth,
            elementHeight,
            x,
            visibleTop,
            visibleWidth,
            visibleHeight,
            image.Src,
            image.Srcm!,
            button);
    }

    private static void DrawBackgrounds(
        SKCanvas canvas,
        NativeFrame frame,
        NativeImageCache images,
        float canvasWidth,
        float canvasHeight,
        float scrollX)
    {
        // WinForms BakeBackground：按 depth 降序（大的先画、在底层；小的后画、在上层）。
        foreach (var background in frame.BackgroundLayers.OrderByDescending(b => b.Depth))
        {
            if (background.Opacity <= 0)
                continue;

            var image = images.Get(background.Src).Image;
            if (image is null || image.Width <= 0 || image.Height <= 0)
                continue;

            var alpha = (byte)Math.Clamp((int)(background.Opacity * 255), 0, 255);
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, alpha),
                IsAntialias = false,
            };

            // 与 Emuera WinForms BakeBackground 一致：等比缩放到「铺满画布」（cover），
            // 取 max(宽比, 高比) 作唯一缩放系数（不拉伸变形）；水平居中、顶部对齐。
            // 画布宽 = 游戏画布宽；横向平移时背景随内容一起平移（Native 额外能力）。
            var coverScale = Math.Max(canvasWidth / image.Width, canvasHeight / image.Height);
            var drawWidth = image.Width * coverScale;
            var drawHeight = image.Height * coverScale;
            var left = (canvasWidth - drawWidth) / 2f - scrollX;
            canvas.DrawImage(
                image,
                new SKRect(left, 0f, left + drawWidth, drawHeight),
                Sampling,
                paint);
        }
    }

    private static void DrawRun(
        SKCanvas canvas,
        Run run,
        SKPaint defaultPaint,
        float x,
        float baseline,
        float fontSize)
    {
        var color = run.Color is null
            ? defaultPaint.Color
            : ParseColor(run.Color, defaultPaint.Color);

        SKPaint paint;
        var ownsPaint = false;
        if (run.Color is null)
        {
            paint = defaultPaint;
        }
        else
        {
            paint = new SKPaint { Color = color, IsAntialias = true };
            ownsPaint = true;
        }

        canvas.DrawText(run.Text, x, baseline, run.Font, paint);

        if (run.Underline || run.Strikeout)
        {
            using var decor = new SKPaint
            {
                Color = color,
                IsAntialias = false,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1f, fontSize / 16f),
            };
            if (run.Underline)
            {
                var y = baseline + fontSize * 0.12f;
                canvas.DrawLine(x, y, x + run.Width, y, decor);
            }
            if (run.Strikeout)
            {
                var y = baseline - fontSize * 0.28f;
                canvas.DrawLine(x, y, x + run.Width, y, decor);
            }
        }

        if (ownsPaint)
            paint.Dispose();
    }

    private static void DrawImage(
        SKCanvas canvas,
        NativeImage image,
        float x,
        float lineTop,
        float scale,
        NativeImageCache images)
    {
        var width = NativeGeometry.ImageWidth(image, scale);
        var height = Math.Max(0f, image.Height * scale);
        if (width <= 0 || height <= 0)
            return;

        var top = lineTop + image.YPos * scale;
        var mask = new SKRect(x, top, x + width, top + height);
        var result = images.Get(image.Src);

        if (result.Image is not { } skImage)
        {
            // 加载中保持空白；缺资源/解码失败画红色占位框，保证可观测。
            if (result.State is NativeImageState.Missing or NativeImageState.Failed)
                DrawImagePlaceholder(canvas, mask);
            return;
        }

        using var paint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = false,
        };

        if (image.Crop is { } crop)
        {
            // 与 Web 的「掩膜 + 负 margin」等价：整图按 imgWidth/imgHeight 绘制，
            // 起点偏移 (x,y)，再裁到 mask。
            canvas.Save();
            canvas.ClipRect(mask);
            var dest = new SKRect(
                mask.Left + crop.X * scale,
                mask.Top + crop.Y * scale,
                mask.Left + (crop.X + crop.ImgWidth) * scale,
                mask.Top + (crop.Y + crop.ImgHeight) * scale);
            canvas.DrawImage(skImage, dest, Sampling, paint);
            canvas.Restore();
        }
        else
        {
            canvas.DrawImage(skImage, mask, Sampling, paint);
        }
    }

    private static void DrawImagePlaceholder(SKCanvas canvas, SKRect mask)
    {
        using var fill = new SKPaint { Color = new SKColor(255, 0, 0, 36), IsAntialias = false };
        using var border = new SKPaint
        {
            Color = new SKColor(255, 0, 0, 120),
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        };
        canvas.DrawRect(mask, fill);
        canvas.DrawRect(mask, border);
    }

    private static void DrawShape(SKCanvas canvas, NativeShape shape, float x, float lineTop, float scale)
    {
        if (!string.Equals(shape.Type, "rect", StringComparison.OrdinalIgnoreCase))
            return;

        var left = x + shape.X * scale;
        var top = lineTop + shape.Y * scale;
        var width = Math.Max(0f, shape.Width * scale);
        var height = Math.Max(0f, shape.Height * scale);
        if (width <= 0 || height <= 0)
            return;

        using var paint = new SKPaint
        {
            Color = ParseColor(shape.Color, new SKColor(0x80, 0x80, 0x80)),
            IsAntialias = false,
        };
        canvas.DrawRect(new SKRect(left, top, left + width, top + height), paint);
    }

    private static Variant PickVariant(NativeSegment segment)
    {
        if (segment.Bold)
            return segment.Italic ? Variant.BoldItalic : Variant.Bold;
        return segment.Italic ? Variant.Italic : Variant.Regular;
    }

    /// <summary>
    /// 按码点选择「内置字体 → 系统回退字体」的 typeface，把文本切成同字体的连续 run。
    /// 每个 run 用同一种字体度量与绘制，宽度总和与命中区保持一致。
    /// </summary>
    private static List<Run> SplitByTypeface(NativeSegment segment, Variant variant, FontCache fonts)
    {
        var text = segment.Text;
        var runs = new List<Run>();
        var start = 0;
        var current = NativeFontManager.Resolve(text[0]);
        for (var i = 1; i < text.Length; i++)
        {
            var typeface = NativeFontManager.Resolve(text[i]);
            if (typeface == current)
                continue;
            runs.Add(MakeRun(segment, text.Substring(start, i - start), current, variant, fonts));
            start = i;
            current = typeface;
        }
        runs.Add(MakeRun(segment, text.Substring(start), current, variant, fonts));
        return runs;
    }

    private static Run MakeRun(
        NativeSegment segment,
        string text,
        SKTypeface typeface,
        Variant variant,
        FontCache fonts)
    {
        var font = fonts.Get(typeface, variant);
        var width = font.MeasureText(text);
        return new Run(text, font, width, segment.Color, segment.Underline, segment.Strikeout);
    }

    /// <summary>按 (typeface, 变体) 缓存本次绘制的 <see cref="SKFont"/>；绘制结束统一释放。</summary>
    private sealed class FontCache : IDisposable
    {
        private readonly float _size;
        private readonly Dictionary<(SKTypeface, Variant), SKFont> _fonts = new();

        public FontCache(float size) => _size = size;

        public SKFont Get(SKTypeface typeface, Variant variant)
        {
            var key = (typeface, variant);
            if (_fonts.TryGetValue(key, out var cached))
                return cached;

            var font = new SKFont(typeface, _size);
            switch (variant)
            {
                case Variant.Bold:
                    font.Embolden = true;
                    break;
                case Variant.Italic:
                    font.SkewX = -0.25f;
                    break;
                case Variant.BoldItalic:
                    font.Embolden = true;
                    font.SkewX = -0.25f;
                    break;
            }
            _fonts[key] = font;
            return font;
        }

        public void Dispose()
        {
            foreach (var font in _fonts.Values)
                font.Dispose();
            _fonts.Clear();
        }
    }

    /// <summary>"#RRGGBB" → SKColor；非法值回退。</summary>
    private static SKColor ParseColor(string? hex, SKColor fallback)
    {
        if (hex is not { Length: 7 } || hex[0] != '#')
            return fallback;

        var r = HexByte(hex, 1);
        var g = HexByte(hex, 3);
        var b = HexByte(hex, 5);
        return r < 0 || g < 0 || b < 0
            ? fallback
            : new SKColor((byte)r, (byte)g, (byte)b);
    }

    private static int HexByte(string text, int offset)
    {
        var high = HexValue(text[offset]);
        var low = HexValue(text[offset + 1]);
        return high < 0 || low < 0 ? -1 : high * 16 + low;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
