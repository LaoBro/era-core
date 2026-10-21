using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Sessions;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

/// <summary>
/// MAUI 托管宿主（issue 05「抽共享层 + 双宿主」）——用 BCL 自带的 <c>System.Net.HttpListener</c>
/// 起 HTTP + WebSocket，复用 <see cref="GameServerProtocol"/>（回合协议 + 控制状态机）与
/// <see cref="WsRelay"/>（WS 旁路循环），**不引入 ASP.NET Core**（保住「MAUI 只依赖 Core」）。
///
/// 与 <see cref="KestrelGameServer"/> 是对称的宿主壳：
/// - 本类只做传输专属部分：HttpListener 构建/启动/accept 循环、路由 (method, path)→协议方法、
///   body 解析（POCO + System.Text.Json）、token 读取、HttpResult→HttpListenerResponse 写入、WS 升级。
/// - 协议语义单点在 <see cref="GameServerProtocol"/>，两宿主的 wire 契约逐字节一致。
///
/// 端口：<paramref name="port"/> == 0 时自动挑选空闲端口（MAUI 场景用），经 <see cref="Port"/> 暴露
/// 供 agent 发现（issue 05 agent 发现机制）。
///
/// 注：本宿主不提供前端静态文件服务（Vue dist 由 MAUI WebView 侧处理/后续阶段决定同源策略）；
/// API + WS 端点与 Kestrel 完全对齐。
/// </summary>
internal sealed class HttpListenerHost : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly GameConfigService _config;
    private readonly SessionRegistry _sessions;
    private readonly GameServerProtocol _protocol;
    private readonly HttpRouteDispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private bool _disposed;

    /// <summary>实际绑定端口（port==0 时为自动挑选的空闲端口）——agent 发现机制读取。</summary>
    public int Port { get; }

    /// <summary>测试接缝：会话注册表（供测试经此访问当前 session）。</summary>
    internal SessionRegistry Sessions => _sessions;

    /// <param name="displayReportOverride">启动日志覆盖三态（null=跟随配置）。</param>
    /// <param name="scanGameDirHandler">
    /// 宿主可选的 <c>POST /game/scan</c> 实现（MAUI 注入带 SAF accessor 的扫描；
    /// <c>null</c> = 走默认 <see cref="GameServerProtocol.ScanGameDir"/>）。见 <see cref="HttpRouteDispatcher"/> 同名委托。
    /// 参数可为 <c>null</c> = 「扫宿主自己的主目录」（控制面因而不必持有会过期的路径）。
    /// </param>
    /// <param name="loadGameHandler">
    /// 宿主可选的 <c>POST /load-game</c> 实现（MAUI 注入转交 <c>MainPage.OnReloadGame</c> 的实现；
    /// <c>null</c> = 走默认 <see cref="GameServerProtocol.LoadGame"/>）。见 <see cref="HttpRouteDispatcher"/> 同名委托。
    /// </param>
    /// <param name="appSettings">全局设置服务（ADR-0030）——<c>null</c> 时用空内存 store（测试/占位路径）。</param>
    public HttpListenerHost(
        int port,
        ITerminalSetup terminalSetup,
        ConfigData configData,
        bool? displayReportOverride = null,
        Func<string?, HttpResult?>? scanGameDirHandler = null,
        Func<string, ControlIdentity?, HttpResult?>? loadGameHandler = null,
        AppSettings? appSettings = null)
    {
        _config = new GameConfigService(configData, displayReportOverride, appSettings);
        _sessions = new SessionRegistry(terminalSetup, _config, GameServerProtocol.ReadAgentLease());
        _protocol = new GameServerProtocol(_sessions, _config);
        _dispatcher = new HttpRouteDispatcher(_protocol, ServerJsonContext.Default, scanGameDirHandler, loadGameHandler);

        if (port == 0)
            port = FindFreePort();
        Port = port;

        // Windows http.sys：localhost 前缀对当前用户自动注册 URL ACL，无需管理员。
        // 同时显式注册 127.0.0.1 前缀——发现记录 host 写 127.0.0.1，agent 以该地址探活；
        // 虽 http.sys 对 localhost 前缀通常也接受 loopback IP，显式注册保证两种 host 都可达。
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening)
            {
                // 宿主关闭（Stop/Dispose）→ GetContextAsync 抛错退出
                break;
            }
            catch (HttpListenerException)
            {
                // 未知监听错误——避免单次错误终止 accept 循环
                continue;
            }

            // 每个请求独立 Task：长轮询（/turn /control/wait）与 WS 不阻塞 accept 循环
            _ = Task.Run(() => DispatchAsync(context, _cts.Token));
        }
    }

    internal async Task DispatchAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        // WebSocket 升级：不走 HttpResult 路径，Response 由升级接管，不能 Close
        if (request.Url?.AbsolutePath == "/ws")
        {
            await HandleWsAsync(context, ct);
            return;
        }

        try
        {
            var result = await DispatchHttpAsync(request, ct);
            await WriteResultAsync(response, result);
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭，长轮询被取消——正常路径
        }
        catch (HttpListenerException)
        {
            // 客户端断开 / 宿主关闭
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // 关闭中出现的任何异常一律吞掉
        }
        catch (Exception ex)
        {
            // 未预期错误：尽力回 500
            try
            {
                var body = new JsonObject { ["error"] = "INTERNAL_ERROR", ["code"] = "INTERNAL_ERROR", ["message"] = ex.Message }
                    .ToJsonString(GameServerProtocol.JsonOptions);
                await WriteResultAsync(response, new HttpResult(500, body));
            }
            catch
            {
                // 忽略——连接可能已断开
            }
        }
        finally
        {
            try { response.Close(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>HTTP 路由 → 协议方法（与 Kestrel MapRoutes 一一对应）。</summary>
    private async Task<HttpResult> DispatchHttpAsync(HttpListenerRequest request, CancellationToken ct)
    {
        var path = request.Url?.AbsolutePath ?? "/";
        var method = request.HttpMethod;

        switch ((method, path))
        {
            // 无 body/token 的纯状态端点直连协议（本就没重复胶水）。
            case ("POST", "/session"):
                return _protocol.CreateSession();
            case ("GET", "/control"):
                return _protocol.GetControl();
            case ("GET", "/control/wait"):
                return await _protocol.WaitForControlAsync(ct);
            case ("GET", "/state"):
                return _protocol.GetState();
            case ("GET", "/config"):
                return _protocol.GetConfig();
            // ADR-0030：全局设置端点——GET/DELETE 无 body 直连协议，PUT 触及 body 走共享 dispatcher。
            case ("GET", "/settings"):
                return _protocol.GetSettings();
            case ("PUT", "/settings"):
                return await _dispatcher.DispatchAsync(BuildRequestData(request), ct);
            case ("DELETE", "/settings"):
                return _protocol.ResetSettings();
            case ("GET", "/snapshot"):
                return _protocol.GetSnapshot();
            case ("POST", "/native/pick-directory"):
                return GameServerProtocol.PickDirectory();
            // 触及 body/token 的端点 + 未知路径统一交共享 HttpRouteDispatcher（C1，单点路由/解析/校验/身份）。
            default:
                return await _dispatcher.DispatchAsync(BuildRequestData(request), ct);
        }
    }

    /// <summary>构造传输无关 <see cref="HttpRequestData"/>（读 body + 抽 header/query token）。</summary>
    private static HttpRequestData BuildRequestData(HttpListenerRequest request)
    {
        _ = TryReadBody(request, out var body);
        return new HttpRequestData(request.HttpMethod, request.Url?.AbsolutePath ?? "/", body, ReadToken(request));
    }

    private async Task HandleWsAsync(HttpListenerContext context, CancellationToken ct)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            // 非 WS 请求打到 /ws：400 并关闭 response——本方法不走 DispatchAsync 的 finally，
            // 必须自行 Close 避免挂起连接。
            context.Response.StatusCode = 400;
            try { context.Response.Close(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            return;
        }

        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception)
        {
            // 升级被拒（缺 Upgrade 头 / 协议不支持）：400 并关闭 response
            context.Response.StatusCode = 400;
            try { context.Response.Close(); }
            catch (HttpListenerException) { }
            catch (ObjectDisposedException) { }
            return;
        }

        using var ws = wsContext.WebSocket;

        // 锁内 accept→re-check→subscribe 原子序列由 SessionRegistry 保证
        var subscription = await _sessions.TryGetWsSubscriptionAsync(ReadIdentity(context.Request));
        if (subscription == null)
        {
            await ws.CloseAsync(WsRelay.WsCloseNoActiveSession, "No active session", CancellationToken.None);
            return;
        }

        await WsRelay.RunConnectionAsync(ws, subscription);
    }

    private static async Task WriteResultAsync(HttpListenerResponse response, HttpResult result)
    {
        response.StatusCode = result.StatusCode;
        if (result.StatusCode == 204 || result.Body == null)
            return;
        response.ContentType = result.ContentType;
        var bytes = Encoding.UTF8.GetBytes(result.Body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    /// <summary>读请求体为字符串；失败（无体/IO 错）返回 false。</summary>
    private static bool TryReadBody(HttpListenerRequest request, out string body)
    {
        body = string.Empty;
        try
        {
            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            body = reader.ReadToEnd();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>token → 身份：header（X-Control-Token / Authorization: Bearer）→ query（与 Kestrel ReadToken 同源）。</summary>
    private static ControlIdentity ReadIdentity(HttpListenerRequest request)
    {
        return GameServerProtocol.ToIdentity(ReadToken(request));
    }

    private static string? ReadToken(HttpListenerRequest request)
    {
        var header = request.Headers["X-Control-Token"];
        if (!string.IsNullOrWhiteSpace(header))
            return header;

        var authorization = request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization[7..];

        return request.QueryString["token"];
    }

    /// <summary>挑选空闲端口（port==0 时）：临时占一个 loopback 端口后释放，交给 HttpListener 绑定。</summary>
    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // 响应 JSON 序列化共用 GameServerProtocol.JsonOptions（双宿主去重）；
    // HTTP body POCO（HttpInput/LoadGameRequest/ControlRequest）位于共享层 HttpRouteDispatcher
    //（本宿主不注入源生成上下文，反射解析为其原行为）。

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _cts.Cancel();
        try { _listener.Stop(); } catch (HttpListenerException) { }
        try { _listener.Close(); } catch (HttpListenerException) { }
        _sessions.DisposeAll();
    }
}
