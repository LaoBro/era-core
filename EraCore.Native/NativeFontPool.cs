using System;
using System.Collections.Generic;

namespace EraCore.Native;

/// <summary>
/// 长期字体池（平台无关泛型壳）：按 (字体身份, 变体, 字号) 复用。
/// 平台侧（Android）注入「创建 SKFont」的工厂；测试可注入假的可释放对象。
///
/// <para>容量上界：<c>4 ×（不同字体身份数）×（用过的字号数）</c>。字体身份由 <c>NativeFontManager.Resolve</c>
/// 从静态缓存返回，实际不同身份数 = 内置字体 + 系统回退字体数（通常 ≤ 4）；字号只随设置页变化，
/// 实际并存 1–2 个。不设硬上限、不淘汰：字体只在 <see cref="Dispose"/>（view 卸载）时批量释放，
/// 避免释放正在被渲染线程或预热线程使用的 SKFont。</para>
///
/// <para>线程约束：本类可被渲染线程与后台预热线程并发调用。<see cref="Get"/> 只在字典查/建期间持锁；
/// 返回的 SKFont 是只读对象，<c>MeasureText</c> 在锁外并发执行（Skia 的只读文本度量线程安全）。</para>
/// </summary>
public sealed class NativeFontPool<TFont> : IDisposable where TFont : class, IDisposable
{
    private readonly Func<object, NativeFontVariant, float, TFont> _factory;
    private readonly Dictionary<Key, TFont> _fonts = new();
    private readonly object _gate = new();
    private volatile float _size = float.NaN;
    private bool _disposed;

    public NativeFontPool(Func<object, NativeFontVariant, float, TFont> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _fonts.Count;
        }
    }

    /// <summary>最近一次请求的字号；未创建过字体时为 NaN。</summary>
    public float FontSize => _size;

    /// <summary>取一支字体：同 (typeface, variant, size) 返回同一实例；换字号不会释放旧字号字体。</summary>
    public TFont Get(object typeface, NativeFontVariant variant, float size)
    {
        ArgumentNullException.ThrowIfNull(typeface);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _size = size;

            var key = new Key(typeface, variant, size);
            if (_fonts.TryGetValue(key, out var cached))
                return cached;

            var font = _factory(typeface, variant, size);
            _fonts[key] = font;
            return font;
        }
    }

    /// <summary>释放全部字体。调用前必须确保没有线程仍在 <c>MeasureText</c>（Android 侧先停预热线程）。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            foreach (var font in _fonts.Values)
                font.Dispose();
            _fonts.Clear();
            _size = float.NaN;
            _disposed = true;
        }
    }

    private readonly record struct Key(object Typeface, NativeFontVariant Variant, float Size);
}
