using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EraCore.Maui.JsBridge;
using EraCore.Maui.Json;
using Microsoft.Maui.Storage;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;

namespace EraCore.Maui;

/// <summary>
/// MAUI 桥接编排器——issue 07 / 08 / 09 / 05（托管 server）/ spec ID8 / ID9 / ID10。
/// <para>
/// 持有 <see cref="IJsBridge"/> + 托管 <see cref="HttpListenerHost"/>（issue 05），在游戏会话与
/// UI 线程之间转发 turn / input：会话由 <see cref="HttpListenerHost"/> 的 <see cref="SessionRegistry"/> 持有
/// （共享层 <see cref="Session"/>），WebView 经 <see cref="OutputHub"/> 订阅 turn（<see cref="TurnPumpAsync"/>）
/// 转发 + <see cref="HttpSessionIO"/> 入输入；agent 经 localhost HTTP+WS 连接同一会话（单实例共享）。
/// 订阅 <see cref="IJsBridge.InputReceived"/> 接收 Vue 端 postMessage。
/// </para>
/// <para>
/// <b>启动时序（spec ID7 + issue 05）</b>：构造不启动托管 server。Vue 启动后 <c>postMessage({"type":"ready"})</c>，
/// <see cref="OnInputFromJs"/> 识别 ready 后调 <see cref="Start"/>；Start 建 <see cref="HttpListenerHost"/> +
/// <see cref="SessionRegistry.CreateMauiSessionAsync"/> 建共享会话 + 起 turn/control 泵 + 写 agent 发现记录。
/// Vue ready 是会话启动的前置条件，第一帧 turn（全量 diff）自然推给已 ready 的 Vue。
/// </para>
/// <para>
/// <b>致命错误处理（spec ID10）</b>：会话游戏循环异常由共享层 <see cref="Session"/> 兜底（写 error turn），
/// 本类仅在宿主构建/订阅失败时 <see cref="ShowFatalError"/> 推 error turn 给 Vue（Vue 渲染 error 字段），
/// fallback 用 <c>DisplayAlert</c>。单 step 脚本异常（ERB THROW / 除零等）由 <c>AgentJsonlProtocol.StepAsync</c>
/// 内 catch 处理，不传播到 <see cref="ShowFatalError"/>。
/// </para>
/// <para>
/// <b>文件选择器（issue 09）</b>：Vue 端 <c>pickGameFolder()</c> 投递 <c>{"type":"pickFolder"}</c> →
/// <see cref="HandlePickFolder"/> 调 <see cref="IJsBridge.PickFolderAsync"/> 弹原生选择器 →
/// 经 <see cref="IJsBridge.PostMessage"/> 推 <c>{"type":"folderPicked","path":...}</c> 回 Vue →
/// Vue 端 <c>loadGameFromPath(path)</c> 投递 <c>{"type":"loadGame","path":...}</c> →
/// <see cref="HandleLoadGame"/> 调 <c>_onReloadGame(path)</c> 让 <c>MainPage</c> 重建 BridgeHost。
/// </para>
/// </summary>
/// <remarks>
/// 生命周期：在 <c>MainPage</c> 构造时 new（不启动游戏循环），在 <c>MainPage.OnDisappearing</c> 或
/// <c>MainPage.RecreateHost</c>（issue 09 hot-swap reload）时 <see cref="Dispose"/>。
/// <see cref="ConfigData"/> / <see cref="ITerminalSetup"/> 单例由 <c>MauiProgram</c> DI 注入，
/// <c>MainPage</c> 重建时不重新初始化运行时；issue 09 hot-swap 时 <c>MainPage</c> 重新调
/// <c>EmueraRuntimeInitializer.Initialize</c> 替换字段后重建 BridgeHost。
/// </remarks>
internal sealed class BridgeHost : IDisposable
{
    /// <summary>Preferences key——主目录路径持久化（spec ID5）。</summary>
    private const string MainGameDirKey = "app.mainGameDir";

    /// <summary>
    /// Preferences key——文件日志（AgentLog）开关（A0，saf-accel 计划）。
    /// MauiProgram 启动早期读此 key 调 AgentLog.Configure（默认 false）；
    /// 设置页开关经 HandleSetAgentLogEnabled 写此 key + 运行时切换 AgentLog.Enabled。
    /// </summary>
    internal const string AgentLogEnabledKey = "app.agentLogEnabled";

    /// <summary>
    /// Preferences key——启动日志覆盖三态（DisplayReport override）。
    /// 值为 <c>"auto"</c>（跟随游戏配置，默认）/ <c>"off"</c>（强制关，隐藏启动读取日志）/
    /// <c>"on"</c>（强制开，逐文件报告 + time.log 阶段计时）。
    /// 设置页经 HandleSetDisplayReportOverride 写此 key + 即时切换 ConfigData.OverrideDisplayReport；
    /// 游戏加载（OnReloadGame → Initialize）时读此 key 前置位。
    /// </summary>
    internal const string DisplayReportOverrideKey = "app.displayReportOverride";

    /// <summary>
    /// 旧版 bool 开关 key（三态化前的 <c>app.noLoadingReport</c>）——仅作迁移读取：
    /// <c>true</c>（含缺省）→ <c>"off"</c>；<c>false</c> → <c>"auto"</c>。不再写入。
    /// </summary>
    internal const string NoLoadingReportKey = "app.noLoadingReport";

    /// <summary>
    /// Preferences key——退出游戏后主动 Full GC 开关（mem-leak-fix，默认 true）。
    /// 作用：host.Dispose 完成（游戏循环已 join、静态根已清理）后在后台线程触发
    /// GC.Collect(2) + WaitForPendingFinalizers + 二次收集，让已释放的游戏运行时内存
    /// 在任务管理器中可见回落（.NET GC 默认不把 Gen2 堆段归还 OS）。
    /// </summary>
    internal const string ActiveGcOnExitKey = "app.activeGcOnExit";

    /// <summary>三态字符串 ↔ <see cref="ConfigData.OverrideDisplayReport"/> 的映射（纯函数，单测覆盖）。</summary>
    internal static bool? ParseDisplayReportOverride(string? value) => value switch
    {
        "off" => false,
        "on" => true,
        _ => null, // "auto" / 未知值 / null 均为不覆盖
    };

    /// <summary>三态 → 协议字符串（config 消息 / Preferences 存储格式）。</summary>
    internal static string DisplayReportOverrideToString(bool? value) => value switch
    {
        false => "off",
        true => "on",
        _ => "auto",
    };

    /// <summary>app 内日志查看器推送 Vue 的内容上限（字符）——超限保留尾部最新（A0 补充，真机无 adb）。</summary>
    private const int AgentLogViewMaxChars = 200_000;

    private readonly IDispatcher _dispatcher;
    private readonly ConfigData _configData;
    private readonly ITerminalSetup _terminalSetup;
    private readonly IJsBridge _jsBridge;
#if ANDROID
    // mem-leak-fix：缓存 OnInputFromJs 委托实例——构造时赋给 AndroidJsBridge.FallbackInputHandler，
    // Dispose 时用同一实例做 ReferenceEquals 比较（方法组每次转委托新建实例，直接 ReferenceEquals
    // 方法组恒为 false，守卫失效且触发 CS8974）。
    private readonly Action<string> _onInputFromJsDelegate;
#endif
    private readonly Action<string>? _onReloadGame;
    private readonly Action? _onGameExited;
    private readonly CancellationTokenSource _cts = new();
    // issue 05：MAUI 托管 Server——共享层会话由 HttpListenerHost 的 SessionRegistry 持有，
    // WebView 经 OutputHub 订阅 turn（TurnPumpAsync）投递 + session.IO 入输入；
    // agent 经 localhost HTTP+WS 连接同一会话（单实例共享，非另起一局）。
    private HttpListenerHost? _serverHost;
    private Session? _session;
    private HttpSessionIO? _sessionIO;
    private Task? _turnPump;
    private Task? _controlPump;
    private string? _discoveryPath;
    private string? _discoveryToken;
    private bool _disposed;
    private bool _started;
    private bool _readyReceived;

    /// <summary>
    /// 当前主目录路径——game-library spec ID4 / ID5。
    /// <para>
    /// 构造时从 <see cref="Preferences"/> 读取（key=<see cref="MainGameDirKey"/>）；无值时按平台回退：
    /// Windows <c>Documents/emuera</c>，Android <c>null</c>（待权限引导后设置）。
    /// </para>
    /// <para>
    /// <see cref="HandleScanGames"/> 收到非空 rootDir 时更新此字段 + 写 Preferences，
    /// 保证 Vue 端与 C# 端主目录一致。
    /// </para>
    /// </summary>
    private string? _mainGameDir;

    /// <summary>
    /// 构造桥接宿主——不启动游戏循环（spec ID7 延迟启动）。
    /// <para>
    /// <b>Spec 偏离说明</b>：spec ID8 伪代码签名含 <c>webView</c> 参数，实现省略——
    /// <c>IJsBridge.Attach(webView)</c> 由 <see cref="MainPage"/> 在 <c>HandlerChanged</c> UI 事件内直接调
    /// （WebView 平台原生视图就绪的最早可靠时机，UI 线程约束），<see cref="BridgeHost"/> 仅编排 turn/input 流转，
    /// 不持 <c>webView</c> 引用避免跨线程访问风险。
    /// </para>
    /// <para>
    /// <b>可访问性</b>：ctor 标 <c>internal</c>——参数类型 <see cref="ConfigData"/> / <see cref="ITerminalSetup"/>
    /// 在 <c>Emuera.Headless.Core</c> 内为 <c>internal</c>，C# 规则要求方法可访问性不得高于参数类型
    /// （与 <see cref="MainPage"/> ctor 一致）。
    /// </para>
    /// <para>
    /// <b>issue 09 reload 回调</b>：<paramref name="onReloadGame"/> 由 <see cref="MainPage"/> 提供——
    /// <see cref="HandleLoadGame"/> 收到 <c>{"type":"loadGame","path":...}</c> 时调此回调，
    /// 让 MainPage 重新初始化运行时 + 重建 BridgeHost（hot-swap reload）。
    /// </para>
    /// </summary>
    /// <param name="dispatcher">MAUI <see cref="IDispatcher"/>——用于把游戏循环线程的 turn 回调切到 UI 线程调 <see cref="IJsBridge.PostTurn"/>。</param>
    /// <param name="configData">已加载的 <see cref="ConfigData"/> 单例（<c>MauiProgram</c> 初始化时加载）。</param>
    /// <param name="terminalSetup">已启用 ANSI 的 <see cref="ITerminalSetup"/> 单例。</param>
    /// <param name="jsBridge">平台 <see cref="IJsBridge"/> 实现（Windows / Android）。</param>
    /// <param name="onReloadGame">issue 09 hot-swap reload 回调——传游戏目录绝对路径让 MainPage 重建 BridgeHost。</param>
    /// <param name="onGameExited">game-library spec ID10 退出游戏回调——BridgeHost 处理 <c>exitGame</c>
    /// 后调此回调让 MainPage 重建 BridgeHost（新 host 不启动游戏循环，等 Vue 触发 scanGames）。</param>
    internal BridgeHost(
        IDispatcher dispatcher,
        ConfigData configData,
        ITerminalSetup terminalSetup,
        IJsBridge jsBridge,
        Action<string>? onReloadGame = null,
        Action? onGameExited = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _configData = configData ?? throw new ArgumentNullException(nameof(configData));
        _terminalSetup = terminalSetup ?? throw new ArgumentNullException(nameof(terminalSetup));
        _jsBridge = jsBridge ?? throw new ArgumentNullException(nameof(jsBridge));
        _onReloadGame = onReloadGame;
        _onGameExited = onGameExited;

        // game-library spec ID5：从 Preferences 读 mainGameDir——无值时按平台回退
        // Windows: Documents/emuera；Android: null（待权限引导后设置）
        _mainGameDir = LoadMainGameDir();

        // 托管模式（issue 05）：会话由 HttpListenerHost 的 SessionRegistry 持有，
        // WebView 的 turn 流由 Start 里起的 TurnPumpAsync 从 OutputHub 订阅读取后
        // Dispatcher.Dispatch 切 UI 线程投递——不再用 MauiBridgeIO 直连游戏循环。
        _jsBridge.InputReceived += OnInputFromJs;
#if ANDROID
        // ADR-0019：兜底——InputReceived 无订阅者时仍能收到 JS 消息。
        // 缓存同一委托实例供 Dispose 时 ReferenceEquals 比较（方法组每次转委托新建实例，
        // 直接对方法组 ReferenceEquals 恒为 false——守卫失效 + CS8974）。
        _onInputFromJsDelegate = OnInputFromJs;
        AndroidJsBridge.FallbackInputHandler = _onInputFromJsDelegate;
        // ADR-0019：BridgeClient URL 拦截——可靠 JS→C# 备份通道
        AndroidJsBridge.BridgeUrlReceived += OnBridgeUrl;
#endif
    }

    /// <summary>
    /// 当前主目录路径——game-library spec ID4。
    /// Vue 端首次启动时通过 <c>mainDir</c> 消息接收此值；用户更改主目录后通过 <c>scanGames</c> 更新。
    /// </summary>
    public string? MainGameDir => _mainGameDir;

    /// <summary>
    /// game-library spec ID10：游戏循环是否正在运行——MainPage 据此决定 Android 物理返回键行为。
    /// <c>true</c> 表示游戏循环已启动且未 Dispose。
    /// </summary>
    public bool IsGameRunning => _started && !_disposed;

    /// <summary>
    /// 从 Preferences 加载 mainGameDir——无值时按平台回退（spec ID5）。
    /// <para>
    /// 平台默认：
    /// <list type="bullet">
    ///   <item>Windows：<c>Documents/emuera</c></item>
    ///   <item>Android：<c>null</c>（首启动待权限引导后由 <c>PermissionGuidePage</c> 写入默认值）</item>
    /// </list>
    /// </para>
    /// </summary>
    private static string? LoadMainGameDir()
    {
        try
        {
            var stored = Preferences.Get(MainGameDirKey, null);
            if (!string.IsNullOrEmpty(stored))
                return stored;
        }
        catch
        {
            // Preferences 不可用（极少见）——回退到平台默认
        }
#if WINDOWS
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "emuera");
#else
        return null;
#endif
    }

    /// <summary>
    /// 持久化 mainGameDir 到 Preferences——spec ID5。
    /// 失败时静默（Preferences 不可用），_mainGameDir 字段仍更新。
    /// </summary>
    private void SaveMainGameDir(string dir)
    {
        try
        {
            Preferences.Set(MainGameDirKey, dir);
        }
        catch
        {
            // 静默——字段已更新，仅持久化失败
        }
    }

    /// <summary>
    /// turn 泵回调——<see cref="TurnPumpAsync"/> 从 OutputHub 订阅 reader 读到 turn 后调用。
    /// <para>
    /// <see cref="IJsBridge.PostTurn"/> 内部调 <c>EvaluateJavaScriptAsync</c> / <c>EvaluateJavaScript</c>，
    /// 必须在 UI 线程执行（CoreWebView2 / Android.Webkit.WebView 要求）。
    /// <see cref="IDispatcher.Dispatch"/> 是 fire-and-forget——不阻塞 turn 泵读取。
    /// </para>
    /// </summary>
    private void OnTurnFromGame(string turnJson)
    {
        _dispatcher.Dispatch(() => _jsBridge.PostTurn(turnJson));
    }

    /// <summary>
    /// JS → C# 消息处理——<see cref="IJsBridge.InputReceived"/> 触发。
    /// <para>
    /// 识别四类消息：
    /// <list type="bullet">
    ///   <item><c>{"type":"ready"}</c>——首帧 ready 信号，调 <see cref="Start"/> 启动游戏循环（仅一次，幂等）</item>
    ///   <item><c>{"type":"pickFolder"}</c>——issue 09 文件选择器，调 <see cref="HandlePickFolder"/> 弹原生选择器</item>
    ///   <item><c>{"type":"loadGame","path":...}</c>——issue 09 hot-swap reload，调 <see cref="HandleLoadGame"/> 通知 MainPage 重建</item>
    ///   <item>game-library spec ID3：<c>{"type":"scanGames","rootDir":...}</c> /
    ///       <c>{"type":"listDirectories","dirPath":...}</c> / <c>{"type":"exitGame"}</c>——
    ///       分别调 <see cref="HandleScanGames"/> / <see cref="HandleListDirectories"/> / <see cref="HandleExitGame"/></item>
    ///   <item><c>{"type":"getGameThreadStatus"}</c>——前端静默探测游戏线程存活，调 <see cref="HandleGetGameThreadStatus"/></item>
    ///   <item><c>{"type":"control","action":...}</c>——issue 05 托管控制权桥接（acquire/release/status），
    ///       调 <see cref="HandleControlMessage"/>，让 WebView 用户能接管 agent 控制权</item>
    ///   <item>其他（如 <c>{"type":"input","value":"..."}</c>）——经控制门禁入 <see cref="HttpSessionIO.EnqueueInput"/>，
    ///       由 <c>AgentJsonlProtocol.RunLoopAsync</c> 反序列化消费</item>
    /// </list>
    /// </para>
    /// <para>
    /// 非 JSON 消息静默吞掉（仅写日志）——Vue 端 <c>postInput</c> 总发合法 JSON，
    /// 到此分支说明桥接层异常，但不阻塞游戏循环。
    /// </para>
    /// </summary>
    /// <summary>ADR-0019：BridgeClient 备用通道。</summary>
    private void OnBridgeUrl(string data)
    {
        if (_disposed) return;
        // bridge://post?msg=... → data 是已解码的 JSON 字符串
        // bridge://pickSafDirectory → data 是简单 action 名
        if (data.StartsWith('{'))
            OnInputFromJs(data);
        else
            OnInputFromJs($"{{\"type\":\"{data}\"}}");
    }

    private void OnInputFromJs(string message)
    {
        // 三通道日志：Console（dotnet run 终端）+ AgentLog（持久化文件）+ Debug.WriteLine（VS 调试器）
        Console.WriteLine($"[bridge] OnInputFromJs: {message}");
        if (_disposed)
        {
            // Dispose 后到达的消息丢弃——游戏循环已取消 / IO 已关闭
            AgentLog.Instance.Write($"[bridge] input after dispose dropped: {message}");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(message);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("type", out var typeEl)
                && typeEl.ValueKind == JsonValueKind.String)
            {
                var type = typeEl.GetString();
                if (type == "ready")
                {
                    HandleReady();
                    return;
                }
                if (type == "pickFolder")
                {
                    HandlePickFolder();
                    return;
                }
                if (type == "pickSafDirectory")
                {
                    HandlePickSafDirectory();
                    return;
                }
                if (type == "loadGame")
                {
                    HandleLoadGame(doc.RootElement);
                    return;
                }
                // game-library spec ID3：游戏列表扫描 / 目录列举 / 退出游戏——
                // 这些消息可在游戏循环未启动时处理（Vue 启动后立即 scanGames 填充列表）
                if (type == "scanGames")
                {
                    HandleScanGames(doc.RootElement);
                    return;
                }
                if (type == "listDirectories")
                {
                    HandleListDirectories(doc.RootElement);
                    return;
                }
                if (type == "exitGame")
                {
                    HandleExitGame();
                    return;
                }
                // game-library spec ID6：Android 存储权限检查与请求
                if (type == "checkPermission")
                {
                    HandleCheckPermission();
                    return;
                }
                if (type == "requestPermission")
                {
                    HandleRequestPermission();
                    return;
                }
                if (type == "getGameThreadStatus")
                {
                    HandleGetGameThreadStatus();
                    return;
                }
                // A0：设置页文件日志开关——写 Preferences + 运行时切换 AgentLog.Enabled
                if (type == "setAgentLogEnabled")
                {
                    HandleSetAgentLogEnabled(doc.RootElement);
                    return;
                }
                // 设置页启动日志覆盖三态——写 Preferences + 切换 ConfigData.OverrideDisplayReport
                if (type == "setDisplayReportOverride")
                {
                    HandleSetDisplayReportOverride(doc.RootElement);
                    return;
                }
                // A0 补充：app 内日志查看器——读取 agent.log 内容推给 Vue（真机无 adb 场景）
                if (type == "getAgentLog")
                {
                    HandleGetAgentLog();
                    return;
                }
                // A0 补充：导出 agent.log——FileProvider 分享给系统面板（绕开 WebView 剪贴板限制）
                if (type == "exportAgentLog")
                {
                    HandleExportAgentLog();
                    return;
                }
                // issue 05：MAUI 托管控制权桥接——用户经 WebView 接管/让权/查询控制状态
                if (type == "control")
                {
                    HandleControlMessage(doc.RootElement);
                    return;
                }
                // 其他 typed 消息（input 等）原样入队——AgentJsonlProtocol.RunLoopAsync 内
                // JsonSerializer.Deserialize<JsonlCommand> 校验 type=="input" 后取 value。
                // 不在此处解 value 字段，避免与 protocol 层重复解析 / 不一致。
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[bridge] non-JSON input received (ignored): {ex.Message}");
            AgentLog.Instance.Write($"[bridge] non-JSON input received (ignored): {ex.Message}");
            return;
        }

        // input / 其他 typed 消息：经控制门禁入 session 输入队列（WebView = 用户身份，无 token）。
        // 未起会话（无游戏）时丢弃——与旧 MauiBridgeIO 关闭后丢弃语义一致。
        if (_session is { HasEnded: false } s)
        {
            var gate = s.Controller.CheckInput(ControlIdentity.User, s.HasEnded);
            if (gate.Status == ControlGateStatus.Allowed)
            {
                s.IO.EnqueueInput(message);
            }
            else
            {
                Console.WriteLine($"[bridge] input dropped (control held: {gate.Reason}): {message}");
                // 控制权被 agent 持有时同步一次状态——前端输入栏应已禁用（旁观态），此为兜底
                PushControlStatus(s);
            }
        }
    }

    /// <summary>
    /// 处理 Vue ready 信号——首帧时调 <see cref="Start"/>，重复 ready 幂等忽略。
    /// <para>
    /// <b>无游戏启动（2026-08-06 移除 test_game 打包后）</b>：启动期不再 Resolve 游戏目录，
    /// <see cref="GamePaths.Current"/> 为 null——ready 后不 Start（无游戏可跑），
    /// 等用户经游戏列表 / 目录选择器选游戏（<c>loadGame</c> → <c>MainPage.OnReloadGame</c>
    /// 重建 host + Start）。若未来恢复「默认游戏自动启动」，把 null 守卫改为对默认目录 Resolve。
    /// </para>
    /// </summary>
    private void HandleReady()
    {
        if (_readyReceived)
        {
            Console.WriteLine("[bridge] duplicate ready signal ignored");
            return;
        }
        _readyReceived = true;
        Console.WriteLine("[bridge] Vue ready signal received");
        AgentLog.Instance.Write("[bridge] Vue ready signal received");
        System.Diagnostics.Debug.WriteLine("[bridge] Vue ready signal received");
        if (GamePaths.Current == null)
        {
            Console.WriteLine("[bridge] no game loaded at startup (no-game-start), waiting for loadGame");
            return;
        }
        Start();
    }

    /// <summary>
    /// 弹出原生文件夹选择器（issue 09）——UI 线程调用，结果异步推回 Vue。
    /// <para>
    /// 调 <see cref="IJsBridge.PickFolderAsync"/>（MAUI <c>FolderPicker.PickAsync</c>）弹原生选择器，
    /// 用户选中后经 <see cref="IJsBridge.PostMessage"/> 投递 <c>{"type":"folderPicked","path":...}</c> 回 Vue。
    /// 用户取消（path 为 null）静默 no-op，不发 folderPicked 消息——Vue 端 picking 状态由点击立即重置。
    /// </para>
    /// <para>
    /// <b>async void</b>：UI 事件回调常见模式——异常在 try/catch 内吞掉，不让 async void 异常逃逸到 SynchronizationContext。
    /// </para>
    /// </summary>
    private async void HandlePickFolder()
    {
        Console.WriteLine("[bridge] HandlePickFolder: opening native folder picker");
        AgentLog.Instance.Write("[bridge] HandlePickFolder: opening native folder picker");
        try
        {
            var path = await _jsBridge.PickFolderAsync();
            if (string.IsNullOrEmpty(path))
            {
                // 用户取消——不推 folderPicked，Vue 端 picking 状态由点击立即重置
                Console.WriteLine("[bridge] HandlePickFolder: user cancelled or path empty");
                return;
            }
            Console.WriteLine($"[bridge] HandlePickFolder: picked path={path}");
            var msgJson = JsonSerializer.Serialize(
                new FolderPickedMessage("folderPicked", path), MauiJsonContext.Default.FolderPickedMessage);
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandlePickFolder failed: {ex}");
            AgentLog.Instance.Write($"[bridge] HandlePickFolder failed: {ex}");
            // 推 error 事件给 Vue 让 UI 解除 picking 状态 + 显示错误
            var errJson = JsonSerializer.Serialize(
                new FolderPickedError("folderPicked", ex.Message), MauiJsonContext.Default.FolderPickedError);
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(errJson));
        }
    }

    /// <summary>
    /// ADR-0019：处理 Vue 端 pickSafDirectory 消息——Android SAF 原生目录选择器。
    /// 通过 <see cref="IGameDirAccessor.PickDirectoryAsync"/> 弹出系统原生目录选择器。
    /// </summary>
    private async void HandlePickSafDirectory()
    {
        Console.WriteLine("[bridge] HandlePickSafDirectory");
        AgentLog.Instance.Write("[bridge] HandlePickSafDirectory");
        try
        {
#if ANDROID
            var dirAccessor = (IGameDirAccessor?)SafGameDirAccessor.Instance;
#else
            IGameDirAccessor? dirAccessor = null;
#endif
            if (dirAccessor == null)
            {
                Console.WriteLine("[bridge] HandlePickSafDirectory: no DirAccessor");
#if ANDROID
                var errJson = JsonSerializer.Serialize(
                    new SafDirectoryPickedError("safDirectoryPicked", "DirAccessor is null — SafGameDirAccessor not initialized"),
                    MauiJsonContext.Default.SafDirectoryPickedError);
#else
                var errJson = JsonSerializer.Serialize(
                    new SafDirectoryPickedError("safDirectoryPicked", "SAF not supported on this platform"),
                    MauiJsonContext.Default.SafDirectoryPickedError);
#endif
                _dispatcher.Dispatch(() => _jsBridge.PostMessage(errJson));
                return;
            }

            var result = await dirAccessor.PickDirectoryAsync();
            if (result == null)
            {
                var cancelJson = JsonSerializer.Serialize(
                    new SafDirectoryPickedCancelled("safDirectoryPicked", true),
                    MauiJsonContext.Default.SafDirectoryPickedCancelled);
                _dispatcher.Dispatch(() => _jsBridge.PostMessage(cancelJson));
                return;
            }

            Console.WriteLine($"[bridge] HandlePickSafDirectory: picked={result}");
            // 更新主目录 + 持久化
            _mainGameDir = result;
            SaveMainGameDir(result);

            // 方案 B / B1：写权限 + 探针（logcat 验收；缺写时提示重选）
            var hasWrite = dirAccessor.HasWriteAccess();
            var writeProbeOk = false;
            var writeProbeDetail = "";
#if ANDROID
            if (dirAccessor is SafGameDirAccessor saf)
            {
                writeProbeOk = saf.TryWriteProbe(out writeProbeDetail);
            }
#endif
            Console.WriteLine(
                $"[bridge] HandlePickSafDirectory: hasWrite={hasWrite}, writeProbeOk={writeProbeOk}, detail={writeProbeDetail}");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui",
                $"safDirectoryPicked hasWrite={hasWrite} writeProbeOk={writeProbeOk} detail={writeProbeDetail}");
#endif
            if (!hasWrite || !writeProbeOk)
            {
                var needRepick = JsonSerializer.Serialize(
                    new SafDirectoryPickedRepick(
                        Type: "safDirectoryPicked",
                        Path: result,
                        HasWrite: hasWrite,
                        WriteProbeOk: writeProbeOk,
                        WriteProbeDetail: writeProbeDetail,
                        Error: hasWrite
                            ? $"Write probe failed: {writeProbeDetail}"
                            : "No write permission on selected folder. Please re-select the game directory and allow access.",
                        NeedRepickForWrite: true),
                    MauiJsonContext.Default.SafDirectoryPickedRepick);
                _dispatcher.Dispatch(() => _jsBridge.PostMessage(needRepick));
                // 仍继续扫描——读权限可能足够浏览；存档会再失败并提示
            }

            // 直接扫描并推送 gamesScanned——避免 JS→C# 走不可靠的 bridge
            ScanAndPushGames(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandlePickSafDirectory failed: {ex}");
            var errJson = JsonSerializer.Serialize(
                new SafDirectoryPickedError("safDirectoryPicked", ex.Message),
                MauiJsonContext.Default.SafDirectoryPickedError);
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(errJson));
        }
    }

    /// <summary>
    /// ADR-0019：直接扫描 rootDir 并推送 gamesScanned 到 Vue。
    /// 绕过 JS→C# scanGames 消息链（bridge 不可靠）。
    /// </summary>
    private void ScanAndPushGames(string? rootDir)
    {
        var dirAccessor = ResolveDirAccessor(rootDir);
        bool rootDirExists = GameScanner.RootDirectoryExists(rootDir, dirAccessor);
        var games = GameScanner.Scan(rootDir, dirAccessor);
        var gamesPayload = games.Select(g => new GameInfoDto(g.Name, g.FullPath)).ToList();
        var msgJson = JsonSerializer.Serialize(
            new GamesScannedMessage("gamesScanned", gamesPayload, rootDir, rootDirExists),
            MauiJsonContext.Default.GamesScannedMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
        Console.WriteLine($"[bridge] ScanAndPushGames: {gamesPayload.Count} games, rootDir={rootDir}");
    }

    /// <summary>
    /// 按主目录路径选择正确的文件访问器。
    /// Android SAF 的 <c>content://</c> URI 必须使用持久化的 SAF accessor；
    /// 传统路径则使用当前游戏 accessor（或本地文件系统 fallback）。
    /// </summary>
    private static IGameDirAccessor ResolveDirAccessor(string? rootDir)
    {
        if (rootDir?.StartsWith("content://", StringComparison.Ordinal) == true)
        {
#if ANDROID
            if (SafGameDirAccessor.Instance is { } safAccessor)
                return safAccessor;
#endif
        }

        return GamePaths.Current?.DirAccessor ?? new FileSystemGameDirAccessor();
    }

    /// <summary>
    /// 处理 Vue 端 scanGames 消息（game-library spec ID3 / ID4）——
    /// 扫描 rootDir 下的游戏列表并回复 <c>gamesScanned</c>。
    /// <para>
    /// 流程：
    /// <list type="number">
    ///   <item>读 <c>msg.rootDir</c>——若未提供则用 <see cref="_mainGameDir"/></item>
    ///   <item>若 rootDir 非空且与 <see cref="_mainGameDir"/> 不同——更新 <see cref="_mainGameDir"/> + 写 Preferences</item>
    ///   <item>调 <see cref="GameScanner.Scan"/> 扫描</item>
    ///   <item>回复 <c>{"type":"gamesScanned","games":[{name,fullPath}],"rootDir":...}</c></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>rootDir 为空时的处理</b>：若 <see cref="_mainGameDir"/> 也为 null（Android 首启动未授权），
    /// 回复空 games + null rootDir——Vue 端据此显示「请选择主目录」UI。
    /// </para>
    /// </summary>
    private void HandleScanGames(JsonElement root)
    {
        string? rootDir = null;
        if (root.TryGetProperty("rootDir", out var rootEl) && rootEl.ValueKind == JsonValueKind.String)
        {
            rootDir = rootEl.GetString();
        }
        if (string.IsNullOrEmpty(rootDir))
            rootDir = _mainGameDir;
        else if (rootDir != _mainGameDir)
        {
            // 用户更改主目录——更新字段 + 持久化
            _mainGameDir = rootDir;
            SaveMainGameDir(rootDir!);
            Console.WriteLine($"[bridge] mainGameDir updated to: {rootDir}");
        }

        Console.WriteLine($"[bridge] HandleScanGames: rootDir={rootDir}");
        // game-library spec ID13：检查主目录是否存在——Vue 端区分「目录不存在」vs「无游戏」
        var dirAccessor = ResolveDirAccessor(rootDir);
        bool rootDirExists = GameScanner.RootDirectoryExists(rootDir, dirAccessor);
        var games = GameScanner.Scan(rootDir, dirAccessor);
        var gamesPayload = games.Select(g => new GameInfoDto(g.Name, g.FullPath)).ToList();
        var msgJson = JsonSerializer.Serialize(
            new GamesScannedMessage("gamesScanned", gamesPayload, rootDir, rootDirExists),
            MauiJsonContext.Default.GamesScannedMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
        // A0 补充（code-review 修复）：Vue 启动必发 scanGames——此处同步推 config，
        // 让「重启 app 未进游戏」时设置页开关也能显示 Preferences 持久化的权威状态
        // （Start() 的 PushConfigMessage 仅在进游戏后才触发）。
        PushConfigMessage();
    }

    /// <summary>
    /// 处理 Vue 端 listDirectories 消息（game-library spec ID3 / ID8）——
    /// 列举 dirPath 下的子目录并回复 <c>directoriesListed</c>，用于 Android 目录浏览器弹窗。
    /// <para>
    /// 回复格式：<c>{"type":"directoriesListed","currentPath":...,"parentPath":...|"null","subDirectories":[...]}</c>。
    /// dirPath 缺失时使用 <see cref="_mainGameDir"/>（若也为 null 则回复空结果）。
    /// </para>
    /// </summary>
    private void HandleListDirectories(JsonElement root)
    {
        string? dirPath = null;
        if (root.TryGetProperty("dirPath", out var dirEl) && dirEl.ValueKind == JsonValueKind.String)
        {
            dirPath = dirEl.GetString();
        }
        if (string.IsNullOrEmpty(dirPath))
            dirPath = _mainGameDir;

        Console.WriteLine($"[bridge] HandleListDirectories: dirPath={dirPath}");
        var result = DirectoryLister.ListDirectories(dirPath, ResolveDirAccessor(dirPath));
        var msgJson = JsonSerializer.Serialize(
            new DirectoriesListedMessage("directoriesListed", result.CurrentPath, result.ParentPath, result.SubDirectories),
            MauiJsonContext.Default.DirectoriesListedMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
    }

    /// <summary>
    /// 处理 Vue 端 exitGame 消息（game-library spec ID3 / ID10）——
    /// 退出当前游戏并回复 <c>gameExited</c>，让 Vue 回到列表态。
    /// <para>
    /// 流程：
    /// <list type="number">
    ///   <item>回复 <c>{"type":"gameExited"}</c> 给 Vue——必须先回复再 Dispose，
    ///       Dispose 后 OnInputFromJs 守卫会丢消息</item>
    ///   <item>调 <see cref="Dispose"/>——取消 turn/control 泵 + 停托管 server（会话随之结束）</item>
    ///   <item>调 <c>_onGameExited?.Invoke()</c>——让 MainPage 重建 BridgeHost
    ///       （新 host 不启动游戏循环，等 Vue 触发 scanGames）</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>时序保证</b>：PostMessage 用 <see cref="IDispatcher.Dispatch"/> 异步派发到 UI 线程，
    /// 此方法返回后 UI 线程会依次执行：投递 gameExited → 重建 host。
    /// Vue 端收到 gameExited 后投递 scanGames，此时新 host 已就绪可接收。
    /// </para>
    /// </summary>
    private void HandleExitGame()
    {
        Console.WriteLine("[bridge] HandleExitGame: disposing game loop and notifying Vue");
        AgentLog.Instance.Write("[bridge] HandleExitGame: disposing game loop and notifying Vue");

        // 1. 先回复 gameExited——Dispatch 异步派发到 UI 线程队列
        var msgJson = JsonSerializer.Serialize(new GameExitedMessage("gameExited"), MauiJsonContext.Default.GameExitedMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));

        // 2. 调回调让 MainPage 重建 BridgeHost——RecreateHost 内会 Dispose 当前 host
        //    Dispose 后 OnInputFromJs 守卫 _disposed=true，新 host 接管后续消息
        try
        {
            _onGameExited?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandleExitGame: _onGameExited threw: {ex}");
            AgentLog.Instance.Write($"[bridge] HandleExitGame: _onGameExited threw: {ex}");
        }

        // 3. 若未提供 _onGameExited 回调（旧调用方）——直接 Dispose 让游戏循环停止
        if (_onGameExited is null)
        {
            Dispose();
        }
    }

    /// <summary>
    /// game-library spec ID6：检查 Android MANAGE_EXTERNAL_STORAGE 权限状态——回复 Vue。
    /// <para>
    /// Android 平台检查 <see cref="Android.OS.Environment.IsExternalStorageManager"/>；
    /// 非 Android 平台（Windows）自动视为已授权（<c>granted: true</c>）。
    /// 回复消息：<c>{"type":"permissionStatus","granted":true/false}</c>
    /// </para>
    /// </summary>
    private void HandleCheckPermission()
    {
        bool granted;
#if ANDROID
        // ADR-0019：SAF 替代了 MANAGE_EXTERNAL_STORAGE，不需要存储权限。
        // 始终返 granted=true，让 Vue 跳过 PermissionGuide 直接进入游戏列表。
        granted = true;
#else
        // Windows MAUI 不检查 Android 权限——默认视为已授权
        granted = true;
#endif
        var msg = JsonSerializer.Serialize(
            new PermissionStatusMessage("permissionStatus", granted),
            MauiJsonContext.Default.PermissionStatusMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
        Console.WriteLine($"[bridge] HandleCheckPermission: granted={granted}");
        AgentLog.Instance.Write($"[bridge] HandleCheckPermission: granted={granted}");
    }

    /// <summary>
    /// 前端探测游戏线程存活状态——<c>{"type":"getGameThreadStatus"}</c> 查询回复。
    /// <para>
    /// 前端在「静默超时」（长时间无 turn 帧且不在等待输入）时探测一次，据此区分
    /// 「游戏运行中」（线程存活，慢计算/死循环）与「游戏已停止」（线程死亡）两种静默。
    /// 本方法在 JS 桥线程（UI 线程）直接读 <see cref="Task.IsCompleted"/>——不依赖游戏线程
    /// 配合，脚本卡死时也能准确应答。
    /// </para>
    /// <para>
    /// 回复 <c>{"type":"gameThreadStatus","alive":true/false}</c>。
    /// 游戏未启动（<c>_session</c> 为 null / 已结束）时视为不存活——前端无游戏时不探测，不会误显示。
    /// </para>
    /// </summary>
    private void HandleGetGameThreadStatus()
    {
        var alive = _session is { IsRunning: true };
        var msg = JsonSerializer.Serialize(
            new GameThreadStatusMessage("gameThreadStatus", alive),
            MauiJsonContext.Default.GameThreadStatusMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
        Console.WriteLine($"[bridge] HandleGetGameThreadStatus: alive={alive}");
        AgentLog.Instance.Write($"[bridge] HandleGetGameThreadStatus: alive={alive}");
    }

    /// <summary>
    /// A0（saf-accel 计划）：设置页「文件日志」开关——写 Preferences + 运行时切换 AgentLog.Enabled。
    /// <para>
    /// 接收 <c>{"type":"setAgentLogEnabled","enabled":true/false}</c>；持久化到 Preferences
    /// （MauiProgram 启动早期读同一 key 决定 AgentLog 初值），再切换 AgentLog.Enabled 即时生效
    /// （无需重启）。完成后推回 <c>config</c> 消息（含 agentLogEnabled 字段）让设置页与 C# 权威
    /// 状态同步——复用 handleMauiMessage 的 <c>type==='config'</c> 消费链。
    /// </para>
    /// </summary>
    private void HandleSetAgentLogEnabled(JsonElement root)
    {
        if (!root.TryGetProperty("enabled", out var el)
            || (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
        {
            Console.WriteLine("[bridge] HandleSetAgentLogEnabled: missing or non-boolean 'enabled' field");
            return;
        }
        var enabled = el.GetBoolean();
        try
        {
            Preferences.Set(AgentLogEnabledKey, enabled);
        }
        catch
        {
            // Preferences 不可用——运行时切换仍生效，仅持久化失败（下次启动回默认）
        }
        AgentLog.Instance.Enabled = enabled;
        Console.WriteLine($"[bridge] agentLogEnabled set to {enabled}");
        // 推回 config 消息同步 UI——设置页开关显示与 C# 权威状态一致
        PushConfigMessage();
    }

    /// <summary>
    /// 接收 <c>{"type":"setDisplayReportOverride","value":"auto"|"off"|"on"}</c>；持久化到
    /// Preferences（游戏加载时 OnReloadGame → Initialize 读同一 key 前置位），并即时切换
    /// <see cref="ConfigData.OverrideDisplayReport"/>——off/on 直接压 DisplayReport；
    /// auto 置 null 后 ReLoadConfig 恢复游戏自身配置（三源重读，含 fixed.config 强制值）。
    /// 完成后推回 <c>config</c> 消息同步 UI。
    /// <para>
    /// 说明：即时切换只影响后续读取 <c>DisplayReport</c> 的输出；当前会话已打印的启动日志
    /// 不会回滚（真正生效发生在游戏加载时）。
    /// </para>
    /// </summary>
    private void HandleSetDisplayReportOverride(JsonElement root)
    {
        if (root.TryGetProperty("value", out var el) && el.ValueKind == JsonValueKind.String)
        {
            var value = el.GetString();
            if (value is not ("auto" or "off" or "on"))
            {
                Console.WriteLine($"[bridge] HandleSetDisplayReportOverride: invalid value '{value}'");
                return;
            }
            try
            {
                Preferences.Set(DisplayReportOverrideKey, value);
            }
            catch
            {
                // Preferences 不可用——运行时切换仍生效，仅持久化失败（下次启动回默认）
            }
            _configData.OverrideDisplayReport = ParseDisplayReportOverride(value);
            if (_configData.OverrideDisplayReport.HasValue)
                _configData.GetItem(ConfigCode.DisplayReport).SetValue(_configData.OverrideDisplayReport.Value);
            else
                _configData.ReLoadConfig();
            Console.WriteLine($"[bridge] displayReportOverride set to {value}");
            PushConfigMessage();
            return;
        }
        Console.WriteLine("[bridge] HandleSetDisplayReportOverride: missing or non-string 'value' field");
    }

    /// <summary>
    /// A0 补充（真机无 adb）：app 内日志查看器——读取 agent.log 内容推给 Vue。
    /// <para>
    /// 接收 <c>{"type":"getAgentLog"}</c>；调 <see cref="AgentLog.ReadAllText"/>（内部 flush，
    /// 无需退出进程即可拿到最新日志）。内容超过 <see cref="AgentLogViewMaxChars"/> 时截断为
    /// 尾部（保留最新，取证场景最新行最有价值），回复
    /// <c>{"type":"agentLog","content":...,"truncated":true/false}</c>。未启用/无文件时
    /// content 为空串、truncated=false。
    /// </para>
    /// </summary>
    private void HandleGetAgentLog()
    {
        var text = AgentLog.Instance.ReadAllText();
        if (string.IsNullOrEmpty(text))
        {
            var emptyJson = JsonSerializer.Serialize(
                new AgentLogMessage("agentLog", "", false), MauiJsonContext.Default.AgentLogMessage);
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(emptyJson));
            return;
        }
        var truncated = text.Length > AgentLogViewMaxChars;
        var content = truncated ? text.Substring(text.Length - AgentLogViewMaxChars) : text;
        var msgJson = JsonSerializer.Serialize(
            new AgentLogMessage("agentLog", content, truncated), MauiJsonContext.Default.AgentLogMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msgJson));
        Console.WriteLine($"[bridge] HandleGetAgentLog: {text.Length} chars, truncated={truncated}");
    }

    /// <summary>
    /// A0 补充（真机无 adb）：导出 agent.log——FileProvider + ACTION_SEND 分享给系统面板
    /// （微信/文件应用等），用户自行保存/转发，绕开 WebView 剪贴板复制 200K 文字的限制。
    /// <para>
    /// 接收 <c>{"type":"exportAgentLog"}</c>。agent.log 在 app files 目录
    /// （<see cref="AgentLog.FilePath"/> = AppDataPaths.Directory/agent.log），FileProvider 已配置
    /// （AndroidManifest + file_paths.xml，authority=<c>com.eracore.maui.fileprovider</c>）。
    /// 分享面板经 <see cref="IDispatcher.Dispatch"/> 在 UI 线程启动；Application context 启动
    /// Activity 需 <see cref="Android.Content.ActivityFlags.NewTask"/>。
    /// 文件不存在（日志未开启）时静默提示。非 Android 平台 no-op。
    /// </para>
    /// </summary>
    private void HandleExportAgentLog()
    {
#if ANDROID
        try
        {
            var path = AgentLog.Instance.FilePath;
            if (path == null || !File.Exists(path))
            {
                Console.WriteLine("[bridge] HandleExportAgentLog: no agent.log (file log not enabled?)");
                AgentLog.Instance.Write("[bridge] export agent.log skipped: no file");
                return;
            }
            var context = Android.App.Application.Context;
            var file = new Java.IO.File(path);
            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                context, context.PackageName + ".fileprovider", file);
            var intent = new Android.Content.Intent(Android.Content.Intent.ActionSend);
            intent.SetType("text/plain");
            intent.PutExtra(Android.Content.Intent.ExtraStream, uri);
            intent.PutExtra(Android.Content.Intent.ExtraSubject, "agent.log");
            intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
            var chooser = Android.Content.Intent.CreateChooser(intent, "导出 agent.log");
            if (chooser == null)
            {
                Console.WriteLine("[bridge] HandleExportAgentLog: CreateChooser null");
                return;
            }
            chooser.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission
                             | Android.Content.ActivityFlags.NewTask);
            _dispatcher.Dispatch(() =>
            {
                try
                {
                    context.StartActivity(chooser);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[bridge] export chooser failed: {ex.Message}");
                }
            });
            Console.WriteLine("[bridge] HandleExportAgentLog: chooser launched");
            AgentLog.Instance.Write("[bridge] export agent.log chooser launched");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandleExportAgentLog failed: {ex}");
        }
#else
        Console.WriteLine("[bridge] HandleExportAgentLog: Android only, ignored");
#endif
    }

    /// <summary>
    /// game-library spec ID6：引导用户授权 MANAGE_EXTERNAL_STORAGE——
    /// 启动 Android 系统设置页。
    /// <para>
    /// Intent: <c>Settings.ActionManageAppAllFilesPermission</c> +
    /// <c>Intent.SetData(Android.Net.Uri.FromParts("package", PackageName, null))</c>
    /// </para>
    /// <para>
    /// API &lt; 30 降级：打开应用详情设置页（<c>Settings.ActionApplicationDetailsSettings</c>），
    /// 用户可在此授予 READ_EXTERNAL_STORAGE。
    /// </para>
    /// <para>
    /// 不回复消息——用户跳转设置页后，返回 app 时通过 <c>MainActivity.OnResume</c> 或
    /// Vue 端「已授权，重新扫描」按钮重新投递 <c>checkPermission</c>。
    /// 非 Android 平台 no-op（Windows 无此权限概念）。
    /// </para>
    /// </summary>
    private void HandleRequestPermission()
    {
        // ADR-0019：SAF 替代了 MANAGE_EXTERNAL_STORAGE，不需要跳转系统设置。
        // 此方法保留为空操作——Vue 因 permissionStatus 永远 granted 已不会调用。
        Console.WriteLine("[bridge] HandleRequestPermission: no-op (SAF replaces MANAGE_EXTERNAL_STORAGE)");
        AgentLog.Instance.Write("[bridge] HandleRequestPermission: no-op (SAF replaces MANAGE_EXTERNAL_STORAGE)");
    }

    /// <summary>
    /// 处理 Vue 端 loadGame 消息（issue 09 hot-swap reload）——调 <c>_onReloadGame</c> 回调让 MainPage 重建。
    /// <para>
    /// Vue 端 <c>loadGameFromPath(path)</c> 投递 <c>{"type":"loadGame","path":...}</c>，
    /// 此方法解析 path 字段后调 <see cref="Action{T}"/> 回调。
    /// MainPage 收到回调后：后台线程跑 <c>EmueraRuntimeInitializer.Initialize</c> → UI 线程 Dispose 旧 BridgeHost +
    /// new 新 BridgeHost + <see cref="Start"/>（Vue 已 ready，无需再等 ready 信号）。
    /// </para>
    /// <para>
    /// <b>无回调时静默 no-op</b>：旧 BridgeHost（issue 08 版本）构造未传 <c>_onReloadGame</c>，
    /// 收到 loadGame 消息仅写日志，不抛错——保证向后兼容。
    /// </para>
    /// </summary>
    private void HandleLoadGame(JsonElement root)
    {
        if (!root.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.String)
        {
            Console.WriteLine("[bridge] HandleLoadGame: missing or non-string 'path' field");
            AgentLog.Instance.Write("[bridge] HandleLoadGame: missing or non-string 'path' field");
            return;
        }
        var path = pathEl.GetString();
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine("[bridge] HandleLoadGame: empty path");
            return;
        }
        Console.WriteLine($"[bridge] HandleLoadGame: path={path}");
        AgentLog.Instance.Write($"[bridge] HandleLoadGame: path={path}");
        if (_onReloadGame is null)
        {
            Console.WriteLine("[bridge] HandleLoadGame: no _onReloadGame callback (old BridgeHost without issue 09 support)");
            return;
        }
        try
        {
            _onReloadGame.Invoke(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] HandleLoadGame: _onReloadGame threw: {ex}");
            AgentLog.Instance.Write($"[bridge] HandleLoadGame: _onReloadGame threw: {ex}");
            ShowError("重新加载游戏失败: " + ex.Message);
        }
    }

    /// <summary>
    /// 推送布局元数据给 Vue——让前端在首帧 turn 到达前就能用正确的游戏设置渲染终端区域。
    /// <para>
    /// 与 <c>KestrelGameServer.HandleGetStateAsync</c> 一致的计算逻辑：
    /// 从 <see cref="ConfigData"/> 读取 WindowX/FontSize/LineHeight/FontName，
    /// 按无头模式 <c>DrawingParam_ShapePositionShift = Max(2, FontSize/6)</c> 算 <c>gameColumns</c>。
    /// </para>
    /// <para>
    /// 消息格式：<c>{"type":"layout","state":"Loading","gameDir":"D:/game","windowWidth":760,"fontSize":18,"lineHeight":19,"gameColumns":84,"fontName":"ＭＳ ゴシック"}</c>
    /// </para>
    /// </summary>
    private void PushLayoutMessage()
    {
        var ww = _configData.GetConfigValue<int>(ConfigCode.WindowX);
        var fs = _configData.GetConfigValue<int>(ConfigCode.FontSize);
        var lh = _configData.GetConfigValue<int>(ConfigCode.LineHeight);
        var fn = _configData.GetConfigValue<string>(ConfigCode.FontName) ?? "";

        int charWidth = Math.Max(fs / 2, 1);
        int marginOffset = Math.Max(2, fs / 6);
        int gameColumns = (ww - marginOffset) / charWidth;

        var msg = JsonSerializer.Serialize(
            new LayoutMessage(
                Type: "layout",
                State: "Loading",
                GameDir: GamePaths.Current?.ExeDir,
                WindowWidth: ww,
                FontSize: fs,
                LineHeight: lh,
                GameColumns: gameColumns,
                FontName: fn),
            MauiJsonContext.Default.LayoutMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
    }

    /// <summary>
    /// 推送 emuera.config 值给 Vue——让设置页显示当前配置。
    /// <para>
    /// 消息格式：<c>{"type":"config","maxLog":5000,"agentLogEnabled":false,"displayReportOverride":"auto"}</c>
    /// （A0：agentLogEnabled 为文件日志开关的 C# 权威状态——启动时 Start() 推一次，
    /// 设置页切换后 HandleSetAgentLogEnabled 推回同步；displayReportOverride 为启动日志
    /// 覆盖三态的权威状态，切换后 HandleSetDisplayReportOverride 推回同步）。
    /// </para>
    /// </summary>
    private void PushConfigMessage()
    {
        var maxLog = _configData.GetConfigValue<int>(ConfigCode.MaxLog);
        var displayReportOverride = DisplayReportOverrideToString(_configData.OverrideDisplayReport);
        var msg = JsonSerializer.Serialize(
            new ConfigMessage("config", maxLog, AgentLog.Instance.Enabled, displayReportOverride),
            MauiJsonContext.Default.ConfigMessage);
        _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
    }

    /// <summary>
    /// 启动托管 server + 共享会话（issue 05）——仅首次调用生效；重复调用（多次 ready 信号等）幂等忽略。
    /// <para>
    /// <b>步骤</b>：
    /// <list type="number">
    ///   <item>建 <see cref="HttpListenerHost"/>（port=0 自动选空闲端口）——agent 经 localhost HTTP+WS 连接</item>
    ///   <item><see cref="SessionRegistry.CreateMauiSessionAsync"/> 建共享会话（游戏已由 OnReloadGame 初始化）</item>
    ///   <item>订阅 OutputHub turn 广播 → <see cref="TurnPumpAsync"/> 转发 WebView；起 <see cref="ControlPumpAsync"/> 同步控制状态</item>
    ///   <item>写 agent 发现记录（端口 + token + gameDir）</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>issue 09 hot-swap reload</b>：MainPage 重建 BridgeHost 后直接调 <see cref="Start"/>——
    /// Vue 已 ready（首次 ready 信号早已收到），无需再等 ready 信号。
    /// </para>
    /// </summary>
    internal void Start()
    {
        if (_started)
            return;
        _started = true;
        _readyReceived = true; // 标记 ready 已收到——避免后续 ready 信号重复触发 Start
        // 在托管 server 启动前推送布局元数据——Vue 在首帧 turn 到达前拿到字体/列宽/字号/行距
        PushLayoutMessage();
        PushConfigMessage();
        Console.WriteLine("[bridge] Starting hosted server + session");
        AgentLog.Instance.Write("[bridge] starting hosted server + session");
        // 托管 server + 会话 + 订阅在后台线程初始化（StartHostedSessionAsync）——
        // 避免 UI 线程上 sync-over-async（HttpListenerHost 构造 / SessionRegistry 锁 + Start）。
        _ = Task.Run(StartHostedSessionAsync);
    }

    /// <summary>
    /// 后台初始化托管 server + 共享会话（issue 05）——<see cref="Start"/> 的异步主体，UI 线程不阻塞。
    /// <para>
    /// 顺序：建 <see cref="HttpListenerHost"/>（port=0 自动选空闲端口）→
    /// <see cref="SessionRegistry.CreateMauiSessionAsync"/> 建共享会话 →
    /// 订阅 OutputHub turn 广播（<see cref="TurnPumpAsync"/>）+ 起 <see cref="ControlPumpAsync"/> →
    /// 写 agent 发现记录。
    /// </para>
    /// <para>
    /// <b>Dispose 竞态</b>：Dispose（UI 线程）可能先于本方法完成执行——每步检查 <see cref="_disposed"/>；
    /// 已建的 host 在发现 _disposed 后立即释放（含赋值与检查之间的窗口），避免泄漏。
    /// </para>
    /// </summary>
    private async Task StartHostedSessionAsync()
    {
        HttpListenerHost? serverHost = null;
        try
        {
            if (_disposed)
                return;

            // 1. 建 HttpListener 宿主（port=0 自动选空闲端口，issue 05 MAUI 托管 server）。
            //    共享层会话由宿主内 SessionRegistry 持有——agent 经 localhost HTTP+WS 连接同一会话。
            serverHost = new HttpListenerHost(
                port: 0,
                _terminalSetup,
                _configData,
                displayReportOverride: _configData.OverrideDisplayReport);
            _serverHost = serverHost;
            if (_disposed)
            {
                // Dispose 先到且已读过 _serverHost（null）——这里释放刚建的 host 防止泄漏
                _serverHost = null;
                serverHost.Dispose();
                return;
            }

            // 2. 建共享 session——游戏已由 OnReloadGame 初始化（GamePaths.Current 已 Resolve + ConfigData 已加载），
            //    CreateMauiSessionAsync 允许注册表为空（区别于 HTTP 的 /load-game 建首个 session）。
            //    fullDiffOnFirstTurn=true：MAUI WebView 桥接无 GET /snapshot 入口，首帧 diff 须全量。
            var createResult = await serverHost.Sessions.CreateMauiSessionAsync(fullDiffOnFirstTurn: true);
            if (_disposed)
                return;
            if (createResult.Status != SessionCreateStatus.Created)
            {
                ShowError("创建游戏会话失败: " + createResult.Status);
                return;
            }

            // 3. 订阅 turn 广播 → WebView。订阅发生在 session 首帧之前（Initialize 在后台 Task 跑），
            //    首帧全量 diff 不会丢。同时起控制事件泵同步前端控制状态（旁观/可操作切换）。
            var subscription = await serverHost.Sessions.TryGetWsSubscriptionAsync();
            if (_disposed)
                return;
            if (subscription == null)
            {
                ShowError("订阅游戏会话失败");
                return;
            }
            _session = subscription.Session;
            _sessionIO = _session.IO;
            _turnPump = Task.Run(() => TurnPumpAsync(subscription.Reader, subscription.Hub));
            _controlPump = Task.Run(() => ControlPumpAsync(_session));

            // 4. 写 agent 发现记录（端口 + token + gameDir）——eracore_agent start 侦测后复用本会话
            WriteDiscoveryRecord();

            Console.WriteLine($"[bridge] hosted server on port {_serverHost.Port}, session {createResult.SessionId}");
            AgentLog.Instance.Write($"[bridge] hosted server on port {_serverHost.Port}, session {createResult.SessionId}");
#if ANDROID
            // O4：游戏加载完成 → 后台预热 sav + 游戏根目录子项缓存（原 GameLoopAsync runLoop 开头逻辑，托管后移此）
            var prefetchExeDir = GamePaths.Current?.ExeDir;
            if (!string.IsNullOrEmpty(prefetchExeDir))
                _ = Task.Run(() => TryPrefetchSaveDirectories(prefetchExeDir));
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] hosted session start failed: {ex}");
            AgentLog.Instance.Write($"[bridge] hosted session start failed: {ex}");
            ShowFatalError(ex);
            // 失败路径：释放尚未挂到 _serverHost 的局部 host（已挂载的由 Dispose 收尾）
            if (serverHost != null && !ReferenceEquals(serverHost, _serverHost))
            {
                try { serverHost.Dispose(); }
                catch (Exception disposeEx)
                {
                    AgentLog.Instance.Write($"[bridge] hosted session start cleanup dispose failed: {disposeEx.Message}");
                }
            }
        }
    }

    /// <summary>
    /// turn 泵：从 OutputHub 订阅 reader 读 turn，转发给 WebView（<see cref="OnTurnFromGame"/> 内
    /// Dispatcher.Dispatch 切 UI 线程 PostTurn）。reader 完成（session 结束 hub.Complete）或取消（Dispose）即退出。
    /// </summary>
    private async Task TurnPumpAsync(ChannelReader<string> reader, OutputHub hub)
    {
        try
        {
            await foreach (var turn in reader.ReadAllAsync(_cts.Token))
                OnTurnFromGame(turn);
        }
        catch (OperationCanceledException)
        {
            // Dispose 取消，正常退出
        }
        catch (ChannelClosedException)
        {
            // session 结束 hub.Complete，正常退出
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] turn pump error: {ex.Message}");
            AgentLog.Instance.Write($"[bridge] turn pump error: {ex.Message}");
        }
        finally
        {
            hub.Unsubscribe(reader);
        }
    }

    /// <summary>
    /// 控制事件泵：长轮询 Controller 控制事件，事件发生时把当前控制状态推给 WebView
    /// （前端据此切 旁观/可操作 UI，输入栏启用/禁用）。首帧先推一次，保证 WebView 就绪即同步。
    /// </summary>
    private async Task ControlPumpAsync(Session session)
    {
        PushControlStatus(session);
        while (!_cts.IsCancellationRequested)
        {
            ControlEvent? ev;
            try
            {
                ev = await session.Controller.WaitForEventAsync(timeoutMs: 25000, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[bridge] control pump error: {ex.Message}");
                AgentLog.Instance.Write($"[bridge] control pump error: {ex.Message}");
                break;
            }
            if (ev != null)
                PushControlStatus(session);
        }
    }

    /// <summary>
    /// 推控制状态给 Vue——<c>{"type":"controlStatus","controller":{kind,leaseExpiresAt}|null,"state":"idle"|"held"}</c>。
    /// 前端 useAppInit 的 handleMauiMessage 调 conn.applyControlStatus 同步 controller ref。
    /// </summary>
    private void PushControlStatus(Session session)
    {
        try
        {
            var controller = session.Controller.CurrentInfo;
            var msg = new JsonObject
            {
                ["type"] = "controlStatus",
                ["controller"] = controller == null
                    ? null
                    : new JsonObject
                    {
                        ["kind"] = controller.Kind,
                        ["leaseExpiresAt"] = controller.LeaseExpiresAt,
                    },
                ["state"] = session.Controller.State,
            }.ToJsonString();
            _dispatcher.Dispatch(() => _jsBridge.PostMessage(msg));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] PushControlStatus failed: {ex.Message}");
            AgentLog.Instance.Write($"[bridge] PushControlStatus failed: {ex.Message}");
        }
    }

    /// <summary>
    /// issue 05：处理 WebView 的控制权桥接消息 <c>{"type":"control","action":"acquire"|"release"|"status"}</c>——
    /// 用户身份（无 token）acquire = 直取/强夺（agent 持有时 steal），与 HTTP POST /control/acquire 用户语义一致。
    /// 操作后推回 controlStatus 让前端同步。
    /// </summary>
    private void HandleControlMessage(JsonElement root)
    {
        var session = _session;
        if (session == null)
            return;
        var action = root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;
        switch (action)
        {
            case "acquire":
                session.Controller.Acquire(ControlIdentity.User);
                break;
            case "release":
                session.Controller.Release(ControlIdentity.User);
                break;
        }
        PushControlStatus(session);
    }

    // ===== agent 发现记录（issue 05）：MAUI 托管 server 的端口 + token 供 eracore_agent 侦测复用 =====

    /// <summary>发现记录路径——Windows <c>%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json</c>（Android 托管后置）。</summary>
    private static string? DiscoveryFilePath()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "EmueraCore", "eracore-maui-server.json");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写发现记录：<c>{host,port,token,gameDir,startedByAgent:false,mauiHosted:true}</c>。
    /// token 由 MAUI 生成（所有权翻转——不再由 agent 生成），agent 读取后作为身份凭证。</summary>
    private void WriteDiscoveryRecord()
    {
        try
        {
            var path = DiscoveryFilePath();
            if (path == null || _serverHost == null)
                return;
            var dir = Path.GetDirectoryName(path);
            if (dir != null)
                Directory.CreateDirectory(dir);
            _discoveryToken ??= Guid.NewGuid().ToString("N");
            var record = new JsonObject
            {
                ["host"] = "127.0.0.1",
                ["port"] = _serverHost.Port,
                ["token"] = _discoveryToken,
                ["gameDir"] = GamePaths.Current?.ExeDir,
                ["startedByAgent"] = false,
                ["mauiHosted"] = true,
            }.ToJsonString();
            File.WriteAllText(path, record);
            _discoveryPath = path;
            Console.WriteLine($"[bridge] discovery record written: {path} port={_serverHost.Port}");
            AgentLog.Instance.Write($"[bridge] discovery record written: {path} port={_serverHost.Port}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] discovery record write failed: {ex.Message}");
            AgentLog.Instance.Write($"[bridge] discovery record write failed: {ex.Message}");
        }
    }

    /// <summary>删发现记录（Dispose / 退出游戏时）——server 随会话结束，agent 不应再连。</summary>
    private void DeleteDiscoveryRecord()
    {
        if (_discoveryPath == null)
            return;
        try
        {
            File.Delete(_discoveryPath);
        }
        catch
        {
            // 删除失败无害——agent 探活失败会自然放弃
        }
        _discoveryPath = null;
    }

#if ANDROID
    /// <summary>
    /// O4 后台预取：预热 sav + 游戏根目录子项缓存（进存档界面首查命中，0 次枚举 IPC）。
    /// 游戏加载完成后（runLoop 开头）由 fire-and-forget 后台任务调用；异常全吞——
    /// 预取失败不影响游戏，仅留痕。SAF 模式专属：<see cref="SafGameDirAccessor.Instance"/>
    /// 仅 Android + 已选游戏目录时非 null，天然守卫非 SAF 平台。
    /// </summary>
    private void TryPrefetchSaveDirectories(string exeDir)
    {
        try
        {
            var acc = SafGameDirAccessor.Instance;
            if (acc == null) return; // 非 SAF 模式（Windows / 未选目录）无操作
            acc.WarmDirectoryCache(exeDir); // 游戏根
            // sav 理论 document URI：Config.ForceSavDir（= SafCompat.ResolveSubPath(ExeDir, "sav")，
            // 语义一致；BridgeHost 已 using MinorShift.Emuera.Runtime.Config，零新增 using；
            // 不依赖 AsyncLocal——后台线程安全）。sav 尚不存在时 Query 得空/null，无害。
            acc.WarmDirectoryCache(Config.ForceSavDir);
        }
        catch (Exception ex)
        {
            AgentLog.Instance.Write($"[prefetch] failed: {ex.Message}"); // 静默降级，仅留痕
        }
    }
#endif

    /// <summary>
    /// 推致命错误 turn 给 Vue——spec ID10。
    /// <para>
    /// 构造 <c>TurnRecord(state:"Error", error: ex.Message)</c> JSON 调 <see cref="IJsBridge.PostTurn"/> 推给 Vue，
    /// Vue 端 <c>applyTurn</c> 渲染 error 字段。error turn 格式与 <c>AgentJsonlProtocol.StepAsync</c> 的脚本异常
    /// error turn 一致——前端代码零改动复用渲染逻辑。
    /// </para>
    /// <para>
    /// <b>fallback</b>：<see cref="IJsBridge.PostTurn"/> 失败（WebView 未 Attach / EvaluateJavaScriptAsync 抛异常）
    /// 时调 <c>MainThread.BeginInvokeOnMainThread</c> + <c>Application.Current.MainPage.DisplayAlert</c>
    /// 兜底展示错误。ID7 延迟启动保证 Vue ready 后才 <see cref="Start"/>，崩溃时 Vue 已在跑能接收 error turn。
    /// </para>
    /// </summary>
    private void ShowFatalError(Exception ex)
    {
#if ANDROID
        Android.Util.Log.Error("EmueraMaui", $"FATAL: {ex}");
#endif
        System.Diagnostics.Debug.WriteLine($"[FATAL] {ex}");
        ShowError(ex.Message);
    }

    /// <summary>
    /// 推 error turn 给 Vue（issue 09 公开版本——可被外部 reload 路径调用）。
    /// <para>
    /// 与 <see cref="ShowFatalError"/> 共用渲染链路——构造 <c>TurnRecord(state:"Error", error: message)</c> JSON
    /// 调 <see cref="IJsBridge.PostTurn"/> 推给 Vue。失败 fallback <c>DisplayAlert</c>。
    /// </para>
    /// </summary>
    /// <param name="message">错误消息字符串（Vue error turn 的 error 字段）。</param>
    internal void ShowError(string message)
    {
        try
        {
            // NativeAOT：显式走源生成 TypeInfo（TurnOp/LineOp 基类已挂 [JsonConverter]，
            // context 生成的 TurnRecord TypeInfo 自动使用自定义多态 converter，wire 与 options 重载一致）。
            var errorTurn = JsonSerializer.Serialize(new TurnRecord(
                state: "Error",
                inputType: null,
                needValue: false,
                diff: null,
                error: message
            ), EmueraJsonContext.Default.TurnRecord);
            // _jsBridge.PostTurn 内部 fire-and-forget（async void），异常被自身 catch 不抛——
            // 但 try/catch 兜底防御：PostTurn 之外的序列化失败也走 DisplayAlert fallback。
            _dispatcher.Dispatch(() => _jsBridge.PostTurn(errorTurn));
            Console.WriteLine($"[bridge] error turn pushed to Vue: {errorTurn}");
            AgentLog.Instance.Write($"[bridge] error turn pushed to Vue");
        }
        catch (Exception postEx)
        {
            // PostTurn 失败——fallback DisplayAlert
            Console.WriteLine($"[bridge] PostTurn failed, fallback to DisplayAlert: {postEx}");
            AgentLog.Instance.Write($"[bridge] PostTurn failed, fallback to DisplayAlert: {postEx}");
            try
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var mainPage = Application.Current?.Windows[0].Page;
                    if (mainPage != null)
                    {
                        await mainPage.DisplayAlertAsync("Error", message, "OK");
                    }
                });
            }
            catch (Exception alertEx)
            {
                // DisplayAlert 也失败——只剩日志
                Console.WriteLine($"[bridge] DisplayAlert fallback failed: {alertEx}");
                AgentLog.Instance.Write($"[bridge] DisplayAlert fallback failed: {alertEx}");
            }
        }
    }

    /// <summary>
    /// 释放桥接资源——fire-and-forget，不阻塞 UI 线程（spec ID9）。
    /// <para>
    /// <b>关键约束</b>：UI 线程不阻塞——不调 <see cref="Task.Wait()"/> / <c>GetAwaiter().GetResult()</c>。
    /// 托管 server 的 Dispose（join 游戏循环）移到后台线程执行。
    /// </para>
    /// <para>
    /// <b>步骤</b>：
    /// <list type="number">
    ///   <item><see cref="CancellationTokenSource.Cancel()"/>——通知 turn/control 泵退出</item>
    ///   <item><see cref="HttpSessionIO.Close"/>——关 session 输入/输出 Channel，<c>ReadLineAsync</c> 返回 null 让游戏循环退出</item>
    ///   <item>删 agent 发现记录——server 随会话结束</item>
    ///   <item>后台线程 <see cref="HttpListenerHost.Dispose"/>——join 游戏循环 + 关监听（不阻塞 UI）</item>
    /// </list>
    /// </para>
    /// <para>
    /// 幂等：多次调用安全（<c>_disposed</c> 守卫）。
    /// issue 09 hot-swap reload 也调此方法 Dispose 旧 BridgeHost——主线程调用，不阻塞。
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _jsBridge.InputReceived -= OnInputFromJs;
#if ANDROID
        // mem-leak-fix：构造时（ctor）订阅的 AndroidJsBridge 静态事件/委托必须对称退订，
        // 否则被 Dispose 的旧 BridgeHost 实例被静态根钉住（每次进出游戏泄漏一个 host 及其引用链）。
        // FallbackInputHandler 是共享静态委托——仅当仍指向本实例的处理器时才置 null，
        // 避免误清新 host 已赋值的处理器（RecreateHost 先 Dispose 旧 host 再 new 新 host，时序安全）。
        // 用 ctor 缓存的 _onInputFromJsDelegate 做同一实例比较（方法组转委托每次新建实例，
        // 直接对方法组 ReferenceEquals 恒 false——原守卫永不成立；同时消除 CS8974）。
        AndroidJsBridge.BridgeUrlReceived -= OnBridgeUrl;
        if (ReferenceEquals(AndroidJsBridge.FallbackInputHandler, _onInputFromJsDelegate))
            AndroidJsBridge.FallbackInputHandler = null;
#endif

        // 1. 取消泵（turn/control）——Task.Run 的泵读 _cts.Token，取消即退出
        try
        {
            _cts.Cancel();
        }
        catch (Exception ex)
        {
            // CTS 已 Dispose / 其他异常——日志吞掉，不阻断后续清理
            AgentLog.Instance.Write($"[bridge] Dispose CTS.Cancel failed: {ex.Message}");
        }

        // 2. 关 session 输入——ReadLineAsync 返回 null 让游戏循环退出（幂等；session 结束也安全）
        try
        {
            _sessionIO?.Close();
        }
        catch (Exception ex)
        {
            AgentLog.Instance.Write($"[bridge] Dispose session IO close failed: {ex.Message}");
        }

        // 3. 删 agent 发现记录——server 随会话结束，agent 不再连
        DeleteDiscoveryRecord();

        // 4. 停托管 server——后台线程 join session（spec ID9：不阻塞 UI）。
        //    Session.Dispose 会 GetAwaiter().GetResult() join 游戏循环；游戏循环多在
        //    ReadLineAsync await，IO.Close 后毫秒级退出。
        var host = _serverHost;
        _serverHost = null;
        _session = null;
        _sessionIO = null;
        if (host != null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    host.Dispose();
                }
                catch (Exception ex)
                {
                    AgentLog.Instance.Write($"[bridge] server host dispose failed: {ex.Message}");
                    Console.WriteLine($"[bridge] server host dispose failed: {ex}");
                }
                // mem-leak-fix：host.Dispose 已 join 游戏循环（Session.Dispose 内 GetAwaiter().GetResult()），
                // 此时 GlobalStatic.Scope.Dispose 的静态根清理必然已完成——在此触发 Full GC，
                // 让已释放的游戏运行时内存可见回落（Preferences 开关默认 true）。
                TryCollectAfterGameExit();
            });
        }
    }

    /// <summary>
    /// mem-leak-fix：退出游戏后主动 Full GC + 等待终结器 + 二次收集。
    /// <para>
    /// 调用时机：仅限 <see cref="Dispose"/> 内 host.Dispose 完成后（游戏循环已 join、
    /// scope 静态根清理已执行）。后台线程执行，不阻塞 UI。
    /// Preferences key：<see cref="ActiveGcOnExitKey"/>，默认 true；false 时跳过。
    /// </para>
    /// </summary>
    private static void TryCollectAfterGameExit()
    {
        bool enabled = true;
        try
        {
            enabled = Preferences.Get(ActiveGcOnExitKey, true);
        }
        catch
        {
            // Preferences 不可用（极少见）——保持默认开启
        }
        if (!enabled)
            return;
        try
        {
            // 切到 Batch（无延迟调优）模式再做 Full GC——比默认交互模式更彻底：
            // Forced 强制压缩 Gen2 + LOH，配合 compacting 触发堆段修剪归还 OS。
            // 完成后恢复原 LatencyMode（进程全局设置，仅退出瞬间短暂生效）。
            var prevLatency = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.Batch;
            try
            {
                // 先回收一轮 + 终结器，再压缩收集——确保已无引用的游戏对象图（含终结点对象）被回收，
                // 并触发 Gen2 堆段修剪让内存归还 OS。
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            }
            finally
            {
                GCSettings.LatencyMode = prevLatency;
            }
            Console.WriteLine("[bridge] post-exit GC completed");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] post-exit GC failed: {ex}");
        }
    }
}
