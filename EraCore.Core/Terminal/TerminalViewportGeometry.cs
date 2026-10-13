using System;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// CLI（VT 终端）视口 ↔ 游戏可绘制区（像素）的坐标换算（ADR-0031 / spec-v3 M4）。
    /// <para>
    /// 🔴 <b>Y 为什么不是 <c>row × LineHeight</c></b>：CLI 把内容<b>顶部对齐</b>渲染
    /// （<c>TerminalRenderer</c>：<c>startLine = max(0, lineCount − visibleLines − offset)</c>，
    /// 然后 <c>WriteLineAt(viewportRow = i)</c>），而引擎 <c>MoveMouse</c> 的几何模型是
    /// <b>底部对齐</b> —— 原版 <c>GetLinePointY(lineNo) = (H − LineHeight) − (bottomLineNo − lineNo) × LineHeight</c>，
    /// 即「内容最后一行贴在可绘制区底部」。
    /// </para>
    /// <para>
    /// 两个布局差一个<b>常数</b>：<c>WindowY − renderedLines × LineHeight</c>。
    /// 不补它，<c>relPointY</c> 会整体平移，命中判定系统性错位
    /// （真机实测：按钮「经常点不到」，底行 <c>relPointY = −5</c> 而非 0）。
    /// </para>
    /// <para>
    /// ⚠️ <c>startLine</c> 的公式与 <c>TerminalRenderer</c> / <c>ButtonRegionTracker</c>
    /// 同式 —— 布局真相源目前分散在三处，改动任一方须同步其余。
    /// </para>
    /// </summary>
    internal static class TerminalViewportGeometry
    {
        /// <summary>终端列 → 游戏像素 X。<c>charWidth = max(FontSize / 2, 1)</c>，与 <c>HeadlessRunner</c> 建议终端尺寸的表达式同源（互逆）。</summary>
        internal static int PointXAtViewportCol(int col) => col * Math.Max(Config.FontSize / 2, 1);

        /// <summary>
        /// 终端行 → 游戏像素 Y（左上基准）。
        /// <para>
        /// 语义：<b>渲染出来的那一块内容贴在游戏可绘制区底部</b>。
        /// 于是内容最后一行 ⇒ <c>Y = WindowY − LineHeight</c>（<c>MOUSEY = −LineHeight</c>）；
        /// 内容下方的空白/状态栏 ⇒ <c>MOUSEY ≥ 0</c>（对应原版输入框区域 <c>MY ∈ [0, +20]</c>）。
        /// </para>
        /// </summary>
        /// <param name="row">终端视口行（0-based）。</param>
        /// <param name="lineCount">引擎 <c>displayLineList</c> 行数。</param>
        /// <param name="terminalHeight">终端高度（行数）；<c>0</c> 或负表示尚未建立屏幕，退化为游戏视口行数。</param>
        /// <param name="scrollOffset">距底偏移（Scroll Mode）。</param>
        internal static int PointYAtViewportRow(int row, int lineCount, int terminalHeight, int scrollOffset)
        {
            int lineHeight = Config.LineHeight;

            // 与 TerminalRenderer 同式：正常模式留 1 行（光标/倒计时），Scroll Mode 留 2 行（+状态栏）。
            int visibleLines = terminalHeight > 0
                ? Math.Max(1, terminalHeight - (scrollOffset > 0 ? 2 : 1))
                : Math.Max(1, Config.WindowY / lineHeight);

            int startLine = Math.Max(0, lineCount - visibleLines - scrollOffset);

            // 与 ConsoleInputHandler.MoveMouse 同式（含同一处上界钳制）。
            int bottomLineNo = lineCount - scrollOffset - 1;
            if (lineCount - 1 < bottomLineNo)
                bottomLineNo = lineCount - 1;
            if (bottomLineNo < 0)
                bottomLineNo = 0;

            int lineNo = startLine + row;

            // 原版 GetLinePointY 的逆用：把 engine 行号换回像素 Y。
            return (Config.WindowY - lineHeight) - (bottomLineNo - lineNo) * lineHeight;
        }
    }
}
