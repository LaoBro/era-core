using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>
    /// 字体身份的稳定解析：<c>NativeFontManager.Resolve</c> 返回静态缓存的同一实例，
    /// 故身份对象可跨帧保存在行布局缓存里（缓存不持有 <see cref="SKFont"/>）。
    /// </summary>
    private static readonly Func<char, object> ResolveTypeface = static c => NativeFontManager.Resolve(c);

    public static NativeDrawHits Draw(
        SKCanvas canvas,
        double viewportWidth,
        double viewportHeight,
        NativeFrame frame,
        NativeRenderStyle style,
        NativeImageCache images,
        double scrollX,
        double scrollY,
        NativeLineLayoutCache layoutCache,
        NativeFontPool<SKFont> fonts,
        NativePaintPool paints,
        NativeTextBlobCache textBlobs
#if DEBUG
        , NativeFrameTelemetry telemetry
#endif
        )
    {
        using var traceDraw = NativeTrace.Section("era:draw");
        // 精确分项：只在 trace 开关打开时用 Stopwatch 累加（Release AOT 下一次 GetTimestamp ≈ 数十 ns），
        // 每 50 帧汇总打一条日志；ATrace 逐 op 分片在本机（MT6763）每次约 23µs，会把 11ms 帧放大到 15ms，
        // 因此分项走这里、整帧用 era:draw 分片交叉校验。
        var traceAgg = NativeTrace.Enabled;
        var aggDrawStart = traceAgg ? Stopwatch.GetTimestamp() : 0;
        long aggClear = 0, aggPrep = 0, aggBg = 0, aggPlan = 0, aggLines = 0;
        long aggText = 0, aggImage = 0, aggShape = 0, aggLayoutHit = 0, aggLayoutMiss = 0, aggHits = 0;
        long aggChrome = 0;
        var aggRuns = 0;
        var aggImages = 0;
        var aggShapes = 0;
        var aggLayoutHits = 0;
        var aggLayoutMisses = 0;
        var aggHitRegions = 0;
#if DEBUG
        ref var sample = ref telemetry.Sample;
        sample.DrawStartTicks = Stopwatch.GetTimestamp();
        sample.AllocStart = GC.GetAllocatedBytesForCurrentThread();
        sample.Lines = frame.Lines.Count;
        sample.Extents = frame.ImageExtentList.Count;
#endif
        var aggMark = traceAgg ? Stopwatch.GetTimestamp() : 0;
        using (NativeTrace.Section("era:clear"))
            canvas.Clear(ParseColor(style.BackgroundOverride ?? frame.BackgroundColor, new SKColor(0, 0, 0)));
        if (traceAgg)
            aggClear += Stopwatch.GetTimestamp() - aggMark;
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return NativeDrawHits.Empty;

        var aggPrepMark = traceAgg ? Stopwatch.GetTimestamp() : 0;
        var lineHeight = (float)Math.Max(1, style.LineHeight);
        var fontSize = (float)Math.Max(1, style.FontSize);
        var scale = (float)Math.Max(0.1, style.Scale);
        var maxScrollX = Math.Max(0f, (float)style.ContentWidth - (float)viewportWidth);
        var sx = Math.Clamp((float)scrollX, 0f, maxScrollX);
        var prepScope = NativeTrace.Section("era:prep");

        // 上一帧绘制已结束，帧起点是安全的池修剪时机。
        paints.BeginFrame();

        var window = VirtualWindow.Compute(
            frame.Lines.Count,
            scrollY,
            viewportHeight,
            new VirtualWindowOptions(style.LineHeight));
        var sy = (float)window.ScrollY;

        // 行布局缓存：frame 引用或 style 全字段变化时整体清空（口径与原 NativeImageLayoutCache 一致）。
        layoutCache.Prepare(frame, style);
        // 文本 blob 缓存与行布局共用同一失效口径；滚动帧内复用；换帧/换 style 清空。
        textBlobs.Prepare(frame, style);
#if DEBUG
        // 命中/未命中按「本次 Draw 的增量」上报；累计值会在同一帧的多次重绘里越滚越大，不可当速率看。
        var cacheHitsStart = layoutCache.Hits;
        var cacheMissesStart = layoutCache.Misses;
        var blobHitsStart = textBlobs.Hits;
        var blobMissesStart = textBlobs.Misses;
#endif

        var defaultPaint = paints.Get(
            ParseColor(style.ForeColor, new SKColor(0xC0, 0xC0, 0xC0)),
            SKPaintStyle.Fill,
            0f,
            antialias: true);

        var measureFont = fonts.Get(NativeFontManager.Embedded, NativeFontVariant.Regular, fontSize);
        var fontMetrics = measureFont.Metrics;
        // 行高只来自 style.LineHeight；字体度量只决定「行内」基线位置（WinForms 顶对齐 = -Ascent）。
        var baselineOffset = (float)NativeTextLayout.BaselineOffset(fontMetrics.Ascent);
        prepScope.Dispose();
        if (traceAgg)
            aggPrep += Stopwatch.GetTimestamp() - aggPrepMark;

        var hits = new List<NativeHitRegion>();
        var imageHits = new List<NativeImageHitRegion>();

#if DEBUG
        long layoutTicks = 0;
        long paintTicks = 0;
        long paintTextTicks = 0;
        long paintImageTicks = 0;
        long paintShapeTicks = 0;
        var runs = 0;
        var chars = 0;
        var imageDraws = 0;
        var textOps = 0;
        var blobFallbacks = 0;
#endif

        // 量测委托按 (文本, 字体身份, 变体) 走长期字体池；每帧只分配这一个闭包。
        var measureText = (string text, object typeface, NativeFontVariant variant) =>
            fonts.Get(typeface, variant, fontSize).MeasureText(text);

        // 按行布局缓存：命中直接复用实测宽度；未命中才量测（逐字符 Resolve + MeasureText + Substring）。
        // 窗口行与窗口外锚定行共用这一层，不再有第二套图片布局缓存。
        NativeLineLayout GetLayout(int index)
        {
            if (traceAgg)
            {
                var hitStart = Stopwatch.GetTimestamp();
                if (layoutCache.TryGet(index) is { } cached)
                {
                    aggLayoutHit += Stopwatch.GetTimestamp() - hitStart;
                    aggLayoutHits++;
                    return cached;
                }
                aggLayoutHit += Stopwatch.GetTimestamp() - hitStart;
                aggLayoutHits++;
                var missStart = Stopwatch.GetTimestamp();
                var builtHit = BuildAndStore(index);
                aggLayoutMiss += Stopwatch.GetTimestamp() - missStart;
                aggLayoutMisses++;
                return builtHit;
            }

            if (layoutCache.TryGet(index) is { } cacheHit)
                return cacheHit;
            return BuildAndStore(index);
        }

        NativeLineLayout BuildAndStore(int index)
        {
#if DEBUG
            var layoutStart = Stopwatch.GetTimestamp();
#endif
            var built = NativeLineLayoutBuilder.Build(
                frame.Lines[index],
                scale,
                ResolveTypeface,
                measureText,
                out var measuredRuns,
                out var measuredChars);
#if DEBUG
            layoutTicks += Stopwatch.GetTimestamp() - layoutStart;
            runs += measuredRuns;
            chars += measuredChars;
#endif
            layoutCache.Store(index, built);
            return built;
        }

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewportWidth, (float)viewportHeight));

        // 背景的烘焙画布与 WinForms 一致：宽 = 游戏画布宽（不是视口宽），高 = 视口高。
#if DEBUG
        var backgroundStart = Stopwatch.GetTimestamp();
#endif
        int backgroundLayers;
        var aggBgMark = traceAgg ? Stopwatch.GetTimestamp() : 0;
        using (NativeTrace.Section("era:paint.bg"))
            backgroundLayers = DrawBackgrounds(
                canvas,
                frame,
                images,
                paints,
                (float)style.ContentWidth,
                (float)viewportHeight,
                sx);
        if (traceAgg)
            aggBg += Stopwatch.GetTimestamp() - aggBgMark;
#if DEBUG
        var backgroundTicks = Stopwatch.GetTimestamp() - backgroundStart;
        paintTicks += backgroundTicks;
#endif

        float? previousBaseline = null;
        var previousIndex = 0;
        var measuredBaselineSpacing = lineHeight;

        // 窗口行：整行绘制（文本 + 内联图片），顺序与 old 行循环完全一致。
        void DrawLineSlot(int index)
        {
            var aggLineMark = traceAgg ? Stopwatch.GetTimestamp() : 0;
            var line = frame.Lines[index];
            var layout = GetLayout(index);
            if (layout.Ops.Count == 0)
            {
                if (traceAgg)
                    aggLines += Stopwatch.GetTimestamp() - aggLineMark;
                return;
            }
#if DEBUG
            var paintStart = Stopwatch.GetTimestamp();
#endif

            // WinForms：文本/图像/形状统一起点右移 ShapePositionShift，
            // 对齐宽度 = Config.DrawableWidth（game width，不是视口宽度）。
            var startX = (float)style.Inset - sx
                + (float)AlignGeometry.OffsetPixels(line.Align, layout.TotalWidth, style.DrawableWidth);
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

            foreach (var op in layout.Ops)
            {
                if (op.RunIndex >= 0)
                {
                    var run = layout.Runs[op.RunIndex];
#if DEBUG
                    var opStart = Stopwatch.GetTimestamp();
                    blobFallbacks += DrawRun(
                        canvas,
                        run,
                        fonts.Get(run.Typeface, run.Variant, fontSize),
                        defaultPaint,
                        paints,
                        textBlobs,
                        x,
                        baseline,
                        fontSize) ? 0 : 1;
                    var paintOpTicks = Stopwatch.GetTimestamp();
                    paintTextTicks += paintOpTicks - opStart;
                    textOps++;
#else
                    if (traceAgg)
                    {
                        var opStart = Stopwatch.GetTimestamp();
                        DrawRun(
                            canvas,
                            run,
                            fonts.Get(run.Typeface, run.Variant, fontSize),
                            defaultPaint,
                            paints,
                            textBlobs,
                            x,
                            baseline,
                            fontSize);
                        aggText += Stopwatch.GetTimestamp() - opStart;
                        aggRuns++;
                    }
                    else
                    {
                        DrawRun(
                            canvas,
                            run,
                            fonts.Get(run.Typeface, run.Variant, fontSize),
                            defaultPaint,
                            paints,
                            textBlobs,
                            x,
                            baseline,
                            fontSize);
                    }
#endif
                }
                else if (op.Image is { } image)
                {
                    var opStart = traceAgg ? Stopwatch.GetTimestamp() : 0;
                    DrawImageAndHit(image, op.Button, x, lineTop);
                    if (traceAgg)
                    {
                        aggImage += Stopwatch.GetTimestamp() - opStart;
                        aggImages++;
                    }
                }
                else if (op.Shape is { } shape)
                {
                    var opStart = traceAgg ? Stopwatch.GetTimestamp() : 0;
                    DrawShape(canvas, shape, paints, x, lineTop, scale);
                    if (traceAgg)
                    {
                        aggShape += Stopwatch.GetTimestamp() - opStart;
                        aggShapes++;
                    }
                }

                x += op.Width;
            }

            var aggHitMark = traceAgg ? Stopwatch.GetTimestamp() : 0;
            foreach (var entry in layout.Entries)
            {
                if (entry.Button is null || entry.Width <= 0)
                    continue;
                hits.Add(new NativeHitRegion(
                    startX + entry.StartX,
                    lineTop,
                    entry.Width,
                    lineHeight,
                    entry.Button));
                aggHitRegions++;
            }
            if (traceAgg)
                aggHits += Stopwatch.GetTimestamp() - aggHitMark;

            if (traceAgg)
                aggLines += Stopwatch.GetTimestamp() - aggLineMark;

#if DEBUG
            if (traceAgg)
                paintTicks += Stopwatch.GetTimestamp() - paintStart;
#endif
        }

        // 锚定行在窗口外的跨界图片：只画图，不画该行文本；x 由同一按行缓存布局算。
        void DrawImageSlot(NativeImageExtent extent)
        {
            var line = frame.Lines[extent.LineIndex];
            var layout = GetLayout(extent.LineIndex);
            var prefix = layout.PrefixOf(extent.EntryIndex, extent.SegmentIndex);
            if (prefix is null)
                return;

            var startX = (float)style.Inset - sx
                + (float)AlignGeometry.OffsetPixels(line.Align, layout.TotalWidth, style.DrawableWidth);
            var lineTop = (float)NativeTextLayout.LineTop(
                extent.LineIndex,
                lineHeight,
                sy,
                frame.Lines.Count,
                viewportHeight);
#if DEBUG
            var paintStart = Stopwatch.GetTimestamp();
#endif
            DrawImageAndHit(extent.Image, extent.Button, startX + prefix.Value, lineTop);
#if DEBUG
            var imageTicks = Stopwatch.GetTimestamp() - paintStart;
            paintTicks += imageTicks;
            paintImageTicks += imageTicks;
#endif
        }

        void DrawImageAndHit(NativeImage image, NativeButton? button, float x, float lineTop)
        {
            DrawImage(canvas, image, x, lineTop, scale, images, paints);
#if DEBUG
            imageDraws++;
#endif
            if (button is { } b && !string.IsNullOrEmpty(image.Srcm))
                imageHits.Add(CreateImageHit(image, b, x, lineTop, lineHeight, scale));
        }

        // 唯一绘制序列：上方表 → 窗口行（内联）→ 下方表。禁止两趟式分层。
#if DEBUG
        var planStart = Stopwatch.GetTimestamp();
#endif
        IReadOnlyList<NativeDrawStep> plan;
        var aggPlanMark = traceAgg ? Stopwatch.GetTimestamp() : 0;
        using (NativeTrace.Section("era:plan"))
            plan = NativeImageViewport.Plan(
                frame,
                lineHeight,
                sy,
                viewportHeight,
                scale,
                window.StartIndex,
                window.EndIndexExclusive);
        if (traceAgg)
            aggPlan += Stopwatch.GetTimestamp() - aggPlanMark;
#if DEBUG
        var planTicks = Stopwatch.GetTimestamp() - planStart;
#endif

        foreach (var step in plan)
        {
            if (step.DrawLine)
                DrawLineSlot(step.LineIndex);
            else if (step.Image is { } extent)
                DrawImageSlot(extent);
        }

        // 回归诊断：打印字号/行高/字体度量/基线偏移，以及实际绘制到的相邻基线间距。
        LogLayoutOnce(fontSize, lineHeight, fontMetrics.Ascent, fontMetrics.Descent, baselineOffset, measuredBaselineSpacing);

        canvas.Restore();

#if DEBUG
        var scrollbarStart = Stopwatch.GetTimestamp();
#endif
        var aggChromeMark = traceAgg ? Stopwatch.GetTimestamp() : 0;
        using (NativeTrace.Section("era:paint.chrome"))
        {
            if (window.ThumbHeight > 0)
            {
                var trackWidth = 3f;
                var trackX = (float)viewportWidth - trackWidth - 2f;
                canvas.DrawRect(
                    new SKRect(trackX, 0, trackX + trackWidth, (float)viewportHeight),
                    paints.Get(new SKColor(255, 255, 255, 24)));

                canvas.DrawRoundRect(
                    new SKRect(trackX, (float)window.ThumbY, trackX + trackWidth, (float)(window.ThumbY + window.ThumbHeight)),
                    1.5f, 1.5f,
                    paints.Get(new SKColor(255, 255, 255, 110), SKPaintStyle.Fill, 0f, antialias: true));
            }
        }
        if (traceAgg)
            aggChrome += Stopwatch.GetTimestamp() - aggChromeMark;
#if DEBUG
        var chromeTicks = Stopwatch.GetTimestamp() - scrollbarStart;
        paintTicks += chromeTicks;

        sample.PlanTicks = planTicks;
        sample.LayoutTicks = layoutTicks;
        sample.PaintTicks = paintTicks;
        sample.PaintTextTicks = paintTextTicks;
        sample.PaintBackgroundTicks = backgroundTicks;
        sample.PaintImageTicks = paintImageTicks;
        sample.PaintShapeTicks = paintShapeTicks;
        sample.PaintChromeTicks = chromeTicks;
        sample.BackgroundLayers = backgroundLayers;
        sample.TextOps = textOps;
        sample.TextBlobHits = textBlobs.Hits - blobHitsStart;
        sample.TextBlobMisses = textBlobs.Misses - blobMissesStart;
        sample.TextBlobFallbacks = blobFallbacks;
        sample.PlanSteps = plan.Count;
        sample.VisibleLines = window.EndIndexExclusive - window.StartIndex;
        sample.ImageDraws = imageDraws;
        sample.Runs = runs;
        sample.Chars = chars;
        sample.LayoutHits = layoutCache.Hits - cacheHitsStart;
        sample.LayoutMisses = layoutCache.Misses - cacheMissesStart;
#endif

        if (traceAgg)
            FlushAggregate(
                aggDrawStart, aggClear, aggPrep, aggBg, aggPlan, aggLines, aggText, aggImage, aggShape,
                aggLayoutHit, aggLayoutMiss, aggHits, aggChrome,
                aggRuns, aggImages, aggShapes, aggLayoutHits, aggLayoutMisses, aggHitRegions);

        return new NativeDrawHits(hits, imageHits);
    }

    /// <summary>每秒汇总一次分项耗时到 logcat（仅 trace 开关打开时调用）。</summary>
    private static void FlushAggregate(
        long drawStart,
        long clear, long prep, long bg, long plan, long lines, long text, long image, long shape,
        long layoutHit, long layoutMiss, long hits, long chrome,
        int runs, int images, int shapes, int layoutHits, int layoutMisses, int hitRegions)
    {
        var now = Stopwatch.GetTimestamp();
        var frames = ++_aggFrames;
        _aggDraw += now - drawStart;
        _aggClear += clear;
        _aggPrep += prep;
        _aggBg += bg;
        _aggPlan += plan;
        _aggLines += lines;
        _aggText += text;
        _aggImage += image;
        _aggShape += shape;
        _aggLayoutHit += layoutHit;
        _aggLayoutMiss += layoutMiss;
        _aggHits += hits;
        _aggChrome += chrome;
        _aggRuns += runs;
        _aggImages += images;
        _aggShapes += shapes;
        _aggLayoutHits += layoutHits;
        _aggLayoutMisses += layoutMisses;
        _aggHitRegions += hitRegions;

        if (_aggLastLog == 0)
            _aggLastLog = now;
        if (now - _aggLastLog < Stopwatch.Frequency)
            return;

        var ticksToMs = 1000.0 / Stopwatch.Frequency;
        var n = _aggFrames;
        Log.Info("EraTraceAgg",
            $"frames={n} draw={_aggDraw * ticksToMs / n:F2} clear={_aggClear * ticksToMs / n:F3} " +
            $"prep={_aggPrep * ticksToMs / n:F3} bg={_aggBg * ticksToMs / n:F3} plan={_aggPlan * ticksToMs / n:F3} " +
            $"lines={_aggLines * ticksToMs / n:F2} text={_aggText * ticksToMs / n:F2} " +
            $"img={_aggImage * ticksToMs / n:F2} shape={_aggShape * ticksToMs / n:F3} " +
            $"layoutHit={_aggLayoutHit * ticksToMs / n:F3} layoutMiss={_aggLayoutMiss * ticksToMs / n:F3} " +
            $"hits={_aggHits * ticksToMs / n:F3} chrome={_aggChrome * ticksToMs / n:F3} " +
            $"runs/f={_aggRuns / (double)n:F1} imgs/f={_aggImages / (double)n:F1} shapes/f={_aggShapes / (double)n:F1} " +
            $"layoutHit/f={_aggLayoutHits / (double)n:F1} layoutMiss/f={_aggLayoutMisses / (double)n:F1} " +
            $"hitRegions/f={_aggHitRegions / (double)n:F1}");

        _aggFrames = _aggDraw = _aggClear = _aggPrep = _aggBg = _aggPlan = _aggLines = 0;
        _aggText = _aggImage = _aggShape = _aggLayoutHit = _aggLayoutMiss = _aggHits = _aggChrome = 0;
        _aggRuns = _aggImages = _aggShapes = _aggLayoutHits = _aggLayoutMisses = _aggHitRegions = 0;
        _aggLastLog = now;
    }

    private static long _aggLastLog;
    private static long _aggFrames, _aggDraw, _aggClear, _aggPrep, _aggBg, _aggPlan, _aggLines;
    private static long _aggText, _aggImage, _aggShape, _aggLayoutHit, _aggLayoutMiss, _aggHits, _aggChrome;
    private static long _aggRuns, _aggImages, _aggShapes, _aggLayoutHits, _aggLayoutMisses, _aggHitRegions;

    /// <summary>
    /// 字体池工厂（Android 侧）：按 (字体身份, 变体, 字号) 创建 <see cref="SKFont"/>。
    /// 池按字号变化整体 Dispose，因此不会跨字号复用；变体通过 <c>Embolden/SkewX</c> 模拟。
    /// </summary>
    internal static SKFont CreateFont(object typeface, NativeFontVariant variant, float size)
    {
        var font = new SKFont((SKTypeface)typeface, size);
        switch (variant)
        {
            case NativeFontVariant.Bold:
                font.Embolden = true;
                break;
            case NativeFontVariant.Italic:
                font.SkewX = -0.25f;
                break;
            case NativeFontVariant.BoldItalic:
                font.Embolden = true;
                font.SkewX = -0.25f;
                break;
        }
        return font;
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

    /// <summary>按 depth 降序绘制背景层，返回实际绘制的层数（跳过透明层与未解码层）。</summary>
    private static int DrawBackgrounds(
        SKCanvas canvas,
        NativeFrame frame,
        NativeImageCache images,
        NativePaintPool paints,
        float canvasWidth,
        float canvasHeight,
        float scrollX)
    {
        var drawn = 0;

        // WinForms BakeBackground：按 depth 降序（大的先画、在底层；小的后画、在上层）。
        foreach (var background in frame.BackgroundLayers.OrderByDescending(b => b.Depth))
        {
            if (background.Opacity <= 0)
                continue;

            var image = images.Get(background.Src).Image;
            if (image is null || image.Width <= 0 || image.Height <= 0)
                continue;

            var alpha = (byte)Math.Clamp((int)(background.Opacity * 255), 0, 255);
            var paint = paints.Get(new SKColor(255, 255, 255, alpha));

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
            drawn++;
        }

        return drawn;
    }

    /// <summary>
    /// 绘制一个文本 run。返回是否走了 blob 路径（false = 因不保证几何一致而回退到直接 DrawText）。
    /// 两条路径使用同一支字体、同一坐标、同一 paint，仅「预先 shaping」与「即时 shaping」的区别。
    /// </summary>
    private static bool DrawRun(
        SKCanvas canvas,
        NativeLayoutRun run,
        SKFont font,
        SKPaint defaultPaint,
        NativePaintPool paints,
        NativeTextBlobCache textBlobs,
        float x,
        float baseline,
        float fontSize)
    {
        var color = run.Color is null
            ? defaultPaint.Color
            : ParseColor(run.Color, defaultPaint.Color);

        var paint = run.Color is null
            ? defaultPaint
            : paints.Get(color, SKPaintStyle.Fill, 0f, antialias: true);

        // 代理对（BMP 外字符）在 blob 与 DrawText 的 UTF-16→glyph 映射上不保证逐像素一致，
        // 这类 run 回退到直接绘制；ERB 文本里几乎不出现，回退占比应由诊断字段可观测。
        // blob 创建失败（SKTextBlob.Create 返回 null）同样回退。
        var usedBlob = !HasSurrogatePair(run.Text);
        var blob = usedBlob
            ? textBlobs.Get(run.Text, run.Typeface, run.Variant, fontSize, font)
            : null;

        if (blob is not null)
        {
            canvas.DrawText(blob, x, baseline, paint);
        }
        else
        {
            usedBlob = false;
            canvas.DrawText(run.Text, x, baseline, font, paint);
        }

        if (run.Underline || run.Strikeout)
        {
            var decor = paints.Get(
                color,
                SKPaintStyle.Stroke,
                Math.Max(1f, fontSize / 16f),
                antialias: false);
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

        return usedBlob;
    }

    /// <summary>文本中是否存在成对的 UTF-16 代理（BMP 外字符）。</summary>
    private static bool HasSurrogatePair(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
                return true;
        }
        return false;
    }

    private static void DrawImage(
        SKCanvas canvas,
        NativeImage image,
        float x,
        float lineTop,
        float scale,
        NativeImageCache images,
        NativePaintPool paints)
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
                DrawImagePlaceholder(canvas, mask, paints);
            return;
        }

        var paint = paints.Get(SKColors.White);

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

    private static void DrawImagePlaceholder(SKCanvas canvas, SKRect mask, NativePaintPool paints)
    {
        canvas.DrawRect(mask, paints.Get(new SKColor(255, 0, 0, 36)));
        canvas.DrawRect(
            mask,
            paints.Get(new SKColor(255, 0, 0, 120), SKPaintStyle.Stroke, 1f));
    }

    private static void DrawShape(
        SKCanvas canvas,
        NativeShape shape,
        NativePaintPool paints,
        float x,
        float lineTop,
        float scale)
    {
        if (!string.Equals(shape.Type, "rect", StringComparison.OrdinalIgnoreCase))
            return;

        var left = x + shape.X * scale;
        var top = lineTop + shape.Y * scale;
        var width = Math.Max(0f, shape.Width * scale);
        var height = Math.Max(0f, shape.Height * scale);
        if (width <= 0 || height <= 0)
            return;

        canvas.DrawRect(
            new SKRect(left, top, left + width, top + height),
            paints.Get(ParseColor(shape.Color, new SKColor(0x80, 0x80, 0x80))));
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
