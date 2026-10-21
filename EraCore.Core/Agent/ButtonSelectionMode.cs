using System;
using System.Collections.Generic;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 按钮选择模式：管理方向键导航、高亮、确认，以及 VT 鼠标区域同步。
    /// ADR-0005：VT-only 后原 <c>_ansiEnabled</c> 字段已删除，ANSI 路径直接走。
    /// ADR-0005 Issue 4：删除 <c>_getScreen() != null</c> / <c>vtInput == null</c>
    /// 降级分支与 <c>_requestFullRefresh</c> 字段，调用方保证 VT 主循环内必非 null。
    /// 从 AgentCliProtocol 拆分以隔离按钮相关状态与逻辑。
    /// </summary>
    internal sealed class ButtonSelectionMode : IButtonSelection
    {
        private readonly EmueraConsole _console;
        private readonly ScrollController _scroll;
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private readonly Action<string> _dispatchInput;
        private readonly Action _clearInputBuffer;
        // Phase 3-3b：DisplayState 注入（Phase 4 起 AgentCliProtocol 传入自有实例）。
        // 当前 Phase 3 阶段为 null——RefreshButtonRegionsFromSnapshot 仅在 Phase 4 接线后调用。
        private readonly DisplayState? _displayState;

        private bool _buttonMode;
        private List<ButtonPos> _buttonPositions = [];
        private int _selectedButtonIndex = -1;
        private long _lastButtonSyncGen = -1;
        private long _lastRegionGeneration = -1;

        public ButtonSelectionMode(
            EmueraConsole console,
            ScrollController scroll,
            Func<AgentCliVtScreen?> getScreen,
            Action<string> dispatchInput,
            Action clearInputBuffer,
            DisplayState? displayState = null)
        {
            _console = console;
            _scroll = scroll;
            _getScreen = getScreen;
            _dispatchInput = dispatchInput;
            _clearInputBuffer = clearInputBuffer;
            _displayState = displayState;
        }

        public bool IsButtonMode => _buttonMode;
        public bool HasButtons => _buttonPositions.Count > 0;

        /// <summary>
        /// 处理按键。返回 true 表示已消费（按钮模式），false 表示未消费需常规处理。
        /// </summary>
        internal bool HandleKey(ConsoleKeyInfo key)
        {
            bool isArrow = key.Key == ConsoleKey.UpArrow
                || key.Key == ConsoleKey.DownArrow
                || key.Key == ConsoleKey.LeftArrow
                || key.Key == ConsoleKey.RightArrow;

            if (_buttonMode)
            {
                if (key.Key == ConsoleKey.Enter || isArrow)
                {
                    ProcessButtonModeKey(key);
                    return true;
                }
                ExitButtonMode();
                return false;
            }

            if (isArrow && _buttonPositions.Count > 0)
            {
                _buttonMode = true;
                _selectedButtonIndex = 0;
                _console.SetSelectingButton(_buttonPositions[0].Button);
                _clearInputBuffer();
                RedrawButtonLine(_buttonPositions[0].Row);
                ProcessButtonModeKey(key);
                return true;
            }

            return false;
        }

        /// <summary>鼠标点击时退出按钮模式（若有）。</summary>
        internal void ExitButtonMode() => ExitButtonModeCore();

        /// <summary>同步按钮位置状态，按需进入/退出选择模式或更新高亮。
        /// ADR-0006：Scroll Mode（offset>0）下跳过——按钮命中区由 RefreshButtonRegions 清空，
        /// 不维护选择模式状态（避免误触旧 Generation 按钮）。</summary>
        public void SyncButtonState()
        {
            if (_scroll.ScrollOffset > 0) return;

            bool shouldHaveButtons = false;
            List<ButtonPos> newPositions = [];

            if (_console.State == ConsoleState.WaitInput)
            {
                var req = _console.CurrentRequest;
                if (req != null && req.InputType != InputType.EnterKey && req.InputType != InputType.AnyKey)
                {
                    long currentGen = _console.LastButtonGeneration;
                    if (_buttonMode && currentGen == _lastButtonSyncGen)
                    {
                        return;
                    }

                    newPositions = RebuildButtonPositions();
                    _lastButtonSyncGen = currentGen;
                    if (newPositions.Count > 0)
                        shouldHaveButtons = true;
                }
            }
            else
            {
                _lastButtonSyncGen = -1;
            }

            if (!shouldHaveButtons)
            {
                if (_buttonMode) ExitButtonModeCore();
                _buttonPositions = [];
            }
            else
            {
                // 按钮存在时仅维护位置列表，不自动进入选择模式；
                // 选择模式由玩家按方向键显式触发。
                if (_buttonMode)
                {
                    if (!ButtonInputKeysEqual(_buttonPositions, newPositions))
                    {
                        int oldRow = _selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count
                            ? _buttonPositions[_selectedButtonIndex].Row : -1;
                        _buttonPositions = newPositions;
                        _selectedButtonIndex = Math.Clamp(_selectedButtonIndex, 0, newPositions.Count - 1);
                        _console.SetSelectingButton(newPositions[_selectedButtonIndex].Button);
                        if (oldRow >= 0) RedrawButtonLine(oldRow);
                        int newRow = newPositions[_selectedButtonIndex].Row;
                        if (newRow != oldRow)
                            RedrawButtonLine(newRow);
                    }
                }
                else
                {
                    _buttonPositions = newPositions;
                }
            }
        }

        /// <summary>
        /// Phase 3-3b 新路径：从 DisplayState.Current 快照构建命中区（value-based，单次调用）。
        /// 直接调 <see cref="ButtonRegionTracker.UpdateFromSnapshot"/>；Generation 过滤移除（服务端兜底）。
        /// Phase 4 已将全部调用点从旧 RefreshButtonRegions 切换到此方法。
        /// </summary>
        /// <remarks>
        /// 调用前置：_displayState 必须已注入（Phase 4 AgentCliProtocol 传入自有实例）。
        /// </remarks>
        public void RefreshButtonRegionsFromSnapshot(VtInputHandler vtInput, bool force = false)
        {
            if (_displayState == null)
                throw new InvalidOperationException(
                    "RefreshButtonRegionsFromSnapshot requires DisplayState injection (Phase 4).");

            // ADR-0006：Scroll Mode 下清空命中区，不更新 _lastRegionGeneration
            // （退出 scroll mode 时由 force=true 路径重建）。
            if (_scroll.ScrollOffset > 0)
            {
                vtInput.ClearRegions();
                return;
            }

            // 非按钮模式或无请求时清除所有区域，防止过期按钮被点击触发
            var req = _console.CurrentRequest;
            if (req == null || req.InputType == InputType.EnterKey || req.InputType == InputType.AnyKey)
            {
                vtInput.ClearRegions();
                _lastRegionGeneration = _console.LastButtonGeneration;
                return;
            }

            long currentGen = _console.LastButtonGeneration;
            if (!force && currentGen == _lastRegionGeneration) return;
            _lastRegionGeneration = currentGen;

            // 单次调用：直接调 tracker.UpdateFromSnapshot，不经 RecordLineRegions 转发。
            // 2026.10.14 贴底（spec §3.3）：视口行数 / 起始行 / 贴底基准行收敛到
            // TerminalViewportGeometry —— 原先此处自算 `viewportHeight = windowHeight - 1`
            // 是第六处重复公式，且与渲染侧的 baseRow 无关联，贴底后必然错位。
            var snapshot = _displayState.Current;
            int terminalHeight = _getScreen()!.WindowHeight;
            vtInput.Tracker.UpdateFromSnapshot(snapshot, _scroll.ScrollOffset, terminalHeight);
        }

        /// <summary>
        /// 遍历可见 displayLineList，按终端显示宽度计算每个按钮的 (row, left, right)。
        /// 同一 InputKey 的分裂片段（DivideAt 产生）只保留第一个。
        /// </summary>
        private List<ButtonPos> RebuildButtonPositions()
        {
            var result = new List<ButtonPos>();
            var lines = _console.DisplayLineList;
            if (lines == null || lines.Count == 0) return result;

            int terminalHeight = _getScreen()!.WindowHeight;
            // 2026.10.14 贴底（spec §3.3）：几何收敛到 TerminalViewportGeometry（第四/五处重复公式）。
            // 本方法只在 offset==0 时被调用（SyncButtonState 在 offset>0 时提前返回）。
            int startLine = TerminalViewportGeometry.StartLine(lines.Count, terminalHeight, 0);
            int rendered = TerminalViewportGeometry.RenderedLines(lines.Count, terminalHeight, 0);
            // ButtonPos.Row 必须是终端绝对行（见 UpdateFromSnapshot 注释）：SGR mouse 上报绝对行，
            // TerminalRenderer 把内容第 i 行写在 baseRow + i。
            int baseRow = TerminalViewportGeometry.BaseRow(lines.Count, terminalHeight, 0);

            long currentGen = _console.LastButtonGeneration;
            // 整数输入语义下（IntValue/IntButton/AnyValue），字符串按钮提交后会被
            // DispatchInput 的 long.TryParse 拒绝、或 SelectedString 返回 null，
            // 属于按了无反应的噪声按钮，这里统一过滤掉。
            var reqType = _console.CurrentRequest?.InputType;
            bool intMode = reqType == InputType.IntValue
                || reqType == InputType.IntButton
                || reqType == InputType.AnyValue;
            var seenKeys = new HashSet<string>();

            for (int i = 0; i < rendered; i++)
            {
                int lineIndex = startLine + i;
                var line = lines[lineIndex];
                if (line?.Buttons == null || line.Buttons.Length == 0) continue;

                string formatted = TerminalLineFormatter.FormatLineForTerminal(
                    line, _console.SelectingButton, _console.CharWidthConfig, ansiEnabled: true);
                int column = TerminalDisplayWidth.LeadingDisplayWidth(formatted);

                foreach (var btn in line.Buttons)
                {
                    if (btn == null) continue;
                    string btnText = btn.ToString() ?? "";
                    int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                    if (btn.IsButton && btn.Generation == currentGen && segmentWidth > 0
                        && (!intMode || btn.IsInteger))
                    {
                        string key = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
                        if (seenKeys.Add(key))
                            result.Add(new ButtonPos(baseRow + i, column, column + segmentWidth - 1, btn, key));
                    }
                    column += segmentWidth;
                }
            }
            return result;
        }

        /// <summary>
        /// 重绘指定终端行的按钮内容（含选中高亮）。
        /// ADR-0005 Issue 4：删除非 VT FullRefresh 退化分支，仅保留 VT 绝对定位单行重绘。
        /// 2026.10.14 贴底（spec §3.3）：<paramref name="row"/> 是<b>终端绝对行</b>
        /// （= <c>baseRow + i</c>，来自 <c>ButtonPos.Row</c>）—— 贴底前它是视口内相对行号。
        /// 本方法反解出内容行索引（<c>i = row − baseRow</c>）后按绝对行写屏。
        /// </summary>
        private void RedrawButtonLine(int row)
        {
            var screen = _getScreen()!;
            int savedRow, savedCol;
            try { savedRow = Console.CursorTop; savedCol = Console.CursorLeft; }
            catch (Exception) { /* 光标位置探测失败，用 0,0 */ savedRow = 0; savedCol = 0; }

            var lines = _console.DisplayLineList;
            int terminalHeight = screen.WindowHeight;
            int startLine = TerminalViewportGeometry.StartLine(lines.Count, terminalHeight, 0);
            int baseRow = TerminalViewportGeometry.BaseRow(lines.Count, terminalHeight, 0);
            int lineIndex = startLine + (row - baseRow);
            if (lineIndex >= 0 && lineIndex < lines.Count)
            {
                string formatted = TerminalLineFormatter.FormatLineForTerminal(
                    lines[lineIndex], _console.SelectingButton, _console.CharWidthConfig, ansiEnabled: true);
                screen.WriteLineAt(row, formatted.Length > 0 ? formatted : "");
            }

            screen.SetCursor(savedRow, savedCol);
        }

        private void ProcessButtonModeKey(ConsoleKeyInfo key)
        {
            if (_buttonPositions.Count == 0) return;

            if (key.Key == ConsoleKey.Enter)
            {
                ConfirmButton();
                return;
            }

            int currentIdx = (_selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count)
                ? _selectedButtonIndex
                : 0;

            var dir = key.Key switch
            {
                ConsoleKey.UpArrow => ButtonNavigator.Direction.Up,
                ConsoleKey.DownArrow => ButtonNavigator.Direction.Down,
                ConsoleKey.LeftArrow => ButtonNavigator.Direction.Left,
                ConsoleKey.RightArrow => ButtonNavigator.Direction.Right,
                _ => (ButtonNavigator.Direction?)null,
            };
            if (dir == null) return;

            int newIdx = ButtonNavigator.FindNext(_buttonPositions, _buttonPositions[currentIdx], dir.Value);

            if (newIdx >= 0 && newIdx != currentIdx)
            {
                int oldRow = _buttonPositions[currentIdx].Row;
                int newRow = _buttonPositions[newIdx].Row;
                _selectedButtonIndex = newIdx;
                _console.SetSelectingButton(_buttonPositions[newIdx].Button);
                RedrawButtonLine(oldRow);
                if (newRow != oldRow)
                    RedrawButtonLine(newRow);
            }
        }

        private void ConfirmButton()
        {
            if (_selectedButtonIndex < 0 || _selectedButtonIndex >= _buttonPositions.Count) return;

            var btn = _buttonPositions[_selectedButtonIndex].Button;
            string input = btn.IsInteger ? btn.Input.ToString() : btn.Inputs;
            ExitButtonModeCore();
            _dispatchInput(input);
        }

        private void ExitButtonModeCore()
        {
            int highlightedRow = -1;
            if (_selectedButtonIndex >= 0 && _selectedButtonIndex < _buttonPositions.Count)
                highlightedRow = _buttonPositions[_selectedButtonIndex].Row;

            _console.SetSelectingButton(null);
            _buttonMode = false;
            _buttonPositions = [];
            _selectedButtonIndex = -1;

            if (highlightedRow >= 0)
                RedrawButtonLine(highlightedRow);
        }

        private static bool ButtonInputKeysEqual(List<ButtonPos> a, List<ButtonPos> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].InputKey != b[i].InputKey) return false;
            }
            return true;
        }
    }
}
