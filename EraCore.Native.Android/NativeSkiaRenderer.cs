using System.Collections.Concurrent;
using EraCore.Hosting;
using EraCore.Native;
using MinorShift.Emuera.Sessions;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// SKCanvas 文本终端绘制器（M2）。
///
/// <para>只做「文本 + 样式 + 颜色 + 对齐 + 可见行 + 按钮命中区」；图像/精灵/动画不在范围。
/// 行高/字号/窗宽/前景色来自 <see cref="HostMetrics"/>；可见区间与滚动条比例来自
/// <see cref="VirtualWindow"/>。返回值是本次绘制的按钮命中区（基于实际 SKFont 度量的 run 位置，
/// 不使用 col×FontSize/2）。</para>
/// </summary>
internal static class NativeSkiaRenderer
{
    private static readonly ConcurrentDictionary<string, SKTypeface> BaseTypefaces = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<StyleKey, SKTypeface> StyledTypefaces = new();

    private readonly record struct StyleKey(string Family, bool Bold, bool Italic);
    private readonly record struct Run(NativeSegment Segment, SKFont Font, float Width);
    private readonly record struct EntryLayout(float StartX, float Width, NativeButton? Button);

    public static IReadOnlyList<NativeHitRegion> Draw(
        SKCanvas canvas,
        double viewportWidth,
        double viewportHeight,
        NativeFrame frame,
        HostMetrics metrics,
        double scrollX,
        double scrollY)
    {
        canvas.Clear(ParseColor(frame.BackgroundColor, new SKColor(0, 0, 0)));
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return Array.Empty<NativeHitRegion>();

        var lineHeight = Math.Max(1f, (float)metrics.LineHeight);
        var fontSize = Math.Max(1f, (float)metrics.FontSize);
        var maxScrollX = Math.Max(0f, metrics.WindowWidth - (float)viewportWidth);
        var sx = Math.Clamp((float)scrollX, 0f, maxScrollX);

        var window = VirtualWindow.Compute(
            frame.Lines.Count,
            scrollY,
            viewportHeight,
            new VirtualWindowOptions(metrics.LineHeight));
        var sy = (float)window.ScrollY;

        var baseTypeface = ResolveBaseTypeface(metrics.FontName);
        using var regular = new SKFont(Styled(baseTypeface, metrics.FontName, bold: false, italic: false), fontSize);
        using var bold = new SKFont(Styled(baseTypeface, metrics.FontName, bold: true, italic: false), fontSize);
        using var italic = new SKFont(Styled(baseTypeface, metrics.FontName, bold: false, italic: true), fontSize);
        using var boldItalic = new SKFont(Styled(baseTypeface, metrics.FontName, bold: true, italic: true), fontSize);

        using var defaultPaint = new SKPaint
        {
            Color = ParseColor(metrics.ForeColor, new SKColor(0xC0, 0xC0, 0xC0)),
            IsAntialias = true,
        };

        var fontMetrics = regular.Metrics;
        var textHeight = fontMetrics.Descent - fontMetrics.Ascent;
        var baselineOffset = (lineHeight - textHeight) / 2f - fontMetrics.Ascent;

        var hits = new List<NativeHitRegion>();

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewportWidth, (float)viewportHeight));

        for (var index = window.StartIndex; index < window.EndIndexExclusive; index++)
        {
            var line = frame.Lines[index];
            var runs = new List<Run>(line.Entries.Count);
            var entries = new List<EntryLayout>(line.Entries.Count);
            var total = 0f;

            foreach (var entry in line.Entries)
            {
                var entryStart = total;
                foreach (var segment in entry.Segments)
                {
                    if (segment.Text.Length == 0)
                        continue;
                    var font = PickFont(segment, regular, bold, italic, boldItalic);
                    var width = font.MeasureText(segment.Text);
                    runs.Add(new Run(segment, font, width));
                    total += width;
                }
                entries.Add(new EntryLayout(entryStart, total - entryStart, entry.Button));
            }

            if (runs.Count == 0)
                continue;

            var startX = -sx + (float)AlignGeometry.OffsetPixels(line.Align, total, metrics.WindowWidth);
            var lineTop = index * lineHeight - sy;
            var baseline = lineTop + baselineOffset;
            var x = startX;

            foreach (var run in runs)
            {
                var color = run.Segment.Color is null
                    ? defaultPaint.Color
                    : ParseColor(run.Segment.Color, defaultPaint.Color);

                SKPaint paint;
                var ownsPaint = false;
                if (run.Segment.Color is null)
                {
                    paint = defaultPaint;
                }
                else
                {
                    paint = new SKPaint { Color = color, IsAntialias = true };
                    ownsPaint = true;
                }

                canvas.DrawText(run.Segment.Text, x, baseline, run.Font, paint);

                if (run.Segment.Underline || run.Segment.Strikeout)
                {
                    using var decor = new SKPaint
                    {
                        Color = color,
                        IsAntialias = false,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = Math.Max(1f, fontSize / 16f),
                    };
                    if (run.Segment.Underline)
                    {
                        var y = baseline + fontSize * 0.12f;
                        canvas.DrawLine(x, y, x + run.Width, y, decor);
                    }
                    if (run.Segment.Strikeout)
                    {
                        var y = baseline - fontSize * 0.28f;
                        canvas.DrawLine(x, y, x + run.Width, y, decor);
                    }
                }

                if (ownsPaint)
                    paint.Dispose();

                x += run.Width;
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

    private static SKFont PickFont(NativeSegment segment, SKFont regular, SKFont bold, SKFont italic, SKFont boldItalic)
    {
        if (segment.Bold)
            return segment.Italic ? boldItalic : bold;
        return segment.Italic ? italic : regular;
    }

    private static SKTypeface ResolveBaseTypeface(string family)
    {
        if (!string.IsNullOrWhiteSpace(family) && BaseTypefaces.TryGetValue(family, out var cached))
            return cached;

        SKTypeface? typeface = null;
        if (!string.IsNullOrWhiteSpace(family))
            typeface = SKFontManager.Default.MatchFamily(family);

        // Android 没有 'ＭＳ ゴシック'：退到系统中文字体（通常是 Noto Sans CJK）。
        typeface ??= SKFontManager.Default.MatchCharacter('漢');
        typeface ??= SKFontManager.Default.MatchCharacter('あ');
        typeface ??= SKTypeface.Default;

        if (!string.IsNullOrWhiteSpace(family))
            BaseTypefaces[family] = typeface;
        return typeface;
    }

    private static SKTypeface Styled(SKTypeface baseTypeface, string requestedFamily, bool bold, bool italic)
    {
        if (!bold && !italic)
            return baseTypeface;

        var family = baseTypeface.FamilyName ?? requestedFamily;
        var key = new StyleKey(family, bold, italic);
        return StyledTypefaces.GetOrAdd(key, _ =>
        {
            var style = bold && italic
                ? SKFontStyle.BoldItalic
                : bold
                    ? SKFontStyle.Bold
                    : SKFontStyle.Italic;
            return SKFontManager.Default.MatchFamily(family, style) ?? baseTypeface;
        });
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
