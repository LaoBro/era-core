using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.UI.Game;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// Lifecycle management: Initialize, Quit, ForceQuit, ThrowError, ThrowTitleError,
/// GotoTitle, ReloadErb, ReloadPartialErb, ReloadFolder, ReloadResource, Dispose.
/// </summary>
internal sealed class ConsoleStateManager
{
    private readonly ConsoleStateData _state;
    private readonly EmueraConsole _console;
    private readonly IConsoleUI _ui;

    internal ConsoleStateManager(ConsoleStateData state, EmueraConsole console, IConsoleUI ui)
    {
        _state = state;
        _console = console;
        _ui = ui;
    }

    public async Task Initialize()
    {
        var boottimeDebugStopwatch = System.Diagnostics.Stopwatch.StartNew();
        StreamWriter? logWriter = null;
        try
        {
            if (Config.DisplayReport)
            {
                string timeLogPath = SafCompat.CombinePath(Program.ExeDir, "time.log");
                logWriter = new StreamWriter(SafCompat.OpenWrite(timeLogPath));
            }
        }
        catch (Exception ex)
        {
            EmueraLog.Warn("time.log", $"Failed to open initialization log: {ex}");
            ParserMediator.Warn(trerror.TimeLogFileLocked.Text, null, 0);
        }
        try
        {
            logWriter?.WriteLine("Init:Start");
			_state._genericTimerStopwatch.Restart();

		// issue 09：内容 Preload 已前移至 Process.Initialize（RenameDic 载毕后）并随 IR 命中判定门控——
		// 命中轮跳过内容读取，只留清单枚举。此处仅清空跨会话 stale 缓存（File:Preload 标记随预载段移动）。
		Preload.Clear();

		GlobalStatic.Console = _console;
		_state.process = new Process(_console);
		GlobalStatic.Process = _state.process!;
		if (Program.DebugMode && Config.DebugShowWindow)
		{
			// OpenDebugDialog: WinForms-only stub removed in Headless
			_ui.Focus();
		}
		_console.ClearDisplay();
		var env = new LoaderEnv(Program.CsvDir, Program.ErbDir, Program.AnalysisMode, Program.AnalysisFiles, Program.DebugMode)
		{
			DirAccessor = GamePaths.Current.DirAccessor
		};
		if (!await _state.process!.Initialize(env, logWriter))
        {
            EmueraLog.Error("csm", "Process.Initialize returned false");
            _state.State = ConsoleState.Error;
            _console.OutputLog(null, false, showFailure: false);
            _console.PrintFlush(false);
            _console.RefreshStrings(true);
            return;
        }
        EmueraLog.Info("csm", "Process.Initialize OK");
        _console.RunEmueraProgram("");
        _console.RefreshStrings(true);

            logWriter?.WriteLine("Init:End " + boottimeDebugStopwatch.ElapsedMilliseconds + "ms");
        }
        catch (Exception ex)
        {
            // 失败可见切片（handoff-nativeaot-parallel-erb §6.2）：初始化期任何 C# 异常统一转为
            // Error 终态而非穿透。issue 09 后 Preload 已移入 Process.Initialize 保护圈内（异常由
            // Process.Initialize catch → handleException → 返回 false → 走上方 false 分支），
            // 本 catch 覆盖圈外剩余段（env 装配 / 控制台初始化 / RunEmueraProgram 等）。
            // 与 "Process.Initialize 返回 false" 分支同构：置 State、输出系统行、刷控制台。
            // Session 层 catch 仅作最后防线，两层都不静默吞异常（照记 AgentLog/EmueraLog）。
            EmueraLog.Error("csm", $"Initialize EXCEPTION: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            _state.State = ConsoleState.Error;
            _console.PrintSystemLine($"[Initialize] unexpected error: {ex.GetType().Name}: {ex.Message}");
            _console.OutputLog(null, false, showFailure: false);
            _console.PrintFlush(false);
            _console.RefreshStrings(true);
        }
        finally
        {
            try
            {
                logWriter?.Dispose();
            }
            catch (Exception ex)
            {
                EmueraLog.Warn("time.log", $"Failed to close initialization log: {ex}");
            }
        }
    }

    /// <summary>
    /// issue-002：请求停止脚本执行。这只是转落到共享状态上 —— <b>不</b>改动
    /// <see cref="ConsoleState.State"/>，理由见 ConsoleStateData.stopRequested 的注释。
    /// </summary>
    public void RequestStop() => _state.RequestStop();

    public void Quit() => _state.State = ConsoleState.Quit;

    public void ForceQuit()
    {
        if (GlobalStatic.ForceQuitAndRestart == true)
        {
            EmueraLog.Error("csm", trmb.ForceQuitAndRestart.Text);
            Program.rebootFlag = false;
            throw new CodeEE(trerror.ForceQuitAndRestartError.Text);
        }
        if (Program.rebootFlag)
            _ui.Reboot();
        else
            _ui.ExitApplication();
        GlobalStatic.ForceQuitAndRestart = true;
    }

    public void ThrowTitleError(bool error)
    {
        _state.State = ConsoleState.Error;
        _state.notToTitle = true;
        _state.byError = error;
    }

    public void ThrowError(bool playSound)
    {
        _console.ForceUpdateGeneration();
        _state.UseUserStyle = false;
        _console.PrintFlush(false);
        _console.RefreshStrings(false);
        _state.State = ConsoleState.Error;
    }

    public void GotoTitle()
    {
        _console.ForceStopTimer();
        _console.ClearDisplay();
        _state.redraw = ConsoleRedraw.Normal;
        _state.UseUserStyle = false;
        _state.userStyle = new StringStyle(Config.ForeColor, MinorShift.Emuera.Primitives.EmuFontStyle.Regular, null);
        _state.process!.BeginTitle();
        _console.ReadAnyKey(false, false);
        _console.RunEmueraProgram("");
        _console.RefreshStrings(true);
    }

    public void GotoTitleAndLoadAndRepeatInput()
    {
        if (!Config.Ctrl_Z_Enabled) return;
        if (JSONConfig.Data.UseNewRandom)
        {
            EmueraLog.Warn("csm", "CtrlZ: JSONConfig.Data.UseNewRandom not supported");
            return;
        }
        if (GlobalStatic.ctrlZ.mLastSave < 0) return;
        if (GlobalStatic.ctrlZ.mInputs.Count == 0) return;
        if (GlobalStatic.ctrlZ.mRewindInProgress)
        {
            GlobalStatic.ctrlZ.mRepeatedUndoRequested = true;
            return;
        }

    again:
        GotoTitle();
        GlobalStatic.ctrlZ.mRewindInProgress = true;
        GlobalStatic.VEvaluator.Rand.SetRand(GlobalStatic.ctrlZ.mRandomSeed);
        GlobalStatic.Process.LoadSilent();
        _console.PressEnterKey(true, GlobalStatic.ctrlZ.mLastSave.ToString(), false);
        var inputs = GlobalStatic.ctrlZ.mInputs;
        if (inputs.Count > 0)
            inputs.RemoveAt(inputs.Count - 1);
        for (int i = 0; i < inputs.Count; i++)
        {
            if (GlobalStatic.ctrlZ.mRepeatedUndoRequested)
            {
                GlobalStatic.ctrlZ.mRepeatedUndoRequested = false;
                goto again;
            }
            _console.PressEnterKey(true, inputs[i], false);
        }
        GlobalStatic.ctrlZ.mRewindInProgress = false;
        GlobalStatic.ctrlZ.mRepeatedUndoRequested = false;
    }

    public async Task ReloadErb()
    {
        if (_state.State == ConsoleState.Error)
        {
            EmueraLog.Error("csm", trerror.CanNotUseWhenError.Text);
            return;
        }
        if (_state.State == ConsoleState.Initializing)
        {
            EmueraLog.Error("csm", trerror.CanNotUseWhenInitialize.Text);
            return;
        }
        bool notRedraw = false;
        if (_state.redraw == ConsoleRedraw.None)
        {
            notRedraw = true;
            _state.redraw = ConsoleRedraw.Normal;
        }
        if (_state.genericTimer.Enabled)
        {
            _state.genericTimer.Enabled = false;
            _state.timer_suspended = true;
        }
        _state.prevState = _state.State;
        _state.prevReq = _state.inputReq;
        _state.State = ConsoleState.Initializing;
        _console.PrintSingleLine(trsl.ReloadingErb.Text, true);
        _state.force_temporary = true;
        await _state.process!.ReloadErb();
        _state.force_temporary = false;
        _console.PrintSingleLine(trsl.ReloadCompleted.Text, true);
        _console.RefreshStrings(true);
        _state.updatedGeneration = true;
        if (notRedraw)
            _state.redraw = ConsoleRedraw.None;
    }

    public void ReloadErbFinished()
    {
        _state.State = _state.prevState;
        _state.inputReq = _state.prevReq;
        _console.PrintSingleLine(" ");
        if (_state.timer_suspended)
        {
            _state.timer_suspended = false;
            _state.genericTimer.Enabled = true;
        }
    }

    public async Task ReloadPartialErb(List<string> path)
    {
        if (_state.State == ConsoleState.Error)
        {
            EmueraLog.Error("csm", trerror.CanNotUseWhenError.Text);
            return;
        }
        if (_state.State == ConsoleState.Initializing)
        {
            EmueraLog.Error("csm", trerror.CanNotUseWhenInitialize.Text);
            return;
        }
        bool notRedraw = false;
        if (_state.redraw == ConsoleRedraw.None)
        {
            notRedraw = true;
            _state.redraw = ConsoleRedraw.Normal;
        }
        if (_state.genericTimer.Enabled)
        {
            _state.genericTimer.Enabled = false;
            _state.timer_suspended = true;
        }
        _state.prevState = _state.State;
        _state.prevReq = _state.inputReq;
        _state.State = ConsoleState.Initializing;
        _console.PrintSingleLine(trsl.ReloadingErb.Text, true);
        _state.force_temporary = true;
        await _state.process!.ReloadPartialErb(path);
        _state.force_temporary = false;
        _console.PrintSingleLine(trsl.ReloadCompleted.Text, true);
        _console.RefreshStrings(true);
        _state.updatedGeneration = true;
        if (notRedraw)
            _state.redraw = ConsoleRedraw.None;
    }

    public async Task ReloadFolder(string erbPath)
    {
        if (_state.State == ConsoleState.Error)
        {
            EmueraLog.Error("csm", trerror.CanNotUseWhenError.Text);
            return;
        }
        if (_state.State == ConsoleState.Initializing)
        {
            EmueraLog.Error("csm", trerror.CanNotUseWhenInitialize.Text);
            return;
        }
        if (_state.genericTimer.Enabled)
        {
            _state.genericTimer.Enabled = false;
            _state.timer_suspended = true;
        }
        List<string> paths = [];
        SearchOption op = SearchOption.AllDirectories;
        if (!Config.SearchSubdirectory)
            op = SearchOption.TopDirectoryOnly;
        var fnames = GamePaths.Current.DirAccessor.GetFiles(erbPath, "*.ERB", op);
        foreach (var fname in fnames)
        {
            var logicalName = SafPath.GetLogicalFileName(fname);
            if (string.Equals(Path.GetExtension(logicalName), ".ERB", StringComparison.OrdinalIgnoreCase))
                paths.Add(fname);
        }
        bool notRedraw = false;
        if (_state.redraw == ConsoleRedraw.None)
        {
            notRedraw = true;
            _state.redraw = ConsoleRedraw.Normal;
        }
        _state.prevState = _state.State;
        _state.prevReq = _state.inputReq;
        _state.State = ConsoleState.Initializing;
        _console.PrintSingleLine(trsl.ReloadingErb.Text, true);
        _state.force_temporary = true;
        await _state.process!.ReloadPartialErb(paths);
        _state.force_temporary = false;
        _console.PrintSingleLine(trsl.ReloadCompleted.Text, true);
        _console.RefreshStrings(true);
        _state.updatedGeneration = true;
        if (notRedraw)
            _state.redraw = ConsoleRedraw.None;
    }

    public void ReloadResource()
    {
        _console.PrintSingleLine(trsl.ReloadResourceMessage.Text, true);
    }

    public void Dispose()
    {
        _state.agentBridge?.Stop();
        if (_state.genericTimer != null)
            _state.genericTimer.Dispose();
    }

    public void DoSystemCommand(string command)
    {
        if (_state.genericTimer.Enabled)
        {
            _console.PrintError(trerror.CanNotInputTimerWait.Text);
            _console.PrintError("");
            _console.RefreshStrings(true);
            return;
        }
        if (_console.IsInProcess)
        {
            _console.PrintError(trerror.CanNotInputScriptRunning.Text);
            _console.RefreshStrings(true);
            return;
        }
        StringComparison sc = Config.StringComparison;
        _console.Print(command);
        _console.PrintFlush(false);
        _console.RefreshStrings(true);
        string com = command[1..];
        if (com.Length == 0)
            return;
        if (com.Equals("REBOOT", sc))
        {
            _ui.Reboot();
            return;
        }
        else if (com.Equals("OUTPUT", sc) || com.Equals("OUTPUTLOG", sc))
        {
            _console.OutputSystemLog("emuera.log");
            return;
        }
        else if (com.Equals("QUIT", sc) || com.Equals("EXIT", sc))
        {
            _ui.Close();
            return;
        }
        else if (com.Equals("CONFIG", sc))
        {
            _ui.ShowConfigDialog();
            return;
        }
        else if (com.Equals("DEBUG", sc))
        {
            if (!Program.DebugMode)
            {
                _console.PrintError(trerror.CanNotUseDebugWindow.Text);
                _console.RefreshStrings(true);
                return;
            }
            // OpenDebugDialog: WinForms-only stub removed in Headless
        }
        else
        {
            if (!Config.UseDebugCommand)
            {
                _console.PrintError(trerror.CanNotUseDebugCommand.Text);
                _console.RefreshStrings(true);
                return;
            }
            // DebugCommand: WinForms-only stub removed in Headless
            _console.PrintFlush(false);
        }
        _console.RefreshStrings(true);
    }

    /// <summary>
    /// issue-003：<c>AWAIT</c> 的等待语义对齐 <c>TWAIT t,1</c>。
    ///
    /// 改动前这里走 <c>Thread.Sleep</c> 且把 State 置为「休眠」—— 休眠不触发回合边界，
    /// AWAIT 期间画面完全不外送，背景色渐变 / 进度条因此表现为「卡住一阵然后突然跳变」。
    /// （参考实现 <c>Emuera/UI/Game/EmueraConsole.cs:642</c> 同样是 Sleep + Sleep 态，
    /// 但它是 WinForms 直绘：<c>RefreshStrings(true)</c> + <c>ProcessEvents()</c> 同步重绘
    /// 窗口，玩家本就逐帧可见。无头移植里「重绘」的等价物是「交付一个回合」，
    /// 故对齐到等待输入态才是原语义在本次移植中的正确落点。）
    ///
    /// 改后：等待期间进入 WaitInput（<see cref="InputType.Void"/> + 时限），回合边界触发 →
    /// 画面能交付出去；时限到达后由宿主循环走<b>既有的</b>超时续跑路径继续脚本
    /// （server：<c>RunLoopAsync</c> 的 CancelAfter → SubmitTimeout；
    /// CLI：<c>CliGameLoop.HandleTimeout</c> 的挂钟比对）—— 不新建机制。
    ///
    /// <para><b>2026.10.7 W1（让路停稳点）</b>：<c>time &lt;= 0</c>（无参 <c>AWAIT</c> /
    /// <c>AWAIT 0</c>）不再「只刷新就继续跑」，而是同样进入 WaitInput、但
    /// <see cref="InputRequest.Timelimit"/><c>=0</c> + <see cref="InputRequest.IsYield"/><c>=true</c>：
    /// 零时长挂起点，宿主交付一帧后调 <c>EmueraConsole.EndYield</c> 在同线程续跑。
    /// 与 <c>time &gt; 0</c> 的差别是<b>不装定时器</b>（<c>Timelimit=0</c> ⇒
    /// <c>InputTimeoutMs</c> 返回 null）⇒ 不引入任何超时/tick 风暴，也不会有超时续跑。</para>
    ///
    /// <para>⚠️ 这是**强于参考实现**的控制流变更（spec §3.1.1）：参考实现的
    /// <c>Await(int)</c> 在 <c>time &lt;= 0</c> 时是 <c>ProcessEvents()</c> 泵一次窗口后立刻
    /// 回 Running（靠 WinForms 同步重绘逐帧可见）；无头架构里「重绘」的等价物只有一个
    /// 停稳点上交付的帧，故这里必须真的挂起。</para>
    ///
    /// 插件 API <c>PluginManager.Await</c> 也走这里，一并获得同一语义。
    /// </summary>
    public void Await(int time)
    {
        if (!_ui.Created || _state.State != ConsoleState.Running)
        {
            Quit();
            return;
        }
        _console.RefreshStrings(true);
        if (time <= 0)
        {
            // 无参 AWAIT / AWAIT 0：零时长让路点。Timelimit=0 让 InputTimeoutMs 返回 null
            // （ConsoleInputHandler.WaitInput 只在 Timelimit>0 时 PresetTimer）⇒ 不装定时器、
            // 不发 tick、不会被 SubmitTimeout 提前续跑。让路期间客户端输入与定时等待
            // 同待遇：吞掉、不报错、不推进（spec §10.1）。
            _console.WaitInput(new InputRequest
            {
                InputType = InputType.Void,
                Timelimit = 0,
                IsYield = true,
            });
            return;
        }
        _console.WaitInput(new InputRequest
        {
            InputType = InputType.Void,
            Timelimit = time,
            // 🔴 不置任何「可打断」标记：参考实现下 AWAIT 是 Sleep 态，不建立等待、
            // 输入无从打断（spec §10.1）。era-core 为「无头架构下能交付帧」把它落成
            // WaitInput(Void)，形态可以不同，但**可观测行为不得不同** ⇒ 输入一律吞掉，
            // 等待只由定时器正常到期结束。与 TWAIT t,1 逐字同待遇（issue 10）。
        });
    }

    public void OpenErrorFile(ScriptPosition? pos)
    {
        var pInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Config.TextEditor
        };
        var ignoreCaseCmp = StringComparison.OrdinalIgnoreCase;
        string fname = pos!.Value.Filename.ToUpper();
        if (fname.EndsWith(".CSV", ignoreCaseCmp))
        {
            if (fname.Contains(Program.CsvDir, ignoreCaseCmp))
                fname = fname.Replace(Program.CsvDir, "", ignoreCaseCmp);
            fname = Program.CsvDir + fname;
        }
        else
        {
            if (!Program.AnalysisMode)
            {
                if (fname.Contains(Program.ErbDir, ignoreCaseCmp))
                    fname = fname.Replace(Program.ErbDir, "", ignoreCaseCmp);
                fname = Path.Combine(Program.ErbDir + fname);
            }
        }
        switch (Config.EditorType)
        {
            case TextEditorType.SAKURA:
                pInfo.Arguments = "-Y=" + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
                break;
            case TextEditorType.TERAPAD:
                pInfo.Arguments = "/jl=" + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
                break;
            case TextEditorType.EMEDITOR:
                pInfo.Arguments = "/l " + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
                break;
            case TextEditorType.USER_SETTING:
                if (!string.IsNullOrEmpty(Config.EditorArg) && Config.EditorArg != null)
                    pInfo.Arguments = Config.EditorArg + pos.Value.LineNo.ToString() + " \"" + fname + "\"";
                else
                    pInfo.Arguments = fname;
                break;
        }
        try
        {
            System.Diagnostics.Process.Start(pInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            _console.PrintError(trerror.FailedOpenEditor.Text);
            _console.ForceUpdateGeneration();
        }
    }
}
