using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;

namespace MinorShift.Emuera.Output;

/// <summary>
/// 输出广播中枢（Hub 旁路模式）。
///
/// 游戏循环 <c>HttpSessionIO.WriteLine(turn)</c> 时，<see cref="Turn"/> 实例既推入 HTTP 长轮询消费
/// 的 <c>_output</c> Channel，也经本 Hub 广播给所有经 <see cref="Subscribe"/> 挂上来的
/// 旁路传输（WebSocket 连接等观察者）。
///
/// 设计要点：
/// - 纯转发，不解析、不持有业务状态，也不依赖 <c>Session</c>/协议层/引擎层。
/// - 每个订阅者拿到一个独立、独占的 <see cref="ChannelReader{T}"/>（晚加入者只收后续 turn）。
/// - 线程安全：游戏循环（Publish）与 WS 处理线程（Subscribe/Unsubscribe）并发访问。
/// - <see cref="Complete"/> 在 session 结束时调用，完成所有订阅者 Channel，使 WS 发送循环
///   自然结束并下发关闭帧。
/// </summary>
/// <remarks>
/// <b>为什么没有 <c>IOutputBroadcaster</c> 抽象</b>（2026-10-19 收）：本类原先实现
/// <c>IOutputBroadcaster</c>，动机是让 <c>HttpSessionIO</c> 能注入假实现——随之而来的第二条论据是
/// "<c>OutputHub</c> 是 <c>internal sealed</c>，无法继承做 mock"。
/// 两点都不成立：①「不能继承」不等于「不能用于测试」——本类按 O-1 零外部依赖，可直接 <c>new</c>，
/// 生产类本身就是最好的替身；②该接口自创立起<b>从未被任何替身实现过</b>，而唯一装配点
/// <c>SessionRegistry.CreateSessionCore</c> 与 <c>WsSubscription</c> 全都直指具体类型——
/// 抽象面 1 处 vs 具体面 4 处，耦合没消除，只是挪到了 registry。
/// <para>
/// 历史沿革（保留以备将来判断"会不会再有第二个实现"）：HTTP 与 MAUI 托管共用同一份中枢；
/// 原 <c>MauiBridgeIO</c>（曾经的第二个适配方向）已随 issue 05 托管架构删除。
/// 若将来真的出现第二个投递目标，按 O-3' 重新引入接口即可，成本与本次删除对称。
/// </para>
/// </remarks>
internal sealed class OutputHub
{
    private readonly object _subscribersLock = new();
    private readonly List<(ChannelReader<Turn> Reader, ChannelWriter<Turn> Writer)> _subscribers = new();
    private bool _completed;

    /// <summary>
    /// 注册一个新观察者，返回其独占的 <see cref="ChannelReader{T}"/>。
    /// 晚加入者只收连接之后新产生的 turn（订阅 Channel 不回放历史）。
    /// 若 Hub 已 <see cref="Complete"/>（session 已结束），返回已完成的 reader。
    /// </summary>
    public ChannelReader<Turn> Subscribe()
    {
        var channel = Channel.CreateUnbounded<Turn>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        lock (_subscribersLock)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
                return channel.Reader;
            }

            _subscribers.Add((channel.Reader, channel.Writer));
        }

        return channel.Reader;
    }

    /// <summary>
    /// 向所有当前订阅者各推送一次 turn。已 <see cref="Complete"/> 后无操作。
    /// 新订阅者在本次 Publish 快照之后注册，将只收后续 turn（可接受的最终一致性）。
    /// </summary>
    public void Publish(Turn turn)
    {
        if (turn == null)
            return;

        List<ChannelWriter<Turn>> writers;
        lock (_subscribersLock)
        {
            if (_completed)
                return;
            writers = _subscribers.ConvertAll(s => s.Writer);
        }

        foreach (var writer in writers)
            writer.TryWrite(turn);
    }

    /// <summary>
    /// 移除一个观察者（WS 断开 / 取消订阅时调用）。同时完成其 writer 以结束其发送循环。
    /// 未找到对应 reader 时安全无操作。
    /// </summary>
    public void Unsubscribe(ChannelReader<Turn> reader)
    {
        lock (_subscribersLock)
        {
            var idx = _subscribers.FindIndex(s => s.Reader == reader);
            if (idx < 0)
                return;

            _subscribers[idx].Writer.TryComplete();
            _subscribers.RemoveAt(idx);
        }
    }

    /// <summary>
    /// 结束整个 Hub：完成所有订阅者 Channel 并清空列表。幂等。
    /// 由 <c>HttpSessionIO.Close()</c> 在 session 结束时调用，
    /// 使所有 WS 发送循环自然结束并下发关闭帧。
    /// </summary>
    public void Complete()
    {
        List<ChannelWriter<Turn>> writers;
        lock (_subscribersLock)
        {
            if (_completed)
                return;
            _completed = true;
            writers = _subscribers.ConvertAll(s => s.Writer);
            _subscribers.Clear();
        }

        foreach (var writer in writers)
            writer.TryComplete();
    }
}
