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
internal record JsonlCommand(
    string type,
    string? value,
    int? button = null,
    string? hoverValue = null,
    InputKeyEvent[]? keys = null,
    InputGeometry? geometry = null,
    bool? focus = null);

/// <summary>键盘事件（Win32 VK 键码 + 按下/松开）。前端按 ~50ms 合并成批，keydown/keyup 必须成对。</summary>
internal sealed record InputKeyEvent(int code, bool down);

/// <summary>console 局部坐标（<b>左上基准</b>，ADR-0031 §8）。仅 <c>Geometry</c> 宿主发送；M5 前不启用。</summary>
internal sealed record InputGeometry(int x, int y);
