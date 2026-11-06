using System;
using System.Collections.Generic;
using EraCore.Native;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 可见窗口布局预热批次：并入校验（frame 引用 / style 值 / 行对象引用）与「预热结果 = 现场构建」等价性。
/// 预热范围的索引算术直接复用 <c>VirtualWindow.Compute</c>，与渲染侧完全相同，这里只覆盖并入口径。
/// </summary>
public sealed class NativeLayoutPrewarmTests
{
    private const string Latin = "latin";
    private const string Cjk = "cjk";

    [Fact]
    public void Batch_MatchesOnlySameFrameReferenceAndStyleValue()
    {
        var frame = FrameWith(Line("a"), Line("b"));
        var style = new FakeStyle(16f);
        var batch = new NativeLayoutPrewarmBatch(
            frame, style, new[] { 0 }, new[] { frame.Lines[0] }, new[] { NativeLineLayout.Empty });

        Assert.True(batch.Matches(frame, new FakeStyle(16f)));
        Assert.False(batch.Matches(FrameWith(Line("a")), style)); // 新 frame 引用
        Assert.False(batch.Matches(frame, new FakeStyle(18f)));    // style 值变化
    }

    [Fact]
    public void Batch_AdoptsOnlyMatchingLineReferences()
    {
        var frame = FrameWith(Line("a"), Line("b"));
        var lineA = frame.Lines[0];
        var lineB = frame.Lines[1];
        var batch = new NativeLayoutPrewarmBatch(
            frame,
            new FakeStyle(16f),
            new[] { 0, 1 },
            new[] { lineA, lineB },
            new[] { NativeLineLayout.Empty, NativeLineLayout.Empty });

        var cache = new NativeLineLayoutCache(4);
        cache.Prepare(frame, new FakeStyle(16f));

        Assert.Equal(2, batch.AdoptInto(cache, frame.Lines));
        Assert.NotNull(cache.TryGet(0));
        Assert.NotNull(cache.TryGet(1));
    }

    [Fact]
    public void Batch_SkipsShiftedWindowAndOutOfRangeIndices()
    {
        // 预热按旧窗口构建：index 0 → lineA；绘制时窗口上移，index 1 才是 lineA。
        var frame = FrameWith(Line("x"), Line("a"));
        var lineA = frame.Lines[1];
        var batch = new NativeLayoutPrewarmBatch(
            frame,
            new FakeStyle(16f),
            new[] { 0, 7 },
            new[] { lineA, lineA },
            new[] { NativeLineLayout.Empty, NativeLineLayout.Empty });

        var cache = new NativeLineLayoutCache(4);
        cache.Prepare(frame, new FakeStyle(16f));

        Assert.Equal(0, batch.AdoptInto(cache, frame.Lines));
        Assert.Null(cache.TryGet(0));
        Assert.Null(cache.TryGet(1));
    }

    [Fact]
    public void Batch_AdoptsOverlappingLinesAfterWindowShift()
    {
        var frame = FrameWith(Line("x"), Line("a"), Line("b"));
        var lineA = frame.Lines[1];
        var lineB = frame.Lines[2];
        // 预热时窗口是 [1,2]（lineA/lineB）；绘制时窗口下移一行，仍会命中这两行的绝对下标。
        var batch = new NativeLayoutPrewarmBatch(
            frame,
            new FakeStyle(16f),
            new[] { 1, 2 },
            new[] { lineA, lineB },
            new[] { NativeLineLayout.Empty, NativeLineLayout.Empty });

        var cache = new NativeLineLayoutCache(4);
        cache.Prepare(frame, new FakeStyle(16f));

        Assert.Equal(2, batch.AdoptInto(cache, frame.Lines));
        Assert.NotNull(cache.TryGet(1));
        Assert.NotNull(cache.TryGet(2));
    }

    [Fact]
    public void Batch_RejectsMismatchedParallelArrays()
    {
        var frame = FrameWith(Line("a"));
        Assert.Throws<ArgumentException>(() => new NativeLayoutPrewarmBatch(
            frame,
            new FakeStyle(16f),
            new[] { 0, 1 },
            new[] { frame.Lines[0] },
            new[] { NativeLineLayout.Empty }));
    }

    [Fact]
    public void Batch_PrewarmedLayoutEqualsDirectBuild()
    {
        var line = Line("abc漢def");
        var frame = FrameWith(line);
        var style = new FakeStyle(16f);

        var direct = NativeLineLayoutBuilder.Build(
            line,
            1f,
            ResolveTypeface,
            Measure,
            out var directRuns,
            out var directChars);
        Assert.True(directRuns > 0);
        Assert.True(directChars > 0);

        // 预热线程构建的同一条行：用同一 builder + 同一组注入函数，必须逐字段相同。
        var prewarmed = NativeLineLayoutBuilder.Build(
            line,
            1f,
            ResolveTypeface,
            Measure,
            out _, out _);
        var batch = new NativeLayoutPrewarmBatch(
            frame,
            style,
            new[] { 0 },
            new[] { line },
            new[] { prewarmed });

        var cache = new NativeLineLayoutCache(4);
        cache.Prepare(frame, style);
        Assert.Equal(1, batch.AdoptInto(cache, frame.Lines));

        var cached = Assert.IsType<NativeLineLayout>(cache.TryGet(0));
        Assert.Equal(direct.TotalWidth, cached.TotalWidth);
        Assert.Equal(direct.Ops.Count, cached.Ops.Count);
        for (var i = 0; i < direct.Ops.Count; i++)
            Assert.Equal(direct.Ops[i], cached.Ops[i]);
        Assert.Equal(direct.Runs.Count, cached.Runs.Count);
        for (var i = 0; i < direct.Runs.Count; i++)
            Assert.Equal(direct.Runs[i], cached.Runs[i]);
    }

    private static NativeFrame FrameWith(params NativeLine[] lines)
        => new(lines, "#000000", 0, 0, 0, null);

    private static NativeLine Line(string text)
        => new(
            new[]
            {
                new NativeEntry(
                    new[]
                    {
                        new NativeSegment(text, null, false, false, null, false, false),
                    },
                    null),
            },
            null);

    private static object ResolveTypeface(char c) => c < 0x80 ? Latin : Cjk;

    private static float Measure(string text, object typeface, NativeFontVariant variant)
        => text.Length * (ReferenceEquals(typeface, Cjk) ? 20f : 10f);

    private sealed record FakeStyle(float LineHeight);
}
