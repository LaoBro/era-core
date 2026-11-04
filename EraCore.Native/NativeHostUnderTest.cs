using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EraCore.Hosting;
using EraCore.Hosting.Conformance;
using MinorShift.Emuera.Input;

namespace EraCore.Native;

/// <summary>参考适配器的行为开关；默认即协议正确行为。</summary>
public sealed record NativeHostOptions(
    int ContainerColumns = NativeHostUnderTest.DefaultContainerColumns,
    bool HonorRevisionDedup = true,
    bool ApplyTerminalDespiteRevision = true);

/// <summary>
/// EraCore.Native 的一致性参考适配器：把 Conformance 的协议镜像投影成与
/// <see cref="NativeRenderModel"/> 相同的 <see cref="NativeFrame"/> 语义，
/// 并实现 awaiting 投影、revision 去重（终态例外）与字符列命中。
///
/// <para>只依赖 Hosting/Hosting.Conformance；不引用 Android/Avalonia。</p>
/// </summary>
public sealed class NativeHostUnderTest : IHostUnderTest
{
    public const int DefaultContainerColumns = 84;

    /// <summary>本适配器在 manifest 中声明、且均有夹具覆盖的能力项。</summary>
    public static IReadOnlyList<HostFeature> SupportedFeatures { get; } = new[]
    {
        HostFeature.Snapshot,
        HostFeature.TurnStream,
        HostFeature.RevisionDedup,
        HostFeature.DiffAppend,
        HostFeature.DiffReplaceEqualLength,
        HostFeature.DiffShiftHead,
        HostFeature.DiffClearScreen,
        HostFeature.Generation,
        HostFeature.AwaitingInput,
        HostFeature.AwaitingTimeout,
        HostFeature.AwaitingZeroDurationWait,
        HostFeature.AwaitingUnknownKey,
        HostFeature.TerminalFrames,
        HostFeature.ErrorFrame,
        HostFeature.TextStyles,
        HostFeature.AlignGeometry,
        HostFeature.ButtonHitTest,
    };

    private readonly NativeHostOptions _options;
    private readonly List<HostWarning> _warnings = new();

    private NativeFrame _frame = NativeFrame.Empty;
    private ConformanceAwaiting _awaitingSource = new();
    private IReadOnlyList<RenderedLine> _rendered = Array.Empty<RenderedLine>();
    private long _lastRevision;
    private DateTimeOffset? _waitStartedAt;

    public NativeHostUnderTest(NativeHostOptions? options = null)
    {
        _options = options ?? new NativeHostOptions();
        Declared = new HostManifest(
            HostContract.ApiVersion,
            HostContract.DataVersion,
            new InputCapabilities(InputCapability.Geometry),
            RenderCapability.Text,
            SupportedFeatures);
    }

    public string HostName => "EraCore.Native reference adapter";

    public HostManifest Declared { get; }

    public IReadOnlyList<RenderedLine> RenderedLines => _rendered;

    public AwaitingUiState Awaiting => AwaitingUiState.From(_awaitingSource, _waitStartedAt);

    public long LastAppliedRevision => _lastRevision;

    public IReadOnlyList<HostWarning> Warnings => _warnings;

    public Task LoadSnapshotAsync(ConformanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _warnings.Clear();
        _frame = new NativeFrame(
            snapshot.Lines.Select(ProjectLine).ToArray(),
            string.IsNullOrEmpty(snapshot.BgColor) ? "#000000" : snapshot.BgColor,
            snapshot.Generation,
            snapshot.ProtocolVersion,
            snapshot.Revision,
            snapshot.Awaiting.Ended?.Terminal);
        _awaitingSource = snapshot.Awaiting;
        _lastRevision = snapshot.Revision;
        _waitStartedAt = snapshot.Awaiting.Wait is { DurationMs: > 0 } ? DateTimeOffset.UtcNow : null;

        ObserveAwaiting(snapshot.Awaiting);
        RefreshRendered();
        return Task.CompletedTask;
    }

    public Task ApplyTurnAsync(ConformanceTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var dedupWouldDrop = turn.Revision <= _lastRevision;
        if (_options.HonorRevisionDedup && dedupWouldDrop)
        {
            // 终态/错误帧不受 revision 去重约束；其它陈旧帧丢弃。
            if (!turn.IsTerminal || !_options.ApplyTerminalDespiteRevision)
                return Task.CompletedTask;
        }

        ApplyCore(turn);
        return Task.CompletedTask;
    }

    public ButtonHit? HitTest(int column, int row)
    {
        if (row < 0 || row >= _rendered.Count)
            return null;

        var line = _rendered[row];
        var contentColumns = line.Text.Length;
        var offset = AlignGeometry.OffsetColumns(line.Align, contentColumns, _options.ContainerColumns);

        foreach (var button in line.Buttons)
        {
            if (button.Col is not { } col || button.Width is not { } width || width <= 0)
                continue;

            var start = col + offset;
            if (column < start || column >= start + width)
                continue;

            return new ButtonHit(
                column,
                row,
                button.Value,
                button.IsInteger,
                button.Generation,
                Awaiting.CanSubmitButton(button.Generation, _frame.Generation));
        }

        return null;
    }

    public Task<byte[]?> CapturePngAsync() => Task.FromResult<byte[]?>(null);

    private void ApplyCore(ConformanceTurn turn)
    {
        var lines = _frame.Lines.ToList();

        if (turn.Diff is { } diff)
        {
            foreach (var op in diff.LineOps)
            {
                switch (op.Type)
                {
                    case "append":
                        if (op.NewLines is { } newLines)
                            lines.AddRange(newLines.Select(ProjectLine));
                        break;
                    case "clear_line_diff":
                        RemoveTail(lines, op.ClearCount ?? 0);
                        break;
                    case "clear_screen":
                        lines.Clear();
                        break;
                    case "shift_head":
                        RemoveHead(lines, op.Count ?? 0);
                        break;
                }
            }
        }

        _frame = new NativeFrame(
            lines,
            string.IsNullOrEmpty(turn.Diff?.BgColor) ? _frame.BackgroundColor : turn.Diff.BgColor,
            turn.Generation,
            turn.ProtocolVersion ?? _frame.ProtocolVersion,
            Math.Max(_frame.Revision, turn.Revision),
            turn.Awaiting.Ended?.Terminal);

        _lastRevision = Math.Max(_lastRevision, turn.Revision);
        _awaitingSource = turn.Awaiting;
        if (turn.Awaiting.Wait is { DurationMs: > 0 })
        {
            _waitStartedAt ??= DateTimeOffset.UtcNow;
        }
        else
        {
            _waitStartedAt = null;
        }

        ObserveAwaiting(turn.Awaiting);
        RefreshRendered();
    }

    private void ObserveAwaiting(ConformanceAwaiting awaiting)
    {
        if (!awaiting.HasUnknownKeys)
            return;

        var keys = awaiting.Unknown!.Keys.OrderBy(k => k, StringComparer.Ordinal);
        _warnings.Add(new HostWarning(
            "unknown-awaiting-key",
            "unknown awaiting keys: " + string.Join(", ", keys)));
    }

    private void RefreshRendered()
    {
        _rendered = _frame.Lines.Select(line => new RenderedLine(
            line.Entries
                .SelectMany(entry => entry.Segments)
                .Select(segment => new RenderedSegment(
                    segment.Text,
                    segment.Color,
                    segment.Bold,
                    segment.Italic,
                    segment.FontName,
                    segment.Underline,
                    segment.Strikeout,
                    segment.IsImage,
                    segment.IsShape))
                .ToArray(),
            line.Align,
            line.Entries
                .Where(entry => entry.Button is not null)
                .Select(entry => new RenderedButton(
                    entry.Button!.Value,
                    entry.Button.IsInteger,
                    entry.Button.Generation,
                    entry.Button.Col,
                    entry.Button.Width))
                .ToArray()))
            .ToArray();
    }

    private static NativeLine ProjectLine(ConformanceLine line)
        => new(
            line.Entries
                .Select(entry => new NativeEntry(
                    entry.Segments
                        .Select(segment => new NativeSegment(
                            segment.Text ?? string.Empty,
                            segment.Color,
                            segment.Bold == true,
                            segment.Italic == true,
                            segment.FontName,
                            segment.Underline == true,
                            segment.Strikeout == true,
                            segment.Image is not null,
                            segment.Shape is not null))
                        .ToArray(),
                    entry.Button is null
                        ? null
                        : new NativeButton(
                            entry.Button.ToValue(),
                            entry.Button.IsInteger,
                            entry.Button.Generation,
                            entry.Button.Col,
                            entry.Button.Width)))
                .ToArray(),
            line.Align);

    private static void RemoveHead(List<NativeLine> lines, int count)
    {
        if (count <= 0)
            return;
        lines.RemoveRange(0, Math.Min(count, lines.Count));
    }

    private static void RemoveTail(List<NativeLine> lines, int count)
    {
        if (count <= 0)
            return;
        var start = Math.Max(0, lines.Count - count);
        lines.RemoveRange(start, lines.Count - start);
    }
}
