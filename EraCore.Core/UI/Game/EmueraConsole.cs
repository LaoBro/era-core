using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameView;

internal sealed class EmueraConsole : IDisposable, IConsoleStateView, IButtonDisplayContext
{
    internal readonly ConsoleStateData _state;
    internal readonly ConsoleStateManager _stateManager;
    internal readonly ConsolePrintManager _printManager;
    internal readonly ConsoleInputHandler _inputHandler;
    internal readonly ConsoleTimerManager _timer;
    internal readonly ConsoleRefreshHandler _refresh;
    private readonly IConsoleUI _uiAdapter;
    private readonly ITerminalSetup _terminalSetup;

    // --- Shared state objects required by PrintStringBuffer/ConsoleButtonString ---
    internal readonly PrintStringBuffer printBuffer;
    internal readonly StringMeasure stringMeasure = new();

    internal EmueraConsole(IConsoleUI ui, ITerminalSetup terminalSetup)
    {
        _uiAdapter = ui;
        _terminalSetup = terminalSetup;
        _state = new ConsoleStateData();
        _stateManager = new ConsoleStateManager(_state, this, ui);
        _printManager = new ConsolePrintManager(_state, this, ui);
        _inputHandler = new ConsoleInputHandler(_state, this, ui);
        _timer = new ConsoleTimerManager(_state, this, ui);
        _refresh = new ConsoleRefreshHandler(_state, this, ui);

        // PrintStringBuffer needs EmueraConsole as parent
        printBuffer = new PrintStringBuffer(this);

        _state.displayLineList = [];
    }

    // ========================================
    // State & Properties (direct delegation to _state)
    // ========================================

    internal ConsoleState State => _state.State;
    internal InputRequest? CurrentRequest => _state.inputReq;
    internal IConsoleUI UIAdapter => _uiAdapter;
    internal bool Enabled => _uiAdapter.Created;
    internal bool IsActive => _uiAdapter.IsActive;
    internal bool MesSkip { get => _state.MesSkip; set => _state.MesSkip = value; }
    internal InputRequest? inputReq { get => _state.inputReq; set => _state.inputReq = value; }
    internal bool IsTimeOut => _state.isTimeout;
    internal InputType NowInputType => _state.inputReq!.InputType;
    internal List<ConsoleDisplayLine> DisplayLineList => _state.displayLineList;
    internal PrintStringBuffer PrintBuffer => printBuffer;
    internal Dictionary<int, List<AConsoleDisplayNode>>? EscapedParts => _state.escapedParts;
    internal int GetLineNo => _state.lineNo;
    internal long LineCount => _state.logicalLineCount;
    internal long DeletedLines => _state.deletedLines;
    internal ConsoleButtonString? SelectingButton => _state.selectingButton;
    ConsoleButtonString? IButtonDisplayContext.SelectingButton => _state.selectingButton;
    TerminalCharWidthConfig IButtonDisplayContext.CharWidthConfig => _state.CharWidthConfig;
    internal ConsoleButtonString? PointingSring => _state.pointingString;
    internal bool AlwaysRefresh { get => _state.AlwaysRefresh; set => _state.AlwaysRefresh = value; }
    internal bool RunERBFromMemory { get => _state.runningERBfromMemory; set => _state.runningERBfromMemory = value; }
    internal bool LastLineIsTemporary => _printManager.LastLineIsTemporary;
    internal bool LastLineIsEmpty => _printManager.LastLineIsEmpty;
    internal bool EmptyLine => printBuffer.IsEmpty;
    internal bool noOutputLog { get => _state.noOutputLog; set => _state.noOutputLog = value; }
    internal EmuColor bgColor { get => _state.bgColor; set => _state.bgColor = value; }
    internal bool UseUserStyle { get => _state.UseUserStyle; set => _state.UseUserStyle = value; }
    internal bool UseSetColorStyle { get => _state.UseSetColorStyle; set => _state.UseSetColorStyle = value; }
    internal StringStyle StringStyle => _state.userStyle;
    internal DisplayLineAlignment Alignment { get => _state.alignment; set => _state.alignment = value; }
    internal bool UpdatedGeneration { get => _state.updatedGeneration; set => _state.updatedGeneration = value; }
    internal int LastButtonGeneration => (int)_state.lastButtonGeneration;
    internal int NewButtonGeneration => (int)_state.newButtonGeneration;
    internal ConsoleRedraw Redraw => _state.redraw;
    internal StringMeasure StrMeasure => stringMeasure;
    /// <summary>
    /// 脚本层唯一的「是否还在运行」判据（被 <c>Process.DoScript</c> 与 <c>ScriptProc.Run</c> 消费）。
    ///
    /// 返回 false 的两类情形：
    /// <list type="bullet">
    /// <item><b>拆除请求</b>（issue-002，<see cref="RequestStop"/> 置位）—— 脚本应在下一个指令边界停下。</item>
    /// <item><b>让路点</b>（等待输入 / 休眠）—— 脚本在同一次 DoScript 里先行返回，
    /// 由下一次输入唤醒时再从断点续跑。此分支的返回值与改动前逐位相同，故既有的
    /// 让路语义不受本次改动影响。</item>
    /// </list>
    /// </summary>
    internal bool IsRunning
    {
        get
        {
            if (_state.StopRequested)
                return false;
            if (_state.State == ConsoleState.Initializing) return true;
            return _state.State == ConsoleState.Running || _state.runningERBfromMemory;
        }
    }
    internal bool IsInProcess
    {
        get
        {
            if (_state.State == ConsoleState.Initializing) return true;
            if (_state.inProcess) return true;
            return _state.State == ConsoleState.Running || _state.runningERBfromMemory;
        }
    }
    internal bool IsError => _state.State == ConsoleState.Error;
    internal bool IsWaitingEnterKey
    {
        get
        {
            if (_state.State == ConsoleState.Quit || _state.State == ConsoleState.Error)
            {
                GlobalStatic.ForceQuitAndRestart = false;
                return true;
            }
            if (_state.State == ConsoleState.WaitInput)
            {
                GlobalStatic.ForceQuitAndRestart = false;
                return _state.inputReq!.InputType == InputType.AnyKey || _state.inputReq!.InputType == InputType.EnterKey;
            }
            return false;
        }
    }
    internal bool IsWaitAnyKey
    {
        get
        {
            GlobalStatic.ForceQuitAndRestart = false;
            return _state.State == ConsoleState.WaitInput && _state.inputReq?.InputType == InputType.AnyKey;
        }
    }
    internal bool IsWaintingOnePhrase => _state.State == ConsoleState.WaitInput && _state.inputReq?.OneInput == true;
    internal bool IsWaintingInputWithMouse => _state.State == ConsoleState.WaitInput && _state.inputReq?.MouseInput == true;
    internal bool IsRunningTimer => _state.State == ConsoleState.WaitInput && (_state.inputReq?.Timelimit ?? 0) > 0 && !_state.isTimeout;
    internal bool IsWaitingPrimitive => _state.State == ConsoleState.WaitInput && _state.inputReq?.InputType == InputType.PrimitiveMouseKey;
    internal bool ButtonIsSelected(ConsoleButtonString button) => _state.selectingButton == button;
    internal bool ButtonIsPointing(ConsoleButtonString button) => _state.pointingStrings.Contains(button);
    internal int ClientWidth => _uiAdapter.ClientWidth;
    internal int ClientHeight => _uiAdapter.ClientHeight;
    internal string? SelectedString
    {
        get
        {
            if (_state.selectingButton == null) return null;
            if (_state.State == ConsoleState.Error) return _state.selectingButton.Inputs;
            if (_state.State != ConsoleState.WaitInput) return null;
            if ((_state.inputReq!.InputType == InputType.IntValue || _state.inputReq!.InputType == InputType.IntButton) && _state.selectingButton.IsInteger)
                return _state.selectingButton.Input.ToString();
            if (_state.inputReq!.InputType == InputType.StrValue || _state.inputReq!.InputType == InputType.StrButton)
                return _state.selectingButton.Inputs;
            if (_state.inputReq!.InputType == InputType.AnyValue && _state.selectingButton.IsInteger)
                return _state.selectingButton.Input.ToString();
            if (_state.inputReq!.InputType == InputType.AnyValue)
                return _state.selectingButton.Inputs;
            return null;
        }
    }

    // ========================================
    // Lifecycle delegation
    // ========================================

    internal System.Threading.Tasks.Task Initialize() => _stateManager.Initialize();
    internal void Quit() => _stateManager.Quit();
    /// <summary>
    /// issue-002：请求停止脚本执行 —— 把会话拆除信号送达脚本层的唯一入口。
    /// 效果：<see cref="IsRunning"/> 从此返回 false，脚本在下一条指令边界停下。
    /// 幂等、不等待、不触碰显示/输入状态机（见 ConsoleStateData.stopRequested 的注释）。
    /// </summary>
    internal void RequestStop() => _stateManager.RequestStop();
    internal void ForceQuit() => _stateManager.ForceQuit();
    internal void ThrowTitleError(bool error) => _stateManager.ThrowTitleError(error);
    internal void ThrowError(bool playSound) => _stateManager.ThrowError(playSound);
    internal void GotoTitle() => _stateManager.GotoTitle();
    internal void GotoTitleAndLoadAndRepeatInput() => _stateManager.GotoTitleAndLoadAndRepeatInput();
    internal System.Threading.Tasks.Task ReloadErb() => _stateManager.ReloadErb();
    internal void ReloadErbFinished() => _stateManager.ReloadErbFinished();
    internal System.Threading.Tasks.Task ReloadPartialErb(List<string> path) => _stateManager.ReloadPartialErb(path);
    internal System.Threading.Tasks.Task ReloadFolder(string erbPath) => _stateManager.ReloadFolder(erbPath);
    internal void ReloadResource() => _stateManager.ReloadResource();
    internal void Await(int time) => _stateManager.Await(time);

    // ========================================
    // Input delegation
    // ========================================

    internal void WaitInput(InputRequest req) => _inputHandler.WaitInput(req);

    /// <summary>
    /// NF 变体等待（实现票 21，TINPUTNF 族）。Skia 上游此处跳过「强制滚动到底」一步；
    /// era-core 的等待本就不含该步，故 NF 与标准等待等价——语义接缝保留，观感差异归渲染路线。
    /// </summary>
    internal void WaitInputNoFocus(InputRequest req) => _inputHandler.WaitInput(req);
    internal void ReadAnyKey(bool anykey = false, bool stopMesskip = false) => _inputHandler.ReadAnyKey(anykey, stopMesskip);
    internal void PressEnterKey(bool keySkip, string input, bool changedByMouse) => _inputHandler.PressEnterKey(keySkip, input, changedByMouse);
    internal void InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5) => _inputHandler.InputMouseKey(type, result1, result2, result3, result4, result5);
    internal void MouseWheel(EmuPoint point, int delta) => _inputHandler.MouseWheel(point, delta);
    internal void MouseDown(EmuPoint point, int button) => _inputHandler.MouseDown(point, button);
    internal void PressPrimitiveKey(int keycode, int keydata, int keymod) => _inputHandler.PressPrimitiveKey(keycode, keydata, keymod);
    internal bool MoveMouse(EmuPoint point) => _inputHandler.MoveMouse(point);
    internal void LeaveMouse() => _inputHandler.LeaveMouse();
    internal void SetSelectingButton(ConsoleButtonString? button) => _inputHandler.SetSelectingButton(button);
    internal List<ConsoleButtonString> CollectCurrentButtons()
    {
        var result = new List<ConsoleButtonString>();
        _inputHandler.CollectCurrentButtons(result);
        return result;
    }

    // ========================================
    // Print delegation
    // ========================================

    internal void Print(string str, bool lineEnd = true) => _printManager.Print(str, lineEnd);
    internal void PrintSingleLine(string str) => _printManager.PrintSingleLine(str);
    internal void PrintSingleLine(string str, bool temporary) => _printManager.PrintSingleLine(str, temporary);
    internal void PrintC(string str, bool alignmentRight) => _printManager.PrintC(str, alignmentRight);
    internal void PrintFlush(bool force) => _printManager.PrintFlush(force);
    internal void PrintSystemLine(string str) => _printManager.PrintSystemLine(str);
    internal void PrintError(string str) => _printManager.PrintError(str);
    internal void PrintErrorButton(string str, ScriptPosition? pos, int level = 0) => _printManager.PrintErrorButton(str, pos, level);
    internal void PrintWarning(string str, ScriptPosition? position, int level) => _printManager.PrintWarning(str, position, level);
    internal void PrintTemporaryLine(string str) => _printManager.PrintTemporaryLine(str);
    internal void PrintBar() => _printManager.PrintBar();
    internal void printCustomBar(string barStr, bool isConst) => _printManager.PrintCustomBar(barStr, isConst);
    internal void PrintButton(string str, string p) => _printManager.PrintButton(str, p);
    internal void PrintButton(string str, long p) => _printManager.PrintButton(str, p);
    internal void PrintButtonC(string str, string p, bool isRight) => _printManager.PrintButtonC(str, p, isRight);
    internal void PrintButtonC(string str, long p, bool isRight) => _printManager.PrintButtonC(str, p, isRight);
    internal void PrintPlain(string str) => _printManager.PrintPlain(str);
    internal void PrintHtml(string str, bool toPrintBuffer) => _printManager.PrintHtml(str, toPrintBuffer);
    internal void PrintHtmlC(string str, bool alignRight, int cellWidth) => _printManager.PrintHtmlC(str, alignRight, cellWidth);
    internal void PrintHTMLIsland(string html) => _printManager.PrintHTMLIsland(html);
    internal void ClearHTMLIsland() => _printManager.ClearHTMLIsland();
    internal void PrintImg(string name, string? nameb, string? namem, Utils.MixedNum? height, Utils.MixedNum? width, Utils.MixedNum? ypos) => _printManager.PrintImg(name, nameb, namem, height, width, ypos);
    internal void PrintShape(string type, Utils.MixedNum[] param) => _printManager.PrintShape(type, param);
    // --- 背景图（issue 02） ---
    internal List<BgImageState> BgImages => _state.bgImages;
    internal void SetBgImage(string name, long depth, float opacity) => _printManager.SetBgImage(name, depth, opacity);
    internal void RemoveBgImage(string name) => _printManager.RemoveBgImage(name);
    internal void ClearBgImage() => _printManager.ClearBgImage();
    // --- 图像层（实现票 22，语义接受） ---
    // Skia 上游的 SETIMAGELAYER 族落到独立图层管线（ImageLayerManager）。era-core 无 ImageLayer 抽象
    // （只有上面的 CBG* 精灵管线），故此处只做**语义接缝**：参数已完整解析并校验，交由 era-core 渲染路线
    // 日后消费；当前实现为 no-op。观感差异属「已知差异清单」（spec §5），不在本 effort 追平。
    internal void SetImageLayer(string spriteName, long depth, int x, int y, int width, int height, int opacity, float[][]? colorMatrix, bool followScroll)
    {
    }
    internal void ClearImageLayer(long depth)
    {
    }
    internal void ClearImageLayerAll()
    {
    }
    // --- 文本背景色（实现票 23，语义接受） ---
    /// <summary>
    /// TEXT_BGC_ON/OFF 的语义状态（RGB + 透明度）。era-core 渲染线暂未消费 → 「已知差异」（spec §5）；
    /// 语义开关在此接缝落地，供未来渲染路线 / 快照消费。
    /// </summary>
    internal EmuColor? TextBackgroundColor
    {
        get => _state.textBackgroundColor;
        set => _state.textBackgroundColor = value;
    }
    internal ConsoleDisplayLine[]? GetDisplayLines(long lineNo) => _printManager.GetDisplayLines(lineNo);
    internal ConsoleDisplayLine[]? PopDisplayingLines() => _printManager.PopDisplayingLines();
    internal ConsoleDisplayLine? PrintPlainwithSingleLine(string str) => _printManager.PrintPlainwithSingleLine(str);
    internal void PrintPlainWithSingleLineFix(string str) => _printManager.PrintPlainWithSingleLineFix(str);
    internal ConsoleDisplayLine? BufferToSingleLine(bool force, bool temporary) => _printManager.BufferToSingleLine(force, temporary);
    internal void ClearText() => _printManager.ClearText();

    // ========================================
    // Line management
    // ========================================

    internal void ClearDisplay() => _printManager.ClearDisplay();
    internal void deleteLine(int argNum) => _printManager.DeleteLine(argNum);
    internal void changeLastLine(string str) { _printManager.DeleteLine(1); _printManager.PrintSingleLine(str, false); }
    internal void ChangeLastLine(string str) => changeLastLine(str);
    internal void ForceStopTimer() => _timer.ForceStopTimer();
    internal void NewLine() { PrintFlush(true); RefreshStrings(false); }

    // ========================================
    // Style delegation
    // ========================================

    internal void SetStringStyle(EmuFontStyle fs) => _printManager.SetStringStyle(fs);
    internal void SetStringStyle(EmuColor color) => _printManager.SetStringStyle(color);
    internal void SetFont(string fontname) => _printManager.SetFont(fontname);
    internal void ResetStyle() => _printManager.ResetStyle();
    internal void SetBgColor(EmuColor color) => _printManager.SetBgColor(color);

    // ========================================
    // Bar / StBar delegation
    // ========================================

    internal string getStBar(string barStr) => _printManager.GetStBar(barStr);
    internal string? getDefStBar() => _printManager.GetDefStBar();
    internal void setStBar(string barStr) => _printManager.SetStBar(barStr);

    // ========================================
    // Log delegation
    // ========================================

    internal bool OutputLog(string? filename, bool hideInfo, bool showFailure = true) => _printManager.OutputLog(filename, hideInfo, showFailure);
    internal bool OutputSystemLog(string? filename) => _printManager.OutputSystemLog(filename);
    internal string GetLog(bool hideInfo) => _printManager.GetLog(hideInfo);

    // ========================================
    // Timer delegation
    // ========================================

    internal void SubmitTimeout() => _timer.SubmitTimeout();
    /// <summary>
    /// 让路点（<c>InputRequest.IsYield</c>）的续跑入口 —— <b>宿主侧唯一应调用的引擎侧入口</b>。
    /// 宿主识别出让路点后应交付一帧，再调用本方法在同线程续跑；<b>不要直接摸内部计时器</b>
    /// （保持 facade 分层，spec §5.1b W1-4）。语义见 <c>ConsoleTimerManager.EndYieldCore</c>：
    /// 与超时续跑不同，它不留下任何「这次是超时」的痕迹。
    /// </summary>
    internal void EndYield() => _timer.EndYieldCore();
    internal long? InputTimeoutMs => _timer.InputTimeoutMs;
    internal long InputTimelimit => _timer.InputTimelimit;
    internal bool IsDisplayTimeActive => _timer.IsDisplayTimeActive;
    internal string? TimeUpMessage => _timer.TimeUpMessage;
    internal string BuildCountdownText(long elapsedMs) => _timer.BuildCountdownText(elapsedMs);
    internal void setRedrawTimer(int tickcount) => _timer.SetRedrawTimer(tickcount);
    internal int getRedrawTimer() => _timer.GetRedrawTimer();
    internal void forceStopTimer() => _timer.ForceStopTimer();

    // ========================================
    // Refresh delegation
    // ========================================

    internal void RefreshStrings(bool force_Paint) => _refresh.RefreshStrings(force_Paint);
    internal void SetRedraw(long i) => _refresh.SetRedraw(i);
    internal void VerticalScrollBarUpdate() => _refresh.VerticalScrollBarUpdate();

    // ========================================
    // RunEmueraProgram / newGeneration (core game loop)
    // ========================================

    internal void RunEmueraProgram(string? input)
    {
        if (input != null)
        {
            if (!_inputHandler.DoInputToEmueraProgram(input))
                return;
            if (_state.State == ConsoleState.Error)
                return;
        }
        // issue-002：拆除已请求停止 —— 不得再把 State 拉回 Running 去推进脚本。
        // 少了这道守卫的话，DoScript 会空跑一趟立刻返回，而收尾的
        // 「跑完仍是 Running ⇒ 判 Error」会把正常拆除染成错误终态
        // （副作用：多打一行错误 + OutputSystemLog 往游戏目录写 emuera.log）。
        if (_state.StopRequested)
            return;
        _state.State = ConsoleState.Running;
        _state.process!.DoScript();
        if (_state.State == ConsoleState.Running)
        {
            _state.State = ConsoleState.Error;
            PrintError(trerror.ProgramStatusError.Text);
        }
        if (_state.State == ConsoleState.Error && !_state.noOutputLog)
            OutputSystemLog("emuera.log");
        PrintFlush(false);
        _inputHandler.NewGeneration();
    }

    internal void ForceUpdateGeneration() => _inputHandler.ForceUpdateGeneration();
    internal void UpdateGeneration() => _inputHandler.UpdateGeneration();

    // ========================================
    // Debug delegation
    // ========================================

    internal void DebugPrint(string str)
    {
        if (!Program.DebugMode) return;
        _state.dConsoleLog.Append(str);
    }
    internal void DebugClear() => _state.dConsoleLog.Remove(0, _state.dConsoleLog.Length);
    internal void DebugNewLine()
    {
        if (!Program.DebugMode) return;
        _state.dConsoleLog.Append(Environment.NewLine);
    }
    internal string DebugConsoleLog => _state.dConsoleLog.ToString();
    internal string? DebugTitle => _state.debugTitle;
    internal void DebugAddTraceLog(string str)
    {
        if (!Program.DebugMode || _state.runningERBfromMemory) return;
        _state.dTraceLogList.Add(str);
    }
    internal void DebugRemoveTraceLog()
    {
        if (!Program.DebugMode || _state.runningERBfromMemory) return;
        if (_state.dTraceLogList.Count > 0)
            _state.dTraceLogList.RemoveAt(_state.dTraceLogList.Count - 1);
    }
    internal void DebugClearTraceLog()
    {
        if (!Program.DebugMode || _state.runningERBfromMemory) return;
        _state.dTraceLogList.Clear();
    }

    // ========================================
    // Window delegation
    // ========================================

    internal void SetWindowTitle(string str)
    {
        if (Program.DebugMode)
        {
            _state.debugTitle = str;
            _uiAdapter.Text = str + " (Debug Mode)";
        }
        else
            _uiAdapter.Text = str;
    }
    internal string GetWindowTitle()
    {
        if (Program.DebugMode && _state.debugTitle != null)
            return _state.debugTitle;
        return _uiAdapter.Text;
    }
    internal void SetEmueraVersionInfo(string str) => _uiAdapter.TextBox.Text = str;

    // ========================================
    // Mouse helpers
    // ========================================

    internal EmuPoint GetMousePosition() => _uiAdapter.GetMousePosition();

    // ========================================
    // IConsoleStateView implementation (from AgentBridge.cs partial)
    // ========================================

    internal TerminalCharWidthConfig CharWidthConfig
    {
        get => _state.CharWidthConfig;
        set => _state.CharWidthConfig = value;
    }

    public bool ConsumeNeedFullRefresh()
    {
        bool v = _state._needFullRefresh;
        _state._needFullRefresh = false;
        return v;
    }

    public bool IsGameExited => State is ConsoleState.Quit or ConsoleState.Error;

    public int DisplayLineCount => DisplayLineList.Count;

    // ========================================
    // Pending ops management
    // ========================================

    /// <summary>
    /// 当前待处理 op 数量（peek，不 drain）。
    /// DisplayState.TryUpdate 用此作为权威变更信号——引擎每次显示变更都向 _pendingOps Enqueue
    /// （ClearOp/ClearLineOp/SetBgOp/PrintOp/NewLineOp，见 ConsolePrintManager.cs:65/174/549/570/647）。
    /// 单调无关（不受 CLEARLINE LineNo 回退影响），且捕捉原地末行编辑（会 Enqueue(PrintOp)）。
    /// Phase 5-3：_pendingOps 改为 ConcurrentQueue，Count/Enqueue/Clear 跨线程安全。
    /// </summary>
    internal int PendingOpCount => _state._pendingOps.Count;

    /// <summary>
    /// ADR-0022（清空信号溯源）：drain 全部 _pendingOps 并原样返回，供 DisplayState
    /// 累积出权威清空事件（ClearOp/ClearLineOp/SetBgOp）。ConcurrentQueue.TryDequeue 跨线程安全。
    /// 原 <c>ClearPendingOps</c>（直接丢弃）已随 ADR-0022 移除——消费方统一走本方法累积，
    /// 不再有"二选一"的重复 drain 语义。
    /// </summary>
    internal List<TurnOp> DrainPendingOps()
    {
        var list = new List<TurnOp>();
        while (_state._pendingOps.TryDequeue(out var op))
            list.Add(op);
        return list;
    }

    // ========================================
    // Agent / Bridge
    // ========================================

    internal AgentProtocolBase? AgentBridge => _state.agentBridge;
    internal void SetAgentBridge(AgentProtocolBase protocol) => _state.agentBridge = protocol;

    // ========================================
    // Button generation helpers
    // ========================================

    internal void forceUpdateGeneration() => _inputHandler.ForceUpdateGeneration();
    internal bool bitmapCacheEnabledForNextLine { get => _state.bitmapCacheEnabledForNextLine; set => _state.bitmapCacheEnabledForNextLine = value; }
    internal bool updatedGeneration { get => _state.updatedGeneration; set => _state.updatedGeneration = value; }

    // ========================================
    // Dispose
    // ========================================

    public void Dispose()
    {
        _stateManager.Dispose();
    }
}
