using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Android.Util;
using EraCore.Hosting;
using EraCore.Native;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// 可见窗口布局预热：UI 线程换帧时投递请求，单条后台线程用<b>同一</b>
/// <see cref="NativeLineLayoutBuilder"/> + 同一 <see cref="NativeFontPool{TFont}"/> 实例构建
/// 「可见窗口 + overscan」的行布局，渲染线程在 <c>Draw</c> 起点按 (frame 引用, style 值, 行对象引用)
/// 校验并入 <see cref="NativeLineLayoutCache"/>。
///
/// <para>为什么不做成「后台线程直接写缓存」：<see cref="NativeLineLayoutCache"/> 与
/// <see cref="NativeTextBlobCache"/> 的无锁前提是渲染线程串行访问。若后台线程直接 Prepare/Store，
/// 换帧时的一次 Reset 可能与在途绘制交错，旧帧的 Store 会污染新帧的下标。这里改为
/// 「私有只读批次 + 渲染线程并入」：后台只写自己的批次，渲染线程独占缓存，滚动热路径上
/// 只有一次队列空检查，不引入任何锁。</para>
///
/// <para><b>为什么按块发布而不是整批发布</b>：换帧到首绘只有 0–22ms 的窗口，而整窗 40+ 行全量构建
/// 可能要 20ms 以上。整批发布会让首绘经常看到「批次还没好 → 一行都用不上」。因此每
/// <see cref="ChunkLines"/> 行发布一个不可变小批次；首绘能并入多少算多少，剩余行仍由渲染线程现场构建。</para>
///
/// <para>预热是尽力而为且必须与现场构建等价：同一 builder、同一 style 值、同一字体池、同一字体身份解析。
/// 行布局不含 <c>SKFont</c>，批次只是纯托管数据，过期批次直接交给 GC。</para>
/// </summary>
internal sealed class NativeLayoutPrewarmer : IDisposable
{
    private const string LogTag = "EraCoreNative";

    /// <summary>每块行数：兼顾「尽早可用」与「批次开销」。4 行 ≈ 1–2ms 构建时间。</summary>
    private const int ChunkLines = 4;

    /// <summary>与 <see cref="NativeSkiaRenderer"/> 共用的字体身份解析（静态缓存返回同一实例）。</summary>
    private static readonly Func<char, object> ResolveTypeface = static c => NativeFontManager.Resolve(c);

    private readonly NativeFontPool<SKFont> _fonts;
    private readonly object _gate = new();

    /// <summary>已完成批次的无锁队列；后台线程只入队，渲染线程只出队。</summary>
    private readonly ConcurrentQueue<NativeLayoutPrewarmBatch> _batches = new();

    private readonly Thread _worker;

    /// <summary>最新请求（后到覆盖先到）；只由 <see cref="_gate"/> 保护。</summary>
    private PrewarmRequest? _pending;

    private long _generation;
    private bool _stopped;
    private bool _disposed;

    private sealed record PrewarmRequest(
        NativeFrame Frame,
        NativeRenderStyle Style,
        double ScrollY,
        double ViewportHeight,
        long Generation);

    public NativeLayoutPrewarmer(NativeFontPool<SKFont> fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        _fonts = fonts;
        _worker = new Thread(Loop)
        {
            IsBackground = true,
            Name = "EraCoreLayoutPrewarm",
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
    /// 渲染线程：取走队列里所有批次，把与当前 frame/style 匹配的行并入缓存，返回实际并入行数。
    /// 不持锁；不匹配的批次（已换帧 / 已改 style）直接丢弃。
    /// </summary>
    public int TryAdopt(NativeLineLayoutCache cache, NativeFrame frame, object style)
    {
        var adopted = 0;
        var discarded = 0;
        while (_batches.TryDequeue(out var batch))
        {
            if (!batch.Matches(frame, style))
            {
                discarded += batch.Count;
                continue;
            }
            adopted += batch.AdoptInto(cache, frame.Lines);
        }

        if (NativeTrace.Enabled && (adopted > 0 || discarded > 0))
            Log.Info(LogTag, $"prewarm: adopted={adopted} discarded={discarded}");
        return adopted;
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

            Build(request);
        }
    }

    /// <summary>
    /// 构建「可见窗口 + overscan」的行布局并按块发布。
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

        var indices = new int[ChunkLines];
        var lines = new NativeLine[ChunkLines];
        var layouts = new NativeLineLayout[ChunkLines];
        var filled = 0;
        var published = 0;
        var startedAt = Stopwatch.GetTimestamp();

        for (var index = start; index < end; index++)
        {
            if (!IsCurrent(request.Generation))
                return;

            var line = frame.Lines[index];
            indices[filled] = index;
            lines[filled] = line;
            layouts[filled] = NativeLineLayoutBuilder.Build(
                line, scale, ResolveTypeface, measureText, out _, out _);
            filled++;
            published++;

            if (filled == ChunkLines)
            {
                if (!Publish(request, filled))
                    return;
                filled = 0;
            }
        }

        if (filled > 0 && !Publish(request, filled))
            return;

        if (NativeTrace.Enabled)
        {
            var elapsed = (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
            Log.Info(LogTag, $"prewarm: lines={published} build={elapsed:F2}ms");
        }

        bool Publish(PrewarmRequest current, int count)
        {
            // 发布前最后一次校验：换代请求一到，旧批次的剩余块立刻作废，不再进入队列。
            if (!IsCurrent(current.Generation))
                return false;
            // 队列里的是不可变快照：复用工作数组，必须复制。
            var chunkIndices = new int[count];
            var chunkLines = new NativeLine[count];
            var chunkLayouts = new NativeLineLayout[count];
            Array.Copy(indices, chunkIndices, count);
            Array.Copy(lines, chunkLines, count);
            Array.Copy(layouts, chunkLayouts, count);
            _batches.Enqueue(new NativeLayoutPrewarmBatch(
                current.Frame, current.Style, chunkIndices, chunkLines, chunkLayouts));
            return true;
        }
    }

    private bool IsCurrent(long generation)
    {
        lock (_gate)
            return !_stopped && generation == _generation;
    }

    /// <summary>
    /// view 卸载时调用：先停止并等待预热线程退出（最坏等一块行量测），再允许上层释放字体池，
    /// 保证没有线程还在用即将 Dispose 的 SKFont。
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

        while (_batches.TryDequeue(out _))
        {
        }
    }
}
