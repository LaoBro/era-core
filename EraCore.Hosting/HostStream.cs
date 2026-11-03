using System;
using System.Threading.Channels;
using MinorShift.Emuera.Output;

namespace EraCore.Hosting;

/// <summary>
/// 一次「初始快照 + 后续增量流」的原子组合。
///
/// <para><b>用法</b>：先订阅（<see cref="Subscription"/>）、后取 <see cref="InitialSnapshot"/>；
/// 随后丢弃 <c>revision &lt;= InitialSnapshot.revision</c> 的帧（v13），再按 revision 顺序应用
/// <c>Turn.Record.diff</c>。不使用增量时可直接忽略 <see cref="Turns"/>，只轮询快照。</para>
/// </summary>
public sealed class HostStream : IDisposable
{
    internal HostStream(DisplaySnapshot initialSnapshot, TurnSubscription subscription)
    {
        InitialSnapshot = initialSnapshot;
        Subscription = subscription;
    }

    /// <summary>订阅时刻之后的全量基线。</summary>
    public DisplaySnapshot InitialSnapshot { get; }

    public TurnSubscription Subscription { get; }

    /// <summary>后续回合流；null = 没有旁路中枢，宿主只能读快照。</summary>
    public ChannelReader<Turn>? Turns => Subscription.Turns;

    public void Dispose() => Subscription.Dispose();
}
