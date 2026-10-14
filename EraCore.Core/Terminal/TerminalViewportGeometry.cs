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
    /// <b>视口高度 <c>H</c> 是「视口行数 × 行高」，不是 <c>Config.WindowY</c></b>。
    /// 引擎 <c>MoveMouse</c> 用 <c>H</c> 同时做两件事：<c>topLineNo = bottomLineNo − H/LineHeight</c>
    /// 与 <c>relPointY = pointY − H</c>。若 <c>H</c> 固定为 480（25 行）而终端渲染 32 行，
    /// 多出来的 7 行会落到 <c>y &lt; 0</c> ⇒ 被 <c>ClientRectangle.Contains</c> 挡掉 ⇒ 顶部按钮永远点不到。
    /// 这与 spec-v3 §6 引擎-12 一致：「<c>VisibleHeight</c> 由宿主提供，CLI 视口 = 终端高度，≠ <c>WindowY</c>」。
    /// </item>
    /// <item>
    /// <b>于是 <c>y = 终端绝对行 × LineHeight</c> 就是对的</b>。2026.10.14 起渲染改<b>贴底</b>
    /// （spec §3.1：内容块末行恒锚 <c>visibleLines − 1</c>），取 <c>H = VisibleLines × LH</c> 后：
    /// <c>relPointY = (baseRow + i)·LH − H + (bottomLineNo − lineNo + 1)·LH ≡ 0</c>，
    /// 与 <c>row</c> 无关 ⇒ 恒定命中（推导见 spec §1.1；改动前取 <c>RenderedLines × LH</c>，
    /// 稀疏画面下 <c>MY</c> 会随内容量漂移）。
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// ⚠️ <c>StartLine</c>/<c>VisibleLines</c> 与 <c>TerminalRenderer</c>、<c>ButtonRegionTracker</c>、
    /// <c>ButtonSelectionMode</c>（×2）、<c>RefreshButtonRegionsFromSnapshot</c> 同式 ——
    /// 布局真相源目前分散在六处，改任一方须同步其余（统一收敛计划见 spec §3.3）。
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
        /// 实际渲染出来的行数。内容不足一屏时小于 <see cref="VisibleLines"/>。
        /// </summary>
        internal static int RenderedLines(int lineCount, int terminalHeight, int scrollOffset)
        {
            int start = StartLine(lineCount, terminalHeight, scrollOffset);
            return Math.Max(0, Math.Min(lineCount - start, VisibleLines(terminalHeight, scrollOffset)));
        }

        /// <summary>
        /// 贴底渲染下内容块的<b>起始终端行</b>（= 顶部留白行数 = <c>VisibleLines − RenderedLines</c>）。
        /// 渲染器写屏行号与按钮命中区 <c>Region.Row</c> 都必须是 <c>BaseRow + i</c> ——
        /// SGR mouse 上报的是终端绝对行（<c>VtInputHandler</c>），两者必须逐行一致。
        /// 内容占满视口或 Scroll Mode 下为 0（贴底 = 顶部对齐）。
        /// </summary>
        internal static int BaseRow(int lineCount, int terminalHeight, int scrollOffset)
            => VisibleLines(terminalHeight, scrollOffset) - RenderedLines(lineCount, terminalHeight, scrollOffset);

        /// <summary>
        /// 可绘制区高度（像素）= <b>视口行数</b> × 行高（与内容量无关，稳定）。
        /// <b>必须由宿主提供而非取 <c>Config.WindowY</c></b> —— 理由见类注释 ①。
        /// <para>
        /// ⚠️ 2026.10.14 贴底改造起不再随内容量收缩：改动前取 <c>RenderedLines × LH</c>，
        /// 稀疏画面下 <c>MY = rawY − H</c> 会随内容增减漂移（标定记录 §5.4 实测偏差
        /// = (可视行数 − 内容行数) × 行高）；贴底 + 视口高度后 <c>H</c> 恒定。
        /// </para>
        /// </summary>
        internal static int VisibleHeightPixels(int lineCount, int terminalHeight, int scrollOffset)
            => Math.Max(1, VisibleLines(terminalHeight, scrollOffset) * Config.LineHeight);

        /// <summary>终端列 → 游戏像素 X。<c>charWidth = max(FontSize / 2, 1)</c>，与 <c>HeadlessRunner</c> 建议终端尺寸的表达式同源（互逆）。</summary>
        internal static int PointXAtViewportCol(int col) => col * Math.Max(Config.FontSize / 2, 1);

        /// <summary>
        /// 终端<b>绝对行</b> → 游戏像素 Y（左上基准）。贴底渲染下内容行 <c>row ∈ [BaseRow, VisibleLines)</c>，
        /// 值域 <c>[0, VisibleHeight − LineHeight]</c>，全部落在可绘制区内 ⇒ 每一行都可命中
        /// （值域的不变量测试见 <c>TerminalViewportGeometryTests</c>）。
        /// </summary>
        internal static int PointYAtViewportRow(int row) => row * Config.LineHeight;
    }
}
