using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// Input processing: WaitInput, ReadAnyKey, PressEnterKey, doInputToEmueraProgram,
/// InputMouseKey, MouseWheel, MouseDown, PressPrimitiveKey, MoveMouse, LeaveMouse,
/// parseInput, newGeneration, button selection state.
/// </summary>
internal sealed class ConsoleInputHandler
{
    private readonly ConsoleStateData _state;
    private readonly EmueraConsole _console;
    private readonly IConsoleUI _ui;

    internal ConsoleInputHandler(ConsoleStateData state, EmueraConsole console, IConsoleUI ui)
    {
        _state = state;
        _console = console;
        _ui = ui;
    }

    public void WaitInput(InputRequest req)
    {
        if (Config.CBUseClipboard)
            _state.CBProc?.Check(ClipboardProcessor.CBTriggers.InputWait);
        _state.State = ConsoleState.WaitInput;
        _state.inputReq = req;
        if (req.Timelimit > 0)
        {
            if (req.OneInput)
                _ui.UpdateLastInput();
            _console._timer.PresetTimer();
        }
    }

    public void ReadAnyKey(bool anykey = false, bool stopMesskip = false)
    {
        if (Config.CBUseClipboard)
            _state.CBProc?.Check(ClipboardProcessor.CBTriggers.AnyKeyWait);
        InputRequest req = new();
        if (!anykey)
            req.InputType = InputType.EnterKey;
        else
            req.InputType = InputType.AnyKey;
        req.StopMesskip = stopMesskip;
        _state.inputReq = req;
        _state.State = ConsoleState.WaitInput;
        _state.process!._systemProc.NeedWaitToEventComEnd = false;
    }

    public void PressEnterKey(bool keySkip, string input, bool changedByMouse)
    {
        _state.MesSkip = keySkip;
        if ((_state.State == ConsoleState.Running) || (_state.State == ConsoleState.Initializing))
            return;
        else if (_state.State == ConsoleState.Quit)
        {
            if (Program.rebootFlag)
                _ui.Reboot();
            else
                _ui.Close();
            return;
        }
        else if (_state.State == ConsoleState.Error)
        {
            if (Program.DebugMode)
                return;
            if (input == ErrorButtonsText && _state.selectingButton != null && _state.selectingButton.ErrPos != null)
            {
                _console._stateManager.OpenErrorFile(_state.selectingButton.ErrPos);
                return;
            }
            _ui.Close();
            return;
        }
#if DEBUG
        if (_state.State != ConsoleState.WaitInput || _state.inputReq == null)
            throw new ExeEE("");
#endif
        _state.KillMacro = false;
        try
        {
            string[] text;
            if (changedByMouse)
            { text = [input]; }
            else
            {
                if (input.Length > 1 && !_state.inputReq!.OneInput && input.StartsWith('@'))
                {
                    _console._stateManager.DoSystemCommand(input);
                    return;
                }
                if (_state.inputReq!.InputType == InputType.Void)
                    return;
                if (_state.genericTimer.Enabled &&
                        (_state.inputReq!.InputType == InputType.AnyKey || _state.inputReq!.InputType == InputType.EnterKey))
                    _console._timer.StopTimer();
                if (input.Contains('(', StringComparison.Ordinal))
                    input = ParseInput(new CharStream(input), false);
                text = input.Split(Spliter, StringSplitOptions.None);
            }

            _state.inProcess = true;
            for (int i = 0; i < text.Length; i++)
            {
                string inputs = text[i];
                if (inputs.Contains("\\e", StringComparison.Ordinal))
                {
                    inputs = inputs.Replace("\\e", "", StringComparison.Ordinal);
                    _state.MesSkip = true;
                }
                if (_state.inputReq!.OneInput && (!_ui.Created || !changedByMouse) && inputs.Length > 1)
                    inputs = inputs.Remove(1);
                if (_state.inputReq!.InputType == InputType.Void)
                {
                    i--;
                    inputs = "";
                }
                _console.RunEmueraProgram(inputs);
                _console.RefreshStrings(false);
                while (_state.MesSkip && _state.State == ConsoleState.WaitInput)
                {
                    if (_state.inputReq!.NeedValue)
                        break;
                    if (_state.inputReq!.StopMesskip)
                        break;
                    _console.RunEmueraProgram("");
                    _console.RefreshStrings(false);
                }
                _state.MesSkip = false;
                if (_state.State != ConsoleState.WaitInput)
                    break;
                _ui.ProcessEvents();
#if DEBUG
                if (_state.State != ConsoleState.WaitInput || _state.inputReq == null)
                    throw new ExeEE("");
#endif
                if (_state.KillMacro)
                {
                    EndMacro();
                    return;
                }
            }
        }
        finally
        {
            _state.inProcess = false;
        }
        EndMacro();

        void EndMacro()
        {
            if (_state.State == ConsoleState.WaitInput && _state.inputReq!.NeedValue)
            {
                // ADR-0031 / spec-v3 M1：无 Geometry 能力的宿主不参与命中判定。
                // 旧行为恒用 (0,0) 调用 MoveMouse —— HeadlessPictureBox.PointToClient 是恒等、
                // EmuRectangle.Contains 又是左闭，于是每次都在左上角产生「幽灵命中」。
                // ⚠️ M4：**仅有能力位还不够**。宿主声明了 Geometry 但还**没上报过任何**坐标时
                // GetCursorPosition() 返回 Empty ⇒ MoveMouse((0,0)) ⇒ 左上角幽灵命中
                // （M1 刚修掉的那个问题会在 CLI 上复活）。故同时要求坐标非空。
                if (_ui.Input.Capabilities.Has(InputCapability.Geometry)
                    && _ui.Input.Current.CursorPosition is { } cursor)
                {
                    EmuPoint point = _ui.MainPicBox.PointToClient(cursor);
                    if (_ui.MainPicBox.ClientRectangle.Contains(point))
                        _console.MoveMouse(point);
                }
            }
            _console.RefreshStrings(true);
        }
    }

    public bool DoInputToEmueraProgram(string str)
    {
        if (_state.State == ConsoleState.WaitInput)
        {
            long inputValue;

            switch (_state.inputReq!.InputType)
            {
                case InputType.IntValue:
                    // TINPUT 超时路径（_state.isTimeout=true）由 EndTimerCore 调 RunEmueraProgram("")
                    // 推进游戏——此时必须用默认值，否则 DoInputToEmueraProgram 返回 false、游戏不推进、
                    // InputTimeoutMs 持续返回 0，RunLoopAsync 死循环（CPU 100%）。
                    // 原 !IsDisplayTimeActive 条件意图：disp=1 时用户按 Enter 不应触发默认值（应等倒计时），
                    // 但超时路径绕过此约束——isTimeout=true 即代表倒计时已结束。
                    if (string.IsNullOrEmpty(str) && _state.inputReq!.HasDefValue && (!_console._timer.IsDisplayTimeActive || _state.isTimeout))
                    {
                        inputValue = _state.inputReq!.DefIntValue;
                        str = inputValue.ToString();
                    }
                    else if (!long.TryParse(str, out inputValue))
                        return false;
                    if (_state.inputReq!.IsSystemInput)
                        _state.process!.InputSystemInteger(inputValue);
                    else
                        _state.process!.InputInteger(inputValue);
                    break;
                case InputType.IntButton:
                    if (string.IsNullOrEmpty(str) && _state.inputReq!.HasDefValue && !_console._timer.IsDisplayTimeActive)
                    {
                        inputValue = _state.inputReq!.DefIntValue;
                        str = inputValue.ToString();
                    }
                    else if (!long.TryParse(str, out inputValue))
                        return false;
                    if (!FindIntegerButton(inputValue))
                        return false;
                    _state.process!.InputInteger(inputValue);
                    break;
                case InputType.StrValue:
                    if (string.IsNullOrEmpty(str) && _state.inputReq!.HasDefValue && !_console._timer.IsDisplayTimeActive)
                        str = _state.inputReq!.DefStrValue;
                    if (str == null)
                        str = "";
                    if (_state.inputReq!.IsSystemInput)
                        _state.process!.InputSystemInteger(_state.inputReq!.DefIntValue);
                    _state.process!.InputString(str);
                    break;
                case InputType.StrButton:
                    if (string.IsNullOrEmpty(str) && _state.inputReq!.HasDefValue && !_console._timer.IsDisplayTimeActive)
                        str = _state.inputReq!.DefStrValue;
                    if (str == null)
                        str = "";
                    if (!FindStringButton(str))
                        return false;
                    _state.process!.InputString(str);
                    break;
                case InputType.AnyValue:
                    if (long.TryParse(str, out inputValue))
                    {
                        if (_state.inputReq!.IsSystemInput)
                            _state.process!.InputSystemInteger(inputValue);
                        else
                            _state.process!.InputInteger(inputValue);
                    }
                    else
                        _state.process!.InputString(str);
                    break;
            }
            _console._timer.StopTimer();
        }
        _console.Print(str);
        _state.inputed = true;
        _console.PrintFlush(false);
        if (_ui.TextBoxPosChanged)
            _ui.ResetTextBoxPos();
        return true;
    }

    /// <summary>
    /// IntButton 命中：当前代按钮索引 O(1) 命中；未命中回退 escapedParts——
    /// div 逃逸按钮不在 displayLineList，旧逻辑主扫描 stop（遇旧代或扫完）后同样扫 div。
    /// 等价性：旧逻辑倒序扫 displayLineList，遇旧代按钮（Gen != 0 && Gen != last）即停；
    /// Generation 单调递增，旧代行之后不可能再有当前代按钮，故主扫描 = 扫全部当前代按钮，
    /// 与索引（只收 Gen == last 的按钮）等价；同值多按钮保留最新行（覆盖写入）。
    /// 唯一差异：ChangeStr 合并的混合代行（数组内旧代按钮在前）旧逻辑提前拒绝，
    /// 索引按 Gen 过滤后仍接受该行当前代按钮——方向为「新更宽容」，见 ButtonIndex 注释。
    /// </summary>
    internal bool FindIntegerButton(long inputValue)
    {
        _state.buttonIndex.EnsureSynced(_state.displayLineList, _state.lastButtonGeneration);
        if (_state.buttonIndex.TryGetInteger(inputValue, out _))
            return true;
        return ScanEscapedParts(_state, button => button.IsInteger && button.Input == inputValue);
    }

    /// <summary>
    /// StrButton 命中：同上，按字符串匹配（int 按钮按 Input.ToString() 与 Inputs 两种
    /// 拼写进索引，等价旧条件 <c>(IsInteger &amp;&amp; Input.ToString() == str) || Inputs == str</c>）。
    /// </summary>
    internal bool FindStringButton(string str)
    {
        _state.buttonIndex.EnsureSynced(_state.displayLineList, _state.lastButtonGeneration);
        if (_state.buttonIndex.TryGetString(str, out _))
            return true;
        return ScanEscapedParts(_state, button =>
            (button.IsInteger && button.Input.ToString() == str) || button.Inputs == str);
    }

    /// <summary>
    /// escapedParts（div 逃逸按钮）回退扫描，IntButton/StrButton 共享。
    /// 仅在索引未命中时调用（div 按钮不在 displayLineList）；无 Generation 检查，
    /// 与旧逻辑 loopepint/loopepstr 段一致（该路径按钮少，无需索引化）。
    /// </summary>
    private static bool ScanEscapedParts(ConsoleStateData state, Func<ConsoleButtonString, bool> match)
    {
        if (state.escapedParts == null)
            return false;
        foreach (var value in state.escapedParts)
        {
            var ep = value.Value;
            foreach (var part in ep)
            {
                if (part is ConsoleDivPart div)
                {
                    foreach (var line in Enumerable.Reverse(div.Children))
                    {
                        foreach (var button in line.Buttons)
                        {
                            if (match(button))
                                return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    public void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5)
    {
        _state.process!.InputResult5(type, result1, result2, result3, result4, result5);
        _state.inProcess = true;
        try
        {
            _console.RunEmueraProgram(null);
            if (_state.State == ConsoleState.WaitInput && _state.inputReq!.NeedValue)
            {
                // ADR-0031 / spec-v3 M1：无 Geometry 能力的宿主不参与命中判定。
                // 旧行为恒用 (0,0) 调用 MoveMouse —— HeadlessPictureBox.PointToClient 是恒等、
                // EmuRectangle.Contains 又是左闭，于是每次都在左上角产生「幽灵命中」。
                // ⚠️ M4：**仅有能力位还不够**。宿主声明了 Geometry 但还**没上报过任何**坐标时
                // GetCursorPosition() 返回 Empty ⇒ MoveMouse((0,0)) ⇒ 左上角幽灵命中
                // （M1 刚修掉的那个问题会在 CLI 上复活）。故同时要求坐标非空。
                if (_ui.Input.Capabilities.Has(InputCapability.Geometry)
                    && _ui.Input.Current.CursorPosition is { } cursor)
                {
                    EmuPoint point = _ui.MainPicBox.PointToClient(cursor);
                    if (_ui.MainPicBox.ClientRectangle.Contains(point))
                        _console.MoveMouse(point);
                }
            }
        }
        finally
        {
            _state.inProcess = false;
        }
        _console.RefreshStrings(true);
    }

    public void MouseWheel(EmuPoint point, int delta)
    {
        if (!_console.IsWaitingPrimitive)
            return;
        EmuPoint clientPoint = new(point.X, point.Y - _ui.ClientHeight);
        InputMouseKey(2, delta, clientPoint.X, clientPoint.Y, 0, 0);
    }

    public void MouseDown(EmuPoint point, int button)
    {
        if (!_console.IsWaitingPrimitive)
            return;
        EmuPoint clientPoint = new(point.X, point.Y - _ui.ClientHeight);
        int buttonNum = -1;
        if (_state.selectingButton != null)
        {
            if (!_state.selectingButton.IsInteger)
            {
                GlobalStatic.VEvaluator.RESULTS = _state.selectingButton.Inputs;
                InputMouseKey(1, (int)button, clientPoint.X, clientPoint.Y, buttonNum, 0);
            }
            else
                InputMouseKey(1, (int)button, clientPoint.X, clientPoint.Y, buttonNum, _state.selectingButton.Input);
        }
        else
            InputMouseKey(1, (int)button, clientPoint.X, clientPoint.Y, buttonNum, 0);
    }

    public void PressPrimitiveKey(int keycode, int keydata, int keymod)
    {
        if (_console.IsWaitingPrimitive)
            InputMouseKey(3, (int)keycode, (int)keydata, 0, 0, 0);
    }

    public bool MoveMouse(EmuPoint point)
    {
        _state.selectingCBGButtonInt = -1;
        ConsoleButtonString? select = null;
        ConsoleButtonString? pointing = null;
        int prevPointingStringsLen = _state.pointingStrings.Count;
        _state.pointingStrings.Clear();
        bool firstPointngSelected = false;
        bool canSelect = false;

        if (_state.State == ConsoleState.Error)
            canSelect = true;
        else if (_state.State == ConsoleState.WaitInput && _state.inputReq!.NeedValue)
            canSelect = true;
        if (_state.inProcess && _state.AlwaysRefresh == false)
            goto end;

        int pointX = point.X;
        int pointY = point.Y;
        ConsoleDisplayLine curLine;

        // ADR-0031 / spec-v3 M3（引擎-9）：视口源改走 IViewportProvider，不再读
        // _ui.ScrollBar / _ui.MainPicBox —— 后者在 CLI 下与真实滚动脱节（缺口 F：
        // CLI 的真实滚动在 ScrollController 里，从未同步回 ScrollBar）。
        IViewportProvider viewport = _ui.Viewport;
        int lineHeight = viewport.LineHeight > 0 ? viewport.LineHeight : Config.LineHeight;
        int lineCount = _state.displayLineList.Count;

        // 宿主报「距底偏移」，行数换算由引擎完成（宿主不持有 displayLineList）。
        int bottomLineNo = lineCount - viewport.ScrollOffset - 1;
        if (lineCount - 1 < bottomLineNo)
            bottomLineNo = lineCount - 1;
        int topLineNo = bottomLineNo - (viewport.VisibleHeight / lineHeight);
        if (topLineNo < 0)
            topLineNo = 0;
        int relPointY = pointY - viewport.VisibleHeight;

        if (_state.escapedParts == null || _state.escapedParts.Count == 0)
            ConsoleEscapedParts.GetPartsInRange(topLineNo, bottomLineNo, (int)_state.lastButtonGeneration, _state.escapedParts!);

        var edepth = _state.escapedParts == null || _state.escapedParts.Keys.Count == 0 ? [0] : _state.escapedParts.Keys.ToArray();
        Array.Sort(edepth);
        int eidx = 0;
        bool zeroTested = false;
        var bottomLineBase = viewport.VisibleHeight - lineHeight;
        while (eidx < edepth.Length)
        {
            var depth = edepth[eidx];
            if (!zeroTested && depth >= 0)
            {
                depth = 0;
                zeroTested = true;
                for (int i = bottomLineNo; i >= topLineNo; i--)
                {
                    relPointY += lineHeight;
                    curLine = _state.displayLineList[i];
                    for (int b = 0; b < curLine.Buttons.Length; b++)
                    {
                        ConsoleButtonString button = curLine.Buttons[curLine.Buttons.Length - b - 1];
                        if (button == null || button.StrArray == null)
                            continue;
                        if ((button.PointX <= pointX) && (button.PointX + button.Width >= pointX))
                        {
                            foreach (AConsoleDisplayNode part in button.StrArray)
                            {
                                if (part == null || part is ConsoleDivPart)
                                    continue;
                                if ((part.PointX <= pointX) && (part.PointX + part.Width >= pointX)
                                    && (relPointY >= part.Top) && (relPointY <= part.Bottom))
                                {
                                    if (!firstPointngSelected)
                                        pointing = button;
                                    if (button.IsButton)
                                    {
                                        if (!canSelect)
                                            goto breakfor;
                                        _state.pointingStrings.Add(button);
                                        firstPointngSelected = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if (firstPointngSelected && bottomLineNo - i > 100)
                        break;
                }
            }
            if (eidx < edepth.Length && edepth[eidx] == depth)
            {
                var correction = _state.lineNo > Config.MaxLog ? _state.lineNo - Config.MaxLog : 0;
                if (_state.escapedParts != null && (depth != 0 || _state.escapedParts.ContainsKey(depth)))
                    foreach (var part in _state.escapedParts[depth])
                    {
                        if (part is ConsoleDivPart div)
                        {
                            var lineY = bottomLineBase + (div.Parent.ParentLine.LineNo - bottomLineNo - correction) * lineHeight;
                            var childPointing = div.TestChildHitbox(pointX, pointY, lineY);
                            if (childPointing != null)
                            {
                                pointing = childPointing;
                                if (pointing.IsButton)
                                    goto breakfor;
                            }
                        }
                        else if ((part.PointX <= pointX) && (part.PointX + part.Width >= pointX)
                            && (relPointY >= part.Top) && (relPointY <= part.Bottom))
                        {
                            pointing = part.Parent;
                            if (pointing.IsButton)
                                goto breakfor;
                        }
                    }
                eidx++;
            }
        }

    breakfor:
        if (_state.pointingStrings.Count > 0)
        {
            foreach (var p in _state.pointingStrings)
            {
                if ((p == null) || (p.Generation != _state.lastButtonGeneration))
                    continue;
                else if (!p.IsButton)
                    continue;
                else if (_state.State == ConsoleState.WaitInput && !p.IsInteger)
                {
                    if ((_state.inputReq!.InputType == InputType.IntValue) || (_state.inputReq!.InputType == InputType.IntButton))
                        continue;
                }
                pointing = p;
                goto end;
            }
            canSelect = false;
        }
        else
        {
            if ((pointing == null) || (pointing.Generation != _state.lastButtonGeneration))
                canSelect = false;
            else if (!pointing.IsButton)
                canSelect = false;
            else if (_state.State == ConsoleState.WaitInput && !pointing.IsInteger)
            {
                if ((_state.inputReq!.InputType == InputType.IntValue) || (_state.inputReq!.InputType == InputType.IntButton))
                    canSelect = false;
            }
        }
    end:
        if (canSelect)
            select = pointing;
        bool needRefresh = select != _state.selectingButton || pointing != _state.pointingString || _state.pointingStrings.Count != prevPointingStringsLen;
        _state.pointingString = pointing;
        _state.selectingButton = select;
        return needRefresh;
    }

    public void LeaveMouse()
    {
        bool needRefresh = _state.selectingButton != null || _state.pointingString != null;
        _state.selectingButton = null;
        _state.pointingString = null;
        _state.pointingStrings.Clear();
        if (needRefresh)
            _console.RefreshStrings(true);
    }

    public void NewGeneration()
    {
        if (_state.State != ConsoleState.WaitInput || !_state.inputReq!.NeedValue)
            return;
        if (!_state.updatedGeneration && _state.process!.getCurrentLine != _state.lastInputLine)
            _state.lastButtonGeneration = _state.newButtonGeneration;
        else
            _state.updatedGeneration = false;
        _state.lastInputLine = _state.process!.getCurrentLine;
        switch (_state.inputReq!.InputType)
        {
            case InputType.IntValue:
            case InputType.IntButton:
                if (_state.lastButtonGeneration == _state.newButtonGeneration)
                    unchecked { _state.newButtonGeneration++; }
                else if (!_state.lastButtonIsInput)
                    _state.lastButtonGeneration = _state.newButtonGeneration;
                _state.lastButtonIsInput = true;
                break;
            case InputType.StrValue:
            case InputType.AnyValue:
            case InputType.StrButton:
                if (_state.lastButtonGeneration == _state.newButtonGeneration)
                    unchecked { _state.newButtonGeneration++; }
                else if (_state.lastButtonIsInput)
                    _state.lastButtonGeneration = _state.newButtonGeneration;
                _state.lastButtonIsInput = false;
                break;
        }
    }

    public void ForceUpdateGeneration()
    {
        _state.newButtonGeneration++;
        _state.lastButtonGeneration = _state.newButtonGeneration;
        _state.updatedGeneration = true;
    }

    public void UpdateGeneration()
    {
        _state.lastButtonGeneration = _state.newButtonGeneration;
        _state.updatedGeneration = true;
    }

    public void CollectCurrentButtons(List<ConsoleButtonString> result)
    {
        var lines = _state.displayLineList;
        if (lines == null || lines.Count == 0)
            return;
        long currentGen = _state.lastButtonGeneration;
        foreach (var line in lines)
        {
            if (line?.Buttons == null) continue;
            foreach (var btn in line.Buttons)
            {
                if (btn == null || !btn.IsButton) continue;
                if (btn.Generation != currentGen) continue;
                result.Add(btn);
            }
        }
    }

    public void SetSelectingButton(ConsoleButtonString? button) => _state.selectingButton = button;

    // --- Static helpers ---

    static readonly string[] Spliter = ["\\n", "\r\n", "\n", "\r"];

    static string ParseInput(CharStream st, bool isNest)
    {
        StringBuilder sb = new(20);
        StringBuilder num = new(20);
        bool hasRet = false;
        int res;
        while (!st.EOS && (!isNest || st.Current != ')'))
        {
            if (st.Current == '(')
            {
                st.ShiftNext();
                string tstr = ParseInput(st, true);
                if (!st.EOS)
                {
                    st.ShiftNext();
                    if (st.Current == '*')
                    {
                        st.ShiftNext();
                        while (char.IsNumber(st.Current))
                        {
                            num.Append(st.Current);
                            st.ShiftNext();
                        }
                        if (num.Length > 0)
                        {
                            if (!int.TryParse(num.ToString(), out res))
                                res = 0;
                            for (int i = 0; i < res; i++)
                                sb.Append(tstr);
                            num.Remove(0, num.Length);
                        }
                    }
                    else
                        sb.Append(tstr);
                    continue;
                }
                else
                {
                    sb.Append(tstr);
                    break;
                }
            }
            else if (st.Current == '\\')
            {
                st.ShiftNext();
                switch (st.Current)
                {
                    case 'n':
                        if (!hasRet)
                            sb.Append('\n');
                        else
                            hasRet = false;
                        break;
                    case 'r':
                        sb.Append('\r');
                        break;
                    case 'e':
                        sb.Append("\\e\n");
                        hasRet = true;
                        break;
                    case '\n':
                        break;
                    default:
                        sb.Append(st.Current);
                        break;
                }
            }
            else
                sb.Append(st.Current);
            st.ShiftNext();
        }
        return sb.ToString();
    }

    const string ErrorButtonsText = "__openFileWithDebug__";
}
