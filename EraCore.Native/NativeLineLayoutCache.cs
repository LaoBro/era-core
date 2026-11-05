using System;
using System.Collections.Generic;

namespace EraCore.Native;

/// <summary>
/// 按行布局缓存（有界 LRU）：键 = 「frame 引用」+「style 值相等」，行键 = 行下标。
///
/// <para>失效口径与历史 <c>NativeImageLayoutCache</c> 完全一致：frame 引用变化（新快照 → 新
/// <see cref="NativeFrame"/>）或 style 任意字段变化（D 记录 <c>Equals</c>）时整体清空并归零统计。
/// 调用方每帧在绘制前调用一次 <see cref="Prepare"/>。</para>
///
/// <para>容量 <see cref="DefaultCapacity"/> = 512 行：可见窗口 + overscan 约 40–60 行（720×1280 /
/// lineHeight 16），512 行约等于 8–12 屏历史，能覆盖小幅来回滚动；单行布局约 0.3–0.5 KB，
/// 上限内存约 150–250 KB。超限按 LRU 淘汰最久未使用的行（不淘汰正在使用的行，因为每帧都会 Touch）。
/// 不做无界缓存：2750 行全日志缓存没有收益（顺序滚动每行只用一次）。</para>
/// </summary>
public sealed class NativeLineLayoutCache
{
    public const int DefaultCapacity = 512;

    private sealed class Entry
    {
        public Entry(NativeLineLayout layout, LinkedListNode<int> node)
        {
            Layout = layout;
            Node = node;
        }

        public NativeLineLayout Layout { get; set; }

        public LinkedListNode<int> Node { get; }
    }

    private readonly Dictionary<int, Entry> _lines;
    private readonly LinkedList<int> _lru = new();
    private object? _frame;
    private object? _style;

    public NativeLineLayoutCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "capacity 必须为正数");
        Capacity = capacity;
        _lines = new Dictionary<int, Entry>(capacity);
    }

    public int Capacity { get; }

    public int Count => _lines.Count;

    /// <summary>命中次数（仅统计 <see cref="TryGet"/> 命中；<see cref="Prepare"/> 重置时归零）。</summary>
    public int Hits { get; private set; }

    /// <summary>未命中次数（含「查无此行」；<see cref="Prepare"/> 重置时归零）。</summary>
    public int Misses { get; private set; }

    /// <summary>
    /// 每帧绘制前调用：frame 引用或 style 值变化时整体清空。用 <see cref="object"/> 承载是因为
    /// 平台无关层不认识 Android 的 <c>NativeRenderStyle</c> 类型；调用方负责传入「值相等」的 style 对象。
    /// </summary>
    public void Prepare(object frame, object style)
    {
        if (_frame is not null && ReferenceEquals(_frame, frame) && _style is not null && _style.Equals(style))
            return;
        Reset(frame, style);
    }

    /// <summary>查一行；命中时把该行移到 LRU 头部。</summary>
    public NativeLineLayout? TryGet(int lineIndex)
    {
        if (!_lines.TryGetValue(lineIndex, out var entry))
        {
            Misses++;
            return null;
        }

        _lru.Remove(entry.Node);
        _lru.AddFirst(entry.Node);
        Hits++;
        return entry.Layout;
    }

    /// <summary>写入一行（已存在则替换并 Touch）；超容量时淘汰 LRU 尾部。</summary>
    public void Store(int lineIndex, NativeLineLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (_lines.TryGetValue(lineIndex, out var existing))
        {
            existing.Layout = layout;
            _lru.Remove(existing.Node);
            _lru.AddFirst(existing.Node);
            return;
        }

        _lines[lineIndex] = new Entry(layout, _lru.AddFirst(lineIndex));
        while (_lines.Count > Capacity)
        {
            var last = _lru.Last;
            if (last is null)
                break;
            _lru.RemoveLast();
            _lines.Remove(last.Value);
        }
    }

    /// <summary>清空全部缓存与统计（保留容量）。</summary>
    public void Reset(object? frame = null, object? style = null)
    {
        _lines.Clear();
        _lru.Clear();
        _frame = frame;
        _style = style;
        Hits = 0;
        Misses = 0;
    }
}
