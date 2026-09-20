using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MinorShift.Emuera.Assets;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

/// <summary>
/// Kestrel 游戏服务器（issue 05「抽共享层 + 双宿主」重构后）——仅负责 Kestrel 构建、
/// 静态资源中间件与路由注册，端点业务逻辑统一委托给 <see cref="GameServerProtocol"/>
/// （Core 共享层，与 MAUI 的 HttpListener 宿主复用同一份回合协议与控制状态机）。
///
/// 职责边界：
/// - 本类保留 ASP.NET Core 传输专属部分：路由注册、body 解析（ServerJsonContext 源生成）、
///   token 读取、HttpResult→IResult 映射、静态资源（wwwroot / /assets）中间件。
/// - 协议语义（session/turn/input/snapshot/state/control 事件）在 <see cref="GameServerProtocol"/> 单点实现。
///
/// 对外 HTTP/WS 契约与 ServerRunner 调用序列保持不变（wire 逐字节一致）。
/// </summary>
internal sealed class KestrelGameServer : IDisposable
{
    private readonly WebApplication _app;
    private readonly GameConfigService _config;
    private readonly SessionRegistry _sessions;
    private readonly GameServerProtocol _protocol;
    private readonly HttpRouteDispatcher _dispatcher;
    private readonly WsConnectionHandler _ws;

    /// <summary>测试接缝：端点测试经此访问当前 session（如注入 turn）。</summary>
    internal SessionRegistry Sessions => _sessions;

    public KestrelGameServer(int port, ITerminalSetup terminalSetup, ConfigData configData, bool? displayReportOverride = null, AppSettings? appSettings = null)
    {
        _config = new GameConfigService(configData, displayReportOverride, appSettings);
        _sessions = new SessionRegistry(terminalSetup, _config, GameServerProtocol.ReadAgentLease());
        _protocol = new GameServerProtocol(_sessions, _config);
        _dispatcher = new HttpRouteDispatcher(_protocol, ServerJsonContext.Default);
        _ws = new WsConnectionHandler(_sessions);

        // issue 10：WebRootPath 显式指向 exe 所在目录的 wwwroot/——
        // WebApplication.CreateBuilder() 默认用 Directory.GetCurrentDirectory()/wwwroot，
        // 工作目录错位（如从仓库根启动 exe）会导致静态文件 404。
        // 必须用 WebApplicationOptions 在 builder 构造前指定 WebRootPath——
        // builder 创建后 builder.WebHost.UseWebRoot() 会抛 NotSupportedException
        // ("The web root changed ... Use WebApplication.CreateBuilder(WebApplicationOptions) instead")。
        // AppContext.BaseDirectory 在 PublishSingleFile=true 下返回 exe 所在目录
        // （.NET 8+ 单文件部署不再解压到临时目录），保证 wwwroot 跟随 exe 分发。
        var options = new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls($"http://localhost:{port}");
        builder.Logging.ClearProviders();
        // 3.3（NativeAOT）：minimal API 响应序列化走 HttpJsonOptions 的 resolver chain，
        // AOT 下默认反射 resolver 被禁用——把 Server 的源生成上下文插到链首，
        // 否则 JsonResult(JsonObject) 抛 JsonTypeInfo metadata not provided（全端点 500）。
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonContext.Default));
        _app = builder.Build();

        // P0-2 为 WebSocket 铺路：启用 WS 中间件（仅启用，未实现端点前无副作用）。
        _app.UseWebSockets();

        // issue 10：静态资源服务——server 模式下 Kestrel 直接提供 Vue 构建产物（wwwroot/）。
        // 顺序必须 UseDefaultFiles → UseStaticFiles：前者把 `/` 重写为 `/index.html`，
        // 后者从 wwwroot 提供文件。二者必须在 MapRoutes 之前注册，否则默认文件请求不会被拦截。
        // wwwroot/ 由 csproj BuildVueFrontend pre-build target 从 ../EraCore.Web/dist/ 复制填充。
        _app.UseDefaultFiles();
        _app.UseStaticFiles();

        MapRoutes();
    }

    private void MapRoutes()
    {
        // JSON API 中触及 body/token 的端点统一经共享 HttpRouteDispatcher（C1）——
        // 路由匹配 + body 解析 + 校验错误映射 + 身份解析单点，宿主只透传 body/token。
        _app.MapPost("/control/acquire", (Delegate)HandleAcquireControlAsync);
        _app.MapPost("/control/release", (Delegate)HandleReleaseControlAsync);
        _app.MapGet("/turn", (Delegate)HandleGetTurnAsync);
        _app.MapPost("/input", (Delegate)HandlePostInputAsync);
        _app.MapDelete("/session", (Delegate)HandleDeleteSessionForHttpAsync);
        _app.MapPost("/load-game", (Delegate)HandleLoadGameAsync);
        // Web 模式游戏选择（issue 05 后续）：/game/scan 扫主目录下游戏，/game/dirs 目录浏览
        _app.MapPost("/game/scan", (Delegate)HandleScanGameAsync);
        _app.MapPost("/game/dirs", (Delegate)HandleListDirsAsync);
        // 无 body/token 的纯状态端点直连协议（本就没重复胶水，保持薄线程）。
        _app.MapPost("/session", (Delegate)HandleCreateSessionAsync);
        _app.MapGet("/control", (Delegate)HandleGetControlAsync);
        _app.MapGet("/control/wait", (Delegate)HandleWaitForControlAsync);
        _app.MapGet("/state", (Delegate)HandleGetStateAsync);
        _app.MapGet("/config", (Delegate)HandleGetConfigAsync);
        _app.MapGet("/snapshot", (Delegate)HandleGetSnapshotAsync);
        // ADR-0030：全局设置端点（GET/PUT/DELETE 均经共享 dispatcher——PUT 触及 body 解析/校验）。
        _app.MapGet("/settings", (Delegate)HandleGetSettingsAsync);
        _app.MapPut("/settings", (Delegate)HandlePutSettingsAsync);
        _app.MapDelete("/settings", (Delegate)HandleDeleteSettingsAsync);
        // issue 05：游戏选择器端点——/load-game 重载游戏目录，/native/pick-directory 安卓 SAF 桩
        _app.MapPost("/native/pick-directory", (Delegate)HandlePickDirectoryAsync);
        // 传输专属端点：WS / 静态资源中间件 / /assets 留宿主侧（不进 dispatcher）。
        _app.MapGet("/ws", (Delegate)_ws.HandleAsync);
        // issue 03：游戏目录图片资源通道（spec Q5 安全决策）——路径消毒 + 扩展名白名单 + 缓存/CORS 头
        _app.MapGet("/assets/{**path}", (Delegate)HandleGetAssetAsync);
    }

    public async Task StartAsync()
    {
        await _app.StartAsync();
        EmueraLog.Info("server", "Kestrel 监听已启动");
    }

    public async Task WaitForShutdownAsync()
    {
        await _app.WaitForShutdownAsync();
    }

    // ===== 端点委托（协议语义见 GameServerProtocol；此处只做 body/token 解析 + HttpResult 映射）=====

    /// <summary>
    /// POST /session —— 建会话。协议语义（503/409/201）见 <see cref="GameServerProtocol.CreateSession"/>。
    /// </summary>
    internal Task<IResult> HandleCreateSessionAsync()
    {
        return Task.FromResult(ToIResult(_protocol.CreateSession()));
    }

    /// <summary>POST /control/acquire —— 获取控制权（协议语义见 AcquireControl；解析/身份在 dispatcher）。</summary>
    internal Task<IResult> HandleAcquireControlAsync(HttpContext context) => DispatchApiAsync("POST", "/control/acquire", context);

    /// <summary>POST /control/release —— 释放控制权（协议语义见 ReleaseControl）。</summary>
    internal Task<IResult> HandleReleaseControlAsync(HttpContext context) => DispatchApiAsync("POST", "/control/release", context);

    /// <summary>GET /control —— 当前控制权状态（协议语义见 GetControl）。</summary>
    internal IResult HandleGetControlAsync()
    {
        return ToIResult(_protocol.GetControl());
    }

    /// <summary>GET /control/wait —— 长轮询等控制事件（协议语义见 WaitForControlAsync）。</summary>
    internal async Task<IResult> HandleWaitForControlAsync(HttpContext context)
    {
        return ToIResult(await _protocol.WaitForControlAsync(context.RequestAborted));
    }

    /// <summary>
    /// GET /turn —— 长轮询等一个 turn（协议语义见 <see cref="GameServerProtocol.GetTurnAsync"/>：
    /// 200 裸 JSON / 204 超时 / 404 无 session 或已结束 / 409 控制权丢失；身份解析在 dispatcher）。
    /// </summary>
    internal Task<IResult> HandleGetTurnAsync(HttpContext context) => DispatchApiAsync("GET", "/turn", context);

    /// <summary>POST /input —— 提交输入（协议语义见 PostInput；body 解析/校验/身份在 dispatcher）。</summary>
    internal Task<IResult> HandlePostInputAsync(HttpContext context) => DispatchApiAsync("POST", "/input", context);

    /// <summary>
    /// GET /state —— 当前会话状态 + gameDir + 窗口布局元信息（协议语义见 GetState）。
    /// 前端 App.vue 挂载时调用：判断 server 是否空闲（gameDir==null / state=="Idle"），
    /// 空闲则展示选择器并预填 localStorage 上次目录（T-025 D9 rev，不再自动 loadGame）。
    /// </summary>
    internal IResult HandleGetStateAsync()
    {
        return ToIResult(_protocol.GetState());
    }

    /// <summary>GET /config —— 当前 maxLog（前端履历容量，协议语义见 GetConfig）。</summary>
    internal IResult HandleGetConfigAsync()
    {
        return ToIResult(_protocol.GetConfig());
    }

    /// <summary>GET /settings —— 全局设置全量（ADR-0030；无 body，仍经 dispatcher 保持单点路由）。</summary>
    internal Task<IResult> HandleGetSettingsAsync(HttpContext context) => DispatchApiAsync("GET", "/settings", context);

    /// <summary>PUT /settings —— 批量设置全局设置（body 解析/校验在 dispatcher，协议语义见 PutSettings）。</summary>
    internal Task<IResult> HandlePutSettingsAsync(HttpContext context) => DispatchApiAsync("PUT", "/settings", context);

    /// <summary>DELETE /settings —— 恢复默认（ADR-0030，协议语义见 ResetSettings）。</summary>
    internal Task<IResult> HandleDeleteSettingsAsync(HttpContext context) => DispatchApiAsync("DELETE", "/settings", context);

    /// <summary>GET /snapshot —— 全量显示状态快照（协议语义见 GetSnapshot：404/503/200）。</summary>
    internal IResult HandleGetSnapshotAsync()
    {
        return ToIResult(_protocol.GetSnapshot());
    }

    /// <summary>DELETE /session —— 无身份销毁（测试接缝/内部用，协议语义见 DeleteSession(null)）。</summary>
    internal Task<IResult> HandleDeleteSessionAsync()
    {
        return HandleDeleteSessionCoreAsync(null);
    }

    /// <summary>DELETE /session（HTTP）—— 身份/token 解析在 dispatcher。</summary>
    internal Task<IResult> HandleDeleteSessionForHttpAsync(HttpContext context) => DispatchApiAsync("DELETE", "/session", context);

    /// <summary>DELETE /session —— 无身份销毁（测试接缝/内部用，协议语义见 DeleteSession(null)）。</summary>
    private Task<IResult> HandleDeleteSessionCoreAsync(ControlIdentity? identity)
    {
        return Task.FromResult(ToIResult(_protocol.DeleteSession(identity)));
    }

    /// <summary>
    /// POST /load-game {gameDir} —— 原子重载游戏目录（协议语义见 <see cref="GameServerProtocol.LoadGame"/>：
    /// 400 {error:{code,message}} 路径/格式错误、500 LOAD_FAILED、成功 200 {sessionId,state,gameDir}；
    /// body 解析/校验/身份在 dispatcher）。路径校验/拆旧/重建 ConfigData/建新 Session 的原子序列
    /// 见 <see cref="SessionRegistry.ReplaceForLoadGameAsync"/>。
    /// </summary>
    internal Task<IResult> HandleLoadGameAsync(HttpContext context) => DispatchApiAsync("POST", "/load-game", context);

    /// <summary>POST /game/scan —— 扫主目录下游戏（协议语义见 GameServerProtocol.ScanGameDir；body 解析在 dispatcher）。</summary>
    internal Task<IResult> HandleScanGameAsync(HttpContext context) => DispatchApiAsync("POST", "/game/scan", context);

    /// <summary>POST /game/dirs —— 目录浏览（协议语义见 GameServerProtocol.ListDirectories；body 解析在 dispatcher）。</summary>
    internal Task<IResult> HandleListDirsAsync(HttpContext context) => DispatchApiAsync("POST", "/game/dirs", context);

    /// <summary>POST /native/pick-directory —— 安卓 SAF 目录选择器桩（协议语义见 PickDirectory）。</summary>
    private IResult HandlePickDirectoryAsync()
    {
        return ToIResult(GameServerProtocol.PickDirectory());
    }

    // ===== 传输专属辅助：body / token / HttpResult 映射 =====

    /// <summary>
    /// 统一 API 转发（C1）：用端点已知的 method+path + 从 context 读 body/token 构造传输无关
    /// <see cref="HttpRequestData"/> → 共享 <see cref="HttpRouteDispatcher"/>（路由/解析/校验/身份单点）
    /// → 映射 IResult。method+path 固定传入而非从 context 读，保证直连调用的测试接缝（空
    /// DefaultHttpContext）也稳定命中正确路由。
    /// </summary>
    private async Task<IResult> DispatchApiAsync(string method, string path, HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        return ToIResult(await _dispatcher.DispatchAsync(new HttpRequestData(method, path, body, ReadToken(context)), context.RequestAborted));
    }

    /// <summary>读请求体为字符串（无体/空体返回空串；读失败回退空串——与 HttpListener TryReadBody 同语义）。</summary>
    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            return await reader.ReadToEndAsync() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string? ReadToken(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Control-Token", out var headerToken))
            return headerToken.ToString();
        if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            var value = authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return value[7..];
        }
        return context.Request.Query["token"].ToString();
    }

    /// <summary>HttpResult（共享层统一响应）→ ASP.NET Core IResult。204 走 NoContent，其余写文本 body。</summary>
    private static IResult ToIResult(HttpResult result)
    {
        if (result.StatusCode == 204)
            return Results.NoContent();
        return Results.Text(result.Body!, result.ContentType, Encoding.UTF8, result.StatusCode);
    }

    /// <summary>
    /// GET /assets/{path} —— 游戏目录图片资源通道（issue 03，spec Q3 路线 B / Q5 安全）。
    ///
    /// 前端拿协议里的相对路径（如 <c>img/portrait.png</c>）拼 <c>/assets/</c> URL 即得；
    /// 浏览器原生缓存（Cache-Control）让状态屏每回合重印同一画像零流量。
    ///
    /// 安全规则（spec L116-118）：只服务图片扩展名白名单；路径消毒拒绝穿越（统一走
    /// <see cref="AssetPathValidator"/>，与 05 安卓 PathHandler 共用同一函数）；CORS 头
    /// 允许 canvas 读像素算 srcm 映射色。任何失败一律 404（不泄露资源是否存在）。
    /// 注：静态资源通道是传输专属（Kestrel 的 wwwroot + /assets），MAUI 双平台自行拦截/PathHandler。
    /// </summary>
    internal async Task HandleGetAssetAsync(HttpContext context, string path)
    {
        // issue 05：四步（消毒/白名单/读字节/MIME）收敛到 AssetChannel——与 MAUI 双平台
        // （Windows 拦截 + 安卓 PathHandler）共用同一函数，spec Q5「单点实现」真正成立。
        var paths = GamePaths.Current;
        if (paths?.DirAccessor == null
            || !AssetChannel.TryGetImage(paths.DirAccessor, paths.ExeDir, path, out var bytes, out var mime))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = mime;
        // 头策略与 MAUI 双平台共用（AssetChannel 常量单点）——改缓存/CORS 只动 Core
        context.Response.Headers.CacheControl = AssetChannel.CacheControlHeader;
        context.Response.Headers.AccessControlAllowOrigin = AssetChannel.CorsAllowOriginHeader;
        await context.Response.Body.WriteAsync(bytes);
    }

    // HTTP body POCO（HttpInput/ControlRequest/LoadGameRequest）已上收共享层
    // HttpRouteDispatcher 嵌套类型（双宿主去重），by dispatcher 解析；本宿主把
    // ServerJsonContext.Default 注入 dispatcher 作源生成反序列化（NativeAOT + 大小写不敏感）。

    public void Dispose()
    {
        // 先销毁 session（join 游戏循环，scope 释放安全），再释放 Kestrel
        _sessions.DisposeAll();
        ((IDisposable)_app).Dispose();
    }
}
