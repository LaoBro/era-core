using System;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// CLI 的视口真相源（ADR-0031 决策 4 / spec-v3 M4）。
    /// <para>
    /// <b>为什么不是 <see cref="ScrollController"/> 直接实现 <see cref="IViewportProvider"/></b>：
    /// 它只能回答 <c>ScrollOffset</c>。<c>VisibleHeight</c> 依赖「渲染了多少行」——
    /// 即 <c>lineCount</c> 与终端高度，而 <c>ScrollController</c> 是纯滚动算术
    /// （它的 <c>lineCount</c> 全是<b>参数</b>，从不持有），拿不到这两个数。
    /// 硬让它实现接口，就只好退回 <c>Config.WindowY</c>，那正是真机「顶部按钮点不到」的根因。
    /// </para>
    /// <para>
    /// 故拆成：滚动位置由 <see cref="ScrollController"/> 提供，几何高度由本类按
    /// <see cref="TerminalViewportGeometry"/> 现算。两个 <see cref="Func{TResult}"/> 是<b>惰性</b>的 ——
    /// 本对象在 <c>AgentCliProtocol</c> 构造期创建，那时 VT 屏幕还没建立。
    /// </para>
    /// </summary>
    internal sealed class CliViewport : IViewportProvider
    {
        private readonly ScrollController _scroll;
        private readonly Func<int> _lineCount;
        private readonly Func<int> _terminalHeight;

        internal CliViewport(ScrollController scroll, Func<int> lineCount, Func<int> terminalHeight)
        {
            _scroll = scroll;
            _lineCount = lineCount;
            _terminalHeight = terminalHeight;
        }

        /// <summary>距底偏移，直接来自 CLI 的真实滚动。</summary>
        public int ScrollOffset => _scroll.ScrollOffset;

        /// <summary>渲染行数 × 行高 —— 不是 <c>Config.WindowY</c>，见类注释。</summary>
        public int VisibleHeight
            => TerminalViewportGeometry.VisibleHeightPixels(_lineCount(), _terminalHeight(), _scroll.ScrollOffset);

        public int LineHeight => Config.LineHeight;
    }
}
