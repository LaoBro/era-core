using System;
using System.Collections.Generic;
using System.Linq;
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
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.None);

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
        NativeShape? Shape);

    public static IReadOnlyList<NativeHitRegion> Draw(
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
            return Array.Empty<NativeHitRegion>();

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
        var textHeight = fontMetrics.Descent - fontMetrics.Ascent;
        var baselineOffset = (lineHeight - textHeight) / 2f - fontMetrics.Ascent;

        var hits = new List<NativeHitRegion>();

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewportWidth, (float)viewportHeight));

        DrawBackgrounds(canvas, frame, images);

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
                            ops.Add(new DrawOp(run.Width, run, null, null));
                            total += run.Width;
                        }
                    }

                    if (segment.Image is { } image)
                    {
                        var width = NativeGeometry.ImageWidth(image, scale);
                        ops.Add(new DrawOp(width, null, image, null));
                        total += width;
                    }

                    if (segment.Shape is { } shape)
                    {
                        // 与 Web 掩膜一致：形状占 x + width，起笔从 x 开始。
                        var width = NativeGeometry.ShapeWidth(shape, scale);
                        ops.Add(new DrawOp(width, null, null, shape));
                        total += width;
                    }
                }
                entries.Add(new EntryLayout(entryStart, total - entryStart, entry.Button));
            }

            if (ops.Count == 0)
                continue;

            var startX = -sx + (float)AlignGeometry.OffsetPixels(line.Align, total, style.ContentWidth);
            var lineTop = index * lineHeight - sy;
            var baseline = lineTop + baselineOffset;
            var x = startX;

            foreach (var op in ops)
            {
                if (op.Run is { } run)
                {
                    DrawRun(canvas, run, defaultPaint, x, baseline, fontSize);
                }
                else if (op.Image is { } image)
                {
                    DrawImage(canvas, image, x, lineTop, scale, images);
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

        return hits;
    }

    private static void DrawBackgrounds(SKCanvas canvas, NativeFrame frame, NativeImageCache images)
    {
        foreach (var background in frame.BackgroundLayers.OrderBy(b => b.Depth))
        {
            if (background.Opacity <= 0)
                continue;

            var image = images.Get(background.Src).Image;
            if (image is null)
                continue;

            var alpha = (byte)Math.Clamp((int)(background.Opacity * 255), 0, 255);
            using var paint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, alpha),
                IsAntialias = false,
            };
            // 背景固定不随内容滚动；超出视口的部分由外层 clip 截断。
            canvas.DrawImage(image, new SKRect(0, 0, image.Width, image.Height), Sampling, paint);
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
