using System;
using System.Collections.Generic;

namespace EraCore.Native;

/// <summary>
/// 长期字体池（平台无关泛型壳）：按 (字体身份, 变体) 复用，<c>FontSize</c> 变化时整体 Dispose。
/// 平台侧（Android）注入「创建 SKFont」的工厂；测试可注入假的可释放对象。
///
/// <para>容量上界：<c>4 ×（不同字体身份数量）</c>。字体身份由 <c>NativeFontManager.Resolve</c> 从静态
/// 缓存返回，实际不同身份数 = 内置字体 + 系统回退字体数（通常 ≤ 4），故池内 SKFont 通常 ≤ 16 个。
/// 不设硬上限、不淘汰：字体只在 FontSize 变化时批量释放，避免释放正在被当前帧使用的 SKFont。</para>
///
/// <para>线程约束：调用方必须保证串行调用（Android 侧 <c>ICustomDrawOperation.Render</c> 在同一渲染线程
/// 串行执行同一 view）；本类不加锁。</para>
/// </summary>
public sealed class NativeFontPool<TFont> : IDisposable where TFont : class, IDisposable
{
    private readonly Func<object, NativeFontVariant, float, TFont> _factory;
    private readonly Dictionary<Key, TFont> _fonts = new();
    private float _size = float.NaN;
    private bool _disposed;

    public NativeFontPool(Func<object, NativeFontVariant, float, TFont> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public int Count => _fonts.Count;

    /// <summary>当前池对应的字号；未创建过字体时为 NaN。</summary>
    public float FontSize => _size;

    /// <summary>取一支字体：同 (typeface, variant, size) 返回同一实例；字号变化先整体释放旧池。</summary>
    public TFont Get(object typeface, NativeFontVariant variant, float size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(typeface);

        if (float.IsNaN(_size) || size != _size)
        {
            DisposeFonts();
            _size = size;
        }

        var key = new Key(typeface, variant);
        if (_fonts.TryGetValue(key, out var cached))
            return cached;

        var font = _factory(typeface, variant, size);
        _fonts[key] = font;
        return font;
    }

    /// <summary>释放全部字体（下次 <see cref="Get"/> 会按当前字号重建）。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        DisposeFonts();
        _size = float.NaN;
        _disposed = true;
    }

    private void DisposeFonts()
    {
        foreach (var font in _fonts.Values)
            font.Dispose();
        _fonts.Clear();
    }

    private readonly record struct Key(object Typeface, NativeFontVariant Variant);
}
