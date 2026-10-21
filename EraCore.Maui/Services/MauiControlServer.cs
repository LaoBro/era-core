using System;
using System.IO;
using System.Text.Json.Nodes;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using MinorShift.Emuera.Terminal.Platform;

namespace EraCore.Maui.Services;

/// <summary>
/// app 级托管控制 server（issues/13 M6）——生命周期与游戏会话解耦。
/// <para>
/// <b>为何存在</b>：改造前 <see cref="HttpListenerHost"/> 由 <see cref="BridgeHost.Start"/> 创建，
/// 而 <c>Start()</c> 只在用户手点游戏、走完 <c>OnReloadGame</c> 之后才被调用——
/// 即「server 比『加载游戏』晚出生」。结果是 eracore_agent 在无会话时永远探不到 MAUI，
/// 只能回退拉起 standalone CLI server（测的是 headless 路径，不是 MAUI）。
/// 本类把 server 的创建提到 app 启动期，使 agent 在<b>未加载游戏</b>时也能连上并驱动加载。
/// </para>
/// <para>
/// <b>单一 server 原则</b>：会话必须建在本类持有的 host 上。若仍由 <see cref="BridgeHost"/> 各起一个，
/// 会变成两套 server 抢同一个发现文件——agent 连到的端口上没有会话，<c>GET /state</c> 恒为空。
/// 故 <see cref="BridgeHost"/> 改为复用 <see cref="Host"/>，只负责建/销毁会话，不再 own server。
/// </para>
/// <para>
/// <b>ConfigData 时序</b>：构造时的 <c>ConfigData</c> 是启动期占位实例（此时用户还没选游戏）。
/// 真实配置由 <see cref="BridgeHost"/> 在 <c>OnReloadGame</c> 后经
/// <c>CreateMauiSessionAsync(ConfigData, bool)</c> 显式注入——不能依赖 host 内部的
/// <c>_configService.Current</c>（那会是占位配置）。
/// </para>
/// </summary>
internal sealed class MauiControlServer
{
    /// <summary>logcat 标记行前缀——Android 上 PC 端据此解析端口（发现文件在 app 私有目录，PC 不可读）。</summary>
    private const string LogcatTag = "EmueraMaui";
    private const string LogcatPortMarker = "eracore-control-server";

    private static readonly object Gate = new();
    private static MauiControlServer? _instance;

    private readonly string? _discoveryPath;
    private string? _mainGameDir;

    private MauiControlServer(ConfigData configData, ITerminalSetup terminalSetup, AppSettings appSettings)
    {
        Token = Guid.NewGuid().ToString("N");
        // 注入的是"转发委托"——真正实现由 MainPage 在构造完成后注册到 ScanGameDirSink / LoadGameSink。
        // 为何不直接传实现：HttpListenerHost 的委托是构造参数（readonly），而 MainPage 此刻尚未构造完
        // （EnsureStarted 由 MainPage ctor 调用），拿不到它的实例方法。转发委托把绑定推迟到调用时。
        Host = new HttpListenerHost(
            port: 0,
            terminalSetup,
            configData,
            displayReportOverride: configData.OverrideDisplayReport,
            scanGameDirHandler: rootDir => ScanGameDirSink?.Invoke(rootDir),
            loadGameHandler: (gameDir, identity) => LoadGameSink?.Invoke(gameDir, identity),
            appSettings: appSettings);
        _discoveryPath = DiscoveryFilePath();
        WriteDiscoveryRecord(null);
        Console.WriteLine($"[maui] control server on port {Host.Port} (no session yet)");
    }

    /// <summary>当前 app 级控制 server；未启动时为 <c>null</c>。</summary>
    public static MauiControlServer? Instance => _instance;

    /// <summary>共享的托管 server——<see cref="BridgeHost"/> 经此建会话，不自行创建。</summary>
    public HttpListenerHost Host { get; }

    /// <summary>发现记录里的 agent 身份 token（所有权在 MAUI：由本类生成，agent 读取后作为凭证）。</summary>
    public string Token { get; }

    /// <summary>实际监听端口——agent 发现机制读取。</summary>
    public int Port => Host.Port;

    /// <summary>
    /// <c>POST /game/scan</c> 的宿主实现——由 <see cref="MainPage"/> 注册（带 SAF accessor 的扫描）。
    /// 为 <c>null</c> 时端点回落默认实现（硬编码 FileSystem accessor，Android SAF 路径不可用）。
    /// <para>
    /// 参数为 <c>null</c> = 「扫本宿主的主目录」：控制面（agent）因此不必持有路径——
    /// 它手上的路径会随权限/主目录变更而过期，而宿主取用自己当前生效的主目录永远是新的。
    /// </para>
    /// </summary>
    public Func<string?, HttpResult?>? ScanGameDirSink { get; set; }

    /// <summary>
    /// <c>POST /load-game</c> 的宿主实现——由 <see cref="MainPage"/> 注册（转交 <c>OnReloadGame</c> 链路）。
    /// 为 <c>null</c> 时端点回落默认实现（会绕过 MAUI 运行期初始化与性能覆盖旋钮，WebView 也收不到 turn）。
    /// </summary>
    public Func<string, ControlIdentity?, HttpResult?>? LoadGameSink { get; set; }

    /// <summary>
    /// 幂等启动——首次调用创建单例，后续调用忽略（app 级单一实例）。
    /// 调用点：<see cref="MainPage"/> 构造（DI 已备好 ConfigData / ITerminalSetup）。
    /// </summary>
    public static MauiControlServer EnsureStarted(ConfigData configData, ITerminalSetup terminalSetup, AppSettings appSettings)
    {
        var existing = _instance;
        if (existing != null)
            return existing;
        lock (Gate)
        {
            var current = _instance;
            if (current != null)
                return current;
            try
            {
                current = new MauiControlServer(configData, terminalSetup, appSettings);
            }
            catch (Exception ex)
            {
                // 控制 server 起不来不应静默降级——那会让 agent 连不上却无任何痕迹。
                // 抛给调用方：MainPage 构造失败是可观测的硬错误。
                Console.WriteLine($"[maui] control server start failed: {ex}");
                throw;
            }
            _instance = current;
            return current;
        }
    }

    /// <summary>
    /// 会话已建立（游戏加载完成）——更新发现记录里的 gameDir。
    /// <see cref="BridgeHost.StartHostedSessionAsync"/> 建会话成功后调用。
    /// </summary>
    public void NotifySessionStarted(string? gameDir)
        => WriteDiscoveryRecord(gameDir);

    /// <summary>
    /// 会话已结束（用户退出游戏 / hot-swap 让位）——重写发现记录但 gameDir 置空，
    /// 使 agent 仍能看到 MAUI 在线（否则发现文件被删，agent 又回到「探不到」状态）。
    /// </summary>
    public void NotifySessionEnded()
        => WriteDiscoveryRecord(null);

    /// <summary>
    /// 游戏库主目录（权威源在 <see cref="BridgeHost"/> 的 Preferences）——由 <see cref="MainPage"/> 同步。
    /// <para>
    /// 为何外泄：CLI 的 <c>maui games</c> / <c>maui load --name</c> 需要它做目录扫描与名字解析。
    /// Android 上该值是 SAF <c>content://</c> 树 URI，手写到命令行极易出错——
    /// 让 app 说出主目录，CLI 就不必让用户去记这串 URI。
    /// </para>
    /// </summary>
    public void NotifyMainGameDir(string? mainGameDir)
    {
        if (_mainGameDir == mainGameDir)
            return;
        _mainGameDir = mainGameDir;
        WriteDiscoveryRecord(gameDir: null);
    }

    /// <summary>
    /// 发现记录路径——Windows <c>%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json</c>。
    /// Android 上该路径在 app 私有目录，PC 不可读——故额外打 logcat（见 <see cref="WriteDiscoveryRecord"/>）。
    /// </summary>
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

    /// <summary>
    /// 写发现记录 <c>{host,port,token,gameDir,startedByAgent:false,mauiHosted:true}</c> +
    /// Android 上打一行 logcat（PC 端 <c>adb logcat -d -s EmueraMaui</c> 解析端口用）。
    /// </summary>
    private void WriteDiscoveryRecord(string? gameDir)
    {
        var record = new JsonObject
        {
            ["host"] = "127.0.0.1",
            ["port"] = Port,
            ["token"] = Token,
            ["gameDir"] = gameDir,
            ["mainGameDir"] = _mainGameDir,
            ["startedByAgent"] = false,
            ["mauiHosted"] = true,
        }.ToJsonString();

        if (_discoveryPath != null)
        {
            try
            {
                var dir = Path.GetDirectoryName(_discoveryPath);
                if (dir != null)
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_discoveryPath, record);
                Console.WriteLine($"[maui] discovery record written: {_discoveryPath} port={Port}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[maui] discovery record write failed: {ex.Message}");
            }
        }

        // Android：发现文件在 app 私有目录、PC 不可读——logcat 是唯一通道。
        // 格式固定便于 CLI 解析：eracore-control-server port=<P> token=<T> gameDir=<G|null> mainGameDir=<M|null>
        // Windows 也打同一行（Console）：DEBUG 下有 console 窗口，与 Android 的解析逻辑保持同构。
        var marker = $"{LogcatPortMarker} port={Port} token={Token} gameDir={gameDir ?? "null"} mainGameDir={_mainGameDir ?? "null"}";
        Console.WriteLine($"[maui] {marker}");
#if ANDROID
        Android.Util.Log.Info(LogcatTag, marker);
#endif
    }
}
