using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// TurnDelivery 深模块单测：注入 fake ITurnSink + IControlSignal，用 TCS 确定性编排分支。
/// 可确定性钳制的分支在此覆盖；SessionWaitForTurnTests 用真实对象接线兜底。
/// <para>
/// ⚠️ <b>分层原则（2026-10-15 修订）</b>：确定性用例断言<b>具体分支</b>（如「已取出的 turn 必须回滚」），
/// 真实接线用例只断言<b>不变式</b>（如「回合不许丢」）。此前 Session 层那条竞态用例断言了
/// 赛跑<b>胜负</b>（drain 那一刻是否已看到回滚），而那个时点本就无排序保证 ⇒ 负载高时偶发假红。
/// 分支覆盖下沉到本文件（<c>Turn_ready_concurrently_with_steal_is_rolled_back_for_next_drain</c>）。
/// </para>
/// </summary>
public class TurnDeliveryTests
{
    private static (TurnDelivery delivery, FakeTurnSink sink, FakeSignal signal) Create()
    {
        var sink = new FakeTurnSink();
        var signal = new FakeSignal();
        return (new TurnDelivery(sink, signal), sink, signal);
    }

    [Fact]
    public async Task Wait_returns_queued_turn()
    {
        var (delivery, sink, _) = Create();
        sink.ProvideTurn("T");

        var r = await delivery.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Turn, r.Status);
        Assert.Equal("T", r.Turn!.Wire);
    }

    [Fact]
    public async Task Wait_times_out_when_no_output()
    {
        var (delivery, _, _) = Create();

        var r = await delivery.WaitForTurnAsync(50, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Timeout, r.Status);
        Assert.Null(r.Turn);
    }

    [Fact]
    public async Task Wait_returns_closed_when_stream_ends_with_null()
    {
        var (delivery, sink, _) = Create();
        sink.ProvideTurn(null);

        var r = await delivery.WaitForTurnAsync(1000, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Closed, r.Status);
    }

    [Fact]
    public async Task Not_controller_fails_fast_without_waiting()
    {
        var (delivery, _, signal) = Create();
        signal.Current = ControlIdentity.Agent("other");

        var r = await delivery.WaitForTurnAsync(1000, ControlIdentity.Agent("me"), CancellationToken.None);

        Assert.Equal(TurnWaitStatus.ControlLost, r.Status);
        Assert.Equal("not_controller", r.Reason);
    }

    [Fact]
    public async Task Owner_changed_while_parked_returns_control_lost()
    {
        var (delivery, _, signal) = Create();
        signal.Current = ControlIdentity.Agent("agent-1");

        var wait = delivery.WaitForTurnAsync(5000, ControlIdentity.Agent("agent-1"), CancellationToken.None);
        await Task.Yield();
        signal.FireOwnerChanged();
        var r = await wait;

        Assert.Equal(TurnWaitStatus.ControlLost, r.Status);
        Assert.Equal("control_changed", r.Reason);
    }

    [Fact]
    public async Task External_cancellation_propagates()
    {
        var (delivery, _, _) = Create();
        using var cts = new CancellationTokenSource();

        var wait = delivery.WaitForTurnAsync(5000, cts.Token);
        await Task.Yield();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
    }

    [Fact]
    public async Task End_of_game_delivers_final_turn_to_parked_waiter()
    {
        var (delivery, sink, _) = Create();

        var wait = delivery.WaitForTurnAsync(5000, CancellationToken.None);
        await Task.Yield();
        delivery.EndOfGame(TestTurnFactory.Wire("FINAL"));
        var r = await wait;

        Assert.Equal(TurnWaitStatus.Turn, r.Status);
        Assert.Equal("FINAL", r.Turn!.Wire);
        Assert.True(delivery.IsFinalTurnDelivered);
        Assert.True(sink.SinkClosed);
    }

    [Fact]
    public async Task Final_turn_is_delivered_exactly_once()
    {
        var (delivery, sink, _) = Create();
        delivery.EndOfGame(TestTurnFactory.Wire("FINAL"));

        // 首次等待消费 final turn
        var first = await delivery.WaitForTurnAsync(50, CancellationToken.None);
        Assert.Equal(TurnWaitStatus.Turn, first.Status);
        Assert.Equal("FINAL", first.Turn!.Wire);

        // delivered 旗标已置位后，再到的多余 turn 被丢弃（防御性 continue → 超时）
        sink.ProvideTurn("EXTRA");
        var second = await delivery.WaitForTurnAsync(50, CancellationToken.None);

        Assert.Equal(TurnWaitStatus.Timeout, second.Status);
        Assert.True(delivery.IsFinalTurnDelivered);
    }

    [Fact]
    public void Drain_on_acquire_returns_backlog_fifo()
    {
        var (delivery, sink, _) = Create();

        sink.WriteLine(TestTurnFactory.Wire("a"));
        sink.WriteLine(TestTurnFactory.Wire("b"));
        var (advanced, last) = delivery.DrainOnAcquire();

        Assert.Equal(2, advanced);
        Assert.Equal("b", last!.Wire);
    }

    [Fact]
    public async Task End_of_game_closes_sink_and_signals_ended()
    {
        var (delivery, sink, signal) = Create();

        delivery.EndOfGame(null);

        Assert.True(sink.SinkClosed);
        Assert.Equal("game_ended", signal.LastEndedType);
    }

    [Fact]
    public async Task Dispose_mid_wait_returns_closed()
    {
        var (delivery, _, _) = Create();

        var wait = delivery.WaitForTurnAsync(5000, CancellationToken.None);
        await Task.Yield();
        delivery.Dispose();
        var r = await wait;

        // 拆除期在途等待应稳定 404 Closed，而非与 409 ControlLost 竞争
        Assert.Equal(TurnWaitStatus.Closed, r.Status);
        Assert.Null(r.Turn);
    }

    [Fact]
    public async Task Dispose_while_turn_pending_rolls_back_and_returns_closed()
    {
        var (delivery, sink, _) = Create();

        var wait = delivery.WaitForTurnAsync(5000, CancellationToken.None);
        await Task.Yield();
        delivery.Dispose();      // 先拆除：End(ownerChanged) + Close
        sink.ProvideTurn("T");   // 之后到达的 turn 触发 readTask 完成
        var r = await wait;

        Assert.Equal(TurnWaitStatus.Closed, r.Status);
        // 已取出但未交付的 turn 经 UnreadOutput 回滚（会话已拆，无新持有者 drain，丢弃无害）
        Assert.Contains("T", sink.UnreadLog);
    }

    /// <summary>
    /// 「turn 已落到等待方手里」与「控制权易主」交叠时，那个 turn 必须被<b>回滚</b>，
    /// 留给下一位持有者 drain —— 不丢、也不交付给已失守的一方。
    /// <para>
    /// 🔒 <b>确定性编排</b>（不开线程赛跑）：<c>ProvideTurn</c> 与 <c>FireOwnerChanged</c> 在同一线程上
    /// 先后执行，而 <c>ProvideTurn</c> 会<b>同步</b>完成 <c>ReadOutputAsync</c> 的 TCS ⇒ 等待方
    /// 必然走 <c>if (readTask.IsCompleted) → await readTask → UnreadOutput</c> 这一支，不存在第二种交错。
    /// </para>
    /// <para>
    /// 与 <c>SessionWaitForTurnTests.Steal_racing_a_queued_turn_never_loses_that_turn</c> 的分工：
    /// 那条用真 Session + 真 <c>DrainOnAcquire</c>，是<b>真实接线回归</b>，但它断言不了
    /// 「drain 那一刻回滚是否已完成」（drain 在 <c>AcquireControl</c> 内同步跑，回滚要等等待线程
    /// 被唤醒后跑到 rollback 点）⇒ 那条只断言「回合不丢」。本用例把<b>回滚本身</b>钉死。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Turn_ready_concurrently_with_steal_is_rolled_back_for_next_drain()
    {
        var (delivery, sink, signal) = Create();
        signal.Current = ControlIdentity.Agent("agent-1");

        var wait = delivery.WaitForTurnAsync(5000, ControlIdentity.Agent("agent-1"), CancellationToken.None);
        await Task.Yield();

        sink.ProvideTurn("raced");
        signal.FireOwnerChanged();

        var r = await wait;

        Assert.Equal(TurnWaitStatus.ControlLost, r.Status);
        Assert.Contains("raced", sink.UnreadLog);   // 已取出但未交付 ⇒ 必须回滚

        // 下一位持有者 drain 时能拿到它（复用真实接线那条测试的语义，此处确定性）
        var (advanced, last) = delivery.DrainOnAcquire();
        Assert.Equal(1, advanced);
        Assert.Equal("raced", last!.Wire);
    }

    [Fact]
    public async Task Game_end_mid_wait_with_identity_delivers_final()
    {
        var (delivery, _, signal) = Create();
        signal.Current = ControlIdentity.Agent("agent-1");

        var wait = delivery.WaitForTurnAsync(5000, ControlIdentity.Agent("agent-1"), CancellationToken.None);
        await Task.Yield();
        delivery.EndOfGame(TestTurnFactory.Wire("FINAL"));
        var r = await wait;

        // game_end（有 final）应交付 final turn，而非因 End 清空 _current 被误判 409 ControlLost
        Assert.Equal(TurnWaitStatus.Turn, r.Status);
        Assert.Equal("FINAL", r.Turn!.Wire);
        Assert.True(delivery.IsFinalTurnDelivered);
    }

    [Fact]
    public async Task Lease_expired_mid_wait_returns_control_lost()
    {
        var (delivery, _, signal) = Create();
        signal.Current = ControlIdentity.Agent("agent-1");

        var wait = delivery.WaitForTurnAsync(5000, ControlIdentity.Agent("agent-1"), CancellationToken.None);
        await Task.Yield();
        signal.Current = null;   // lease 过期：控制权被清空但会话仍在跑
        signal.FireOwnerChanged();
        var r = await wait;

        // lease_expired（非 teardown、非 game_end）→ 保持 409 ControlLost，可重新 acquire
        Assert.Equal(TurnWaitStatus.ControlLost, r.Status);
        Assert.Equal("control_changed", r.Reason);
    }

    [Fact]
    public async Task Teardown_after_game_end_still_delivers_ready_final()
    {
        var (delivery, _, _) = Create();

        delivery.EndOfGame(TestTurnFactory.Wire("FINAL"));   // game_end：final 已就绪并入队
        delivery.Dispose();            // 随后拆除（teardown 旗标置位）

        var r = await delivery.WaitForTurnAsync(1000, CancellationToken.None);

        // 四种终止态归一：已就绪的 final 优先于拆除（spec L38「game_end → 交付 final」），
        // 拆除旗标不得在循环顶早退处把它丢弃成 404 Closed。
        Assert.Equal(TurnWaitStatus.Turn, r.Status);
        Assert.Equal("FINAL", r.Turn!.Wire);
        Assert.True(delivery.IsFinalTurnDelivered);
    }

    private sealed class FakeTurnSink : ITurnSink
    {
        private readonly ConcurrentQueue<string?> _available = new();
        private TaskCompletionSource<Turn?>? _pending;
        public List<string> UnreadLog { get; } = new();
        public bool SinkClosed { get; private set; }

        private static Turn? ToTurn(string? value) => value == null ? null : TestTurnFactory.Wire(value);

        private void Push(string? value)
        {
            var pending = _pending;
            if (pending != null)
            {
                _pending = null;
                pending.TrySetResult(ToTurn(value));
            }
            else
            {
                _available.Enqueue(value);
            }
        }

        public Task<Turn?> ReadOutputAsync(CancellationToken ct)
        {
            if (_available.TryDequeue(out var value))
                return Task.FromResult(ToTurn(value));
            var tcs = new TaskCompletionSource<Turn?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public Task? PendingRead => _pending?.Task;

        public void ProvideTurn(string? turn) => Push(turn);

        public void UnreadOutput(Turn turn)
        {
            UnreadLog.Add(turn.Wire);
            Push(turn.Wire);
        }

        public void WriteLine(Turn turn) => Push(turn.Wire);

        public List<Turn> DrainOutput()
        {
            var list = new List<Turn>();
            while (_available.TryDequeue(out var v))
                if (v != null)
                    list.Add(TestTurnFactory.Wire(v));
            return list;
        }

        public void Close() => SinkClosed = true;
    }

    private sealed class FakeSignal : IControlSignal
    {
        private TaskCompletionSource _ownerChanged = New();
        public ControlIdentity? Current { get; set; }
        public string? LastEndedType { get; private set; }

        public ControlWaitSnapshot CaptureWaitSnapshot() => new(Current, _ownerChanged.Task);

        public bool IsCurrent(ControlIdentity identity) => Current.HasValue && Current.Value.Matches(identity);

        public ControlEvent End(string type)
        {
            LastEndedType = type;
            // 与真实 Controller.End 一致：先清空当前持有者再发事件（事件不带 holder）。
            // 此前 fake 不清 Current，无法复现真实「game_end 清空 _current → IsCurrent 变 false」的
            // 第二子句竞态，导致 game_end+身份 分支在旧代码下不会变红。
            Current = null;
            FireOwnerChanged();
            return new ControlEvent(type, null, DateTimeOffset.UtcNow, null, "ended");
        }

        public void FireOwnerChanged()
        {
            var old = _ownerChanged;
            _ownerChanged = New();
            old.TrySetResult();
        }

        public static TaskCompletionSource New() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}