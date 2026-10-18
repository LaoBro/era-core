using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

/// <summary>
/// TurnDelivery 消费的控制信号窄接缝：供代轮等待循环判断控制权是否已易主/结束。
/// Controller 满足该接缝；测试以 fake 注入以获得确定性竞态编排。
/// </summary>
internal interface IControlSignal
{
    ControlWaitSnapshot CaptureWaitSnapshot();
    bool IsCurrent(ControlIdentity identity);
    ControlEvent End(string type);
}

/// <summary>
/// TurnDelivery 消费的 turn 队列窄接缝：读 / 回滚 / 排水 / 写 / 关闭（仅 output 侧）。
/// HttpSessionIO 满足该接缝；测试以 fake 注入。
/// </summary>
internal interface ITurnSink
{
    Task<string?> ReadOutputAsync(CancellationToken ct);
    void UnreadOutput(string turn);
    List<string> DrainOutput();
    void WriteLine(string text);
    void Close();
}

/// <summary>
/// 深模块：独占「长轮询一个 turn + 控制权变更抢占 + final-turn-once + 超时」不变式。
/// 从 Session 门面抽出：消费侧全部 turn 取水（WaitForTurn / acquire 排水）与 <see cref="_turnAccessLock"/>
/// 归一于此；生产侧（游戏结束 / 会话拆除）经 EndOfGame / Dispose 过渡方法封装备序。
/// Session 只做组装，把 Controller / HttpSessionIO 注入（它们各自满足窄接缝）。
/// </summary>
internal sealed class TurnDelivery : IDisposable
{
    private readonly ITurnSink _sink;
    private readonly IControlSignal _signal;
    private readonly SemaphoreSlim _turnAccessLock = new(1, 1);
    private readonly object _turnLock = new();
    private volatile bool _finalTurnReady;
    private bool _finalTurnDelivered;
    private bool _sessionEnded;
    /// <summary>
    /// 拆除中旗标：<see cref="Dispose"/> 首行置位（先于 End/Close），供等待循环把拆除期在途
    /// /turn 稳定判定为 Closed(404)，不再与 ownerChanged 竞态产生 409/404 不确定返回。
    /// volatile：Dispose 线程写、等待循环线程读，与 <see cref="_finalTurnReady"/> 同构。
    /// </summary>
    private volatile bool _sessionTeardown;

    public TurnDelivery(ITurnSink sink, IControlSignal signal)
    {
        _sink = sink;
        _signal = signal;
    }

    /// <summary>final turn 是否已交付一次（协议层据此对后续 GET /turn 短路返回 404）。</summary>
    public bool IsFinalTurnDelivered => _finalTurnDelivered;

    public Task<TurnWaitResult> WaitForTurnAsync(int timeoutMs, CancellationToken externalCt)
    {
        return WaitForTurnCoreAsync(timeoutMs, null, externalCt);
    }

    public Task<TurnWaitResult> WaitForTurnAsync(
        int timeoutMs,
        ControlIdentity identity,
        CancellationToken externalCt)
    {
        return WaitForTurnCoreAsync(timeoutMs, identity, externalCt);
    }

    /// <summary>
    /// 新持有者 acquire 后排空 backlog（原 Session.AcquireControl 的 drain 部分）。
    /// 与 WaitForTurn 共享 <see cref="_turnAccessLock"/>，保证取水路径单一逻辑读者。
    /// 返回 (已推进的 turn 数, 最后一个 turn，可能为 null)。
    /// </summary>
    public (int Advanced, string? Last) DrainOnAcquire()
    {
        _turnAccessLock.Wait();
        try
        {
            var turns = _sink.DrainOutput();
            return (turns.Count, turns.Count == 0 ? null : turns[^1]);
        }
        finally
        {
            _turnAccessLock.Release();
        }
    }

    /// <summary>
    /// 生产侧过渡：游戏循环 finally 调用。封装备序——置 ready → 写 final turn → End(game_ended) → Close。
    /// finalTurn 可为 null（脚本退出但 BuildFinalTurn 失败/为空）：此时不写，等待者走 Closed。
    /// 保证「等待中的 GET /turn 拿到 final turn 而非被当作 control_lost/404」。
    /// </summary>
    public void EndOfGame(string? finalTurn)
    {
        lock (_turnLock)
        {
            _finalTurnReady = true;
        }
        if (finalTurn != null)
            _sink.WriteLine(finalTurn);
        _signal.End("game_ended");
        _sessionEnded = true;
        _sink.Close();
    }

    /// <summary>
    /// 会话拆除：置拆除旗标 → End(reason) → Close。不 Dispose 共享的 signal/sink（归属 Session）。
    /// <para>
    /// <b>这三步的顺序是本类型对外契约的一部分</b>（ADR-0049 D1）：等待者由 End 发布的任务完成唤醒，
    /// 其后必然能看到旗标（任务完成自带 acquire/release 语义），因此拆除期的在途 GET /turn
    /// 稳定归一为 Closed(404)，不会与 ownerChanged 分支竞争出 409/404 不确定返回。
    /// <b>调用方不得先自行 End</b>——那会在「End 已发布、旗标未置」的窗口里把 404 变成 409。
    /// </para>
    /// <para>
    /// 注意：不置 <see cref="_sessionEnded"/>（该旗标只在 EndOfGame/game-ended 置位），以保持
    /// 与原 Session.HasEnded「仅游戏结束后」的快失败判定语义（DELETE-mid-game 仍走 not_controller 409）。
    /// </para>
    /// </summary>
    /// <param name="reason">
    /// 控制权终止原因（<c>session_ended</c> / <c>session_replaced</c>）。由 <see cref="Session.RequestTeardown"/>
    /// 透传，取代原先 registry 在拆除前自行调用 <c>Controller.End</c> 的旁路。
    /// </param>
    /// <summary>IDisposable 契约入口：等价于 <see cref="Dispose(string)"/> 的 <c>session_ended</c>。</summary>
    public void Dispose() => Dispose("session_ended");

    public void Dispose(string reason)
    {
        // 顺序不可换：先置旗标（volatile 写），再 End，再 Close。
        _sessionTeardown = true;
        _signal.End(reason);
        _sink.Close();
    }

    /// <summary>
    /// 异步等待一个 turn。用于 GET /turn 长轮询，语义与原 Session.WaitForTurnCoreAsync 一致：
    /// - 拿到 turn（含 finalTurn）→ Turn（finalTurn 交付后标记 IsFinalTurnDelivered）
    /// - 超时 → Timeout（HTTP 204）；Channel 关闭 → Closed（HTTP 404）
    /// - externalCt 取消（服务器关闭）→ 抛 OperationCanceledException 向上传播
    /// - 控制权在等待期间变化 → ControlLost（HTTP 409），已取出的 turn 经 UnreadOutput 回滚给新持有者
    /// </summary>
    private async Task<TurnWaitResult> WaitForTurnCoreAsync(
        int timeoutMs,
        ControlIdentity? identity,
        CancellationToken externalCt)
    {
        var waitSnapshot = _signal.CaptureWaitSnapshot();
        if (identity.HasValue && waitSnapshot.Controller.HasValue && !waitSnapshot.Controller.Value.Matches(identity.Value) && !_sessionEnded)
            return new TurnWaitResult(TurnWaitStatus.ControlLost, null, "not_controller", DateTimeOffset.UtcNow);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        linkedCts.CancelAfter(timeoutMs);
        var ct = linkedCts.Token;

        // 控制权是否已丢失：owner-changed 已触发（且非 final-turn 就绪，game_end 不算失守）或
        // 持身份等待但当前持有者已非本人。循环内多处复用同一判定。
        // 第二子句同样以 !_finalTurnReady 防护：game_end 会清空 _current 使 IsCurrent 转 false，
        // 但那是「应交付 final」而非失守，不得据此误判 ControlLost。
        bool IsControlLost(Task ownerChangedTask) =>
            (ownerChangedTask.IsCompleted && !_finalTurnReady) ||
            (identity.HasValue && waitSnapshot.Controller.HasValue && !_signal.IsCurrent(identity.Value) && !_finalTurnReady);

        // 终止态归一：拆除中 → Closed(404)；否则（steal / lease_expired / release）→ ControlLost(409)。
        // 两处回滚点共用，保证拆除期与易主期的判定一致且确定。
        TurnWaitResult ClosedOrControlLost() =>
            _sessionTeardown
                ? new TurnWaitResult(TurnWaitStatus.Closed, null)
                : new TurnWaitResult(TurnWaitStatus.ControlLost, null, "control_changed", DateTimeOffset.UtcNow);

        while (true)
        {
            var ownerChangedTask = waitSnapshot.OwnerChangedTask;
            await _turnAccessLock.WaitAsync(ct);
            try
            {
                // 拆除中且无已就绪 final：会话已删除、无后续 turn → 稳定 Closed(404)，先于 ControlLost 判定。
                // 若拆除恰逢 game_end 已就绪 final（EndOfGame 先完成），仍走下方交付 final（四种终止态归一）。
                if (_sessionTeardown && !_finalTurnReady)
                    return ClosedOrControlLost();
                if (IsControlLost(ownerChangedTask))
                    return ClosedOrControlLost();

                var readTask = _sink.ReadOutputAsync(ct);
                await Task.WhenAny(readTask, ownerChangedTask);
                if (ownerChangedTask.IsCompleted && !_finalTurnReady)
                {
                    if (readTask.IsCompleted)
                    {
                        try
                        {
                            var raced = await readTask;
                            if (raced != null)
                                _sink.UnreadOutput(raced);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                    else
                    {
                        linkedCts.Cancel();
                        try
                        {
                            await readTask;
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }
                    return ClosedOrControlLost();
                }

                string? turn;
                try
                {
                    turn = await readTask;
                }
                catch (OperationCanceledException)
                {
                    // 服务器关闭：向上传播，调用方不应写 response
                    if (externalCt.IsCancellationRequested)
                        throw;
                    // 超时：返回 204
                    return new TurnWaitResult(TurnWaitStatus.Timeout, null);
                }

                // Channel 关闭且队列空：session 已结束
                if (turn == null)
                    return new TurnWaitResult(TurnWaitStatus.Closed, null);

                if (IsControlLost(ownerChangedTask))
                {
                    _sink.UnreadOutput(turn);
                    return ClosedOrControlLost();
                }

                // 拿到 turn，在 lock 内检查 finalTurn 标记（一次性交付）
                lock (_turnLock)
                {
                    if (_finalTurnDelivered)
                        continue; // 防御性：已交付，丢弃多余的 turn（理论上不会发生）
                    if (_finalTurnReady)
                        _finalTurnDelivered = true;
                    return new TurnWaitResult(TurnWaitStatus.Turn, turn);
                }
            }
            finally
            {
                _turnAccessLock.Release();
            }
        }
    }
}

/// <summary>WaitForTurnAsync 的等待结果状态。</summary>
internal enum TurnWaitStatus
{
    /// <summary>拿到一个 turn（含 finalTurn）。</summary>
    Turn,
    /// <summary>等待超时（HTTP 204）。</summary>
    Timeout,
    /// <summary>Channel 关闭，session 已结束（HTTP 404）。</summary>
    Closed,
    /// <summary>控制权在等待期间发生变化（HTTP 409）。</summary>
    ControlLost
}

/// <summary>WaitForTurnAsync 的返回值：状态 + turn 内容（仅 Status=Turn 时非 null）。</summary>
internal readonly record struct TurnWaitResult(
    TurnWaitStatus Status,
    string? Turn,
    string? Reason = null,
    DateTimeOffset? At = null);