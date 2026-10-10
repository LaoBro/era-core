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
/// issue 13：一个逻辑行可以占<b>多个显示行</b>，而 <c>CLEARLINE n</c> 的参数是<b>逻辑行</b>数。
/// 协议层（<c>ClearLineDiffOp.clearCount</c> / <c>TestAdapter</c> / 前端 applyDiff）却一律按
/// <b>显示行</b>解释该数字 ⇒ 少删少补 ⇒ 每帧只替换尾部一小段，上面一大片永远停在旧帧。
///
/// <para>eraTW <c>@BADAPPLE</c> 的实测形状（<c>TITLE.ERB:143-171</c>）：
/// <c>HTML_PRINT</c> 一段 45 行字符画 = <b>1 个逻辑行 / 45 个显示行</b>，再打 10 行 UI
/// ⇒ 每帧逻辑 +11、显示 +55；<c>CLEARLINE LINECOUNT - gLineCount</c> = <c>CLEARLINE 11</c>
/// 实际删掉 <b>55</b> 个显示行。wire 上却是 <c>clear_line_diff 11 + append 11</c>（净 0，
/// 极具误导性：看起来一切正常），前端于是只重画尾部 11 行 —— 字符画前 44 行自始至终没被写过。</para>
///
/// <para><b>防假绿</b>：断言不是「帧里有 diff」「行数不变」，而是<b>具体数字</b>
/// （删 55 / 补 55）。修好前 clearCount=11、append=11 ⇒ 必然红。</para>
/// </summary>
public class ClearLineOpDisplayRowCountTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _displayState;

    public ClearLineOpDisplayRowCountTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        var ui = new HeadlessConsole();
        _console = new EmueraConsole(ui, new NullTerminalSetup());
        _displayState = new DisplayState(_console, "MS Gothic");
    }

    public void Dispose() => _scope.Dispose();

    private static ConsoleDisplayLine Line(string text, bool logical) =>
        new(new[] { TestButtonFactory.CreateNonButton(text) }, isLogical: logical, temporary: false);

    /// <summary>
    /// 打印一个「1 个逻辑行 / <c>rows</c> 个显示行」的段落 ——
    /// <c>HTML_PRINT</c> 一段多行文本（<c>%LOADTEXT%</c>）落地到 displayLineList 的形状：
    /// 只有段落首行 <c>IsLogicalLine=true</c>，其余都是「表示の都合で改行された2行目以降」。
    /// </summary>
    private void PrintParagraph(string prefix, int rows)
    {
        for (int i = 0; i < rows; i++)
            _console.DisplayLineList.Add(Line($"{prefix}{i}", logical: i == 0));
    }

    /// <summary>打印 <c>count</c> 个普通行（每行 = 1 逻辑行 / 1 显示行）。</summary>
    private void PrintRows(string prefix, int count)
    {
        for (int i = 0; i < count; i++)
            _console.DisplayLineList.Add(Line($"{prefix}{i}", logical: true));
    }

    private DisplayDiff? BuildDiff() => _displayState.ComputeDiff();

    /// <summary>取走本回合累积的 CLEARLINE 显示行数（<c>DisplayState</c> 侧看到的值）。</summary>
    private int DrainClearLineRows()
    {
        var ops = _console.DrainPendingOps();
        return ops.OfType<ClearLineOp>().Sum(o => o.n);
    }

    // ------------------------------------------------------------------
    // ① 引擎→协议边界：ClearLineOp 必须携带「实际消失的显示行数」
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>CLEARLINE 3</c>（逻辑）在一幅「5 显示行 / 3 逻辑行」的画面上应当删掉 <b>5</b> 个显示行，
    /// 而不是 3。协议层按显示行解释 <c>ClearLineOp.n</c>，故 op 里必须是 5。
    /// </summary>
    [Fact]
    public void DeleteLine_reports_actual_display_rows_not_logical_argument()
    {
        PrintParagraph("P", 3);   // 1 逻辑 / 3 显示
        PrintRows("U", 2);         // 2 逻辑 / 2 显示
        Assert.Equal(5, _console.DisplayLineList.Count);

        _console.deleteLine(3);    // CLEARLINE 3（逻辑行）

        Assert.Equal(5, DrainClearLineRows());
        Assert.Empty(_console.DisplayLineList);
    }

    /// <summary>对照组：全是 1:1 行时两个口径相等 —— 证明上面那条不是把常量改大了。</summary>
    [Fact]
    public void DeleteLine_reports_same_number_when_rows_are_one_to_one()
    {
        PrintRows("R", 4);         // 4 逻辑 / 4 显示

        _console.deleteLine(2);

        Assert.Equal(2, DrainClearLineRows());
        Assert.Equal(2, _console.DisplayLineList.Count);
    }

    // ------------------------------------------------------------------
    // ② 端到端 diff：@BADAPPLE 那一帧的形状
    // ------------------------------------------------------------------

    /// <summary>
    /// 复刻 eraTW <c>@BADAPPLE</c> 的一帧：1 行固定头 + 45 行字符画（1 逻辑）+ 10 行 UI，
    /// 下一帧 <c>CLEARLINE 11</c> 后原样重印。diff 必须是 <b>clear 55 + append 55</b>。
    ///
    /// <para>修好前是 <c>clear 11 + append 11</c>：净 0、行数恒定，看上去完全正常，
    /// 但被替换的只有尾部 11 行 —— 字符画 45 行里 44 行停在首帧，即「只有下边在动」。</para>
    /// </summary>
    [Fact]
    public void Reprinting_a_multi_row_paragraph_replaces_all_its_display_rows()
    {
        // ---- 帧 0：56 个显示行 / 12 个逻辑行
        PrintRows("H", 1);
        PrintParagraph("A", 45);
        PrintRows("U", 10);
        Assert.Equal(56, _console.DisplayLineList.Count);
        Assert.Null(BuildDiff());   // 首帧只设基线

        // ---- 帧 1：CLEARLINE LINECOUNT - gLineCount（= 11 逻辑行）后重印 55 显示行
        _console.deleteLine(11);
        PrintParagraph("B", 45);
        PrintRows("U", 10);
        Assert.Equal(56, _console.DisplayLineList.Count);

        var diff = BuildDiff();
        Assert.NotNull(diff);
        Assert.Equal(2, diff!.lineOps.Count);

        var clear = Assert.IsType<ClearLineDiffOp>(diff.lineOps[0]);
        Assert.Equal(55, clear.clearCount);

        var append = Assert.IsType<AppendLinesOp>(diff.lineOps[1]);
        Assert.Equal(55, append.newLines.Count);
        // 补进来的必须真的是新一帧的字符画（B 段），而不是把旧帧尾部 11 行又贴了一遍
        Assert.Equal("B0", append.newLines[0].entries[0].segments[0].text);
        Assert.Equal("U9", append.newLines[^1].entries[0].segments[0].text);
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
