using System;
using System.Collections.Generic;
using MinorShift.Emuera.Output;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 终端输出渲染：刷新屏幕、擦除行、刷新缓冲区。
    /// ADR-0005：VT-only 后原 <c>_ansiEnabled</c> 字段已删除，调用方假设 ANSI 可用。
    /// ADR-0005 Issue 4：删除 <c>_screen == null</c> 降级分支与 <c>_cursor</c> 字段，
    /// 调用方保证 <see cref="AgentCliVtScreen"/> 在 VT 主循环内必非 null。
    /// 从 AgentCliProtocol 拆分以隔离终端输出逻辑。
    ///
    /// Phase 4-1（ADR-0014）：数据源从 <c>_console.DisplayLineList</c> 换成 <see cref="DisplayState.Current"/>。
    /// delta 算法（<c>_lastRenderedLineNo</c> 比较、末行改写路径的尾部 delta）保持不变；
    /// **新行路径**自 2026.10.14 贴底改造起改为一律 FullRefresh（见 <see cref="FlushBuffer(DisplaySnapshot)"/>）。
    /// 全屏事件（CLEAR/CLEARLINE/SET_BG）改用 snapshot 比对推断，脱离 <c>_pendingOps</c>（R1）。
    /// Phase 5-3：TryUpdate 消费式清空 _pendingOps，FlushBuffer 不再显式 drain。
    /// CLEARLINE+reprint 检测：count/LineNo 比对之外，额外比较旧末行位置的 <c>SourceLine</c> 引用
    /// （spec R1 原方案漏检 count/LineNo 回到旧值的反例——SourceLine 持原 ConsoleDisplayLine 引用，引用变更 = 行被替换）。
    /// 末行类型 <c>ConsoleDisplayLine?</c> → <see cref="DisplayLine"/>?（R3），删除 <c>ReferenceEquals</c> 检查。
    /// SelectingButton / CharWidthConfig 由 <c>IButtonDisplayContext</c> 提供（R2：真相源边界 = DisplayLineList + bgColor）。
    /// </summary>
    internal sealed class TerminalRenderer : IRedrawRenderer
    {
        private readonly IButtonDisplayContext _buttonContext;
        private readonly ScrollController _scroll;
        private IAgentCliVtScreen? _screen;
        private readonly IDisplayState _displayState;

        private int _lastRenderedLineNo = -1;
        // Phase 4-1：CLEARLINE 检测——行数减少时重置 delta tracking（R1）
        private int _lastSnapshotLineCount = 0;
        // Phase 4-1（R3）：类型从 ConsoleDisplayLine? 改为 DisplayLine?
        private DisplayLine? _lastRenderedLastLine;
        private string? _currentBgHex;

        private static readonly Dictionary<string, (int, int, int)> _bgRgbCache = new(StringComparer.Ordinal);

        /// <summary>
        /// 最近一次 FullRefresh 绘制的可见内容行数（不含状态栏行）。仅测试/诊断消费。
        /// -1 表示尚未渲染过。
        /// </summary>
        internal int LastDrawnRows { get; private set; } = -1;

        /// <summary>
        /// 最近一次 FullRefresh 的内容末行终端行号。贴底渲染下恒为 <c>visibleLines − 1</c>
        /// （与内容量无关），CountdownRenderer 经它在恒定行覆盖倒计时/超时文案，
        /// 替代不可靠的 Console.CursorTop 探测（ConPTY 下间歇抛异常或返回错误值）。
        /// -1 表示尚未渲染过。
        /// </summary>
        internal int LastContentRow { get; private set; } = -1;

        /// <summary>
        /// auto-follow 回调：FlushBuffer 检测到新行且 offset>0 时，归零 offset + FullRefresh 后调用，
        /// 让 AgentCliProtocol 同步状态栏/倒计时/按钮区域。ADR-0006。
        /// </summary>
        internal Action? OnScrollAutoFollow { get; set; }

        /// <summary>注入或更换 VT 屏幕（生产：RunVtLoop 内创建后设置；测试：构造时直接传入）。</summary>
        internal IAgentCliVtScreen? Screen
        {
            get => _screen;
            set => _screen = value;
        }

        public TerminalRenderer(
            IButtonDisplayContext buttonContext,
            ScrollController scroll,
            IAgentCliVtScreen? screen,
            IDisplayState displayState)
        {
            _buttonContext = buttonContext;
            _scroll = scroll;
            _screen = screen;
            _displayState = displayState;
        }

        /// <summary>FlushBuffer：帧级刷新快照 + 渲染 delta（Phase 4-2 帧级 TryUpdate）。
        /// Phase 5-3：TryUpdate 已消费式清空 _pendingOps，无需再显式 drain。
        /// 返回 true 表示快照确有变更并重绘（False = no-op 帧，无重绘）。</summary>
        public bool FlushBuffer()
        {
            bool changed = _displayState.TryUpdate();
            if (!changed) return false;
            FlushBuffer(_displayState.Current);
            return true;
        }

        /// <summary>plan C 支线(2)：CLEARLINE 分类结果（Clear 空屏由 FlushBuffer 的 lines.Count==0 分支处理）。</summary>
        internal enum FlushClearLineEventType { None, ClearLine }

        /// <summary>
        /// plan C 支线(2)：从 snapshot 比对推断 CLEARLINE 的纯函数（无副作用，可单测）。
        /// 三子句 + SourceLine 引用反例（spec R1 漏检：CLEARLINE+reprint 使 count/LineNo 回到旧值但内容全换，
        /// SourceLine 引用变更 = 行被替换）。返回 ClearLine 表示末部被截断/替换，需重置 delta tracking 走 FullRefresh。
        /// </summary>
        internal static FlushClearLineEventType ClassifyClearLineEvent(
            DisplaySnapshot snapshot, int lastRenderedLineNo, int lastSnapshotLineCount, DisplayLine? lastRenderedLastLine)
        {
            var lines = snapshot.lines;
            if (lines.Count == 0) return FlushClearLineEventType.None; // 空屏归 Clear 分支

            int currentLineNo = lines[^1].LineNo;
            bool clearLine = lines.Count < lastSnapshotLineCount ||
                (lastRenderedLineNo >= 0 && currentLineNo < lastRenderedLineNo);
            if (!clearLine && lastSnapshotLineCount > 0 && lastRenderedLastLine != null)
            {
                int oldLastIdx = lastSnapshotLineCount - 1;
                if (oldLastIdx < lines.Count)
                {
                    var oldPositionSource = lines[oldLastIdx].SourceLine;
                    if (!ReferenceEquals(oldPositionSource, lastRenderedLastLine.SourceLine))
                        clearLine = true;
                }
            }
            return clearLine ? FlushClearLineEventType.ClearLine : FlushClearLineEventType.None;
        }

        /// <summary>
        /// Phase 4-1：从 snapshot 比对推断全屏事件 + delta 渲染。
        /// 全屏事件检测（R1）：
        /// - SET_BG: snapshot.bgColor != _currentBgHex → WriteBgEscape
        /// - CLEAR: lines.Count == 0（且之前有内容）→ ClearScreen + auto-follow
        /// - CLEARLINE: lines.Count &lt; _lastSnapshotLineCount 或末行 LineNo 回退 → 重置 delta tracking 走 FullRefresh
        /// </summary>
        private void FlushBuffer(DisplaySnapshot snapshot)
        {
            var lines = snapshot.lines;
            var screen = _screen;

            // SET_BG：背景色变更（独立于 CLEAR/CLEARLINE，VT 全局状态——ClearScreen 不重置背景色）
            if (snapshot.bgColor != _currentBgHex)
            {
                _currentBgHex = snapshot.bgColor;
                WriteBgEscape(snapshot.bgColor);
            }

            // CLEAR：lines.Count == 0
            if (lines.Count == 0)
            {
                // 仅在之前有内容时 ClearScreen + auto-follow（初始状态不触发）
                if (_lastRenderedLineNo != -1 || _lastSnapshotLineCount > 0)
                {
                    screen?.ClearScreen();
                    // ADR-0006：ClearOp 触发 auto-follow，归零 Scroll Offset
                    if (_scroll.ScrollOffset > 0)
                    {
                        _scroll.Reset();
                        OnScrollAutoFollow?.Invoke();
                    }
                }
                _lastRenderedLineNo = -1;
                _lastRenderedLastLine = null;
                _lastSnapshotLineCount = 0;
                return;
            }

            var lastLine = lines[^1];
            int currentLineNo = lastLine.LineNo;

            // CLEARLINE 检测（R1 + 修正）：委托纯函数 ClassifyClearLineEvent（plan C 支线(2) 可单测）。
            bool clearlineDetected =
                ClassifyClearLineEvent(snapshot, _lastRenderedLineNo, _lastSnapshotLineCount, _lastRenderedLastLine)
                == FlushClearLineEventType.ClearLine;
            if (clearlineDetected)
            {
                _lastRenderedLineNo = -1;
                _lastRenderedLastLine = null;
            }

            if (_lastRenderedLineNo < 0)
            {
                FullRefresh(snapshot, "init");
                _lastSnapshotLineCount = lines.Count;
                return;
            }

            // 上一轮 FlushBuffer 末行 IsLineEnd=false（PRINTN/PRINTC 等）时，
            // CLI 协议在回合间会写输入提示/回显，光标已不在该行末尾。
            // 增量擦除依赖光标位置会擦错行，改走 FullRefresh（绝对定位）最安全。
            if (_lastRenderedLastLine != null && !_lastRenderedLastLine.isLineEnd)
            {
                FullRefresh(snapshot, "lastLineNotEnd");
                _lastSnapshotLineCount = lines.Count;
                return;
            }

            if (currentLineNo > _lastRenderedLineNo)
            {
                // ADR-0006：新输出到达时 auto-follow 归零 offset。
                // offset>0 时视口锚在历史切片，必须走 FullRefresh（绝对定位）重算布局。
                if (screen != null && _scroll.ScrollOffset > 0)
                {
                    _scroll.Reset();
                    FullRefresh(snapshot, "offset>0");
                    OnScrollAutoFollow?.Invoke();
                }
                // 贴底渲染（2026.10.14 spec §3.4 / B3）：FullRefresh 把光标恒置预留行
                // （终端末行），原光标相对增量（WriteNewLinesSince，本次已删）写在末行会触发
                // 终端滚动 —— ADR-0006 实测 ConPTY 不会把滚出视口的字节重新发送给 master，
                // 后续行永远渲染不出来。故新行一律绝对定位全刷：满屏路径与今天 overflow 分支
                // 逐字节一致，稀疏路径成本 = R 行（标题/首屏行数少，可忽略）。
                else if (screen != null)
                {
                    FullRefresh(snapshot, "newline");
                }
                // screen == null：无 VT 屏无从渲染（VT-only 后调用方保证主循环内必非 null）。
            }
            else
            {
                // LineNo 不变：仅当末行 SourceLine 引用变更时才擦除重写。
                // Phase 4 R3 原删除 ReferenceEquals 检查（"DisplayLine 引用每次必不同"），
                // 但忽略了 no-op 帧（INPUT 等待、DisplayLineList 未变）每帧都进入此分支，
                // 无条件 EraseTerminalRows + WriteDisplayLine 触发 EraseTerminalRows
                // 内部 SetCursor 参数顺序 bug，光标跑到窗口顶部，末行被反复擦写到顶部。
                // 修正：改用 SourceLine（ConsoleDisplayLine）引用比较——Rebuild 浅拷贝 DisplayLineList，
                // 游戏线程未替换行时 SourceLine 引用稳定，正确跳过 no-op 帧。
                if (_lastRenderedLastLine == null ||
                    !ReferenceEquals(_lastRenderedLastLine.SourceLine, lastLine.SourceLine))
                {
                    EraseTerminalRows(1);
                    WriteDisplayLine(lastLine);
                }
            }

            _lastRenderedLineNo = currentLineNo;
            _lastRenderedLastLine = lastLine;
            _lastSnapshotLineCount = lines.Count;
        }

        /// <summary>全量重绘可见行（外部调用：OnScrollChanged/CheckResize/ConsumeNeedFullRefresh）。
        /// Phase 4-1：内部调 _displayState.Current 保证最新快照。</summary>
        public void FullRefresh(string reason = "?")
        {
            var snapshot = _displayState.Current;
            FullRefresh(snapshot, reason);
        }

        /// <summary>全量重绘可见行（内部实现，接受快照）。
        /// ADR-0005 Issue 4：删除非 VT 降级分支，仅保留 VT 备用屏绝对定位路径。
        /// ADR-0006：读 ScrollController.ScrollOffset 计算 startLine + visibleLines。
        /// ADR-0009：offset 从 ScrollController 读，visibleLines 内联算。
        /// 2026.10.14 贴底（spec §3.1）：内容块末行恒锚 <c>visibleLines-1</c>，
        /// 写屏行号 = <c>baseRow + i</c>（<c>baseRow = visibleLines - drawnRows</c>），
        /// 光标恒置预留行 <c>visibleLines</c>。</summary>
        private void FullRefresh(DisplaySnapshot snapshot, string reason = "?")
        {
            var lines = snapshot.lines;
            var screen = _screen!;

            screen.ClearScreen();

            if (_currentBgHex != null)
                WriteBgEscape(_currentBgHex);

            int offset = _scroll.ScrollOffset;
            // ADR-0009：visibleLines 内联算——offset=0 → consoleHeight-1（无状态栏），offset>0 → consoleHeight-2（底部留状态栏）。
            int visibleLines = offset > 0
                ? Math.Max(1, screen.WindowHeight - 2)
                : Math.Max(1, screen.WindowHeight - 1);
            // 贴底渲染（2026.10.14 渲染布局统一 spec §3.1）：内容块末行恒锚 visibleLines-1，
            // 块占终端行 [visibleLines-drawnRows, visibleLines-1]，上方留白。空屏也给出该值，
            // 供 CountdownRenderer 在恒定行定位（空屏显示倒计时的行为变化见 spec §3.2）。
            LastContentRow = visibleLines - 1;

            if (lines.Count == 0)
            {
                _lastRenderedLineNo = -1;
                _lastRenderedLastLine = null;
                _lastSnapshotLineCount = 0;
                return;
            }

            // offset>0 时回看历史：startLine 向前移动 offset 行。
            int startLine = offset > 0
                ? Math.Max(0, lines.Count - visibleLines - offset)
                : Math.Max(0, lines.Count - visibleLines);

            // 本次实际画几行（内容不足一屏时 < visibleLines）。贴底 ⇒ 块起始终端行 = 顶部留白行数。
            int drawnRows = Math.Min(visibleLines, lines.Count - startLine);
            int baseRow = visibleLines - drawnRows;

            for (int i = 0; i < visibleLines && (startLine + i) < lines.Count; i++)
            {
                int lineIndex = startLine + i;
                int viewportRow = baseRow + i;
                var dl = lines[lineIndex];
                // Phase 4-1：经 SourceLine 调 FormatLineForTerminal；CLI 对图片/矩形
                // 按流宽度补空格占位（T-026，BuildTerminalLine 计入 textWidth），不再跳过。
                var sourceLine = dl.SourceLine;
                string formatted = sourceLine != null
                    ? TerminalLineFormatter.FormatLineForTerminal(
                        sourceLine, _buttonContext.SelectingButton, _buttonContext.CharWidthConfig, ansiEnabled: true)
                    : "";
                screen.WriteLineAt(viewportRow, formatted.Length > 0 ? formatted : "");
            }

            // 光标恒置预留行（visibleLines）：输入回显位置 + 末行改写路径（EraseTerminalRows）
            // 的锚点——其擦除 currentRow-1 恰为内容末行。
            screen.SetCursor(visibleLines, 0);
            LastDrawnRows = drawnRows;

            _lastRenderedLineNo = lines[^1].LineNo;
            _lastRenderedLastLine = lines[^1];
            _lastSnapshotLineCount = lines.Count;
        }

        /// <summary>擦除指定行数。ADR-0005 Issue 4：删除非 VT 空格覆盖分支，仅保留 VT ESC[2K 路径。</summary>
        internal void EraseTerminalRows(int rows)
        {
            if (rows <= 0) return;

            var screen = _screen!;
            int currentRow = screen.GetCurrentRow();
            for (int i = 0; i < rows; i++)
            {
                int targetRow = currentRow - 1 - i;
                if (targetRow < 0) break;
                screen.ClearLine(targetRow);
            }
            // 参数顺序修复：SetCursor(row, col)。原代码 SetCursor(0, X) 把 0 当 row、
            // X 当 col，导致光标定位到 row=0（窗口顶部）。应为 row=X（擦除后行位置）、col=0（行首）。
            screen.SetCursor(Math.Max(currentRow - rows, 0), 0);
        }

        private static void WriteBgEscape(string? hexColor)
        {
            if (hexColor == null) return;
            if (!_bgRgbCache.TryGetValue(hexColor, out var rgb))
            {
                string hex = hexColor.TrimStart('#');
                rgb = (
                    Convert.ToInt32(hex.Substring(0, 2), 16),
                    Convert.ToInt32(hex.Substring(2, 2), 16),
                    Convert.ToInt32(hex.Substring(4, 2), 16));
                _bgRgbCache[hexColor] = rgb;
            }
            Console.Write($"\x1b[48;2;{rgb.Item1};{rgb.Item2};{rgb.Item3}m");
        }

        // Phase 4-1（R3）：参数类型从 ConsoleDisplayLine 改为 DisplayLine
        private void WriteDisplayLine(DisplayLine line)
        {
            var sourceLine = line.SourceLine;
            if (sourceLine == null) return;  // 不应发生（BuildSnapshot 总会填入）
            string text = TerminalLineFormatter.FormatLineForTerminal(
                sourceLine, _buttonContext.SelectingButton, _buttonContext.CharWidthConfig, ansiEnabled: true);
            if (line.isLineEnd)
                Console.WriteLine(text);
            else
                Console.Write(text);
        }
    }
}
