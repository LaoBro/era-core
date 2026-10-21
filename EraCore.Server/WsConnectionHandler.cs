using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using MinorShift.Emuera.Sessions;

namespace MinorShift.Emuera.Server;

/// <summary>
/// GET /ws —— WebSocket 旁路传输端点（Kestrel 宿主壳，issue 05「双宿主」）。
/// 仅保留 ASP.NET Core 相关部分（升级判定、身份解析）；传输无关的连接循环已抽到
/// <see cref="WsRelay"/>（Core，BCL only），MAUI 的 HttpListener 宿主复用同一实现。
///
/// 生命周期：
/// - 无活跃 session 时接受升级后立即以下发关闭码 4004 退回。
/// - 有 session：SessionRegistry.TryGetWsSubscriptionAsync 锁内完成 accept→re-check→subscribe
///   原子序列，避免 session 被另一线程 DELETE+POST 替换后仍从旧 hub 订阅。
/// - 任一侧结束 / WS 关闭 → WsRelay 取消另一循环并退订；session 结束（hub.Complete）以 WS 关闭帧通知。
/// </summary>
internal sealed class WsConnectionHandler
{
    private readonly SessionRegistry _sessions;

    public WsConnectionHandler(SessionRegistry sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>路由委托入口（迁移自原 HandleWebSocketAsync）。</summary>
    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        using var ws = await context.WebSockets.AcceptWebSocketAsync();

        // 锁内 accept→re-check→subscribe 原子序列由 SessionRegistry 保证
        var subscription = await _sessions.TryGetWsSubscriptionAsync(ReadIdentity(context));
        if (subscription == null)
        {
            await ws.CloseAsync(WsRelay.WsCloseNoActiveSession, "No active session", CancellationToken.None);
            return;
        }

        await WsRelay.RunConnectionAsync(ws, subscription);
    }

    private static ControlIdentity ReadIdentity(HttpContext context)
    {
        string? token = context.Request.Query["token"].ToString();
        if (context.Request.Headers.TryGetValue("X-Control-Token", out var headerToken))
            token = headerToken.ToString();
        if (string.IsNullOrWhiteSpace(token) && context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = value[7..];
        }
        return string.IsNullOrWhiteSpace(token)
            ? ControlIdentity.User
            : ControlIdentity.Agent(token);
    }
}
