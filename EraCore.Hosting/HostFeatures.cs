using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera.Input;

namespace EraCore.Hosting;

/// <summary>
/// 宿主能力清单的语义项。只收录能在 <c>docs/guides/turn-protocol.md</c> 找到条款的语义；
/// 每个枚举值的语义与条款号集中在 <see cref="HostFeatureCatalog"/>。
/// </summary>
public enum HostFeature
{
    SessionLifecycle,
    Snapshot,
    TurnStream,
    RevisionDedup,
    DiffAppend,
    DiffReplaceEqualLength,
    DiffShiftHead,
    DiffClearScreen,
    Generation,
    AwaitingInput,
    AwaitingTimeout,
    AwaitingZeroDurationWait,
    AwaitingUnknownKey,
    TerminalFrames,
    ErrorFrame,
    TextStyles,
    AlignGeometry,
    ButtonHitTest,
    VirtualWindow,
    InputSubmission,
    Images,
    BackgroundImages,
}

/// <summary>
/// 单个能力的机械可核对描述。<see cref="DtoFields"/> 写协议里实际出现的字段名，
/// 供前端自检与文档化；<see cref="RequiresRender"/> 表示该项属于渲染/显示语义。
/// </summary>
public sealed record HostFeatureSpec(
    HostFeature Feature,
    string Semantics,
    IReadOnlyList<string> DtoFields,
    bool Required,
    bool RequiresRender)
{
    /// <summary>该语义首次成为 wire 契约的版本；<see cref="HostFeatureCatalog.RequiredFor"/> 据此过滤。</summary>
    public int SinceDataVersion { get; init; } = HostContract.DataVersion;
}

/// <summary>
/// 能力目录：协议语义与条款号的单一登记处。
/// <para><b>不保证任何前端正确</b>——它只把「读文档才知道」的语义变成可逐项对照的清单。</para>
/// </summary>
public static class HostFeatureCatalog
{
    public static IReadOnlyList<HostFeatureSpec> All { get; } = new HostFeatureSpec[]
    {
        new(HostFeature.SessionLifecycle,
            "会话 Start/Stop/WaitForState 的生命周期语义；帧内的 state 已删除，会话级状态只在 GET /state 出现。",
            new[] { "state" }, Required: false, RequiresRender: false),
        new(HostFeature.Snapshot,
            "GET /snapshot 提供 lines/bgColor/bgImages/awaiting/protocolVersion/generation/revision 的全量基线。",
            new[] { "lines", "bgColor", "bgImages", "awaiting", "protocolVersion", "generation", "revision" },
            Required: true, RequiresRender: true),
        new(HostFeature.TurnStream,
            "帧经 /turn 长轮询或 /ws 下发；晚加入者不回放历史，必须先取快照再应用增量（§1、§6、§9）。",
            new[] { "diff", "awaiting", "protocolVersion", "generation", "revision" },
            Required: true, RequiresRender: false),
        new(HostFeature.RevisionDedup,
            "宿主丢弃 revision <= 初始快照 revision 的帧，再按 revision 顺序应用增量（§2 revision、§6）。",
            new[] { "revision" }, Required: true, RequiresRender: false),
        new(HostFeature.DiffAppend,
            "lineOps append 把 newLines 追加到末尾（§4）。",
            new[] { "diff.lineOps", "lineOps[].type", "lineOps[].newLines" },
            Required: true, RequiresRender: true),
        new(HostFeature.DiffReplaceEqualLength,
            "clear_line_diff 从末尾删 clearCount 行，其后 append 即重印；等长行替换/末行编辑用它表达（§4）。",
            new[] { "diff.lineOps", "lineOps[].clearCount", "lineOps[].newLines" },
            Required: true, RequiresRender: true),
        new(HostFeature.DiffShiftHead,
            "shift_head 从头部删除 count 行（MaxLog 滚动截断），可与 clear_line_diff/append 共存（§4）。",
            new[] { "diff.lineOps", "lineOps[].count" },
            Required: true, RequiresRender: true),
        new(HostFeature.DiffClearScreen,
            "clear_screen 全清全部行，其后 append 即重印全量（§4）。",
            new[] { "diff.lineOps", "lineOps[].type" },
            Required: true, RequiresRender: true),
        new(HostFeature.Generation,
            "按钮代号：结构性变更递增；提交按钮时回传同一 generation，陈旧代输入无效（§5）。",
            new[] { "generation", "button.generation" },
            Required: true, RequiresRender: false),
        new(HostFeature.AwaitingInput,
            "awaiting.input.type 的输入类型表（IntValue/StrValue/AnyValue/AnyKey/EnterKey/按钮等）（§2、§3）。",
            new[] { "awaiting.input", "awaiting.input.type" },
            Required: true, RequiresRender: false),
        new(HostFeature.AwaitingTimeout,
            "awaiting.wait.durationMs > 0 的定时等待；倒计时/超时文案/timedOut 仅在真/有值时出现（§2）。",
            new[] { "awaiting.wait", "awaiting.wait.durationMs", "awaiting.wait.displayTime", "awaiting.wait.timeUpMessage", "awaiting.wait.timedOut" },
            Required: true, RequiresRender: false),
        new(HostFeature.AwaitingZeroDurationWait,
            "awaiting.wait.durationMs == 0 是零时长让路点，语义为下一帧立刻到（§2、§8）。",
            new[] { "awaiting.wait", "awaiting.wait.durationMs" },
            Required: true, RequiresRender: false),
        new(HostFeature.AwaitingUnknownKey,
            "awaiting 出现未知键/未知等待选项时必须回退 GET /snapshot，不得静默忽略；未知 terminal 按终态处理（§2）。",
            new[] { "awaiting", "awaiting.*" },
            Required: true, RequiresRender: false),
        new(HostFeature.TerminalFrames,
            "awaiting.ended.terminal 是终态，出现时 input/wait 必不出现；终态不可抑制（§2、§8 B5）。",
            new[] { "awaiting.ended", "awaiting.ended.terminal" },
            Required: true, RequiresRender: false),
        new(HostFeature.ErrorFrame,
            "致命错误帧为 ended{terminal:Error} + error（+ errorInfo），不经过脚本（§7、§8 B5）。",
            new[] { "awaiting.ended", "awaiting.ended.terminal", "error", "errorInfo" },
            Required: true, RequiresRender: false),
        new(HostFeature.TextStyles,
            "PrintSegment 的 color/bold/italic/underline/strikeout/fontname；null/省略表示默认或不加该样式（§4）。",
            new[] { "segments[].text", "segments[].color", "segments[].bold", "segments[].italic", "segments[].underline", "segments[].strikeout", "segments[].fontname" },
            Required: true, RequiresRender: true),
        new(HostFeature.AlignGeometry,
            "DisplayLine.align 取 left/center/right；前导偏移是容器列/宽减去内容后的几何（§4）。",
            new[] { "lines[].align" }, Required: true, RequiresRender: true),
        new(HostFeature.ButtonHitTest,
            "ButtonRef.col/width 给出字符列区间；命中按行 + 列判定，并受 generation 过期约束（§4、§5）。",
            new[] { "button.col", "button.width", "button.value", "button.isInteger", "button.generation" },
            Required: true, RequiresRender: true),
        new(HostFeature.VirtualWindow,
            "虚拟滚动的可见区间/overscan/贴底/滚动条比例是平台无关纯算术，无 wire 字段（§4 的「零布局数学」宿主侧投影）。",
            Array.Empty<string>(), Required: false, RequiresRender: true),
        new(HostFeature.InputSubmission,
            "POST /input 与 /ws 上行文本帧同源：{\"type\":\"input\",\"value\":\"...\"}（§9）。",
            new[] { "type", "value" }, Required: false, RequiresRender: false),
        new(HostFeature.Images,
            "PrintSegment.image / SegmentImage.src/srcb/srcm/width/height/ypos/crop（§4、§6 v8/v9）。",
            new[] { "segments[].image", "image.src", "image.srcb", "image.srcm", "image.width", "image.height", "image.ypos", "image.crop" },
            Required: false, RequiresRender: true),
        new(HostFeature.BackgroundImages,
            "bgImages 为整体替换语义：省略/null = 未变，[] = 清空，非空 = 全量新状态（§4）。",
            new[] { "bgImages", "bgImages[].src", "bgImages[].depth", "bgImages[].opacity" },
            Required: false, RequiresRender: true),
    };

    /// <summary>给定 wire 数据版本下要求宿主实现的语义项；当前目录以 v13 为基线登记。</summary>
    public static IReadOnlyList<HostFeature> RequiredFor(int dataVersion)
        => All.Where(s => s.Required && s.SinceDataVersion <= dataVersion)
              .Select(s => s.Feature)
              .ToArray();

    public static HostFeatureSpec? Find(HostFeature feature)
        => All.FirstOrDefault(s => s.Feature == feature);
}

/// <summary>
/// 宿主能力清单：版本协商 + 输入/渲染能力位 + 语义项。
/// </summary>
public sealed record HostManifest(
    int ApiVersion,
    int DataVersion,
    InputCapabilities InputCapabilities,
    RenderCapability RenderCapability,
    IReadOnlyList<HostFeature> Features)
{
    public bool Has(HostFeature feature) => Features.Contains(feature);

    public HostManifest WithFeatures(IEnumerable<HostFeature> features)
        => this with { Features = features.Distinct().ToArray() };

    public HostManifest WithCapabilities(HostCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return this with { InputCapabilities = capabilities.Input, RenderCapability = capabilities.Render };
    }
}
