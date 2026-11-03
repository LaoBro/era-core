using Android.Content;
using Android.Util;
using EraCore.Hosting;
using EraCore.Native;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Sessions;

namespace EraCore.Native.Android;

/// <summary>
/// 进程级原生会话（M2 仍只有一个会话）：解压资源、后台启动 <see cref="GameHost"/>，
/// 给 UI 提供可轮询的快照/元信息/状态与输入提交。
/// </summary>
internal static class NativeRuntime
{
    private const string LogTag = "EraCoreNative";

    private static readonly object Gate = new();
    private static volatile GameHost? _host;
    private static volatile string _status = "正在准备游戏资源…";
    private static bool _starting;

    public static HostMetrics Metrics => _host?.Metrics ?? NativeDefaults.Metrics;

    public static DisplaySnapshot? Snapshot => _host?.Snapshot;

    public static CoreSessionState State => _host?.State ?? CoreSessionState.Created;

    public static string Status
    {
        get
        {
            var host = _host;
            if (host is null)
                return _status;
            return _status + " · " + host.State;
        }
    }

    public static void EnsureStarted(Context context)
    {
        lock (Gate)
        {
            // Activity 重建时 _host 仍在：直接复用，不重启游戏循环。
            if (_host is not null || _starting)
                return;
            _starting = true;
        }

        Task.Run(async () =>
        {
            try
            {
                SetStatus("正在解压 test_game…");
                var gameRoot = AndroidGameAssets.EnsureGame(context);

                SetStatus("正在启动会话…");
                var host = new GameHost(
                    new CoreSessionOptions(gameRoot, new FileSystemGameDirAccessor())
                    {
                        TeardownTimeout = TimeSpan.FromSeconds(15),
                    },
                    RenderCapability.Text);

                var outcome = await host.StartAsync().ConfigureAwait(false);
                if (!outcome.Succeeded)
                {
                    Log.Error(LogTag, $"session start failed: {outcome.ErrorCode} {outcome.ErrorMessage}");
                    SetStatus($"启动失败：{outcome.ErrorCode} {outcome.ErrorMessage}");
                    await host.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                _host = host;
                SetStatus("运行中");
                var metrics = host.Metrics;
                Log.Info(LogTag,
                    $"session started: root={gameRoot} font={metrics.FontName}/{metrics.FontSize} " +
                    $"line={metrics.LineHeight} width={metrics.WindowWidth} maxLog={metrics.MaxLog}");
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, "native runtime failed: " + ex);
                SetStatus("启动异常：" + ex.Message);
            }
            finally
            {
                lock (Gate)
                {
                    _starting = false;
                }
            }
        });
    }

    public static void Submit(InputFrame frame)
    {
        var host = _host;
        if (host is null)
            return;
        host.Submit(frame);
    }

    public static void Stop()
    {
        GameHost? host;
        lock (Gate)
        {
            host = _host;
            _host = null;
        }
        if (host is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, "stop failed: " + ex.Message);
            }
        });
    }

    private static void SetStatus(string status)
    {
        _status = status;
        Log.Info(LogTag, status);
    }
}
