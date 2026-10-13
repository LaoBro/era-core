using System;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>Scroll Mode 滚动热键动作（ADR-0006 Issue 003）。</summary>
    internal enum ScrollAction
    {
        Home,
        End,
        PageUp,
        PageDown,
    }

    /// <summary>
    /// 纯逻辑视口偏移控制器（ADR-0009）。
    /// 持有 ScrollOffset + 算术（ScrollBy/ScrollTo/Clamp/Reset + ApplyAction）+ ScrollChanged 事件 + _scrollVisibleLines（构造注入）。
    /// ScrollBy/ScrollTo/Clamp 在 offset 变化时 raise 事件，Reset() 静默（系统归零由调用方编排渲染）。
    /// 替代 AgentCliVtScreen 的 scroll 算术职责——screen 只保留 VT I/O。
    /// ADR-0006：滚动热键语义（ScrollAction → 算子）随 ApplyAction 一并收归本类，协议层只做一行转发。
    /// <para>
    /// ADR-0031 / spec-v3 M3：CLI 的<b>真实</b>滚动位置由本类进入引擎几何判定
    /// （<c>ConsoleInputHandler.MoveMouse</c>），取代此前恒读 <c>ScrollBar.Value</c> 的脱节路径（缺口 F）。
    /// <para>
    /// ⚠️ <b>本类不实现 <see cref="MinorShift.Emuera.UI.Game.IViewportProvider"/></b>（M4 修正）：
    /// 它只能回答 <c>ScrollOffset</c>，而 <c>VisibleHeight</c> 需要 <c>lineCount</c> 与终端高度 ——
    /// 本类从不持有这两个数（<c>lineCount</c> 全是方法参数）。硬实现就只能退回
    /// <c>Config.WindowY</c>，那正是真机「顶部按钮点不到」的根因。
    /// 视口真相源是 <see cref="CliViewport"/>：它组合本类的 offset 与
    /// <see cref="TerminalViewportGeometry"/> 现算的几何高度。
    /// </para>
    /// </para>
    /// </summary>
    internal sealed class ScrollController
    {
        private int _scrollVisibleLines;
        private int _scrollOffset;

        /// <param name="scrollVisibleLines">Scroll Mode 视口高度（= WindowHeight - 2，底部留状态栏）。</param>
        internal ScrollController(int scrollVisibleLines)
        {
            _scrollVisibleLines = Math.Max(1, scrollVisibleLines);
        }

        /// <summary>
        /// 当前视口偏移。0 = 跟随底部（正常模式）；&gt;0 = 回看历史（Scroll Mode）。
        /// 语义 = <b>距底偏移</b>，与引擎侧 <c>bottomLineNo = lineCount − ScrollOffset − 1</c> 配套。
        /// </summary>
        internal int ScrollOffset => _scrollOffset;

        /// <summary>是否处于 Scroll Mode（offset > 0）。</summary>
        internal bool IsScrollMode => _scrollOffset > 0;

        /// <summary>offset 变化时 raise（用户主动滚动路径）。Reset() 不 raise（系统归零静默）。</summary>
        internal event Action<int>? ScrollChanged;

        /// <summary>相对偏移滚动 delta 行；正=向上回看历史，负=向下回底。钳到 [0, maxOffset]。返回新 offset。</summary>
        internal int ScrollBy(int delta, int lineCount)
            => SetScrollOffset(_scrollOffset + delta, lineCount);

        /// <summary>设置绝对 offset；钳到 [0, maxOffset]。返回新 offset。</summary>
        internal int ScrollTo(int target, int lineCount)
            => SetScrollOffset(target, lineCount);

        /// <summary>将当前 offset 钳到新 max（resize 后调用）。返回新 offset。</summary>
        internal int Clamp(int lineCount)
        {
            if (_scrollOffset <= 0) { _scrollOffset = 0; return 0; }
            int maxOffset = MaxOffset(lineCount);
            int newOffset = Math.Min(_scrollOffset, maxOffset);
            return UpdateAndMaybeRaise(newOffset);
        }

        /// <summary>归零 offset（auto-follow / ClearOp / ConsumeNeedFullRefresh 调用）。静默不 raise 事件。</summary>
        internal void Reset() => _scrollOffset = 0;

        /// <summary>更新视口高度（resize 后调用）。不自动 clamp——下次 ScrollBy/Clamp 才生效。</summary>
        internal void UpdateVisibleLines(int newVisibleLines)
        {
            _scrollVisibleLines = Math.Max(1, newVisibleLines);
        }

        private int SetScrollOffset(int target, int lineCount)
        {
            if (target <= 0) { return UpdateAndMaybeRaise(0); }
            int maxOffset = MaxOffset(lineCount);
            int newOffset = Math.Min(target, maxOffset);
            return UpdateAndMaybeRaise(newOffset);
        }

        private int UpdateAndMaybeRaise(int newOffset)
        {
            int oldOffset = _scrollOffset;
            _scrollOffset = newOffset;
            if (newOffset != oldOffset)
            {
                ScrollChanged?.Invoke(newOffset);
            }
            return newOffset;
        }

        private int MaxOffset(int lineCount)
            => Math.Max(0, lineCount - _scrollVisibleLines);

        /// <summary>
        /// 将滚动热键动作翻译为偏移算子并应用（ADR-0006）。
        /// PageUp/PageDown 的 delta 用动态 visibleLines：offset==0 用正常模式（W-1），
        /// offset>0 用 Scroll Mode（W-2），保持与原 AgentCliVtScreen.GetVisibleLines() 行为零变化。
        /// Home 滚到顶（ScrollTo(int.MaxValue) 钳到 max），End 回底退出 Scroll Mode（ScrollTo(0)）。
        /// </summary>
        internal void ApplyAction(ScrollAction action, int windowHeight, int lineCount)
        {
            int pageLines = IsScrollMode
                ? Math.Max(1, windowHeight - 2)
                : Math.Max(1, windowHeight - 1);

            switch (action)
            {
                case ScrollAction.Home:
                    ScrollTo(int.MaxValue, lineCount);
                    break;
                case ScrollAction.End:
                    ScrollTo(0, lineCount);
                    break;
                case ScrollAction.PageUp:
                    ScrollBy(pageLines, lineCount);
                    break;
                case ScrollAction.PageDown:
                    ScrollBy(-pageLines, lineCount);
                    break;
                default:
                    return;
            }
        }
    }
}
