using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 基于内存 Channel 的 SessionIO，用于 HTTP 长轮询模式。
/// input/output 双 Channel，支持 async 读取与同步写入。
/// </summary>
internal sealed class HttpSessionIO : SessionIO, ITurnSink
{
    private readonly OutputHub _broadcaster;
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Channel<Turn> _output = Channel.CreateUnbounded<Turn>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = false
    });
    private volatile bool _closed;
    private readonly ConcurrentQueue<Turn> _unread = new();

    /// <summary>
    /// 注入旁路广播中枢：所有 <see cref="WriteLine"/> 的 turn <b>既</b>写入 HTTP 长轮询消费的
    /// <c>_output</c> Channel，<b>也</b>经 hub 广播给 WS 等观察者——必须同源、同序、同一份实例（不变量 O-4，
    /// 行为锁见 <c>HttpSessionIoDualWriteTests</c>）。二者不可只做其一，否则 HTTP 与 WS 输出分叉。
    /// <para>
    /// 直持 <see cref="OutputHub"/> 具体类型：本类原先经 <c>IOutputBroadcaster</c> 抽象注入，理由
    /// （"<c>OutputHub</c> 是 <c>internal sealed</c>，无法继承做 mock"）不成立——理由与历史沿革见
    /// <see cref="OutputHub"/> 的 remarks。
    /// </para>
    /// </summary>
    public HttpSessionIO(OutputHub broadcaster)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
    }

    /// <summary>
    /// 异步读取一行输入。
    /// Channel 关闭后返回 null（同步版 ReadLine 行为一致）。
    /// CancellationToken 取消时抛 OperationCanceledException，由调用方处理。
    /// </summary>
    public override async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        try
        {
            return await _input.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public override void WriteLine(Turn turn)
    {
        if (_closed)
            return;
        _output.Writer.TryWrite(turn);
        // 旁路广播：同一个 Turn 实例广播给所有 WS 等观察者，无需任何转换
        // （Wire 已是与 GET /turn 响应体字节一致的 TurnRecord v12 JSON）。
        _broadcaster.Publish(turn);
    }

    /// <summary>
    /// 关闭 IO。幂等：多次调用安全。
    /// 调用后 ReadLineAsync 返回 null，WriteLine 丢弃数据。
    /// 同时通知 <see cref="OutputHub"/> 完成所有观察者 Channel，使 WS 连接收到关闭帧。
    /// </summary>
    public override void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _input.Writer.TryComplete();
        _output.Writer.TryComplete();
        _broadcaster.Complete();
    }

    public override bool IsConnected => !_closed;

    /// <summary>供 HttpGameServer.POST /input 调用：入队输入并唤醒读取方。</summary>
    public void EnqueueInput(string line)
    {
        if (_closed)
            return;
        _input.Writer.TryWrite(line);
    }

    /// <summary>
    /// 供 Session.WaitForTurnAsync 调用：异步等待并读取一个 output turn。
    /// Channel 关闭且队列空时返回 null（语义与同步 TryDequeueOutput 关闭后返回 false 一致）。
    /// CancellationToken 取消时抛 OperationCanceledException，由调用方处理。
    /// 注意：output Channel 配置为 SingleReader=false，多 reader 并发安全。
    /// </summary>
    public List<Turn> DrainOutput()
    {
        var turns = new List<Turn>();
        while (_unread.TryDequeue(out var unread))
            turns.Add(unread);
        while (_output.Reader.TryRead(out var turn))
            turns.Add(turn);
        return turns;
    }

    /// <summary>
    /// 把刚读出的 turn 放回队列头部，供后续 Drain/Read 再消费。
    /// 用于控制权变更时：in-flight GET /turn 已取出回合但不能交给旧 Controller。
    /// </summary>
    public void UnreadOutput(Turn turn)
    {
        _unread.Enqueue(turn);
    }

    public async Task<Turn?> ReadOutputAsync(CancellationToken ct)
    {
        if (_unread.TryDequeue(out var unread))
            return unread;
        try
        {
            return await _output.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}
