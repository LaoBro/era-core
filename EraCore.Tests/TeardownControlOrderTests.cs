using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0049 D1 的行为锁 3：拆除的三步顺序（置旗标 → 控制权 <c>End</c> → <c>Close</c>）
/// 与「registry 不得旁路」这条结构约束。
///
/// <para><b>被钉住的 bug</b>：<c>DetachSessionLocked</c> 曾在发拆除请求<b>之前</b>自行调用
/// <c>Controller.End(reason)</c>。End 会发布 ownerChanged 唤醒在途的 <c>GET /turn</c>，
/// 而 <c>TurnDelivery</c> 的拆除旗标此时还没置位——等待者据此判成 <c>ControlLost(409)</c>，
/// 与「拆除期稳定归一为 <c>Closed(404)</c>」的契约直接矛盾。窗口很窄，单次跑通没有意义，
/// 故运行时用例循环 100 轮。</para>
///
/// <para><b>为什么还要一条源码守卫</b>：竞态窗口窄到无法靠概率稳定复现，而这条约束
/// 的本质是「只能有一个编排点」。守卫直接禁止 <c>SessionRegistry</c> 出现 <c>Controller.End(</c>，
/// 并校验 <c>TurnDelivery.Dispose</c> 的三步书写顺序——与 <c>GameDirAccessorIoPolicyTests</c>
/// 同一种手法（把架构约束变成可执行断言）。</para>
/// </summary>
public class TeardownControlOrderTests
{
    /// <summary>竞态窗口窄，单轮通过不能说明问题；重复以放大「顺序被倒置」的暴露概率。</summary>
    private const int Rounds = 100;

    [Fact]
    public async Task In_flight_wait_is_closed_not_control_lost_during_teardown()
    {
        for (var i = 0; i < Rounds; i++)
        {
            var identity = ControlIdentity.Agent("agent-" + i);
            var sink = new BlockingSink();
            var signal = new FakeControlSignal(identity);
            var delivery = new TurnDelivery(sink, signal);

            var wait = delivery.WaitForTurnAsync(5000, identity, CancellationToken.None);
            // 等读通道真的被进入，确保等待者已停在 Task.WhenAny 上（而不是尚未开始）。
            await sink.Entered.Task;

            delivery.Dispose("session_ended");

            var result = await wait;
            Assert.Equal(TurnWaitStatus.Closed, result.Status);
        }
    }

    [Fact]
    public void Registry_does_not_end_control_by_itself_and_delivery_keeps_the_order()
    {
        var root = FindRepositoryRoot();

        var registry = File.ReadAllText(Path.Combine(root, "EraCore.Core", "Server", "SessionRegistry.cs"));
        Assert.DoesNotContain("Controller.End(", registry);

        var delivery = File.ReadAllText(Path.Combine(root, "EraCore.Core", "Server", "TurnDelivery.cs"));
        var flag = delivery.IndexOf("_sessionTeardown = true", StringComparison.Ordinal);
        Assert.True(flag >= 0, "TurnDelivery 中找不到拆除旗标置位语句");
        var end = delivery.IndexOf("_signal.End(", flag, StringComparison.Ordinal);
        Assert.True(end > flag, "拆除旗标必须先于控制权 End（否则在途 GET /turn 会误报 409）");
        var close = delivery.IndexOf("_sink.Close()", end, StringComparison.Ordinal);
        Assert.True(close > end, "控制权 End 必须先于 sink Close（等待者需先被唤醒再收到关闭）");
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }

    /// <summary>读通道一旦进入就永不返回（直到取消）。</summary>
    private sealed class BlockingSink : ITurnSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Turn?> ReadOutputAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }

        public void UnreadOutput(Turn turn)
        {
        }

        public List<Turn> DrainOutput() => [];

        public void WriteLine(Turn turn)
        {
        }

        public void Close()
        {
        }
    }

    /// <summary>控制权信号替身：持有 identity，<c>End</c> 时完成 ownerChanged。</summary>
    private sealed class FakeControlSignal : IControlSignal
    {
        private readonly TaskCompletionSource _ownerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ControlIdentity _identity;

        public FakeControlSignal(ControlIdentity identity) => _identity = identity;

        public ControlWaitSnapshot CaptureWaitSnapshot() => new(_identity, _ownerChanged.Task);

        public bool IsCurrent(ControlIdentity identity) => true;

        public ControlEvent End(string type)
        {
            _ownerChanged.TrySetResult();
            return new ControlEvent(type, null, DateTimeOffset.UtcNow, null, "idle");
        }
    }
}
