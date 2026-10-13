namespace MinorShift.Emuera.GameView;

/// <summary>
/// 上行 <c>input</c> 帧（spec-v3 §5.1）。既有两字段 <c>type</c> / <c>value</c> 语义完全不变，
/// 其余为<b>可选捎带</b>的状态字段 —— 旧客户端不发送时行为与今天逐项一致。
/// <para>
/// <b>两类发送</b>（spec-v3 §5.3）：
/// <list type="bullet">
/// <item><b>提交类</b>：带 <c>value</c> ⇒ 经 <c>DispatchInput</c> 推进，并产一帧回音（既有行为）。</item>
/// <item><b>状态类</b>：<c>value</c> 缺省 ⇒ <b>只写 <see cref="MinorShift.Emuera.UI.Game.IInputHost"/></b>，
/// 不派发、不产帧（值等待下带空 <c>value</c> 会触发 <c>OnInputRejected</c>，故必须跳过）。</item>
/// </list>
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
    bool? focus = null);

/// <summary>
/// 键鼠事件（Win32 VK 键码 + 按下/松开）。鼠标键用 1/2/4（= <c>VK_LBUTTON/RBUTTON/MBUTTON</c>）。
/// <para>
/// ⚠️ <b>逐条立即上报，不做时间合并</b>（spec-v3 §3.6）：down+up 若被合并进同一帧，
/// 宿主按序处理后电平已归零 ⇒ 按键被压成零宽度脉冲 ⇒ 快速按键的触发整条丢失。
/// 一帧含多条 <c>keys</c> 只用于「同时按下」的组合键，不是跨时间合并。
/// </para>
/// </summary>
internal sealed record InputKeyEvent(int code, bool down);

/// <summary>console 局部坐标（<b>左上基准</b>，ADR-0031 §8）。仅 <c>Geometry</c> 宿主发送；M5 前不启用。</summary>
internal sealed record InputGeometry(int x, int y);
