using System;
using System.Threading.Channels;

namespace MinorShift.Emuera.Output;

/// <summary>
/// 可退订的回合订阅（P1）。
///
/// <para>语义与 <see cref="OutputPort.Turns"/> 一致：订阅后<b>不回放历史</b>，只收订阅之后的
/// 新 turn。宿主正确用法是<b>先订阅、再取快照</b>，然后丢弃
/// <c>revision &lt;= snapshot.revision</c> 的帧（v13），再按 <c>revision</c> 顺序应用增量。</para>
///
/// <para>没有旁路中枢（例如 CLI 只读快照）时 <see cref="Turns"/> 为 null；
/// <see cref="Dispose"/> 幂等，内部完成 writer 并退订。</para>
/// </summary>
public sealed class TurnSubscription : IDisposable
{
    private readonly OutputHub? _hub;
    private readonly ChannelReader<Turn>? _reader;
    private bool _disposed;

    internal TurnSubscription(OutputHub? hub, ChannelReader<Turn>? reader)
    {
        _hub = hub;
        _reader = reader;
    }

    /// <summary>订阅的回合流；null = 本会话/本宿主没有旁路中枢。</summary>
    public ChannelReader<Turn>? Turns => _reader;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hub is not null && _reader is not null)
            _hub.Unsubscribe(_reader);
    }
}
