using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;

namespace Emuera.Headless.Tests;

/// <summary>
/// 协议消费端参考实现（ADR-0013 决策四——seam 真实性验证）。
///
/// 模拟 Web 前端的状态管理：
/// - <see cref="ApplySnapshot"/> 用 DisplaySnapshot 初始化全量状态
/// - <see cref="ApplyOps"/> 消费增量 ops（print/newline/clearline/clear/set_bg）更新状态
/// - <see cref="Lines"/> / <see cref="BgColor"/> / <see cref="Awaiting"/>
///   暴露重建后的状态，可与原 displayLineList 逐字段比对。
///
/// 如果 TestAdapter 能从 DisplayState 输出重建显示状态，任何前端（Web/移动/另一个 CLI）也能——
/// 这就是 seam 真实性的证明。
///
/// 关键状态机：PrintOp 追加到"当前行"（最后一行且 isLineEnd=false），NewLineOp 终止当前行。
/// 这与 ConsolePrintManager 的 EmitPrintOps + NewLine 行为对齐。
/// </summary>
internal sealed class TestAdapter
{
    public List<AdapterLine> Lines { get; } = new();
    public string? BgColor { get; private set; }
    /// <summary>v8（issue 02）：背景图状态。null=无背景；[]=已清空；非空=当前背景层列表。</summary>
    public List<BgImageState>? BgImages { get; private set; }
    /// <summary>
    /// v12：唯一契约字段——「服务端此刻在等什么」（`input` / `wait` 两个正交维度 + `ended` 互斥终态）。
    /// </summary>
    public AwaitInfo? Awaiting { get; private set; }

    /// <summary>
    /// 用 DisplaySnapshot 替换内部状态——lines[] 转为内部行列表，bgColor / bgImages / awaiting 覆盖。
    /// </summary>
    public void ApplySnapshot(DisplaySnapshot snapshot)
    {
        Lines.Clear();
        foreach (var line in snapshot.lines)
        {
            var entries = line.entries
                .Select(e => new AdapterEntry(e.segments.ToList(), e.button))
                .ToList();
            Lines.Add(new AdapterLine(entries, line.align, line.isLineEnd));
        }
        BgColor = snapshot.bgColor;
        BgImages = snapshot.bgImages;
        Awaiting = snapshot.awaiting;
    }

    /// <summary>
    /// 消费 DisplayDiff（plan C v5 显式清空信号 + shift_head 扩展）更新状态——模拟 Web 前端从 diff 重建显示。
    /// 与 <see cref="ApplyOps"/> 并存：ApplyOps 验证引擎内部 TurnOp 流，ApplyDiff 验证对外 diff 契约。
    /// - AppendLinesOp → 追加整行（diff 已按行结构化，逐条转为 AdapterLine）
    /// - ClearLineDiffOp(n) → 从行列表末尾删除 n 行（清行）
    /// - ClearScreenOp → 清空全部行（全清）
    /// - ShiftHeadLineOp(count) → 从行列表头部删除 min(count, Lines.Count) 行（MaxLog 滚动）
    /// diff.bgColor 非空 → 更新 bgColor。
    /// </summary>
    public void ApplyDiff(DisplayDiff diff)
    {
        foreach (var op in diff.lineOps)
        {
            switch (op)
            {
                case AppendLinesOp append:
                    foreach (var line in append.newLines)
                    {
                        var entries = line.entries
                            .Select(e => new AdapterEntry(e.segments.ToList(), e.button))
                            .ToList();
                        Lines.Add(new AdapterLine(entries, line.align, line.isLineEnd));
                    }
                    break;
                case ClearLineDiffOp clearline:
                    var n = Math.Min(clearline.clearCount, Lines.Count);
                    if (n > 0)
                        Lines.RemoveRange(Lines.Count - n, n);
                    break;
                case ClearScreenOp:
                    Lines.Clear();
                    break;
                case ShiftHeadLineOp shiftHead:
                    var h = Math.Min(shiftHead.count, Lines.Count);
                    if (h > 0)
                        Lines.RemoveRange(0, h);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown diff op type: {op.type}");
            }
        }
        if (diff.bgColor != null)
            BgColor = diff.bgColor;
        // v8（issue 02）：背景图——非 null 即变更（[] 表达清空），null 无变更不覆盖
        if (diff.bgImages != null)
            BgImages = diff.bgImages;
    }

    /// <summary>
    /// 消费增量 ops 更新状态。处理全部 op 类型：
    /// - PrintOp → 向当前行追加 entry（segments + button，含几何 col/width）
    /// - NewLineOp → 终止当前行（isLineEnd=true，记录 align），下一行开始
    /// - ClearLineOp(displayRows) → 从行列表末尾删除 displayRows 行（显示行，非逻辑行）
    /// - ClearOp → 清空全部行 + 重置 bgColor
    /// - SetBgOp → 更新 bgColor
    /// </summary>
    public void ApplyOps(List<TurnOp> ops)
    {
        foreach (var op in ops)
        {
            switch (op)
            {
                case PrintOp print:
                    EnsureCurrentLine();
                    Lines[^1].Entries.Add(new AdapterEntry(print.segments.ToList(), print.button));
                    break;
                case NewLineOp newline:
                    if (Lines.Count == 0 || Lines[^1].IsLineEnd)
                    {
                        // 空行后立即 newline，或连续 newline——产生一个空行
                        Lines.Add(new AdapterLine(new List<AdapterEntry>(), newline.align, isLineEnd: true));
                    }
                    else
                    {
                        Lines[^1].Align = newline.align;
                        Lines[^1].IsLineEnd = true;
                    }
                    break;
                case ClearLineOp clearline:
                    var n = Math.Min(clearline.displayRows, Lines.Count);
                    if (n > 0)
                        Lines.RemoveRange(Lines.Count - n, n);
                    break;
                case ClearOp:
                    Lines.Clear();
                    BgColor = null;
                    break;
                case SetBgOp setbg:
                    BgColor = setbg.color;
                    break;
                case SetBgImageOp setbgimg:
                    BgImages ??= new List<BgImageState>();
                    BgImages.Add(new BgImageState(setbgimg.src, setbgimg.depth, setbgimg.opacity));
                    break;
                case RemoveBgImageOp removebg:
                    var bi = BgImages?.FindIndex(b => b.src == removebg.src) ?? -1;
                    if (bi >= 0)
                        BgImages!.RemoveAt(bi);
                    break;
                case ClearBgImageOp:
                    BgImages = new List<BgImageState>();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown op type: {op.type}");
            }
        }
    }

    /// <summary>
    /// 确保存在一个"当前行"——最后一行未终止（isLineEnd=false）；若不存在则追加新行。
    /// </summary>
    private void EnsureCurrentLine()
    {
        if (Lines.Count == 0 || Lines[^1].IsLineEnd)
            Lines.Add(new AdapterLine(new List<AdapterEntry>(), null, isLineEnd: false));
    }
}

/// <summary>
/// TestAdapter 内部行模型——对应 DisplayLine / ConsoleDisplayLine。
/// </summary>
internal sealed class AdapterLine
{
    public List<AdapterEntry> Entries { get; } = new();
    public string? Align { get; set; }
    public bool IsLineEnd { get; set; }

    internal AdapterLine(List<AdapterEntry> entries, string? align, bool isLineEnd)
    {
        Entries = entries;
        Align = align;
        IsLineEnd = isLineEnd;
    }
}

/// <summary>
/// TestAdapter 内部条目模型——对应 DisplayEntry / ConsoleButtonString。
/// </summary>
internal sealed class AdapterEntry
{
    public List<PrintSegment> Segments { get; }
    public ButtonRef? Button { get; }

    internal AdapterEntry(List<PrintSegment> segments, ButtonRef? button)
    {
        Segments = segments;
        Button = button;
    }
}
