using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;
using EraCore.Hosting;
using EraCore.Native;
using MinorShift.Emuera.Files;
using SkiaSharp;

namespace EraCore.Native.Android;

internal enum NativeImageState
{
    Ready,
    Loading,
    Missing,
    Failed,
}

internal readonly record struct NativeImageResult(
    SKBitmap? Bitmap,
    SKImage? Image,
    NativeImageState State,
    string? Detail);

/// <summary>
/// 图片解码缓存：按 <c>src</c>（相对路径或 sprite 名）缓存 <see cref="SKBitmap"/> + <see cref="SKImage"/>。
///
/// <para>字节读取走 <see cref="GameTree.TryReadImage"/>（与 <c>/assets</c> 同一套消毒/sprite 名回退），
/// 解码在后台线程完成（并发 2），绘制线程只取已就绪的结果，滚动时不会重复解码。
/// 失败/缺资源会写日志，绘制侧画占位框，不静默丢图。</para>
/// </summary>
internal sealed class NativeImageCache : IDisposable
{
    private const int MaxEntries = 64;
    private const long MaxBytes = 64L * 1024 * 1024;
    private const string LogTag = "EraCoreNative";

    private sealed class Entry
    {
        public SKBitmap? Bitmap;
        public SKImage? Image;
        public bool Failed;
        public bool Loading;
        public string? Detail;
        public long LastUse;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new();
    /// <summary>已从缓存移除、但可能仍被上一帧 Skia 绘制引用的位图；由下一次绘制开始时统一释放。</summary>
    private readonly List<(SKImage? Image, SKBitmap? Bitmap)> _retired = new();
    private readonly SemaphoreSlim _decodeGate = new(2);
    private readonly Action _onChanged;

    private GameTree? _tree;
    private int _generation;
    private long _clock;
    private bool _disposed;

    public NativeImageCache(Action onChanged)
    {
        _onChanged = onChanged;
    }

    /// <summary>切换游戏（或会话结束传 null）：清空上一局的所有位图。</summary>
    public void UpdateSource(GameTree? tree)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_tree, tree))
                return;

            _tree = tree;
            _generation++;
            ClearLocked();
        }
    }

    public NativeImageResult Get(string? src)
    {
        if (string.IsNullOrWhiteSpace(src))
            return new NativeImageResult(null, null, NativeImageState.Missing, "empty src");

        var key = src.Trim();

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry is null)
            {
                // 还没排进队列：交给下一次 Prefetch；本次绘制先留空。
                return new NativeImageResult(null, null, NativeImageState.Loading, "not queued");
            }

            entry.LastUse = ++_clock;
            if (entry.Image is not null)
            {
                // 正在返回给绘制线程的条目不能是淘汰候选，否则会释放即将绘制的图。
                TrimLocked(key);
                return new NativeImageResult(entry.Bitmap, entry.Image, NativeImageState.Ready, null);
            }
            if (entry.Failed)
                return new NativeImageResult(null, null, NativeImageState.Failed, entry.Detail);
            return new NativeImageResult(null, null, NativeImageState.Loading, "decoding");
        }
    }

    /// <summary>
    /// 在锁内读取一个像素：绘制线程的淘汰/退役与 <see cref="SKBitmap.GetPixel"/> 互斥，
    /// 避免 UI 线程取色时位图刚好被下一帧淘汰并释放。
    /// </summary>
    public bool TryReadPixel(string src, int x, int y, out SKColor color, out string? detail)
    {
        color = default;
        lock (_gate)
        {
            if (!_entries.TryGetValue(src.Trim(), out var entry) || entry is null)
            {
                detail = "not queued";
                return false;
            }
            if (entry.Bitmap is not { } bitmap)
            {
                detail = entry.Failed ? entry.Detail ?? "decode failed" : "decoding";
                return false;
            }
            if (x < 0 || y < 0 || x >= bitmap.Width || y >= bitmap.Height)
            {
                detail = $"out of bounds ({x},{y}) vs {bitmap.Width}x{bitmap.Height}";
                return false;
            }

            color = bitmap.GetPixel(x, y);
            detail = null;
            return true;
        }
    }

    /// <summary>
    /// 只预取当前可见行窗口（含 overscan）里的图片，避免为整份快照排队——
    /// 否则大图库会一次解码上千张、且被淘汰后反复重排。滚动时下一帧即补排新进入视口的图。
    /// </summary>
    public void PrefetchWindow(NativeFrame frame, double scrollY, double viewportHeight, double lineHeight)
    {
        DrainRetired();

        var window = VirtualWindow.Compute(
            frame.Lines.Count,
            scrollY,
            viewportHeight,
            new VirtualWindowOptions(lineHeight, Overscan: 2));
        var start = Math.Max(0, window.StartIndex);
        var end = Math.Min(frame.Lines.Count, window.EndIndexExclusive);
        for (var i = start; i < end; i++)
        {
            foreach (var entry in frame.Lines[i].Entries)
            {
                foreach (var segment in entry.Segments)
                {
                    if (segment.Image is { Src: { Length: > 0 } src })
                    {
                        Queue(src);
                        // srcm 映射图与显示图一起预取：点击取色时两者都必须已解码。
                        if (segment.Image.Srcm is { Length: > 0 } srcm)
                            Queue(srcm);
                    }
                }
            }
        }
        foreach (var background in frame.BackgroundLayers)
            Queue(background.Src);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            ClearLocked();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _generation++;
            ClearLocked();
        }
        DrainRetired();
    }

    private void Queue(string src)
    {
        var key = src.Trim();
        if (key.Length == 0)
            return;

        GameTree? tree;
        int generation;
        lock (_gate)
        {
            if (_disposed || _tree is null || _entries.ContainsKey(key))
                return;

            tree = _tree;
            var entry = new Entry { Loading = true, LastUse = ++_clock };
            _entries[key] = entry;
            generation = _generation;
        }

        _ = Task.Run(() => DecodeAsync(tree, key, generation));
    }

    private async Task DecodeAsync(GameTree tree, string key, int generation)
    {
        await _decodeGate.WaitAsync().ConfigureAwait(false);
        SKBitmap? bitmap = null;
        string? detail = null;
        try
        {
            if (!tree.TryReadImage(key, out var bytes, out _) || bytes is null || bytes.Length == 0)
            {
                detail = "image not found: " + key;
            }
            else
            {
                bitmap = SKBitmap.Decode(bytes);
                if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
                    detail = "decode returned empty: " + key;
            }
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message + " (" + key + ")";
            bitmap?.Dispose();
            bitmap = null;
        }
        finally
        {
            _decodeGate.Release();
        }

        var changed = false;
        lock (_gate)
        {
            if (_disposed || generation != _generation || !_entries.TryGetValue(key, out var entry))
            {
                bitmap?.Dispose();
                return;
            }

            entry.Loading = false;
            entry.LastUse = ++_clock;
            if (bitmap is not null)
            {
                entry.Bitmap = bitmap;
                entry.Image = SKImage.FromBitmap(bitmap);
            }
            else
            {
                entry.Failed = true;
                entry.Detail = detail ?? "decode failed: " + key;
                Log.Warn(LogTag, "image " + entry.Detail);
            }
            changed = true;
        }

        if (changed)
            _onChanged();
    }

    private void ClearLocked()
    {
        foreach (var entry in _entries.Values)
            RetireLocked(entry);
        _entries.Clear();
        _clock = 0;
    }

    private void RetireLocked(Entry entry)
    {
        if (entry.Image is null && entry.Bitmap is null)
            return;
        _retired.Add((entry.Image, entry.Bitmap));
        entry.Image = null;
        entry.Bitmap = null;
    }

    /// <summary>
    /// 在绘制线程每次开始新一帧时调用：上一帧的 Skia 绘制已结束，此时释放退役位图才安全。
    /// （不能在 <see cref="Get"/> 中途释放——同一次绘制内的 DrawImage 可能还没 flush。）
    /// </summary>
    private void DrainRetired()
    {
        List<(SKImage? Image, SKBitmap? Bitmap)>? batch = null;
        lock (_gate)
        {
            if (_retired.Count > 0)
            {
                batch = new List<(SKImage?, SKBitmap?)>(_retired);
                _retired.Clear();
            }
        }

        if (batch is null)
            return;
        foreach (var (image, bitmap) in batch)
        {
            image?.Dispose();
            bitmap?.Dispose();
        }
    }

    private long BytesLocked()
    {
        var total = 0L;
        foreach (var entry in _entries.Values)
            total += entry.Bitmap is null ? 0 : (long)entry.Bitmap.Width * entry.Bitmap.Height * 4;
        return total;
    }

    /// <summary>只在绘制线程（<see cref="Get"/>）调用，避免解码线程释放正在绘制的位图。</summary>
    private void TrimLocked(string inUseKey)
    {
        while (_entries.Count > MaxEntries || BytesLocked() > MaxBytes)
        {
            var oldest = LeastRecentlyUsedLocked(inUseKey);
            if (oldest is null)
                return;
            RemoveLocked(oldest.Value.Key);
        }
    }

    private KeyValuePair<string, Entry>? LeastRecentlyUsedLocked(string exceptKey)
    {
        KeyValuePair<string, Entry>? oldest = null;
        foreach (var pair in _entries)
        {
            if (pair.Key == exceptKey || pair.Value.Loading)
                continue;
            if (oldest is null || pair.Value.LastUse < oldest.Value.Value.LastUse)
                oldest = pair;
        }
        return oldest;
    }

    private void RemoveLocked(string key)
    {
        if (!_entries.Remove(key, out var removed))
            return;
        RetireLocked(removed);
    }
}
