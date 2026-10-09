using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// TestAdapter seam 真实性验证单测（ADR-0013 决策四）。
/// 验证 TestAdapter 能从 DisplayState 输出重建显示状态——
/// 如果 TestAdapter 能做到，任何前端（Web/移动/另一个 CLI）也能。
///
/// 8 个场景：
/// - T-snapshot：displayLineList → GetSnapshot → ApplySnapshot → CurrentState 与原 displayLineList 逐字段比对
/// - T-ops-print：ApplyOps([PrintOp, NewLineOp]) → 新行追加，button col/width 记录
/// - T-ops-clearline：ApplyOps([ClearLineOp(2)]) → 末尾 2 行删除
/// - T-ops-clear：ApplyOps([ClearOp]) → 全部行清空 + bgColor 重置
/// - T-ops-setbg：ApplyOps([SetBgOp]) → bgColor 更新
/// - T-ops-geometry：ApplyOps([PrintOp with ButtonRef(col=5, width=4)]) → 几何正确记录
/// - T-full-roundtrip：ApplySnapshot(初始快照) → ApplyOps(增量 ops) → 最终 CurrentState 正确
/// - T-multi-button-line：ApplySnapshot(多按钮行) → 每个按钮 col 按前序宽度累加
/// </summary>
public class TestAdapterTests
{
    // ---------- T-snapshot：全量快照重建 ----------

    [Fact]
    public void T_snapshot_reconstructs_displayLineList_field_by_field()
    {
        // 构造已知 displayLineList：2 行，第一行单按钮，第二行多按钮（含非按钮）
        var line1Buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        var line2Buttons = new ConsoleButtonString[]
        {
            TestButtonFactory.CreateButton("[Yes]", 10),
            TestButtonFactory.CreateNonButton(" or "),
            TestButtonFactory.CreateButton("[No]", 20),
        };
        var displayLineList = new List<ConsoleDisplayLine>
        {
            new ConsoleDisplayLine(line1Buttons, isLogical: true, temporary: false),
            new ConsoleDisplayLine(line2Buttons, isLogical: true, temporary: false),
        };

        // 通过 DisplayState 序列化为快照
        var snapshot = DisplayState.BuildSnapshot(
            displayLineList,
            bgColor: EmuColor.FromArgb(10, 20, 30),
            state: ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        // TestAdapter 从快照重建状态
        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        // 断言重建状态与原 displayLineList 一一对应
        Assert.Equal(2, adapter.Lines.Count);
        Assert.Equal("#0A141E", adapter.BgColor);
        // v12：非终态 + req=null ⇒ 两个正交维度皆无（`{}`，快照瞬态形态）
        Assert.NotNull(adapter.Awaiting);
        Assert.Null(adapter.Awaiting!.input);
        Assert.Null(adapter.Awaiting!.wait);
        Assert.Null(adapter.Awaiting!.ended);

        // 第一行：单按钮
        var line1 = adapter.Lines[0];
        Assert.Single(line1.Entries);
        Assert.True(line1.IsLineEnd);
        Assert.Equal("left", line1.Align); // 默认 LEFT

        var entry1 = line1.Entries[0];
        Assert.Equal("[OK]", entry1.Segments[0].text);
        Assert.NotNull(entry1.Button);
        Assert.Equal(0, entry1.Button!.col);
        Assert.Equal(4, entry1.Button.width);
        Assert.Equal(1L, (long)entry1.Button.value);
        Assert.True(entry1.Button.isInteger);

        // 第二行：3 entries（button + non-button + button），col 累加
        var line2 = adapter.Lines[1];
        Assert.Equal(3, line2.Entries.Count);

        Assert.Equal(0, line2.Entries[0].Button!.col);  // "[Yes]" width=5
        Assert.Equal(5, line2.Entries[0].Button!.width);

        Assert.Null(line2.Entries[1].Button);             // " or " 非按钮

        Assert.Equal(9, line2.Entries[2].Button!.col);    // 5 + 4 = 9
        Assert.Equal(4, line2.Entries[2].Button!.width);  // "[No]" width=4
    }

    // ---------- T-ops-print：增量 print + newline ----------

    [Fact]
    public void T_ops_print_appends_entry_and_newline_terminates_line()
    {
        var adapter = new TestAdapter();

        var segments = new List<PrintSegment> { new("Hello", null, null, null, null) };
        var button = new ButtonRef(value: 42L, isInteger: true, generation: 0, col: 0, width: 5);
        var ops = new List<TurnOp>
        {
            new PrintOp(segments, button),
            new NewLineOp(null),
        };

        adapter.ApplyOps(ops);

        Assert.Single(adapter.Lines);
        var line = adapter.Lines[0];
        Assert.True(line.IsLineEnd);
        Assert.Single(line.Entries);

        var entry = line.Entries[0];
        Assert.Equal("Hello", entry.Segments[0].text);
        Assert.NotNull(entry.Button);
        Assert.Equal(0, entry.Button!.col);
        Assert.Equal(5, entry.Button.width);
        Assert.Equal(42L, (long)entry.Button.value);
    }

    // ---------- T-ops-clearline：删除末尾 n 行 ----------

    [Fact]
    public void T_ops_clearline_removes_last_n_lines()
    {
        var adapter = new TestAdapter();
        // 先建 3 行
        adapter.ApplyOps(new List<TurnOp>
        {
            new PrintOp(new List<PrintSegment> { new("A", null, null, null, null) }, null),
            new NewLineOp(null),
            new PrintOp(new List<PrintSegment> { new("B", null, null, null, null) }, null),
            new NewLineOp(null),
            new PrintOp(new List<PrintSegment> { new("C", null, null, null, null) }, null),
            new NewLineOp(null),
        });
        Assert.Equal(3, adapter.Lines.Count);

        adapter.ApplyOps(new List<TurnOp> { new ClearLineOp(2) });

        Assert.Single(adapter.Lines);
        Assert.Equal("A", adapter.Lines[0].Entries[0].Segments[0].text);
    }

    [Fact]
    public void T_ops_clearline_with_n_exceeding_count_clears_all()
    {
        var adapter = new TestAdapter();
        adapter.ApplyOps(new List<TurnOp>
        {
            new PrintOp(new List<PrintSegment> { new("only", null, null, null, null) }, null),
            new NewLineOp(null),
        });

        adapter.ApplyOps(new List<TurnOp> { new ClearLineOp(5) }); // n > count

        Assert.Empty(adapter.Lines);
    }

    // ---------- T-ops-clear：清空全部 ----------

    [Fact]
    public void T_ops_clear_empties_lines_and_resets_bgcolor()
    {
        var adapter = new TestAdapter();
        // 通过 snapshot 设置初始状态（含 bgColor）
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>
            {
                new ConsoleDisplayLine(
                    new[] { TestButtonFactory.CreateNonButton("data") },
                    isLogical: true, temporary: false),
            },
            EmuColor.Red,
            ConsoleState.WaitInput,
            currentRequest: null, "TestFont");
        adapter.ApplySnapshot(snapshot);
        Assert.Single(adapter.Lines);
        Assert.Equal("#FF0000", adapter.BgColor);

        adapter.ApplyOps(new List<TurnOp> { new ClearOp() });

        Assert.Empty(adapter.Lines);
        Assert.Null(adapter.BgColor);
    }

    // ---------- T-ops-setbg：更新背景色 ----------

    [Fact]
    public void T_ops_setbg_updates_bgcolor()
    {
        var adapter = new TestAdapter();

        adapter.ApplyOps(new List<TurnOp> { new SetBgOp("#FF0000") });

        Assert.Equal("#FF0000", adapter.BgColor);

        adapter.ApplyOps(new List<TurnOp> { new SetBgOp("#00FF00") });

        Assert.Equal("#00FF00", adapter.BgColor);
    }

    // ---------- T-ops-geometry：按钮几何记录 ----------

    [Fact]
    public void T_ops_geometry_records_button_col_and_width()
    {
        var adapter = new TestAdapter();

        // 显式构造 col=5, width=4 的按钮（非 0 起点——验证几何透传，不重算）
        var segments = new List<PrintSegment> { new("btn", null, null, null, null) };
        var button = new ButtonRef(value: "click", isInteger: false, generation: 0, col: 5, width: 4);

        adapter.ApplyOps(new List<TurnOp>
        {
            new PrintOp(segments, button),
            new NewLineOp(null),
        });

        var entry = adapter.Lines[0].Entries[0];
        Assert.NotNull(entry.Button);
        Assert.Equal(5, entry.Button!.col);
        Assert.Equal(4, entry.Button.width);
        Assert.Equal("click", entry.Button.value);
        Assert.False(entry.Button.isInteger);
    }

    // ---------- T-full-roundtrip：快照 + 增量 ----------

    [Fact]
    public void T_full_roundtrip_snapshot_then_ops_yields_correct_final_state()
    {
        // 初始快照：1 行 "[Start]" 按钮
        var initialList = new List<ConsoleDisplayLine>
        {
            new ConsoleDisplayLine(
                new[] { TestButtonFactory.CreateButton("[Start]", input: 1) },
                isLogical: true, temporary: false),
        };
        var snapshot = DisplayState.BuildSnapshot(
            initialList, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        Assert.Single(adapter.Lines);
        Assert.Equal("[Start]", adapter.Lines[0].Entries[0].Segments[0].text);

        // 增量 ops：清屏 + 新按钮 + 换背景
        var newSegments = new List<PrintSegment> { new("[Next]", null, null, null, null) };
        var newButton = new ButtonRef(value: 2L, isInteger: true, generation: 0, col: 0, width: 6);
        adapter.ApplyOps(new List<TurnOp>
        {
            new ClearOp(),
            new SetBgOp("#0000FF"),
            new PrintOp(newSegments, newButton),
            new NewLineOp(null),
        });

        // 最终状态：1 行（清屏后新加的），"[Next]" 按钮，蓝色背景
        Assert.Single(adapter.Lines);
        Assert.Equal("[Next]", adapter.Lines[0].Entries[0].Segments[0].text);
        Assert.Equal(0, adapter.Lines[0].Entries[0].Button!.col);
        Assert.Equal(6, adapter.Lines[0].Entries[0].Button!.width);
        Assert.Equal("#0000FF", adapter.BgColor);
    }

    [Fact]
    public void T_full_roundtrip_preserves_awaiting_from_snapshot()
    {
        var req = new InputRequest { InputType = InputType.IntValue };
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(),
            EmuColor.Black,
            ConsoleState.WaitInput,
            req,
            "TestFont");

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        Assert.Equal("IntValue", adapter.Awaiting!.input!.type);

        // 增量 ops 不应改变 awaiting（它由 TurnRecord 的顶层字段携带，不是 ops）
        adapter.ApplyOps(new List<TurnOp>
        {
            new PrintOp(new List<PrintSegment> { new("x", null, null, null, null) }, null),
            new NewLineOp(null),
        });

        Assert.Equal("IntValue", adapter.Awaiting!.input!.type);
    }

    // ---------- T-multi-button-line：多按钮 col 累加 ----------

    [Fact]
    public void T_multi_button_line_cols_accumulate_by_preceding_widths()
    {
        // 构造一行 4 按钮："[0]"(3) + "[1]"(3) + "[2]"(3) + "[Cancel]"(8)
        var buttons = TestButtonFactory.CreateButtons(
            ("[0]", 0), ("[1]", 1), ("[2]", 2), ("[Cancel]", 3));
        var displayLineList = new List<ConsoleDisplayLine>
        {
            new ConsoleDisplayLine(buttons, isLogical: true, temporary: false),
        };

        var snapshot = DisplayState.BuildSnapshot(
            displayLineList, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        var entries = adapter.Lines[0].Entries;
        Assert.Equal(4, entries.Count);

        Assert.Equal(0, entries[0].Button!.col);   // "[0]" width=3
        Assert.Equal(3, entries[0].Button!.width);

        Assert.Equal(3, entries[1].Button!.col);   // 累加 3
        Assert.Equal(3, entries[1].Button!.width);

        Assert.Equal(6, entries[2].Button!.col);   // 累加 3+3=6
        Assert.Equal(3, entries[2].Button!.width);

        Assert.Equal(9, entries[3].Button!.col);   // 累加 3+3+3=9
        Assert.Equal(8, entries[3].Button!.width); // "[Cancel]" width=8
    }

    // ---------- 额外：连续 PrintOp 同行不换行 ----------

    [Fact]
    public void Consecutive_print_ops_without_newline_stay_on_same_line()
    {
        var adapter = new TestAdapter();

        adapter.ApplyOps(new List<TurnOp>
        {
            new PrintOp(new List<PrintSegment> { new("Hello", null, null, null, null) }, null),
            new PrintOp(new List<PrintSegment> { new(" ", null, null, null, null) }, null),
            new PrintOp(new List<PrintSegment> { new("World", null, null, null, null) }, null),
            new NewLineOp(null),
        });

        Assert.Single(adapter.Lines);
        Assert.Equal(3, adapter.Lines[0].Entries.Count);
        Assert.True(adapter.Lines[0].IsLineEnd);
    }

    // ---------- 额外：空 ops 不改变状态 ----------

    [Fact]
    public void Empty_ops_leaves_awaiting_unchanged()
    {
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>
            {
                new ConsoleDisplayLine(
                    new[] { TestButtonFactory.CreateButton("[OK]", 1) },
                    isLogical: true, temporary: false),
            },
            EmuColor.Red,
            ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        adapter.ApplyOps(new List<TurnOp>());

        Assert.Single(adapter.Lines);
        Assert.Equal("#FF0000", adapter.BgColor);
        Assert.NotNull(adapter.Awaiting);
    }

    // ---------- plan C：ApplyDiff 消费 DisplayDiff（显式清空信号）----------

    private static DisplayLine MakeDiffLine(string text) =>
        new(new List<DisplayEntry>
        {
            new(new List<PrintSegment> { new(text, null, null, null, null) }, null),
        }, "left", isLineEnd: true);

    [Fact]
    public void T_diff_append_adds_lines()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("a"), MakeDiffLine("b") }) },
            null));

        Assert.Equal(2, adapter.Lines.Count);
        Assert.Equal("a", adapter.Lines[0].Entries[0].Segments[0].text);
        Assert.Equal("b", adapter.Lines[1].Entries[0].Segments[0].text);
    }

    [Fact]
    public void T_diff_clearline_removes_last_n_lines()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("a"), MakeDiffLine("b"), MakeDiffLine("c") }) },
            null));

        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new ClearLineDiffOp(2) },
            null));

        Assert.Single(adapter.Lines);
        Assert.Equal("a", adapter.Lines[0].Entries[0].Segments[0].text);
    }

    [Fact]
    public void T_diff_clear_empties_lines_and_sets_bg()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("data") }) },
            null));

        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new ClearScreenOp() },
            "#FF0000"));

        Assert.Empty(adapter.Lines);
        Assert.Equal("#FF0000", adapter.BgColor);
    }

    [Fact]
    public void T_diff_clearscreen_then_append_rebuilds_state()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("old") }) },
            null));

        // CLEAR + 重印新行：等价于引擎 CLEAR 后打印
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp>
            {
                new ClearScreenOp(),
                new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("new") }),
            },
            null));

        Assert.Single(adapter.Lines);
        Assert.Equal("new", adapter.Lines[0].Entries[0].Segments[0].text);
    }

    [Fact]
    public void T_diff_setbg_only_updates_bgcolor()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("keep") }) },
            "#00FF00"));

        adapter.ApplyDiff(new DisplayDiff(new List<LineOp>(), "#0000FF"));

        Assert.Single(adapter.Lines);
        Assert.Equal("keep", adapter.Lines[0].Entries[0].Segments[0].text);
        Assert.Equal("#0000FF", adapter.BgColor);
    }

    // ---------- ADR-0016：TINPUT 计时元数据经 awaiting.wait 保留 ----------

    [Fact]
    public void T_snapshot_tinput_populates_awaiting_wait()
    {
        // TINPUT 期间：Timelimit>0 + DisplayTime=true + 非空 TimeUpMes → wait 带全量元数据
        var req = new InputRequest
        {
            InputType = InputType.EnterKey,
            Timelimit = 5000,
            DisplayTime = true,
            TimeUpMes = "时间到",
        };
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(),
            EmuColor.Black,
            ConsoleState.WaitInput,
            req,
            "TestFont");

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        var wait = adapter.Awaiting!.wait!;
        Assert.Equal(5000L, wait.durationMs);
        Assert.True(wait.displayTime);
        Assert.Equal("时间到", wait.timeUpMessage);
    }

    [Fact]
    public void T_snapshot_non_tinput_has_no_wait()
    {
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(),
            EmuColor.Black,
            ConsoleState.WaitInput,
            currentRequest: null,
            "TestFont");

        var adapter = new TestAdapter();
        adapter.ApplySnapshot(snapshot);

        Assert.Null(adapter.Awaiting!.wait);
    }

    // ---------- shift_head：TestAdapter 消费方对称测试 ----------
    //
    // 与前端 opsApplier.test.ts 的 shift_head 系列对称——验证 C# 端 TestAdapter.ApplyDiff
    // 能正确消费 ShiftHeadLineOp（与前端 applyDiff 行为一致）。

    [Fact]
    public void T_diff_shift_head_removes_first_n_lines()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("a"), MakeDiffLine("b"), MakeDiffLine("c") }) },
            null));
        Assert.Equal(3, adapter.Lines.Count);

        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new ShiftHeadLineOp(1) },
            null));

        Assert.Equal(2, adapter.Lines.Count);
        // 头部删除——剩下 b, c
        Assert.Equal("b", adapter.Lines[0].Entries[0].Segments[0].text);
        Assert.Equal("c", adapter.Lines[1].Entries[0].Segments[0].text);
    }

    [Fact]
    public void T_diff_shift_head_count_exceeding_lines_clamps_to_count()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("only") }) },
            null));

        // count > Lines.Count —— 钳制为 Lines.Count，不抛错
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new ShiftHeadLineOp(5) },
            null));

        Assert.Empty(adapter.Lines);
    }

    [Fact]
    public void T_diff_shift_head_then_append_applies_in_order()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("a"), MakeDiffLine("b") }) },
            null));

        // shift_head(1) + append(1) —— 先删头部 a，再追加 c → [b, c]
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp>
            {
                new ShiftHeadLineOp(1),
                new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("c") }),
            },
            null));

        Assert.Equal(2, adapter.Lines.Count);
        Assert.Equal("b", adapter.Lines[0].Entries[0].Segments[0].text);
        Assert.Equal("c", adapter.Lines[1].Entries[0].Segments[0].text);
    }

    [Fact]
    public void T_diff_shift_head_with_clear_line_diff_applies_both()
    {
        var adapter = new TestAdapter();
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp> { new AppendLinesOp(new List<DisplayLine> { MakeDiffLine("a"), MakeDiffLine("b"), MakeDiffLine("c"), MakeDiffLine("d") }) },
            null));

        // shift_head(1) + clear_line_diff(1) —— 先删头部 a，再删尾部 d → [b, c]
        adapter.ApplyDiff(new DisplayDiff(
            new List<LineOp>
            {
                new ShiftHeadLineOp(1),
                new ClearLineDiffOp(1),
            },
            null));

        Assert.Equal(2, adapter.Lines.Count);
        Assert.Equal("b", adapter.Lines[0].Entries[0].Segments[0].text);
        Assert.Equal("c", adapter.Lines[1].Entries[0].Segments[0].text);
    }
}
