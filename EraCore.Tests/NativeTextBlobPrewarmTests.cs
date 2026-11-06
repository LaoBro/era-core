using System;
using System.Collections.Generic;
using EraCore.Native;
using Xunit;

namespace EraCore.Tests;

/// <summary>
/// 文本 blob 预热批次：校验口径、所有权转移，以及「创建 = 采纳 + 释放」的生命周期守恒。
/// 用假 blob 记录每次释放，可在 Windows 上穷举原生对象的所有权路径（真机只做最终验证）。
/// </summary>
public sealed class NativeTextBlobPrewarmTests
{
    [Fact]
    public void Batch_MatchesOnlySameFrameReferenceAndStyleValue()
    {
        var frame = new object();
        var style = new FakeStyle(16f);
        var batch = Batch(frame, style, new[] { Key("a") }, new[] { new FakeBlob() });

        Assert.True(batch.Matches(frame, new FakeStyle(16f)));
        Assert.False(batch.Matches(new object(), style));
        Assert.False(batch.Matches(frame, new FakeStyle(18f)));
    }

    [Fact]
    public void Batch_TransfersOwnershipAndReleasesDuplicates()
    {
        var accepted = new List<string>();
        var first = new FakeBlob();
        var second = new FakeBlob();
        var keys = new[] { Key("a"), Key("b") };
        var batch = Batch(new object(), new FakeStyle(16f), keys, new[] { first, second });

        // 缓存已有一条同键条目：第二个 blob 必须立刻释放，且不再属于批次。
        var adopted = batch.AdoptInto((key, blob) =>
        {
            accepted.Add(key.Text);
            return key.Text != "b";
        });

        Assert.Equal(1, adopted);
        Assert.Equal(1, batch.AdoptedCount);
        Assert.Equal(1, batch.ReleasedCount);
        Assert.False(first.Disposed);       // 所有权已转移给缓存
        Assert.True(second.Disposed);       // 键已存在于缓存 → 释放新造的
        Assert.Equal(0, batch.Release());    // 已没有剩余对象可释放
    }

    [Fact]
    public void Batch_ReleaseDisposesOnlyRemainingInOrder()
    {
        var order = new List<int>();
        var zero = new FakeBlob(0);
        var one = new FakeBlob(1);
        var two = new FakeBlob(2);
        var batch = new NativeTextBlobPrewarmBatch<FakeBlob>(
            new object(),
            new FakeStyle(16f),
            new[] { Key("a"), Key("b"), Key("c") },
            new[] { zero, one, two },
            blob =>
            {
                order.Add(blob.Id);
                blob.Disposed = true;
            });

        Assert.Equal(3, batch.Release());
        Assert.Equal(new[] { 0, 1, 2 }, order);
        Assert.Equal(0, batch.Release());      // 幂等
        Assert.All(new[] { zero, one, two }, blob => Assert.True(blob.Disposed));
    }

    [Fact]
    public void Batch_ReleaseSkipsCreationFailures()
    {
        var blobs = new[] { new FakeBlob(), null, new FakeBlob() };
        var batch = new NativeTextBlobPrewarmBatch<FakeBlob>(
            new object(),
            new FakeStyle(16f),
            new[] { Key("a"), Key("b"), Key("c") },
            blobs,
            blob => blob.Disposed = true);

        // 创建失败（null）的键不占生命周期份额：Count 是键数，Release 只释放真对象。
        Assert.Equal(3, batch.Count);
        Assert.Equal(2, batch.Release());
        Assert.Equal(2, batch.ReleasedCount);
    }

    [Fact]
    public void Lifecycle_CountsAreConserved()
    {
        // 模拟一次换帧：预热创建 5 个 → 缓存采纳 3 个、拒收 1 个（键已存在）、整批作废 1 个。
        var created = 0;
        var adopted = 0;
        var released = 0;

        FakeBlob Make()
        {
            created++;
            return new FakeBlob();
        }

        void Dispose(FakeBlob blob)
        {
            blob.Disposed = true;
            released++;
        }

        var live = new[] { Make(), Make(), Make(), Make() };
        var liveBatch = new NativeTextBlobPrewarmBatch<FakeBlob>(
            new object(),
            new FakeStyle(16f),
            new[] { Key("a"), Key("b"), Key("c"), Key("d") },
            live,
            Dispose);

        adopted += liveBatch.AdoptInto((key, _) => key.Text != "d"); // a/b/c 采纳，d 被拒收并释放
        Assert.Equal(1, liveBatch.ReleasedCount);

        var stale = new[] { Make() };
        var staleBatch = new NativeTextBlobPrewarmBatch<FakeBlob>(
            new object(),
            new FakeStyle(16f),
            new[] { Key("e") },
            stale,
            Dispose);
        Assert.Equal(1, staleBatch.Release());  // frame 已换，整批作废

        Assert.Equal(5, created);
        Assert.Equal(3, adopted);
        Assert.Equal(2, released);
        Assert.Equal(0, created - adopted - released); // 无在途、无泄漏
        Assert.Equal(0, liveBatch.Release());          // 已采纳的不可能被再次释放
        Assert.Equal(0, staleBatch.Release());
    }

    [Fact]
    public void Batch_RejectsMismatchedParallelArrays()
    {
        Assert.Throws<ArgumentException>(() => new NativeTextBlobPrewarmBatch<FakeBlob>(
            new object(),
            new FakeStyle(16f),
            new[] { Key("a"), Key("b") },
            new[] { new FakeBlob() },
            _ => { }));
    }

    [Fact]
    public void Policy_RejectsSurrogatePairsOnly()
    {
        Assert.True(NativeTextBlobPolicy.CanBlob("abc漢字def"));
        Assert.True(NativeTextBlobPolicy.CanBlob(string.Empty));
        Assert.False(NativeTextBlobPolicy.CanBlob("a\U0001F600b"));    // 成对代理
        Assert.False(NativeTextBlobPolicy.CanBlob("\U0001F600"));       // 单个 emoji = 一对代理
        Assert.True(NativeTextBlobPolicy.CanBlob("a\uD800"));          // 孤立高代理不算成对
        Assert.True(NativeTextBlobPolicy.CanBlob("\uDC00b"));          // 孤立低代理不算成对
    }

    private static NativeTextBlobKey Key(string text) => new(text, "typeface", NativeFontVariant.Regular, 16f);

    private static NativeTextBlobPrewarmBatch<FakeBlob> Batch(
        object frame,
        object style,
        NativeTextBlobKey[] keys,
        FakeBlob?[] blobs)
        => new(frame, style, keys, blobs, blob => blob.Disposed = true);

    private sealed class FakeBlob
    {
        public FakeBlob(int id = 0)
        {
            Id = id;
        }

        public int Id { get; }

        public bool Disposed { get; set; }
    }

    private sealed record FakeStyle(float FontSize);
}
