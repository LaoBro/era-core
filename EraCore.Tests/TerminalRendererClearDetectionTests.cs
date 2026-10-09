using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// plan C 支线(2)：TerminalRenderer.ClassifyClearLineEvent 纯函数单测。
/// 该启发式原为 FlushBuffer 内联、零覆盖的核心逻辑（清空检测三子句 + SourceLine 引用反例）。
/// 抽成纯函数后直击清空语义，不依赖活屏/console。
/// </summary>
public class TerminalRendererClearDetectionTests
{
    private static ConsoleDisplayLine MakeSource() =>
        new(Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false);

    private static DisplayLine MakeLine(int lineNo, ConsoleDisplayLine source)
    {
        var line = new DisplayLine(new List<DisplayEntry>(), "left", isLineEnd: true);
        line.LineNo = lineNo;
        line.SourceLine = source;
        return line;
    }

    private static DisplaySnapshot Snapshot(params DisplayLine[] lines) =>
        new(new List<DisplayLine>(lines), null,
            AwaitInfo.Of(new InputRequest { InputType = InputType.EnterKey }, ConsoleState.WaitInput, timedOut: false),
            5, 0);

    // ---------- 子句 1：行数减少 → ClearLine ----------

    [Fact]
    public void Count_reduced_detects_clearline()
    {
        var s1 = MakeSource();
        var s2 = MakeSource();
        var s3 = MakeSource();
        var prevLast = MakeLine(3, s3);

        // 上一帧 3 行（lineNo 1/2/3）；当前帧只剩 1 行 → 行数减少
        var snapshot = Snapshot(MakeLine(1, s1));

        var result = TerminalRenderer.ClassifyClearLineEvent(
            snapshot, lastRenderedLineNo: 3, lastSnapshotLineCount: 3, lastRenderedLastLine: prevLast);

        Assert.Equal(TerminalRenderer.FlushClearLineEventType.ClearLine, result);
    }

    // ---------- 子句 2：末行 LineNo 回退 → ClearLine ----------

    [Fact]
    public void LineNo_rewind_detects_clearline()
    {
        var s1 = MakeSource();
        var s2 = MakeSource();
        var s3 = MakeSource();
        var prevLast = MakeLine(3, s3);

        // 行数未变（仍 3 行），但末行 LineNo 从 3 回退到 1（CLEARLINE + reprint 不足）
        var snapshot = Snapshot(MakeLine(1, s1), MakeLine(2, s2), MakeLine(1, s1));

        var result = TerminalRenderer.ClassifyClearLineEvent(
            snapshot, lastRenderedLineNo: 3, lastSnapshotLineCount: 3, lastRenderedLastLine: prevLast);

        Assert.Equal(TerminalRenderer.FlushClearLineEventType.ClearLine, result);
    }

    // ---------- 子句 3：SourceLine 引用变更（反例）→ ClearLine ----------

    [Fact]
    public void SourceLine_reference_change_detects_clearline()
    {
        var s1 = MakeSource();
        var s2 = MakeSource();
        var s3Old = MakeSource();
        var s3New = MakeSource(); // 不同引用 = 行被替换
        var prevLast = MakeLine(3, s3Old);

        // count/LineNo 回到旧值（3 行，lineNo 1/2/3），但末位 SourceLine 引用已变 → 内容全换
        var snapshot = Snapshot(MakeLine(1, s1), MakeLine(2, s2), MakeLine(3, s3New));

        var result = TerminalRenderer.ClassifyClearLineEvent(
            snapshot, lastRenderedLineNo: 3, lastSnapshotLineCount: 3, lastRenderedLastLine: prevLast);

        Assert.Equal(TerminalRenderer.FlushClearLineEventType.ClearLine, result);
    }

    // ---------- 无变化 → None ----------

    [Fact]
    public void No_change_detects_none()
    {
        var s1 = MakeSource();
        var s2 = MakeSource();
        var s3 = MakeSource();
        var prevLast = MakeLine(3, s3);

        // 与上一帧完全一致（同 count、同 LineNo、同 SourceLine 引用）
        var snapshot = Snapshot(MakeLine(1, s1), MakeLine(2, s2), MakeLine(3, s3));

        var result = TerminalRenderer.ClassifyClearLineEvent(
            snapshot, lastRenderedLineNo: 3, lastSnapshotLineCount: 3, lastRenderedLastLine: prevLast);

        Assert.Equal(TerminalRenderer.FlushClearLineEventType.None, result);
    }

    // ---------- 空屏 → None（Clear 由 FlushBuffer 的 lines.Count==0 分支处理）----------

    [Fact]
    public void Empty_snapshot_detects_none()
    {
        var prevLast = MakeLine(3, MakeSource());
        var snapshot = Snapshot(); // 0 行

        var result = TerminalRenderer.ClassifyClearLineEvent(
            snapshot, lastRenderedLineNo: 3, lastSnapshotLineCount: 3, lastRenderedLastLine: prevLast);

        Assert.Equal(TerminalRenderer.FlushClearLineEventType.None, result);
    }

    // ---------- 末行原地编辑（SourceLine 同引用）→ None ----------

    [Fact]
    public void In_place_last_line_edit_with_same_source_detects_none()
    {
        var s1 = MakeSource();
        var s2 = MakeSource();
        var s3 = MakeSource();
        var prevLast = MakeLine(3, s3);

        // 末行 LineNo 不变、SourceLine 引用不变（仅 entries 内容在别处被改写）→ 不应误判 ClearLine
        var snapshot = Snapshot(MakeLine(1, s1), MakeLine(2, s2), MakeLine(3, s3));

        var result = TerminalRenderer.ClassifyClearLineEvent(
            snapshot, lastRenderedLineNo: 3, lastSnapshotLineCount: 3, lastRenderedLastLine: prevLast);

        Assert.Equal(TerminalRenderer.FlushClearLineEventType.None, result);
    }
}
