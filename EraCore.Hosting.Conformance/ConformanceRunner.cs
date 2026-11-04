using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EraCore.Hosting;

namespace EraCore.Hosting.Conformance;

public enum FixtureStatus
{
    Passed,
    Failed,
    Skipped,
    DeclaredButUnverified,
}

/// <summary>单个夹具的结果。Message 在失败时是可直接阅读的期望/实际。</summary>
public sealed record FixtureResult(
    string FixtureId,
    FixtureStatus Status,
    string Message,
    IReadOnlyList<HostFeature> Features);

public sealed record ConformanceOptions(bool IncludeOptionalFixtures = false);

/// <summary>一次套件运行的完整报告。</summary>
public sealed record ConformanceReport(
    string HostName,
    HostManifest Manifest,
    IReadOnlyList<FixtureResult> Fixtures)
{
    public bool HasFailures => Fixtures.Any(f => f.Status == FixtureStatus.Failed);
    public int PassedCount => Fixtures.Count(f => f.Status == FixtureStatus.Passed);
    public int FailedCount => Fixtures.Count(f => f.Status == FixtureStatus.Failed);
    public int SkippedCount => Fixtures.Count(f => f.Status == FixtureStatus.Skipped);
    public int UnverifiedCount => Fixtures.Count(f => f.Status == FixtureStatus.DeclaredButUnverified);

    public string ToTextTable()
    {
        const string header = "STATUS | FIXTURE | MESSAGE";
        var rows = Fixtures.Select(f => $"{f.Status} | {f.FixtureId} | {f.Message}").ToList();
        var width = Math.Max(header.Length, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
        var builder = new StringBuilder();
        builder.AppendLine(new string('-', width));
        builder.AppendLine(header);
        builder.AppendLine(new string('-', width));
        foreach (var row in rows)
            builder.AppendLine(row);
        builder.AppendLine(new string('-', width));
        builder.AppendLine(
            $"passed={PassedCount} failed={FailedCount} skipped={SkippedCount} " +
            $"declared_but_unverified={UnverifiedCount} host={HostName}");
        return builder.ToString();
    }

    public string ToJson()
        => JsonSerializer.Serialize(this, ReportJsonOptions);

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>
/// 夹具套件执行器。所有夹具从程序集内嵌 JSON 载入；判定规则：
/// <list type="bullet">
/// <item>宿主未声明夹具所需能力 → Skipped（declared-unsupported）。</item>
/// <item>声明但断言失败 → Failed。</item>
/// <item>manifest 声明了某能力但没有任何夹具覆盖 → DeclaredButUnverified。</item>
/// </list>
/// </summary>
public sealed class ConformanceRunner
{
    private static readonly JsonSerializerOptions FixtureJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Lazy<IReadOnlyList<ConformanceFixture>> LoadedFixtures =
        new(LoadFixtures, isThreadSafe: true);

    public static IReadOnlyList<ConformanceFixture> Fixtures => LoadedFixtures.Value;

    public static async Task<ConformanceReport> RunAsync(
        IHostUnderTest host,
        ConformanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        options ??= new ConformanceOptions();

        var manifest = host.Declared;
        var results = new List<FixtureResult>();

        foreach (var fixture in Fixtures)
        {
            var features = fixture.Features;
            if (fixture.Optional && !options.IncludeOptionalFixtures)
            {
                results.Add(new FixtureResult(
                    fixture.Id, FixtureStatus.Skipped,
                    "optional fixture disabled by options", features));
                continue;
            }

            var unsupported = features.Where(f => !manifest.Has(f)).ToArray();
            if (unsupported.Length > 0)
            {
                results.Add(new FixtureResult(
                    fixture.Id, FixtureStatus.Skipped,
                    "declared-unsupported: " + string.Join(", ", unsupported),
                    features));
                continue;
            }

            try
            {
                await RunFixtureAsync(host, fixture).ConfigureAwait(false);
                results.Add(new FixtureResult(fixture.Id, FixtureStatus.Passed, "ok", features));
            }
            catch (ConformanceException ex)
            {
                results.Add(new FixtureResult(fixture.Id, FixtureStatus.Failed, ex.Message, features));
            }
            catch (Exception ex)
            {
                results.Add(new FixtureResult(
                    fixture.Id, FixtureStatus.Failed,
                    $"{ex.GetType().Name}: {ex.Message}", features));
            }
        }

        var covered = Fixtures.SelectMany(f => f.Features).ToHashSet();
        foreach (var feature in manifest.Features)
        {
            if (!covered.Contains(feature))
            {
                results.Add(new FixtureResult(
                    "declared:" + feature, FixtureStatus.DeclaredButUnverified,
                    "manifest declares " + feature + " but no fixture covers it",
                    new[] { feature }));
            }
        }

        return new ConformanceReport(host.HostName, manifest, results);
    }

    private static async Task RunFixtureAsync(IHostUnderTest host, ConformanceFixture fixture)
    {
        foreach (var step in fixture.Steps)
        {
            switch (step.Kind)
            {
                case "snapshot":
                    if (step.Snapshot is null)
                        throw new ConformanceException(fixture.Id, "snapshot step missing snapshot payload");
                    await host.LoadSnapshotAsync(step.Snapshot).ConfigureAwait(false);
                    break;
                case "turn":
                    if (step.Turn is null)
                        throw new ConformanceException(fixture.Id, "turn step missing turn payload");
                    await host.ApplyTurnAsync(step.Turn).ConfigureAwait(false);
                    break;
                case "expect":
                    if (step.Expect is null)
                        throw new ConformanceException(fixture.Id, "expect step missing expectation");
                    Evaluate(host, fixture.Id, step.Expect);
                    break;
                default:
                    throw new ConformanceException(fixture.Id, "unknown step kind: " + step.Kind);
            }
        }
    }

    private static void Evaluate(IHostUnderTest host, string fixtureId, ConformanceExpectation expect)
    {
        var failures = new List<string>();

        if (expect.Revision is { } expectedRevision)
            Require(failures, host.LastAppliedRevision == expectedRevision,
                "lastAppliedRevision", expectedRevision, host.LastAppliedRevision);

        var lines = host.RenderedLines;
        if (expect.LineCount is { } expectedCount)
            Require(failures, lines.Count == expectedCount,
                "lineCount", expectedCount, lines.Count);

        if (expect.Lines is { } lineExpectations)
        {
            foreach (var lineExpect in lineExpectations)
            {
                if (lineExpect.Index < 0 || lineExpect.Index >= lines.Count)
                {
                    failures.Add($"line[{lineExpect.Index}] 越界（实际行数 {lines.Count}）");
                    continue;
                }

                var actual = lines[lineExpect.Index];
                if (lineExpect.ExactText is { } exact)
                    Require(failures, actual.Text == exact,
                        $"line[{lineExpect.Index}].text", exact, actual.Text);
                if (lineExpect.Contains is { } contains)
                    Require(failures, actual.Text.Contains(contains, StringComparison.Ordinal),
                        $"line[{lineExpect.Index}].contains", contains, actual.Text);
                if (lineExpect.Align is { } align)
                    Require(failures, string.Equals(actual.Align, align, StringComparison.Ordinal),
                        $"line[{lineExpect.Index}].align", align, actual.Align);
            }
        }

        if (expect.Awaiting is { } awaitingExpect)
            EvaluateAwaiting(failures, host.Awaiting, awaitingExpect);

        if (expect.WarningCodes is { } warningCodes)
        {
            var actualCodes = host.Warnings.Select(w => w.Code).ToHashSet(StringComparer.Ordinal);
            foreach (var code in warningCodes)
                Require(failures, actualCodes.Contains(code),
                    "warningCodes", code, string.Join(",", actualCodes));
        }

        if (expect.Hits is { } hits)
        {
            foreach (var hitExpect in hits)
            {
                var hit = host.HitTest(hitExpect.Column, hitExpect.Row);
                if (!hitExpect.ExpectHit)
                {
                    Require(failures, hit is null,
                        $"hit({hitExpect.Column},{hitExpect.Row})", "null", DescribeHit(hit));
                    continue;
                }

                if (hit is null)
                {
                    failures.Add($"hit({hitExpect.Column},{hitExpect.Row}) 期望命中但实际为 null");
                    continue;
                }

                if (hitExpect.Value is { } expectedValue)
                    Require(failures, ValueEquals(hit.Value, expectedValue),
                        $"hit({hitExpect.Column},{hitExpect.Row}).value",
                        DescribeJson(expectedValue), hit.Value);
                if (hitExpect.Generation is { } expectedGeneration)
                    Require(failures, hit.Generation == expectedGeneration,
                        $"hit({hitExpect.Column},{hitExpect.Row}).generation",
                        expectedGeneration, hit.Generation);
                if (hitExpect.IsLive is { } expectedLive)
                    Require(failures, hit.IsLive == expectedLive,
                        $"hit({hitExpect.Column},{hitExpect.Row}).isLive",
                        expectedLive, hit.IsLive);
            }
        }

        if (expect.TerminalMustSurvive == true)
        {
            Require(failures, host.Awaiting.IsTerminal && host.Awaiting.Terminal is not null,
                "terminalMustSurvive", "terminal awaiting", host.Awaiting.Mode + "/" + host.Awaiting.Terminal);
        }

        if (failures.Count > 0)
            throw new ConformanceException(fixtureId, failures);
    }

    private static void EvaluateAwaiting(
        List<string> failures,
        AwaitingUiState actual,
        ConformanceAwaitingExpectation expect)
    {
        if (expect.Mode is { } mode)
            Require(failures, actual.Mode == mode, "awaiting.mode", mode, actual.Mode);
        if (expect.InputType is { } inputType)
            Require(failures, actual.InputType == inputType, "awaiting.inputType", inputType, actual.InputType);
        if (expect.IsWaitingForInput is { } waiting)
            Require(failures, actual.IsWaitingForInput == waiting, "awaiting.isWaitingForInput", waiting, actual.IsWaitingForInput);
        if (expect.RequiresValue is { } requires)
            Require(failures, actual.RequiresValue == requires, "awaiting.requiresValue", requires, actual.RequiresValue);
        if (expect.AllowsEmpty is { } allows)
            Require(failures, actual.AllowsEmpty == allows, "awaiting.allowsEmpty", allows, actual.AllowsEmpty);
        if (expect.IsTerminal is { } terminal)
            Require(failures, actual.IsTerminal == terminal, "awaiting.isTerminal", terminal, actual.IsTerminal);
        if (expect.Terminal is { } terminalName)
            Require(failures, actual.Terminal == terminalName, "awaiting.terminal", terminalName, actual.Terminal);
        if (expect.WaitDurationMs is { } duration)
            Require(failures, actual.WaitDurationMs == duration, "awaiting.waitDurationMs", duration, actual.WaitDurationMs);
        if (expect.DisplayCountdown is { } countdown)
            Require(failures, actual.DisplayCountdown == countdown, "awaiting.displayCountdown", countdown, actual.DisplayCountdown);
        if (expect.TimeUpMessage is { } timeUp)
            Require(failures, actual.TimeUpMessage == timeUp, "awaiting.timeUpMessage", timeUp, actual.TimeUpMessage);
        if (expect.TimedOut is { } timedOut)
            Require(failures, actual.TimedOut == timedOut, "awaiting.timedOut", timedOut, actual.TimedOut);
        if (expect.HasUnknownKeys is { } unknown)
            Require(failures, actual.HasUnknownKeys == unknown, "awaiting.hasUnknownKeys", unknown, actual.HasUnknownKeys);
    }

    private static void Require(
        List<string> failures,
        bool condition,
        string item,
        object? expected,
        object? actual)
    {
        if (condition)
            return;
        failures.Add($"{item} 期望 {Describe(expected)}，实际 {Describe(actual)}");
    }

    private static bool ValueEquals(object actual, JsonElement expected)
    {
        var expectedText = DescribeJson(expected);
        var actualText = actual.ToString() ?? string.Empty;
        return string.Equals(expectedText, actualText, StringComparison.Ordinal);
    }

    private static string DescribeJson(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out var integer)
                ? integer.ToString()
                : element.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => element.ToString(),
        };

    private static string DescribeHit(ButtonHit? hit)
        => hit is null
            ? "null"
            : $"{hit.Value}@gen={hit.Generation},live={hit.IsLive}";

    private static string Describe(object? value)
        => value switch
        {
            null => "null",
            string text => $"\"{text}\"",
            _ => value.ToString() ?? "null",
        };

    private static IReadOnlyList<ConformanceFixture> LoadFixtures()
    {
        var assembly = typeof(ConformanceRunner).Assembly;
        var fixtures = new List<ConformanceFixture>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!resource.Contains(".fixtures.", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
                continue;

            var fixture = JsonSerializer.Deserialize<ConformanceFixture>(stream, FixtureJsonOptions)
                ?? throw new InvalidOperationException("无法解析夹具 " + resource);
            fixtures.Add(fixture);
        }

        return fixtures.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
    }
}
