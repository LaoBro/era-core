using System.Collections.Generic;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    /// <summary>
    /// 按钮命中区追踪器（Phase 3-3 / ADR-0014）。
    /// Phase 3：Region 改为 value-based（Value/IsInteger），移除 Generation 客户端过滤——
    /// 服务端 ConsoleInputHandler 按 button.Generation 校验兜底（ConsoleInputHandler.cs:210-267）。
    /// Phase 4：生产路径改用 <see cref="UpdateFromSnapshot"/> 从 DisplaySnapshot 构建命中区；
    /// <see cref="RecordLineRegions"/> 保留为底层 API 供测试直接构造区域。
    /// </summary>
    internal sealed class ButtonRegionTracker
    {
        private readonly List<Region> _regions = new();

        public int RegionCount => _regions.Count;

        public void Clear() => _regions.Clear();

        /// <summary>
        /// 从 ConsoleButtonString[] 记录命中区（底层 API，供测试直接构造区域）。
        /// 生产路径 Phase 4 后改用 <see cref="UpdateFromSnapshot"/>。
        /// </summary>
        public void RecordLineRegions(string formattedLine, int bufferRow, ConsoleButtonString[]? buttons, long currentGeneration)
        {
            if (buttons == null || buttons.Length == 0 || string.IsNullOrEmpty(formattedLine)) return;

            int column = TerminalDisplayWidth.LeadingDisplayWidth(formattedLine);

            foreach (var btn in buttons)
            {
                if (btn == null) continue;

                string btnText = btn.ToString() ?? "";
                int segmentWidth = TerminalDisplayWidth.GetDisplayWidth(btnText);

                if (btn.IsButton && segmentWidth > 0)
                {
                    object value = btn.IsInteger ? (object)btn.Input : (object)btn.Inputs;
                    _regions.Add(new Region(bufferRow, column, column + segmentWidth - 1, value, btn.IsInteger));
                    AgentLog.Instance.Write($"[region] row={bufferRow} col={column}-{column + segmentWidth - 1} input={btnText}");
                }

                column += segmentWidth;
            }
        }

        /// <summary>
        /// 从 DisplaySnapshot 构建命中区（Phase 3-3 / Q13）。
        /// value-based——从快照 entries[j].button 取 col/width/value/isInteger。
        /// align 偏移：DisplayLine.AlignOffset（CLI 渲染专用）加到 button.col 上得到 PTY 绝对列——
        /// 与 SGR mouse 的 Cb 列匹配。Web 路径 AlignOffset 为 0（JSON 反序列化默认），col 保持相对。
        /// <para>
        /// 2026.10.14 贴底（spec §3.3）：<c>Row</c> 是<b>终端绝对行</b> = <c>baseRow + i</c>，
        /// 不再是视口内相对行号。SGR mouse 上报的正是终端绝对行（<c>VtInputHandler</c>），
        /// 而 <c>TerminalRenderer</c> 把内容第 i 行写在 <c>baseRow + i</c> ⇒ 三者必须逐行一致，
        /// 否则贴底后按钮点不到（稀疏画面下偏差 = 顶部留白行数）。
        /// 视口几何（起始行 / 渲染行数 / 贴底基准行）统一取自 <see cref="TerminalViewportGeometry"/>，
        /// 不再在本方法内自算。
        /// </para>
        /// </summary>
        /// <param name="terminalHeight">终端总行数（<c>AgentCliVtScreen.WindowHeight</c>）。
        /// 视口行数由 <see cref="TerminalViewportGeometry.VisibleLines"/> 推导，不由调用方自算。</param>
        public void UpdateFromSnapshot(DisplaySnapshot snapshot, int scrollOffset, int terminalHeight)
        {
            _regions.Clear();

            int startLine = TerminalViewportGeometry.StartLine(snapshot.lines.Count, terminalHeight, scrollOffset);
            int rendered = TerminalViewportGeometry.RenderedLines(snapshot.lines.Count, terminalHeight, scrollOffset);
            int baseRow = TerminalViewportGeometry.BaseRow(snapshot.lines.Count, terminalHeight, scrollOffset);

            for (int i = 0; i < rendered; i++)
            {
                var line = snapshot.lines[startLine + i];
                int alignOffset = line.AlignOffset;
                foreach (var entry in line.entries)
                {
                    if (entry.button is { } btn && btn.col is { } c && btn.width is { } w)
                    {
                        int absLeft = c + alignOffset;
                        _regions.Add(new Region(
                            Row: baseRow + i,
                            Left: absLeft,
                            Right: absLeft + w - 1,
                            Value: btn.value,
                            IsInteger: btn.isInteger));
                    }
                }
            }
        }

        /// <summary>命中测试，返回匹配的 Region（value-based）。</summary>
        public Region? HitTest(int row, int col)
        {
            for (int i = _regions.Count - 1; i >= 0; i--)
            {
                var r = _regions[i];
                if (r.Row == row && col >= r.Left && col <= r.Right)
                    return r;
            }
            return null;
        }

        /// <summary>
        /// 命中区（value-based）。Value 是按钮提交值（int/long 或 string），
        /// IsInteger 标记整数按钮。Phase 3 前持有 ConsoleButtonString 引用 + Generation，
        /// Phase 3 改为 value-based 以支持从 DisplaySnapshot 构建。
        /// </summary>
        internal readonly record struct Region(
            int Row,
            int Left,
            int Right,
            object Value,
            bool IsInteger);
    }
}
