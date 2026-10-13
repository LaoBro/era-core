using System;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// CLI（VT 终端）视口 ↔ 游戏可绘制区（像素）的坐标换算（ADR-0031 / spec-v3 M4）。
    /// <para>
    /// 🔴 <b>两个反直觉但必须成立的结论</b>（都由真机「按钮点不到」倒推出来）：
    /// <list type="number">
    /// <item>
    /// <b>视口高度 <c>H</c> 是「渲染行数 × 行高」，不是 <c>Config.WindowY</c></b>。
    /// 引擎 <c>MoveMouse</c> 用 <c>H</c> 同时做两件事：<c>topLineNo = bottomLineNo − H/LineHeight</c>
    /// 与 <c>relPointY = pointY − H</c>。若 <c>H</c> 固定为 480（25 行）而终端渲染 32 行，
    /// 多出来的 7 行会落到 <c>y &lt; 0</c> ⇒ 被 <c>ClientRectangle.Contains</c> 挡掉 ⇒ 顶部按钮永远点不到。
    /// 这与 spec-v3 §6 引擎-12 一致：「<c>VisibleHeight</c> 由宿主提供，CLI 视口 = 终端高度，≠ <c>WindowY</c>」。
    /// </item>
    /// <item>
    /// <b>于是 <c>y = row × LineHeight</c> 就是对的</b>（顶部对齐 + 视口高 = 渲染高，二者自洽）。
    /// 代入可验证：<c>relPointY = (row − R)·LH + LH·(R − row) = 0</c>，与 <c>row</c> 无关 ⇒ 恒定命中。
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ 本类的 <c>StartLine</c>/<c>VisibleLines</c> 与 <c>TerminalRenderer</c>、
    /// <c>ButtonRegionTracker</c> 同式 —— 布局真相源目前分散在三处，改任一方须同步其余。
    /// </para>
    /// </summary>
    internal static class TerminalViewportGeometry
    {
        /// <summary>终端视口可容纳的内容行数。正常模式留 1 行（光标/倒计时），Scroll Mode 多留 1 行（状态栏）。</summary>
        internal static int VisibleLines(int terminalHeight, int scrollOffset)
            => terminalHeight > 0
                ? Math.Max(1, terminalHeight - (scrollOffset > 0 ? 2 : 1))
                : Math.Max(1, Config.WindowY / Config.LineHeight);

        /// <summary>渲染到终端的<b>第一行</b>的内容行号（与 <c>TerminalRenderer</c> 同式）。</summary>
        internal static int StartLine(int lineCount, int terminalHeight, int scrollOffset)
            => Math.Max(0, lineCount - VisibleLines(terminalHeight, scrollOffset) - scrollOffset);

        /// <summary>
        /// 实际渲染出来的行数。内容不足一屏时小于 <see cref="VisibleLines"/> ——
        /// 这直接决定 <c>VisibleHeight</c>，进而决定命中判定的扫描范围。
        /// </summary>
        internal static int RenderedLines(int lineCount, int terminalHeight, int scrollOffset)
        {
            int start = StartLine(lineCount, terminalHeight, scrollOffset);
            return Math.Max(0, Math.Min(lineCount - start, VisibleLines(terminalHeight, scrollOffset)));
        }

        /// <summary>
        /// 可绘制区高度（像素）= 渲染行数 × 行高。
        /// <b>必须由宿主提供而非取 <c>Config.WindowY</c></b> —— 理由见类注释 ①。
        /// </summary>
        internal static int VisibleHeightPixels(int lineCount, int terminalHeight, int scrollOffset)
            => Math.Max(1, RenderedLines(lineCount, terminalHeight, scrollOffset) * Config.LineHeight);

        /// <summary>终端列 → 游戏像素 X。<c>charWidth = max(FontSize / 2, 1)</c>，与 <c>HeadlessRunner</c> 建议终端尺寸的表达式同源（互逆）。</summary>
        internal static int PointXAtViewportCol(int col) => col * Math.Max(Config.FontSize / 2, 1);

        /// <summary>
        /// 终端行 → 游戏像素 Y（左上基准）。<c>row ∈ [0, RenderedLines)</c> 时值域为
        /// <c>[0, VisibleHeight − LineHeight]</c>，全部落在可绘制区内 ⇒ 每一行都可命中。
        /// </summary>
        internal static int PointYAtViewportRow(int row) => row * Config.LineHeight;
    }
}
