using System;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 倒计时行渲染：在终端固定行覆盖写入倒计时文本。
    /// ADR-0005：VT-only 后原 <c>_ansiEnabled</c> 字段已删除，ANSI 路径直接走。
    /// ADR-0005 Issue 4：删除非 VT 光标定位分支与 <c>_cursor</c> 字段，仅保留 VT 绝对定位。
    /// 从 AgentCliProtocol 拆分以隔离倒计时显示状态。
    /// 2026.10.14 贴底（spec §3.2）：行位置改读 <c>TerminalRenderer.LastContentRow</c>
    /// （贴底后恒为 visibleLines−1，与内容量无关），不再由 LastDrawnRows 推算。
    /// </summary>
    internal sealed class CountdownRenderer : ICountdownRenderer
    {
        private readonly EmueraConsole _console;
        private readonly Func<int> _getScrollOffset;
        private readonly Func<AgentCliVtScreen?> _getScreen;
        private readonly Func<int> _getLastContentRow;

        // 倒计时行状态（viewport row，备用屏下与 buffer row 等价）
        private int _countdownLineRow = -1;
        private string _lastCountdownText = "";
        private int _lastCountdownWidth;

        public CountdownRenderer(
            EmueraConsole console,
            Func<int> getScrollOffset,
            Func<AgentCliVtScreen?> getScreen,
            Func<int> getLastContentRow)
        {
            _console = console;
            _getScrollOffset = getScrollOffset;
            _getScreen = getScreen;
            _getLastContentRow = getLastContentRow;
        }

        /// <summary>检测倒计时状态变化并刷新显示；倒计时结束时重置。
        /// ADR-0006：Scroll Mode（offset>0）下跳过 Update，不在历史行上覆盖倒计时。
        /// ADR-0007：接受外部传入的 elapsedMs（CLI 挂钟），绕过失效的 stopwatch。
        /// ADR-0009：offset 从 Func<int> 读（替代 Func<AgentCliVtScreen?>）。
        /// 贴底（2026.10.14 spec §3.2）：倒计时行位置从 TerminalRenderer.LastContentRow 读
        /// （visibleLines−1，恒定），替代 LastDrawnRows 推算与不可靠的 Console.CursorTop 探测
        /// （后者在 ConPTY 下间歇抛异常或返回错误值导致倒计时永不渲染）。</summary>
        internal void Update(long elapsedMs)
        {
            // Scroll Mode 下跳过：光标在状态栏行，倒计时行位置失效，
            // 且在历史切片上覆盖倒时会污染历史视图。
            if (_getScrollOffset() > 0) return;

            if (_console.IsDisplayTimeActive)
            {
                string currentText = _console.BuildCountdownText(elapsedMs);
                if (currentText != _lastCountdownText)
                {
                    if (_countdownLineRow < 0)
                    {
                        int lastContentRow = _getLastContentRow();
                        _countdownLineRow = lastContentRow >= 0 ? lastContentRow : -1;
                    }
                    Overwrite(currentText);
                }
            }
            else if (_countdownLineRow >= 0)
            {
                Reset();
            }
        }

        /// <summary>用超时消息覆盖倒计时行。ADR-0005 Issue 4：删除非 VT 光标定位分支，仅保留 VT 绝对定位。</summary>
        internal void Overwrite(string newText)
        {
            if (_countdownLineRow < 0) return;

            string padded = PadToWidth(newText, _lastCountdownWidth, out int newWidth);

            // VT 模式：绝对定位 + 清行尾
            _getScreen()!.WriteLineAt(_countdownLineRow, padded);

            _lastCountdownText = newText;
            _lastCountdownWidth = Math.Max(newWidth, _lastCountdownWidth);
        }

        void ICountdownRenderer.Update(long elapsedMs) => Update(elapsedMs);

        void ICountdownRenderer.Overwrite(string newText) => Overwrite(newText);

        /// <summary>重置倒计时行状态。</summary>
        public void Reset()
        {
            _countdownLineRow = -1;
            _lastCountdownText = "";
            _lastCountdownWidth = 0;
        }

        private static int GetDisplayWidth(string str) => TerminalDisplayWidth.GetDisplayWidth(str);

        private static string PadToWidth(string text, int minWidth, out int actualWidth)
        {
            actualWidth = GetDisplayWidth(text);
            if (actualWidth < minWidth)
                return text + new string(' ', minWidth - actualWidth);
            return text;
        }
    }
}
