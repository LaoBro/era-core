#if DEBUG
using System;
using System.Diagnostics;
using Android.Util;

namespace EraCore.Native.Android;

/// <summary>单帧诊断采样槽（仅 Debug 构建存在）。</summary>
internal struct NativeFrameSample
{
    public long DrawStartTicks;
    public long AllocStart;
    public long PrefetchTicks;
    public long PlanTicks;
    public long LayoutTicks;
    public long PaintTicks;
    public int Lines;
    public int VisibleLines;
    public int Extents;
    public int PlanSteps;
    public int ImageDraws;
    public int Runs;
    public int Chars;
    public int LayoutHits;
    public int LayoutMisses;
}

/// <summary>
/// 每帧诊断聚合器（仅 Debug 构建存在）：采集绘制分段耗时与规模计数，每 60 帧输出一条
/// <c>Android.Util.Log.Info</c> 日志。
///
/// <para>只读观测：不改渲染顺序、不改缓存决策、不改布局结果。所有引用点都在
/// <c>#if DEBUG</c> 内，Release 编译期整体消失（连同类型与字段）。</para>
///
/// <para>双闸门：外层 <c>#if DEBUG</c>，内层 <see cref="Enabled"/>（本地排查时可临时改 false，
/// 即便 Debug 也不采样、不打日志）。</para>
/// </summary>
internal sealed class NativeFrameTelemetry
{
    internal const bool Enabled = true;

    private const int ReportEvery = 60;
    private const string LogTag = "EraCoreNative";
    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private NativeFrameSample _sample;
    private bool _recording;

    private long _frameIndex;
    private int _windowFrames;
    private double _scrollY;
    private long _revision;

    private Agg _drawMs;
    private Agg _prefetchMs;
    private Agg _planMs;
    private Agg _layoutMs;
    private Agg _paintMs;
    private Agg _allocKb;
    private Agg _lines;
    private Agg _visibleLines;
    private Agg _extents;
    private Agg _planSteps;
    private Agg _imageDraws;
    private Agg _runs;
    private Agg _chars;
    private Agg _layoutHits;
    private Agg _layoutMisses;

    /// <summary>本帧采样槽；<see cref="NativeSkiaRenderer.Draw"/> 直接写入。</summary>
    public ref NativeFrameSample Sample => ref _sample;

    /// <summary>开始一帧：清空采样槽并进入采集状态（在 <c>PrefetchWindow</c> 之前调用）。</summary>
    public void BeginFrame(double scrollY, long revision)
    {
        if (Enabled)
        {
            _scrollY = scrollY;
            _revision = revision;
            _sample = default;
            _recording = true;
        }
    }

    public void AddPrefetchTicks(long ticks)
    {
        if (Enabled && _recording)
            _sample.PrefetchTicks += ticks;
    }

    /// <summary>结束一帧：累计 avg/max，满 60 帧输出一条日志并清零。</summary>
    public void EndFrame()
    {
        if (!Enabled || !_recording)
            return;
        if (_sample.DrawStartTicks == 0)
            return;

        _recording = false;
        var frame = ++_frameIndex;
        var drawMs = (Stopwatch.GetTimestamp() - _sample.DrawStartTicks) * TicksToMs;
        var allocKb = (GC.GetAllocatedBytesForCurrentThread() - _sample.AllocStart) / 1024.0;

        _drawMs.Add(drawMs);
        _prefetchMs.Add(_sample.PrefetchTicks * TicksToMs);
        _planMs.Add(_sample.PlanTicks * TicksToMs);
        _layoutMs.Add(_sample.LayoutTicks * TicksToMs);
        _paintMs.Add(_sample.PaintTicks * TicksToMs);
        _allocKb.Add(allocKb);
        _lines.Add(_sample.Lines);
        _visibleLines.Add(_sample.VisibleLines);
        _extents.Add(_sample.Extents);
        _planSteps.Add(_sample.PlanSteps);
        _imageDraws.Add(_sample.ImageDraws);
        _runs.Add(_sample.Runs);
        _chars.Add(_sample.Chars);
        _layoutHits.Add(_sample.LayoutHits);
        _layoutMisses.Add(_sample.LayoutMisses);
        _windowFrames++;

        if (frame % ReportEvery != 0)
            return;

        Log.Info(LogTag, Report(frame));
        Reset();
    }

    private string Report(long frame)
    {
        var n = Math.Max(1, _windowFrames);
        return
            $"perf frame#{frame} scrollY={_scrollY:F1} rev={_revision} n={n} " +
            $"draw={Avg(_drawMs, n):F2}/{_drawMs.Max:F2}ms " +
            $"prefetch={Avg(_prefetchMs, n):F2}/{_prefetchMs.Max:F2}ms " +
            $"plan={Avg(_planMs, n):F2}/{_planMs.Max:F2}ms " +
            $"layout={Avg(_layoutMs, n):F2}/{_layoutMs.Max:F2}ms " +
            $"paint={Avg(_paintMs, n):F2}/{_paintMs.Max:F2}ms " +
            $"alloc={Avg(_allocKb, n):F1}/{_allocKb.Max:F1}KB " +
            $"lines={Avg(_lines, n):F0} visible={Avg(_visibleLines, n):F1} " +
            $"extents={Avg(_extents, n):F1} planSteps={Avg(_planSteps, n):F1} " +
            $"images={Avg(_imageDraws, n):F1} runs={Avg(_runs, n):F0} chars={Avg(_chars, n):F0} " +
            $"layoutCache={Avg(_layoutHits, n):F1}h/{Avg(_layoutMisses, n):F1}m";
    }

    private static double Avg(Agg agg, int count) => agg.Sum / count;

    private void Reset()
    {
        _windowFrames = 0;
        _drawMs = default;
        _prefetchMs = default;
        _planMs = default;
        _layoutMs = default;
        _paintMs = default;
        _allocKb = default;
        _lines = default;
        _visibleLines = default;
        _extents = default;
        _planSteps = default;
        _imageDraws = default;
        _runs = default;
        _chars = default;
        _layoutHits = default;
        _layoutMisses = default;
    }

    private struct Agg
    {
        public double Sum;
        public double Max;

        public void Add(double value)
        {
            Sum += value;
            if (value > Max)
                Max = value;
        }
    }
}
#endif
