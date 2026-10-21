using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;

namespace MinorShift.Emuera.Server;

/// <summary>
/// WebSocket 旁路传输循环（双宿主共享，issue 05「抽共享层 + 双宿主」）。
/// <para>
/// 从 <see cref="WsConnectionHandler"/>（原 Kestrel 宿主内）抽出的传输无关部分——只依赖 BCL 的
/// <c>System.Net.WebSockets.WebSocket</c> 与 <c>System.Threading.Channels</c>，不依赖 ASP.NET Core。
/// Kestrel（<c>HttpContext.WebSockets.AcceptWebSocketAsync</c>）与 MAUI HttpListener
/// （<c>HttpListenerContext.AcceptWebSocketAsync</c>）拿到同一个 BCL <c>WebSocket</c> 实例后，
/// 都调用 <see cref="RunConnectionAsync"/> 走同一份发布/订阅/输入转发逻辑，行为与 wire 契约一致。
/// </para>
/// <para>
/// 生命周期：
/// - 无可用 session 由宿主在订阅前拦截（关闭码 4004）；
/// - 双循环并行（发送循环从 hub 订阅 reader 读 turn、接收循环读输入帧），任一侧结束即收尾退订；
/// - session 结束（hub.Complete）以 WS 关闭帧通知客户端。
/// </para>
/// </summary>
internal static class WsRelay
{
    /// <summary>无可用 session 时 /ws 升级被拒，下发的自定义 WS 关闭码（未在 IANA 注册，借用于"应用层错误"语义）。</summary>
    internal const WebSocketCloseStatus WsCloseNoActiveSession = (WebSocketCloseStatus)4004;

    /// <summary>
    /// 连接主循环（internal 可测核心）：双循环并行，任一侧结束即收尾。
    /// - 发送循环从 hub 订阅 reader 读 turn，逐个以裸 JSON 文本帧下发；reader 完成（session 结束）即退出。
    /// - 接收循环读文本帧 → 校验 type=="input" → EnqueueInput；客户端关闭帧即退出。
    /// </summary>
    internal static async Task RunConnectionAsync(WebSocket ws, WsSubscription subscription)
    {
        using var cts = new CancellationTokenSource();

        try
        {
            await Task.WhenAny(
                SendLoopAsync(ws, subscription.Reader, cts.Token),
                ReceiveLoopAsync(ws, subscription, cts.Token)
            );
        }
        finally
        {
            cts.Cancel();
            subscription.Hub.Unsubscribe(subscription.Reader);
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived or WebSocketState.CloseSent)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch (WebSocketException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>发送循环：从 hub 订阅 reader 读 turn，逐个以裸 JSON 文本帧下发。reader 完成（session 结束）即退出。</summary>
    private static async Task SendLoopAsync(WebSocket ws, ChannelReader<Turn> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var turn in reader.ReadAllAsync(ct))
            {
                if (ws.State != WebSocketState.Open)
                    break;
                var bytes = Encoding.UTF8.GetBytes(turn.Wire);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 被取消（对端结束 / 收尾），正常退出。
        }
        catch (ChannelClosedException)
        {
            // hub.Complete() 完成 reader，正常退出。
        }
        catch (WebSocketException)
        {
            // 客户端断开，正常退出。
        }
    }

    /// <summary>接收循环：读文本帧 → 校验 type=="input" → EnqueueInput。支持分片重组。客户端关闭帧即退出。</summary>
    private static async Task ReceiveLoopAsync(WebSocket ws, WsSubscription subscription, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var accumulator = new List<byte>(8192);
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                accumulator.AddRange(buffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage)
                    continue;

                var text = Encoding.UTF8.GetString(accumulator.ToArray());
                accumulator.Clear();
                HandleWsInput(text, subscription.Session, subscription.Identity);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消，正常退出。
        }
        catch (WebSocketException)
        {
            // 客户端断开，正常退出。
        }
    }

    /// <summary>WS 输入帧已是 <c>{"type":"input","value":"..."}</c> 格式，直接入队。
    /// 协议层 <c>AgentJsonlProtocol</c> 会校验 <c>type=="input"</c>，无效帧自然被忽略。</summary>
    private static void HandleWsInput(string text, Session session, ControlIdentity identity)
    {
        var gate = session.Controller.CheckInput(identity, session.HasEnded);
        if (gate.Status == ControlGateStatus.Allowed)
            session.IO.EnqueueInput(text);
    }
}
