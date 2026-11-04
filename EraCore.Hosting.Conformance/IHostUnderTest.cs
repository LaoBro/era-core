using System.Collections.Generic;
using System.Threading.Tasks;
using EraCore.Hosting;

namespace EraCore.Hosting.Conformance;

/// <summary>
/// 被一致性套件驱动的宿主投影面。实现者只负责「把协议 DTO 变成自己的渲染/输入状态」，
/// 不依赖 Core 的 internal 构造器，也不依赖任何 UI 框架。
/// </summary>
public interface IHostUnderTest
{
    string HostName { get; }

    /// <summary>宿主自报的能力清单；套件据此决定跑/跳过哪些夹具。</summary>
    HostManifest Declared { get; }

    /// <summary>载入全量基线；实现应重置告警与增量状态（等同一次晚加入）。</summary>
    Task LoadSnapshotAsync(ConformanceSnapshot snapshot);

    /// <summary>应用一帧增量。revision 去重与终态例外由宿主负责。</summary>
    Task ApplyTurnAsync(ConformanceTurn turn);

    IReadOnlyList<RenderedLine> RenderedLines { get; }

    AwaitingUiState Awaiting { get; }

    long LastAppliedRevision { get; }

    /// <summary>结构化告警；未知 awaiting 键等必须留下可断言的 Code。</summary>
    IReadOnlyList<HostWarning> Warnings { get; }

    /// <summary>按字符列/行命中按钮；未命中返回 null。</summary>
    ButtonHit? HitTest(int column, int row);

    /// <summary>可选截图能力；默认返回 null 即表示未实现。</summary>
    Task<byte[]?> CapturePngAsync();
}
