using System.Collections.Generic;
using System.Text.Json;
using EraCore.Hosting;

namespace EraCore.Hosting.Conformance;

/// <summary>一个手写夹具：内嵌 JSON，不依赖 test_game/引擎。</summary>
public sealed record ConformanceFixture
{
    public string Id { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool Optional { get; init; }
    public IReadOnlyList<HostFeature> Features { get; init; } = System.Array.Empty<HostFeature>();
    public IReadOnlyList<ConformanceStep> Steps { get; init; } = System.Array.Empty<ConformanceStep>();
}

public sealed record ConformanceStep
{
    /// <summary>snapshot / turn / expect。</summary>
    public string Kind { get; init; } = string.Empty;
    public ConformanceSnapshot? Snapshot { get; init; }
    public ConformanceTurn? Turn { get; init; }
    public ConformanceExpectation? Expect { get; init; }
}

public sealed record ConformanceExpectation
{
    public long? Revision { get; init; }
    public int? LineCount { get; init; }
    public IReadOnlyList<ConformanceLineExpectation>? Lines { get; init; }
    public ConformanceAwaitingExpectation? Awaiting { get; init; }
    public IReadOnlyList<string>? WarningCodes { get; init; }
    public IReadOnlyList<ConformanceHitExpectation>? Hits { get; init; }

    /// <summary>显式断言：终态/错误帧即使 revision 落后于快照也不得被去重丢弃。</summary>
    public bool? TerminalMustSurvive { get; init; }
}

public sealed record ConformanceLineExpectation
{
    public int Index { get; init; }
    public string? Contains { get; init; }
    public string? ExactText { get; init; }
    public string? Align { get; init; }
}

public sealed record ConformanceAwaitingExpectation
{
    public string? Mode { get; init; }
    public string? InputType { get; init; }
    public bool? IsWaitingForInput { get; init; }
    public bool? RequiresValue { get; init; }
    public bool? AllowsEmpty { get; init; }
    public bool? IsTerminal { get; init; }
    public string? Terminal { get; init; }
    public long? WaitDurationMs { get; init; }
    public bool? DisplayCountdown { get; init; }
    public string? TimeUpMessage { get; init; }
    public bool? TimedOut { get; init; }
    public bool? HasUnknownKeys { get; init; }
}

public sealed record ConformanceHitExpectation
{
    public int Column { get; init; }
    public int Row { get; init; }
    public bool ExpectHit { get; init; } = true;
    public JsonElement? Value { get; init; }
    public long? Generation { get; init; }
    public bool? IsLive { get; init; }
}
