using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// WsConnectionHandler 单元测试（重构补测——原 WS 端点零覆盖）。
/// 用 RecordingWebSocket 录制 Send/Close 调用、FakeWebSocketFeature 注入 WS 请求，
/// 覆盖：非 WS 请求 400、无 session 关闭码 4004、订阅-发布-退订、session 结束正常关闭。
/// </summary>
public class WsConnectionHandlerTests
{
    private static WsConnectionHandler CreateHandler()
    {
        var config = new GameConfigService(new ConfigData());
        var registry = new SessionRegistry(new SessionRegistryTests.NullTerminalSetup(), config);
        return new WsConnectionHandler(registry);
    }

    private static (Session session, HttpSessionIO io, OutputHub hub) CreateSessionAndIo()
    {
        var hub = new OutputHub();
        var io = new HttpSessionIO(hub);
        var session = new Session(io, new SessionRegistryTests.NullTerminalSetup(), new ConfigData());
        return (session, io, hub);
    }

    [Fact]
    public async Task HandleAsync_rejects_non_ws_request_with_400()
    {
        var handler = CreateHandler();
        var ctx = new DefaultHttpContext(); // 无 IHttpWebSocketFeature → IsWebSocketRequest=false

        await handler.HandleAsync(ctx);

        Assert.Equal(400, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_closes_with_4004_when_no_active_session()
    {
        var handler = CreateHandler();
        var ws = new RecordingWebSocket();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpWebSocketFeature>(new FakeWebSocketFeature(ws));

        await handler.HandleAsync(ctx);

        Assert.Single(ws.CloseCalls);
        Assert.Equal((WebSocketCloseStatus)4004, ws.CloseCalls[0].Status);
        Assert.Equal("No active session", ws.CloseCalls[0].Description);
    }

    [Fact]
    public async Task RunConnection_forwards_published_turns_then_closes_normal()
    {
        var (session, io, hub) = CreateSessionAndIo();
        var sub = new WsSubscription(session, hub.Subscribe(), hub);
        var ws = new RecordingWebSocket();
        var task = WsRelay.RunConnectionAsync(ws, sub);

        io.WriteLine(TestTurnFactory.Wire("turn-1"));
        await WaitUntilAsync(() => ws.SentFrames.Count > 0, TimeSpan.FromSeconds(5));
        Assert.Equal("turn-1", ws.SentFrames[0]);

        // session 结束：IO.Close → hub.Complete → reader 完成 → 发送循环退出 → 收尾
        io.Close();
        await task;

        Assert.Contains(ws.CloseCalls, c => c.Status == WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task RunConnection_completes_when_subscriber_unsubscribed()
    {
        var (_, _, hub) = CreateSessionAndIo();
        var reader = hub.Subscribe();
        var sub = new WsSubscription(SessionFixture(), reader, hub);
        var ws = new RecordingWebSocket();
        var task = WsRelay.RunConnectionAsync(ws, sub);

        hub.Unsubscribe(reader); // 完成 reader → 发送循环退出
        await task;

        Assert.Contains(ws.CloseCalls, c => c.Status == WebSocketCloseStatus.NormalClosure);
    }

    [Fact]
    public async Task RunConnection_enters_receive_loop_before_forwarding()
    {
        var (session, io, hub) = CreateSessionAndIo();
        var sub = new WsSubscription(session, hub.Subscribe(), hub);
        var ws = new RecordingWebSocket();
        var task = WsRelay.RunConnectionAsync(ws, sub);

        // 接收循环首次调用 ReceiveAsync 后置位——证明连接已建立并进入双循环
        await ws.FirstReceiveTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        io.Close();
        await task;
    }

    private static Session SessionFixture()
    {
        var (session, _, _) = CreateSessionAndIo();
        return session;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "condition not met within timeout");
    }

    /// <summary>录制型 WebSocket：记录 Send/Close 调用；ReceiveAsync 阻塞至取消（首次调用置位 FirstReceiveTcs）。</summary>
    private sealed class RecordingWebSocket : WebSocket
    {
        private volatile WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override string SubProtocol => string.Empty;

        /// <summary>已发送的文本帧（SendAsync 调用顺序）。</summary>
        public readonly List<string> SentFrames = new();
        /// <summary>CloseAsync 调用记录。</summary>
        public readonly List<(WebSocketCloseStatus Status, string Description)> CloseCalls = new();
        /// <summary>首次调用 ReceiveAsync 时置位——用于断言"接收循环已启动"。</summary>
        public TaskCompletionSource FirstReceiveTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            lock (SentFrames)
                SentFrames.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            FirstReceiveTcs.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 连接收尾时取消——正常退出
            }
            return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            lock (CloseCalls)
                CloseCalls.Add((closeStatus, statusDescription ?? string.Empty));
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }

    /// <summary>把 RecordingWebSocket 暴露为 IHttpWebSocketFeature，使 DefaultHttpContext.WebSockets 可用。</summary>
    private sealed class FakeWebSocketFeature : IHttpWebSocketFeature
    {
        private readonly WebSocket _ws;

        public FakeWebSocketFeature(WebSocket ws) => _ws = ws;

        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext? context) => Task.FromResult(_ws);
    }
}
