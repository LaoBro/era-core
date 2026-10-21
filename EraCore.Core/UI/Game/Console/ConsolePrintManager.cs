using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;
using MinorShift.Emuera.UI.Game;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// Print, style, line management, log output.
/// Extracted from EmueraConsole.Print.cs.
/// </summary>
internal sealed class ConsolePrintManager
{
    private readonly ConsoleStateData _state;
    private readonly EmueraConsole _console;
    private readonly IConsoleUI _ui;

    private int printCWidth = -1;
    private int printCWidthL = -1;
    private int printCWidthL2 = -1;

    private const string ErrorButtonsText = "__openFileWithDebug__";

    internal ConsolePrintManager(ConsoleStateData state, EmueraConsole console, IConsoleUI ui)
    {
        _state = state;
        _console = console;
        _ui = ui;
    }

    // Computed style property matching original
    private StringStyle Style
    {
        get
        {
            if (!_state.UseUserStyle)
                return _state.defaultStyle;
            if (_state.UseSetColorStyle)
                return _state.userStyle;
            if (_state.userStyle.Color == _state.defaultStyle.Color)
                return _state.userStyle;
            return new StringStyle(_state.defaultStyle.Color, _state.userStyle.FontStyle, _state.userStyle.Fontname);
        }
    }

    private bool Enabled => _ui.Created;

    // --- Line management ---

    public void ClearDisplay()
    {
        _state._pendingOps.Enqueue(new ClearOp());
        _state.CBProc?.ClearScreen();
        _state.displayLineList.Clear();
        _state.buttonIndex.Invalidate();
        _state._htmlElementList.Clear();
        ConsoleEscapedParts.Clear();
        _state.logicalLineCount = 0;
        _state.deletedLines = 0;
        _state.lineNo = 0;
        _state.lastDrawnLineNo = -1;
        _state._needFullRefresh = true;
        _console.VerticalScrollBarUpdate();
        _ui.Refresh();
    }

    public bool LastLineIsTemporary =>
        _state.displayLineList.Count != 0 && _state.displayLineList[^1].IsTemporary;

    public bool LastLineIsEmpty =>
        _state.displayLineList.Count != 0 && string.IsNullOrEmpty(_state.displayLineList[^1].ToString().Trim());

    internal void AddRangeDisplayLine(ConsoleDisplayLine[] lineList)
    {
        for (int i = 0; i < lineList.Length; i++)
            AddDisplayLine(lineList[i], false);
    }

    internal void AddDisplayLine(ConsoleDisplayLine line, bool force_LEFT)
    {
        if (Config.CBUseClipboard)
            _state.CBProc!.AddLine(line, force_LEFT);
        if (LastLineIsTemporary)
            DeleteLine(1);

        AConsoleDisplayNode? errorStr = null;
        foreach (ConsoleButtonString button in line.Buttons)
        {
            foreach (AConsoleDisplayNode css in button.StrArray)
            {
                if (css.Error)
                {
                    errorStr = css;
                    goto ScanBreak;
                }
            }
            if (Config.TextDrawingMode != TextDrawingMode.WINAPI)
            {
                button.FilterEscaped();
                if (button.EscapedParts != null)
                {
                    foreach (var p in button.EscapedParts)
                    {
                        p.Parent = button;
                        ConsoleEscapedParts.Add(p, _state.lineNo, p.Depth,
                            (int)Math.Ceiling((float)p.Top / Config.LineHeight) + _state.lineNo,
                            (int)Math.Floor((float)Math.Max(0, p.Bottom - 1) / Config.LineHeight) + _state.lineNo);
                    }
                }
            }
        }
    ScanBreak:
        if (errorStr != null)
        {
            Dialog.Show(trmb.IllegalFontError.Text, trmb.IllegalFontError.Text);
            _console.Quit();
            return;
        }
        if (force_LEFT)
            line.SetAlignment(DisplayLineAlignment.LEFT);
        else
            line.SetAlignment(_state.alignment);
        line.LineNo = _state.lineNo;
        line.bitmapCacheEnabled = _state.bitmapCacheEnabledForNextLine;

        if (_state.displayLineList.Count != 0 && !_state.displayLineList[^1].IsLineEnd)
        {
            var lastline = _state.displayLineList[^1];
            DeleteLine(1, suppressOp: true);
            line.ShiftPositionX(lastline.Buttons[^1].PointX + lastline.Buttons[^1].Width);
            line.ChangeStr([.. lastline.Buttons, .. line.Buttons]);
        }
        _state.displayLineList.Add(line);
        _state.buttonIndex.Invalidate();

        EmitPrintOps(line);
        if (line.IsLineEnd)
            EmitNewLineOp(line.Align);

        _state.lineNo++;
        if (line.IsLogicalLine && _state.displayLineList[^1].IsLineEnd)
            _state.logicalLineCount++;
        if (_state.lineNo == int.MaxValue)
        {
            _state.lastDrawnLineNo = -1;
            _state.lineNo = 0;
        }
        if (_state.logicalLineCount == long.MaxValue)
            _state.logicalLineCount = 0;

        if (_state.displayLineList.Count > Config.MaxLog)
        {
            if (Config.TextDrawingMode != TextDrawingMode.WINAPI)
                ConsoleEscapedParts.RemoveAt(_state.displayLineList[0].LineNo);
            _state.displayLineList.RemoveAt(0);
            _state.deletedLines++;
            // shift_head 协议：MaxLog 头部截断主动 enqueue ShiftHeadTurnOp，
            // 让 DisplayState.ComputeDiff 产出 ShiftHeadLineOp(1) 而非 ClearScreenOp + 全量重印。
            _state._pendingOps.Enqueue(new ShiftHeadTurnOp(1));
        }
    }

    /// <summary>
    /// <c>CLEARLINE n</c>：删掉末尾 <b>n 个逻辑行</b>。
    ///
    /// <para>🔴 issue 13：一个逻辑行可以占<b>多个显示行</b>（<c>HTML_PRINT</c> 一段多行文本、
    /// 长 <c>PRINT</c> 折行）—— 只有首行 <c>IsLogicalLine=true</c>，其余是
    /// 「表示の都合で改行された2行目以降」。故删 n 个逻辑行会连带删掉 <b>更多</b>显示行，
    /// 二者只在 1:1 时相等。</para>
    ///
    /// <para>上报给协议的 <c>ClearLineOp.n</c> 必须是<b>实际消失的显示行数</b>——
    /// 协议层（<c>DisplayState.ComputeDiff</c> → <c>ClearLineDiffOp.clearCount</c>、
    /// <c>TestAdapter</c>、前端 applyDiff）一律按显示行解释这个数字。用 <c>argNum</c>
    /// 会让 wire 自洽地算错：eraTW <c>@BADAPPLE</c> 每帧实际替换 55 行却只报 11 行，
    /// 净增 0 掩盖了它 ⇒ 字符画前 44 行永远停在首帧（「只有下边一部分在动」）。</para>
    /// </summary>
    public void DeleteLine(int argNum, bool suppressOp = false)
    {
        // 可能移除 / 插入 / 头截断 displayLineList 行——无条件失效按钮索引
        // （即使本次未删任何行也只是虚高版本号，多一次重建，不影响正确性）。
        _state.buttonIndex.Invalidate();

        if (Config.CBUseClipboard)
            _state.CBProc!.DelLine(Math.Min(argNum, _state.displayLineList.Count));
        int delNum = 0;
        int num = argNum;
        bool deleted = false;
        int removed = 0;   // issue 13：实际从 displayLineList 摘掉的行数（≠ argNum）
        while (delNum < num)
        {
            if (_state.displayLineList.Count == 0)
                break;
            ConsoleDisplayLine line = _state.displayLineList[^1];
            deleted = true;
            _state.displayLineList.RemoveAt(_state.displayLineList.Count - 1);
            removed++;
            _state.lineNo--;
            if (line.IsLogicalLine)
            {
                delNum++;
                if (line.IsLineEnd)
                    _state.logicalLineCount--;
            }
            if (_state.displayLineList.Count == Config.MaxLog - 1)
                _state.deletedLines++;
            if (_state.displayLineList.Count == Config.MaxLog - 2 && _state.lineNo > _state.displayLineList.Count)
            {
                ConsoleDisplayLine dummyline = BufferToSingleLine(true, false)!;
                _state.displayLineList.Insert(0, dummyline);
            }
        }
        if (delNum < num)
        {
            _state.lineNo = 0;
            _state.logicalLineCount -= num - delNum;
        }
        if (deleted && Config.TextDrawingMode != TextDrawingMode.WINAPI)
            ConsoleEscapedParts.Remove(_state.lineNo);
        if (_state.lineNo < 0)
            _state.lineNo += int.MaxValue;
        _state.lastDrawnLineNo = -1;
        // issue 13：清空计数必须用**实际摘掉的显示行数** removed，不是逻辑参数 argNum。
        // 位置仍排在下面的 ShiftHeadTurnOp 之前 —— 与改动前的 enqueue 顺序一致
        // （DisplayState 按类型累积、与顺序无关；保序只为不惊动 TestAdapter 的模型）。
        if (!suppressOp)
            _state._pendingOps.Enqueue(new ClearLineOp(displayRows: removed));
        if (_state.displayLineList.Count == Config.MaxLog)
        {
            _state.displayLineList.RemoveAt(0);
            // shift_head 协议：CLEARLINE 后恰好达到 MaxLog 时触发的头部截断——
            // 同样 enqueue ShiftHeadTurnOp，否则该路径仍会触发全量重印 diff。
            _state._pendingOps.Enqueue(new ShiftHeadTurnOp(1));
        }
        _state.deletedLines -= num;

    }

    public ConsoleDisplayLine? BufferToSingleLine(bool force, bool temporary)
    {
        if (!Enabled)
            return null;
        if (!force && _console.printBuffer.IsEmpty)
            return null;
        if (force && _console.printBuffer.IsEmpty)
            _console.printBuffer.Append(" ", Style);
        ConsoleDisplayLine dispLine = _console.printBuffer.FlushSingleLine(_console.stringMeasure, temporary | _state.force_temporary);
        return dispLine;
    }

    public void ClearText() => _ui.ClearRichText();

    // --- Print methods ---

    public void Print(string? str, bool lineEnd = true)
    {
        if (string.IsNullOrEmpty(str))
            return;
        var lineEndIndex = str.IndexOf('\n', StringComparison.Ordinal);
        if (lineEndIndex != -1)
        {
            string upper = str[..lineEndIndex];
            _console.printBuffer.Append(upper, Style);
            _console.NewLine();
            if (lineEndIndex < str.Length - 1)
            {
                string lower = str[(lineEndIndex + 1)..];
                Print(lower);
            }
            return;
        }
        _console.printBuffer.Append(str, Style, lineEnd: lineEnd);
    }

    public void PrintSingleLine(string str) => PrintSingleLine(str, false);

    public void PrintSingleLine(string str, bool temporary)
    {
        if (string.IsNullOrEmpty(str))
            return;
        PrintFlush(false);
        _console.printBuffer.Append(str, Style);
        ConsoleDisplayLine? dispLine = BufferToSingleLine(true, temporary);
        if (dispLine == null)
            return;
        AddDisplayLine(dispLine, false);
        _console.RefreshStrings(false);
    }

    public void PrintC(string str, bool alignmentRight)
    {
        if (string.IsNullOrEmpty(str))
            return;
        _console.printBuffer.Append(CreateTypeCString(str, alignmentRight), Style, true);
    }

    public void PrintFlush(bool force)
    {
        if (!Enabled)
            return;
        if (!force && _console.printBuffer.IsEmpty)
            return;
        if (force && _console.printBuffer.IsEmpty)
            _console.printBuffer.Append(" ", Style);
        ConsoleDisplayLine[] dispList = _console.printBuffer.Flush(_console.stringMeasure, _state.force_temporary);
        AddRangeDisplayLine(dispList);
    }

    public void PrintSystemLine(string str)
    {
        PrintFlush(false);
        _state.UseUserStyle = false;
        PrintSingleLine(str, false);
    }

    public void PrintError(string str)
    {
        if (string.IsNullOrEmpty(str))
            return;
        if (Program.DebugMode)
        {
            _console.DebugPrint(str);
            _console.DebugNewLine();
        }
        PrintFlush(false);
        _state.UseUserStyle = false;
        ConsoleDisplayLine? dispLine = PrintPlainwithSingleLine(str);
        if (dispLine == null)
            return;
        AddDisplayLine(dispLine, true);
        _console.RefreshStrings(false);
    }

    internal void PrintErrorButton(string str, ScriptPosition? pos, int level = 0)
    {
        if (string.IsNullOrEmpty(str))
            return;
        if (Program.DebugMode)
        {
            _console.DebugPrint(str);
            _console.DebugNewLine();
        }
        _state.UseUserStyle = false;
        var errColor = EmuColor.FromArgb(255, 255, 255, 160);
        var errerStyle = Style;
        errerStyle.Color = level switch
        {
            0 => errColor,
            1 => errColor,
            2 => errColor,
            3 => EmuColor.Red,
            _ => EmuColor.Red
        };
        ConsoleDisplayLine dispLine = _console.printBuffer.AppendAndFlushErrButton(str, errerStyle, ErrorButtonsText, pos, _console.stringMeasure);
        if (dispLine == null)
            return;
        AddDisplayLine(dispLine, true);
        _console.RefreshStrings(false);
    }

    public void PrintWarning(string str, ScriptPosition? position, int level)
    {
        if (level < Config.DisplayWarningLevel && !Program.AnalysisMode)
            return;
        bool b = _state.force_temporary;
        _state.force_temporary = false;
        if (position != null)
        {
            if (position.Value.LineNo >= 0)
            {
                PrintErrorButton(string.Format(trerror.Warning1.Text, level, position.Value.Filename, position.Value.LineNo, str), position, level);
                GlobalStatic.Process.printRawLine(position);
            }
            else
                PrintErrorButton(string.Format(trerror.Warning2.Text, level, position.Value.Filename, str), position, level);
        }
        else
            PrintError(string.Format(trerror.Warning3.Text, level, str));
        _state.force_temporary = b;
    }

    public void PrintTemporaryLine(string str) => PrintSingleLine(str, true);

    public void PrintBar()
    {
        StringStyle ss = _state.userStyle;
        _state.userStyle.FontStyle = EmuFontStyle.Regular;
        Print(_state.stBar);
        _state.userStyle = ss;
    }

    public void PrintCustomBar(string barStr, bool isConst)
    {
        if (string.IsNullOrEmpty(barStr))
            throw new CodeEE(trerror.EmptyDrawline.Text);
        StringStyle ss = _state.userStyle;
        _state.userStyle.FontStyle = EmuFontStyle.Regular;
        if (isConst)
            Print(barStr);
        else
            Print(GetStBar(barStr));
        _state.userStyle = ss;
    }

    public void PrintButton(string str, string p)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(str, Style, p);
    }

    public void PrintButton(string str, long p)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(str, Style, p);
    }

    public void PrintButtonC(string str, string p, bool isRight)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(CreateTypeCString(str, isRight), Style, p);
    }

    public void PrintButtonC(string str, long p, bool isRight)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendButton(CreateTypeCString(str, isRight), Style, p);
    }

    public void PrintPlain(string str)
    {
        if (string.IsNullOrEmpty(str)) return;
        _console.printBuffer.AppendPlainText(str, Style);
    }

    public void PrintHtml(string str, bool toPrintBuffer)
    {
        if (string.IsNullOrEmpty(str) || !Enabled) return;
        if (toPrintBuffer)
        {
            foreach (var button in HtmlManager.Html2ButtonList(str, _console.stringMeasure, _console))
                _console.printBuffer.AppendButton(button);
        }
        else
        {
            if (!_console.printBuffer.IsEmpty)
            {
                ConsoleDisplayLine[] dispList = _console.printBuffer.Flush(_console.stringMeasure, _state.force_temporary);
                AddRangeDisplayLine(dispList);
            }
            AddRangeDisplayLine(HtmlManager.Html2DisplayLine(str, _console.stringMeasure, _console));
        }
        _console.RefreshStrings(false);
    }

    /// <summary>
    /// HTML_PRINTC / HTML_PRINTLC（实现票 23）：复用既有 HTML 管线渲染，再按单元格宽度补空白——
    /// <paramref name="alignmentRight"/> = true（HTML_PRINTC）在内容前补空白（右对齐），
    /// false（HTML_PRINTLC）在内容后补空白（左对齐）。<paramref name="cellWidthPx"/> ≤ 0 时取
    /// <c>PrintCLength * FontSize / 2</c> 默认单元格宽度（对齐 Skia 上游）。
    /// </summary>
    public void PrintHtmlC(string str, bool alignmentRight, int cellWidthPx)
    {
        if (string.IsNullOrEmpty(str) || !Enabled) return;
        if (cellWidthPx <= 0)
            cellWidthPx = Config.PrintCLength * Config.FontSize / 2;

        ConsoleButtonString[] buttons = HtmlManager.Html2ButtonList(str, _console.stringMeasure, _console);
        if (buttons.Length == 0) return;

        int contentWidth = 0;
        foreach (ConsoleButtonString btn in buttons)
        {
            btn.CalcWidth(_console.stringMeasure, 0);
            contentWidth += btn.Width;
        }

        int padPx = cellWidthPx - contentWidth;
        if (padPx > 0 && alignmentRight)
            _console.printBuffer.AppendButton(CreateSpaceButton(padPx));

        foreach (ConsoleButtonString btn in buttons)
            _console.printBuffer.AppendButton(btn);

        if (padPx > 0 && !alignmentRight)
            _console.printBuffer.AppendButton(CreateSpaceButton(padPx));

        _console.RefreshStrings(false);
    }

    /// <summary>构造一个宽度为 <paramref name="padPx"/> 像素的空白按钮（供 PrintHtmlC 单元格对齐）。</summary>
    private ConsoleButtonString CreateSpaceButton(int padPx)
    {
        ConsoleShapePart spacePart = ConsoleShapePart.CreateShape(
            "space",
            [new MixedNum { num = padPx, isPx = true }],
            _state.userStyle.Color,
            _state.userStyle.ButtonColor,
            false);
        var spaceBtn = new ConsoleButtonString(_console, [spacePart]);
        spaceBtn.CalcWidth(_console.stringMeasure, 0);
        return spaceBtn;
    }

    public void PrintHTMLIsland(string html)
    {
        _state._htmlElementList.AddRange(HtmlManager.Html2DisplayLine(html, _console.stringMeasure, _console));
    }

    public void ClearHTMLIsland() => _state._htmlElementList.Clear();

    public void PrintImg(string name, string? nameb, string? namem, MixedNum? height, MixedNum? width, MixedNum? ypos)
    {
        _console.printBuffer.Append(new ConsoleImagePart(name, nameb, namem, height, width, ypos));
    }

    // --- 背景图（issue 02：SETBGIMAGE 落地） ---
    // WinForms 语义：set=追加（同 src 可多份）、remove=移除首个同名、clear=全清。
    // 无头不做烘焙——状态进快照/diff，由前端按 depth 排序渲染。

    public void SetBgImage(string name, long depth, float opacity)
    {
        _state.bgImages.Add(new BgImageState(name, depth, opacity));
        _state._pendingOps.Enqueue(new SetBgImageOp(name, depth, opacity));
    }

    public void RemoveBgImage(string name)
    {
        int i = _state.bgImages.FindIndex(b => b.src == name);
        if (i >= 0)
            _state.bgImages.RemoveAt(i);
        _state._pendingOps.Enqueue(new RemoveBgImageOp(name));
    }

    public void ClearBgImage()
    {
        _state.bgImages.Clear();
        _state._pendingOps.Enqueue(new ClearBgImageOp());
    }

    public void PrintShape(string type, MixedNum[] param)
    {
        ConsoleShapePart part = ConsoleShapePart.CreateShape(type, param, _state.userStyle.Color, _state.userStyle.ButtonColor, false);
        _console.printBuffer.Append(part);
    }

    internal ConsoleDisplayLine? PrintPlainwithSingleLine(string str)
    {
        if (!Enabled || string.IsNullOrEmpty(str))
            return null;
        _console.printBuffer.AppendPlainText(str, Style);
        return _console.printBuffer.FlushSingleLine(_console.stringMeasure, false);
    }

    public void PrintPlainWithSingleLineFix(string str)
    {
        ConsoleDisplayLine? dispLine = PrintPlainwithSingleLine(str);
        if (dispLine == null) return;
        AddDisplayLine(dispLine, false);
        _console.RefreshStrings(false);
    }

    public ConsoleDisplayLine[]? GetDisplayLines(long lineNo)
    {
        if (lineNo < 0 || lineNo > _state.displayLineList.Count)
            return null;
        int count = 0;
        List<ConsoleDisplayLine> list = [];
        for (int i = _state.displayLineList.Count - 1; i >= 0; i--)
        {
            if (count == lineNo)
                list.Insert(0, _state.displayLineList[i]);
            if (_state.displayLineList[i].IsLogicalLine)
                count++;
            if (count > lineNo)
                break;
        }
        if (list.Count == 0)
            return null;
        ConsoleDisplayLine[] ret = new ConsoleDisplayLine[list.Count];
        list.CopyTo(ret);
        return ret;
    }

    public ConsoleDisplayLine[]? PopDisplayingLines()
    {
        if (!Enabled || _console.printBuffer.IsEmpty)
            return null;
        return _console.printBuffer.Flush(_console.stringMeasure, _state.force_temporary);
    }

    // --- Bar helpers ---

    public string? GetDefStBar() => _state.stBar;

    public string GetStBar(string barStr)
    {
        var builder = new StringBuilder();
        builder.Append(barStr);
        int width = 0;
        EmuFont font = Config.DefaultFont;
        while (width < Config.DrawableWidth)
        {
            builder.Append(barStr);
            width = _console.stringMeasure.GetDisplayLength(builder.ToString(), font);
        }
        while (width > Config.DrawableWidth)
        {
            builder.Remove(builder.Length - 1, 1);
            width = _console.stringMeasure.GetDisplayLength(builder.ToString(), font);
        }
        return builder.ToString();
    }

    public void SetStBar(string barStr) => _state.stBar = GetStBar(barStr);

    // --- Style helpers ---

    public void SetStringStyle(EmuFontStyle fs) => _state.userStyle.FontStyle = fs;

    public void SetStringStyle(EmuColor color)
    {
        _state.userStyle.Color = color;
        _state.userStyle.ColorChanged = color != Config.ForeColor;
    }

    public void SetFont(string fontname)
    {
        if (!string.IsNullOrEmpty(fontname))
            _state.userStyle.Fontname = fontname;
        else
            _state.userStyle.Fontname = Config.FontName;
    }

    public void ResetStyle()
    {
        _state.userStyle = _state.defaultStyle;
        _state.alignment = DisplayLineAlignment.LEFT;
    }

    public void SetBgColor(EmuColor color)
    {
        _state._pendingOps.Enqueue(new SetBgOp(color.ToHex()));
        _state.bgColor = color;
        _state.forceTextBoxColor = true;
        if (_state.redraw == ConsoleRedraw.None && _ui.ScrollBar.Value == _ui.ScrollBar.Maximum)
            return;
        _state.WaitFrameSyncIfEnabled(_ui);
        _console.RefreshStrings(true);
        // WaitFrameSyncIfEnabled 已保证 _drawStopwatch 非 null（内部 StartNew 兜底）
        _state._drawStopwatch!.Restart();
    }

    // --- Op emission (v2 turn protocol) ---

    private void EmitPrintOps(ConsoleDisplayLine line)
    {
        foreach (var op in BuildPrintOpsForLine(line, Config.FontName))
            _state._pendingOps.Enqueue(op);
    }

    /// <summary>
    /// 将单个 ConsoleDisplayLine 转换为 PrintOp 列表（ADR-0013 决策三）。
    /// 遍历 line.Buttons 时累计 column 位置，对 IsButton 的条目用 TerminalDisplayWidth.GetDisplayWidth
    /// 计算 col/width 并填入 ButtonRef。col 是行内累计列位置（从 0 开始，按前序 button 文本显示
    /// 宽度累加）；width 是按钮文本的显示宽度（字符单位，含 CJK 双宽）。非按钮的 PrintOp 中
    /// ButtonRef 仍为 null（col/width 不出现，WhenWritingNull）。
    /// 提取为 internal static 以便单元测试（OpGeometryTests）直测几何计算，无需构造 ConsolePrintManager。
    ///
    /// defaultFontName 参数：默认字体名，用于判断 segment 的 fontname 是否为默认字体（相同则不写入 JSON）。
    /// 调用方负责传入——EmitPrintOps 从 Config.FontName 读（游戏循环线程，Config.Current 已设）；
    /// DisplayState.BuildSnapshot 从 ConfigData 读（HTTP 线程，Config.Current 不可用）。
    /// </summary>
    internal static List<PrintOp> BuildPrintOpsForLine(ConsoleDisplayLine line, string defaultFontName)
    {
        var ops = new List<PrintOp>();
        int column = 0;
        foreach (var btn in line.Buttons)
        {
            var segments = new List<PrintSegment>();
            foreach (var node in btn.StrArray)
            {
                string text;
                string? color = null;
                bool? bold = null;
                bool? italic = null;
                string? fontname = null;
                bool? underline = null;
                bool? strikeout = null;
                SegmentImage? image = null;
                SegmentShape? shape = null;

                if (node is ConsoleStyledString css)
                {
                    text = css.Text ?? "";
                    var style = css.StringStyle;
                    if (style.ColorChanged)
                        color = style.Color.ToHex();
                    if ((style.FontStyle & EmuFontStyle.Bold) != 0)
                        bold = true;
                    if ((style.FontStyle & EmuFontStyle.Italic) != 0)
                        italic = true;
                    if ((style.FontStyle & EmuFontStyle.Underline) != 0)
                        underline = true;
                    if ((style.FontStyle & EmuFontStyle.Strikeout) != 0)
                        strikeout = true;
                    if (style.Fontname != defaultFontName)
                        fontname = style.Fontname;
                }
                else if (node is ConsoleImagePart img)
                {
                    // v8（issue 02）：图片 segment——几何来自 01 解耦路径，不再空串丢弃
                    // v9（issue 07）：裁切矩形几何（图集 sprite）进入协议
                    text = img.Text ?? "";
                    image = new SegmentImage(
                        src: img.ResourceName,
                        srcb: img.ButtonResourceName,
                        srcm: img.MappingGraphName,
                        width: img.Width,
                        height: img.Height,
                        ypos: img.YPos,
                        crop: img.HasCrop
                            ? new SegmentCrop(img.CropX, img.CropY, img.CropImgWidth, img.CropImgHeight)
                            : null
                    );
                }
                else if (node is ConsoleSpacePart space)
                {
                    // ConsoleSpacePart.Text 固定为空；若直接走通用分支，协议会丢失
                    // HTML_PRINT 的横向占位（例如立绘前的 <shape type='space'>）。
                    // 与 TerminalLineFormatter 保持相同的像素→半角列换算。
                    int charWidth = Math.Max(Config.FontSize / 2, 1);
                    text = new string(' ', Math.Max(space.Width / charWidth, 0));
                }
                else if (node is ConsoleRectangleShapePart rect)
                {
                    // v8（issue 02）：形状 segment——rect 绝对几何 + 填充色
                    text = rect.Text ?? "";
                    shape = new SegmentShape(
                        type: "rect",
                        x: rect.Rect.X,
                        y: rect.Rect.Y,
                        width: rect.Rect.Width,
                        height: rect.Rect.Height,
                        color: rect.FillColor.ToHex()
                    );
                }
                else
                {
                    text = node.Text ?? "";
                }

                segments.Add(new PrintSegment(text, color, bold, italic, fontname, image, shape, underline, strikeout));
            }

            string buttonText = btn.ToString() ?? "";
            int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(buttonText);

            ButtonRef? button = null;
            if (btn.IsButton)
                button = new ButtonRef(
                    value: btn.IsInteger ? (object)btn.Input : (object)btn.Inputs,
                    isInteger: btn.IsInteger,
                    generation: btn.Generation,
                    col: column,
                    width: segmentWidth
                );

            ops.Add(new PrintOp(segments, button));
            column += segmentWidth;
        }
        return ops;
    }

    private void EmitNewLineOp(DisplayLineAlignment align)
    {
        string? alignStr = align switch
        {
            DisplayLineAlignment.CENTER => "center",
            DisplayLineAlignment.RIGHT => "right",
            _ => null
        };
        _state._pendingOps.Enqueue(new NewLineOp(alignStr));
    }

    // --- Log ---

    private bool OutputLogInternal(string fullpath, bool hideInfo, bool showFailure)
    {
        try
        {
            var log = GetLog(hideInfo);
            SafCompat.WriteAllText(fullpath, log, EncodingHandler.UTF8BOMEncoding);
        }
        catch (Exception ex)
        {
            EmueraLog.Error("OutputLog", $"Failed to write {fullpath}: {ex}");
            if (showFailure)
                Dialog.Show(trmb.FailedOutputLog.Text, trmb.FailedOutputLogError.Text);
            return false;
        }
        return true;
    }

    public bool OutputLog(string? filename, bool hideInfo, bool showFailure = true)
    {
        if (!TryResolveOutputLogPath(filename, out var fullpath, out var relativePath))
        {
            Dialog.Show(trmb.FailedOutputLog.Text, trmb.CanOnlyOutputToSubDirectory.Text);
            return false;
        }
        if (OutputLogInternal(fullpath, hideInfo, showFailure))
        {
            if (_ui.Created)
            {
                PrintSystemLine(string.Format(trsl.LogFileHasBeenCreated.Text, relativePath));
                _console.RefreshStrings(true);
            }
            return true;
        }
        return false;
    }

    public bool OutputSystemLog(string? filename)
    {
        if (!TryResolveOutputLogPath(filename, out var fullpath, out var relativePath))
        {
            Dialog.Show(trmb.FailedOutputLog.Text, trmb.CanOnlyOutputToSubDirectory.Text);
            return false;
        }
        if (OutputLogInternal(fullpath, false, showFailure: true))
        {
            if (_ui.Created)
            {
                PrintSystemLine(string.Format(trsl.LogFileHasBeenCreated.Text, relativePath));
                _console.RefreshStrings(true);
            }
            return true;
        }
        return false;
    }

    private static bool TryResolveOutputLogPath(string? filename, out string fullpath, out string relativePath)
    {
        relativePath = string.IsNullOrEmpty(filename) ? "emuera.log" : filename;
        return SafCompat.TryResolvePathUnderGameRoot(relativePath, createParentDirectories: true, out fullpath);
    }

    public string GetLog(bool hideInfo)
    {
        var builder = new StringBuilder();
        if (!hideInfo)
        {
            builder.AppendLine(trsl.EnvironmentInformation.Text);
            builder.AppendLine(AssemblyData.EmueraVersionText);
            builder.AppendLine();
            builder.AppendLine(trsl.Variant.Text);
            if (string.IsNullOrEmpty(_state.process!.gameBase.ScriptTitle))
                builder.AppendLine(trsl.NotDefinedGameBase.Text);
            else
                builder.AppendLine(_state.process!.gameBase.ScriptTitle + " " + _state.process!.gameBase.ScriptVersionText);
            var patchVersionsPath = SafCompat.ResolveSubPath(Program.ExeDir, "patch_versions");
            if (SafCompat.DirectoryExists(patchVersionsPath))
            {
                builder.AppendLine(trsl.PatchVersion.Text);
                var versionTexts = SafCompat.GetFiles(patchVersionsPath, "*.txt", SearchOption.TopDirectoryOnly)
                    .Where(x => Path.GetExtension(SafPath.GetLogicalFileName(x)).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .Select(x => SafCompat.ReadAllText(x).Trim());
                builder.AppendLine(string.Join("+", versionTexts));
            }
            builder.AppendLine();
            builder.AppendLine(trsl.Log.Text);
            builder.AppendLine();
        }
        for (int i = 0; i < _state.displayLineList.Count; i++)
            builder.AppendLine(ClipboardProcessor.StripHTML(_state.displayLineList[i].ToString()));
        return builder.ToString();
    }

    // --- PrintC helpers ---

    private void CalcPrintCWidth(StringMeasure sm)
    {
        string str = new(' ', Config.PrintCLength);
        EmuFont font = Config.DefaultFont;
        printCWidth = sm.GetDisplayLength(str, font);
        printCWidthL = sm.GetDisplayLength(str, font);
        printCWidthL2 = sm.GetDisplayLength(str, font);
    }

    private string CreateTypeCString(string str, bool alignmentRight)
    {
        if (printCWidth == -1)
            CalcPrintCWidth(_console.stringMeasure);
        int length = Encoding.GetEncoding("Shift-JIS").GetByteCount(str);
        int width;
        int printcLength = Config.PrintCLength;
        EmuFont font;
        try
        {
            font = new EmuFont(Style.Fontname, Config.DefaultFont.Size, Style.FontStyle);
        }
        catch (Exception ex) { AgentLog.Instance.Write("font creation failed, fallback: " + ex.Message); return str; }

        if (alignmentRight && (length < printcLength))
        {
            str = new string(' ', printcLength - length) + str;
            width = _console.stringMeasure.GetDisplayLength(str, font);
            while (width > printCWidth)
            {
                if (str[0] != ' ') break;
                str = str.Remove(0, 1);
                width = _console.stringMeasure.GetDisplayLength(str, font);
            }
        }
        else if ((!alignmentRight) && (length < printcLength + 1))
        {
            str += new string(' ', printcLength + 1 - length);
            width = _console.stringMeasure.GetDisplayLength(str, font);
            while (width > printCWidthL)
            {
                if (str[^1] != ' ') break;
                str = str.Remove(str.Length - 1, 1);
                width = _console.stringMeasure.GetDisplayLength(str, font);
            }
        }
        return str;
    }
}
