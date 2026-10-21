using MinorShift.Emuera.Files;
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 传输无关的请求数据（C1 深化 seam 的最小面）——宿主做平台 IO 后产出，dispatcher 纯函数消费。
/// <list type="bullet">
/// <item><c>Method</c> / <c>Path</c>：路由匹配键。</item>
/// <item><c>BodyText</c>：原始 body 文本（无 body / GET 时为 null）。解析与校验由 dispatcher 负责。</item>
/// <item><c>TokenFromHeaderOrQuery</c>：宿主从 header（X-Control-Token / Authorization: Bearer）/ query 抽出的 token；
/// body 内 token 优先于它（见 D4 回退语义）。</item>
/// </list>
/// </summary>
internal readonly record struct HttpRequestData(
    string Method,
    string Path,
    string? BodyText = null,
    string? TokenFromHeaderOrQuery = null);

/// <summary>
/// 双宿主共用的 HTTP 请求整形器（C1）。吞掉「需要 body/token 的端点路由 + body 解析 +
/// 校验错误映射 + token/身份解析」，Kestrel 与 <see cref="HttpListenerHost"/> 退化为纯传输 adapter。
/// wire 语义与原两宿主逐字节一致（行为零漂移）。
///
/// 本次只收编「触及 body 或 token」的端点（/control/acquire、/control/release、/turn、
/// /input、DELETE /session、/load-game、/game/scan、/game/dirs）——它们正是原两宿主重复胶水（解析/校验/身份）所在；
/// 无 body/token 的纯 GET/状态端点（/session、/control、/control/wait、/state、/config、/snapshot、
/// /native/pick-directory）本就没重复胶水，仍由各宿主直连 <see cref="GameServerProtocol"/>。
/// WS 升级 / 静态资源 / /assets 为传输专属，留宿主侧。
/// </summary>
internal sealed class HttpRouteDispatcher
{
    private const string NotFoundBody = """{"error":"Not found","code":"NOT_FOUND"}""";

    private readonly GameServerProtocol _protocol;
    /// <summary>
    /// 请求 body 反序列化的源生成上下文。双宿主均注入 <c>ServerJsonContext.Default</c>（Core 共享）：
    /// 经 <c>JsonSerializer.Deserialize(string, Type, context)</c> 源生成重载反序列化，NativeAOT 安全
    /// （无 IL2026/IL3050）且大小写不敏感。默认构造用 <c>ServerJsonContext.Default</c>，使
    /// HttpListener / 测试与 Kestrel 走同一路径。
    /// </summary>
    private readonly JsonSerializerContext _requestContext;

    /// <summary>
    /// 宿主可选的 <c>POST /game/scan</c> 实现——<c>(rootDir) =&gt; HttpResult?</c>。
    /// <para>
    /// 为何需要：默认 <see cref="GameServerProtocol.ScanGameDir"/> 硬编码
    /// <c>FileSystemGameDirAccessor</c>，Android 的 SAF <c>content://</c> 树 URI 无法枚举。
    /// MAUI 宿主注入自己的实现（按路径选 SAF / 文件系统 accessor）后此处优先。
    /// </para>
    /// <para>
    /// <b><c>rootDir</c> 可为 <c>null</c></b>（issues/15 后续）：表示"扫你自己的主目录"。
    /// 控制面（eracore_agent）因此不必持有路径——它手上的路径会随权限/主目录变更而过期，
    /// 而宿主取用自己当前生效的主目录永远是新的。无宿主时不放行 null（见
    /// <see cref="HandleScanGame"/>）：Web 前端与独立 Server/CLI 一直传显式路径。
    /// </para>
    /// <para>返回 <c>null</c> 表示"本次不接管"，回落到默认实现——宿主可据此做条件接管。</para>
    /// </summary>
    private readonly Func<string?, HttpResult?>? _scanGameDirHandler;

    /// <summary>
    /// 宿主可选的 <c>POST /load-game</c> 实现——<c>(gameDir, identity) =&gt; HttpResult?</c>。
    /// <para>
    /// 为何需要：默认 <see cref="GameServerProtocol.LoadGame"/> 走
    /// <c>SessionRegistry.ReplaceForLoadGameAsync</c>，它会（a）硬编码文件系统 accessor、
    /// （b）绕过 MAUI 运行期初始化与性能覆盖旋钮、（c）新建 <c>OutputHub</c> 使 WebView 侧
    /// turn 订阅失效。MAUI 宿主注入实现把请求转交 <c>MainPage.OnReloadGame</c>，走同一条手工链路。
    /// </para>
    /// <para>返回 <c>null</c> 表示"本次不接管"，回落到默认实现。</para>
    /// </summary>
    private readonly Func<string, ControlIdentity?, HttpResult?>? _loadGameHandler;

    public HttpRouteDispatcher(GameServerProtocol protocol)
        : this(protocol, ServerJsonContext.Default)
    {
    }

    public HttpRouteDispatcher(GameServerProtocol protocol, JsonSerializerContext requestJsonContext)
    {
        _protocol = protocol;
        _requestContext = requestJsonContext;
    }

    /// <summary>
    /// 宿主回调版构造——注入 <c>/game/scan</c> / <c>/load-game</c> 的宿主实现（见两个委托的文档）。
    /// 两委托均可为 <c>null</c>（此时行为与前两个构造逐字节一致——Core 层零漂移）。
    /// </summary>
    public HttpRouteDispatcher(
        GameServerProtocol protocol,
        JsonSerializerContext requestJsonContext,
        Func<string?, HttpResult?>? scanGameDirHandler,
        Func<string, ControlIdentity?, HttpResult?>? loadGameHandler)
    {
        _protocol = protocol;
        _requestContext = requestJsonContext;
        _scanGameDirHandler = scanGameDirHandler;
        _loadGameHandler = loadGameHandler;
    }

    /// <summary>
    /// 分发一个需要 body/token 整形的请求。未知路径返回 404（与原 HttpListener switch 的 default 一致）。
    /// </summary>
    public async Task<HttpResult> DispatchAsync(HttpRequestData request, CancellationToken ct)
    {
        switch ((request.Method, request.Path))
        {
            case ("POST", "/control/acquire"):
                return _protocol.AcquireControl(ResolveIdentity(request, Parse<ControlRequest>(request.BodyText)?.token));

            case ("POST", "/control/release"):
                return _protocol.ReleaseControl(ResolveIdentity(request, Parse<ControlRequest>(request.BodyText)?.token));

            case ("GET", "/turn"):
                return await _protocol.GetTurnAsync(ResolveIdentity(request), ct);

            case ("POST", "/input"):
                return HandlePostInput(request);

            case ("DELETE", "/session"):
                return _protocol.DeleteSession(ResolveIdentity(request));

            case ("POST", "/load-game"):
                return HandleLoadGame(request);

            case ("POST", "/game/scan"):
                return HandleScanGame(request);

            case ("POST", "/game/dirs"):
                return HandleListDirs(request);

            // ADR-0030：全局设置端点——GET 全量 / PUT 批量 / DELETE reset。
            // 无认证（与 /config 同级，localhost；Web 前端 HTTP 模式无 token）。
            case ("GET", "/settings"):
                return _protocol.GetSettings();
            case ("PUT", "/settings"):
                return _protocol.PutSettings(request.BodyText);
            case ("DELETE", "/settings"):
                return _protocol.ResetSettings();

            default:
                return HttpResult.Text(NotFoundBody, statusCode: 404);
        }
    }

    private HttpResult HandlePostInput(HttpRequestData request)
    {
        var input = Parse<HttpInput>(request.BodyText);
        if (input == null)
            return InvalidJsonInput();

        // 结构化 body 与预构建命令帧进入 Core 后是同一个 InputFrame；
        // 字段映射的唯一实现在 InputFrame.FromWire（协议帧经 JsonlCommand.ToFrame 走同一处）。
        var frame = InputFrame.FromWire(
            input.value, input.hoverValue, input.keys, input.geometry, input.focus);

        // spec-v3 §5.3：状态类输入不带 value（只更新按键/悬停/坐标），同样是合法输入。
        if (!frame.HasValue && !frame.HasState)
            return MissingInputValue();

        return _protocol.PostInputFrame(frame, ResolveIdentity(request, input.token));
    }

    private HttpResult HandleLoadGame(HttpRequestData request)
    {
        var payload = Parse<LoadGameRequest>(request.BodyText);
        if (payload == null)
            return InvalidJsonLoadGame();
        if (string.IsNullOrWhiteSpace(payload.gameDir))
            return MissingGameDir();
        if (_loadGameHandler is { } loadHandler)
        {
            // 宿主接管（MAUI：转交 MainPage.OnReloadGame）——返回 null 表示不接管，走默认实现
            var hosted = loadHandler(payload.gameDir, ResolveIdentity(request, payload.token));
            if (hosted.HasValue)
                return hosted.Value;
        }
        return _protocol.LoadGame(payload.gameDir, ResolveIdentity(request, payload.token));
    }

    private HttpResult HandleScanGame(HttpRequestData request)
    {
        var payload = Parse<ScanGameRequest>(request.BodyText);
        if (payload == null)
            return InvalidJsonScanGame();

        var hasRootDir = !string.IsNullOrWhiteSpace(payload.rootDir);

        // 省略 rootDir = 「扫宿主自己的主目录」（issues/15 后续）。
        // 为何放开：控制面（eracore_agent）手上的路径会随权限/主目录变更而过期，而宿主取用
        // 自己当前生效的主目录永远是新的——路径根本不越过边界，就没有过期路径这回事。
        // 无宿主时仍要求显式路径（保持原 400）：Web 前端与独立 Server/CLI 一直传路径，契约不变。
        if (!hasRootDir && _scanGameDirHandler is null)
            return MissingRootDir();

        if (_scanGameDirHandler is { } scanHandler)
        {
            // 宿主接管（MAUI：带 SAF accessor 的扫描）——返回 null 表示不接管，走默认实现
            var hosted = scanHandler(payload.rootDir);
            if (hosted.HasValue)
                return hosted.Value;
        }

        // 到达此处 = 无宿主接管或宿主明确拒绝。空 rootDir 已无处可扫（默认实现硬编码文件系统
        // accessor，也没有"主目录"概念），如实回落原有的缺参错误。
        if (!hasRootDir)
            return MissingRootDir();

        return GameServerProtocol.ScanGameDir(payload.rootDir!);
    }

    private HttpResult HandleListDirs(HttpRequestData request)
    {
        var payload = Parse<ListDirsRequest>(request.BodyText);
        if (payload == null)
            return InvalidJsonListDirs();
        return GameServerProtocol.ListDirectories(payload.dir);
    }

    /// <summary>
    /// token/身份解析（D4，行为零漂移）：body.token 优先，空/坏/无 body token 回退 header/query。
    /// </summary>
    private static ControlIdentity ResolveIdentity(HttpRequestData request, string? bodyToken = null)
        => GameServerProtocol.ToIdentity(bodyToken ?? request.TokenFromHeaderOrQuery);

    /// <summary>body 解析：空体/坏 JSON → null（调用方按端点映射校验错误）。</summary>
    private T? Parse<T>(string? bodyText) where T : class
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return null;
        try
        {
            // 源生成重载（NativeAOT 安全 + 大小写不敏感）：双宿主均经 ServerJsonContext.Default。
            return (T?)JsonSerializer.Deserialize(bodyText, typeof(T), _requestContext);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ===== 校验助手（自 GameServerProtocol 移入，与解析/校验/身份同址）=====

    private static HttpResult InvalidJsonInput()
        => Json(new JsonObject { ["error"] = "Invalid JSON, expected {\"value\":\"...\"}" }, 400);

    private static HttpResult MissingInputValue()
        => Json(new JsonObject { ["error"] = "Missing 'value' field" }, 400);

    private static HttpResult InvalidJsonLoadGame()
        => Json(
            new JsonObject { ["error"] = new JsonObject { ["code"] = "INVALID_JSON", ["message"] = "Invalid JSON, expected {\"gameDir\":\"...\"}" } },
            400);

    private static HttpResult MissingGameDir()
        => Json(
            new JsonObject { ["error"] = new JsonObject { ["code"] = "MISSING_GAME_DIR", ["message"] = "Missing or empty 'gameDir' field" } },
            400);

    private static HttpResult InvalidJsonScanGame()
        => Json(
            new JsonObject { ["error"] = new JsonObject { ["code"] = "INVALID_JSON", ["message"] = "Invalid JSON, expected {\"rootDir\":\"...\"}" } },
            400);

    private static HttpResult MissingRootDir()
        => Json(
            new JsonObject { ["error"] = new JsonObject { ["code"] = "MISSING_ROOT_DIR", ["message"] = "Missing or empty 'rootDir' field" } },
            400);

    private static HttpResult InvalidJsonListDirs()
        => Json(
            new JsonObject { ["error"] = new JsonObject { ["code"] = "INVALID_JSON", ["message"] = "Invalid JSON, expected {\"dir\":\"...\"}" } },
            400);

    private static HttpResult Json(JsonObject payload, int statusCode = 200)
    {
        var body = payload.ToJsonString(GameServerProtocol.JsonOptions);
        return HttpResult.Text(body, "application/json", statusCode);
    }

    // ---- 请求 body POCO（自 GameServerProtocol 移入，与解析同址）----

    /// <summary>
    /// POST /input body。spec-v3 §5.1：<c>value</c> 之外的字段为可选状态捎带，
    /// 与 WS 上行帧（<c>JsonlCommand</c>）同源 —— 这里只是 HTTP 的入口形状。
    /// <para>
    /// 本类不提供 wire → 输入状态的转换：状态与提交值统一由 <see cref="InputFrame.FromWire"/>
    /// 构造，避免出现第二份字段映射。
    /// </para>
    /// </summary>
    internal sealed class HttpInput
    {
        public string? value { get; set; }
        public string? token { get; set; }
        public string? hoverValue { get; set; }
        public InputKeyEvent[]? keys { get; set; }
        public InputGeometry? geometry { get; set; }
        public bool? focus { get; set; }
    }

    /// <summary>POST /control/acquire|release body（token 可选）。</summary>
    internal sealed class ControlRequest
    {
        public string? token { get; set; }
    }

    /// <summary>POST /load-game body。</summary>
    internal sealed class LoadGameRequest
    {
        public string? gameDir { get; set; }
        public string? token { get; set; }
    }

    /// <summary>POST /game/scan body。</summary>
    internal sealed class ScanGameRequest
    {
        public string? rootDir { get; set; }
    }

    /// <summary>POST /game/dirs body。</summary>
    internal sealed class ListDirsRequest
    {
        public string? dir { get; set; }
    }
}