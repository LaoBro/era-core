using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.GameView;

internal record TurnRecord(
    string state,
    string? inputType,
    bool needValue,
    DisplayDiff? diff,
    // v12：唯一契约字段（必填、非 nullable、每帧必现）——「服务端此刻在等什么」。
    // 展开阶段（05）它与下面七个旧顶层字段并存；收缩阶段（09）删旧字段。
    AwaitInfo awaiting,
    string? error = null,
    int? protocolVersion = null,
    long? timeLimit = null,
    bool? displayTime = null,
    string? timeUpMessage = null,
    bool timedOut = false,
    long generation = 0,
    // 诊断：暂停运行期异常的结构化信息（type/message/inner），供 agent 一次性定位。
    // 纯增量 nullable 字段 + WhenWritingNull → 非异常 turn 不出现，不升协议版本。
    ErrorInfo? errorInfo = null
)
{
    /// <summary>
    /// Agent 协议版本的单一来源（ADR-0013 / ADR-0014 / ADR-0016）。
    /// TurnRecord（增量 diff 流）与 DisplaySnapshot（全量快照）必须携带同一版本号——
    /// 晚加入者用 snapshot.protocolVersion 校验与 diff 流兼容性。
    /// AgentJsonlProtocol 与 DisplayState 共同引用此常量，避免版本升级时多处不同步。
    /// Phase 5-2：v4 = diff 模式（ops 字段已移除，仅 diff）。
    /// Phase 5-3/plan C：v5 = diff 显式清空信号——TruncateLinesOp/ReplaceAllOp 被
    /// ClearLineDiffOp(截断即清行)/ClearScreenOp(全清) 取代，清空语义自描述（ADR-0014 统一真相源）。
    /// ADR-0016（v6）：新增 4 个 TINPUT timer 字段——timeLimit / displayTime / timeUpMessage
    /// （nullable + WhenWritingNull，非 TINPUT 时不出现）+ timedOut（非 nullable bool，默认 false）。
    /// v8（issue 02）：PrintSegment 加 image?/shape?；DisplaySnapshot/DisplayDiff 加 bgImages?；
    /// TurnOp 加 set_bg_image/remove_bg_image/clear_bg_image。纯增量，老客户端忽略未知字段自然降级。
    /// v9（issue 07）：SegmentImage 加 crop?（裁切矩形几何）——图集 sprite（resources/*.csv
    /// 第 3-6 列）只显示裁切区。crop 携带已缩放几何（img 负偏移 + 元素尺寸），前端零布局数学。
    /// v10：PrintSegment 加 underline?，保留 PRINT_SLIDER 用于绘制彩色横线的下划线空格样式。
    /// v11：PrintSegment 加 strikeout?——PRINT_SLIDER 实际用 ERB FONTSTYLE 位4（删除线，取消線）
    /// 绘制滑条轨道，v10 只实现 underline 导致删除线缺失、被 v10 修复提交误映射为 underline；
    /// 现按 ERB 位契约（位4=删除线、位8=下划线）对称补齐 strikeout 字段。
    /// v12（issue 05）：新增 `awaiting`（唯一契约字段：input / wait 两个正交维度 + ended 互斥终态），
    /// 描述「服务端此刻在等什么」；旧七个顶层字段暂时保留（展开阶段），09 删除。
    /// </summary>
    internal const int CurrentProtocolVersion = 12;
}

/// <summary>
/// 结构化异常信息（诊断用，目标 1）：type（完整类型名）+ message + 可选 inner。
/// 供 agent 一次定位崩溃；对比裸 <c>error</c> 文本串，避免在长堆栈里手工挖类型与信息。
/// </summary>
internal record ErrorInfo(
    string type,
    string message,
    ErrorInfo? inner = null
)
{
    public const int MaxDepth = 4;

    /// <summary>从异常构造（递归 inner，最深 <see cref="MaxDepth"/> 层防过度展开）。</summary>
    internal static ErrorInfo? From(Exception? ex, int depth = 0)
    {
        if (ex == null)
            return null;
        return new ErrorInfo(
            ex.GetType().FullName ?? ex.GetType().Name,
            ex.Message,
            depth < MaxDepth ? From(ex.InnerException, depth + 1) : null);
    }

    /// <summary>
    /// 转为 JsonObject（NativeAOT 安全：JsonNode 手拼，不走匿名类型反射序列化）。
    /// 供 Session 游戏循环异常的 errorInfo 兜底分支使用（该路径不经 TurnRecord 源生成序列化）。
    /// </summary>
    internal JsonObject ToJsonObject()
    {
        var node = new JsonObject { [nameof(type)] = type, [nameof(message)] = message };
        if (inner != null)
            node[nameof(inner)] = inner.ToJsonObject();
        return node;
    }
}

/// <summary>
/// 两个连续 DisplaySnapshot 之间的差异（Phase 2 / ADR-0014）。
/// 仅含行级操作序列 + 背景色；state/inputType/needValue/protocolVersion 由外层 TurnRecord 携带，不在此重复。
/// Phase 5 后 diff 是唯一的增量格式（ops 已废弃移除）。
/// plan C（v5）：行级操作显式携带清空语义——AppendLinesOp（追加）/ ClearLineDiffOp（截尾=清行）/
/// ClearScreenOp（全清）。清空不再是隐含的结构推断，而是 op 名即语义（DisplayState 统一真相源）。
/// </summary>
internal record DisplayDiff(
    List<LineOp> lineOps,
    string? bgColor,
    List<BgImageState>? bgImages = null
);

/// <summary>
/// 行级差异操作（Phase 2 / plan C v5 + shift_head 扩展）。Emuera 显示模型以追加为主：
/// 新内容追加到末尾，CLEARLINE 从末尾删除，CLEAR 全部清除；MaxLog 截断时头部行被删除。
/// 故 diff 需要四类操作：
/// - AppendLinesOp：curr 比 prev 多出的尾部行
/// - ClearLineDiffOp：清行（CLEARLINE）——截断末 clearCount 行，其后 AppendLinesOp 为 reprint
/// - ClearScreenOp：全清（CLEAR / 全重置）——清空全部行，其后 AppendLinesOp 为重印全量
/// - ShiftHeadLineOp：头部截断（MaxLog 滚动）——删除头部 count 行，与 AppendLinesOp 共存
/// 公共前缀 k 之后的差异用 ClearLineDiffOp(clearCount=prev.Count-k)+Append 表达
/// （含末行原地编辑：PRINT 不带换行落到最后一行 = ClearLineDiffOp(1)+Append(1)）。
/// shift_head 由 ConsolePrintManager.RemoveAt(0) 主动 enqueue ShiftHeadTurnOp 捕获，
/// 避免 StructuralDiff 走 CommonPrefix 检测到 k=0 误判为 ClearScreenOp + 全量重印。
/// </summary>
[JsonConverter(typeof(LineOpConverter))]
internal abstract record LineOp(string type);

/// <summary>追加新行（curr 比 prev 多出的尾部）。</summary>
internal record AppendLinesOp(List<DisplayLine> newLines) : LineOp("append");

/// <summary>清行（CLEARLINE 场景）：从末尾删除 clearCount 行。其后如有 AppendLinesOp 即 reprint。
/// 取代 Phase 2-4 的 TruncateLinesOp——op 名即语义，前端无需从「截断」推断「清行」。</summary>
internal record ClearLineDiffOp(int clearCount) : LineOp("clear_line_diff");

/// <summary>全清（CLEAR / 全重置场景）：清空全部行。其后如有 AppendLinesOp 即重印全量内容。</summary>
internal record ClearScreenOp() : LineOp("clear_screen");

/// <summary>头部截断（MaxLog 滚动场景）：删除头部 count 行。
/// 由 ConsolePrintManager 在 displayLineList.Count > Config.MaxLog 时 RemoveAt(0) 主动 enqueue
/// ShiftHeadTurnOp 捕获——避免 StructuralDiff 把头部删除误判为 ClearScreenOp + 全量重印。
/// 与 AppendLinesOp 共存：长游戏达 MaxLog 后每新增一行 = ShiftHeadLineOp(1) + AppendLinesOp(1)。
/// 与 ClearLineDiffOp 共存：头部截断 + 尾部 CLEARLINE 可同回合发生。
/// 与 ClearScreenOp 互斥：全清已无头部可截。</summary>
internal record ShiftHeadLineOp(int count) : LineOp("shift_head");

[JsonConverter(typeof(TurnOpConverter))]
internal abstract record TurnOp(string type);

internal record PrintOp(
    List<PrintSegment> segments,
    ButtonRef? button
) : TurnOp("print");

internal record NewLineOp(string? align) : TurnOp("newline");

internal record ClearLineOp(int n) : TurnOp("clearline");

internal record ClearOp() : TurnOp("clear");

internal record SetBgOp(string color) : TurnOp("set_bg");

/// <summary>v8：背景图 set——src 为游戏目录内相对路径；depth 决定 z 序；opacity 0.0-1.0（0-255/255）。</summary>
internal record SetBgImageOp(string src, long depth, float opacity) : TurnOp("set_bg_image");

/// <summary>v8：背景图 remove——按 src 移除（WinForms 语义：同名多份时移除最前一份）。</summary>
internal record RemoveBgImageOp(string src) : TurnOp("remove_bg_image");

/// <summary>v8：背景图 clear——全部移除。</summary>
internal record ClearBgImageOp() : TurnOp("clear_bg_image");

/// <summary>头部截断（MaxLog 滚动）：ConsolePrintManager.RemoveAt(0) 时 enqueue。
/// DrainAndClassifyClears 累加为 ShiftHeadCount，ComputeDiff 在 lineOps 前置 ShiftHeadLineOp(count)。</summary>
internal record ShiftHeadTurnOp(int count) : TurnOp("shift_head");

internal record PrintSegment(
    string text,
    string? color,
    bool? bold,
    bool? italic,
    string? fontname,
    SegmentImage? image = null,
    SegmentShape? shape = null,
    bool? underline = null,
    bool? strikeout = null
);

/// <summary>
/// v8：图片 segment（issue 02）——src/srcb/srcm 为游戏目录内相对路径，
/// width/height/ypos 为已解析 px 几何（01 产出，前端零布局数学）。
/// srcb = 悬停/选中态替换图，srcm = 点击读像素色的映射图。
/// v9（issue 07）：crop 为可选裁切几何——图集 sprite 只显示裁切区。
/// </summary>
internal record SegmentImage(
    string src,
    string? srcb,
    string? srcm,
    int width,
    int height,
    int ypos,
    SegmentCrop? crop = null
);

/// <summary>
/// v9：图片裁切几何（issue 07）——resources/*.csv 第 3-6 列裁切矩形已按显示尺寸缩放。
/// x/y = img 元素负偏移（margin-left/margin-top，≤0）；imgWidth/imgHeight = img 元素渲染尺寸
/// （整图×缩放系数，缩放系数 = 显示尺寸/裁切尺寸）。
/// 前端渲染：掩膜 → overflow:hidden 定尺寸容器（width/height）→ img 负 margin——零布局数学。
/// </summary>
internal record SegmentCrop(
    int x,
    int y,
    int imgWidth,
    int imgHeight
);

/// <summary>
/// v8：形状 segment（issue 02）——type='rect'，x/y/width/height 已解析 px，color 填充色 hex。
/// 1 参 rect（整行色条）与 4 参（绝对定位）统一为该形态。
/// </summary>
internal record SegmentShape(
    string type,
    int x,
    int y,
    int width,
    int height,
    string color
);

/// <summary>
/// v8：背景图状态（issue 02）——depth 决定 z 序（WinForms 降序烘焙），opacity 0.0-1.0。
/// 用于 DisplaySnapshot.bgImages 与 DisplayDiff.bgImages。
/// </summary>
internal record BgImageState(
    string src,
    long depth,
    float opacity
);

internal record ButtonRef(object value, bool isInteger, long generation, int? col = null, int? width = null);

internal sealed class TurnOpConverter : JsonConverter<TurnOp>
{
    public override TurnOp? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("TurnOp deserialization not supported");

    public override void Write(Utf8JsonWriter writer, TurnOp value, JsonSerializerOptions options)
    {
        // NativeAOT：装箱反射重载（Serialize((object)value, options)）触发 IL2026/IL3050。
        // 改经 options.GetTypeInfo 按运行时具体类型取源生成 TypeInfo——EmueraJsonContext 已注册
        // 全部多态叶子（见 EmueraJsonContext.cs 机制 2 注释），GetTypeInfo 命中，wire 与反射版逐字节一致。
        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
    }
}

/// <summary>
/// LineOp 多态序列化（Phase 2 / plan C v5）。与 TurnOpConverter 同模式——
/// 序列化时按具体子类型（AppendLinesOp/ClearLineDiffOp/ClearScreenOp）写出全部字段 + type 鉴别符；
/// 反序列化不支持（服务端只产出 diff，前端单向消费）。
/// </summary>
internal sealed class LineOpConverter : JsonConverter<LineOp>
{
    public override LineOp? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => throw new NotSupportedException("LineOp deserialization not supported");

    public override void Write(Utf8JsonWriter writer, LineOp value, JsonSerializerOptions options)
    {
        // 同 TurnOpConverter.Write——GetTypeInfo 按运行时类型取源生成 TypeInfo，NativeAOT 兼容。
        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));
    }
}
