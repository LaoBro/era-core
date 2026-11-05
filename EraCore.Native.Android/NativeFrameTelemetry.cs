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

    /// <summary>绘制分段总耗时（背景 + 行槽 + 锚定行图片 + 滚动条）之和。</summary>
    public long PaintTicks;

    /// <summary>文本 run 绘制（含彩色 run 与下划线/删除线）。</summary>
    public long PaintTextTicks;

    /// <summary>背景层（每层 <c>DrawImage</c> 与 paint 构造）。</summary>
    public long PaintBackgroundTicks;

    /// <summary>图片 op（内联图片与窗口外锚定行跨界图片）。</summary>
    public long PaintImageTicks;

    /// <summary>形状 op（矩形）。</summary>
    public long PaintShapeTicks;

    /// <summary>滚动条轨道与滑块。</summary>
    public long PaintChromeTicks;

    public int Lines;
    public int VisibleLines;
    public int Extents;
    public int PlanSteps;
    public int ImageDraws;
    public int Runs;
    public int Chars;
    public int LayoutHits;
    public int LayoutMisses;

    /// <summary>实际绘制的背景层数（已跳过透明/未解码层）。</summary>
    public int BackgroundLayers;

    /// <summary>实际绘制的文本 op 数。</summary>
    public int TextOps;
}

/// <summary>
/// 每帧诊断聚合器（仅 Debug 构建存在）：采集绘制分段耗时与规模计数，每 60 帧输出日志。
///
/// <para>只读观测：不改渲染顺序、不改缓存决策、不改布局结果。所有引用点都在
/// <c>#if DEBUG</c> 内，Release 编译期整体消失（连同类型与字段）。</para>
///
/// <para>双闸门：外层 <c>#if DEBUG</c>，内层 <see cref="Enabled"/>（本地排查时可临时改 false，
/// 即便 Debug 也不采样、不打日志）。</para>
///
/// <para>输出按「本帧 revision 是否相对上一帧变化」分两桶：<c>scroll</c>（换帧后的第 1 帧算
/// flip，之后同 revision 的重绘算滚动帧）与 <c>flip</c>（内容切换帧）。两桶构成不同，
/// 混在一起求平均会失真，故分别统计。</para>
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

    /// <summary>跨窗口保留上一帧 revision，用于判定本帧是否换帧。</summary>
    private long _previousRevision;
    private bool _hasPreviousRevision;

    private bool _flip;

    private Bucket _all = new();
    private Bucket _scroll = new();
    private Bucket _flips = new();

    /// <summary>本帧采样槽；<see cref="NativeSkiaRenderer.Draw"/> 直接写入。</summary>
    public ref NativeFrameSample Sample => ref _sample;

    /// <summary>开始一帧：清空采样槽并判定分桶（在 <c>PrefetchWindow</c> 之前调用）。</summary>
    public void BeginFrame(double scrollY, long revision)
    {
        if (Enabled)
        {
            _scrollY = scrollY;
            _revision = revision;
            _flip = _hasPreviousRevision && revision != _previousRevision;
            _previousRevision = revision;
            _hasPreviousRevision = true;
            _sample = default;
            _recording = true;
        }
    }

    public void AddPrefetchTicks(long ticks)
    {
        if (Enabled && _recording)
            _sample.PrefetchTicks += ticks;
    }

    /// <summary>结束一帧：累计 avg/max，满 60 帧输出日志并清零。</summary>
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

        _all.Add(_sample, drawMs, allocKb);
        (_flip ? _flips : _scroll).Add(_sample, drawMs, allocKb);
        _windowFrames++;

        if (frame % ReportEvery != 0)
            return;

        Log.Info(LogTag, Report(frame));
        Reset();
    }

    private string Report(long frame)
    {
        var n = Math.Max(1, _windowFrames);

        var summary =
            $"perf frame#{frame} scrollY={_scrollY:F1} rev={_revision} n={n} " +
            $"scrollFrames={_scroll.Frames} flipFrames={_flips.Frames} " +
            $"draw={Avg(_all.DrawMs, n):F2}/{_all.DrawMs.Max:F2}ms " +
            $"prefetch={Avg(_all.PrefetchMs, n):F2}/{_all.PrefetchMs.Max:F2}ms " +
            $"plan={Avg(_all.PlanMs, n):F2}/{_all.PlanMs.Max:F2}ms " +
            $"layout={Avg(_all.LayoutMs, n):F2}/{_all.LayoutMs.Max:F2}ms " +
            $"paint={Avg(_all.PaintMs, n):F2}/{_all.PaintMs.Max:F2}ms " +
            $"alloc={Avg(_all.AllocKb, n):F1}/{_all.AllocKb.Max:F1}KB " +
            $"lines={Avg(_all.Lines, n):F0} visible={Avg(_all.VisibleLines, n):F1} " +
            $"extents={Avg(_all.Extents, n):F1} planSteps={Avg(_all.PlanSteps, n):F1} " +
            $"images={Avg(_all.ImageDraws, n):F1} runs={Avg(_all.Runs, n):F0} chars={Avg(_all.Chars, n):F0} " +
            $"layoutCache={Avg(_all.LayoutHits, n):F1}h/{Avg(_all.LayoutMisses, n):F1}m";

        var scrollDetail = Detail($"{frame}.scroll", _scroll);
        var flipDetail = Detail($"{frame}.flip", _flips);
        return string.Join(
            "\n",
            summary,
            scrollDetail ?? string.Empty,
            flipDetail ?? string.Empty).TrimEnd('\n');
    }

    private static string? Detail(string tag, Bucket b)
    {
        if (b.Frames == 0)
            return null;

        var n = b.Frames;
        return
            $"perf frame#{tag} n={n} " +
            $"draw={Avg(b.DrawMs, n):F2}/{b.DrawMs.Max:F2} " +
            $"paint={Avg(b.PaintMs, n):F2}/{b.PaintMs.Max:F2} " +
            $"pText={Avg(b.PaintTextMs, n):F2} pBg={Avg(b.PaintBgMs, n):F2} " +
            $"pImg={Avg(b.PaintImageMs, n):F2} pShape={Avg(b.PaintShapeMs, n):F2} " +
            $"pChrome={Avg(b.PaintChromeMs, n):F2} [ms] " +
            $"alloc={Avg(b.AllocKb, n):F1}/{b.AllocKb.Max:F1}KB " +
            $"bgLayers={Avg(b.BgLayers, n):F1} textOps={Avg(b.TextOps, n):F1} " +
            $"imageOps={Avg(b.ImageDraws, n):F1} runs={Avg(b.Runs, n):F0} chars={Avg(b.Chars, n):F0} " +
            $"layout={Avg(b.LayoutMs, n):F2} lines={Avg(b.VisibleLines, n):F1}";
    }

    private static double Avg(Agg agg, int count) => agg.Sum / count;

    private void Reset()
    {
        _windowFrames = 0;
        _all = new Bucket();
        _scroll = new Bucket();
        _flips = new Bucket();
    }

    private sealed class Bucket
    {
        public int Frames;
        public Agg DrawMs = new();
        public Agg PrefetchMs = new();
        public Agg PlanMs = new();
        public Agg LayoutMs = new();
        public Agg PaintMs = new();
        public Agg PaintTextMs = new();
        public Agg PaintBgMs = new();
        public Agg PaintImageMs = new();
        public Agg PaintShapeMs = new();
        public Agg PaintChromeMs = new();
        public Agg AllocKb = new();
        public Agg Lines = new();
        public Agg VisibleLines = new();
        public Agg Extents = new();
        public Agg PlanSteps = new();
        public Agg ImageDraws = new();
        public Agg Runs = new();
        public Agg Chars = new();
        public Agg LayoutHits = new();
        public Agg LayoutMisses = new();
        public Agg BgLayers = new();
        public Agg TextOps = new();

        public void Add(NativeFrameSample s, double drawMs, double allocKb)
        {
            Frames++;
            DrawMs.Add(drawMs);
            PrefetchMs.Add(s.PrefetchTicks * TicksToMs);
            PlanMs.Add(s.PlanTicks * TicksToMs);
            LayoutMs.Add(s.LayoutTicks * TicksToMs);
            PaintMs.Add(s.PaintTicks * TicksToMs);
            PaintTextMs.Add(s.PaintTextTicks * TicksToMs);
            PaintBgMs.Add(s.PaintBackgroundTicks * TicksToMs);
            PaintImageMs.Add(s.PaintImageTicks * TicksToMs);
            PaintShapeMs.Add(s.PaintShapeTicks * TicksToMs);
            PaintChromeMs.Add(s.PaintChromeTicks * TicksToMs);
            AllocKb.Add(allocKb);
            Lines.Add(s.Lines);
            VisibleLines.Add(s.VisibleLines);
            Extents.Add(s.Extents);
            PlanSteps.Add(s.PlanSteps);
            ImageDraws.Add(s.ImageDraws);
            Runs.Add(s.Runs);
            Chars.Add(s.Chars);
            LayoutHits.Add(s.LayoutHits);
            LayoutMisses.Add(s.LayoutMisses);
            BgLayers.Add(s.BackgroundLayers);
            TextOps.Add(s.TextOps);
        }
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
