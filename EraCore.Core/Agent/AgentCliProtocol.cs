using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal sealed class AgentCliProtocol : AgentProtocolBase, IVtHost, IInputTimer
    {
        // 职责拆分：输入行缓冲（CliInputBuffer）与 VT 生命周期（VtTerminalLifecycle）独立成类，
        // 本类只做编排，不再持有输入缓冲与进程级清理钩子的内联状态。
        private readonly CliInputBuffer _input;
        private readonly VtTerminalLifecycle _vtLifecycle;

        // VT 模式状态（DA1 探测通过后非 null）
        private AgentCliVtScreen? _screen;
        private VtInputHandler? _vtInput;
        private readonly ITerminalInput _terminalInput;
        private readonly ITerminalSetup _terminalSetup;

        // ADR-0009：scroll 状态由 ScrollController 持有（纯逻辑，构造时占位，RunVtLoop 内 UpdateVisibleLines）。
        private readonly ScrollController _scroll;

        private readonly ButtonSelectionMode _buttons;
        private readonly CountdownRenderer _countdown;
        private readonly TerminalRenderer _renderer;
        private readonly CliRedrawCoordinator _redraw;
        // Phase 4-2：CLI 自有 DisplayState（Q7）。AgentCliProtocol 构造持有，FlushBuffer 帧级 TryUpdate。
        // 与 server 路径的 DisplayState（Session 持有、BuildTurn 回合级 TryUpdate）独立。
        private readonly DisplayState _displayState;
        // ADR-0006：Scroll Status Bar 渲染器，独立于 TerminalRenderer。
        private ScrollStatusBarRenderer? _scrollStatusBar;

        private CliGameLoop _gameLoop = null!;

        /// <summary>是否在 primitive 输入等待状态。ADR-0009：通过 IVtHost 暴露给 VtInputHandler 门卫。</summary>
        bool IVtHost.IsWaitingPrimitive => console.IsWaitingPrimitive;

        /// <summary>ADR-0010：primitive 键盘输入分发，委托给 EmueraConsole。</summary>
        void IVtHost.PressPrimitiveKey(int keycode, int keydata, int keymod)
            => console.PressPrimitiveKey(keycode, keydata, keymod);

        /// <summary>ADR-0010：primitive 鼠标输入分发，委托给 EmueraConsole。</summary>
        void IVtHost.InputMouseKey(int type, int result1, int result2, int result3, int result4, long result5)
            => console.InputMouseKey(type, result1, result2, result3, result4, result5);

        /// <summary>
        /// ADR-0031 / spec-v3 M4：VT 鼠标位置（终端行列）→ console 局部像素（左上基准）→ 输入宿主。
        /// <para>
        /// 换算见 <see cref="TerminalViewportGeometry"/> —— X 是列宽累加，
        /// Y 是<b>终端绝对行</b> × <c>LineHeight</c>（左上基准，不锚内容块）。
        /// </para>
        /// <para>
        /// 2026.10.14 贴底改造后三者自洽（spec §1.1）：内容块末行恒锚 <c>visibleLines − 1</c>、
        /// <c>H = VisibleLines × LH</c>（视口高度，与内容量无关）⇒ 代入引擎 <c>MoveMouse</c> 得
        /// <c>relPointY ≡ 0</c>（恒定命中），<c>MY = rawY − H</c> 顶行 <c>−H</c>、末行 <c>−LH</c>，
        /// 同时符合 M0 标定 H1。配套的 <c>ClientRectangle</c> 门槛（picbox 高度跟随视口）见
        /// <see cref="HeadlessConsole"/> 构造与 <c>HeadlessPictureBox</c> 备注 —— 不修则贴底内容行
        /// <c>y ≥ WindowY</c> 被 <c>Contains</c> 挡掉，<c>MOUSEB</c> 恒空。
        /// </para>
        /// <para>
        /// ⚠️ 只在宿主声明 <see cref="InputCapability.Geometry"/> 时才写 —— 否则
        /// <c>MoveMouse</c> 不会被调用，写了也无消费者（<c>NullInputHost</c> 本身也是空操作）。
        /// 门卫本体（与 wire 入口共用同一份）已收进 <see cref="InputStateApplier"/>。
        /// </para>
        /// </summary>
        void IVtHost.SubmitPointerGeometry(int row, int col)
        {
            int x = TerminalViewportGeometry.PointXAtViewportCol(col);
            int y = TerminalViewportGeometry.PointYAtViewportRow(row);

            // 门卫（能力位判定）已收进 InputStateApplier，与 wire 入口同口径
            InputStateApplier.ApplyPointerGeometry(ui.Input, new EmuPoint(x, y));
        }

        /// <summary>
        /// ADR-0031 / spec-v3 M4b：VT 鼠标键 → <c>IInputHost</c> 键状态。
        /// <para>
        /// ⚠️ 只在宿主声明 <see cref="InputCapability.KeyboardState"/> 时才写 —— 与
        /// <see cref="IVtHost.SubmitPointerGeometry"/> 同一门卫口径：能力位先声明，通道才通电。
        /// 门卫本体（与 wire 入口共用同一份）已收进 <see cref="InputStateApplier"/>。
        /// </para>
        /// </summary>
        void IVtHost.SubmitPointerKey(int virtualKey, bool down)
        {
            // 门卫（能力位判定）已收进 InputStateApplier，与 wire 入口同口径
            InputStateApplier.ApplyPointerKey(ui.Input, virtualKey, down);
        }

        /// <summary>当前 Scroll Offset（IVtHost 门卫读取）。ADR-0009：从 ScrollController 读。</summary>
        int IVtHost.ScrollOffset => _scroll.ScrollOffset;

        public AgentCliProtocol(EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput, ConfigData configData)
            : base(console, ui)
        {
            _terminalSetup = terminalSetup;
            _terminalInput = terminalInput;
            // 职责拆分：输入行缓冲与 VT 生命周期独立成类，协议侧只做编排。
            _input = new CliInputBuffer(DispatchInput, Echo);
            _vtLifecycle = new VtTerminalLifecycle(Stop, CleanupVt);
            // ADR-0009：ScrollController 构造占位（visibleLines=1），RunVtLoop 内 UpdateVisibleLines(WindowHeight-2)。
            _scroll = new ScrollController(scrollVisibleLines: 1);
            // Phase 4-2（Q7/R4）：CLI 自有 DisplayState——从 ConfigData 取 defaultFontName，
            // 与 Session.cs:83 同一取值表达式。TerminalRenderer + ButtonSelectionMode 均注入此实例。
            var defaultFontName = configData.GetConfigValue<string>(ConfigCode.FontName) ?? "";
            _displayState = new DisplayState(console, defaultFontName);
            // VT-only 后 ANSI 始终可用，渲染器假设 _screen 在 VT 主循环内必非 null。
            // Phase 4-1：TerminalRenderer 数据源换成 DisplayState.Current.lines。
            _renderer = new TerminalRenderer(console, _scroll, null, _displayState);
            // Phase 3-3b：ButtonSelectionMode 注入 DisplayState，RefreshButtonRegionsFromSnapshot 启用。
            _buttons = new ButtonSelectionMode(
                console,
                _scroll,
                () => _screen,
                input => DispatchInput(input),
                _input.Clear,
                _displayState);
            _countdown = new CountdownRenderer(console, () => _scroll.ScrollOffset, () => _screen, () => _renderer.LastContentRow);
            _redraw = new CliRedrawCoordinator(_renderer, _scroll, () => _scrollStatusBar, _countdown, _buttons, () => _vtInput);
            _renderer.OnScrollAutoFollow = () => _redraw.Redraw(RedrawKind.ChromeOnly, true);

            // ADR-0031 / spec-v3 M3（引擎-10 接线）：把 CLI 的**真实**滚动（ScrollController）
            // 接成视口真相源。此前 MoveMouse 恒读 HeadlessConsole 的内部 ScrollBar，而 CLI 的
            // 滚动只更新 ScrollController、从不回流到 ScrollBar ⇒ 几何判定与真实滚动脱节（缺口 F）。
            //
            // M4 修正：视口不是 ScrollController 本身而是 CliViewport —— 后者才知道
            // 「渲染了多少行」，从而给出正确的 VisibleHeight（= 渲染行数 × 行高）。
            (ui as HeadlessConsole)?.AttachViewport(
                new CliViewport(
                    _scroll,
                    () => console.DisplayLineList.Count,
                    () => _screen?.WindowHeight ?? 0));
        }

        private void CreateGameLoop()
        {
            _gameLoop = new CliGameLoop(
                console,
                this,
                _screen!,
                _scroll,
                _scrollStatusBar,
                _redraw,
                _vtInput!,
                _countdown,
                _screen!.WindowWidth,
                _screen!.WindowHeight);
        }

        internal void RunCliLoop()
        {
            // 顶层异常边界：覆盖 HandleTimeout / DispatchMouseClick /
            // DispatchMouseMiss / Initialize 等所有调 RunEmueraProgram 的路径。
            // 脚本运行期异常一律 fatal——Process 内部状态不可逆，恢复无意义。
            // VT 终端恢复由 VtTerminalLifecycle（AppDomain.UnhandledException /
            // ProcessExit）的多钩子保障，finally 块只负责 VT 路径。
            // ADR-0005：CLI 协议层改为 VT-only。VT 初始化失败抛 HeadlessFatalException，
            // 由 HeadlessRunner 捕获并非零退出——不再静默降级到残缺体验。
            try
            {
                // 注意：不在 VT 路径启用前 FlushBuffer。
                // Initialize() 阶段 @SYSTEM_TITLE 产生的 SetBgOp 等 pendingOps
                // 必须等到 _screen 就绪后由 RunAgentLoop 内部首次 FlushBuffer 消费，
                // 否则 VT 路径下 SetBgOp 会被提前消费（_screen==null）导致转义不发。
                if (!_terminalSetup.TryPrepareVtInput())
                {
                    throw new HeadlessFatalException(
                        "VT 终端初始化失败：ANSI 未启用或 stdin 被重定向。" +
                        "请在真实交互式终端运行（如 Windows Terminal / ConPTY / 现代 SSH），" +
                        "或改用 --server 模式。");
                }

                EmueraLog.Info("headless", "终端路径: VT（备用屏 + SGR mouse + DA1 探测）");

                // RunVtLoop 的循环末尾已刷新最终状态，CleanupVt() 在 finally 中将 _screen 置 null，
                // 故此处不再额外 FlushBuffer。
                RunVtLoop();
            }
            catch (Exception ex)
            {
                // HeadlessFatalException 不在此处记录——向上传播到 HeadlessRunner 统一处理。
                if (ex is HeadlessFatalException)
                {
                    Stop();
                    throw;
                }
                AgentLog.Instance.Write("cli fatal: " + ex);
                Stop();
            }
        }

        #region Main loops

        /// <summary>
        /// 启动 VT 路径主循环：DA1 探测 → 备用屏 → SGR mouse → 主循环轮询。
        /// 调用前 <see cref="ITerminalSetup.TryPrepareVtInput"/> 必须已返回 true（VT-only）。
        /// VT 终端恢复由 VtTerminalLifecycle（AppDomain.UnhandledException /
        /// ProcessExit）的多钩子保障，finally 块负责禁用 mouse + 退出备用屏。
        /// </summary>
        private void RunVtLoop()
        {
            _vtInput = new VtInputHandler(this, _terminalInput);
            _screen = new AgentCliVtScreen();
            _renderer.Screen = _screen;
            _scrollStatusBar = new ScrollStatusBarRenderer(() => _screen);
            _scroll.UpdateVisibleLines(Math.Max(1, _screen.WindowHeight - 2));
            _vtLifecycle.Register();
            CreateGameLoop();

            try
            {
                _screen.EnterAlternateScreen();
                _vtInput.EnableSgrMouse();
                _gameLoop.RunLoop(StopToken);
            }
            finally
            {
                _vtLifecycle.CleanupNow();
            }
        }

        #endregion

        #region Scroll dispatch (ADR-0006)

        /// <summary>滚轮事件分发（cb=64/65 → delta=±3）。由 VtInputHandler.OnMouseEvent 调用。
        /// ADR-0009：算术委托给 ScrollController，offset 变化时 ScrollChanged 事件触发 OnScrollChanged 做 4 步渲染。</summary>
        void IVtHost.DispatchWheel(int delta)
        {
            if (_screen == null) return;
            _scroll.ScrollBy(delta, console.DisplayLineList.Count);
        }

        /// <summary>键盘滚动热键分发（PgUp/PgDn/Home/End）。由 VtInputHandler.OnKeyEvent 调用。
        /// ADR-0009：动作→算子翻译已收归 ScrollController.ApplyAction，此处仅一行转发；
        /// offset 变化时 ScrollChanged 事件触发 OnScrollChanged 做 4 步渲染。</summary>
        void IVtHost.DispatchScroll(ScrollAction action)
        {
            var screen = _screen;
            if (screen == null) return;
            _scroll.ApplyAction(action, screen.WindowHeight, console.DisplayLineList.Count);
        }

        #endregion

        #region VT lifecycle helpers

        /// <summary>VT 路径请求退出（Ctrl+C / CancelKeyPress 触发）。</summary>
        void IVtHost.RequestExit() => Stop();

        /// <summary>VT 解析器输出的 ConsoleKeyInfo 入口，复用现有 ProcessKey 分支。</summary>
        void IVtHost.ProcessKeyFromVt(ConsoleKeyInfo key) => ProcessKey(key);

        /// <summary>一次性清理：禁用 SGR mouse → 恢复 input mode → 退出备用屏。
        /// ADR-0006：退出前恢复光标可见（Scroll Mode 可能隐藏了光标）。
        /// 幂等由 <see cref="VtTerminalLifecycle.CleanupNow"/> 保障，本方法不再自带标志。</summary>
        private void CleanupVt()
        {
            try { _scrollStatusBar?.RestoreCursor(); }
            catch (Exception ex) { AgentLog.Instance.Write("scroll status bar restore cursor failed: " + ex); }

            try { _vtInput?.Dispose(); }
            catch (Exception ex) { AgentLog.Instance.Write("vt input dispose failed: " + ex); }

            try { _screen?.Dispose(); }
            catch (Exception ex) { AgentLog.Instance.Write("vt screen dispose failed: " + ex); }

            _vtInput = null;
            _screen = null;
            _renderer.Screen = null;
            _scrollStatusBar = null;
        }

        #endregion

        #region IInputTimer

        bool IInputTimer.IsWaitingInput => console.State == ConsoleState.WaitInput;
        long IInputTimer.InputTimelimit => console.InputTimelimit;
        string? IInputTimer.TimeUpMessage => console.TimeUpMessage;
        void IInputTimer.SubmitTimeout() => console.SubmitTimeout();
        void IInputTimer.ClearInputBuffer() => _input.Clear();

        // spec §5.1b W1-6 / W1-7：让路识别与续跑 —— 只做转发，不在协议层自建一套。
        // 判据与 server 共用 AgentProtocolBase.IsAtYieldPoint（单一真相源，避免两份清单漂移）；
        // 续跑走 EmueraConsole.EndYield facade（不置 isTimeout、不打印 TimeUpMes）。
        // 这是 CLI 的存活条件：CliGameLoop 除输入与超时外没有别的续跑入口，
        // 缺了它无参 AWAIT 挂起后 CLI 必然停住（而 server 全绿发现不了）。
        bool IInputTimer.IsYieldPoint => IsAtYieldPoint();
        void IInputTimer.EndYield() => console.EndYield();

        #endregion

        #region Keyboard / character input

        private void ProcessKey(ConsoleKeyInfo key)
        {
            // 按钮选择模式优先处理方向键/Enter，未消费则走输入行缓冲（CliInputBuffer）
            if (_buttons.HandleKey(key))
                return;

            if (key.Key == ConsoleKey.Enter)
                _input.ProcessChar('\r');
            else if (key.Key == ConsoleKey.Backspace)
                _input.ProcessChar('\b');
            else if (key.Key == ConsoleKey.Escape)
                _input.ProcessChar((char)27);
            else if (!char.IsControl(key.KeyChar))
                _input.ProcessChar(key.KeyChar);
        }

        #endregion

        #region Mouse dispatch

        void IVtHost.DispatchMouseClick(object value, bool isInteger)
        {
            if (console.State != ConsoleState.WaitInput) return;

            // 鼠标点击退出按钮选择模式（若有），并执行点击命中
            if (_buttons.IsButtonMode) _buttons.ExitButtonMode();

            // Phase 3-3：value-based dispatch——isInteger 时 value 是 long 装箱，
            // 否则是 string。Generation 校验由服务端 ConsoleInputHandler 兜底。
            string input = isInteger ? value.ToString()! : (string)value;
            DispatchInput(input);
        }

        void IVtHost.DispatchMouseMiss()
        {
            if (console.State != ConsoleState.WaitInput) return;
            if (_buttons.IsButtonMode) return;

            var req = console.CurrentRequest;
            if (req == null) return;

            // 仅在允许空输入的请求类型下才 dispatch 空输入（模拟回车）；
            // 整数输入等场景下点击空白区域直接忽略，与 winforms 行为一致。
            // 判定复用 AgentProtocolBase.AllowsEmptyInput 单一真相源。
            // 注：AnyValue 不在清单内——其空输入经 DispatchInput 必被 long.TryParse("") 拒绝，
            // 与"忽略"净效果相同，故此处不再单独派发（非回归）。
            if (AllowsEmptyInput(req.InputType))
                DispatchInput("");
        }

        #endregion

        /// <summary>输入回显：经 VT 备用屏（与光标/滚动状态一致）。ADR-0005 VT-only 后
        /// _screen 在输入等待态必非 null，故直接断言解引用，不再保留主屏降级分支。</summary>
        private void Echo(string text)
        {
            _screen!.WriteRaw(text);
        }

        protected override void OnInputRejected(string reason)
        {
            // 与 winforms 行为一致：静默忽略无效输入，不向终端输出提示
        }
    }
}
