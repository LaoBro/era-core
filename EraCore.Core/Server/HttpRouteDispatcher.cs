using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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

            default:
                return HttpResult.Text(NotFoundBody, statusCode: 404);
        }
    }

    private HttpResult HandlePostInput(HttpRequestData request)
    {
        var input = Parse<HttpInput>(request.BodyText);
        if (input == null)
            return InvalidJsonInput();
        if (input.value == null)
            return MissingInputValue();
        return _protocol.PostInput(input.value, ResolveIdentity(request, input.token));
    }

    private HttpResult HandleLoadGame(HttpRequestData request)
    {
        var payload = Parse<LoadGameRequest>(request.BodyText);
        if (payload == null)
            return InvalidJsonLoadGame();
        if (string.IsNullOrWhiteSpace(payload.gameDir))
            return MissingGameDir();
        return _protocol.LoadGame(payload.gameDir, ResolveIdentity(request, payload.token));
    }

    private HttpResult HandleScanGame(HttpRequestData request)
    {
        var payload = Parse<ScanGameRequest>(request.BodyText);
        if (payload == null)
            return InvalidJsonScanGame();
        if (string.IsNullOrWhiteSpace(payload.rootDir))
            return MissingRootDir();
        return GameServerProtocol.ScanGameDir(payload.rootDir);
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

    /// <summary>POST /input body。</summary>
    internal sealed class HttpInput
    {
        public string? value { get; set; }
        public string? token { get; set; }
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