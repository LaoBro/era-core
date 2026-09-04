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

    public void Await(int time)
    {
        if (!_ui.Created || _state.State != ConsoleState.Running)
        {
            Quit();
            return;
        }
        _console.RefreshStrings(true);
        _state.State = ConsoleState.Sleep;
        _ui.ProcessEvents();
        if (time > 0)
            System.Threading.Thread.Sleep(time);
        _state.State = ConsoleState.Running;
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
