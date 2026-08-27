using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView; // AgentLog（Configure 在启动期调用）
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
#if ANDROID
using Android.Webkit;
using Microsoft.Maui.Handlers;
#endif

namespace EraCore.Maui;

/// <summary>
/// MAUI 应用入口工厂——issue 07 / spec ID8。
/// <para>
/// <b>无游戏启动（2026-08-06 移除 test_game 打包后）</b>：
/// 启动期不 Resolve 游戏目录、不 <see cref="EmueraRuntimeInitializer.Initialize"/>——
/// <c>GamePaths.Current</c> 保持 null（<c>BridgeHost.HandleReady</c> 据此跳过 Start），
/// DI 注册占位 <see cref="ConfigData"/> / <see cref="ITerminalSetup"/>（无游戏时不消费）。
/// 用户经游戏列表 / 目录选择器选游戏后，<c>MainPage.OnReloadGame</c> 才
/// <see cref="EmueraRuntimeInitializer.Initialize"/> 重建真实实例并 Start 游戏循环。
/// </para>
/// <para>
/// 历史（已移除）：<c>GameResourceExtractor.EnsureGameDirAsync</c> 曾解压内置 <c>test_game/</c>
/// 到 <c>FileSystem.AppDataDirectory/emuera/</c> 后启动期直接初始化并自动进入游戏——
/// 该机制随 test_game 打包移除而退役（用户可选任意游戏目录，无需内置示例）。
/// </para>
/// <para>
/// <b>调试 Console</b>：MAUI Windows app 是 WinUI 进程（OutputType=WinExe），默认无 console 窗口——
/// <c>Console.WriteLine</c> 输出丢失。DEBUG 构建下用 <see cref="AllocConsole"/> 创建 console 窗口，
/// 让 <c>BridgeHost</c> / <c>WindowsJsBridge</c> 的 <c>Console.WriteLine</c> 诊断日志可见。
/// Release 构建不创建 console（避免终端用户看到调试输出）。
/// </para>
/// </summary>
public static class MauiProgram
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    public static MauiApp CreateMauiApp()
    {
        // DEBUG 构建下创建 console 窗口——WinUI app 默认无 console，Console.WriteLine 输出会丢失。
        // dotnet run -c Debug 时此 console 是诊断日志唯一可见通道（VS 调试器 Output 窗口仅 VS 内可见）。
#if DEBUG
        AllocConsole();
        Console.WriteLine("[maui] MauiProgram.CreateMauiApp starting (DEBUG console allocated)");
#endif

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

		try
		{
			AppDataPaths.Configure(FileSystem.AppDataDirectory);
#if ANDROID
			// erb-bounded-lifecycle 观测收尾：EmueraLog → logcat。Release 下 DebuggerSink 空操作、
			// stderr 丢失——真机/模拟器 logcat 此前看不到 LoadPendingParallel / phase=genmerge 等
			// 阶段标记，崩溃判别缺时间线。级别 → LogPriority，tag 用 "Emuera" 与桥接层 "EmueraMaui" 区分。
			EmueraLog.SetPlatformSink((level, line) => Android.Util.Log.WriteLine(
				level switch
				{
					EmueraLog.Level.Debug => Android.Util.LogPriority.Debug,
					EmueraLog.Level.Info => Android.Util.LogPriority.Info,
					EmueraLog.Level.Warn => Android.Util.LogPriority.Warn,
					EmueraLog.Level.Error => Android.Util.LogPriority.Error,
					_ => Android.Util.LogPriority.Info,
				}, "Emuera", line));
#endif
			// === A0：AgentLog 默认关闭 + UI 开关（saf-accel 计划）===
			// 读 Preferences（key 与 BridgeHost 设置页开关共用，默认 false）→ Configure。
			// 时序要求：Configure 必须在首次 AgentLog.Instance 访问之前（Lazy 单例构造时机）——
			// BridgeHost 等一切 Instance 访问都晚于此（游戏循环启动后才写日志）。
			// 运行时开关由设置页经 BridgeHost 处理：写 Preferences + AgentLog.Enabled 即时切换。
			bool agentLogEnabled = false;
			try { agentLogEnabled = Preferences.Get(BridgeHost.AgentLogEnabledKey, false); }
			catch { /* Preferences 不可用（极少见）——保持默认关闭 */ }
			AgentLog.Configure(agentLogEnabled);
			Console.WriteLine($"[maui] AgentLog configured: enabled={agentLogEnabled}");
			// === spec ID8 启动编排（2026-08-06 改为无游戏启动） ===
            // test_game 内置打包已移除（见 csproj MauiAsset 注释）——启动期不再解压/Resolve/Initialize：
            // - GamePaths.Current 保持 null——BridgeHost.HandleReady 据此跳过 Start（无游戏可跑），
            //   等用户经游戏列表 / 目录选择器选游戏（loadGame → MainPage.OnReloadGame 重建 host + Start）
            // - configData/terminalSetup 用占位实例注册 DI——无游戏时不消费（BridgeHost 不 Start）；
            //   用户选游戏后 MainPage.OnReloadGame 经 EmueraRuntimeInitializer.Initialize
            //   重建真实实例（含 LoadConfig）并替换字段
            var configData = new ConfigData();
            var terminalSetup = EmueraRuntimeInitializer.CreateTerminalSetup();
            Console.WriteLine("[maui] no-game-start: placeholder ConfigData/TerminalSetup registered, waiting for loadGame");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", "no-game-start: placeholder ConfigData/TerminalSetup registered");
#endif

            // DI 注册单例——MainPage 经 DI 注入，重建时不重新初始化运行时
            builder.Services.AddSingleton(configData);
            builder.Services.AddSingleton(terminalSetup);
            // MainPage ctor 是 internal（参数类型 ConfigData/ITerminalSetup 在 Core 内 internal）——
            // 用 factory delegate 显式构造，绕过 ActivatorUtilities 仅扫 public ctor 的限制。
            builder.Services.AddTransient<MainPage>(sp =>
                new MainPage(
                    sp.GetRequiredService<ConfigData>(),
                    sp.GetRequiredService<ITerminalSetup>()));

            Console.WriteLine("[maui] MauiProgram.CreateMauiApp completed, returning built app");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", "CreateMauiApp completed, returning built app");
#endif

#if ANDROID
            // Android WebView 配置：JavaScript + DOM Storage + 文件访问 + console 日志捕获
            Android.Util.Log.Info("EmueraMaui", "ConfigureAndroidWebView called before Build");
            ConfigureAndroidWebView();
            Android.Util.Log.Info("EmueraMaui", "ConfigureAndroidWebView completed");
#endif

            return builder.Build();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[maui] FATAL: CreateMauiApp failed: {ex}");
#if ANDROID
            Android.Util.Log.Info("EmueraMaui", $"FATAL: CreateMauiApp failed: {ex}");
#endif
            throw;
        }
    }

#if ANDROID
    /// <summary>
    /// Android WebView handler 自定义配置——启用 JavaScript / DOM Storage / 文件访问，
    /// 并挂载 LoggingWebChromeClient 将 console.log/warn/error 输出到 adb logcat。
    /// </summary>
    private static void ConfigureAndroidWebView()
    {
        // 启用 WebView 远程调试——chrome://inspect 可连接
        Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);

        WebViewHandler.Mapper.AppendToMapping("emueraWebView", (handler, view) =>
        {
            if (handler.PlatformView is Android.Webkit.WebView wv)
            {
                wv.Settings.JavaScriptEnabled = true;
                wv.Settings.DomStorageEnabled = true;
                // 禁用原生捏合缩放——画面缩放统一由前端控制（同 WindowsJsBridge 说明）
                wv.Settings.SetSupportZoom(false);
                wv.Settings.BuiltInZoomControls = false;
                wv.Settings.AllowFileAccess = true;
                // file:// 协议下允许加载外部 JS/CSS——Vite 构建产物使用 ES Module + 独立 CSS 文件，
                // 若不加此行会因 CORS 策略被拦截导致白屏
                wv.Settings.AllowFileAccessFromFileURLs = true;
                wv.Settings.AllowUniversalAccessFromFileURLs = true;
                wv.SetWebChromeClient(new LoggingWebChromeClient());
            }
        });
    }

    /// <summary>
    /// 自定义 WebChromeClient——将 WebView 控制台日志转发到 System.Diagnostics.Debug，
    /// 通过 adb logcat 可见（过滤 tag="EmueraWV"）。
    /// </summary>
    private sealed class LoggingWebChromeClient : WebChromeClient
    {
        public override bool OnConsoleMessage(ConsoleMessage? consoleMessage)
        {
            if (consoleMessage != null)
            {
                var msg = $"[WV] {consoleMessage.Message()} (line {consoleMessage.LineNumber()}, {consoleMessage.SourceId()})";
                System.Diagnostics.Debug.WriteLine(msg, "EmueraWV");
            }
            return base.OnConsoleMessage(consoleMessage);
        }
    }
#endif
}
