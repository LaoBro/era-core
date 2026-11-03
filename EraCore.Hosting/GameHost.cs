using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Sessions;

namespace EraCore.Hosting;

/// <summary>
/// <see cref="IGameHost"/> 的薄实现：只转发到 <see cref="CoreSession"/> 公共三轴。
/// 不持有平台渲染/输入实现，也不访问 Core internal。
/// </summary>
public sealed class GameHost : IGameHost
{
    private const int StatePollIntervalMs = 50;

    private readonly CoreSessionOptions _options;
    private readonly RenderCapability _render;
    private readonly CoreSession _session;
    private bool _disposed;

    public GameHost(CoreSessionOptions options, RenderCapability render = RenderCapability.Text)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _render = render == RenderCapability.None ? RenderCapability.Text : render;
        _session = new CoreSession(options);
    }

    public CoreSessionState State => _session.State;

    public HostMetrics Metrics => _session.Metrics;

    public HostCapabilities Capabilities => new(_options.InputCapabilities, _render);

    public GameTree Files => _session.Files;

    public DisplaySnapshot Snapshot => _session.Output.Snapshot;

    public HostStream OpenStream()
    {
        // 先订阅再取快照：两者之间的帧可能比快照更新；宿主丢弃 revision <= 基线 的帧。
        var subscription = _session.Output.SubscribeTurns();
        var snapshot = _session.Output.Snapshot;
        return new HostStream(snapshot, subscription);
    }

    public Task<CoreSessionStartResult> StartAsync(CancellationToken cancellationToken = default)
        => _session.StartAsync(cancellationToken);

    public Task<bool> StopAsync(CancellationToken cancellationToken = default)
        => _session.StopAsync(cancellationToken);

    public void Submit(InputFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _session.Input.Submit(frame);
    }

    public async Task<bool> WaitForStateAsync(
        CoreSessionState target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (_session.State == target)
            return true;
        if (timeout <= TimeSpan.Zero)
            return false;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            var delay = remaining < TimeSpan.FromMilliseconds(StatePollIntervalMs)
                ? remaining
                : TimeSpan.FromMilliseconds(StatePollIntervalMs);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            if (_session.State == target)
                return true;
        }

        return _session.State == target;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            await _session.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _session.Dispose();
        }
    }
}
