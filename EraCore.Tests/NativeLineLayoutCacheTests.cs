using System;
using System.Collections.Generic;
using EraCore.Native;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 按行布局缓存（平台无关）+ 长期字体池的契约：
/// - 构建器的切分/累加口径必须与历史 <c>LayoutLine</c> 一致（宽度、前缀宽、entry 偏移）；
/// - 缓存命中/失效/淘汰可预测；
/// - 字体池按 (身份, 变体, 字号) 复用，字号变化整体释放。
/// </summary>
public sealed class NativeLineLayoutCacheTests
{
    private static readonly object Latin = new();
    private static readonly object Cjk = new();

    private sealed record FakeStyle(float FontSize, float Scale);

    private sealed class FakeFont : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static object ResolveTypeface(char c) => c >= 0x3000 ? Cjk : Latin;

    /// <summary>确定性量测：拉丁 10px/字，CJK 20px/字；非 Regular 变体 +1，便于捕捉变体丢失。</summary>
    private static float Measure(string text, object typeface, NativeFontVariant variant)
        => text.Length * (ReferenceEquals(typeface, Cjk) ? 20f : 10f)
           + (variant == NativeFontVariant.Regular ? 0f : 1f);

    private static NativeLine Line(params NativeSegment[] segments)
        => new(new List<NativeEntry> { new(new List<NativeSegment>(segments), null) }, null);

    private static NativeSegment Text(string text, bool bold = false, bool italic = false,
        string? color = null, bool underline = false, bool strikeout = false)
        => new(text, color, bold, italic, null, underline, strikeout);

    private static NativeLineLayout Build(NativeLine line, float scale = 1f)
        => NativeLineLayoutBuilder.Build(line, scale, ResolveTypeface, Measure, out _, out _);

    [Fact]
    public void Build_SplitsRunsByTypeface_AndAccumulatesWidths()
    {
        var layout = Build(Line(Text("aあb")));

        Assert.Equal(3, layout.Runs.Count);
        Assert.Equal("a", layout.Runs[0].Text);
        Assert.Equal("あ", layout.Runs[1].Text);
        Assert.Equal("b", layout.Runs[2].Text);
        Assert.Equal(10f, layout.Runs[0].Width);
        Assert.Equal(20f, layout.Runs[1].Width);
        Assert.Equal(10f, layout.Runs[2].Width);
        Assert.Equal(40f, layout.TotalWidth);
        Assert.Equal(3, layout.Ops.Count);
        Assert.Equal(0, layout.Ops[0].RunIndex);
        Assert.Equal(1, layout.Ops[1].RunIndex);
        Assert.Equal(2, layout.Ops[2].RunIndex);
        Assert.Same(Latin, layout.Runs[0].Typeface);
        Assert.Same(Cjk, layout.Runs[1].Typeface);
    }

    [Fact]
    public void Build_KeepsPerRunStyleFlags_AndVariantWidth()
    {
        var layout = Build(Line(Text("ab", bold: true, italic: true,
            color: "#FF0000", underline: true, strikeout: true)));

        var run = Assert.Single(layout.Runs);
        Assert.Equal(NativeFontVariant.BoldItalic, run.Variant);
        Assert.Equal("#FF0000", run.Color);
        Assert.True(run.Underline);
        Assert.True(run.Strikeout);
        Assert.Equal(21f, run.Width); // 2 × 10 + 1（变体加成）
        Assert.Equal(21f, layout.TotalWidth);
    }

    [Fact]
    public void Build_TextWithImageAndShape_AccumulatesInOrder_AndImagePrefix()
    {
        var image = new NativeImage("FACE", null, null, 36, 18, 0, null);
        var shape = new NativeShape("rect", 0, 0, 84, 20, "#FFFFFF");
        var line = new NativeLine(
            new List<NativeEntry>
            {
                new(new List<NativeSegment> { Text("ab"), new(string.Empty, null, false, false, null, false, false, false, false, image, null) }, null),
                new(new List<NativeSegment> { new(string.Empty, null, false, false, null, false, false, false, true, null, shape) }, null),
            },
            "center");

        var layout = Build(line);

        // op 顺序：文本 run(20) → 图片(36) → 形状(84)
        Assert.Equal(3, layout.Ops.Count);
        Assert.Equal(20f, layout.Ops[0].Width);
        Assert.NotNull(layout.Ops[1].Image);
        Assert.Equal(36f, layout.Ops[1].Width);
        Assert.NotNull(layout.Ops[2].Shape);
        Assert.Equal(84f, layout.Ops[2].Width);
        Assert.Equal(140f, layout.TotalWidth);

        // 图片前缀宽 = 该 op 之前累计宽度（20）
        var prefix = Assert.Single(layout.Images);
        Assert.Equal(0, prefix.EntryIndex);
        Assert.Equal(1, prefix.SegmentIndex);
        Assert.Equal(20f, prefix.PrefixWidth);
        Assert.Equal(20f, layout.PrefixOf(0, 1));
        Assert.Null(layout.PrefixOf(0, 0));

        // entry 偏移与命中区宽度
        Assert.Equal(2, layout.Entries.Count);
        Assert.Equal(0f, layout.Entries[0].StartX);
        Assert.Equal(56f, layout.Entries[0].Width);
        Assert.Equal(56f, layout.Entries[1].StartX);
        Assert.Equal(84f, layout.Entries[1].Width);
    }

    [Fact]
    public void Build_VisualSegment_IgnoresDebugText()
    {
        // 有 image 时同片段 text 不参与布局（否则占位框会被推到文字右侧）。
        var image = new NativeImage("FACE", null, null, 36, 18, 0, null);
        var line = Line(new NativeSegment(
            string.Empty, null, false, false, null, false, false, false, false, image, null));

        var layout = Build(line);

        Assert.Single(layout.Ops);
        Assert.Equal(36f, layout.TotalWidth);
        Assert.Empty(layout.Runs);
    }

    [Fact]
    public void Cache_MissThenStoreThenHit()
    {
        var cache = new NativeLineLayoutCache(4);
        cache.Prepare(Frame(), new FakeStyle(16f, 1f));

        Assert.Null(cache.TryGet(7));
        Assert.Equal(1, cache.Misses);

        var layout = Build(Line(Text("ab")));
        cache.Store(7, layout);

        Assert.Same(layout, cache.TryGet(7));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Cache_ResetsOnFrameReferenceChange()
    {
        var cache = new NativeLineLayoutCache(4);
        var frameA = Frame();
        var frameB = Frame();
        var style = new FakeStyle(16f, 1f);

        cache.Prepare(frameA, style);
        cache.Store(3, Build(Line(Text("a"))));

        cache.Prepare(frameA, style);
        Assert.Equal(1, cache.Count); // 同 frame + 同值 style → 不清空

        cache.Prepare(frameB, style);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    [Fact]
    public void Cache_ResetsOnAnyStyleFieldChange()
    {
        var cache = new NativeLineLayoutCache(4);
        var frame = Frame();

        cache.Prepare(frame, new FakeStyle(16f, 1f));
        cache.Store(3, Build(Line(Text("a"))));

        // 等值的新对象 → 不失效（按值相等）
        cache.Prepare(frame, new FakeStyle(16f, 1f));
        Assert.Equal(1, cache.Count);

        // 任一字段变化 → 整体清空
        cache.Prepare(frame, new FakeStyle(16f, 1.25f));
        Assert.Equal(0, cache.Count);

        cache.Store(3, Build(Line(Text("a"))));
        cache.Prepare(frame, new FakeStyle(18f, 1.25f));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Cache_EvictsLeastRecentlyUsed()
    {
        var cache = new NativeLineLayoutCache(2);
        cache.Prepare(Frame(), new FakeStyle(16f, 1f));
        var layout = Build(Line(Text("a")));

        cache.Store(0, layout);
        cache.Store(1, layout);
        Assert.Equal(2, cache.Count);

        Assert.NotNull(cache.TryGet(0)); // 0 变成最近使用
        cache.Store(2, layout);          // 超容量 → 淘汰最久未用的 1

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.TryGet(1));
        Assert.NotNull(cache.TryGet(0));
        Assert.NotNull(cache.TryGet(2));
    }

    [Fact]
    public void Cache_StoreReplacesExisting_WithoutGrowing()
    {
        var cache = new NativeLineLayoutCache(2);
        cache.Prepare(Frame(), new FakeStyle(16f, 1f));
        var first = Build(Line(Text("a")));
        var second = Build(Line(Text("bc")));

        cache.Store(5, first);
        cache.Store(5, second);

        Assert.Equal(1, cache.Count);
        Assert.Same(second, cache.TryGet(5));
    }

    [Fact]
    public void CachedLayout_MatchesFreshBuild_FieldByField()
    {
        var image = new NativeImage("FACE", null, null, 36, 18, 0, null);
        var line = new NativeLine(
            new List<NativeEntry>
            {
                new(new List<NativeSegment> { Text("aあb", color: "#00FF00", underline: true) }, null),
                new(new List<NativeSegment> { new(string.Empty, null, false, false, null, false, false, false, false, image, null) }, null),
            },
            "right");
        var cache = new NativeLineLayoutCache(4);
        var frame = Frame();
        var style = new FakeStyle(16f, 1f);
        cache.Prepare(frame, style);

        // 「强制 bypass」路径
        var fresh = NativeLineLayoutBuilder.Build(line, 1.25f, ResolveTypeface, Measure, out _, out _);

        // 「走缓存」路径
        cache.Store(0, fresh);
        var cached = Assert.IsType<NativeLineLayout>(cache.TryGet(0));

        AssertSameLayout(fresh, cached);

        // 再独立构建一次，确认缓存只是一个普通值（不引入额外状态）
        AssertSameLayout(fresh, NativeLineLayoutBuilder.Build(line, 1.25f, ResolveTypeface, Measure, out _, out _));
    }

    [Fact]
    public void FontPool_ReusesSameInstance_PerTypefaceVariantAndSize()
    {
        var created = 0;
        var pool = new NativeFontPool<FakeFont>((typeface, variant, size) =>
        {
            created++;
            return new FakeFont();
        });

        var first = pool.Get(Latin, NativeFontVariant.Regular, 16f);
        var again = pool.Get(Latin, NativeFontVariant.Regular, 16f);
        var bold = pool.Get(Latin, NativeFontVariant.Bold, 16f);
        var cjk = pool.Get(Cjk, NativeFontVariant.Regular, 16f);

        Assert.Same(first, again);
        Assert.NotSame(first, bold);
        Assert.NotSame(first, cjk);
        Assert.Equal(3, created);
        Assert.Equal(3, pool.Count);
    }

    [Fact]
    public void FontPool_DisposesOnSizeChange()
    {
        var pool = new NativeFontPool<FakeFont>((_, _, _) => new FakeFont());

        var oldFont = pool.Get(Latin, NativeFontVariant.Regular, 16f);
        Assert.False(oldFont.Disposed);

        var newFont = pool.Get(Latin, NativeFontVariant.Regular, 18f);

        Assert.True(oldFont.Disposed);
        Assert.False(newFont.Disposed);
        Assert.Equal(1, pool.Count);
        Assert.Equal(18f, pool.FontSize);
    }

    [Fact]
    public void FontPool_DisposeReleasesAllAndRejectsFurtherUse()
    {
        var pool = new NativeFontPool<FakeFont>((_, _, _) => new FakeFont());
        var latin = pool.Get(Latin, NativeFontVariant.Regular, 16f);
        var cjk = pool.Get(Cjk, NativeFontVariant.BoldItalic, 16f);

        pool.Dispose();

        Assert.True(latin.Disposed);
        Assert.True(cjk.Disposed);
        Assert.Equal(0, pool.Count);
        Assert.Throws<ObjectDisposedException>(() => pool.Get(Latin, NativeFontVariant.Regular, 16f));
    }

    private static object Frame() => new();

    private static void AssertSameLayout(NativeLineLayout expected, NativeLineLayout actual)
    {
        Assert.Equal(expected.TotalWidth, actual.TotalWidth);
        Assert.Equal(expected.Ops.Count, actual.Ops.Count);
        for (var i = 0; i < expected.Ops.Count; i++)
            Assert.Equal(expected.Ops[i], actual.Ops[i]);
        Assert.Equal(expected.Runs.Count, actual.Runs.Count);
        for (var i = 0; i < expected.Runs.Count; i++)
            Assert.Equal(expected.Runs[i], actual.Runs[i]);
        Assert.Equal(expected.Entries.Count, actual.Entries.Count);
        for (var i = 0; i < expected.Entries.Count; i++)
            Assert.Equal(expected.Entries[i], actual.Entries[i]);
        Assert.Equal(expected.Images.Count, actual.Images.Count);
        for (var i = 0; i < expected.Images.Count; i++)
            Assert.Equal(expected.Images[i], actual.Images[i]);
    }
}
