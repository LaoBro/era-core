using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 回合帧<b>形状权威</b>：合法帧的形状与序列化的唯一出口。
///
/// <para><b>职责边界</b> —— 本类型只管「一个合法帧长什么样」：哪些字段由形状固定、
/// 终态如何构造、何时盖协议版本、如何序列化。<b>不</b>持有任何运行期状态，也<b>不</b>知道
/// 「何时产帧」——产帧时机与空转帧抑制属 <see cref="AgentJsonlProtocol"/> 的运行期职责。
/// 依赖只限契约类型（<see cref="TurnRecord"/> / <see cref="AwaitInfo"/> /
/// <see cref="DisplayDiff"/> / <see cref="ErrorInfo"/> / <see cref="ConsoleState"/>），
/// 不触碰 <c>EmueraConsole</c> / <c>DisplayState</c> / <c>SessionIO</c>。</para>
///
/// <para>🔒 <b>终态由类型级方法守护</b>：终态只有 <see cref="Quit"/> / <see cref="Fatal"/> /
/// <see cref="Error"/> 三个入口，各自内部经 <see cref="AwaitInfo.Of"/> 强制
/// <c>ended{terminal}</c>。于是「Quit 帧还带 diff/generation」「错误帧照抄 console.State」
/// 这类非法组合在<b>编译期</b>就构造不出来——不是靠调用方记得。</para>
///
/// <para>与 <see cref="AwaitInfo.Of"/> 同一条路线：契约字段由单一纯工厂产出，
/// 互斥/共存不变式只在一处保证。</para>
/// </summary>
internal static class TurnFrames
{
    /// <summary>
    /// TurnRecord 序列化选项（converter 多态 + 源生成 resolver 的完整形态）。
    ///
    /// <para>3.3（NativeAOT）：<c>TypeInfoResolver</c> 指向源生成上下文——AOT 下反射序列化被禁用，
    /// <see cref="TurnOpConverter"/> / <see cref="LineOpConverter"/> 的装箱 Write 经此 resolver
    /// 按运行时类型解析，wire 不变。</para>
    /// </summary>
    internal static readonly JsonSerializerOptions TurnJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new TurnOpConverter(), new LineOpConverter() },
        TypeInfoResolver = EmueraJsonContext.Default,
    };

    /// <summary>
    /// TurnRecord 序列化（converter 多态 + 源生成 resolver 的完整形态）。
    /// 用 JsonTypeInfo 重载而非 options 重载：`Serialize(T, JsonSerializerOptions)` 标了
    /// RequiresUnreferencedCode/RequiresDynamicCode（IL2026/IL3050），即使运行时经
    /// <see cref="TurnJsonOptions"/>.TypeInfoResolver 走源生成也报静态警告；JsonTypeInfo 重载无标注。
    /// <see cref="JsonSerializerOptions.GetTypeInfo"/> 从 <see cref="TurnJsonOptions"/> 解析——converter 链
    /// （TurnOpConverter/LineOpConverter 的多态装箱 Write）完整保留，wire 与 options 重载一致。
    /// 注意：不可改用 <c>EmueraJsonContext.Default.TurnRecord</c>（context 无 Converters）——
    /// LineOp/TurnOp 抽象基类直接序列化只输出 type 鉴别符，diff 内容丢失
    /// （test_snapshot 抓到的 {"lineOps":[{"type":"append"}]} 空壳回归，2026.8.7）。
    /// </summary>
    internal static string SerializeTurn(TurnRecord turn)
        => JsonSerializer.Serialize(turn, (JsonTypeInfo<TurnRecord>)TurnJsonOptions.GetTypeInfo(typeof(TurnRecord)));

    /// <summary>
    /// 主流回合帧：正常停稳点产出的增量帧（含终帧 <c>BuildFinalTurn</c>）。
    ///
    /// <para><paramref name="isInitial"/> 决定是否盖协议版本——首帧是客户端与服务端对齐的
    /// 唯一机会，故携带；后续帧不带。调用方传 <c>isInitial</c> 而非裸版本号，
    /// 「何时盖章」的规则收在此处。</para>
    /// </summary>
    internal static string Ok(
        DisplayDiff? diff,
        AwaitInfo awaiting,
        string? error,
        long generation,
        bool isInitial)
        => SerializeTurn(new TurnRecord(
            diff: diff,
            awaiting: awaiting,
            error: error,
            protocolVersion: isInitial ? TurnRecord.CurrentProtocolVersion : null,
            generation: generation));

    /// <summary>
    /// QUIT/EXIT 终态帧：只剩 <c>awaiting.ended{terminal:"Quit"}</c>。
    /// 无 <c>diff</c> / <c>generation</c> 形参——「Quit 帧还带 inputType/timeLimit」的残留形态
    /// 在此结构性不可构造。
    /// </summary>
    internal static string Quit() => SerializeTurn(new TurnRecord(
        diff: null,
        awaiting: AwaitInfo.Of(null, ConsoleState.Quit, timedOut: false)));

    /// <summary>
    /// 致命错误帧（轴 A 的例外 B5）：<b>不经过脚本</b>、不对应任何停稳点，由第二产出者
    /// （<c>EraCore.Maui/BridgeHost.ShowError</c>）构造，形态是终态键
    /// <c>awaiting.ended{terminal:"Error"}</c>（<c>input</c> / <c>wait</c> 不得同时出现）。
    ///
    /// <para>🔴 <b>必须与主流帧共用同一条序列化路径</b>（<see cref="SerializeTurn"/>，含多态
    /// converter + 源生成 resolver）。MAUI 侧原先直接用 <c>EmueraJsonContext.Default.TurnRecord</c>
    /// ——那是<b>没有 Converters</b> 的上下文，历史上只因 fatal turn 的 <c>diff:null</c> 才侥幸不炸；
    /// <c>awaiting</c> 一变成内嵌对象，这条路径的风险就不再是「改一行注册」的量级。</para>
    /// </summary>
    internal static string Fatal(string message) => SerializeTurn(new TurnRecord(
        diff: null,
        awaiting: AwaitInfo.Of(null, ConsoleState.Error, timedOut: false),
        error: message));

    /// <summary>
    /// 会话级异常帧（初始化阶段失败 / 游戏循环回调异常 / 脚本运行期异常）——同样<b>不经过脚本</b>、是终态。
    ///
    /// <para>与 <see cref="Fatal"/> 同属「第二产出者」：必须与主流帧共用同一条
    /// 序列化路径（<see cref="SerializeTurn"/>），形状是 <c>awaiting.ended{terminal:"Error"}</c>。
    /// 差别只在这里可额外携带结构化诊断 <paramref name="errorInfo"/>。</para>
    ///
    /// <para>⚠️ 这些调用点原先是手拼 <c>JsonObject</c>（含旧的顶层 <c>state</c>、无
    /// <c>awaiting</c>）——v12 起前端要求 <c>awaiting</c> 必填，手拼帧会被判为协议解析失败、
    /// 真实错误反而看不到。</para>
    ///
    /// <para><paramref name="errorText"/> 由调用方决定粒度：<c>Session</c> 传完整串（含堆栈）以兼容
    /// 既有消费者，脚本运行期异常传 <c>ex.Message</c>。</para>
    /// </summary>
    internal static string Error(string errorText, ErrorInfo? errorInfo)
        => SerializeTurn(new TurnRecord(
            diff: null,
            awaiting: AwaitInfo.Of(null, ConsoleState.Error, timedOut: false),
            error: errorText,
            errorInfo: errorInfo));

    /// <summary>
    /// 抑制指纹的「形状」部分（spec §2.3）：哪个键出现。
    /// 终态带上 <c>terminal</c>——<c>Quit</c> 与 <c>Error</c> 都是终态但<b>不是</b>同一件事。
    ///
    /// <para>与帧形状定义同处：一旦 <see cref="AwaitInfo"/> 增删维度，此处与
    /// <see cref="Ok"/> 必须同改，放在一起才不漂移。</para>
    /// </summary>
    internal static string DescribeShape(AwaitInfo awaiting)
    {
        if (awaiting.ended is { } ended)
            return "ended:" + ended.terminal;
        return (awaiting.input != null ? "input" : "")
               + (awaiting.wait != null ? "+wait" : "");
    }

    /// <summary>
    /// 抑制指纹的「wait 的性质」部分（spec §2.3）：durationMs + displayTime + timeUpMessage。
    /// 刻意<b>不</b>含 <c>timedOut</c>（一次性旗标，不是等待本身的性质；含它会让 B3 永远不可抑制）。
    /// <c>wait</c> 缺省 = 无限期，返回 null（此时没有任何「wait 的性质」参与比较）。
    ///
    /// <para>issue 10 后指纹里不再有 <c>interruptible</c> —— 该键已从契约撤回，
    /// 「AWAIT 与 TWAIT t,1 同形」也因此反映在抑制判据上（两者不再因它产生假差异）。</para>
    /// </summary>
    internal static string? DescribeWaitProps(AwaitWait? wait)
        => wait == null
            ? null
            : $"{wait.durationMs}|{wait.displayTime}|{wait.timeUpMessage}";
}
