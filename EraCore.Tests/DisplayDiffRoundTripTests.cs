using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// <b>diff 往返性质测试</b>：把本帧 <c>diff.lineOps</c> 应用到<b>上一帧快照</b>，
/// 结果必须<b>逐行等于</b>本帧快照。
///
/// <para>为什么要有这一类测试：issue 13 的 wire 输出是「自洽地错」的 ——
/// <c>clear 11 + append 11</c>，净增 0、行数恒定、op 格式合法、无异常。
/// 所有「结构正确」的断言（行数不变 / 净增为 0 / diff 非空 / 不越界）在它身上<b>全部成立</b>，
/// 唯一能抓住它的是<b>具体数字</b>或<b>往返等价</b>。后者的好处是不必预先知道正确答案：</para>
///
/// <para><c>Apply(prev, ops) == current</c> 是协议层的<b>定义性质</b>——前端
/// （<c>opsApplier.ts</c> / <c>TestAdapter</c>）就是这么还原画面的。它一旦不成立，
/// 前端累积出的画面必然与权威快照分叉，不管每个 op 单独看多合理。</para>
///
/// <para>与 <see cref="DisplayDiffTests"/> 的分工：那边断言「op 序列长什么样」（要写死期望值）；
/// 这边断言「op 序列还原得出画面」（不需要期望值，通用于任意帧流）。</para>
///
/// <para><b>关键：喂的是真实引擎产出的 op</b> —— 清行走真的
/// <c>EmueraConsole.deleteLine</c>，<c>ClearLineOp</c> 由 <c>ConsolePrintManager</c> 生成。
/// 手写合成 op 的测试（<c>TestAdapterTests</c>）永远抓不到引擎算错数的缺陷。</para>
/// </summary>
public class DisplayDiffRoundTripTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _displayState;

    public DisplayDiffRoundTripTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        var ui = new HeadlessConsole();
        _console = new EmueraConsole(ui, new NullTerminalSetup());
        _displayState = new DisplayState(_console, "MS Gothic");
    }

    public void Dispose() => _scope.Dispose();

    private static ConsoleDisplayLine Line(string text, bool logical) =>
        new(new[] { TestButtonFactory.CreateNonButton(text) }, isLogical: logical, temporary: false);

    /// <summary>打印一个「1 逻辑行 / <c>rows</c> 显示行」的段落（<c>HTML_PRINT</c> 多行文本的落地形状）。</summary>
    private void PrintParagraph(string prefix, int rows)
    {
        for (int i = 0; i < rows; i++)
            _console.DisplayLineList.Add(Line($"{prefix}{i}", logical: i == 0));
    }

    /// <summary>打印 <c>count</c> 个 1:1 行。</summary>
    private void PrintRows(string prefix, int count)
    {
        for (int i = 0; i < count; i++)
            _console.DisplayLineList.Add(Line($"{prefix}{i}", logical: true));
    }

    private DisplayDiff? BuildDiff() => _displayState.ComputeDiff();

    // ------------------------------------------------------------------
    // 还原器：与前端 opsApplier 同语义（shift_head → clear → append，按序）
    // ------------------------------------------------------------------

    /// <summary>一行 = 该行全部 segment 文本拼接（够区分本文件用到的所有行）。</summary>
    private static string RowText(DisplayLine line)
        => string.Concat(line.entries.SelectMany(e => e.segments.Select(s => s.text)));

    /// <summary>把 lineOps 应用到一组行 —— 前端 applyDiff 的最小等价实现。</summary>
    private static List<string> ApplyLineOps(List<string> prev, List<LineOp> ops)
    {
        var lines = new List<string>(prev);
        foreach (var op in ops)
        {
            switch (op)
            {
                case ClearScreenOp:
                    lines.Clear();
                    break;
                case ShiftHeadLineOp shift:
                    lines.RemoveRange(0, Math.Min(shift.count, lines.Count));
                    break;
                case ClearLineDiffOp clear:
                    int n = Math.Min(clear.clearCount, lines.Count);
                    lines.RemoveRange(lines.Count - n, n);
                    break;
                case AppendLinesOp append:
                    lines.AddRange(append.newLines.Select(RowText));
                    break;
                default:
                    throw new InvalidOperationException($"未知 lineOp: {op.type}");
            }
        }
        return lines;
    }

    /// <summary>当前权威快照的逐行文本。</summary>
    private List<string> CurrentRows()
        => _displayState.Current.lines.Select(RowText).ToList();

    /// <summary>
    /// 核心断言：本帧 ops 应用到上一帧快照 ⇒ 逐行等于本帧快照。
    /// 失败信息给出三方对照（prev / 期望 / 还原结果），便于直接定位是哪个 op 算错。
    ///
    /// <para><b>判别力说明（变异式实测：把 <c>ClearLineOp</c> 退回写逻辑参数）</b>：
    /// ① 与 <see cref="ClearLineOpDisplayRowCountTests"/> 两条转红；
    /// ②④ 保持绿 —— ② 是 1:1 对照组（本就不该红），④ 超量删除会走
    /// <c>ComputeDiff</c> 的 race 降级（<c>keep &lt; 0</c> → <c>StructuralDiff</c>），
    /// 结构推断把它救回来了。故<b>判别力集中在 ①③</b>，②④ 是不变式守卫（防未来回归）。</para>
    /// </summary>
    private void AssertRoundTrip(List<string> prev, List<LineOp> ops, string label)
    {
        var expected = CurrentRows();
        var rebuilt = ApplyLineOps(prev, ops);

        Assert.True(expected.SequenceEqual(rebuilt),
            $"{label}：diff 无法从上一帧快照还原出本帧画面 —— 前端累积的画面会与权威快照分叉。\n" +
            $"ops       = [{string.Join(", ", ops.Select(DescribeOp))}]\n" +
            $"prev      ({prev.Count} 行)\n" +
            $"expected  ({expected.Count} 行) {Head(expected)}\n" +
            $"rebuilt   ({rebuilt.Count} 行) {Head(rebuilt)}\n" +
            FirstDiff(expected, rebuilt));
    }

    private static string DescribeOp(LineOp op) => op switch
    {
        AppendLinesOp a => $"append x{a.newLines.Count}",
        ClearLineDiffOp c => $"clear_line_diff -{c.clearCount}",
        ShiftHeadLineOp s => $"shift_head ^{s.count}",
        ClearScreenOp => "clear_screen",
        _ => op.type,
    };

    private static string Head(List<string> rows)
        => rows.Count <= 6
            ? "[" + string.Join(", ", rows) + "]"
            : "[" + string.Join(", ", rows.Take(3)) + " … " + string.Join(", ", rows.TakeLast(3)) + "]";

    private static string FirstDiff(List<string> expected, List<string> actual)
    {
        for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            string e = i < expected.Count ? expected[i] : "<缺行>";
            string a = i < actual.Count ? actual[i] : "<缺行>";
            if (e != a) return $"首个分歧在第 {i} 行：expected={e} rebuilt={a}";
        }
        return "";
    }

    // ------------------------------------------------------------------
    // ① @BADAPPLE 形状：多行段落重印（issue 13 的回归防护）
    // ------------------------------------------------------------------

    /// <summary>
    /// 一帧 = 1 行固定头 + 45 行字符画（1 逻辑 / 45 显示）+ 10 行 UI；
    /// 下一帧 <c>CLEARLINE 11</c>（逻辑）后原样重印 55 个显示行。
    /// 往返必须成立 —— 修复前 <c>clear 11 + append 11</c> 会还原出
    /// <c>[H, A0..A43, B44, U0..U9]</c> 而非 <c>[H, B0..B44, U0..U9]</c> ⇒ 必红。
    /// </summary>
    [Fact]
    public void Multi_row_paragraph_reprint_round_trips()
    {
        PrintRows("H", 1);
        PrintParagraph("A", 45);
        PrintRows("U", 10);
        Assert.Null(BuildDiff());               // 首帧只设基线

        var prev = CurrentRows();

        _console.deleteLine(11);                // CLEARLINE 11（逻辑行）
        PrintParagraph("B", 45);
        PrintRows("U", 10);

        var diff = BuildDiff();
        Assert.NotNull(diff);
        AssertRoundTrip(prev, diff!.lineOps, "45 行段落重印");
    }

    // ------------------------------------------------------------------
    // ② 对照组：1:1 行不受影响（证明 ① 不是把断言写松/写死）
    // ------------------------------------------------------------------

    [Fact]
    public void One_to_one_row_reprint_round_trips()
    {
        PrintRows("H", 2);
        PrintRows("A", 3);
        Assert.Null(BuildDiff());

        var prev = CurrentRows();

        _console.deleteLine(3);
        PrintRows("B", 3);

        var diff = BuildDiff();
        Assert.NotNull(diff);
        AssertRoundTrip(prev, diff!.lineOps, "1:1 行重印");
    }

    // ------------------------------------------------------------------
    // ③ 重印行数少于清掉的行数（净收缩）—— 往返仍须成立
    // ------------------------------------------------------------------

    /// <summary>
    /// 清 3 个逻辑行 = 摘 8 个显示行（U1,U0 + A5..A0），只补 6 行回来 ⇒ 净收缩。
    ///
    /// <para>补的行数刻意取 6 而非更少：修复前 clearCount=3 ⇒ <c>keep = 9-3 = 6</c>，
    /// 若重印不足 6 行会触发 <c>ComputeDiff</c> 的 race 降级（改走结构推断），
    /// 缺陷被兜底掩盖 ⇒ 该用例失去判别力。补 6 行保证 <c>current.Count(7) ≥ keep(6)</c>，
    /// 降级不触发 ⇒ 变异式下必红。</para>
    /// </summary>
    [Fact]
    public void Partial_reprint_round_trips()
    {
        PrintRows("H", 1);
        PrintParagraph("A", 6);                 // 1 逻辑 / 6 显示
        PrintRows("U", 2);                      // prev = 9 行
        Assert.Null(BuildDiff());

        var prev = CurrentRows();

        _console.deleteLine(3);                 // 摘掉 8 个显示行，剩 [H0]
        PrintRows("R", 6);                      // 只补 6 行 ⇒ 净收缩到 7 行

        var diff = BuildDiff();
        Assert.NotNull(diff);
        AssertRoundTrip(prev, diff!.lineOps, "清多行补少行（净收缩）");
    }

    // ------------------------------------------------------------------
    // ④ CLEARLINE 超过现有行数：摘到空 —— 往返仍须成立
    // ------------------------------------------------------------------

    [Fact]
    public void Over_deleting_clearline_round_trips()
    {
        PrintParagraph("A", 4);
        PrintRows("U", 2);
        Assert.Null(BuildDiff());

        var prev = CurrentRows();

        _console.deleteLine(99);
        PrintRows("N", 3);

        var diff = BuildDiff();
        Assert.NotNull(diff);
        AssertRoundTrip(prev, diff!.lineOps, "超量 CLEARLINE");
    }

    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
