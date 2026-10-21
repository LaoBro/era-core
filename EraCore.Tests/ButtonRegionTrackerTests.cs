using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ButtonRegionTracker 单元测试（ADR-0010 C2/C3）。
/// 用 TestButtonFactory 创建测试按钮（null console → Generation=0），
/// 调 RecordLineRegions 记录区域，调 HitTest 断言命中/未命中。
/// 不依赖真实 EmueraConsole / IConsoleUI / Config。
/// </summary>
public class ButtonRegionTrackerTests
{
    // ---------- C2：HitTest 按钮命中 ----------

    [Fact]
    public void C2_hit_test_within_button_region_hits()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // formattedLine="" → LeadingDisplayWidth=0，按钮从 col=0 开始
        // "[OK]" 显示宽度=4，区域 col=[0,3]
        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        var hit = tracker.HitTest(row: 10, col: 0);
        Assert.True(hit.HasValue);
        // Phase 3-3：value-based Region，Value=1L 装箱、IsInteger=true
        Assert.Equal(1L, hit!.Value.Value);
        Assert.True(hit.Value.IsInteger);
    }

    [Fact]
    public void C2_hit_test_at_right_edge_hits()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        // col=3 是 "[OK]" 的最后一列（区域 [0,3]）
        var hit = tracker.HitTest(row: 10, col: 3);
        Assert.True(hit.HasValue);
    }

    [Fact]
    public void C2_hit_test_past_right_edge_misses()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        // col=4 在区域 [0,3] 之外
        var hit = tracker.HitTest(row: 10, col: 4);
        Assert.False(hit.HasValue);
    }

    [Fact]
    public void C2_hit_test_wrong_row_misses()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 10, buttons, currentGeneration: 0);

        var hit = tracker.HitTest(row: 9, col: 0);
        Assert.False(hit.HasValue);
    }

    [Fact]
    public void C2_leading_display_width_offsets_button_column()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // formattedLine="  " → LeadingDisplayWidth=2，按钮从 col=2 开始
        // 区域 col=[2,5]
        tracker.RecordLineRegions("  ", bufferRow: 0, buttons, currentGeneration: 0);

        Assert.False(tracker.HitTest(row: 0, col: 1).HasValue); // col=1 在区域之前
        Assert.True(tracker.HitTest(row: 0, col: 2).HasValue); // col=2 是起始
        Assert.True(tracker.HitTest(row: 0, col: 5).HasValue); // col=5 是末列
        Assert.False(tracker.HitTest(row: 0, col: 6).HasValue); // col=6 在区域之后
    }

    [Fact]
    public void C2_multiple_buttons_on_same_line()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = TestButtonFactory.CreateButtons(("[OK]", 1), ("[Cancel]", 2));

        // "[OK]" 宽度=4 → col=[0,3]；"[Cancel]" 宽度=8 → col=[4,11]
        tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 0);

        var hitOk = tracker.HitTest(row: 5, col: 1);
        Assert.True(hitOk.HasValue);
        Assert.Equal(1L, hitOk!.Value.Value);

        var hitCancel = tracker.HitTest(row: 5, col: 7);
        Assert.True(hitCancel.HasValue);
        Assert.Equal(2L, hitCancel!.Value.Value);
    }

    [Fact]
    public void C2_overlapping_buttons_last_recorded_wins()
    {
        var tracker = new ButtonRegionTracker();
        var btn1 = TestButtonFactory.CreateButton("[OK]", input: 1);
        var btn2 = TestButtonFactory.CreateButton("[NO]", input: 2);

        // 同一行同一位置先记录 btn1，再记录 btn2
        tracker.RecordLineRegions("X", bufferRow: 0, new[] { btn1 }, currentGeneration: 0);
        tracker.RecordLineRegions("X", bufferRow: 0, new[] { btn2 }, currentGeneration: 0);

        // HitTest 从后往前匹配——后记录的 btn2 优先
        var hit = tracker.HitTest(row: 0, col: 0);
        Assert.True(hit.HasValue);
        Assert.Equal(2L, hit!.Value.Value);
    }

    [Fact]
    public void C2_empty_buttons_array_no_regions_recorded()
    {
        var tracker = new ButtonRegionTracker();

        tracker.RecordLineRegions("X", bufferRow: 0, Array.Empty<ConsoleButtonString>(), currentGeneration: 0);

        Assert.Equal(0, tracker.RegionCount);
    }

    [Fact]
    public void C2_null_buttons_array_no_regions_recorded()
    {
        var tracker = new ButtonRegionTracker();

        tracker.RecordLineRegions("X", bufferRow: 0, null, currentGeneration: 0);

        Assert.Equal(0, tracker.RegionCount);
    }

    [Fact]
    public void C2_clear_removes_all_regions()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        tracker.RecordLineRegions("X", bufferRow: 0, buttons, currentGeneration: 0);
        Assert.Equal(1, tracker.RegionCount);

        tracker.Clear();
        Assert.Equal(0, tracker.RegionCount);
        Assert.False(tracker.HitTest(row: 0, col: 0).HasValue);
    }

    // ---------- C3：Generation 过滤移除（Phase 3-3 / ADR-0014） ----------
    // Phase 3-3 起 ButtonRegionTracker 不再按 Generation 过滤——服务端 ConsoleInputHandler
    // 按 button.Generation 校验兜底。currentGeneration 参数保留以维持签名兼容，但不再用于过滤。

    [Fact]
    public void C3_button_registered_regardless_of_generation_mismatch()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // Generation=0（null console），currentGeneration=1 → 旧路径会过滤，Phase 3-3 后不过滤
        tracker.RecordLineRegions("X", bufferRow: 0, buttons, currentGeneration: 1);

        Assert.Equal(1, tracker.RegionCount);
        Assert.True(tracker.HitTest(row: 0, col: 0).HasValue);
    }

    [Fact]
    public void C3_generation_change_does_not_clear_old_regions()
    {
        var tracker = new ButtonRegionTracker();
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };

        // Generation 0：记录按钮
        tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 0);
        Assert.True(tracker.HitTest(row: 5, col: 0).HasValue);

        // 新 Generation：先 Clear，再 RecordLineRegions（模拟 RefreshButtonRegions 流程）
        tracker.Clear();
        tracker.RecordLineRegions("X", bufferRow: 5, buttons, currentGeneration: 1);

        // Phase 3-3：Generation 不再过滤，按钮仍被记录（服务端兜底校验）
        Assert.Equal(1, tracker.RegionCount);
        Assert.True(tracker.HitTest(row: 5, col: 0).HasValue);
    }

    [Fact]
    public void C3_non_button_not_registered()
    {
        var tracker = new ButtonRegionTracker();
        var nonButton = TestButtonFactory.CreateNonButton("[Text]");

        tracker.RecordLineRegions("X", bufferRow: 0, new[] { nonButton }, currentGeneration: 0);

        Assert.Equal(0, tracker.RegionCount);
    }

    // ---------- Phase 3-3：UpdateFromSnapshot 新路径 ----------

    [Fact]
    public void UpdateFromSnapshot_no_buttons_no_regions()
    {
        var snapshot = MakeSnapshot(
            new DisplayLine(new List<DisplayEntry> { MakeEntry(button: null) }, null, true));

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 2);

        Assert.Equal(0, tracker.RegionCount);
    }

    [Fact]
    public void UpdateFromSnapshot_single_button_registered()
    {
        // 单行单按钮：col=2, width=4；terminalHeight=2 ⇒ 视口 1 行、内容 1 行 ⇒ baseRow=0
        // → Region(Row=0, Left=2, Right=5, Value=1L, IsInteger=true)
        var snapshot = MakeSnapshot(
            new DisplayLine(new List<DisplayEntry> { MakeEntry(button: TestSnapshots.Button(1L, true, 0, 2, 4)) }, null, true));

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 2);

        Assert.Equal(1, tracker.RegionCount);
        var hit = tracker.HitTest(row: 0, col: 2);
        Assert.True(hit.HasValue);
        Assert.Equal(2, hit!.Value.Left);
        Assert.Equal(5, hit.Value.Right);
        Assert.Equal(1L, hit.Value.Value);
        Assert.True(hit.Value.IsInteger);
    }

    [Fact]
    public void UpdateFromSnapshot_string_button_value()
    {
        // 字符串按钮：value="ok", IsInteger=false
        var snapshot = MakeSnapshot(
            new DisplayLine(new List<DisplayEntry> { MakeEntry(button: TestSnapshots.Button("ok", false, 0, 0, 4)) }, null, true));

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 2);

        var hit = tracker.HitTest(row: 0, col: 0);
        Assert.True(hit.HasValue);
        Assert.Equal("ok", hit!.Value.Value);
        Assert.False(hit.Value.IsInteger);
    }

    [Fact]
    public void UpdateFromSnapshot_multiple_lines_row_indexed_by_viewport_position()
    {
        // 3 行，每行一个按钮；terminalHeight=4 ⇒ 视口 3 行，scrollOffset=0
        // 内容填满视口 ⇒ baseRow = 0 ⇒ Row 仍为 0/1/2（与贴底前一致）
        var snapshot = MakeSnapshot(
            MakeLineWithButton(0, 2, 7L),
            MakeLineWithButton(0, 2, 8L),
            MakeLineWithButton(0, 2, 9L));

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 4);

        // Row=0/1/2 对应快照第 0/1/2 行
        Assert.Equal(7L, tracker.HitTest(row: 0, col: 0)!.Value.Value);
        Assert.Equal(8L, tracker.HitTest(row: 1, col: 0)!.Value.Value);
        Assert.Equal(9L, tracker.HitTest(row: 2, col: 0)!.Value.Value);
    }

    [Fact]
    public void UpdateFromSnapshot_scrollOffset_shifts_visible_window()
    {
        // 5 行，terminalHeight=4 + scrollOffset=1 ⇒ VisibleLines = 4-2 = 2
        // startLine = TerminalViewportGeometry.StartLine(5, 4, 1) = max(0, 5 - 2 - 1) = 2
        // 视口第 0 行 = 快照第 2 行；视口第 1 行 = 快照第 3 行
        // Scroll Mode 下内容填满视口 ⇒ baseRow = 0（贴底 = 顶部对齐）
        var snapshot = MakeSnapshot(
            MakeLineWithButton(0, 2, 10L),
            MakeLineWithButton(0, 2, 11L),
            MakeLineWithButton(0, 2, 12L),
            MakeLineWithButton(0, 2, 13L),
            MakeLineWithButton(0, 2, 14L));

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 1, terminalHeight: 4);

        Assert.Equal(2, tracker.RegionCount);
        Assert.Equal(12L, tracker.HitTest(row: 0, col: 0)!.Value.Value);
        Assert.Equal(13L, tracker.HitTest(row: 1, col: 0)!.Value.Value);
    }

    [Fact]
    public void UpdateFromSnapshot_clears_previous_regions()
    {
        var snapshot1 = MakeSnapshot(MakeLineWithButton(0, 2, 1L));
        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot1, scrollOffset: 0, terminalHeight: 2);
        Assert.Equal(1, tracker.RegionCount);

        // 第二次 UpdateFromSnapshot 应先清空再构建
        var snapshot2 = MakeSnapshot(
            new DisplayLine(new List<DisplayEntry> { MakeEntry(button: null) }, null, true));
        tracker.UpdateFromSnapshot(snapshot2, scrollOffset: 0, terminalHeight: 2);
        Assert.Equal(0, tracker.RegionCount);
    }

    // ---------- 贴底（2026.10.14 spec §3.3）：Row 必须是终端绝对行 ----------

    /// <summary>
    /// 🔒 稀疏画面：内容 1 行、终端 29 行 ⇒ 视口 28 行、baseRow = 27。
    /// 按钮必须登记在终端行 27（渲染器写屏行），而不是行 0 —— 否则 SGR mouse 报绝对行 27 时
    /// <c>HitTest</c> 落空，贴底后按钮点不到。
    /// </summary>
    [Fact]
    public void UpdateFromSnapshot_bottom_anchors_sparse_content()
    {
        var snapshot = MakeSnapshot(MakeLineWithButton(0, 2, 42L));

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 29);

        Assert.Equal(1, tracker.RegionCount);
        // 与 TerminalViewportGeometry 的贴底基准行一致（= TerminalRenderer 的 baseRow）
        int baseRow = TerminalViewportGeometry.BaseRow(1, 29, 0);
        Assert.Equal(27, baseRow);
        // 按钮区域 col=[0,1]（MakeLineWithButton(col:0, width:2)）
        Assert.True(tracker.HitTest(row: baseRow, col: 0).HasValue);
        Assert.Equal(42L, tracker.HitTest(row: 27, col: 0)!.Value.Value);
        // 顶部留白行不应命中（贴底前按钮登记在 row 0，正是这里的差异点）
        Assert.False(tracker.HitTest(row: 0, col: 0).HasValue);
        Assert.False(tracker.HitTest(row: 26, col: 0).HasValue);
    }

    /// <summary>
    /// 稀疏多行：3 行内容 / 终端 29 行 ⇒ baseRow = 25 ⇒ 行 25/26/27，逐行对应内容 0/1/2。
    /// </summary>
    [Fact]
    public void UpdateFromSnapshot_bottom_anchored_rows_are_absolute()
    {
        var snapshot = MakeSnapshot(
            MakeLineWithButton(0, 2, 7L),
            MakeLineWithButton(0, 2, 8L),
            MakeLineWithButton(0, 2, 9L));

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 29);

        int baseRow = TerminalViewportGeometry.BaseRow(3, 29, 0);
        Assert.Equal(25, baseRow);
        Assert.Equal(7L, tracker.HitTest(row: 25, col: 0)!.Value.Value);
        Assert.Equal(8L, tracker.HitTest(row: 26, col: 0)!.Value.Value);
        Assert.Equal(9L, tracker.HitTest(row: 27, col: 0)!.Value.Value);
        Assert.False(tracker.HitTest(row: 24, col: 0).HasValue);
        Assert.False(tracker.HitTest(row: 28, col: 0).HasValue);
    }

    /// <summary>
    /// 命中区行号集合与 <c>TerminalRenderer</c> 的写屏行号集合**逐行一致** —— 二者的差集
    /// 就是"看得见但点不到 / 点得到但看不见"的按钮。这里用同一份几何（BaseRow/RenderedLines）
    /// 推出渲染行，断言与 tracker 登记的行完全相同。
    /// </summary>
    [Theory]
    [InlineData(3, 29, 0)]     // 稀疏
    [InlineData(28, 29, 0)]    // 刚好填满
    [InlineData(100, 29, 0)]   // 满屏（baseRow=0）
    [InlineData(100, 33, 10)]  // Scroll Mode（baseRow=0）
    public void UpdateFromSnapshot_rows_match_renderer_write_rows(int lineCount, int terminalHeight, int scrollOffset)
    {
        var lines = new DisplayLine[lineCount];
        for (int i = 0; i < lineCount; i++)
            lines[i] = MakeLineWithButton(0, 2, (long)i);
        var snapshot = MakeSnapshot(lines);

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset, terminalHeight);

        int startLine = TerminalViewportGeometry.StartLine(lineCount, terminalHeight, scrollOffset);
        int rendered = TerminalViewportGeometry.RenderedLines(lineCount, terminalHeight, scrollOffset);
        int baseRow = TerminalViewportGeometry.BaseRow(lineCount, terminalHeight, scrollOffset);

        Assert.Equal(rendered, tracker.RegionCount);
        for (int i = 0; i < rendered; i++)
        {
            int renderRow = baseRow + i;                    // TerminalRenderer 写屏行
            int expectedValue = startLine + i;              // 该行对应的内容行号（= 按钮值）
            var hit = tracker.HitTest(row: renderRow, col: 0);
            Assert.True(hit.HasValue, $"渲染行 {renderRow} 应有命中区");
            Assert.Equal((long)expectedValue, hit!.Value.Value);
        }
    }

    // ---------- Phase 4 align 偏移回归 ----------

    /// <summary>
    /// Phase 4 回归：align=CENTER/RIGHT 行的按钮命中区应包含居中/右对齐前导空格偏移。
    /// BuildPrintOpsForLine 中 col 从 0 累加（相对列），不含 align 前导空格——
    /// DisplayLine.AlignOffset 由 DisplayState.BuildSnapshot 填充（与 FormatLineForTerminal 一致），
    /// UpdateFromSnapshot 把 AlignOffset 加到 button.col 上得到 PTY 绝对列。
    /// 不加偏移时点击 PTY 中显示的居中按钮位置会落空（用户报告"点击任何按钮都无效"）。
    /// </summary>
    [Fact]
    public void UpdateFromSnapshot_align_offset_added_to_button_col()
    {
        // 模拟 align=CENTER 行：AlignOffset=10，button.col=2, width=4
        // 期望 Region.Left=12, Right=15（绝对列，与 PTY 显示位置匹配）
        var line = MakeLineWithButton(col: 2, width: 4, value: 1L);
        line.AlignOffset = 10;
        var snapshot = MakeSnapshot(line);

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 2);

        Assert.Equal(1, tracker.RegionCount);
        // col=2（相对）在绝对列 12 之前——不应命中
        Assert.False(tracker.HitTest(row: 0, col: 2).HasValue, "相对列位置不应命中（align 偏移未应用）");
        // col=12 是绝对起始列——应命中
        var hit = tracker.HitTest(row: 0, col: 12);
        Assert.True(hit.HasValue, "绝对列起始位置应命中");
        Assert.Equal(12, hit!.Value.Left);
        Assert.Equal(15, hit.Value.Right);
        // col=15 是末列——应命中
        Assert.True(tracker.HitTest(row: 0, col: 15).HasValue, "绝对列末列应命中");
        // col=16 在区域之后——不应命中
        Assert.False(tracker.HitTest(row: 0, col: 16).HasValue, "绝对列之后不应命中");
    }

    [Fact]
    public void UpdateFromSnapshot_zero_align_offset_equivalent_to_no_offset()
    {
        // AlignOffset=0（LEFT align 或未设置）时行为与旧路径一致
        var line = MakeLineWithButton(col: 2, width: 4, value: 1L);
        // AlignOffset 默认 0
        var snapshot = MakeSnapshot(line);

        var tracker = new ButtonRegionTracker();
        tracker.UpdateFromSnapshot(snapshot, scrollOffset: 0, terminalHeight: 2);

        var hit = tracker.HitTest(row: 0, col: 2);
        Assert.True(hit.HasValue);
        Assert.Equal(2, hit!.Value.Left);
        Assert.Equal(5, hit.Value.Right);
    }

    // ---------- 测试夹具 ----------

    private static DisplaySnapshot MakeSnapshot(params DisplayLine[] lines)
        => new(new List<DisplayLine>(lines), null,
            AwaitInfo.Of(new InputRequest { InputType = InputType.EnterKey }, ConsoleState.WaitInput, timedOut: false),
            3, 0);

    private static DisplayLine MakeLineWithButton(int col, int width, long value)
        => new(new List<DisplayEntry> { MakeEntry(button: TestSnapshots.Button(value, true, 0, col, width)) }, null, true);

    private static DisplayEntry MakeEntry(ButtonRef? button)
        => new(new List<PrintSegment>(), button);
}
