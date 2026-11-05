using System;
using System.Collections.Generic;
using EraCore.Native;
using SkiaSharp;

namespace EraCore.Native.Android;

/// <summary>
/// 按 run 复用的 <see cref="SKTextBlob"/> 缓存（有界 LRU）：键 = 文本内容 + 字体身份 + 变体 + 字号。
///
/// <para><see cref="SKTextBlob"/> 不可变，创建后只被 <c>SKCanvas.DrawText(blob, x, y, paint)</c>
/// 读取，因此可跨帧复用，不涉及滚动平移。键覆盖所有影响 shaping 的输入（文本、字体身份、变体、
/// 字号）；失效口径跟随行布局缓存：frame 引用或 <see cref="NativeRenderStyle"/> 值变化时整体清空。</para>
///
/// <para>容量 <see cref="DefaultCapacity"/> = 1024：行布局缓存 512 行 × 每行约 1–2 个 run（实测
/// 65–84 ops/帧）≈ 850 条，1024 可覆盖 8–12 屏历史里的全部可见 run；单个 blob 约 0.2–0.5 KB，
/// 上限内存约 0.2–0.5 MB。超限按 LRU 淘汰，但**本帧用过（Epoch 相同）的 blob 不淘汰**——帧内
/// 释放正在使用的对象会重演 SKPaint 池的问题；若尾部全是本帧使用的 blob，则允许临时超限，
/// 下一帧再收缩。</para>
///
/// <para>线程约束：与 <c>NativeFontPool</c>/<c>NativePaintPool</c> 相同，调用方保证串行
/// （Android 侧 <c>ICustomDrawOperation.Render</c> 在同一渲染线程串行执行同一 view）；本类不加锁。</para>
/// </summary>
internal sealed class NativeTextBlobCache
{
    internal const int DefaultCapacity = 1024;

    private sealed class Entry
    {
        public Entry(SKTextBlob blob, LinkedListNode<Key> node, long epoch)
        {
            Blob = blob;
            Node = node;
            Epoch = epoch;
        }

        public SKTextBlob Blob { get; }

        public LinkedListNode<Key> Node { get; }

        public long Epoch { get; set; }
    }

    private readonly Dictionary<Key, Entry> _blobs;
    private readonly LinkedList<Key> _lru = new();
    private object? _frame;
    private object? _style;
    private long _epoch;

    public NativeTextBlobCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity 必须为正数");
        Capacity = capacity;
        _blobs = new Dictionary<Key, Entry>(capacity);
    }

    public int Capacity { get; }

    public int Count => _blobs.Count;

    /// <summary>命中次数（<see cref="Prepare"/> 重置时归零；调用方按帧取增量上报）。</summary>
    public int Hits { get; private set; }

    /// <summary>未命中（= 本帧新建 blob）次数（<see cref="Prepare"/> 重置时归零）。</summary>
    public int Misses { get; private set; }

    /// <summary>每帧绘制前调用：frame 引用或 style 值变化时整体清空并进入新 epoch。</summary>
    public void Prepare(object frame, object style)
    {
        if (_frame is not null && ReferenceEquals(_frame, frame) && _style is not null && _style.Equals(style))
        {
            _epoch++;
            return;
        }

        Reset(frame, style);
    }

    /// <summary>取文本 blob；未命中时用调用方给的字体（来自长期字体池）构建。创建失败返回 null，调用方回退到直接绘制。</summary>
    public SKTextBlob? Get(
        string text,
        object typeface,
        NativeFontVariant variant,
        float fontSize,
        SKFont font)
    {
        var key = new Key(text, typeface, variant, fontSize);
        if (_blobs.TryGetValue(key, out var cached))
        {
            _lru.Remove(cached.Node);
            _lru.AddFirst(cached.Node);
            cached.Epoch = _epoch;
            Hits++;
            return cached.Blob;
        }

        Misses++;
        var blob = SKTextBlob.Create(text, font, SKPoint.Empty);
        if (blob is null)
            return null;

        var node = _lru.AddFirst(key);
        _blobs[key] = new Entry(blob, node, _epoch);
        Trim();
        return blob;
    }

    /// <summary>清空全部 blob 与统计（保留容量）。</summary>
    public void Reset(object? frame = null, object? style = null)
    {
        foreach (var entry in _blobs.Values)
            entry.Blob.Dispose();
        _blobs.Clear();
        _lru.Clear();
        _frame = frame;
        _style = style;
        _epoch = 0;
        Hits = 0;
        Misses = 0;
    }

    private void Trim()
    {
        while (_blobs.Count > Capacity)
        {
            var last = _lru.Last;
            if (last is null)
                return;

            var entry = _blobs[last.Value];
            // 本帧正在使用的 blob 一律不释放；尾部都是本帧用过的，说明无法安全收缩，留到下一帧。
            if (entry.Epoch == _epoch)
                return;

            _lru.RemoveLast();
            _blobs.Remove(last.Value);
            entry.Blob.Dispose();
        }
    }

    private readonly record struct Key(string Text, object Typeface, NativeFontVariant Variant, float FontSize);
}
