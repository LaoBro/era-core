using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EraCore.Hosting.Conformance;
using EraCore.Native;
using Xunit;

namespace EraCore.Hosting.Tests;

public sealed class ConformanceTests
{
    [Fact]
    public async Task ReferenceAdapter_PassesEveryRequiredFixture()
    {
        var host = new NativeHostUnderTest();

        var report = await ConformanceRunner.RunAsync(host);

        Assert.False(report.HasFailures, report.ToTextTable());
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(0, report.UnverifiedCount);
        Assert.Equal(14, report.PassedCount);
        Assert.All(report.Fixtures, fixture => Assert.Equal(FixtureStatus.Passed, fixture.Status));
        Assert.Contains("initial_snapshot", report.ToTextTable(), StringComparison.Ordinal);
        Assert.Contains("\"fixtureId\": \"revision_replay\"", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counterexample_IgnoringRevisionDedup_TurnsRevisionFixtureRed()
    {
        var host = new NativeHostUnderTest(new NativeHostOptions(HonorRevisionDedup: false));

        var report = await ConformanceRunner.RunAsync(host);

        var failure = AssertOnlyFixtureFailed(report, "revision_replay");
        Assert.Contains("lineCount", failure.Message, StringComparison.Ordinal);
        Assert.Contains("期望 3", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counterexample_UnknownAwaitingKeyWithoutWarning_TurnsUnknownFixtureRed()
    {
        var host = new SuppressWarningsHost(new NativeHostUnderTest());

        var report = await ConformanceRunner.RunAsync(host);

        var failure = AssertOnlyFixtureFailed(report, "awaiting_unknown_key");
        Assert.Contains("warningCodes", failure.Message, StringComparison.Ordinal);
        Assert.Contains("unknown-awaiting-key", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counterexample_DroppingTerminalFrame_TurnsErrorFixtureRed()
    {
        var host = new NativeHostUnderTest(new NativeHostOptions(ApplyTerminalDespiteRevision: false));

        var report = await ConformanceRunner.RunAsync(host);

        var failure = AssertOnlyFixtureFailed(report, "error_frame_fatal");
        Assert.Contains("terminalMustSurvive", failure.Message, StringComparison.Ordinal);
        Assert.Contains("terminal", failure.Message, StringComparison.Ordinal);
    }

    private static FixtureResult AssertOnlyFixtureFailed(ConformanceReport report, string fixtureId)
    {
        Assert.True(report.HasFailures, report.ToTextTable());
        var failed = report.Fixtures.Where(f => f.Status == FixtureStatus.Failed).ToArray();
        Assert.Equal(new[] { fixtureId }, failed.Select(f => f.FixtureId).ToArray());
        return failed[0];
    }

    private sealed class SuppressWarningsHost : IHostUnderTest
    {
        private readonly IHostUnderTest _inner;

        public SuppressWarningsHost(IHostUnderTest inner) => _inner = inner;

        public string HostName => _inner.HostName + " (warnings suppressed)";
        public HostManifest Declared => _inner.Declared;
        public IReadOnlyList<RenderedLine> RenderedLines => _inner.RenderedLines;
        public AwaitingUiState Awaiting => _inner.Awaiting;
        public long LastAppliedRevision => _inner.LastAppliedRevision;
        public IReadOnlyList<HostWarning> Warnings => Array.Empty<HostWarning>();

        public Task LoadSnapshotAsync(ConformanceSnapshot snapshot) => _inner.LoadSnapshotAsync(snapshot);
        public Task ApplyTurnAsync(ConformanceTurn turn) => _inner.ApplyTurnAsync(turn);
        public ButtonHit? HitTest(int column, int row) => _inner.HitTest(column, row);
        public Task<byte[]?> CapturePngAsync() => _inner.CapturePngAsync();
    }
}
