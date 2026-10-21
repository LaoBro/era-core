using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

internal enum ControllerKind
{
    User,
    Agent,
}

internal readonly record struct ControlIdentity(ControllerKind Kind, string? Token)
{
    public static ControlIdentity User => new(ControllerKind.User, null);

    public static ControlIdentity Agent(string token) => new(ControllerKind.Agent, token);

    public bool Matches(ControlIdentity other)
    {
        if (Kind != other.Kind)
            return false;
        return Kind == ControllerKind.User || string.Equals(Token, other.Token, StringComparison.Ordinal);
    }

    public string KindName => Kind == ControllerKind.Agent ? "agent" : "user";
}

internal sealed record ControllerInfo(
    string Kind,
    string? Token,
    DateTimeOffset? LeaseExpiresAt);

internal sealed record ControlEvent(
    string Type,
    string? Reason,
    DateTimeOffset At,
    ControllerInfo? Controller,
    string State);

internal readonly record struct ControlWaitSnapshot(
    ControlIdentity? Controller,
    Task OwnerChangedTask);

internal enum ControlAcquireStatus
{
    Acquired,
    HeldByUser,
    HeldByAgent,
}

internal readonly record struct ControlAcquireResult(
    ControlAcquireStatus Status,
    ControlIdentity Identity,
    ControlEvent? Event,
    ControlEvent? ExpiredEvent);

internal enum ControlReleaseStatus
{
    Released,
    NotOwner,
    Empty,
}

internal readonly record struct ControlReleaseResult(
    ControlReleaseStatus Status,
    ControlEvent? Event);

internal enum ControlGateStatus
{
    Allowed,
    NotController,
}

internal readonly record struct ControlGateResult(
    ControlGateStatus Status,
    string Reason,
    ControlIdentity? CurrentController,
    ControlEvent? ExpiredEvent);

/// <summary>
/// Session controller state machine. It owns only control ownership and its
/// notification signals; turn queues remain owned by <see cref="Session"/>.
/// </summary>
internal sealed class Controller : IControlSignal, IDisposable
{
    public const string StateIdle = "idle";
    public const string StateHeld = "held";

    private readonly object _lock = new();
    private readonly TimeSpan _agentLease;
    private readonly List<TaskCompletionSource<ControlEvent>> _eventWaiters = new();
    private TaskCompletionSource _ownerChanged = CreateSignal();
    private ControlIdentity? _current;
    private DateTimeOffset? _leaseExpiresAt;
    private Timer? _leaseTimer;
    private bool _disposed;
    private bool _ended;

    public Controller(TimeSpan? agentLease = null)
    {
        _agentLease = agentLease ?? TimeSpan.FromMinutes(5);
        if (_agentLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(agentLease));
    }

    public ControlIdentity? Current
    {
        get
        {
            lock (_lock)
            {
                ExpireLeaseLocked();
                return _current;
            }
        }
    }

    public ControllerInfo? CurrentInfo
    {
        get
        {
            lock (_lock)
            {
                ExpireLeaseLocked();
                return ToInfoLocked();
            }
        }
    }

    public string State
    {
        get
        {
            lock (_lock)
            {
                ExpireLeaseLocked();
                return _current.HasValue ? StateHeld : StateIdle;
            }
        }
    }

    public ControlWaitSnapshot CaptureWaitSnapshot()
    {
        lock (_lock)
        {
            ExpireLeaseLocked();
            return new ControlWaitSnapshot(_current, _ownerChanged.Task);
        }
    }

    public bool IsCurrent(ControlIdentity identity)
    {
        lock (_lock)
        {
            ExpireLeaseLocked();
            return _current.HasValue && _current.Value.Matches(identity);
        }
    }

    public Task OwnerChangedTask
    {
        get
        {
            lock (_lock)
                return _ownerChanged.Task;
        }
    }

    public ControlAcquireResult Acquire(ControlIdentity identity)
    {
        lock (_lock)
        {
            var expiredEvent = ExpireLeaseLocked();
            if (!_current.HasValue)
            {
                SetOwnerLocked(identity);
                var acquired = CreateEventLocked("acquired", null);
                PublishLocked(acquired);
                return new ControlAcquireResult(ControlAcquireStatus.Acquired, identity, acquired, expiredEvent);
            }

            if (_current.Value.Matches(identity))
            {
                if (identity.Kind == ControllerKind.Agent)
                    RefreshLeaseLocked(identity.Token!);
                return new ControlAcquireResult(ControlAcquireStatus.Acquired, identity, null, expiredEvent);
            }

            if (identity.Kind == ControllerKind.Agent)
            {
                var status = _current.Value.Kind == ControllerKind.User
                    ? ControlAcquireStatus.HeldByUser
                    : ControlAcquireStatus.HeldByAgent;
                return new ControlAcquireResult(status, identity, null, expiredEvent);
            }

            // A user acquire is the deliberate escape hatch: replace an agent.
            var previous = _current.Value;
            SetOwnerLocked(identity);
            var stolen = CreateEventLocked("stolen", previous.KindName);
            PublishLocked(stolen);
            return new ControlAcquireResult(ControlAcquireStatus.Acquired, identity, stolen, expiredEvent);
        }
    }

    public ControlReleaseResult Release(ControlIdentity identity)
    {
        lock (_lock)
        {
            var expiredEvent = ExpireLeaseLocked();
            if (!_current.HasValue)
                return new ControlReleaseResult(ControlReleaseStatus.Empty, expiredEvent);
            if (!_current.Value.Matches(identity))
                return new ControlReleaseResult(ControlReleaseStatus.NotOwner, expiredEvent);

            _current = null;
            _leaseExpiresAt = null;
            StopLeaseTimerLocked();
            var released = CreateEventLocked("released", null);
            PublishLocked(released);
            return new ControlReleaseResult(ControlReleaseStatus.Released, released);
        }
    }

    public ControlGateResult CheckInput(ControlIdentity identity, bool sessionEnded)
    {
        lock (_lock)
            return CheckGateLocked(identity, sessionEnded);
    }

    public ControlGateResult CheckLifecycle(ControlIdentity identity, bool sessionEnded)
    {
        lock (_lock)
            return CheckGateLocked(identity, sessionEnded);
    }

    /// <summary>输入/生命周期门禁共用判定（两 public 方法体同构去重）：session 已结束、无持有者或持有者匹配 → 放行，否则拒绝。</summary>
    private ControlGateResult CheckGateLocked(ControlIdentity identity, bool sessionEnded)
    {
        var expiredEvent = ExpireLeaseLocked();
        if (sessionEnded || !_current.HasValue || _current.Value.Matches(identity))
            return new ControlGateResult(ControlGateStatus.Allowed, string.Empty, _current, expiredEvent);

        return new ControlGateResult(
            ControlGateStatus.NotController,
            _current.Value.Kind == ControllerKind.User ? "CONTROL_HELD_BY_USER" : "CONTROL_HELD_BY_AGENT",
            _current,
            expiredEvent);
    }

    public ControlEvent End(string type)
    {
        lock (_lock)
        {
            if (_ended)
                return CreateEventLocked(type, null);

            _ended = true;
            _current = null;
            _leaseExpiresAt = null;
            StopLeaseTimerLocked();
            var ended = CreateEventLocked(type, null);
            PublishLocked(ended);
            return ended;
        }
    }

    public async Task<ControlEvent?> WaitForEventAsync(int timeoutMs, CancellationToken externalCt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);

        var waiter = new TaskCompletionSource<ControlEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_disposed)
                return null;
            ExpireLeaseLocked();
            _eventWaiters.Add(waiter);
        }

        try
        {
            var timeout = Task.Delay(timeoutMs, externalCt);
            var completed = await Task.WhenAny(waiter.Task, timeout);
            if (completed == waiter.Task)
                return await waiter.Task;
            externalCt.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            lock (_lock)
                _eventWaiters.Remove(waiter);
        }
    }

    private void SetOwnerLocked(ControlIdentity identity)
    {
        _current = identity;
        if (identity.Kind == ControllerKind.Agent)
            RefreshLeaseLocked(identity.Token!);
        else
        {
            _leaseExpiresAt = null;
            StopLeaseTimerLocked();
        }
    }

    private void RefreshLeaseLocked(string token)
    {
        _leaseExpiresAt = DateTimeOffset.UtcNow.Add(_agentLease);
        StopLeaseTimerLocked();
        _leaseTimer = new Timer(static state =>
        {
            var timerState = (LeaseTimerState)state!;
            timerState.Controller.ExpireLease(timerState.Token);
        }, new LeaseTimerState(this, token), _agentLease, Timeout.InfiniteTimeSpan);
    }

    private void ExpireLease(string token)
    {
        lock (_lock)
        {
            if (_current is not { Kind: ControllerKind.Agent, Token: var currentToken } ||
                !string.Equals(currentToken, token, StringComparison.Ordinal))
                return;
            ExpireLeaseLocked();
        }
    }

    private ControlEvent? ExpireLeaseLocked()
    {
        if (_current is not { Kind: ControllerKind.Agent } ||
            !_leaseExpiresAt.HasValue || _leaseExpiresAt.Value > DateTimeOffset.UtcNow)
            return null;

        _current = null;
        _leaseExpiresAt = null;
        StopLeaseTimerLocked();
        var expired = CreateEventLocked("lease_expired", "lease_expired");
        PublishLocked(expired);
        return expired;
    }

    private ControllerInfo? ToInfoLocked()
    {
        return _current is { } current
            ? new ControllerInfo(current.KindName, current.Token, _leaseExpiresAt)
            : null;
    }

    private ControlEvent CreateEventLocked(string type, string? reason)
    {
        return new ControlEvent(type, reason, DateTimeOffset.UtcNow, ToInfoLocked(), _current.HasValue ? StateHeld : StateIdle);
    }

    private void PublishLocked(ControlEvent controlEvent)
    {
        _ownerChanged.TrySetResult();
        _ownerChanged = CreateSignal();
        foreach (var waiter in _eventWaiters.ToArray())
            waiter.TrySetResult(controlEvent);
        _eventWaiters.Clear();
    }

    private void StopLeaseTimerLocked()
    {
        _leaseTimer?.Dispose();
        _leaseTimer = null;
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _ended = true;
            _current = null;
            _leaseExpiresAt = null;
            StopLeaseTimerLocked();
            _ownerChanged.TrySetResult();
            foreach (var waiter in _eventWaiters.ToArray())
                waiter.TrySetCanceled();
            _eventWaiters.Clear();
        }
    }

    private sealed record LeaseTimerState(Controller Controller, string Token);
}
