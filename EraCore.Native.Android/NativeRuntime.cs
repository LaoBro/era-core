using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Util;
using EraCore.Hosting;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Sessions;

namespace EraCore.Native.Android;

/// <summary>
/// 进程级原生会话管理：同一时刻最多一个 <see cref="GameHost"/>。
///
/// <para>游戏库切换时先 <see cref="GameHost.StopAsync"/> 再 <see cref="GameHost.DisposeAsync"/>
/// （后者内部会再次 Stop 并释放 <see cref="CoreSession"/>），避免两个游戏循环并行。
/// UI 只轮询快照/元信息/状态，提交输入经 <see cref="Submit"/>。</para>
/// </summary>
internal static class NativeRuntime
{
    private const string LogTag = "EraCoreNative";

    private static readonly object Gate = new();
    private static readonly SemaphoreSlim SessionGate = new(1, 1);

    private static volatile GameHost? _host;
    private static volatile string? _currentRoot;
    private static volatile string _status = "正在准备游戏资源…";
    private static volatile bool _ready;
    private static Context? _context;

    public static bool IsReady => _ready;

    internal static Context? AppContext => _context;

    public static string? CurrentRoot => _currentRoot;

    public static string CurrentGameName => _currentRoot is null ? "未打开游戏" : Path.GetFileName(_currentRoot);

    public static HostMetrics Metrics => _host?.Metrics ?? NativeDefaults.Metrics;

    /// <summary>当前会话的游戏文件门面（图片字节读取/sprite 名解析）；无会话时为 null。</summary>
    public static GameTree? Files => _host?.Files;

    public static DisplaySnapshot? Snapshot => _host?.Snapshot;

    public static CoreSessionState State => _host?.State ?? CoreSessionState.Created;

    public static NativeRenderStyle Style => NativeRenderStyle.From(Metrics, NativeSettings.Current);

    public static string Status
    {
        get
        {
            var host = _host;
            return host is null ? _status : _status + " · " + host.State;
        }
    }

    /// <summary>启动平台层：加载设置/内置字体，并在后台解压内置示例到 <c>games/*</c>。</summary>
    public static void Initialize(Context context)
    {
        lock (Gate)
        {
            if (_context is not null)
                return;
            _context = context.ApplicationContext ?? context;
        }

        NativeSettings.Initialize(_context);
        NativeFontManager.Initialize(_context);

        _ = Task.Run(async () =>
        {
            try
            {
                SetStatus("正在准备内置示例…");
                await GameLibrary.EnsureBuiltInGamesAsync(_context).ConfigureAwait(false);
                _ready = true;
                SetStatus("就绪");
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, "built-in game extraction failed: " + ex);
                _ready = true;
                SetStatus("内置示例解压失败：" + ex.Message);
            }
        });
    }

    /// <summary>打开/切换游戏。失败时返回 false，旧会话已停止，状态栏保留失败原因。</summary>
    public static async Task<bool> OpenGameAsync(GameLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var context = _context ?? throw new InvalidOperationException("NativeRuntime 尚未初始化。");

        await SessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.Source == GameSourceKind.Path && !entry.IsAvailable)
            {
                var reason = entry.UnavailableReason ?? "目录不可用";
                Log.Warn(LogTag, $"session refused: {entry.Name} reason={reason}");
                SetStatus($"无法打开 {entry.Name}：{reason}（可在库中移除该记录）");
                return false;
            }

            await CloseCoreAsync("switch").ConfigureAwait(false);

            SetStatus($"正在启动 {entry.Name}…");
            Log.Info(LogTag,
                $"session start requested: {entry.Name} source={entry.Source} root={entry.RootPath}");

            // Path 来源与 Internal 来源都是真实目录：FileSystemGameDirAccessor 零拷贝直读，
            // 存档/日志直接写回该目录；Path 不做任何复制、不写 marker。
            var host = new GameHost(
                new CoreSessionOptions(entry.RootPath, new FileSystemGameDirAccessor())
                {
                    TeardownTimeout = TimeSpan.FromSeconds(15),
                },
                RenderCapability.Text | RenderCapability.Images |
                RenderCapability.Shapes | RenderCapability.BackgroundImages);

            var outcome = await host.StartAsync().ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                Log.Error(LogTag, $"session start failed: {outcome.ErrorCode} {outcome.ErrorMessage}");
                SetStatus($"启动失败：{outcome.ErrorCode} {outcome.ErrorMessage}");
                await host.DisposeAsync().ConfigureAwait(false);
                return false;
            }

            _host = host;
            _currentRoot = entry.RootPath;
            GameLibrary.RecordPlayed(context, entry);

            var metrics = host.Metrics;
            SetStatus("运行中");
            Log.Info(LogTag,
                $"session started: game={entry.Name} root={entry.RootPath} " +
                $"font={metrics.FontName}/{metrics.FontSize} line={metrics.LineHeight} " +
                $"width={metrics.WindowWidth} maxLog={metrics.MaxLog}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "open game failed: " + ex);
            SetStatus("启动异常：" + ex.Message);
            return false;
        }
        finally
        {
            SessionGate.Release();
        }
    }

    /// <summary>停止当前会话（回游戏库 / 退出应用）。</summary>
    public static async Task CloseGameAsync()
    {
        await SessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCoreAsync("close").ConfigureAwait(false);
            SetStatus(_ready ? "就绪" : _status);
        }
        finally
        {
            SessionGate.Release();
        }
    }

    public static void Submit(InputFrame frame)
    {
        var host = _host;
        host?.Submit(frame);
    }

    /// <summary>Activity 真正退出时调用；不等待，只保证会话被停止。</summary>
    public static void StopOnExit()
    {
        var host = _host;
        _host = null;
        _currentRoot = null;
        if (host is null)
            return;
        Log.Info(LogTag, "session stopping on exit");
        _ = Task.Run(async () =>
        {
            try
            {
                await host.StopAsync().ConfigureAwait(false);
                await host.DisposeAsync().ConfigureAwait(false);
                Log.Info(LogTag, "session stopped on exit");
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, "stop on exit failed: " + ex.Message);
            }
        });
    }

    private static async Task CloseCoreAsync(string reason)
    {
        var host = _host;
        _host = null;
        _currentRoot = null;
        if (host is null)
            return;

        var root = host.Files.Root;
        try
        {
            await host.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, $"session stop failed ({reason}): {ex.Message}");
        }

        await host.DisposeAsync().ConfigureAwait(false);
        Log.Info(LogTag, $"session stopped ({reason}): root={root}");
    }

    private static void SetStatus(string status)
    {
        _status = status;
        Log.Info(LogTag, status);
    }
}
