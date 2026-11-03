using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Sessions;

namespace EraCore.Hosting;

/// <summary>
/// 原生/桌面宿主的稳定门面：会话生命周期 + 元信息 + 快照/增量 + 输入。
///
/// <para>实现只包装 <see cref="CoreSession"/> 的公共三轴，不复制引擎逻辑；
/// 平台相关的字体度量、像素落位、DOM/ANSI/SKCanvas 不属于本接口。</para>
/// </summary>
public interface IGameHost : IAsyncDisposable
{
    CoreSessionState State { get; }

    /// <summary>启动成功后可用；启动前访问抛 <see cref="InvalidOperationException"/>。</summary>
    HostMetrics Metrics { get; }

    HostCapabilities Capabilities { get; }

    GameTree Files { get; }

    /// <summary>当前全量画面（读取时已刷新）。</summary>
    DisplaySnapshot Snapshot { get; }

    /// <summary>先订阅、再取快照；宿主按 revision 去重。</summary>
    HostStream OpenStream();

    Task<CoreSessionStartResult> StartAsync(CancellationToken cancellationToken = default);

    Task<bool> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>提交一帧输入；启动成功后调用。</summary>
    void Submit(InputFrame frame);

    /// <summary>轮询等待目标状态；超时返回 false，不抛异常。</summary>
    Task<bool> WaitForStateAsync(
        CoreSessionState target,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
