using MinorShift.Emuera.Input;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 上行 <c>input</c> 帧（spec-v3 §5.1）。既有两字段 <c>type</c> / <c>value</c> 语义完全不变，
/// 其余为<b>可选捎带</b>的状态字段 —— 旧客户端不发送时行为与今天逐项一致。
/// <para>
/// <b>两类发送</b>（spec-v3 §5.3）：
/// <list type="bullet">
/// <item><b>提交类</b>：带 <c>value</c> ⇒ 经 <c>DispatchInput</c> 推进，并产一帧回音（既有行为）。</item>
/// <item><b>状态类</b>：<c>value</c> 缺省 ⇒ <b>只写 <see cref="MinorShift.Emuera.Input.IInputHost"/></b>，
/// 不派发、不产帧（值等待下带空 <c>value</c> 会触发 <c>OnInputRejected</c>，故必须跳过）。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 <b>状态字段的键名必须扁平</b>：本 record 是 wire 契约本身（前端 <c>InputExtra</c>
/// 与 HTTP body 都按这套扁平键名发送），不得把四个字段收进嵌套对象 —— 那会改 wire。
/// 形状收口改在 Core 侧做：进 Core 第一步就 <see cref="ToFrame"/> 成
/// <see cref="InputFrame"/>，其后的状态应用与派发链路只有那一个类型。
/// </para>
/// </summary>
/// <remarks>
/// ⚠️ 曾有一个 <c>button</c> 字段（1/2/4 = 左/右/中）。<b>已删除</b>：它会把一次点击
/// 合成 down+up 一对，而 <c>GETKEYTRIGGERED</c> 要求「此刻按着」（原版 <c>s &lt; 0</c>），
/// 合成后电平立即归零 ⇒ 触发永远不成立。鼠标键现统一用 <c>keys</c> 的
/// <c>code</c> = 1/2/4（= <c>VK_LBUTTON/RBUTTON/MBUTTON</c>），天然支持 down/up 分开发。
/// </remarks>
internal record JsonlCommand(
    string type,
    string? value,
    string? hoverValue = null,
    InputKeyEvent[]? keys = null,
    InputGeometry? geometry = null,
    bool? focus = null)
{
    /// <summary>
    /// wire → 输入侧：构造 <see cref="InputFrame"/>。
    /// 字段映射的唯一实现在 <see cref="InputFrame.FromWire"/>（HTTP body 入口也走它）。
    /// </summary>
    public InputFrame ToFrame() => InputFrame.FromWire(value, hoverValue, keys, geometry, focus);
}
