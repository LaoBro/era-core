using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 不变量 O-3' / O-4 的行为锁：<c>HttpSessionIO.WriteLine</c> 的<b>双写语义</b>——一个 turn 必须
/// 同时送达 HTTP 长轮询（<c>_output</c> Channel）与旁路广播（<c>OutputHub</c> → WS 观察者的独占 reader），
/// 且两侧拿到的是<b>同一份字符串实例</b>，不是"内容相等的两份"（见下方对 <c>Assert.Same</c> 的说明）。
/// <para>
/// 被锁住的事故模式：有人只给一侧做加工（WS 侧裁剪字段 / HTTP 侧攒批再发 / 重新序列化一次），
/// <c>/turn</c> 与 WS 就永久看到两个不同的世界，症状是"WebSocket 模式下按钮不刷新、HTTP 模式下正常"
/// 这类最难归因的分叉。此文件让该类改动立即转红。
/// </para>
/// <para>
/// <b>为什么这里用生产类 <c>OutputHub</c> 而不是替身</b>：O-1 保证它纯转发、不解析、不持业务状态、
/// 零外部依赖——可以直接 <c>new</c>，"不能继承"不等于"不能用于测试"。测替身只能验证
/// <c>HttpSessionIO</c> 有没有调 <c>Publish</c>，测不到 <c>OutputHub</c> 自己的 Complete 幂等与
/// observer Channel 完成语义——那才是真正有并发风险的一层。见 <c>ARCHITECTURE.md</c> §3.3 O-1。
/// </para>
/// </summary>
public class HttpSessionIoDualWriteTests
{
    private const string Turn = """{"protocolVersion":12,"generation":1,"type":"turn"}""";

    /// <summary>
    /// O-4 的正向：写入一次，两条路径各拿到一份。
    /// <para>
    /// <b>为什么是 <c>Assert.Same</c> 而非 <c>Assert.Equal</c></b>：O-4 要求的是"同一份 turn，无需任何转换"。
    /// 任一侧一旦重新序列化、Trim、繁简转换或 rebuffering，<c>Equal</c> 仍会通过而 <c>Same</c> 立即转红——
    /// 这正是本文件要拦的那类改动。
    /// </para>
    /// </summary>
    [Fact]
    public void WriteLine_reaches_both_the_long_poll_channel_and_the_broadcast()
    {
        var hub = new OutputHub();
        var ws = hub.Subscribe();
        var io = new HttpSessionIO(hub);

        io.WriteLine(Turn);

        Assert.True(ws.TryRead(out var viaWs), "WS observer must receive the turn.");
        var viaHttp = io.DrainOutput();

        Assert.Single(viaHttp);
        Assert.Same(Turn, viaWs);
        Assert.Same(Turn, viaHttp[0]);
    }

    /// <summary>
    /// O-2 的广播侧前提：每个观察者独占 reader，各自拿到完整的一份——不是"谁抢到算谁的"。
    /// 这是"双写"在观察者为 N 个时仍然成立的前提（两个浏览器标签看到同样的画面）。
    /// </summary>
    [Fact]
    public void Every_observer_receives_its_own_copy_of_the_same_turn()
    {
        var hub = new OutputHub();
        var first = hub.Subscribe();
        var second = hub.Subscribe();
        var io = new HttpSessionIO(hub);

        io.WriteLine(Turn);

        Assert.True(first.TryRead(out var viaFirst), "the first observer must receive the turn.");
        Assert.True(second.TryRead(out var viaSecond), "the second observer must receive its own copy.");
        Assert.Same(Turn, viaFirst);
        Assert.Same(Turn, viaSecond);
    }

    /// <summary>
    /// 两条路径必须是同一个序列：顺序也要一致。
    /// 只断言"都收到了"会放过 WS 侧攒批/重排这类改动。
    /// </summary>
    [Fact]
    public void Both_paths_observe_the_same_sequence_in_the_same_order()
    {
        var hub = new OutputHub();
        var ws = hub.Subscribe();
        var io = new HttpSessionIO(hub);
        var turns = new List<string> { Turn, Turn + "-b", Turn + "-c" };

        foreach (var turn in turns)
            io.WriteLine(turn);

        Assert.Equal(turns, io.DrainOutput());

        var viaWs = new List<string>();
        while (ws.TryRead(out var turn))
            viaWs.Add(turn);
        Assert.Equal(turns, viaWs);
    }

    /// <summary>
    /// 关闭的协作：<c>OutputHub</c> 必须完成观察者 Channel（WS 发送循环据此下发关闭帧），
    /// 且不吞掉关闭前已发布的 turn，两条路径此后都终结而非无限挂起。
    /// </summary>
    [Fact]
    public async Task Close_completes_observers_without_swallowing_the_pending_turn()
    {
        var hub = new OutputHub();
        var ws = hub.Subscribe();
        var io = new HttpSessionIO(hub);

        io.WriteLine(Turn);
        io.Close();
        io.Close(); // 幂等：重复关闭不得改变副作用

        // 关闭前发布的 turn 仍须可读——Complete 不等于丢弃。
        var drained = io.DrainOutput();
        Assert.Single(drained);
        Assert.Same(Turn, drained[0]);
        Assert.True(ws.TryRead(out var pending), "the turn published before Close must survive Close.");
        Assert.Same(Turn, pending);

        // 两侧都已排空并终结：读耗尽后返回终态，而不是挂住调用方。
        Assert.False(await ws.WaitToReadAsync(), "the WS observer channel must complete on Close.");
        Assert.Null(await io.ReadOutputAsync(CancellationToken.None));
    }

    /// <summary>
    /// 关闭后的写入必须对两条路径同时静默——只静默一侧会让 HTTP 与 WS 的帧号错位。
    /// </summary>
    [Fact]
    public void WriteLine_after_close_reaches_neither_path()
    {
        var hub = new OutputHub();
        var ws = hub.Subscribe();
        var io = new HttpSessionIO(hub);

        io.Close();
        io.WriteLine(Turn);

        Assert.False(ws.TryRead(out _), "no turn may reach a WS observer after Close.");
        Assert.Empty(io.DrainOutput());
    }
}
