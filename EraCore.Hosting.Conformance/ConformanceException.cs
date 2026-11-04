using System;
using System.Collections.Generic;
using System.Linq;

namespace EraCore.Hosting.Conformance;

/// <summary>夹具断言失败：带 fixtureId、失败项、期望/实际，供报告与测试直接读取。</summary>
public sealed class ConformanceException : Exception
{
    public ConformanceException(string fixtureId, string item, string? expected = null, string? actual = null)
        : base(BuildMessage(fixtureId, item, expected, actual))
    {
        FixtureId = fixtureId;
        Item = item;
        Expected = expected;
        Actual = actual;
        Failures = new[] { item };
    }

    public ConformanceException(string fixtureId, IReadOnlyList<string> failures)
        : base(BuildMessage(fixtureId, string.Join("; ", failures), null, null))
    {
        FixtureId = fixtureId;
        Failures = failures;
        Item = failures.FirstOrDefault() ?? string.Empty;
    }

    public string FixtureId { get; }

    public string Item { get; }

    public string? Expected { get; }

    public string? Actual { get; }

    public IReadOnlyList<string> Failures { get; }

    private static string BuildMessage(string fixtureId, string item, string? expected, string? actual)
    {
        var message = $"[{fixtureId}] {item}";
        if (expected is not null)
            message += $"；期望: {expected}";
        if (actual is not null)
            message += $"；实际: {actual}";
        return message;
    }
}
