using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Util;
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
    /// <summary>已缓存的位图字节数（增量维护）。不在每次 <see cref="Get"/> 里遍历条目重算。</summary>
    private long _bytes;

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
    /// 按 extent 表预取「图片矩形与视口（±2 行余量）相交」的图，而不是只扫可见行窗口——
    /// 锚定行在窗口外、但图向上/向下伸进视口的跨界图也能及时解码。背景仍全量入队。
    /// </summary>
    public void PrefetchWindow(
        NativeFrame frame,
        double scrollY,
        double viewportHeight,
        double lineHeight,
        double scale)
    {
        DrainRetired();

        if (lineHeight > 0 && scale > 0)
        {
            var margin = lineHeight * 2;
            foreach (var extent in frame.ImageExtentList)
            {
                if (!NativeImageViewport.Intersects(
                        extent,
                        frame.Lines.Count,
                        lineHeight,
                        scrollY,
                        viewportHeight,
                        scale,
                        margin))
                    continue;

                var image = extent.Image;
                if (image.Src.Length > 0)
                    Queue(image.Src);
                // srcm 映射图与显示图一起预取：点击取色时两者都必须已解码。
                if (image.Srcm is { Length: > 0 } srcm)
                    Queue(srcm);
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
                _bytes += BitmapBytes(bitmap);
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
        _bytes = 0;
        _clock = 0;
    }

    private void RetireLocked(Entry entry)
    {
        if (entry.Image is null && entry.Bitmap is null)
            return;
        if (entry.Bitmap is not null)
            _bytes -= BitmapBytes(entry.Bitmap);
        _retired.Add((entry.Image, entry.Bitmap));
        entry.Image = null;
        entry.Bitmap = null;
    }

    private static long BitmapBytes(SKBitmap bitmap) => (long)bitmap.Width * bitmap.Height * 4;

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

    private long BytesLocked() => _bytes;

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
