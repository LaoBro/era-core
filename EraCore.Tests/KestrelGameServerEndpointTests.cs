using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MinorShift.Emuera.Output;
using Microsoft.Extensions.Options;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// KestrelGameServer 端点行为测试（重构补测——/turn、/input、/snapshot、/config、409/201 原零覆盖）。
/// 沿用 IdleTests 惯例：直接构造 new KestrelGameServer(port:0) 后调 internal handler
/// 验返回 IResult 的 statusCode/body，不经 HTTP 网络栈。
/// 涉及全局静态 GamePaths.Current，与 SessionRegistryTests 共享禁用并行的 collection。
/// </summary>
[Collection("ServerState")]
public class KestrelGameServerEndpointTests
{
    private static KestrelGameServer CreateServer()
    {
        return new KestrelGameServer(0, new SessionRegistryTests.NullTerminalSetup(), new ConfigData());
    }

    /// <summary>
    /// 执行 IResult 并返回 (statusCode, body)——用 DefaultHttpContext + MemoryStream 捕获响应。
    /// Results.Json 需要 IOptions&lt;JsonOptions&gt;，故构造最小 ServiceProvider 注入。
    /// </summary>
    private static async Task<(int statusCode, string body)> ExecuteResultAsync(IResult result)
    {
        var ctx = new DefaultHttpContext();
        var stream = new MemoryStream();
        ctx.Response.Body = stream;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.Configure<JsonOptions>(o => { });
        ctx.RequestServices = services.BuildServiceProvider();

        await result.ExecuteAsync(ctx);
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return (ctx.Response.StatusCode, body);
    }

    private static HttpContext MakeLoadGameContext(string gameDir)
    {
        return MakeJsonBodyContext(JsonSerializer.Serialize(new { gameDir }));
    }

    private static HttpContext MakeJsonBodyContext(string jsonBody)
    {
        var ctx = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(jsonBody);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.Method = "POST";
        return ctx;
    }

    /// <summary>POST /load-game {test_game} 成功，并轮询 GET /snapshot 至 200（游戏循环就绪）。</summary>
    private static async Task LoadGame(KestrelGameServer server)
    {
        var loadCtx = MakeLoadGameContext(SessionRegistryTests.FindTestGameDir());
        var (loadStatus, _) = await ExecuteResultAsync(await server.HandleLoadGameAsync(loadCtx));
        Assert.Equal(200, loadStatus);

        // 等待游戏循环初始化 DisplayState（snapshot 200）——之后 /turn 轮询不会空队列阻塞 25s
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var (s, _) = await ExecuteResultAsync(server.HandleGetSnapshotAsync());
            if (s == 200)
                return;
            await Task.Delay(50);
        }
        Assert.Fail("game loop did not initialize in time");
    }

    // ---- POST /session ----

    [Fact]
    public async Task Create_session_returns_409_when_active()
    {
        using var server = CreateServer();
        await LoadGame(server);

        var (status, body) = await ExecuteResultAsync(await server.HandleCreateSessionAsync());

        Assert.Equal(409, status);
        Assert.Contains("A session is already active", body);
    }

    [Fact]
    public async Task Create_session_rebuilds_after_ended_returns_201()
    {
        using var server = CreateServer();
        await LoadGame(server);

        // 等待游戏进入 WaitInput（StepAsync 仅在 console.State==WaitInput 时派发输入）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && server.Sessions.CurrentSession!.StateString != "WaitInput")
            await Task.Delay(100);
        Assert.Equal("WaitInput", server.Sessions.CurrentSession!.StateString);

        // 注入输入驱动 TEST.ERB 走到 QUIT（INPUT 收到 "1" → ELSE 分支 → QUIT）。
        // 注意：input 帧必须是协议 jsonl 格式（与 BuildInputJsonl 同源），裸字符串会被协议忽略。
        server.Sessions.CurrentSession!.IO.EnqueueInput("{\"type\":\"input\",\"value\":\"1\"}");

        // 轮询 GET /turn 直到 404（session 结束）。经 Channel 路径（有内存屏障）观测，
        // 且 HasEnded 已是 volatile（无锁读可见），QUIT 后快速稳定收敛。
        while (DateTime.UtcNow < deadline)
        {
            var (turnStatus, _) = await ExecuteResultAsync(await server.HandleGetTurnAsync(new DefaultHttpContext()));
            if (turnStatus == 404)
                break;
            await Task.Delay(100);
        }

        // 游戏循环已结束 → POST /session 重建并返 201。409 幂等不销毁状态，重试兜底极小竞态窗口。
        int status = 0;
        string body = "";
        var retryDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < retryDeadline)
        {
            (status, body) = await ExecuteResultAsync(await server.HandleCreateSessionAsync());
            if (status == 201)
                break;
            await Task.Delay(100);
        }

        Assert.Equal(201, status);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("sessionId", out _));
        Assert.True(doc.RootElement.TryGetProperty("state", out _));
    }

    // ---- GET /turn ----

    [Fact]
    public async Task Turn_returns_404_when_no_session()
    {
        using var server = CreateServer();

        var (status, body) = await ExecuteResultAsync(await server.HandleGetTurnAsync(new DefaultHttpContext()));

        Assert.Equal(404, status);
        Assert.Contains("No active session", body);
    }

    [Fact]
    public async Task Turn_returns_injected_turn()
    {
        using var server = CreateServer();
        await LoadGame(server);

        // 直接经测试接缝注入（WriteLine 立即入 _output Channel，与游戏循环无关）
        const string marker = "{\"type\":\"turn\",\"test\":true}";
        server.Sessions.CurrentSession!.IO.WriteLine(TestTurnFactory.Wire(marker));

        // 轮询消费：游戏循环可能先产出若干 turn，直到拿到 marker
        string? got = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var (status, body) = await ExecuteResultAsync(await server.HandleGetTurnAsync(new DefaultHttpContext()));
            if (status == 200)
            {
                got = body;
                if (got == marker)
                    break;
            }
            else if (status != 204)
            {
                break; // 404/500：直接失败
            }
        }

        Assert.Equal(marker, got);
    }

    [Fact]
    public async Task Turn_returns_404_after_session_ended()
    {
        using var server = CreateServer();
        await LoadGame(server);

        // 等待游戏进入 WaitInput（StepAsync 仅在 console.State==WaitInput 时派发输入）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && server.Sessions.CurrentSession!.StateString != "WaitInput")
            await Task.Delay(100);
        Assert.Equal("WaitInput", server.Sessions.CurrentSession!.StateString);

        // 注入输入驱动 TEST.ERB 走到 QUIT（INPUT 收到 "1" → ELSE 分支 → QUIT）——
        // 真实 QUIT 路径，与 Create_session_rebuilds_after_ended_returns_201 同源。
        server.Sessions.CurrentSession!.IO.EnqueueInput("{\"type\":\"input\",\"value\":\"1\"}");

        // 消费 QUIT 前后的 turn（含 finalTurn），直到 session 结束返 404（Closed / finalTurn 已交付）
        int status = 0;
        string body = "";
        while (DateTime.UtcNow < deadline)
        {
            (status, body) = await ExecuteResultAsync(await server.HandleGetTurnAsync(new DefaultHttpContext()));
            if (status == 404)
                break;
            await Task.Delay(100);
        }

        Assert.Equal(404, status);
        Assert.Contains("Session ended", body);
    }

    // ---- POST /input ----

    [Fact]
    public async Task Input_returns_404_when_no_session()
    {
        using var server = CreateServer();

        var (status, _) = await ExecuteResultAsync(
            await server.HandlePostInputAsync(MakeJsonBodyContext("{\"value\":\"x\"}")));

        Assert.Equal(404, status);
    }

    [Fact]
    public async Task Input_rejects_invalid_json_with_400()
    {
        using var server = CreateServer();
        await LoadGame(server);

        var (status, body) = await ExecuteResultAsync(
            await server.HandlePostInputAsync(MakeJsonBodyContext("not-json")));

        Assert.Equal(400, status);
        Assert.Contains("Invalid JSON", body);
    }

    [Fact]
    public async Task Input_rejects_missing_value_with_400()
    {
        using var server = CreateServer();
        await LoadGame(server);

        var (status, body) = await ExecuteResultAsync(
            await server.HandlePostInputAsync(MakeJsonBodyContext("{\"foo\":\"bar\"}")));

        Assert.Equal(400, status);
        Assert.Contains("Missing 'value' field", body);
    }

    [Fact]
    public async Task Input_accepts_value_with_200()
    {
        using var server = CreateServer();
        await LoadGame(server);

        var (status, body) = await ExecuteResultAsync(
            await server.HandlePostInputAsync(MakeJsonBodyContext("{\"value\":\"hi\"}")));

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("received").GetBoolean());
    }

    /// <summary>
    /// spec-v3 §5.3「状态类」输入：只带 <c>keys</c> / <c>hoverValue</c>、<b>不带 value</b>，
    /// 同样是合法输入（写 <c>IInputHost</c>，不派发、不产帧）。
    /// 改动前 <c>value == null</c> 一律 400 —— 状态类通道会被门禁挡死，故本例是它的验收点。
    /// </summary>
    [Fact]
    public async Task Input_accepts_state_only_frame_with_200()
    {
        using var server = CreateServer();
        await LoadGame(server);

        var (status, body) = await ExecuteResultAsync(
            await server.HandlePostInputAsync(MakeJsonBodyContext(
                "{\"keys\":[{\"code\":65,\"down\":true}],\"hoverValue\":\"42\"}")));

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("received").GetBoolean());
    }

    // ---- GET /snapshot ----

    [Fact]
    public async Task Snapshot_returns_404_when_no_session()
    {
        using var server = CreateServer();

        var (status, body) = await ExecuteResultAsync(server.HandleGetSnapshotAsync());

        Assert.Equal(404, status);
        Assert.Contains("No active session", body);
    }

    // 注：GET /snapshot 的 503 分支（session 存在但未初始化）未写自动测试——503 是设计中的瞬态窗口
    // （load-game/POST /session 后、console.Initialize 完成前，实测 <660ms 即消失），无法确定性触发。
    // 触发条件（Session.GetDisplaySnapshot()==null）由 SessionWaitForTurnTests.Uninitialized_session_snapshot_is_null
    // 确定性覆盖；handler 侧 503 映射为单行 if（json == null → 503），由 404/200 测试 + 评审覆盖。

    [Fact]
    public async Task Snapshot_returns_200_after_load()
    {
        using var server = CreateServer();
        await LoadGame(server);

        var (status, body) = await ExecuteResultAsync(server.HandleGetSnapshotAsync());

        Assert.Equal(200, status);
        Assert.False(string.IsNullOrEmpty(body));

        // v12（spec §5.2 第 4 项 / §7.4「GET /snapshot 同源」）：晚加入者拿到的第一张画面
        // 必须与流上帧同形 —— awaiting 是快照端点上可读的同一个契约字段。
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("awaiting", out var awaiting),
            "v12 快照必须携带 awaiting（唯一契约字段）—— 未同步即为变异式 E");
        Assert.Equal(JsonValueKind.Object, awaiting.ValueKind);
        Assert.Equal(12, doc.RootElement.GetProperty("protocolVersion").GetInt32());
        // 注：本断言只钉「同形」（键在场且是对象）。§1.2 规则 1 的「至少一维必现」约束的是
        // **停稳点**上的帧；本端点在「脚本还在跑」的瞬态可以取到 awaiting:{}（无维可报，
        // 展开阶段由 state 字段区分）。停稳点的深比对在 TurnAwaitingV12Tests.Snapshot_awaiting_matches_stream_shape。
    }

    // ---- GET /config ----

    [Fact]
    public async Task Config_returns_maxLog()
    {
        using var server = CreateServer();

        var (status, body) = await ExecuteResultAsync(server.HandleGetConfigAsync());

        Assert.Equal(200, status);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.TryGetProperty("maxLog", out var maxLog));
        Assert.Equal(5000, maxLog.GetInt32());
    }
}
