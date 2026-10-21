using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using MinorShift.Emuera.Terminal.Platform;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// T-025：server 模式空闲启动单元测试。
///
/// 验证三组 HTTP 契约（spec L112-114）——以对外可观察行为为准，不测内部标志位：
/// 1. 空闲态（_session==null）POST /session → 503 {"error":"No game loaded"}（D4/D16）
/// 2. 空闲态 GET /state → gameDir==null + state=="Idle"（D5/D15）
/// 3. POST /load-game {合法目录} 成功后 GET /state → gameDir 非 null + state!="Idle"（D10）
/// 4. DELETE /session 后 GET /state → gameDir==null + state=="Idle"，POST /session → 503（D10 复位）
///
/// 测试机制（spec L111）：直接构造 new KestrelGameServer(port:0, stub_setup, new ConfigData())
/// 后调 internal handler 验返回 IResult 的 statusCode。不模拟 Program.Main 启动分支
/// （Validate() 跳过由 Python e2e 覆盖）。
/// </summary>
public class KestrelGameServerIdleTests
{
    /// <summary>
    /// 查找 test_game 目录——从测试 bin 目录向上遍历找 repo 根的 test_game/。
    /// </summary>
    private static string FindTestGameDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test_game");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "test_game directory not found. Searched upward from: " + AppContext.BaseDirectory);
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

        // Results.Json 内部 GetRequiredService<IOptions<JsonOptions>>——需 ServiceProvider
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

    /// <summary>
    /// 构造带 JSON body 的 HttpContext——供 HandleLoadGameAsync 使用。
    /// </summary>
    private static HttpContext MakeLoadGameContext(string gameDir)
    {
        var ctx = new DefaultHttpContext();
        var json = JsonSerializer.Serialize(new { gameDir });
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentType = "application/json";
        ctx.Request.Method = "POST";
        return ctx;
    }

    private static KestrelGameServer CreateServer()
    {
        return new KestrelGameServer(0, new NullTerminalSetup(), new ConfigData());
    }

    /// <summary>
    /// D4/D16：空闲态（_session==null）POST /session → 503 {"error":"No game loaded"}。
    /// 守卫位于 _sessionLock 内首行，早于「已有活跃会话 409」判断。
    /// </summary>
    [Fact]
    public async Task Idle_create_session_returns_503()
    {
        using var server = CreateServer();

        var result = await server.HandleCreateSessionAsync();
        var (status, body) = await ExecuteResultAsync(result);

        Assert.Equal(503, status);
        Assert.Contains("No game loaded", body);
    }

    /// <summary>
    /// D5/D15：空闲态 GET /state → gameDir==null + state=="Idle"。
    /// 即使 GamePaths.Current 指向默认目录，idle 态 gameDir 显式置 null——前端据此可靠判 idle。
    /// 其余窗口元信息字段（windowWidth/fontSize/...）照常由默认 ConfigData 提供。
    /// </summary>
    [Fact]
    public async Task Idle_get_state_returns_null_gameDir()
    {
        using var server = CreateServer();

        var result = server.HandleGetStateAsync();
        var (status, body) = await ExecuteResultAsync(result);

        Assert.Equal(200, status);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("Idle", root.GetProperty("state").GetString());
        // D5：gameDir 必须是 null（JSON null），不是 GamePaths.Current.ExeDir
        Assert.Equal(JsonValueKind.Null, root.GetProperty("gameDir").ValueKind);
        // 窗口元信息字段照常提供
        Assert.True(root.TryGetProperty("windowWidth", out _), "windowWidth present");
        Assert.True(root.TryGetProperty("fontSize", out _), "fontSize present");
        Assert.True(root.TryGetProperty("lineHeight", out _), "lineHeight present");
        Assert.True(root.TryGetProperty("gameColumns", out _), "gameColumns present");
        Assert.True(root.TryGetProperty("fontName", out _), "fontName present");
        // foreColor：turn 协议只带「非默认色」的 segment 颜色，前端靠它画普通文字
        Assert.True(root.TryGetProperty("foreColor", out _), "foreColor present");
        Assert.True(root.TryGetProperty("visibleRows", out _), "visibleRows present");
    }

    /// <summary>
    /// D10 生命周期：POST /load-game {合法目录} → GET /state 有效 → DELETE /session → GET /state idle + POST /session 503。
    ///
    /// 验证空闲/已加载判定复用 _session==null 不变量（不新增 _gameLoaded 标志）：
    /// - /load-game 成功建 session 后 _session!=null（已加载）
    /// - DELETE /session dispose 后 _session==null（复位空闲）
    /// </summary>
    [Fact]
    public async Task Load_game_then_delete_returns_to_idle()
    {
        var gameDir = FindTestGameDir();
        using var server = CreateServer();

        // --- 阶段 1：POST /load-game {合法目录} → 200 + _session!=null ---
        var ctx = MakeLoadGameContext(gameDir);
        var loadResult = await server.HandleLoadGameAsync(ctx);
        var (loadStatus, loadBody) = await ExecuteResultAsync(loadResult);

        Assert.Equal(200, loadStatus);
        using var loadDoc = JsonDocument.Parse(loadBody);
        var loadRoot = loadDoc.RootElement;
        Assert.True(loadRoot.TryGetProperty("sessionId", out _), "load-game response has sessionId");
        // state 应为 "Loading"（_loading=true）或游戏循环已推进后的状态，但不为 "Idle"
        var loadedState = loadRoot.GetProperty("state").GetString();
        Assert.NotNull(loadedState);
        Assert.NotEqual("Idle", loadedState);

        // --- 阶段 2：GET /state → gameDir 非 null + state != "Idle" ---
        var stateResult = server.HandleGetStateAsync();
        var (stateStatus, stateBody) = await ExecuteResultAsync(stateResult);

        Assert.Equal(200, stateStatus);
        using var stateDoc = JsonDocument.Parse(stateBody);
        var stateRoot = stateDoc.RootElement;
        // D5：有 session 时 gameDir = GamePaths.Current.ExeDir（非 null）
        Assert.Equal(JsonValueKind.String, stateRoot.GetProperty("gameDir").ValueKind);
        Assert.NotEqual("Idle", stateRoot.GetProperty("state").GetString());

        // --- 阶段 3：DELETE /session → 200 + removed=true ---
        var deleteResult = await server.HandleDeleteSessionAsync();
        var (deleteStatus, deleteBody) = await ExecuteResultAsync(deleteResult);

        Assert.Equal(200, deleteStatus);
        using var deleteDoc = JsonDocument.Parse(deleteBody);
        Assert.True(deleteDoc.RootElement.GetProperty("removed").GetBoolean(), "delete returns removed=true");

        // --- 阶段 4：GET /state → gameDir==null + state=="Idle"（复位） ---
        var stateAfterDelete = server.HandleGetStateAsync();
        var (statusAfterDelete, bodyAfterDelete) = await ExecuteResultAsync(stateAfterDelete);

        Assert.Equal(200, statusAfterDelete);
        using var afterDeleteDoc = JsonDocument.Parse(bodyAfterDelete);
        var afterDeleteRoot = afterDeleteDoc.RootElement;
        Assert.Equal("Idle", afterDeleteRoot.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, afterDeleteRoot.GetProperty("gameDir").ValueKind);

        // --- 阶段 5：POST /session → 503（_session==null 复位，D4 守卫生效） ---
        var createAfterDelete = await server.HandleCreateSessionAsync();
        var (createStatus, createBody) = await ExecuteResultAsync(createAfterDelete);

        Assert.Equal(503, createStatus);
        Assert.Contains("No game loaded", createBody);
    }

    /// <summary>
    /// 最小 ITerminalSetup 桩——与既有 NullTerminalSetup（AgentJsonlProtocolTests / DisplayDiffTests 等）
    /// 同构，单元测试不依赖真实终端初始化。
    /// </summary>
    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
