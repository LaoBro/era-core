using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Timers;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.UI.Game;
using GameProcProcess = MinorShift.Emuera.GameProc.Process;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// Console state enum (from original EmueraConsole.cs).
/// </summary>
internal enum ConsoleState
{
    Initializing = 0,
    Quit = 5,
    Error = 6,
    Running = 7,
    WaitInput = 20,
    // issue-003：原 Sleep = 21（AWAIT 的线程休眠态）已删除 —— AWAIT 改走等待输入态后
    // 无处再置该值，留着就是一段谁也到不了的死状态。
}

/// <summary>
/// Console redraw enum (from original EmueraConsole.cs).
/// </summary>
internal enum ConsoleRedraw
{
    None = 0,
    Normal = 1,
}

/// <summary>
/// Centralized shared state for EmueraConsole.
/// All mutable state that crosses responsibility boundaries lives here.
/// Manager classes hold a reference to this object to read/write state.
/// </summary>
internal sealed class ConsoleStateData
{
    // --- Core game state ---
    internal ConsoleState State;
    internal InputRequest? inputReq;
    internal GameProcProcess? process;
    internal bool isTimeout;
    /// <summary>
    /// issue-002：会话拆除请求下发到脚本层的「停止执行」标志。
    ///
    /// <b>为什么不直接把 <see cref="State"/> 置成 Quit</b>：ConsoleState 是显示/输入状态机，
    /// Quit/Error/WaitInput 各自都有既有语义与消费者；而「停止执行」是一条执行信号。
    /// 混进 State 会被既有回填逻辑复活 —— <c>EmueraConsole.RunEmueraProgram</c> 入口无条件
    /// 把 State 置回 Running。
    /// 与之分开后，停止是一次性的：一旦置位，<c>IsRunning</c> 恒假，直到下一次会话（新
    /// <c>ConsoleStateData</c> 实例）为止。
    ///
    /// volatile：拆除方（HTTP 线程）写、游戏循环线程读。与既有 <see cref="KillMacro"/> 同手法。
    /// 位于每次游戏循环新建的控制台状态对象上，故不构成新的进程级静态状态。
    /// </summary>
    internal volatile bool stopRequested;
    /// <summary>是否已请求停止脚本执行。</summary>
    internal bool StopRequested => stopRequested;
    /// <summary>请求停止脚本执行（幂等，不可撤销——会话正在拆除，不存在恢复语义）。</summary>
    internal void RequestStop() => stopRequested = true;
    internal long timerID = -1;
    internal long timer_endTime;
    internal bool need_settimer;
    internal bool force_temporary;
    internal bool timer_suspended;
    internal ConsoleState prevState;
    internal InputRequest? prevReq;
    internal bool inProcess;
    internal volatile bool KillMacro;
    internal bool MesSkip;

    // --- Display / line management ---
    internal List<ConsoleDisplayLine> displayLineList = [];
    internal int lineNo;
    internal int lastDrawnLineNo = -1;
    internal long logicalLineCount;
    internal long deletedLines;
    internal Dictionary<int, List<AConsoleDisplayNode>>? escapedParts;
    internal List<ConsoleDisplayLine> _htmlElementList = new(10);

    // --- Button / selection state ---
    /// <summary>
    /// 当前代按钮索引（2.2 按钮匹配改索引，见 <see cref="ButtonIndex"/>）。
    /// displayLineList 结构变更（ConsolePrintManager）调 Invalidate()；匹配时按
    /// lastButtonGeneration 惰性重建——代不变 + 行不变时命中 O(1) 零扫描。
    /// </summary>
    internal readonly ButtonIndex buttonIndex = new();
    internal ConsoleButtonString? selectingButton;
    internal ConsoleButtonString? lastSelectingButton;
    internal ConsoleButtonString? pointingString;
    internal ConsoleButtonString? lastPointingString;
    internal HashSet<ConsoleButtonString> pointingStrings = [];
    internal long lastButtonGeneration;
    internal long newButtonGeneration;
    internal bool lastButtonIsInput = true;
    internal bool updatedGeneration;
    internal LogicalLine? lastInputLine;

	// --- Style ---
	internal StringStyle defaultStyle = new(Config.ForeColor, EmuFontStyle.Regular, null);
	internal StringStyle userStyle = new(Config.ForeColor, EmuFontStyle.Regular, null);
	internal DisplayLineAlignment alignment = DisplayLineAlignment.LEFT;
	internal EmuColor bgColor = Config.BackColor;
	/// <summary>
	/// issue 23：文本背景色（TEXT_BGC_ON/OFF 语义接受）。null = 关闭。era-core 渲染线暂未消费，
	/// 记「已知差异」（spec §5）；此处仅保留语义状态供未来渲染路线/快照消费。
	/// </summary>
	internal EmuColor? textBackgroundColor;
	/// <summary>
	/// issue 02：背景图列表（SETBGIMAGE 落地）。WinForms 追加语义——同 src 可多份，
	/// depth 决定 z 序（前端按 depth 排序渲染），opacity 0.0-1.0。状态由 DisplayState 读入快照/diff。
	/// </summary>
	internal List<BgImageState> bgImages = [];
	internal bool UseUserStyle;
	internal bool UseSetColorStyle;
	internal string? stBar;

    // --- Timer ---
    internal readonly Stopwatch _genericTimerStopwatch = new();
    internal System.Timers.Timer genericTimer = new();
    internal System.Timers.Timer redrawTimer;
    internal long timeDisplayCount;
    internal bool inputed;

    // --- Draw state ---
    internal Stopwatch _frameDeltaTimer = Stopwatch.StartNew();
    /// <summary>
    /// 帧间隔（ms）。headless/移动端默认 0 = 禁用帧同步：IConsoleUI.ProcessEvents 为 no-op，
    /// 忙等循环（见 <see cref="WaitFrameSyncIfEnabled"/>）纯空转，每回合最多浪费 16ms。
    /// 需要帧同步时可显式设为 &gt; 0（例如 1000/FPS）。
    /// 注意：msPerFrame==0 同时禁用 <see cref="ConsoleRefreshHandler"/> 的帧率节流
    /// （RefreshStrings 不再因距上次绘制 &lt; 帧间隔而提前 return）——headless 下绘制为 no-op，
    /// 禁用节流符合「不帧同步」意图，无重绘代价。
    /// </summary>
    internal uint msPerFrame = 0;
    internal Stopwatch? _drawStopwatch;
    internal bool forceTextBoxColor;
    internal ConsoleRedraw redraw = ConsoleRedraw.Normal;

    /// <summary>
    /// 帧同步忙等（SetBgColor / RefreshStrings 共用，ADR 去重）：
    /// 首次绘制启动计时；此后若 msPerFrame&gt;0 且距上次绘制未达帧间隔，循环调用
    /// <paramref name="ui"/>.ProcessEvents() 直到到点。headless/移动端 msPerFrame==0 时
    /// ProcessEvents 为 no-op，直接跳过（每回合最多省 16ms 纯空转）。调用方在返回后自行 Restart。
    /// </summary>
    internal void WaitFrameSyncIfEnabled(IConsoleUI ui)
    {
        if (_drawStopwatch == null)
            _drawStopwatch = System.Diagnostics.Stopwatch.StartNew();
        else if (msPerFrame > 0)
        {
            while (_drawStopwatch.ElapsedMilliseconds < msPerFrame)
                ui.ProcessEvents();
        }
    }

    // --- Misc ---
    internal bool runningERBfromMemory;
    internal bool noOutputLog;
    internal bool notToTitle;
    internal bool byError;
    internal bool AlwaysRefresh;
    internal string? debugTitle;
    internal readonly StringBuilder dConsoleLog = new("");
    internal List<string> dTraceLogList = [];
    internal bool tooltipUsed;
    internal int tooltip_duration;
    internal string tooltip_fontname = Config.FontName;
    internal long tooltip_fontsize = Config.FontSize;
    internal bool tooltip_img;
    internal readonly ClipboardProcessor? CBProc;

    // --- WinForms-only (stubs in headless) ---
    internal int selectingCBGButtonInt = -1;
    internal int lastSelectingCBGButtonInt = -1;

    // --- Agent / Headless ---
    internal AgentProtocolBase? agentBridge;
    /// <summary>
    /// Phase 5-3：变更信号队列（ADR-0014）。ConcurrentQueue 保证游戏线程 Enqueue 与 DisplayState.TryUpdate 的 Clear 跨线程安全。
    /// 引擎每次显示变更都 Enqueue（ConsolePrintManager 5 个站点）；DisplayState.TryUpdate peek Count 做变更检测，
    /// rebuild 后 Clear 消费式清空（防无限增长）。Phase 5 后 ops 不再序列化到 TurnRecord，仅作 dirty flag。
    /// </summary>
    internal readonly ConcurrentQueue<TurnOp> _pendingOps = new();
    internal bool _needFullRefresh;
    internal TerminalCharWidthConfig CharWidthConfig = TerminalCharWidthConfig.Default;

    internal bool bitmapCacheEnabledForNextLine;

    internal ConsoleStateData()
    {
        State = ConsoleState.Initializing;
        CBProc = null;

        genericTimer = new();
        genericTimer.Interval = 10;
        genericTimer.Enabled = false;

        redrawTimer = new System.Timers.Timer
        {
            Enabled = false
        };
        redrawTimer.Interval = 10;
    }
}
