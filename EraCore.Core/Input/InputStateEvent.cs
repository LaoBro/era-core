namespace MinorShift.Emuera.Input;

/// <summary>
/// 键鼠事件（Win32 VK 键码 + 按下/松开）。鼠标键用 1/2/4（= <c>VK_LBUTTON/RBUTTON/MBUTTON</c>）。
/// <para>
/// ⚠️ <b>逐条立即上报，不做时间合并</b>（spec-v3 §3.6）：down+up 若被合并进同一帧，
/// 宿主按序处理后电平已归零 ⇒ 按键被压成零宽度脉冲 ⇒ 快速按键的触发整条丢失。
/// 一帧含多条 <c>keys</c> 只用于「同时按下」的组合键，不是跨时间合并。
/// </para>
/// </summary>
/// <remarks>
/// 2026-10-19 自 <c>Agent/JsonlCommand.cs</c> 下移至输入侧：它是两个 wire 入口
/// （<c>JsonlCommand</c> 协议帧 / <c>HttpInput</c> HTTP body）<b>共用</b>的字段类型，
/// 放在 Agent 会让 Server 反向引用协议层。
/// </remarks>
internal sealed record InputKeyEvent(int code, bool down);

/// <summary>console 局部坐标（<b>左上基准</b>，ADR-0031 §8）。仅 <c>Geometry</c> 宿主发送；M5b 前不启用。</summary>
internal sealed record InputGeometry(int x, int y);

/// <summary>
/// 上行输入事件 —— <b>输入侧的一等类型</b>，两个 wire 入口的共同终点。
/// <para>
/// <b>为什么要有它</b>：上行输入状态原先有四处形状真相 —— 前端 <c>InputExtra</c>、
/// HTTP body POCO <c>HttpInput</c>、手拼的 <c>JsonObject</c>、协议帧 <c>JsonlCommand</c>。
/// 前三者无法物理合并（分属 TS / 反射 POCO / 源生成 record 三种序列化机制），
/// 但它们<b>都能收敛到本类型</b>：wire 层各自保持扁平（契约不变），
/// 进入 Core 的第一步就转成 <see cref="InputStateEvent"/>，
/// 其后的翻译链路只有这一个类型。
/// </para>
/// <para>
/// 与 <see cref="InputSnapshot"/> 的关系：本类型是 <b>push 面</b>（宿主/前端写进来），
/// <see cref="InputSnapshot"/> 是 <b>pull 面</b>（引擎读出去）。二者分居两侧，不互相引用。
/// </para>
/// <para>
/// ⚠️ <b>record 相等性的陷阱</b>：<see cref="Keys"/> 是数组，record 自动生成的
/// <c>Equals</c> 对它按<b>引用</b>比较 —— 反序列化出来的实例即便内容相同也不相等。
/// 需要比较时逐字段断言，不要直接 <c>Assert.Equal</c> 两个实例。
/// </para>
/// </summary>
internal sealed record InputStateEvent(
    string? HoverValue = null,
    InputKeyEvent[]? Keys = null,
    InputGeometry? Geometry = null,
    bool? Focus = null)
{
    public static InputStateEvent Empty { get; } = new();

    /// <summary>
    /// 是否携带任一状态字段（<b>不含</b>提交值 <c>value</c>）。
    /// spec-v3 §5.3：为 true 时即使 <c>value</c> 缺省也是合法输入（「状态类」帧）。
    /// </summary>
    public bool HasAny => HoverValue != null || Keys != null || Geometry != null || Focus.HasValue;
}
