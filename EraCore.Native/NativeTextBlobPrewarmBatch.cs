using System;

namespace EraCore.Native;

/// <summary>
/// 后台预热的文本 blob 批次：预热线程在私有数组里构建 <c>SKTextBlob</c>，渲染线程在 <c>Draw</c>
/// 起点按「frame 引用 + style 全字段值」校验后并入 Android 侧的 <c>NativeTextBlobCache</c>。
///
/// <para><b>所有权模型（这是本类存在的核心理由）</b>：批次在发布时独占其全部 blob；</para>
/// <list type="bullet">
/// <item>被采纳的行：所有权转移给缓存（本类把槽位置空，绝不再碰它，避免缓存 Reset 与本类双重释放）；</item>
/// <item>键已存在等采纳失败的行：立刻用 <c>release</c> 释放（缓存里已有的那一份继续用）；</item>
/// <item>整批作废（frame 已换 / style 已变 / 预热器卸载）：<see cref="Release"/> 释放剩余全部；</item>
/// <item>尚未发布就作废的块：由预热线程就地释放，不进入队列。</item>
/// </list>
/// <para>因此任何时刻都满足 <c>创建数 = 已采纳 + 已释放 + 在途（队列内）</c>，且不存在「既在队列里又被
/// 释放」或「已采纳又被释放」的对象。批次本身只在单一线程内被采纳/释放（队列出队者或预热器 Dispose 者），
/// 不需要加锁。</para>
///
/// <para>构造后批次<b>接管</b>传入的 <c>keys</c>/<c>blobs</c> 数组：采纳或释放时就地把槽位置空，
/// 调用方发布后不得再读写这两个数组。</para>
/// <para><c>TBlob</c> 为泛型，是为了让上面这套「校验 + 所有权转移 + 释放计数」可以在 Windows 单测里
/// 用假对象穷举，而不依赖 Skia 的原生对象。</para>
/// </summary>
public sealed class NativeTextBlobPrewarmBatch<TBlob> where TBlob : class
{
    private readonly object _frame;
    private readonly object _style;
    private readonly NativeTextBlobKey[] _keys;

    /// <summary>与 <see cref="_keys"/> 等长；某个键创建失败时为 null（绘制侧会再次尝试并回退）。</summary>
    private readonly TBlob?[] _blobs;

    private readonly Action<TBlob> _release;
    private int _adopted;
    private int _released;

    public NativeTextBlobPrewarmBatch(
        object frame,
        object style,
        NativeTextBlobKey[] keys,
        TBlob?[] blobs,
        Action<TBlob> release)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentNullException.ThrowIfNull(release);
        if (keys.Length != blobs.Length)
            throw new ArgumentException("keys/blobs 长度必须一致");

        _frame = frame;
        _style = style;
        _keys = keys;
        _blobs = blobs;
        _release = release;
    }

    /// <summary>批次覆盖的键数（含创建失败的空位）。</summary>
    public int Count => _keys.Length;

    /// <summary>已被缓存接纳（所有权已转移）的 blob 数。</summary>
    public int AdoptedCount => _adopted;

    /// <summary>已被本批次释放的 blob 数（含「键已存在」的拒收与整批作废）。</summary>
    public int ReleasedCount => _released;

    /// <summary>与当前绘制的 frame 引用相同且 style 全字段相等时才允许并入。</summary>
    public bool Matches(object frame, object style)
        => ReferenceEquals(_frame, frame) && _style.Equals(style);

    /// <summary>
    /// 逐条调用 <paramref name="adopt"/>：返回 true 表示缓存已接管该 blob（本批次清空槽位），
    /// 返回 false 表示缓存已有同键条目（本批次立即释放新造的这一个）。返回实际采纳条数。
    /// </summary>
    public int AdoptInto(Func<NativeTextBlobKey, TBlob, bool> adopt)
    {
        ArgumentNullException.ThrowIfNull(adopt);

        var adopted = 0;
        for (var i = 0; i < _keys.Length; i++)
        {
            var blob = _blobs[i];
            if (blob is null)
                continue;

            _blobs[i] = null;
            if (adopt(_keys[i], blob))
            {
                adopted++;
                _adopted++;
            }
            else
            {
                _released++;
                _release(blob);
            }
        }

        return adopted;
    }

    /// <summary>释放所有尚未被采纳的 blob（幂等），返回本次释放条数。</summary>
    public int Release()
    {
        var released = 0;
        for (var i = 0; i < _keys.Length; i++)
        {
            var blob = _blobs[i];
            if (blob is null)
                continue;

            _blobs[i] = null;
            _released++;
            _release(blob);
            released++;
        }

        return released;
    }
}
