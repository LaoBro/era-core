using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 当前全量显示状态的快照（ADR-0013 决策二 / ADR-0016 / v12 收拢到 awaiting）。
/// state-based 格式——直接表示当前显示状态，不是操作序列的回放。
/// WS 晚加入者调 GET /snapshot 拿初始状态，再订阅 WS 收增量 ops。
/// v12（issue 09）：旧顶层 state / inputType / needValue / timeLimit / displayTime /
/// timeUpMessage 全部退场，改由 `awaiting` 单一下发（TINPUT 计时元数据在 `awaiting.wait`）。
/// 快照**没有** timedOut —— 它表示「当前状态」，「上一帧是否超时触发」对晚加入者无意义。
/// </summary>
internal record DisplaySnapshot(
    List<DisplayLine> lines,
    string? bgColor,
    // v12（spec §5.2 第 4 项）：GET /snapshot 也下发 awaiting ——「晚加入者拿到的第一张画面
    // 不能是另一套契约形状」。⚠️ 快照**没有** timedOut（见类型注释），故 timedOut 固定传 false。
    AwaitInfo awaiting,
    int protocolVersion,
    long generation,
    List<BgImageState>? bgImages = null
);

/// <summary>
/// 快照中的单行（ADR-0013 决策二）。
/// 每行含 entries[]（每个对应一个 ConsoleButtonString）+ 对齐 + 是否行末。
/// 不含 isLogicalLine / isTemporary——这些是服务端内部概念。
///
/// Phase 4-1（Q1 + R3）：新增 <see cref="LineNo"/> + <see cref="SourceLine"/> 两个 CLI 渲染专用字段。
/// 二者均标 <c>[JsonIgnore]</c> 不进入 JSON 序列化——Web 契约保持 ADR-0013 形态。
/// <see cref="LineNo"/> 用于 CLI delta 算法（<c>_lastRenderedLineNo</c> 比较）；
/// <see cref="SourceLine"/> 持原 <see cref="ConsoleDisplayLine"/> 引用，供 <see cref="TerminalLineFormatter.FormatLineForTerminal"/>
/// 格式化（PrintSegment 不承载图片/矩形等 CLI 不绘制的视觉节点，故保留原引用）。
/// record 默认 Equals 会纳入这两个字段——Phase 2 的 <see cref="DisplayState.LinesEqual"/> 已手写深度值比较
/// 绕过 record.Equals，故 diff 正确性不受影响。
/// </summary>
internal record DisplayLine(
    List<DisplayEntry> entries,
    string? align,
    bool isLineEnd
)
{
    /// <summary>
    /// CLI 渲染专用：原 ConsoleDisplayLine.LineNo（Q1）。用于 delta 算法比较。
    /// BuildSnapshot 从 line.LineNo 传入。不进入 JSON。
    /// </summary>
    [JsonIgnore] internal int LineNo { get; set; }

    /// <summary>
    /// CLI 渲染专用：原 ConsoleDisplayLine 引用（R3 扩展）。
    /// TerminalRenderer 经此调 FormatLineForTerminal；CLI 对图片/矩形按流宽度补空格占位
    /// （TerminalLineFormatter.BuildTerminalLine，T-026），不再是"跳过非文本视觉节点"语义。
    /// Web 路径不访问此字段。不进入 JSON。
    /// </summary>
    [JsonIgnore] internal ConsoleDisplayLine? SourceLine { get; set; }

    /// <summary>
    /// CLI 渲染专用：align 居中/右对齐时的前导空格数（绝对列偏移）。
    /// BuildSnapshot 经 TerminalLineFormatter.GetGameColumnWidth + BuildTerminalLine 计算——
    /// 与 FormatLineForTerminal 输出的前导空格数完全一致。
    /// ButtonRegionTracker.UpdateFromSnapshot 把此偏移加到 entry.button.col 上，
    /// 得到 PTY 显示的绝对列位置（与 SGR mouse 的 Cb 列匹配）。
    /// Web 路径不访问此字段（前端用 CSS 处理 align，col 保持相对）。不进入 JSON。
    /// </summary>
    [JsonIgnore] internal int AlignOffset { get; set; }
};

/// <summary>
/// 行内条目（ADR-0013 决策二）。
/// 含 segments[]（复用 PrintSegment）+ button（ButtonRef?，含 col/width）。
/// </summary>
internal record DisplayEntry(
    List<PrintSegment> segments,
    ButtonRef? button
);

/// <summary>
/// DisplayState 模块（ADR-0013 决策一/二；ADR-0014 推翻决策四）。
/// 封装 EmueraConsole.DisplayLineList → DisplaySnapshot 序列化 + 几何计算。
/// 深模块：删除测试——删掉后序列化 + 几何计算复杂度转移到 KestrelGameServer。
/// 当前服务于 Web 路径（GET /snapshot 端点 + 未来 Web 前端）；CLI 将在 Phase 4 改为消费 DisplayState（ADR-0014 统一真相源）。
///
/// Phase 1（DisplayState 统一真相源执行计划）：从无状态工具类升级为有状态的显示模型。
/// 持有当前 DisplaySnapshot（_current），以 _pendingOps 为权威变更信号做变更检测（TryUpdate）。
/// _gate 锁保护跨线程读写——游戏线程 BuildTurn 调 TryUpdate，HTTP 线程 GET /snapshot 读 Current。
/// </summary>
internal sealed class DisplayState : IDisplayState
{
    /// <summary>
    /// DisplaySnapshot 的 JSON 序列化选项（ADR-0013 决策二）。
    /// 与 TurnRecord 一致使用 WhenWritingNull——nullable 字段（bgColor/inputType/align/button/col/width 等）
    /// 在为 null 时不写入 JSON，保持响应体紧凑。
    /// 3.3（NativeAOT）：TypeInfoResolver 指向源生成上下文，AOT 下反射序列化被禁用。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = EmueraJsonContext.Default,
    };

    private readonly EmueraConsole _console;
    private readonly string _defaultFontName;
    private readonly bool _fullDiffOnFirstTurn;
    private readonly object _gate = new();
    private DisplaySnapshot? _current;
    private DisplaySnapshot? _previous;

    /// <summary>
    /// issue-004（画面判据对齐）：上次构建 <c>_current</c> 时所用的引擎行列表（浅拷贝）与背景。
    /// 变化判据据此比较<b>画面本身</b>，而非「操作队列是否为空」—— 见 <see cref="TryRefreshSnapshot"/>。
    /// 仅 <c>_gate</c> 锁内访问。
    /// </summary>
    private List<ConsoleDisplayLine>? _builtLines;
    private string? _builtBgHex;
    private List<BgImageState>? _builtBgImages;

    /// <summary>
    /// ADR-0022：权威清空事件累积器。语义 =「自上一回 <see cref="ComputeDiff"/> 以来、
    /// 未被取走的权威清空事件」。统一消费语义：谁 drain _pendingOps 谁累积（TryUpdate 与
    /// ComputeDiff 的 drain 都累进此字段），唯一 flush 方是 ComputeDiff（任一分支都取走+重置）。
    /// 根治 Q11 race——帧级 TryUpdate（GET /snapshot）抢先 drain 时不再丢弃清空信号。
    /// 仅 _gate 锁内访问，无并发。
    /// </summary>
    private readonly TurnClearAccumulator _pendingClearAccumulator = new();

    /// <summary>
    /// 3.3 增量快照（S0 优化）：引擎行对象 → 已构建 DisplayLine 的引用缓存。
    /// 键为 <see cref="ConsoleDisplayLine"/> 引用（默认引用相等）——行对象入列后内容不可变
    /// （步骤 0 审计：LineNo/IsLineEnd/Align/buttons 的写点均在 Add 之前，见 ConsolePrintManager
    /// 入列路径与 ConsoleButtonString/ConsoleDisplayLine 的字段守卫），同引用必然同值，
    /// BuildPrintOpsForLine 是纯函数（同输入必同输出），故缓存产物与全量重建值相等。
    ///
    /// 值相等性的字体维度：DisplayLine 的 segment fontname 编码取决于 defaultFontName（
    /// BuildPrintOpsForLine 判断 style.Fontname != defaultFontName 才写入 JSON）。本实例的
    /// <see cref="_defaultFontName"/> 是构造注入的 readonly 字段（同一值贯穿 Rebuild 每回合的
    /// miss 构建，亦即与全量重建传入同一值），故字体变更不会让缓存产物与全量重建产生分歧——
    /// 缓存键只需行对象引用，无需并入字体维度。
    ///
    /// 失效靠对象替换天然生效：CLEAR/CLEARLINE/末行编辑/ShiftHead 均以全新对象替换旧对象
    /// （旧条目成孤儿），孤儿由 <see cref="Rebuild"/> 的兜底阈值清理，绝不误命中。
    /// 仅 Rebuild（_gate 锁内）访问，无并发。
    /// </summary>
    private readonly Dictionary<ConsoleDisplayLine, DisplayLine> _lineCache = new();

    /// <summary>
    /// plan C + shift_head 扩展：本回合从 _pendingOps 分类出的权威清空信号。
    /// ClearAll 主导（CLEAR），ClearLineCount 为累加的清行数，Bg 为最后一次 SetBgOp 颜色（可空）。
    /// ShiftHeadCount 为累加的头部截断行数（MaxLog 滚动）——由 ShiftHeadTurnOp 累加，
    /// ComputeDiff 在 lineOps 前置 ShiftHeadLineOp(ShiftHeadCount)。
    /// </summary>
    private sealed record TurnClearSignal
    {
        public bool ClearAll;
        public int ClearLineCount;
        public int ShiftHeadCount;
        public string? Bg;
    }

    /// <summary>
    /// ADR-0022：权威清空事件累积器（<see cref="_pendingClearAccumulator"/> 字段类型）。
    /// 与 <see cref="TurnClearSignal"/> 同构，但语义是"自上次 flush 以来累积的事件"，
    /// 而非"本回合一次性信号"——故由任意 drain 方累加、仅 ComputeDiff 取走并重置。
    /// </summary>
    private sealed class TurnClearAccumulator
    {
        public bool ClearAll;
        public int ClearLineCount;
        public int ShiftHeadCount;
        public string? Bg;

        public void Add(TurnOp op)
        {
            switch (op)
            {
                case ClearOp: ClearAll = true; break;
                // displayRows = 引擎实际摘掉的显示行数（≠ 脚本写的逻辑行参数，issue 13）
                case ClearLineOp clo: ClearLineCount += clo.displayRows; break;
                case SetBgOp sbo: Bg = sbo.color; break;
                case ShiftHeadTurnOp sho: ShiftHeadCount += sho.count; break;
            }
        }

        /// <summary>返回当前累积信号并重置——仅由 ComputeDiff 调用（统一 flush 方）。</summary>
        public TurnClearSignal TakeSignalAndReset()
        {
            var signal = new TurnClearSignal
            {
                ClearAll = ClearAll,
                ClearLineCount = ClearLineCount,
                ShiftHeadCount = ShiftHeadCount,
                Bg = Bg
            };
            ClearAll = false;
            ClearLineCount = 0;
            ShiftHeadCount = 0;
            Bg = null;
            return signal;
        }
    }

    /// <summary>
    /// <see cref="Rebuild"/> 兜底清理的孤儿行数上限，当 _lineCache.Count 超过 活跃行 + 该值时全清重建。
    /// Config.Current 不可用（单测环境 / 启动早期）时的 MaxLog 降级值——与 ConfigData.cs:88
    /// （ConfigItem.MaxLog 默认 5000）保持一致；正常路径始终读 <see cref="Config.MaxLog"/> 实时值。
    /// </summary>
    private const int FallbackMaxLog = 5000;

    /// <param name="fullDiffOnFirstTurn">
    /// MAUI 模式传 true——首帧（_previous==null）返回全量 AppendLinesOp 而非 null。
    /// MAUI 无 GET /snapshot 端点，首帧 diff 是唯一画面来源；返回 null 会导致游戏开头输出全部丢失。
    /// HTTP/CLI 模式传 false（默认）——首帧 diff=null，靠 GET /snapshot（HTTP）或全量重绘（CLI）获取画面。
    /// </param>
    internal DisplayState(EmueraConsole console, string defaultFontName, bool fullDiffOnFirstTurn = false)
    {
        _console = console;
        _defaultFontName = defaultFontName;
        _fullDiffOnFirstTurn = fullDiffOnFirstTurn;
    }

    bool IDisplayState.TryUpdate() => TryUpdate();
    DisplaySnapshot IDisplayState.Current => Current;

    /// <summary>
    /// 当前快照（线程安全）。读取时先 TryUpdate 保证最新。
    /// GET /snapshot 端点经此取快照；Phase 2 ComputeDiff 亦经此取连续快照对。
    /// </summary>
    internal DisplaySnapshot Current
    {
        get { lock (_gate) { TryUpdate(); return _current!; } }
    }

    /// <summary>
    /// 帧级变更检测 + 刷新：画面变了则重建 <c>_current</c> 并返回 true。
    ///
    /// <b>issue-004（画面判据对齐）</b>：判据是<b>画面本身</b>，不是「操作队列是否为空」：
    /// <list type="bullet">
    /// <item><b>行对象引用比较</b> —— 与 CLI 路径（<c>TerminalRenderer</c> 的 <c>SourceLine</c>
    /// 引用比对）同一判据。行对象入列后内容不可变（见 <see cref="_lineCache"/> 的不变量说明），
    /// 同引用必然同值，故无需深比较。</item>
    /// <item><b>背景色 / 背景图比较</b> —— 不能只比行：<c>SETBG</c> 一个行对象都不换，
    /// 只比行会让背景变更丢帧（操作队列判据之所以没暴露这个问题，是因为 SETBG 会 Enqueue
    /// 一个 op；判据换成画面后必须显式把背景计入）。</item>
    /// </list>
    ///
    /// 为什么不再用「<c>PendingOpCount == 0</c> → 无变化」：操作队列可能被<b>别人</b>排空
    /// （帧级 <c>TryUpdate</c> 与回合级 <c>ComputeDiff</c> 都 drain，见 ADR-0022），
    /// 一旦本回合的 op 在 ComputeDiff 之前被排空，画面明明变了却判定为无变化 → 跳帧。
    ///
    /// 无论是否重建都无条件 drain：<c>_pendingOps</c> 不得累积，且权威清空信号须进累积器。
    /// </summary>
    internal bool TryUpdate()
    {
        lock (_gate)
        {
            return TryRefreshSnapshot();
        }
    }

    /// <summary>按画面判据刷新 <c>_current</c>；仅在 <c>_gate</c> 锁内调用。</summary>
    private bool TryRefreshSnapshot()
    {
        var linesCopy = CopyDisplayLines();

        bool changed = _current == null
            || _builtLines == null
            || !SameLineObjects(_builtLines, linesCopy)
            || _builtBgHex != _console.bgColor.ToHex()
            || !BgImagesEqual(_builtBgImages, _console.BgImages);

        DrainIntoAccumulator();

        if (!changed)
            return false;

        int maxLog = Config.Current != null ? Config.MaxLog : FallbackMaxLog;
        if (_lineCache.Count > linesCopy.Count + maxLog)
            _lineCache.Clear();

        _builtLines = linesCopy;
        _builtBgHex = _console.bgColor.ToHex();
        _builtBgImages = NormalizeBgImages(_console.BgImages) is { } bg ? new List<BgImageState>(bg) : null;
        _current = BuildSnapshotIncremental(_lineCache, linesCopy, _console.bgColor,
            _console.State, _console.CurrentRequest, _defaultFontName, _console.LastButtonGeneration,
            _console.BgImages);
        return true;
    }

    /// <summary>
    /// 行对象引用比较：同长度且逐项同引用即视为画面未变（行对象不可变 ⇒ 同引用同值）。
    /// </summary>
    private static bool SameLineObjects(List<ConsoleDisplayLine> a, List<ConsoleDisplayLine> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    /// <summary>
    /// 计算本回合 DisplayDiff（Phase 2 / ADR-0014 / plan C v5 / ADR-0022）。
    /// 原子化（持 _gate）：先确保 _current 最新——若 _pendingOps 非空（本回合有显示变更）则 Rebuild
    /// 并 drain+累积<b>权威清空信号</b>（ClearOp/ClearLineOp/SetBgOp，见 ConsolePrintManager）；
    /// 再比对 _current（本回合）与 _previous（上一回合）产出 diff。
    ///
    /// ADR-0022：清空信号经 <see cref="_pendingClearAccumulator"/> 累积、此处<b>无条件</b>取走重置——
    /// 帧级 TryUpdate 抢先 drain 时不丢弃信号，ComputeDiff 仍取权威清空事件（精确行数 n），
    /// 不再依赖 Q11 的 <see cref="StructuralDiff"/> 结构推断兜底。
    ///
    /// 返回 null 的三种情况：首次回合（_previous==null）、_current 未构建、本回合显示未变
    /// （ReferenceEquals(_previous, _current)——no-op 回合，仅 state/inputType 变化由 TurnRecord 顶层携带）。
    ///
    /// 调用契约：BuildTurn 直接调 ComputeDiff（不再前置 TryUpdate）；HTTP/CLI 的帧级刷新仍经 TryUpdate。
    /// _previous 读写与 _current 同处 _gate 锁内。仅游戏线程 BuildTurn 调用，无新增并发。
    /// </summary>
    internal DisplayDiff? ComputeDiff()
    {
        lock (_gate)
        {
            // 1. 刷新 _current + drain+累积权威清空信号。
            //    issue-004：判据是画面本身（而非「有/无 pending op」）—— op 可能被帧级
            //    TryUpdate 抢先 drain 掉，只看 op 会让画面变了却判定为无变化（跳帧）。
            //    ADR-0022：下面的 TakeSignalAndReset 仍须无条件执行（原因见原注释）。
            TryRefreshSnapshot();

            // ADR-0022：统一 flush——无论上支是否 drain，都必须取走+重置累积器，
            // 否则被帧级 TryUpdate 抢先累积的清空信号会在无 pending 分支漏成 StructuralDiff。
            var signal = _pendingClearAccumulator.TakeSignalAndReset();

            var current = _current;
            if (current == null) return null; // _current 未构建

            var prev = _previous;
            _previous = current;

            if (prev == null)
            {
                // 首次回合：HTTP/CLI 返回 null（靠 GET /snapshot 或全量重绘获取画面）；
                // MAUI 模式（_fullDiffOnFirstTurn=true）无 snapshot 端点，首帧 diff 是唯一画面来源，
                // 返回全量 AppendLinesOp 让 Vue 端 applyDiff 渲染游戏开头输出。
                if (_fullDiffOnFirstTurn && current.lines.Count > 0)
                    return new DisplayDiff(
                        new List<LineOp> { new AppendLinesOp(current.lines) },
                        current.bgColor,
                        current.bgImages);
                return null;
            }
            if (ReferenceEquals(prev, current)) return null;  // no-op：本回合显示未变

            // 2. 行级操作：权威清空优先，否则结构分类（兼 race 降级）
            //    shift_head 调整"effective prev"——prev 等价于去掉头部 ShiftHeadCount 行后的列表。
            //    后续 ClearLineCount / StructuralDiff 都基于 effectivePrev 计算，避免头部删除
            //    被 StructuralDiff 误判为 ClearScreenOp + Append 全量重印。
            List<LineOp> lineOps;
            if (signal.ClearAll)
            {
                // ClearOp 主导——全清已覆盖头部，shift_head 互斥不产出
                lineOps = new List<LineOp> { new ClearScreenOp() };
                if (current.lines.Count > 0)
                    lineOps.Add(new AppendLinesOp(current.lines));
            }
            else
            {
                // shift_head 调整 effectivePrev：去掉头部 ShiftHeadCount 行。
                // 后续 ClearLineCount / StructuralDiff 基于 effectivePrev 计算，
                // 让头部删除独立表达为 ShiftHeadLineOp，不污染尾部 diff。
                int skipHead = Math.Min(signal.ShiftHeadCount, prev.lines.Count);
                var effectivePrevLines = skipHead > 0
                    ? prev.lines.GetRange(skipHead, prev.lines.Count - skipHead)
                    : prev.lines;

                List<LineOp> tailOps;
                bool prependShiftHead = skipHead > 0;
                if (signal.ClearLineCount > 0)
                {
                    int keep = effectivePrevLines.Count - signal.ClearLineCount;
                    // race 降级（Q11）：部分清空信号被帧级 TryUpdate 抢先 drain，导致 clearCount
                    // 与真实快照行数冲突（keep<0 代表清行数超过上一帧总行；current 行数不足前缀
                    // 代表清行后又重印的行少于应保留的前缀）。两种情况都回退结构分类，避免
                    // GetRange 越界或将真实 CLEAR 误报为单行清空。
                    if (keep < 0 || current.lines.Count < keep)
                    {
                        tailOps = StructuralDiff(prev, current);
                        prependShiftHead = false; // race 降级路径不前置 shift_head
                    }
                    else
                    {
                        tailOps = new List<LineOp> { new ClearLineDiffOp(signal.ClearLineCount) };
                        var appended = current.lines.GetRange(keep, current.lines.Count - keep);
                        if (appended.Count > 0) tailOps.Add(new AppendLinesOp(appended));
                    }
                }
                else
                {
                    // 无权威 ClearLineCount —— 用 effectivePrev 走结构分类，
                    // 避免头部删除被 StructuralDiff 误判为 ClearScreenOp + Append 全量重印。
                    // bgImages 一并透传（StructuralDiff 目前不读，但保持快照字段语义完整）。
                    var effectivePrev = new DisplaySnapshot(
                        lines: effectivePrevLines, bgColor: prev.bgColor,
                        awaiting: prev.awaiting,
                        protocolVersion: prev.protocolVersion, generation: prev.generation,
                        bgImages: prev.bgImages);
                    tailOps = StructuralDiff(effectivePrev, current);
                }

                if (prependShiftHead)
                {
                    lineOps = new List<LineOp> { new ShiftHeadLineOp(skipHead) };
                    lineOps.AddRange(tailOps);
                }
                else
                {
                    lineOps = tailOps;
                }
            }

            // 3. 背景色：权威 SetBgOp 优先，否则从快照比较推断
            string? bg = signal.Bg ?? (prev.bgColor == current.bgColor ? null : current.bgColor);
            // 4. 背景图（issue 02）：快照比较推断——与 bgColor 同模式（state 全量字段）。
            //    清空表达为 []（非 null），与 null（无变更）区分，前端据此清除背景层。
            List<BgImageState>? bgImages = null;
            if (!BgImagesEqual(prev.bgImages, current.bgImages))
                bgImages = current.bgImages ?? [];
            return new DisplayDiff(lineOps, bg, bgImages);
        }
    }

    /// <summary>
    /// ADR-0022：drain 全部 _pendingOps 并累积进 <see cref="_pendingClearAccumulator"/>（谁 drain 谁累积）。
    /// ClearOp 主导（→ClearAll，吞掉 CLEARLINE）；多个 ClearLineOp 的 n 累加；SetBgOp 取最后一次
    /// （bg 由 DisplayDiff.bgColor 携带，不计入 lineOps）；ShiftHeadTurnOp 的 count 累加为
    /// ShiftHeadCount（MaxLog 头部截断，独立于尾部清空）。PrintOp/NewLineOp 不影响清空判定，忽略。
    /// 与旧 DrainAndClassifyClears 的差别：累进累积器而非返回一次性信号——被帧级 TryUpdate
    /// （GET /snapshot）抢先 drain 的清空信号不再丢失，ComputeDiff 仍能取到（根治 Q11 race）。
    /// </summary>
    private void DrainIntoAccumulator()
    {
        foreach (var op in _console.DrainPendingOps())
            _pendingClearAccumulator.Add(op);
    }

    /// <summary>
    /// 比对两个快照的 lines 产出 LineOp 序列（plan C v5 + race 降级）。
    /// Emuera 显示模型以追加为主——头部行仅在 MaxLog 截断时变化（由 ShiftHeadTurnOp 主动捕获，
    /// 不走此 fallback）；差异通常只在尾部。
    /// 公共前缀 k 之后的差异用 ClearLineDiffOp(clearCount=prev.Count-k)+Append 表达；
    /// k==0 且 prev 非空 → ClearScreenOp + Append（全清后重印，等价于旧 ReplaceAll）。
    /// race 降级路径：_pendingOps 被帧级 TryUpdate 抢先 drain 时，ShiftHeadTurnOp 不可见，
    /// 头部截断会令 CommonPrefix 检测到 k=0 → 退化为 ClearScreenOp + Append 全量重印。
    /// 这是罕见并发场景（ADR-0022 后仅病理性兜底），正确性不破坏，仅性能受损。
    ///
    /// ADR-0022（D7 不变量）：本兜底只在如下不变量成立时才保持正确——
    /// 「引擎的清空/截断全部可被 <see cref="CommonPrefix"/> 结构推断（追加为主、无原地替换/移动型 op）」。
    /// 新增结构不可推断的显示 op 类型时，必须在此同步设计其消费语义，否则兜底会静默失真。
    /// </summary>
    private static List<LineOp> StructuralDiff(DisplaySnapshot prev, DisplaySnapshot curr)
    {
        var p = prev.lines;
        var c = curr.lines;
        int k = CommonPrefix(p, c);
        if (k == p.Count)
        {
            // prev 是 curr 的前缀（含完全相等到此分支不可能——ReferenceEquals 已短路，
            // 但值相等不同引用可能到此。p.Count==c.Count 时 GetRange(k,0) 返回空列表 → Append 空）
            return c.Count == p.Count
                ? new List<LineOp>()
                : [new AppendLinesOp(c.GetRange(k, c.Count - k))];
        }
        if (k == c.Count) return [new ClearLineDiffOp(p.Count - k)];                 // 纯截尾（CLEARLINE）
        if (k == 0) return [new ClearScreenOp(), new AppendLinesOp(c)];              // 头部都变 = CLEAR/全重置
        return [new ClearLineDiffOp(p.Count - k), new AppendLinesOp(c.GetRange(k, c.Count - k))]; // 尾部替换
    }

    /// <summary>
    /// 逐行值比较求公共前缀长度。
    /// DisplayLine/DisplayEntry 是 record 但含 List&lt;&gt; 字段——C# record 对集合字段用引用相等，
    /// 故不能直接用 record.Equals。此处做深度值比较：align/isLineEnd + entries 逐条 segments/button 比对。
    /// PrintSegment 全为值类型/string 字段，record.Equals 正确；ButtonRef.value 是 object 但实际为 int/string，
    /// object.Equals 正确委派到对应类型的值相等。
    /// </summary>
    private static int CommonPrefix(List<DisplayLine> a, List<DisplayLine> b)
    {
        int n = Math.Min(a.Count, b.Count);
        int k = 0;
        while (k < n && LinesEqual(a[k], b[k])) k++;
        return k;
    }

    private static bool LinesEqual(DisplayLine a, DisplayLine b)
    {
        if (ReferenceEquals(a, b)) return true; // 3.3（S0）：同引用必然同值（缓存命中行）——短路深比较
        if (a.align != b.align) return false;
        if (a.isLineEnd != b.isLineEnd) return false;
        if (a.entries.Count != b.entries.Count) return false;
        for (int i = 0; i < a.entries.Count; i++)
        {
            if (!EntriesEqual(a.entries[i], b.entries[i])) return false;
        }
        return true;
    }

    private static bool EntriesEqual(DisplayEntry a, DisplayEntry b)
    {
        if (a.segments.Count != b.segments.Count) return false;
        for (int i = 0; i < a.segments.Count; i++)
        {
            if (!a.segments[i].Equals(b.segments[i])) return false;
        }
        return Equals(a.button, b.button);
    }

    /// <summary>
    /// 浅拷贝 displayLineList 防止 HTTP 线程并发遍历时游戏线程写入（Q5）。
    ///
    /// 注意：游戏线程（ConsolePrintManager）写 displayLineList 不持 _gate，故
    /// new List&lt;T&gt;(source) 的 CopyTo 可能与 Add 竞态（ArgumentException: array not long enough）。
    /// 重试即可——竞态窗口极短，下一次拷贝时数组长度已匹配。
    ///
    /// 重建（<see cref="TryRefreshSnapshot"/>）经 _lineCache 做增量构建——命中行复用已构建
    /// DisplayLine（含 entries/几何/LineNo/SourceLine/AlignOffset），仅新行走 BuildPrintOpsForLine。
    /// </summary>
    private List<ConsoleDisplayLine> CopyDisplayLines()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new List<ConsoleDisplayLine>(_console.DisplayLineList);
            }
            catch (ArgumentException) when (attempt < 10)
            {
                // displayLineList 在 CopyTo 期间被游戏线程修改，重试
            }
        }
    }

    /// <summary>
    /// 生成全量快照并序列化为 JSON 字符串（GET /snapshot 端点使用）。
    /// 经 Current → TryUpdate 保证快照随游戏打印实时推进。
    /// JsonTypeInfo 重载消除 IL2026/IL3050（options 重载标 RequiresUnreferencedCode；
    /// 运行时经 JsonOpts.TypeInfoResolver 走源生成，安全——与 AgentJsonlProtocol.SerializeTurn 同模式）。
    /// </summary>
    internal string GetSnapshotJson() =>
        JsonSerializer.Serialize(Current, (JsonTypeInfo<DisplaySnapshot>)JsonOpts.GetTypeInfo(typeof(DisplaySnapshot)));

    /// <summary>
    /// 从已知 displayLineList 构造 DisplaySnapshot（ADR-0013 决策二）。
    /// 遍历 displayLineList，每行映射为 DisplayLine：ConsoleButtonString[] → entries[]，
    /// 每个 entry 的 segments 从 StrArray 提取（复用 ConsolePrintManager.BuildPrintOpsForLine 的
    /// segment + 几何计算），button 填充 ButtonRef（含 col/width）。
    /// 提取为 internal static 以便单元测试（DisplayStateTests）直测序列化，无需构造 EmueraConsole。
    ///
    /// defaultFontName 参数：默认字体名，传给 BuildPrintOpsForLine 判断 segment fontname 是否为默认。
    /// 调用方负责传入——DisplayState.Rebuild 从构造函数注入的值传（HTTP 线程由 Session 从 ConfigData 读）。
    ///
    /// 3.3（S0）：本方法等价于以空缓存调 <see cref="BuildSnapshotIncremental"/>（每行全 miss 重建），
    /// 产物与增量路径值相等——供单元测试直测完整映射（不构造 EmueraConsole）以及无缓存降级路径。
    /// </summary>
    internal static DisplaySnapshot BuildSnapshot(
        List<ConsoleDisplayLine> displayLineList,
        EmuColor bgColor,
        ConsoleState state,
        InputRequest? currentRequest,
        string defaultFontName,
        long generation = 0,
        List<BgImageState>? bgImages = null)
        => BuildSnapshotIncremental(new Dictionary<ConsoleDisplayLine, DisplayLine>(),
            displayLineList, bgColor, state, currentRequest, defaultFontName, generation, bgImages);

    /// <summary>
    /// 3.3 增量快照构建（S0 优化）——BuildSnapshot 的缓存版本。
    /// lineCache 键为引擎行对象引用：命中行直接复用已构建 DisplayLine（entries/几何/LineNo/
    /// SourceLine/AlignOffset，含 CLI 渲染三字段），未命中行经 <see cref="BuildDisplayLine"/>
    /// 构建后入缓存。行对象入列后内容不可变，同引用必然同值；CLEAR/CLEARLINE/ShiftHead/末行编辑
    /// 均以新行对象替换旧对象（旧条目成孤儿，由调用方兜底清理），不会误命中。
    /// 提取为 internal static 以便单元测试直测缓存命中/失效/值相等性，无需构造 EmueraConsole。
    /// </summary>
    internal static DisplaySnapshot BuildSnapshotIncremental(
        Dictionary<ConsoleDisplayLine, DisplayLine> lineCache,
        List<ConsoleDisplayLine> displayLineList,
        EmuColor bgColor,
        ConsoleState state,
        InputRequest? currentRequest,
        string defaultFontName,
        long generation = 0,
        List<BgImageState>? bgImages = null)
    {
        var lines = new List<DisplayLine>(displayLineList.Count);
        foreach (var line in displayLineList)
        {
            if (!lineCache.TryGetValue(line, out var dl))
            {
                dl = BuildDisplayLine(line, defaultFontName);
                lineCache.Add(line, dl);
            }
            lines.Add(dl);
        }

        return new DisplaySnapshot(
            lines: lines,
            bgColor: bgColor.ToHex(),
            // v12：与流上帧同源（同一工厂、同一 req/state）。timedOut 传 false —— 快照表示
            // 「当前状态」，「上一帧是否超时触发」对晚加入者无意义（见 DisplaySnapshot 注释）。
            // TINPUT 计时元数据（timeLimit / displayTime / timeUpMessage）自 v12 起随
            // `awaiting.wait` 一并下发，顶层不再重复。
            //
            // ⚠️ 与流上帧的唯一差别：快照可能在「脚本还在跑」的瞬态被取到（GET /snapshot 是
            // 随时可调的端点）——此时两个维度都无值，序列化成 awaiting:{}。这不是契约违规：
            // §1.2 规则 1 约束的是**停稳点**上的帧（流上的帧只在停稳点产出）；客户端把
            // `{}` 当作「没有可推进的等待」处理即可（下一次 WS 帧会给出真实停稳点）。
            awaiting: AwaitInfo.Of(currentRequest, state, timedOut: false),
            protocolVersion: TurnRecord.CurrentProtocolVersion,
            generation: generation,
            // issue 02：背景图（浅拷贝防并发/同引用误判——_console.BgImages 是活引用，
            // 快照必须持有独立副本，否则连续非空变更时 SequenceEqual 同引用恒真、diff 丢失；
            // 紧凑归一：空列表不进 JSON，diff 层用 [] 表达清空）
            bgImages: bgImages is { Count: > 0 } ? new List<BgImageState>(bgImages) : null
        );
    }

    /// <summary>
    /// issue 02：背景图列表值比较（null 与空列表视为等价的无背景）。
    ///
    /// issue-004：两侧都先归一再比较 —— 引擎侧的 <c>_console.BgImages</c> 是<b>非 null 的空列表</b>，
    /// 而快照侧按「空即不进 JSON」归一成 <c>null</c>；不归一的话这两个「都没有背景」会被判成
    /// 不相等，画面判据就会每帧误报「变了」（<c>DisplayDiffTests.Noop_turn_produces_null_diff</c>）。
    /// </summary>
    private static bool BgImagesEqual(List<BgImageState>? a, List<BgImageState>? b)
    {
        a = NormalizeBgImages(a);
        b = NormalizeBgImages(b);
        if (a == null || b == null)
            return a == null && b == null;
        return a.SequenceEqual(b);
    }

    /// <summary>背景图归一：空列表 → null（与快照「空即不进 JSON」同一口径）。</summary>
    private static List<BgImageState>? NormalizeBgImages(List<BgImageState>? images)
        => images is { Count: > 0 } ? images : null;

    /// <summary>
    /// 单个 ConsoleDisplayLine → DisplayLine 的完整映射（3.3 提取，供增量缓存复用）。
    /// 复用 BuildPrintOpsForLine 的 segment 提取 + 几何计算——保证快照与增量 ops 一致，
    /// 并填入 CLI 渲染专用字段（LineNo/SourceLine/AlignOffset）。纯函数：同输入必同输出，
    /// 是引用缓存值相等性的基础。
    /// </summary>
    internal static DisplayLine BuildDisplayLine(ConsoleDisplayLine line, string defaultFontName)
    {
        var printOps = ConsolePrintManager.BuildPrintOpsForLine(line, defaultFontName);
        var entries = printOps
            .Select(op => new DisplayEntry(op.segments, op.button))
            .ToList();

        var dl = new DisplayLine(
            entries: entries,
            align: AlignToString(line.Align),
            isLineEnd: line.IsLineEnd
        );
        dl.LineNo = line.LineNo;
        dl.SourceLine = line;
        dl.AlignOffset = ComputeAlignOffset(line);
        return dl;
    }

    /// <summary>
    /// DisplayLineAlignment → 字符串映射。
    /// LEFT → "left"，CENTER → "center"，RIGHT → "right"，未知值 → null。
    /// </summary>
    private static string? AlignToString(DisplayLineAlignment align) => align switch
    {
        DisplayLineAlignment.LEFT => "left",
        DisplayLineAlignment.CENTER => "center",
        DisplayLineAlignment.RIGHT => "right",
        _ => null
    };

    /// <summary>
    /// 计算 align 居中/右对齐的前导空格数（与 TerminalLineFormatter.FormatLineForTerminal 一致）。
    /// ButtonRegionTracker.UpdateFromSnapshot 把此偏移加到 entry.button.col 上得到 PTY 绝对列。
    /// LEFT 或未知 align 返回 0。CENTER: (gameWidth - textWidth)/2，RIGHT: gameWidth - textWidth。
    /// 测试环境（Config.Current 为 null）返回 0——BuildSnapshot 单测用反射设 align 绕过 Config，
    /// 此处避免触发 NullReferenceException。生产路径 Config.Current 在 Program 启动时设置。
    /// </summary>
    private static int ComputeAlignOffset(ConsoleDisplayLine line)
    {
        if (line.Align != DisplayLineAlignment.CENTER && line.Align != DisplayLineAlignment.RIGHT)
            return 0;
        if (Config.Current == null)
            return 0;
        TerminalLineFormatter.BuildTerminalLine(line, out int textWidth);
        int gameWidth = TerminalLineFormatter.GetGameColumnWidth();
        return line.Align == DisplayLineAlignment.CENTER
            ? Math.Max((gameWidth - textWidth) / 2, 0)
            : Math.Max(gameWidth - textWidth, 0);
    }
}
