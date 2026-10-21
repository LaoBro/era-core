using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// DisplayState 全量快照序列化单测（ADR-0013 决策二）。
/// 验证 BuildSnapshot 从 displayLineList → DisplaySnapshot 的映射：
/// - 行数一致
/// - 每行 entries[] / segments[] / button（含 col/width）/ align / isLineEnd 正确
/// - bgColor / awaiting / protocolVersion 字段正确
///
/// 复用 TestButtonFactory（null console → Generation=0），不依赖真实 EmueraConsole/Config。
/// 通过 internal static DisplayState.BuildSnapshot 直测——无需构造完整 console。
/// </summary>
public class DisplayStateTests
{
    private const int ExpectedProtocolVersion = 12; // v12：awaiting 容器

    /// <summary>
    /// 通过反射直接设置 ConsoleDisplayLine 的私有 align 字段，绕过 SetAlignment 对 Config.DrawableWidth 的依赖。
    /// 测试仅验证 DisplayState.AlignToString 的映射，不测试 SetAlignment 的位移逻辑。
    /// </summary>
    private static void SetAlign(ConsoleDisplayLine line, DisplayLineAlignment align)
    {
        var field = typeof(ConsoleDisplayLine).GetField("align",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(line, align);
    }

    // ---------- 空 displayLineList ----------

    [Fact]
    public void Empty_displayLineList_produces_empty_lines()
    {
        var snapshot = DisplayState.BuildSnapshot(
            displayLineList: new List<ConsoleDisplayLine>(),
            bgColor: EmuColor.Black,
            state: ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Empty(snapshot.lines);
        Assert.Equal("#000000", snapshot.bgColor);
        // v12：req=null 且非终态 ⇒ 两个正交维度都无值（`{}`）。快照可在「脚本还在跑」的
        // 瞬态被取到，故这不是契约违规（§1.2 规则 1 只约束停稳点上的流帧）。
        Assert.Null(snapshot.awaiting.input);
        Assert.Null(snapshot.awaiting.wait);
        Assert.Null(snapshot.awaiting.ended);
        Assert.Equal(ExpectedProtocolVersion, snapshot.protocolVersion);
    }

    // ---------- 单行单按钮 ----------

    [Fact]
    public void Single_line_single_button_snapshots_correctly()
    {
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Single(snapshot.lines);
        var snapLine = snapshot.lines[0];
        Assert.Single(snapLine.entries);
        // ConsoleDisplayLine 默认 align=LEFT(0)，AlignToString 映射到 "left"
        Assert.Equal("left", snapLine.align);
        Assert.True(snapLine.isLineEnd);

        var entry = snapLine.entries[0];
        Assert.Single(entry.segments);
        Assert.Equal("[OK]", entry.segments[0].text);
        Assert.NotNull(entry.button);
        Assert.Equal(0, entry.button!.col);
        Assert.Equal(4, entry.button!.width);
        Assert.Equal(1L, (long)entry.button.value);
        Assert.True(entry.button.isInteger);
    }

    // ---------- 多行多按钮（几何累加） ----------

    [Fact]
    public void Multi_line_count_matches_displayLineList_count()
    {
        var line1 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1)), isLogical: true, temporary: false);
        var line2 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[Cancel]", 2), ("[Exit]", 3)), isLogical: true, temporary: false);
        var line3 = new ConsoleDisplayLine(
            Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line1, line2, line3 };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Equal(3, snapshot.lines.Count);
        Assert.Single(snapshot.lines[0].entries);
        Assert.Equal(2, snapshot.lines[1].entries.Count);
        Assert.Empty(snapshot.lines[2].entries);
    }

    [Fact]
    public void Multi_button_geometry_accumulates_within_line()
    {
        // "[OK]"(width=4) + "[Cancel]"(width=8) + "[Exit]"(width=6)
        var buttons = TestButtonFactory.CreateButtons(("[OK]", 1), ("[Cancel]", 2), ("[Exit]", 3));
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entries = snapshot.lines[0].entries;
        Assert.Equal(3, entries.Count);

        Assert.Equal(0, entries[0].button!.col);
        Assert.Equal(4, entries[0].button!.width);

        Assert.Equal(4, entries[1].button!.col);
        Assert.Equal(8, entries[1].button!.width);

        Assert.Equal(12, entries[2].button!.col); // 4 + 8
        Assert.Equal(6, entries[2].button!.width);
    }

    // ---------- CJK 双宽字符按钮 ----------

    [Fact]
    public void Cjk_double_width_button_in_snapshot()
    {
        // "確定" = 2 CJK 字符，width=4
        var buttons = new[] { TestButtonFactory.CreateButton("確定", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entry = snapshot.lines[0].entries[0];
        Assert.Equal(0, entry.button!.col);
        Assert.Equal(4, entry.button!.width);
        Assert.Equal("確定", entry.segments[0].text);
    }

    [Fact]
    public void Cjk_then_ascii_buttons_geometry_accumulates_in_snapshot()
    {
        // "確定"(width=4) + "[OK]"(width=4)
        var buttons = TestButtonFactory.CreateButtons(("確定", 1), ("[OK]", 2));
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entries = snapshot.lines[0].entries;
        Assert.Equal(0, entries[0].button!.col);
        Assert.Equal(4, entries[0].button!.width);
        Assert.Equal(4, entries[1].button!.col); // 累加 CJK 宽度
        Assert.Equal(4, entries[1].button!.width);
    }

    // ---------- 非按钮文本段（ButtonRef=null） ----------

    [Fact]
    public void Non_button_text_segment_has_null_button()
    {
        var nonButton = TestButtonFactory.CreateNonButton("plain text");
        var line = new ConsoleDisplayLine(new[] { nonButton }, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entry = snapshot.lines[0].entries[0];
        Assert.Null(entry.button);
        Assert.Equal("plain text", entry.segments[0].text);
    }

    [Fact]
    public void Mixed_button_and_non_button_geometry_accumulates_through_non_button_in_snapshot()
    {
        // [OK](button, width=4) + plain(non-button, width=10) + [Cancel](button, width=8)
        var buttons = new ConsoleButtonString[]
        {
            TestButtonFactory.CreateButton("[OK]", 1),
            TestButtonFactory.CreateNonButton("plain text"),
            TestButtonFactory.CreateButton("[Cancel]", 2),
        };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var entries = snapshot.lines[0].entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal(0, entries[0].button!.col);
        Assert.Equal(4, entries[0].button!.width);
        Assert.Null(entries[1].button);
        Assert.Equal(14, entries[2].button!.col); // 4 + 10
        Assert.Equal(8, entries[2].button!.width);
    }

    // ---------- PRINTN 不完整行（isLineEnd=false） ----------

    [Fact]
    public void Printn_incomplete_line_isLineEnd_false_preserved_in_snapshot()
    {
        // PRINTN 输出后行未结束——IsLineEnd=false
        var buttons = new[] { TestButtonFactory.CreateButton("continuing", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false, lineEnd: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.False(snapshot.lines[0].isLineEnd);
    }

    [Fact]
    public void Printl_complete_line_isLineEnd_true_preserved_in_snapshot()
    {
        var buttons = new[] { TestButtonFactory.CreateButton("[OK]", input: 1) };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false, lineEnd: true);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.True(snapshot.lines[0].isLineEnd);
    }

    // ---------- 对齐（LEFT/CENTER/RIGHT） ----------

    [Fact]
    public void Alignment_left_center_right_mapped_correctly()
    {
        var leftButtons = new[] { TestButtonFactory.CreateNonButton("left") };
        var centerButtons = new[] { TestButtonFactory.CreateNonButton("center") };
        var rightButtons = new[] { TestButtonFactory.CreateNonButton("right") };

        var leftLine = new ConsoleDisplayLine(leftButtons, isLogical: true, temporary: false);
        var centerLine = new ConsoleDisplayLine(centerButtons, isLogical: true, temporary: false);
        var rightLine = new ConsoleDisplayLine(rightButtons, isLogical: true, temporary: false);
        // 默认 align=LEFT(0)；CENTER/RIGHT 通过反射直接设置私有字段，绕过 SetAlignment 的 Config 依赖
        SetAlign(centerLine, DisplayLineAlignment.CENTER);
        SetAlign(rightLine, DisplayLineAlignment.RIGHT);

        var list = new List<ConsoleDisplayLine> { leftLine, centerLine, rightLine };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Equal("left", snapshot.lines[0].align);
        Assert.Equal("center", snapshot.lines[1].align);
        Assert.Equal("right", snapshot.lines[2].align);
    }

    // ---------- bgColor / state / inputType / needValue / protocolVersion ----------

    [Fact]
    public void BgColor_serialized_as_hex_string()
    {
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(),
            bgColor: EmuColor.FromArgb(0, 0, 139), // DarkBlue
            state: ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Equal("#00008B", snapshot.bgColor);
    }

    [Fact]
    public void Terminal_states_serialize_as_awaiting_ended()
    {
        foreach (ConsoleState s in new[] { ConsoleState.Quit, ConsoleState.Error })
        {
            var snapshot = DisplayState.BuildSnapshot(
                new List<ConsoleDisplayLine>(), EmuColor.Black, s, currentRequest: null, "TestFont");
            // 终态键与两个正交维度互斥（§1.2 规则 1）。
            Assert.Equal(s.ToString(), snapshot.awaiting.ended!.terminal);
            Assert.Null(snapshot.awaiting.input);
            Assert.Null(snapshot.awaiting.wait);
        }
    }

    [Fact]
    public void CurrentRequest_intValue_provides_awaiting_input()
    {
        var req = new InputRequest { InputType = InputType.IntValue };

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        // v12：顶层 inputType 退场——`needValue` 改由客户端按 type 推出（IntValue 属值输入）。
        Assert.Equal("IntValue", snapshot.awaiting.input!.type);
    }

    [Fact]
    public void CurrentRequest_enterKey_provides_awaiting_input()
    {
        var req = new InputRequest { InputType = InputType.EnterKey };

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        Assert.Equal("EnterKey", snapshot.awaiting.input!.type);
    }

    [Fact]
    public void Null_currentRequest_leaves_awaiting_empty()
    {
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Null(snapshot.awaiting.input);
        Assert.Null(snapshot.awaiting.wait);
        Assert.Null(snapshot.awaiting.ended);
    }

    [Fact]
    public void ProtocolVersion_is_twelve()
    {
        // v12：awaiting 容器（input / wait 两个正交维度 + ended 互斥终态）
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Equal(12, snapshot.protocolVersion);
    }

    // ---------- ADR-0016：TINPUT timer 元数据填充 ----------

    [Fact]
    public void BuildSnapshot_tinput_request_populates_awaiting_wait()
    {
        // Timelimit>0 + DisplayTime=true + 非空 TimeUpMes → wait 携带全量计时元数据
        var req = new InputRequest
        {
            InputType = InputType.EnterKey,
            Timelimit = 5000,
            DisplayTime = true,
            TimeUpMes = "时间到",
        };

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        var wait = snapshot.awaiting.wait!;
        Assert.Equal(5000L, wait.durationMs);
        Assert.True(wait.displayTime);
        Assert.Equal("时间到", wait.timeUpMessage);
    }

    [Fact]
    public void BuildSnapshot_non_tinput_request_has_no_wait()
    {
        // currentRequest=null → wait 键缺省（= 无限期），两个维度皆无
        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Null(snapshot.awaiting.wait);
    }

    [Fact]
    public void BuildSnapshot_negative_timelimit_has_no_wait()
    {
        // Timelimit=-1（InputRequest 默认值，等同于"无 TINPUT"）→ wait 键缺省
        var req = new InputRequest { InputType = InputType.EnterKey };

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        Assert.Null(snapshot.awaiting.wait);
        Assert.Equal("EnterKey", snapshot.awaiting.input!.type);
    }

    [Fact]
    public void BuildSnapshot_tinput_without_displaytime_leaves_displaytime_absent()
    {
        // Timelimit>0 + DisplayTime=false → displayTime 键缺省（ERB 要求前端不显示倒计时）
        // durationMs 仍填，timeUpMes 空 → timeUpMessage 键缺省
        var req = new InputRequest
        {
            InputType = InputType.EnterKey,
            Timelimit = 3000,
            DisplayTime = false,
            TimeUpMes = "",
        };

        var snapshot = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(), EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        var wait = snapshot.awaiting.wait!;
        Assert.Equal(3000L, wait.durationMs);
        Assert.Null(wait.displayTime);
        Assert.Null(wait.timeUpMessage);
    }

    // ---------- Phase 0-2：快照确定性 / 等价性 ----------
    // 相同输入 → BuildSnapshot 两次产出 byte-identical JSON（幂等）；
    // 不同输入 → 不同 JSON（区分度）。
    // 这是 Phase 2 ComputeDiff「不同快照不应产生空 diff」的前置（等价于「快照不等价 ⟺ JSON 不同」）。

    /// <summary>
    /// 与生产 DisplayState.JsonOpts 等价（WhenWritingNull）。测试内重建以避免修改生产可见性。
    /// </summary>
    private static readonly JsonSerializerOptions EquivalenceJsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void BuildSnapshot_is_idempotent_same_input_produces_byte_identical_json()
    {
        var buttons = new[]
        {
            TestButtonFactory.CreateButton("[OK]", input: 1),
            TestButtonFactory.CreateNonButton("plain"),
        };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };
        var req = new InputRequest { InputType = InputType.IntValue };

        var snap1 = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");
        var snap2 = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, req, "TestFont");

        var json1 = JsonSerializer.Serialize(snap1, EquivalenceJsonOpts);
        var json2 = JsonSerializer.Serialize(snap2, EquivalenceJsonOpts);
        Assert.Equal(json1, json2);
    }

    [Fact]
    public void BuildSnapshot_different_input_produces_different_json()
    {
        var lineA = new ConsoleDisplayLine(
            new[] { TestButtonFactory.CreateButton("[A]", 1) }, isLogical: true, temporary: false);
        var lineB = new ConsoleDisplayLine(
            new[] { TestButtonFactory.CreateButton("[B]", 2) }, isLogical: true, temporary: false);

        var snapA = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { lineA },
            EmuColor.Black, ConsoleState.WaitInput, null, "TestFont");
        var snapB = DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine> { lineB },
            EmuColor.Black, ConsoleState.WaitInput, null, "TestFont");

        var jsonA = JsonSerializer.Serialize(snapA, EquivalenceJsonOpts);
        var jsonB = JsonSerializer.Serialize(snapB, EquivalenceJsonOpts);
        Assert.NotEqual(jsonA, jsonB);
    }

    // ---------- Phase 3-2：Web 几何完备性断言 ----------
    // 给定多行多按钮快照（含 CJK 双宽 + 非按钮文本 + 混合），
    // 仅凭 lines[i].entries[j].button 的 col/width 与数组位置即可唯一定位每个按钮。
    // 证明 ADR-0013 几何已满足 Web 需求，Phase 3 无新字段要加。

    /// <summary>
    /// 模拟 Web 前端从快照重建按钮命中区：遍历 lines[i].entries[j]，
    /// 对 button 非 null 的 entry 构造 (lineIndex, col, col+width-1) 命中区。
    /// </summary>
    private static List<(int lineIndex, int entryIndex, int col, int right, object value, bool isInteger)>
        CollectButtonRegions(DisplaySnapshot snapshot)
    {
        var regions = new List<(int, int, int, int, object, bool)>();
        for (int i = 0; i < snapshot.lines.Count; i++)
        {
            var entries = snapshot.lines[i].entries;
            for (int j = 0; j < entries.Count; j++)
            {
                var btn = entries[j].button;
                if (btn != null && btn.col is { } c && btn.width is { } w)
                    regions.Add((i, j, c, c + w - 1, btn.value, btn.isInteger));
            }
        }
        return regions;
    }

    [Fact]
    public void Phase3_2_web_geometry_multi_line_multi_button_unique_regions()
    {
        // Line 0: "[OK]"(w=4) + "確定"(CJK w=4) + "[Exit]"(w=6) → 3 buttons
        // Line 1: "plain"(non-button w=5) + "[Cancel]"(w=8) → 1 button + 1 non-button
        var line0 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1), ("確定", 2), ("[Exit]", 3)),
            isLogical: true, temporary: false);
        var line1 = new ConsoleDisplayLine(
            new[] {
                TestButtonFactory.CreateNonButton("plain"),
                TestButtonFactory.CreateButton("[Cancel]", 4),
            },
            isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line0, line1 };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var regions = CollectButtonRegions(snapshot);

        // 4 个按钮命中区（line0 3 个 + line1 1 个）
        Assert.Equal(4, regions.Count);

        // Line 0 几何累加：[OK]@0-3, 確定@4-7, [Exit]@8-13（CJK 双宽正确累加）
        Assert.Equal((0, 0, 0, 3, 1L, true), regions[0]);
        Assert.Equal((0, 1, 4, 7, 2L, true), regions[1]);
        Assert.Equal((0, 2, 8, 13, 3L, true), regions[2]);

        // Line 1：非按钮 "plain"(w=5) 累加后，[Cancel] 从 col=5 开始 → 5-12
        Assert.Equal((1, 1, 5, 12, 4L, true), regions[3]);

        // 唯一性：同一行内无两个按钮的 (col, right) 重叠
        var line0Ranges = regions.Where(r => r.lineIndex == 0).Select(r => (r.col, r.right)).ToList();
        for (int a = 0; a < line0Ranges.Count; a++)
            for (int b = a + 1; b < line0Ranges.Count; b++)
                Assert.True(line0Ranges[a].right < line0Ranges[b].col || line0Ranges[b].right < line0Ranges[a].col,
                    $"Line 0 regions {a} and {b} overlap");
    }

    [Fact]
    public void Phase3_2_web_geometry_cjk_then_ascii_accumulates_correctly()
    {
        // "確定"(CJK w=4) + "[OK]"(ASCII w=4) → CJK 双宽累加到下一按钮起点
        var line = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("確定", 100), ("[OK]", 200)),
            isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var regions = CollectButtonRegions(snapshot);

        Assert.Equal(2, regions.Count);
        // 確定: col=0, right=3
        Assert.Equal(0, regions[0].col);
        Assert.Equal(3, regions[0].right);
        Assert.Equal(100L, regions[0].value);
        // [OK]: col=4（CJK 双宽累加后），right=7
        Assert.Equal(4, regions[1].col);
        Assert.Equal(7, regions[1].right);
        Assert.Equal(200L, regions[1].value);
    }

    [Fact]
    public void Phase3_2_web_geometry_non_button_does_not_create_region_but_accumulates_col()
    {
        // [A](button w=3) + middle(non-button w=6) + [B](button w=3)
        // → 2 regions; [B].col = 3 + 6 = 9（非按钮仍参与列累加）
        var buttons = new ConsoleButtonString[]
        {
            TestButtonFactory.CreateButton("[A]", 1),
            TestButtonFactory.CreateNonButton("middle"),
            TestButtonFactory.CreateButton("[B]", 2),
        };
        var line = new ConsoleDisplayLine(buttons, isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var regions = CollectButtonRegions(snapshot);

        // 仅 2 个按钮命中区（非按钮不产生 region）
        Assert.Equal(2, regions.Count);
        Assert.Equal((0, 0, 0, 2, 1L, true), regions[0]);
        // [B] 在 entryIndex=2（跳过非按钮 entryIndex=1），col=9（3+6 累加）
        Assert.Equal((0, 2, 9, 11, 2L, true), regions[1]);
    }

    [Fact]
    public void Phase3_2_web_geometry_empty_lines_and_buttonless_lines_skipped()
    {
        // Line 0: 按钮行；Line 1: 空行；Line 2: 纯文本（无按钮）行
        var line0 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1)), isLogical: true, temporary: false);
        var line1 = new ConsoleDisplayLine(
            Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false);
        var line2 = new ConsoleDisplayLine(
            new[] { TestButtonFactory.CreateNonButton("no buttons here") },
            isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line0, line1, line2 };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var regions = CollectButtonRegions(snapshot);

        // 仅 Line 0 有 1 个按钮命中区
        Assert.Single(regions);
        Assert.Equal(0, regions[0].lineIndex);
        Assert.Equal(0, regions[0].col);
        Assert.Equal(3, regions[0].right);
    }

    [Fact]
    public void Phase3_2_web_geometry_integer_button_value_and_is_integer_preserved()
    {
        // 验证整数按钮的 value/isInteger 正确传入快照（value-based 提交的基础）
        var line = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 42), ("[Cancel]", 99)),
            isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var snapshot = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        var regions = CollectButtonRegions(snapshot);

        Assert.Equal(2, regions.Count);
        Assert.Equal(42L, regions[0].value);
        Assert.True(regions[0].isInteger);
        Assert.Equal(99L, regions[1].value);
        Assert.True(regions[1].isInteger);
    }

    // ---------- 3.3 增量快照：引用缓存 ----------

    [Fact]
    public void Incremental_cache_reuses_display_line_across_rebuilds()
    {
        // 同引用两回合：DisplayLine 对象整体复用（含 segments/CLI 字段），不重复 BuildPrintOpsForLine
        var cache = new Dictionary<ConsoleDisplayLine, DisplayLine>();
        var line = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1), ("[Exit]", 2)),
            isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };

        var first = DisplayState.BuildSnapshotIncremental(
            cache, list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");
        var second = DisplayState.BuildSnapshotIncremental(
            cache, list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");

        Assert.Single(cache);
        Assert.Same(first.lines[0], second.lines[0]);
        Assert.Same(first.lines[0].entries[0], second.lines[0].entries[0]);
        Assert.Same(line, second.lines[0].SourceLine);
        Assert.Equal(first.lines[0].LineNo, second.lines[0].LineNo);
        Assert.Equal(first.lines[0].AlignOffset, second.lines[0].AlignOffset);
    }

    [Fact]
    public void Incremental_cache_builds_only_new_lines()
    {
        // 旧行命中缓存，新行 miss 构建并入缓存
        var cache = new Dictionary<ConsoleDisplayLine, DisplayLine>();
        var line1 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1)), isLogical: true, temporary: false);
        DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { line1 }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        var line2 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[Cancel]", 2)), isLogical: true, temporary: false);
        var second = DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { line1, line2 }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Equal(2, cache.Count);
        Assert.Same(cache[line1], second.lines[0]);
        Assert.Same(cache[line2], second.lines[1]);
        Assert.Same(line2, second.lines[1].SourceLine);
    }

    [Fact]
    public void Incremental_cache_tail_removal_keeps_result_correct()
    {
        // CLEARLINE：尾部行被删除——保留行命中，被删行条目成孤儿（由 Rebuild 兜底清理，不影响正确性）
        var cache = new Dictionary<ConsoleDisplayLine, DisplayLine>();
        var line1 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1)), isLogical: true, temporary: false);
        var line2 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[Exit]", 2)), isLogical: true, temporary: false);
        DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { line1, line2 }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        var second = DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { line1 }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Single(second.lines);
        Assert.Same(cache[line1], second.lines[0]);
        Assert.True(cache.ContainsKey(line2));
    }

    [Fact]
    public void Incremental_cache_head_removal_keeps_result_correct()
    {
        // MaxLog ShiftHead：头部行被移除——保留行按引用命中（与位置无关）
        var cache = new Dictionary<ConsoleDisplayLine, DisplayLine>();
        var line1 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[A]", 1)), isLogical: true, temporary: false);
        var line2 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[B]", 2)), isLogical: true, temporary: false);
        DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { line1, line2 }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        var second = DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { line2 }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Single(second.lines);
        Assert.Same(cache[line2], second.lines[0]);
    }

    [Fact]
    public void Incremental_cache_last_line_edit_rebuilds_replaced_line()
    {
        // 末行编辑（PRINTN 续行）：旧末行对象被删，新合并行对象加入 → 新对象 miss 重建，旧条目成孤儿
        var cache = new Dictionary<ConsoleDisplayLine, DisplayLine>();
        var oldLast = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[A]", 1)), isLogical: true, temporary: false, lineEnd: false);
        DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { oldLast }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        var merged = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[A]", 1), ("[B]", 2)), isLogical: true, temporary: false);
        var second = DisplayState.BuildSnapshotIncremental(
            cache, new List<ConsoleDisplayLine> { merged }, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Single(second.lines);
        Assert.NotSame(oldLast, second.lines[0].SourceLine);
        Assert.Same(merged, second.lines[0].SourceLine);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Incremental_snapshot_values_equal_full_rebuild()
    {
        // 值相等性：同一列表经空缓存（全量重建）与经缓存（增量）产物逐字段一致——diff/协议零改动的保证
        var line0 = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1), ("確定", 2)), isLogical: true, temporary: false);
        var line1 = new ConsoleDisplayLine(
            new[] { TestButtonFactory.CreateNonButton("plain") }, isLogical: true, temporary: false);
        var line2 = new ConsoleDisplayLine(
            Array.Empty<ConsoleButtonString>(), isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line0, line1, line2 };

        var full = DisplayState.BuildSnapshot(
            list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");
        var incr = DisplayState.BuildSnapshotIncremental(
            new Dictionary<ConsoleDisplayLine, DisplayLine>(), list, EmuColor.Black, ConsoleState.WaitInput,
            currentRequest: null, "TestFont");

        Assert.Equal(full.lines.Count, incr.lines.Count);
        for (int i = 0; i < full.lines.Count; i++)
        {
            Assert.Equal(full.lines[i].align, incr.lines[i].align);
            Assert.Equal(full.lines[i].isLineEnd, incr.lines[i].isLineEnd);
            Assert.Equal(full.lines[i].entries.Count, incr.lines[i].entries.Count);
            Assert.Equal(full.lines[i].LineNo, incr.lines[i].LineNo);
            for (int j = 0; j < full.lines[i].entries.Count; j++)
            {
                Assert.Equal(full.lines[i].entries[j].segments.Count, incr.lines[i].entries[j].segments.Count);
                for (int k = 0; k < full.lines[i].entries[j].segments.Count; k++)
                    Assert.Equal(full.lines[i].entries[j].segments[k], incr.lines[i].entries[j].segments[k]);
                Assert.Equal(full.lines[i].entries[j].button, incr.lines[i].entries[j].button);
            }
        }
    }

    [Fact]
    public void CommonPrefix_short_circuits_on_reference_equality()
    {
        // 引用短路：相同 DisplayLine 引用（缓存命中）直接判等，跳过深比较
        var cache = new Dictionary<ConsoleDisplayLine, DisplayLine>();
        var line = new ConsoleDisplayLine(
            TestButtonFactory.CreateButtons(("[OK]", 1)), isLogical: true, temporary: false);
        var list = new List<ConsoleDisplayLine> { line };
        var s1 = DisplayState.BuildSnapshotIncremental(
            cache, list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");
        var s2 = DisplayState.BuildSnapshotIncremental(
            cache, list, EmuColor.Black, ConsoleState.WaitInput, currentRequest: null, "TestFont");
        Assert.Same(s1.lines[0], s2.lines[0]);

        var method = typeof(DisplayState).GetMethod("CommonPrefix",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var k = (int)method!.Invoke(null, new object[] { s1.lines, s2.lines })!;
        Assert.Equal(1, k);
    }
}
