using System;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 传输无关的回合协议层（双宿主共享）——issue 05「抽共享层 + 双宿主」。
/// <para>
/// 从 <see cref="KestrelGameServer"/> 的端点处理器逐字迁移，行为与 wire 契约不变：
/// Server（Kestrel）与 MAUI（<see cref="HttpListenerHost"/>）都是本协议的宿主壳——
/// 宿主负责 HTTP/WS 传输（读 body、抽 token、写 response）并映射到本协议的方法；
/// 控制状态机（Controller/acquire/release/lease/control 事件）与回合协议（session/turn/input/
/// snapshot/state）在此单点实现，两宿主不重复。
/// </para>
/// <para>
/// 响应统一为 <see cref="HttpResult"/>（状态码 + 文本 body + 内容类型）——不依赖 ASP.NET Core
/// 的 <c>IResult</c>/<c>HttpContext</c>，使 MAUI 可用 BCL 自带的 <c>System.Net.HttpListener</c> 复用
/// （保住「MAUI 只依赖 Core」）。JSON 响应统一用 <see cref="JsonObject"/> 构造（NativeAOT 安全）。
/// </para>
/// </summary>
internal sealed class GameServerProtocol
{
    private const int TurnWaitTimeoutMs = 25000;

    /// <summary>
    /// 响应 JSON 序列化选项单例——双宿主共用（GameServerProtocol.Json 与 HttpListenerHost 的 WriteResultAsync 前缀）。
    /// UnsafeRelaxedJsonEscaping 与 Kestrel 的 HttpJsonOptions 默认 relaxed 转义一致，wire 逐字节相同。
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SessionRegistry _sessions;
    private readonly GameConfigService _config;

    public GameServerProtocol(SessionRegistry sessions, GameConfigService config)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public SessionRegistry Sessions => _sessions;

    /// <summary>
    /// POST /session —— 建会话（迁移自原 HandleCreateSessionAsync，见其注释）。
    /// </summary>
    public HttpResult CreateSession()
    {
        var result = _sessions.CreateNewSessionAsync().GetAwaiter().GetResult();
        return result.Status switch
        {
            SessionCreateStatus.NoGameLoaded => NoGameLoaded(),
            SessionCreateStatus.Conflict => Json(new JsonObject { ["error"] = "A session is already active" }, 409),
            _ => Json(new JsonObject { ["sessionId"] = result.SessionId, ["createdAt"] = result.CreatedAt, ["state"] = result.State }, 201),
        };
    }

    /// <summary>POST /control/acquire —— 获取控制权并返回 drain 后的末位回合确认。</summary>
    public HttpResult AcquireControl(ControlIdentity identity)
    {
        var session = _sessions.CurrentSession;
        if (session == null)
            return ErrorResult("NO_ACTIVE_SESSION", "No active session", 404);

        var result = session.AcquireControl(identity);
        if (result.Control.Status != ControlAcquireStatus.Acquired)
        {
            var code = result.Control.Status == ControlAcquireStatus.HeldByUser
                ? "CONTROL_HELD_BY_USER"
                : "CONTROL_HELD";
            return ControlErrorResult(code, result.Control.Status == ControlAcquireStatus.HeldByUser
                ? "Control is held by the user"
                : "Control is held by another agent", session);
        }

        return Json(new JsonObject
        {
            ["controller"] = ControllerNode(session.Controller.CurrentInfo),
            ["state"] = session.Controller.State,
            ["turn"] = ParseTurn(result.Turn),
            ["turnsAdvanced"] = result.TurnsAdvanced,
        });
    }

    public HttpResult ReleaseControl(ControlIdentity identity)
    {
        var session = _sessions.CurrentSession;
        if (session == null)
            return ErrorResult("NO_ACTIVE_SESSION", "No active session", 404);

        var result = session.Controller.Release(identity);
        if (result.Status == ControlReleaseStatus.NotOwner)
            return ControlErrorResult("CONTROL_NOT_OWNER", "Only the current Controller may release", session);

        return Json(new JsonObject
        {
            ["released"] = result.Status == ControlReleaseStatus.Released,
            ["controller"] = ControllerNode(session.Controller.CurrentInfo),
            ["state"] = session.Controller.State,
        });
    }

    public HttpResult GetControl()
    {
        var session = _sessions.CurrentSession;
        return Json(new JsonObject
        {
            ["controller"] = ControllerNode(session?.Controller.CurrentInfo),
            ["state"] = session?.Controller.State ?? Controller.StateIdle,
        });
    }

    public async Task<HttpResult> WaitForControlAsync(CancellationToken ct)
    {
        var session = _sessions.CurrentSession;
        if (session == null)
            return ErrorResult("NO_ACTIVE_SESSION", "No active session", 404);

        var controlEvent = await session.Controller.WaitForEventAsync(TurnWaitTimeoutMs, ct);
        return controlEvent == null
            ? HttpResult.NoContent()
            : Json(ControlEventNode(controlEvent));
    }

    /// <summary>
    /// GET /turn —— 长轮询等一个 turn（迁移自原 HandleGetTurnAsync，见其注释）。
    /// </summary>
    public async Task<HttpResult> GetTurnAsync(ControlIdentity identity, CancellationToken ct)
    {
        var session = _sessions.CurrentSession;

        if (session == null)
            return Json(new JsonObject { ["error"] = "No active session" }, 404);

        if (session.IsFinalTurnDelivered)
            return Json(new JsonObject { ["error"] = "Session ended" }, 404);

        var result = await session.WaitForTurnAsync(TurnWaitTimeoutMs, identity, ct);
        switch (result.Status)
        {
            case TurnWaitStatus.Turn:
                return HttpResult.Text(result.Turn!, "application/json", 200);
            case TurnWaitStatus.Timeout:
                return HttpResult.NoContent();
            case TurnWaitStatus.Closed:
                return Json(new JsonObject { ["error"] = "Session ended" }, 404);
            case TurnWaitStatus.ControlLost:
                return ControlLostResult(result.Reason ?? "control_changed", result.At ?? DateTimeOffset.UtcNow, session);
            default:
                return ErrorResult("UNEXPECTED_TURN_STATUS", "unexpected turn wait status", 500);
        }
    }

    /// <summary>POST /input —— 提交输入（value 已由宿主解析，此处做门禁 + 入队）。</summary>
    public HttpResult PostInput(string value, ControlIdentity identity)
    {
        var session = _sessions.CurrentSession;

        if (session == null)
            return Json(new JsonObject { ["error"] = "No active session" }, 404);

        var gate = session.Controller.CheckInput(identity, session.HasEnded);
        if (gate.Status != ControlGateStatus.Allowed)
            return ControlErrorResult(gate.Reason, "Input is allowed only for the current Controller", session);

        session.IO.EnqueueInput(BuildInputJsonl(value));
        return Json(new JsonObject { ["received"] = true });
    }

    /// <summary>
    /// GET /state —— 当前会话状态 + gameDir + 窗口布局元信息（迁移自原 HandleGetStateAsync，见其注释）。
    /// </summary>
    public HttpResult GetState()
    {
        var session = _sessions.CurrentSession;
        var m = _config.GetWindowMetrics();

        var payload = new JsonObject();
        if (session == null)
        {
            payload["state"] = "Idle";
            payload["isRunning"] = false;
            payload["threadAlive"] = false;
            payload["gameDir"] = null;
        }
        else
        {
            payload["state"] = session.StateString;
            payload["isRunning"] = session.IsRunning;
            // 目标 3：threadAlive 显式别名（游戏循环 Task 是否存活），替代 agent 侧脆弱推导；
            // lastError 仅在非空时透出（正常 Quit 无未料异常该键缺失）。
            payload["threadAlive"] = session.IsRunning;
            if (session.LastError is not null)
                payload["lastError"] = session.LastError;
            payload["sessionId"] = session.Id;
            payload["createdAt"] = session.CreatedAt;
            payload["gameDir"] = GamePaths.Current.ExeDir;
        }
        payload["windowWidth"] = m.WindowWidth;
        payload["fontSize"] = m.FontSize;
        payload["lineHeight"] = m.LineHeight;
        payload["gameColumns"] = m.GameColumns;
        payload["fontName"] = m.FontName;
        payload["visibleRows"] = m.VisibleRows;
        return Json(payload);
    }

    /// <summary>GET /config —— 当前 maxLog（前端履历容量）。</summary>
    public HttpResult GetConfig()
    {
        var maxLog = _config.GetValue<int>(ConfigCode.MaxLog);
        return Json(new JsonObject { ["maxLog"] = maxLog });
    }

    /// <summary>
    /// GET /snapshot —— 全量显示状态快照（迁移自原 HandleGetSnapshotAsync，见其注释）。
    /// </summary>
    public HttpResult GetSnapshot()
    {
        var session = _sessions.CurrentSession;

        if (session == null)
            return Json(new JsonObject { ["error"] = "No active session" }, 404);

        var json = session.GetDisplaySnapshot();
        if (json == null)
            return Json(new JsonObject { ["error"] = "Session not yet initialized" }, 503);

        return HttpResult.Text(json, "application/json", 200);
    }

    public HttpResult DeleteSession(ControlIdentity? identity)
    {
        var result = _sessions.DeleteSessionAsync(identity).GetAwaiter().GetResult();
        return result.Status switch
        {
            SessionDeleteStatus.Removed => Json(new JsonObject { ["removed"] = true }, 200),
            SessionDeleteStatus.ControlDenied => ControlErrorResult(result.Reason ?? "CONTROL_NOT_CONTROLLER", "Only the current Controller may change the session", _sessions.CurrentSession),
            _ => Json(new JsonObject { ["removed"] = false }, 404),
        };
    }

    /// <summary>POST /load-game —— 原子重载游戏目录（gameDir 已由宿主解析，此处做路径校验/重建，见原注释）。</summary>
    public HttpResult LoadGame(string gameDir, ControlIdentity identity)
    {
        var result = _sessions.ReplaceForLoadGameAsync(gameDir, identity).GetAwaiter().GetResult();
        return result.Status switch
        {
            LoadGameStatus.PathError => Json(
                new JsonObject { ["error"] = new JsonObject { ["code"] = result.Code, ["message"] = result.Message } },
                400),
            LoadGameStatus.LoadFailed => Json(
                new JsonObject { ["error"] = new JsonObject { ["code"] = "LOAD_FAILED", ["message"] = result.Message } },
                500),
            LoadGameStatus.ControlDenied => ControlErrorResult(result.Code ?? "CONTROL_NOT_CONTROLLER", result.Message ?? "Only the current Controller may change the session", _sessions.CurrentSession),
            _ => Json(new JsonObject
            {
                ["sessionId"] = result.SessionId,
                ["state"] = result.State,
                ["gameDir"] = result.GameDir,
            }),
        };
    }

    /// <summary>
    /// POST /native/pick-directory —— 安卓 SAF 目录选择器桩（迁移自原 HandlePickDirectoryAsync）。
    /// MAUI 阶段由 .NET MAUI 壳替换为真实现；前端拿到 supported=false 时回退到路径输入框。
    /// </summary>
    public static HttpResult PickDirectory()
    {
        return Json(new JsonObject
        {
            ["platform"] = "web",
            ["supported"] = false,
            ["message"] = "Not implemented on this platform",
        });
    }

    /// <summary>
    /// POST /game/scan —— 扫描主目录下的有效游戏（Web 模式游戏选择界面）。
    ///
    /// 浏览器沙箱不能枚举本地文件系统（GamePicker 因此只能手动输入路径），但 C# server
    /// 运行在本机可访问文件系统——复用 MAUI 同款 <see cref="GameScanner.Scan"/>（本地 FS 访问器）。
    /// rootDir 不存在不视为错误：返回 200 + rootDirExists=false + 空列表，前端据此展示空状态。
    /// </summary>
    public static HttpResult ScanGameDir(string? rootDir)
    {
        if (string.IsNullOrWhiteSpace(rootDir))
            return Json(
                new JsonObject { ["error"] = new JsonObject { ["code"] = "MISSING_ROOT_DIR", ["message"] = "Missing or empty 'rootDir' field" } },
                400);

        var accessor = new FileSystemGameDirAccessor();
        var exists = GameScanner.RootDirectoryExists(rootDir, accessor);
        var games = exists ? GameScanner.Scan(rootDir, accessor) : Array.Empty<GameEntry>();

        var arr = new JsonArray();
        foreach (var g in games)
        {
            arr.Add((JsonNode)new JsonObject { ["name"] = g.Name, ["fullPath"] = g.FullPath });
        }

        return Json(new JsonObject
        {
            ["rootDir"] = rootDir,
            ["rootDirExists"] = exists,
            ["games"] = arr,
        });
    }

    /// <summary>
    /// POST /game/dirs —— 列举目录下的子目录名（Web 模式目录浏览）。
    ///
    /// 复用 <see cref="DirectoryLister.ListDirectories"/>（本地 FS 访问器），供前端逐层
    /// 浏览文件系统定位主目录，替代手动输入绝对路径。响应含 currentPath / parentPath / dirs。
    /// </summary>
    public static HttpResult ListDirectories(string? dir)
    {
        var result = DirectoryLister.ListDirectories(dir, new FileSystemGameDirAccessor());
        var arr = new JsonArray();
        foreach (var d in result.SubDirectories)
        {
            arr.Add((JsonNode?)JsonValue.Create(d));
        }

        return Json(new JsonObject
        {
            ["currentPath"] = result.CurrentPath,
            ["parentPath"] = result.ParentPath,
            ["dirs"] = arr,
        });
    }

    /// <summary>把输入值构造成协议循环消费的 input jsonl（与 WS 输入帧同源，保证 HTTP/WS 输入格式对称）。</summary>
    public static string BuildInputJsonl(string value)
    {
        return new JsonObject { ["type"] = "input", ["value"] = value }.ToJsonString();
    }

    /// <summary>token → 身份：空 token 视为用户（无 token 调用方），非空视为 agent（token 即身份）。</summary>
    public static ControlIdentity ToIdentity(string? token)
    {
        return string.IsNullOrWhiteSpace(token)
            ? ControlIdentity.User
            : ControlIdentity.Agent(token);
    }

    /// <summary>agent lease 读取（EMUERA_CONTROL_LEASE_SECONDS 环境变量，缺省 5 分钟）——两宿主共用。</summary>
    public static TimeSpan ReadAgentLease()
    {
        var raw = Environment.GetEnvironmentVariable("EMUERA_CONTROL_LEASE_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(5);
    }

    // ===== 宿主共用的小工具：错误 / 序列化 / 节点构造（Kestrel 与 HttpListener 复用同一 wire）=====
    // 注：/input、/load-game 的 body 解析 + 校验错误助手已上收 HttpRouteDispatcher（C1）；
    // 此处仅保留协议层自身的响应构造。

    public static HttpResult NoGameLoaded()
        => Json(new JsonObject { ["error"] = "No game loaded" }, 503);

    private static JsonObject? ControllerNode(ControllerInfo? info)
    {
        if (info == null)
            return null;
        return new JsonObject
        {
            ["kind"] = info.Kind,
            ["leaseExpiresAt"] = info.LeaseExpiresAt,
        };
    }

    private static JsonObject ControlEventNode(ControlEvent controlEvent)
    {
        return new JsonObject
        {
            ["event"] = controlEvent.Type,
            ["type"] = controlEvent.Type,
            ["reason"] = controlEvent.Reason,
            ["at"] = controlEvent.At,
            ["controller"] = ControllerNode(controlEvent.Controller),
            ["state"] = controlEvent.State,
        };
    }

    private static JsonNode ParseTurn(string? turn)
    {
        if (turn == null)
            return null!;
        try
        {
            return JsonNode.Parse(turn) ?? JsonValue.Create(turn)!;
        }
        catch (JsonException)
        {
            return JsonValue.Create(turn)!;
        }
    }

    private static HttpResult Json(JsonObject payload, int statusCode = 200)
    {
        var body = payload.ToJsonString(JsonOptions);
        return HttpResult.Text(body, "application/json", statusCode);
    }

    private static HttpResult ErrorResult(string code, string message, int statusCode)
    {
        return Json(new JsonObject
        {
            ["error"] = code,
            ["code"] = code,
            ["message"] = message,
        }, statusCode: statusCode);
    }

    private static HttpResult ControlErrorResult(string code, string message, Session? session)
    {
        return Json(new JsonObject
        {
            ["error"] = code,
            ["code"] = code,
            ["message"] = message,
            ["reason"] = code,
            ["hint"] = "重新 acquire 可获取快照",
            ["controller"] = ControllerNode(session?.Controller.CurrentInfo),
            ["state"] = session?.Controller.State ?? Controller.StateIdle,
        }, statusCode: 409);
    }

    private static HttpResult ControlLostResult(string reason, DateTimeOffset at, Session session)
    {
        return Json(new JsonObject
        {
            ["error"] = "CONTROL_LOST",
            ["code"] = "CONTROL_LOST",
            ["reason"] = reason,
            ["at"] = at,
            ["controller"] = ControllerNode(session.Controller.CurrentInfo),
            ["state"] = session.Controller.State,
        }, statusCode: 409);
    }

    // 请求 body POCO（HttpInput/ControlRequest/LoadGameRequest）已上收 HttpRouteDispatcher（C1）。
}

/// <summary>
/// 传输无关的响应结果——宿主映射到各自 HTTP 层：
/// Kestrel 经 <c>Results.Text(body, contentType, Encoding.UTF8, statusCode)</c>（204 走 <c>Results.NoContent</c>），
/// MAUI HttpListener 写 <c>HttpListenerResponse</c>。
/// </summary>
internal readonly record struct HttpResult(int StatusCode, string? Body, string ContentType = "application/json")
{
    public static HttpResult Text(string body, string contentType = "application/json", int statusCode = 200)
        => new(statusCode, body, contentType);

    public static HttpResult NoContent()
        => new(204, null);
}
