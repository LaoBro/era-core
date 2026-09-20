using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// HttpRouteDispatcher 单测（C1 三件套第 1 件）——直接注入传输无关 HttpRequestData，
/// 断言 HttpResult，验证「路由 + body 解析 + 校验错误映射」（无需拉起 Kestrel / HttpListener）。
/// 端到端 token 回退语义由既有 KestrelGameServerEndpointTests / HttpListenerHostTests 锁定
/// （二者现均经本 dispatcher 路由）；此处补纯校验面。
/// </summary>
public class HttpRouteDispatcherTests
{
    private static HttpRouteDispatcher CreateDispatcher()
        => new(new GameServerProtocol(CreateRegistry(), CreateConfigService()));

    /// <summary>带宿主 <c>/game/scan</c> 实现的 dispatcher——用于验证「省略 rootDir」这条新边界。</summary>
    private static HttpRouteDispatcher CreateDispatcherWithScanSink(Func<string?, HttpResult?> sink)
        => new(new GameServerProtocol(CreateRegistry(), CreateConfigService()),
               ServerJsonContext.Default, sink, loadGameHandler: null);

    private static GameConfigService CreateConfigService()
        => new(new ConfigData(), false);

    private static GameConfigService CreateConfigService(AppSettings appSettings)
        => new(new ConfigData(), appSettings: appSettings);

    private static SessionRegistry CreateRegistry()
        => new(new SessionRegistryTests.NullTerminalSetup(), CreateConfigService(), GameServerProtocol.ReadAgentLease());

    private static Task<HttpResult> DispatchAsync(HttpRouteDispatcher dispatcher, string method, string path, string? body = null, string? token = null)
        => dispatcher.DispatchAsync(new HttpRequestData(method, path, body, token), CancellationToken.None);

    // ---- 未知路径路由 ----

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "GET", "/does-not-exist");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("NOT_FOUND", resp.Body);
    }

    // ---- 路由映射 ----

    [Fact]
    public async Task Get_turn_without_session_returns_404()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "GET", "/turn");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("No active session", resp.Body);
    }

    [Fact]
    public async Task Acquire_without_session_returns_404()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/control/acquire");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("NO_ACTIVE_SESSION", resp.Body);
    }

    // ---- /input 校验错误映射 ----

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Input_bad_or_empty_body_returns_400_invalid_json(string? body)
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/input", body);
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("Invalid JSON", resp.Body);
    }

    [Fact]
    public async Task Input_missing_value_returns_400()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/input", "{\"foo\":\"bar\"}");
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("Missing 'value' field", resp.Body);
    }

    [Fact]
    public async Task Input_valid_json_reaches_protocol_without_session_returns_404()
    {
        // 解析成功 → 过校验 → 无 session → 协议层 404（校验映射前置、入队在后）。
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/input", "{\"value\":\"hi\"}");
        Assert.Equal(404, resp.StatusCode);
        Assert.Contains("No active session", resp.Body);
    }

    // ---- /load-game 校验错误映射 ----

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData(null)]
    public async Task LoadGame_bad_or_empty_body_returns_400_invalid_json(string? body)
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/load-game", body);
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("INVALID_JSON", resp.Body);
    }

    [Fact]
    public async Task LoadGame_missing_dir_returns_400()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/load-game", "{\"foo\":\"bar\"}");
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("MISSING_GAME_DIR", resp.Body);
    }

    // ---- /game/scan 与 /game/dirs（Web 游戏选择）----

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ScanGame_bad_or_empty_body_returns_400_invalid_json(string? body)
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/game/scan", body);
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("INVALID_JSON", resp.Body);
    }

    [Fact]
    public async Task ScanGame_missing_root_dir_returns_400()
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/game/scan", "{\"foo\":\"bar\"}");
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("MISSING_ROOT_DIR", resp.Body);
    }

    // ---- issues/15 后续：省略 rootDir = 「扫宿主自己的主目录」 ----
    // 控制面（eracore_agent）因而**不必持有路径**：它手上的路径会随权限/主目录变更而过期，
    // 而宿主取用自己当前生效的主目录永远是新的。路径不越过边界，就没有过期路径这回事。

    [Fact]
    public async Task ScanGame_omitted_root_dir_reaches_host_sink_as_null()
    {
        string? seen = "sentinel";
        var dispatcher = CreateDispatcherWithScanSink(rootDir =>
        {
            seen = rootDir;
            return HttpResult.Text("""{"rootDir":"/host/main","rootDirExists":true,"games":[]}""");
        });

        var resp = await DispatchAsync(dispatcher, "POST", "/game/scan", "{}");

        Assert.Equal(200, resp.StatusCode);
        Assert.Null(seen);
        Assert.Contains("/host/main", resp.Body);
    }

    [Fact]
    public async Task ScanGame_omitted_root_dir_without_host_still_requires_root_dir()
    {
        // 无宿主（独立 Server/CLI）时不放行省略——Web 前端与命令行一直传显式路径，契约不变。
        var dispatcher = CreateDispatcher();

        var resp = await DispatchAsync(dispatcher, "POST", "/game/scan", "{}");

        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("MISSING_ROOT_DIR", resp.Body);
    }

    [Fact]
    public async Task ScanGame_omitted_root_dir_still_400_when_host_declines()
    {
        // 宿主存在但明确不接管（返回 null）时空 rootDir 已无处可扫——不得落到默认实现
        // （它硬编码文件系统 accessor，也没有"主目录"概念），如实回落缺参错误。
        var dispatcher = CreateDispatcherWithScanSink(_ => null);

        var resp = await DispatchAsync(dispatcher, "POST", "/game/scan", "{}");

        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("MISSING_ROOT_DIR", resp.Body);
    }

    [Fact]
    public async Task ScanGame_nonexistent_dir_returns_200_with_root_dir_exists_false()
    {
        var dispatcher = CreateDispatcher();
        var missing = Path.Combine(Path.GetTempPath(), "emuera-scan-missing-" + Guid.NewGuid().ToString("N"));
        var resp = await DispatchAsync(dispatcher, "POST", "/game/scan", JsonSerializer.Serialize(new { rootDir = missing }));

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("\"rootDirExists\":false", resp.Body);
        Assert.Contains("\"games\":[]", resp.Body);
    }

    [Fact]
    public async Task ScanGame_valid_dir_returns_detected_games_only()
    {
        using var tmp = new TempRoot();
        CreateGame(tmp.Root, "game-a");
        CreateGame(tmp.Root, "game-b");
        // 缺 erb/ 的目录不应被检测
        Directory.CreateDirectory(Path.Combine(tmp.Root, "not-a-game", "csv"));

        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/game/scan", JsonSerializer.Serialize(new { rootDir = tmp.Root }));

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("\"rootDirExists\":true", resp.Body);
        Assert.Contains("\"name\":\"game-a\"", resp.Body);
        Assert.Contains("\"name\":\"game-b\"", resp.Body);
        Assert.DoesNotContain("\"name\":\"not-a-game\"", resp.Body);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData(null)]
    public async Task ListDirs_bad_or_empty_body_returns_400_invalid_json(string? body)
    {
        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/game/dirs", body);
        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("INVALID_JSON", resp.Body);
    }

    [Fact]
    public async Task ListDirs_returns_subdirectory_names()
    {
        using var tmp = new TempRoot();
        Directory.CreateDirectory(Path.Combine(tmp.Root, "sub1"));
        Directory.CreateDirectory(Path.Combine(tmp.Root, "sub2"));
        File.WriteAllText(Path.Combine(tmp.Root, "file.txt"), "x");

        var dispatcher = CreateDispatcher();
        var resp = await DispatchAsync(dispatcher, "POST", "/game/dirs", JsonSerializer.Serialize(new { dir = tmp.Root }));

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("\"sub1\"", resp.Body);
        Assert.Contains("\"sub2\"", resp.Body);
        Assert.DoesNotContain("file.txt", resp.Body);
    }

    private static void CreateGame(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(dir, "csv"));
        Directory.CreateDirectory(Path.Combine(dir, "erb"));
    }

    // ---- /settings（ADR-0030 全局设置端点）----

    [Fact]
    public async Task Settings_get_returns_all_13_keys()
    {
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), CreateConfigService(new AppSettings())));
        var resp = await DispatchAsync(dispatcher, "GET", "/settings");

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("\"displayReportOverride\":\"auto\"", resp.Body);
        Assert.Contains("\"sweepGatePolicy\":\"auto\"", resp.Body);
        Assert.Contains("\"dimLockPolicy\":\"auto\"", resp.Body);
    }

    [Fact]
    public async Task Settings_put_valid_batch_applies_and_echoes()
    {
        var appSettings = new AppSettings();
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), CreateConfigService(appSettings)));
        var resp = await DispatchAsync(
            dispatcher, "PUT", "/settings",
            JsonSerializer.Serialize(new { sweepGatePolicy = "forceoff", safReadDegree = "4" }));

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("\"sweepGatePolicy\":\"forceoff\"", resp.Body);
        Assert.Contains("\"safReadDegree\":\"4\"", resp.Body);
        Assert.Equal("forceoff", appSettings.Get(AppSettings.SweepGatePolicyKey));
    }

    [Fact]
    public async Task Settings_put_unknown_key_returns_400_invalid_setting()
    {
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), CreateConfigService(new AppSettings())));
        var resp = await DispatchAsync(dispatcher, "PUT", "/settings", "{\"bogus\":\"auto\"}");

        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("INVALID_SETTING", resp.Body);
    }

    [Fact]
    public async Task Settings_put_invalid_value_returns_400_invalid_setting()
    {
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), CreateConfigService(new AppSettings())));
        var resp = await DispatchAsync(dispatcher, "PUT", "/settings", "{\"dimLockPolicy\":\"lock\"}");

        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("INVALID_SETTING", resp.Body);
    }

    [Theory]
    [InlineData("{\"safReadDegree\":4}")]
    [InlineData("{\"agentLogEnabled\":true}")]
    [InlineData("{\"sweepGatePolicy\":{\"a\":1}}")]
    public async Task Settings_put_non_string_value_returns_400_invalid_setting(string body)
    {
        // 设置值是词表字符串——数字/布尔/对象一律 400（不得落成 500）
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), CreateConfigService(new AppSettings())));
        var resp = await DispatchAsync(dispatcher, "PUT", "/settings", body);

        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("INVALID_SETTING", resp.Body);
    }

    [Fact]
    public async Task Settings_put_display_report_applies_immediately_to_current()
    {
        // PUT displayReportOverride=off 即时应用到当前 ConfigData（与 MAUI 运行时切换一致）
        var appSettings = new AppSettings();
        var config = CreateConfigService(appSettings);
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), config));
        var resp = await DispatchAsync(
            dispatcher, "PUT", "/settings",
            JsonSerializer.Serialize(new { displayReportOverride = "off" }));

        Assert.Equal(200, resp.StatusCode);
        Assert.False(config.Current.OverrideDisplayReport);
        Assert.False(config.Current.GetConfigValue<bool>(ConfigCode.DisplayReport));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Settings_put_bad_or_empty_body_returns_400(string? body)
    {
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), CreateConfigService(new AppSettings())));
        var resp = await DispatchAsync(dispatcher, "PUT", "/settings", body);

        Assert.Equal(400, resp.StatusCode);
        Assert.Contains("Invalid JSON", resp.Body);
    }

    [Fact]
    public async Task Settings_delete_resets_to_defaults()
    {
        var appSettings = new AppSettings();
        Assert.True(appSettings.Set(AppSettings.SweepGatePolicyKey, "forceoff"));
        var dispatcher = new HttpRouteDispatcher(
            new GameServerProtocol(CreateRegistry(), CreateConfigService(appSettings)));

        var resp = await DispatchAsync(dispatcher, "DELETE", "/settings");

        Assert.Equal(200, resp.StatusCode);
        Assert.Contains("\"sweepGatePolicy\":\"auto\"", resp.Body);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Root { get; }
        private bool _disposed;

        public TempRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "emuera-dispatcher-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* 测试临时目录清理失败不致测试失败 */ }
        }
    }
}