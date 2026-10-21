using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// HttpListenerHost 测试（issue 05 MAUI 托管宿主）——走真实 localhost HTTP 栈，
/// 验证共享层 GameServerProtocol 经 BCL HttpListener 宿主提供的端点与 Kestrel 契约一致：
/// GET /state（Idle）→ POST /load-game → GET /snapshot（游戏就绪）→ GET /config → 控制权 → DELETE /session → 复位；
/// 另覆盖 /turn、/input、/control/wait 及 409 门禁场景（双宿主 wire 契约一致性）。
/// 涉及全局静态 GamePaths.Current，与 ServerState 集合共享禁用并行的约束。
/// </summary>
[Collection("ServerState")]
public class HttpListenerHostTests
{
    private static HttpListenerHost CreateHost()
        => new(0, new SessionRegistryTests.NullTerminalSetup(), new ConfigData());

    /// <summary>POST /load-game {test_game} 成功，并轮询 GET /snapshot 至 200（游戏循环就绪）。</summary>
    private static async Task LoadGameAsync(HttpClient client)
    {
        var gameDir = SessionRegistryTests.FindTestGameDir();
        var loadJson = JsonSerializer.Serialize(new { gameDir });
        var loadResp = await client.PostAsync("/load-game",
            new StringContent(loadJson, Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)loadResp.StatusCode);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await client.GetAsync("/snapshot");
            if ((int)resp.StatusCode == 200)
                return;
            await Task.Delay(50);
        }
        Assert.Fail("game loop did not initialize in time");
    }

    [Fact]
    public async Task Host_serves_protocol_endpoints_over_http()
    {
        using var host = CreateHost();

        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };

        // --- 1. 空闲态：GET /state → 200 + state==Idle + gameDir==null ---
        var state = await GetJsonAsync(client, "/state");
        Assert.Equal(200, state.Status);
        Assert.Equal("Idle", state.Body?["state"]?.GetValue<string>());
        Assert.Null(state.Body?["gameDir"]);
        Assert.True(state.Body?["windowWidth"] != null, "state has windowWidth");
        Assert.True(state.Body?["visibleRows"] != null, "state has visibleRows");

        // --- 2/3. POST /load-game {test_game} → 200 + 轮询 snapshot 至游戏就绪 ---
        await LoadGameAsync(client);

        // --- 4. GET /state → 有会话：state != Idle + gameDir 非 null ---
        var state2 = await GetJsonAsync(client, "/state");
        Assert.Equal(200, state2.Status);
        Assert.NotEqual("Idle", state2.Body?["state"]?.GetValue<string>());
        Assert.True(state2.Body?["gameDir"] != null, "state has gameDir after load");

        // --- 5. GET /config → 200 + maxLog ---
        var config = await GetJsonAsync(client, "/config");
        Assert.Equal(200, config.Status);
        Assert.NotNull(config.Body?["maxLog"]);

        // --- 6. 控制权：acquire → release ---
        var acquire = await client.PostAsync("/control/acquire", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)acquire.StatusCode);
        var control = await GetJsonAsync(client, "/control");
        Assert.Equal(200, control.Status);
        Assert.Equal("held", control.Body?["state"]?.GetValue<string>());
        var release = await client.PostAsync("/control/release", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(200, (int)release.StatusCode);

        // --- 7. DELETE /session → 200 + removed:true；随后 state 复位 Idle ---
        var del = await client.DeleteAsync("/session");
        Assert.Equal(200, (int)del.StatusCode);
        var state3 = await GetJsonAsync(client, "/state");
        Assert.Equal("Idle", state3.Body?["state"]?.GetValue<string>());
    }

    private static async Task<(int Status, JsonObject? Body)> GetJsonAsync(HttpClient client, string path)
    {
        using var resp = await client.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        JsonObject? body = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                body = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }
        }
        return ((int)resp.StatusCode, body);
    }

    // ---- GET /turn ----

    [Fact]
    public async Task Turn_returns_404_when_no_session()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };

        using var resp = await client.GetAsync("/turn");

        Assert.Equal(404, (int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("No active session", body);
    }

    [Fact]
    public async Task Turn_returns_injected_turn()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        await LoadGameAsync(client);

        // 经测试接缝注入 marker turn（WriteLine 立即入 _output Channel，与游戏循环无关）
        const string marker = "{\"type\":\"turn\",\"test\":true}";
        host.Sessions.CurrentSession!.IO.WriteLine(TestTurnFactory.Wire(marker));

        // 轮询消费：游戏循环可能先产出若干 turn，直到拿到 marker
        string? got = null;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await client.GetAsync("/turn");
            var status = (int)resp.StatusCode;
            if (status == 200)
            {
                got = await resp.Content.ReadAsStringAsync();
                if (got == marker)
                    break;
            }
            else if (status != 204)
            {
                break; // 404/500：直接失败
            }
            await Task.Delay(50);
        }

        Assert.Equal(marker, got);
    }

    // ---- POST /input ----

    [Fact]
    public async Task Input_returns_404_when_no_session()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };

        using var resp = await client.PostAsync("/input",
            new StringContent("{\"value\":\"x\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(404, (int)resp.StatusCode);
    }

    [Fact]
    public async Task Input_rejects_invalid_json_with_400()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        await LoadGameAsync(client);

        using var resp = await client.PostAsync("/input",
            new StringContent("not-json", Encoding.UTF8, "application/json"));

        Assert.Equal(400, (int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Invalid JSON", body);
    }

    [Fact]
    public async Task Input_rejects_missing_value_with_400()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        await LoadGameAsync(client);

        using var resp = await client.PostAsync("/input",
            new StringContent("{\"foo\":\"bar\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(400, (int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("Missing 'value' field", body);
    }

    [Fact]
    public async Task Input_accepts_value_with_200()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        await LoadGameAsync(client);

        using var resp = await client.PostAsync("/input",
            new StringContent("{\"value\":\"hi\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(200, (int)resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("received").GetBoolean());
    }

    // ---- 409 门禁（控制权冲突）----

    [Fact]
    public async Task Input_returns_409_when_control_held_by_agent()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        await LoadGameAsync(client);

        // agent 持有控制权（token 即身份）
        using (var acquire = await client.PostAsync("/control/acquire",
                   new StringContent("{\"token\":\"agent-a\"}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(200, (int)acquire.StatusCode);
        }

        // 其他 agent（token 不同）→ 409 CONTROL_HELD_BY_AGENT
        using (var denied = await client.PostAsync("/input",
                   new StringContent("{\"value\":\"x\",\"token\":\"agent-b\"}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(409, (int)denied.StatusCode);
            var body = await denied.Content.ReadAsStringAsync();
            Assert.Contains("CONTROL_HELD_BY_AGENT", body);
        }

        // 用户（无 token）同样被挡 → 409
        using (var deniedUser = await client.PostAsync("/input",
                   new StringContent("{\"value\":\"x\"}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(409, (int)deniedUser.StatusCode);
        }

        // 当前 agent 自己 → 200 received:true
        using (var ok = await client.PostAsync("/input",
                   new StringContent("{\"value\":\"x\",\"token\":\"agent-a\"}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(200, (int)ok.StatusCode);
            var body = await ok.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.True(doc.RootElement.GetProperty("received").GetBoolean());
        }
    }

    [Fact]
    public async Task Delete_session_returns_409_when_control_held_by_agent()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        await LoadGameAsync(client);

        using (var acquire = await client.PostAsync("/control/acquire",
                   new StringContent("{\"token\":\"agent-a\"}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(200, (int)acquire.StatusCode);
        }

        // 其他 agent 发起 DELETE /session → 409（生命周期门禁；DELETE 无 body，token 走 query）
        using var denied = await client.DeleteAsync("/session?token=agent-b");

        Assert.Equal(409, (int)denied.StatusCode);
        var body = await denied.Content.ReadAsStringAsync();
        Assert.Contains("CONTROL_HELD_BY_AGENT", body);

        // 当前 agent DELETE /session → 200 removed:true
        using var ok = await client.DeleteAsync("/session?token=agent-a");
        Assert.Equal(200, (int)ok.StatusCode);
    }

    // ---- GET /control/wait ----

    [Fact]
    public async Task Control_wait_returns_acquired_event()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };
        await LoadGameAsync(client);

        // 后台发起长轮询 /control/wait（阻塞至控制事件或 25s 超时）
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waitTask = client.GetAsync("/control/wait", cts.Token);

        // 等 waiter 注册（本地 HTTP 往返毫秒级），再触发 acquire 事件
        await Task.Delay(300);

        using (var acquire = await client.PostAsync("/control/acquire",
                   new StringContent("{\"token\":\"agent-a\"}", Encoding.UTF8, "application/json")))
        {
            Assert.Equal(200, (int)acquire.StatusCode);
        }

        using var waitResp = await waitTask;
        Assert.Equal(200, (int)waitResp.StatusCode);
        var body = await waitResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("acquired", doc.RootElement.GetProperty("event").GetString());
        Assert.Equal("held", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("agent", doc.RootElement.GetProperty("controller").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Control_wait_returns_404_when_no_session()
    {
        using var host = CreateHost();
        using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{host.Port}") };

        using var resp = await client.GetAsync("/control/wait");

        Assert.Equal(404, (int)resp.StatusCode);
    }
}
