using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EraCore.Maui.JsBridge;
using EraCore.Maui.Json;
using EraCore.Maui.Services;
using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Config;
// SafPath 仅在 ANDROID 分支（accessor 形态判定）使用——条件 using，免 Windows 构建报未使用
#if ANDROID
using MinorShift.Emuera.Runtime.Utils;
#endif
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;

namespace EraCore.Maui;

/// <summary>
/// MAUI 主页面——issue 07 / 08 / 09 / spec ID6 + ID8。
/// <para>
/// 持有 <see cref="WebView"/> + <see cref="BridgeHost"/> + <see cref="IJsBridge"/>，编排：
/// <list type="number">
///   <item>构造时 DI 注入 <see cref="ConfigData"/> + <see cref="ITerminalSetup"/> 单例</item>
///   <item>new <see cref="BridgeHost"/>（构造不启动游戏循环——spec ID7 延迟启动）</item>
///   <item>new <see cref="IJsBridge"/>（<see cref="JsBridgeFactory.Create"/> 平台分流）</item>
///   <item>订阅 <c>MainWebView.HandlerChanged</c>——Handler 就绪后 <c>_jsBridge.Attach(webView)</c> + 设 URL</item>
///   <item>Vue 加载后 postMessage({type:'ready'}) → <see cref="BridgeHost.OnInputFromJs"/> 识别</item>
/// </list>
/// </para>
/// <para>
/// URL 平台分叉（spec ID6 + control-handoff M4 / issue 05）：
/// <list type="bullet">
///   <item>Windows 本机: <c>https://app.local/index.html</c>（unpackaged 模式下用 WebView2 虚拟主机映射，
///     <c>WindowsJsBridge.Attach</c> 内调 <c>SetVirtualHostNameToFolderMapping</c> 把
///     <c>app.local</c> 映射到输出目录 wwwroot/）</item>
///   <item>Android: <c>https://game.local/wwwroot/index.html</c></item>
/// </list>
/// 注：issue 05 起 MAUI 托管 server（HttpListenerHost）承载共享会话，WebView 仍经 JS 桥接渲染该会话；
/// 原「Windows 远程导航到外部 server 同源页」模式已废弃（RemoteSessionProbe 已删）。
/// </para>
/// <para>
/// <b>issue 09 hot-swap reload</b>：Vue 端 <c>pickGameFolder()</c> → C# <c>BridgeHost.HandlePickFolder</c>
/// → 原生 FolderPicker → <c>folderPicked</c> 推回 Vue → Vue <c>loadGameFromPath(path)</c> →
/// <c>BridgeHost.HandleLoadGame</c> 调 <see cref="OnReloadGame"/> 回调 →
/// 后台线程跑 <c>EmueraRuntimeInitializer.Initialize</c> → UI 线程 <see cref="RecreateHost"/>。
/// <see cref="_host"/> / <see cref="_configData"/> / <see cref="_terminalSetup"/> 为可变字段，
/// reload 时替换为新实例。
/// </para>
/// </summary>
/// <remarks>
/// <see cref="BridgeHost"/> 在 <see cref="OnDisappearing"/> 或 <see cref="RecreateHost"/> 时
/// <see cref="BridgeHost.Dispose"/>——前者是页面销毁（Windows <c>Window.Closed</c> / Android <c>Activity.OnDestroy</c>），
/// 后者是 issue 09 hot-swap 旧 host 让位给新 host。
/// </remarks>
public partial class MainPage : ContentPage
{
    private readonly IJsBridge _jsBridge;
    private BridgeHost? _host;
    private ConfigData _configData;
    private ITerminalSetup _terminalSetup;
    /// <summary>全局设置服务（ADR-0030）——Preferences 后端，注入控制 server 与 BridgeHost。</summary>
    private readonly AppSettings _appSettings;
    private bool _urlSet;
    /// <summary>issues/13 M6：Vue 是否已握手（收到首条 scanGames = 前端已挂载且消息处理器就位）。</summary>
    private bool _vueReady;
    /// <summary>issues/13 M6：控制面 <c>POST /load-game</c> 暂存的游戏目录——等 Vue 握手后投递。</summary>
    private string? _autoLoadPendingPath;

    /// <summary>
    /// DI 注入构造——<see cref="MauiProgram"/> 注册的 <see cref="ConfigData"/> + <see cref="ITerminalSetup"/> 单例经 MAUI DI 容器注入。
    /// <para>
    /// 构造标记 <c>internal</c> 因参数类型 <see cref="ConfigData"/> / <see cref="ITerminalSetup"/>
    /// 在 <c>Emuera.Headless.Core</c> 内为 <c>internal</c>——C# 规则要求方法可访问性不得高于参数类型。
    /// <see cref="MauiProgram"/> 用 factory delegate 注册（同程序集，可访问 internal ctor）。
    /// </para>
    /// </summary>
    internal MainPage(ConfigData configData, ITerminalSetup terminalSetup)
    {
        InitializeComponent();

        _jsBridge = JsBridgeFactory.Create();
        _configData = configData;
        _terminalSetup = terminalSetup;
        // ADR-0030：全局设置服务单例（Preferences 后端）——控制 server 与 BridgeHost 共用同一实例
        _appSettings = new AppSettings(new PreferencesAppSettingsStore());
        // issues/13 M6：app 级托管控制 server 提前到启动期——必须在 RecreateHost 之前，
        // 因为 BridgeHost 建会话时直接复用它（不再自行 new HttpListenerHost）。
        // 起不来就抛——静默降级会让 agent 连不上却无任何痕迹，比启动期硬失败更难查。
        var controlServer = MauiControlServer.EnsureStarted(configData, terminalSetup, _appSettings);
        // 注册控制面回调——此时 this 已可用（字段已赋值），实现见两个方法。
        controlServer.ScanGameDirSink = ScanGamesForControlServer;
        controlServer.LoadGameSink = RequestAutoLoadGame;
        // issue 05：远程模式已废弃（RemoteSessionProbe 已删），无需再等 HandlerChanged 里的探测——
        // 全平台（Windows/Android）都在构造时创建 BridgeHost，保证 Vue 的 ready/scanGames 消息
        // 一来即有 InputReceived 订阅者处理（否则 Windows 会卡在「正在扫描游戏列表…」）。
        RecreateHost();
        // issues/13 M6：主目录同步给控制面——CLI 的 games / load --name 依赖它
        //（Android 上主目录是 SAF content URI，手写命令行极易出错，由 app 说出更可靠）。
        controlServer.NotifyMainGameDir(_host?.MainGameDir);

        // HandlerChanged 是 MAUI WebView 平台原生视图就绪的最早可靠时机（spec ID8）。
        // 在此 Attach IJsBridge（订阅 WebMessageReceived / AddJavascriptInterface）+ 设 URL 加载 Vue。
        MainWebView.HandlerChanged += OnWebViewHandlerChanged;

        // 兜底日志：WebView 导航结果监听——每种结果都输出日志方便诊断白屏
        MainWebView.Navigated += OnWebViewNavigated;
    }

    private async void OnWebViewHandlerChanged(object? sender, EventArgs e)
    {
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"OnWebViewHandlerChanged fired, _urlSet={_urlSet}, Handler={MainWebView.Handler?.GetType().Name}");
#endif
        // Attach 幂等（WindowsJsBridge / AndroidJsBridge 内部 _attached flag 防重复）。
        // Attach 内部会配置虚拟主机映射（Windows）——必须 await 完成后再设 URL，
        // 否则首帧导航到 https://app.local/ 时映射未就绪导致空白。
        await _jsBridge.Attach(MainWebView);

        // 设 URL——仅首次就绪时设一次，避免 HandlerChanged 多次触发重复加载。
        if (!_urlSet)
        {
            _urlSet = true;
            var url = ResolveWebViewUrl();
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", $"Setting WebView URL: {url}");
#endif
            MainWebView.Source = new UrlWebViewSource { Url = url };
        }
    }

    /// <summary>
    /// WebView 导航完成回调——输出导航结果用于诊断白屏。
    /// </summary>
    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        var source = e.Url ?? "(null)";
        var result = e.Result.ToString();
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"WebView Navigated: result={result}, url={source}");
#else
        Console.WriteLine($"[maui] WebView Navigated: result={result}, url={source}");
#endif
        if (e.Result != WebNavigationResult.Success)
        {
#if ANDROID
            Android.Util.Log.Warn("EmueraMaui", $"WebView navigation FAILED: {result}, url={source}");
#endif
        }
    }

    /// <summary>
    /// 按平台返回 WebView 加载 URL（spec ID6）。
    /// <para>
    /// Windows unpackaged 模式下用 <c>https://app.local/index.html</c>——
    /// <c>WindowsJsBridge.Attach</c> 内 <c>SetVirtualHostNameToFolderMapping</c>
    /// 把 <c>app.local</c> 映射到输出目录 <c>wwwroot/</c>。
    /// </para>
    /// <para>
    /// Android 用 <c>https://game.local/wwwroot/index.html</c>——
    /// <c>AndroidJsBridge.Attach</c> 内 <see cref="AndroidX.WebKit.WebViewAssetLoader"/>
    /// 注册 <c>/wwwroot/</c> PathHandler（AssetsPathHandler 读 <c>android_asset/wwwroot/</c>）。
    /// **不能用 <c>file:///android_asset/...</c>**：file:// 页面里的 https:// 子资源请求
    /// 不会进入 <c>shouldInterceptRequest</c>（AndroidX WebViewAssetLoader 的官方设计前提：
    /// 页面与资源同 https 域），实测图片直接走真实网络 →
    /// <c>ERR_NAME_NOT_RESOLVED</c> → 图片全空。整页迁到 https 虚拟域后与 Windows 模式对称。
    /// </para>
    /// <para>
    /// 注：issue 05 起 MAUI 托管 server 是「桥接渲染 + 托管会话」双轨——WebView 仍加载本机 Vue
    /// （https://app.local / game.local），经 JS 桥接渲染托管会话；不再导航到外部 server 同源页
    /// （原远程模式已废弃，RemoteSessionProbe/TryEnterRemoteModeAsync 已删）。
    /// </para>
    /// </summary>

    private static string ResolveWebViewUrl()
    {
#if WINDOWS
        return $"https://{WindowsJsBridge.VirtualHostName}/index.html";
#elif ANDROID
        return $"https://{GameAssetConstants.VirtualHostName}/wwwroot/index.html";
#else
        throw new System.PlatformNotSupportedException(
            "MAUI WebView URL 当前平台不支持（Phase 1 仅 Windows + Android）");
#endif
    }

    /// <summary>
    /// 用当前 <see cref="_configData"/> + <see cref="_terminalSetup"/> 重建 <see cref="BridgeHost"/>。
    /// <para>
    /// Dispose 旧 host（若存在）→ new 新 host（订阅 InputReceived + 传 <see cref="OnReloadGame"/> 回调）→ 不启动。
    /// 启动时机：首次构造时由 Vue ready 信号触发；reload 时由 <see cref="OnReloadGame"/> 直接调 <see cref="BridgeHost.Start"/>。
    /// </para>
    /// <para>
    /// 调用线程：UI 线程（构造时 / reload 后切回 UI 线程）。
    /// </para>
    /// </summary>
    private void RecreateHost()
    {
        _host?.Dispose();
        _host = new BridgeHost(Dispatcher, _configData, _terminalSetup, _jsBridge, _appSettings, OnReloadGame, OnGameExited, OnVueReady);
    }

    /// <summary>
    /// issues/13 M6：Vue 握手——<see cref="BridgeHost.HandleScanGames"/> 收到首条 <c>scanGames</c> 时回调。
    /// <para>
    /// 幂等守卫：退出游戏再重开会重建 BridgeHost，届时 Vue 会再发 scanGames；
    /// <c>_vueReady</c> 置 true 后不再重复投递（pending 早已清空，重复投递会误触发第二次加载）。
    /// </para>
    /// </summary>
    private void OnVueReady()
    {
        if (_vueReady)
            return;
        _vueReady = true;
        Console.WriteLine("[maui] OnVueReady: Vue handshake received (first scanGames)");
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", "OnVueReady: Vue handshake received");
#endif
        FlushAutoLoad();
    }

    /// <summary>
    /// issues/13 M6：把暂存的自动加载路径投递给 Vue——由 Vue 调它现成的
    /// <c>loadGameFromPath(path)</c>，走与用户手点<b>完全相同</b>的链路。
    /// <para>
    /// 为何不直接在 C# 侧初始化：那样会绕过 Vue 的 game store 状态更新（UI 不切 terminal 视图），
    /// 且要另写一套状态同步。转交 Vue 后前端逻辑零新增。
    /// </para>
    /// </summary>
    private void FlushAutoLoad()
    {
        var path = _autoLoadPendingPath;
        if (path == null)
            return;
        _autoLoadPendingPath = null;
        try
        {
            var msg = JsonSerializer.Serialize(
                new AutoLoadGameMessage("autoLoadGame", path),
                MauiJsonContext.Default.AutoLoadGameMessage);
            // PostMessage（WindowsJsBridge → ExecuteScriptAsync）只能在 UI 线程调——
            // 本方法可能从控制面 HTTP 线程（load sink）或 OnInputFromJs 链路进入，
            // 与 BridgeHost 所有 PostMessage 调用点一样必须 Dispatcher.Dispatch。
            // 另注意 WindowsJsBridge.PostMessage 失败只写日志不抛——不能靠 try 感知失败，
            // 只能靠 logcat/console 里有无 "PostMessage failed" 排查。
            Dispatcher.Dispatch(() =>
            {
                _jsBridge.PostMessage(msg);
                Console.WriteLine($"[maui] autoLoadGame dispatched to Vue: {path}");
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[maui] autoLoadGame dispatch failed: {ex}");
        }
    }

    /// <summary>
    /// issues/13 M6：控制面 <c>POST /game/scan</c> 的宿主实现——
    /// 与 UI 扫描共用 <see cref="BridgeHost.ResolveDirAccessor"/>，
    /// 使 Android 的 SAF <c>content://</c> 树 URI 也能枚举（Core 默认实现硬编码
    /// <c>FileSystemGameDirAccessor</c>，对 content URI 一律不可用）。
    /// </summary>
    /// <returns>扫描结果；异常时返回 <c>null</c> 回落 Core 默认实现。</returns>
    private HttpResult? ScanGamesForControlServer(string rootDir)
    {
        try
        {
            var accessor = BridgeHost.ResolveDirAccessor(rootDir);
            var exists = GameScanner.RootDirectoryExists(rootDir, accessor);
            var games = exists ? GameScanner.Scan(rootDir, accessor) : Array.Empty<GameEntry>();
            var arr = new JsonArray();
            foreach (var g in games)
            {
                arr.Add((JsonNode)new JsonObject { ["name"] = g.Name, ["fullPath"] = g.FullPath });
            }
            var payload = new JsonObject
            {
                ["rootDir"] = rootDir,
                ["rootDirExists"] = exists,
                ["games"] = arr,
            }.ToJsonString();
            Console.WriteLine($"[maui] control scan: rootDir={rootDir}, {games.Count} games");
            return HttpResult.Text(payload);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[maui] control scan failed, falling back: {ex}");
            return null;
        }
    }

    /// <summary>
    /// issues/13 M6：控制面 <c>POST /load-game</c> 的宿主实现——
    /// 只暂存路径并请 Vue 代加载，<b>不在此同步初始化运行时</b>（IO 重，且会绕过 Vue 状态）。
    /// <para>
    /// 返回 <c>accepted</c> 表示已受理，实际加载是异步的——调用方需轮询 <c>GET /state</c>
    /// 等 <c>gameDir</c> 变为目标路径。
    /// </para>
    /// </summary>
    /// <returns>
    /// 受理结果；<c>null</c> 表示不接管（空路径等，回落 Core 默认实现的 400 语义）；
    /// token 不匹配时返回 403。
    /// </returns>
    private HttpResult? RequestAutoLoadGame(string gameDir, ControlIdentity? identity)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
            return null;

        // token 校验：提供了就必须匹配——避免 localhost 上串台或误触。
        // 未提供时放行（便于手动 curl 调试；风险面仅限本机 loopback）。
        var expected = MauiControlServer.Instance?.Token;
        var presented = identity?.Token;
        if (!string.IsNullOrEmpty(presented) && presented != expected)
        {
            return HttpResult.Text(
                """{"error":{"code":"BAD_TOKEN","message":"token mismatch"}}""",
                statusCode: 403);
        }

        Console.WriteLine($"[maui] control load-game accepted: {gameDir}");
        _autoLoadPendingPath = gameDir;
        if (_vueReady)
            FlushAutoLoad();

        var payload = new JsonObject
        {
            ["accepted"] = true,
            ["gameDir"] = gameDir,
            ["state"] = _vueReady ? "dispatched" : "pending",
        }.ToJsonString();
        return HttpResult.Text(payload);
    }

    /// <summary>ADR-0019：防止 auto-start 被多次触发。
    /// <para>
    /// 与 <see cref="OnReloadGame"/> 的区别：
    /// <list type="bullet">
    ///   <item><see cref="OnReloadGame"/>：用户选了新游戏 → 后台 init 新运行时 → 重建 host + Start</item>
    ///   <item><see cref="OnGameExited"/>：用户退出当前游戏 → 不 init（保留旧运行时配置供 scanGames 等基础消息处理）→
    ///       重建 host 不 Start（等 Vue 触发 scanGames 后用户再选新游戏触发 OnReloadGame）</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>为何保留旧运行时配置</b>：scanGames / listDirectories / exitGame 等基础消息不依赖 ConfigData/ITerminalSetup，
    /// 但 BridgeHost 构造签名要求这俩参数（用于 PushLayoutMessage / PushConfigMessage）。
    /// 退出后这些 push 不再发生（新 host 不 Start），故旧值无副作用。
    /// 用户选新游戏时 <see cref="OnReloadGame"/> 会重新 init 替换。
    /// </para>
    /// </summary>
    private void OnGameExited()
    {
        Console.WriteLine("[maui] OnGameExited: recreating BridgeHost without starting game loop");
        // RecreateHost 内已 Dispose 旧 host——但此时我们正处在旧 host 的 HandleExitGame 调用栈内，
        // RecreateHost 会 Dispose 当前 host（_disposed=true）。这是安全的——HandleExitGame 后续无访问 host 字段。
        // 新 host 订阅 InputReceived 后接管后续消息（Vue 发的 scanGames）。
        RecreateHost();
    }

    /// <summary>
    /// 读取启动日志覆盖三态（DisplayReport override）。返回 <c>"auto"/"off"/"on"</c>；
    /// 新 key 缺失时迁移旧 bool key（true/缺省 → off；false → auto）。
    /// Preferences 不可用（极少见）时回退默认 off（抑制启动日志）。
    /// </summary>
    private static string ReadDisplayReportOverridePreference()
    {
        try
        {
            var value = Preferences.Get(BridgeHost.DisplayReportOverrideKey, null);
            if (value is "auto" or "off" or "on")
                return value;
            var legacy = Preferences.Get(BridgeHost.NoLoadingReportKey, true);
            return legacy ? "off" : "auto";
        }
        catch
        {
            return "off";
        }
    }

    /// <summary>
    /// issue 09 hot-swap reload 回调——<see cref="BridgeHost.HandleLoadGame"/> 收到
    /// <c>{"type":"loadGame","path":...}</c> 后调此方法。
    /// <para>
    /// <b>步骤</b>：
    /// <list type="number">
    ///   <item>后台线程跑 <see cref="EmueraRuntimeInitializer.Initialize"/> + <see cref="GamePaths.Validate"/>——
    ///     避免阻塞 UI（加载 config / 语言文件耗时）</item>
    ///   <item>成功：UI 线程替换 <see cref="_configData"/> / <see cref="_terminalSetup"/> +
    ///     <see cref="RecreateHost"/> + <see cref="BridgeHost.Start"/>（Vue 已 ready，直接启动）</item>
    ///   <item>失败：用旧 host 推 error turn 给 Vue 让用户看到错误信息，旧 host 继续运行</item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>线程</b>：BridgeHost.HandleLoadGame 在 OnInputFromJs 内调此方法（UI 线程）；
    /// 后台初始化用 <see cref="Task.Run(System.Action)"/>；UI 重建用 <see cref="IDispatcher.Dispatch"/>。
    /// </para>
    /// </summary>
    /// <param name="gamePath">用户选中的游戏目录绝对路径。</param>
    private void OnReloadGame(string gamePath)
    {
        Console.WriteLine($"[maui] OnReloadGame: {gamePath}");
#if ANDROID
        Android.Util.Log.Info("EmueraMaui", $"OnReloadGame: {gamePath}");
#endif
        // 后台线程跑 IO 重型初始化——避免 UI 卡顿（加载 config / 语言文件）
        _ = Task.Run(() =>
        {
            ConfigData? newConfig = null;
            ITerminalSetup? newTerminal = null;
            Exception? initError = null;
            try
            {
                // ADR-0019：按路径形态选 DirAccessor——SAF URI 用 SafGameDirAccessor，本地路径用 FileSystem。
                // issues/15：形态判据统一走 SafPath.IsContentUri（此前是第四份手写 StartsWith）。
#if ANDROID
                var dirAccessor = (IGameDirAccessor?)(
                    SafPath.IsContentUri(gamePath)
                        ? SafGameDirAccessor.Instance
                        : new FileSystemGameDirAccessor());
#else
                // Windows: 不支持 SAF content URI
                IGameDirAccessor? dirAccessor = new FileSystemGameDirAccessor();
#endif
                if (dirAccessor == null)
                {
                    initError = new InvalidOperationException("DirAccessor not available for SAF path");
                    return;
                }
                var paths = GamePaths.Resolve(gamePath, dirAccessor);
                paths.Validate(); // 校验失败抛 GamePathValidationException
#if ANDROID
                if (SafPath.IsContentUri(gamePath)
                    && dirAccessor is SafGameDirAccessor saf)
                {
                    var safHasWrite = dirAccessor.HasWriteAccess();
                    var probeOk = saf.TryWriteProbe(out var probeDetail);
                    if (!safHasWrite || !probeOk)
                    {
                        throw new UnauthorizedAccessException(
                            "SAF game directory does not have usable write permission. " +
                            "Please re-select the game directory and allow write access. " +
                            $" Probe: {probeDetail}");
                    }
                    Android.Util.Log.Info("EmueraMaui",
                        $"OnReloadGame SAF write probe OK: {probeDetail}");
                }
#endif
                (newConfig, newTerminal) = EmueraRuntimeInitializer.Initialize(
                    paths,
                    dirAccessor,
                    // 三态偏好（auto/off/on）→ bool?（null/false/true）
                    BridgeHost.ParseDisplayReportOverride(ReadDisplayReportOverridePreference()));
                // ADR-0030：全局设置应用（perf 覆盖/诊断旋钮/displayReport/agentLog）——Initialize 后、
                // 会话创建前压值；旧 BridgeHost.Apply* 已上收共享 AppSettings.ApplyTo，行为逐位一致。
                _appSettings.ApplyTo(newConfig);
                var hasWrite = dirAccessor.HasWriteAccess();
                Console.WriteLine($"[maui] OnReloadGame init completed: ExeDir={paths.ExeDir}, hasWrite={hasWrite}");
#if ANDROID
                Android.Util.Log.Info("EmueraMaui",
                    $"OnReloadGame init completed: ExeDir={paths.ExeDir}, hasWrite={hasWrite}");
                if (!hasWrite && SafPath.IsContentUri(gamePath))
                {
                    Android.Util.Log.Warn("EmueraMaui",
                        "OnReloadGame: SAF tree has no write permission — saves will fail until user re-picks directory");
                }
#endif
            }
            catch (Exception ex)
            {
                initError = ex;
                Console.WriteLine($"[maui] OnReloadGame init failed: {ex}");
            }

            // 回 UI 线程——BridgeHost ctor 订阅 InputReceived 事件 + Start 必须 UI 线程
            Dispatcher.Dispatch(() =>
            {
                if (initError != null || newConfig == null || newTerminal == null)
                {
                    // 初始化失败——用旧 host 推 error turn 给 Vue，旧 host 继续运行
                    Console.WriteLine($"[maui] OnReloadGame keeping old host, error: {initError?.Message}");
                    _host?.ShowError($"重新加载游戏失败: {initError?.Message}");
                    return;
                }

                _configData = newConfig;
                _terminalSetup = newTerminal;
                RecreateHost();
                // issues/13 M6：主目录可能随本次 scanGames 改变——同步给控制面刷新发现记录
                MauiControlServer.Instance?.NotifyMainGameDir(_host?.MainGameDir);
                // Vue 已 ready（首次 ready 信号早已收到），直接启动新游戏循环
                _host?.Start();
                Console.WriteLine("[maui] OnReloadGame: new BridgeHost started");
            });
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        // issues/13 M6：解绑控制面回调——sink 持有本页实例方法，不摘会让已销毁的 MainPage
        // 被 app 级 MauiControlServer 钉住（每次页面重建泄漏一个）。
        var controlServer = MauiControlServer.Instance;
        if (controlServer != null)
        {
            controlServer.LoadGameSink = null;
            controlServer.ScanGameDirSink = null;
        }
        _host?.Dispose();
    }

    /// <summary>
    /// game-library spec ID10：Android 物理返回键处理。
    /// <para>
    /// 游戏运行中（<see cref="BridgeHost.IsGameRunning"/> 为 true）时拦截返回键，
    /// 投递 <c>backButtonPressed</c> 消息给 Vue——Vue 端弹出退出确认对话框。
    /// 用户确认后调 <c>exitGame</c> 流程；取消时无操作。
    /// </para>
    /// <para>
    /// 列表态（无活跃游戏）不拦截——走系统默认行为（最小化 app）。
    /// </para>
    /// </summary>
    /// <returns><c>true</c> 表示已处理返回键，阻止默认行为。</returns>
    protected override bool OnBackButtonPressed()
    {
        // ADR-0030：游戏运行中（既有）或设置页覆盖层打开（列表页/游戏页都算）→ 转发给 Vue，
        // 由 Vue 按层级处理（设置页→先关设置；否则弹退出确认）。其余（纯列表页无覆盖层）
        // 保持 spec ID10：不拦截，走系统默认（最小化/退出）。
        if (_host != null && (_host.IsGameRunning || _host.IsSettingsOpen))
        {
            Console.WriteLine("[maui] OnBackButtonPressed: forwarding to Vue (game running or settings open)");
            // 投递 backButtonPressed 给 Vue——Vue 端按层级处理
            var msg = System.Text.Json.JsonSerializer.Serialize(
                new BackButtonPressedMessage("backButtonPressed"),
                MauiJsonContext.Default.BackButtonPressedMessage);
            _jsBridge.PostMessage(msg);
            return true; // 已处理，阻止系统默认行为
        }
        Console.WriteLine("[maui] OnBackButtonPressed: no game running, default behavior");
        return base.OnBackButtonPressed();
    }
}
