using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Android.Util;
using EraCore.Hosting;
using EraCore.Native;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// 可见窗口预热（行布局 + 文本 blob）：UI 线程换帧时投递请求，单条后台线程用<b>同一</b>
/// <see cref="NativeLineLayoutBuilder"/> + 同一 <see cref="NativeFontPool{TFont}"/> 实例构建
/// 「可见窗口 + overscan」的行布局，再据此为这些行里的文本 run 创建 <see cref="SKTextBlob"/>；
/// 渲染线程在 <c>Draw</c> 起点按 (frame 引用, style 值) 校验并入行布局缓存与 blob 缓存。
///
/// <para>为什么不做成「后台线程直接写缓存」：<see cref="NativeLineLayoutCache"/> 与 Android 侧的
/// <c>NativeTextBlobCache</c> 的无锁前提是渲染线程串行访问。若后台线程直接 Prepare/Store，
/// 换帧时的一次 Reset 可能与在途绘制交错，旧帧的 Store 会污染新帧的下标。这里改为
/// 「私有只读批次 + 渲染线程并入」：后台只写自己的批次，渲染线程独占缓存，滚动热路径上
/// 只有两次队列空检查（布局队列 + blob 队列），不引入任何锁。</para>
///
/// <para><b>为什么按块发布而不是整批发布</b>：换帧到首绘只有 0–22ms 的窗口，而整窗 40+ 行全量构建
/// 可能要 20ms 以上。整批发布会让首绘经常看到「批次还没好 → 一行都用不上」。因此每
/// <see cref="ChunkLines"/> 行发布一个布局批次，blob 也按同样粒度独立发布；首绘能并入多少算多少，
/// 剩余行仍由渲染线程现场构建。blob 批次独立于布局批次发布，是为了不让 blob 创建拖慢布局的可用时机。</para>
///
/// <para>预热是尽力而为且必须与现场构建等价：同一 builder、同一 style 值、同一字体池、同一字体身份解析、
/// 同一 <see cref="NativeTextBlobPolicy"/> 回退口径。行布局不含 <c>SKFont</c>，批次只是纯托管数据；
/// blob 批次持有原生对象，过期批次在并入点或 <see cref="Dispose"/> 时显式释放（见
/// <see cref="NativeTextBlobPrewarmBatch{TBlob}"/> 的所有权模型）。</para>
/// </summary>
internal sealed class NativeWindowPrewarmer : IDisposable
{
    private const string LogTag = "EraCoreNative";

    /// <summary>每块行数：兼顾「尽早可用」与「批次开销」。4 行 ≈ 1–2ms 构建时间。</summary>
    private const int ChunkLines = 4;

    /// <summary>与 <see cref="NativeSkiaRenderer"/> 共用的字体身份解析（静态缓存返回同一实例）。</summary>
    private static readonly Func<char, object> ResolveTypeface = static c => NativeFontManager.Resolve(c);

    /// <summary>blob 批次的释放委托：原生对象必须显式 Dispose，不能只交给 GC。</summary>
    private static readonly Action<SKTextBlob> DisposeBlob = static blob => blob.Dispose();

    private readonly NativeFontPool<SKFont> _fonts;
    private readonly object _gate = new();

    /// <summary>已完成批次的无锁队列；后台线程只入队，渲染线程只出队。</summary>
    private readonly ConcurrentQueue<NativeLayoutPrewarmBatch> _layoutBatches = new();
    private readonly ConcurrentQueue<NativeTextBlobPrewarmBatch<SKTextBlob>> _blobBatches = new();

    private readonly Thread _worker;

    /// <summary>最新请求（后到覆盖先到）；只由 <see cref="_gate"/> 保护。</summary>
    private PrewarmRequest? _pending;

    private long _generation;

    /// <summary>blob 生命周期对账：创建 = 采纳 + 释放 + 在途。</summary>
    private long _blobsCreated;
    private long _blobsAdopted;
    private long _blobsReleased;

    private bool _stopped;
    private bool _disposed;

    private sealed record PrewarmRequest(
        NativeFrame Frame,
        NativeRenderStyle Style,
        double ScrollY,
        double ViewportHeight,
        long Generation);

    public NativeWindowPrewarmer(NativeFontPool<SKFont> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        _fonts = fonts;
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "EraCoreWindowPrewarm",
        };
        _worker.Start();
    }

    /// <summary>
    /// UI 线程：帧已构建、滚动位置已确定时投递一次。后到的请求覆盖未开始的旧请求；
    /// 正在构建的旧请求会在下一块检查时放弃。
    /// </summary>
    public void Request(NativeFrame frame, NativeRenderStyle style, double scrollY, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(style);
        if (viewportHeight <= 0 || frame.Lines.Count == 0)
            return;

        lock (_gate)
        {
            if (_stopped)
                return;
            _pending = new PrewarmRequest(frame, style, scrollY, viewportHeight, ++_generation);
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>
    /// 渲染线程：取走两个队列里的所有批次，把与当前 frame/style 匹配的内容并入缓存，返回实际并入行数。
    /// 不持锁；不匹配的批次（已换帧 / 已改 style）直接丢弃，blob 批次同时释放其原生对象。
    /// </summary>
    public int TryAdopt(
        NativeLineLayoutCache layoutCache,
        NativeTextBlobCache blobCache,
        NativeFrame frame,
        object style)
    {
        var adoptedLayouts = 0;
        var discardedLayouts = 0;
        while (_layoutBatches.TryDequeue(out var batch))
        {
            if (!batch.Matches(frame, style))
            {
                discardedLayouts += batch.Count;
                continue;
            }

            adoptedLayouts += batch.AdoptInto(layoutCache, frame.Lines);
        }

        var adoptedBlobs = 0;
        var releasedBlobs = 0;
        while (_blobBatches.TryDequeue(out var batch))
        {
            if (!batch.Matches(frame, style))
            {
                releasedBlobs += batch.Release();
                continue;
            }

            // 缓存里已有同键条目时返回 false，批次随即释放新造的那一个（不泄漏、不替换）。
            adoptedBlobs += batch.AdoptInto(blobCache.Adopt);
            releasedBlobs += batch.ReleasedCount;
        }

        if (adoptedBlobs > 0)
            Interlocked.Add(ref _blobsAdopted, adoptedBlobs);
        if (releasedBlobs > 0)
            Interlocked.Add(ref _blobsReleased, releasedBlobs);

        if (NativeTrace.Enabled && (adoptedLayouts > 0 || discardedLayouts > 0 || adoptedBlobs > 0 || releasedBlobs > 0))
        {
            var created = Interlocked.Read(ref _blobsCreated);
            var adopted = Interlocked.Read(ref _blobsAdopted);
            var released = Interlocked.Read(ref _blobsReleased);
            Log.Info(LogTag,
                $"prewarm: layouts adopted={adoptedLayouts} discarded={discardedLayouts} | " +
                $"blobs adopted={adoptedBlobs} released={releasedBlobs} | " +
                $"lifecycle created={created} adopted={adopted} released={released} " +
                $"inflight={created - adopted - released}");
        }

        return adoptedLayouts;
    }

    private void Loop()
    {
        while (true)
        {
            PrewarmRequest request;
            lock (_gate)
            {
                while (!_stopped && _pending is null)
                    Monitor.Wait(_gate);
                if (_stopped)
                    return;
                request = _pending!;
                _pending = null;
            }

            // 预热是尽力而为：原生调用（SKFont 度量 / SKTextBlob.Create）在极端时序（字体池即将 Dispose）
            // 下可能抛异常。后台线程的未捕获异常会杀掉进程，这里必须兜住；失败只意味着这一轮没有预热。
            try
            {
                Build(request);
            }
            catch (Exception ex)
            {
                if (NativeTrace.Enabled)
                    Log.Warn(LogTag, $"prewarm failed: {ex}");
            }
        }
    }

    /// <summary>
    /// 构建「可见窗口 + overscan」的行布局与文本 blob，按块发布。
    /// 窗口算术与 <see cref="NativeSkiaRenderer.Draw"/> 完全相同（含默认 overscan），range 逐行一致。
    /// </summary>
    private void Build(PrewarmRequest request)
    {
        var frame = request.Frame;
        var style = request.Style;
        var window = VirtualWindow.Compute(
            frame.Lines.Count,
            request.ScrollY,
            request.ViewportHeight,
            new VirtualWindowOptions(style.LineHeight));

        var start = window.StartIndex;
        var end = window.EndIndexExclusive;
        if (end <= start)
            return;

        var fontSize = (float)Math.Max(1, style.FontSize);
        var scale = (float)Math.Max(0.1, style.Scale);
        var measureText = (string text, object typeface, NativeFontVariant variant) =>
            _fonts.Get(typeface, variant, fontSize).MeasureText(text);

        var count = end - start;
        var builtLayouts = new NativeLineLayout[count];
        var startedAt = Stopwatch.GetTimestamp();

        // 发布用的工作数组：PublishLayout 与 BuildLayouts 都要读，放在 Build 作用域。
        var chunkIndices = new int[ChunkLines];
        var chunkLines = new NativeLine[ChunkLines];
        var chunkLayouts = new NativeLineLayout[ChunkLines];

        BuildLayouts();

        if (NativeTrace.Enabled)
        {
            var elapsed = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            Log.Info(LogTag, $"prewarm: layouts={count} build={elapsed:F2}ms");
        }

        BuildBlobs();

        void BuildLayouts()
        {
            var filled = 0;

            for (var offset = 0; offset < count; offset++)
            {
                if (!IsCurrent(request.Generation))
                    return;

                var index = start + offset;
                var layout = NativeLineLayoutBuilder.Build(
                    frame.Lines[index], scale, ResolveTypeface, measureText, out _, out _);

                builtLayouts[offset] = layout;
                chunkIndices[filled] = index;
                chunkLines[filled] = frame.Lines[index];
                chunkLayouts[filled] = layout;
                filled++;

                if (filled == ChunkLines)
                {
                    if (!PublishLayout(filled))
                        return;
                    filled = 0;
                }
            }

            if (filled > 0)
                PublishLayout(filled);
        }

        bool PublishLayout(int length)
        {
            // 发布前最后一次校验：换代请求一到，旧批次的剩余块立刻作废，不再进入队列。
            if (!IsCurrent(request.Generation))
                return false;

            // 队列里的是不可变快照：复用工作数组，必须复制。
            var indices = new int[length];
            var lines = new NativeLine[length];
            var layouts = new NativeLineLayout[length];
            Array.Copy(chunkIndices, indices, length);
            Array.Copy(chunkLines, lines, length);
            Array.Copy(chunkLayouts, layouts, length);
            _layoutBatches.Enqueue(new NativeLayoutPrewarmBatch(
                request.Frame, request.Style, indices, lines, layouts));
            return true;
        }

        void BuildBlobs()
        {
            // 同一窗口内文本重复很常见（重复台词/固定 UI 文案）：按窗口去重，避免为同一 run 造多个 blob。
            var seen = new HashSet<NativeTextBlobKey>();
            var keys = new List<NativeTextBlobKey>(ChunkLines * 2);
            var blobs = new List<SKTextBlob?>(ChunkLines * 2);

            for (var offset = 0; offset < count; offset++)
            {
                if (!IsCurrent(request.Generation))
                {
                    AbandonBlobs(blobs);
                    return;
                }

                foreach (var run in builtLayouts[offset].Runs)
                {
                    // 与渲染侧同一回退口径：代理对 run 永远走 DrawText，不为它造 blob。
                    if (!NativeTextBlobPolicy.CanBlob(run.Text))
                        continue;

                    var key = new NativeTextBlobKey(run.Text, run.Typeface, run.Variant, fontSize);
                    if (!seen.Add(key))
                        continue;

                    // 与渲染侧同一字体身份：同 (typeface, variant, fontSize) 由字体池返回同一实例。
                    var blob = SKTextBlob.Create(
                        run.Text,
                        _fonts.Get(run.Typeface, run.Variant, fontSize),
                        SKPoint.Empty);
                    if (blob is null)
                        continue; // 创建失败：该键不预热，绘制侧会再试一次并回退

                    Interlocked.Increment(ref _blobsCreated);
                    keys.Add(key);
                    blobs.Add(blob);
                }

                if ((offset + 1) % ChunkLines == 0 && keys.Count > 0 && !PublishBlobs(keys, blobs))
                    return;
            }

            if (keys.Count > 0)
                PublishBlobs(keys, blobs);
        }

        bool PublishBlobs(List<NativeTextBlobKey> keys, List<SKTextBlob?> blobs)
        {
            if (!IsCurrent(request.Generation))
            {
                AbandonBlobs(blobs);
                return false;
            }

            var chunkBlobs = new SKTextBlob?[blobs.Count];
            for (var i = 0; i < blobs.Count; i++)
                chunkBlobs[i] = blobs[i];

            var batch = new NativeTextBlobPrewarmBatch<SKTextBlob>(
                request.Frame, request.Style, keys.ToArray(), chunkBlobs, DisposeBlob);
            // 所有权已转移给批次：清空工作列表，不能再由 AbandonBlobs 释放。
            keys.Clear();
            blobs.Clear();
            _blobBatches.Enqueue(batch);
            return true;
        }

        // 尚未发布就作废的 blob 在预热线程就地释放，不进入队列（原生对象不能只等 GC）。
        static void AbandonBlobs(List<SKTextBlob?> blobs)
        {
            foreach (var blob in blobs)
                blob?.Dispose();
            blobs.Clear();
        }
    }

    private bool IsCurrent(long generation)
    {
        lock (_gate)
            return !_stopped && generation == _generation;
    }

    /// <summary>
    /// view 卸载时调用：先停止并等待预热线程退出（最坏等一块行量测），再释放队列里未采纳的 blob，
    /// 最后才允许上层释放字体池——保证没有线程还在用即将 Dispose 的 SKFont。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopped = true;
            _pending = null;
            Monitor.Pulse(_gate);
        }

        if (!_worker.Join(2000))
            Log.Warn(LogTag, "prewarm worker did not stop in 2s");

        while (_layoutBatches.TryDequeue(out _))
        {
        }

        var released = 0;
        while (_blobBatches.TryDequeue(out var batch))
            released += batch.Release();
        if (released > 0)
            Interlocked.Add(ref _blobsReleased, released);

        if (NativeTrace.Enabled)
        {
            var created = Interlocked.Read(ref _blobsCreated);
            var adopted = Interlocked.Read(ref _blobsAdopted);
            var justReleased = Interlocked.Read(ref _blobsReleased);
            Log.Info(LogTag,
                $"prewarm: stop lifecycle created={created} adopted={adopted} released={justReleased} " +
                $"inflight={created - adopted - justReleased}");
        }
    }
}
