using MinorShift.Emuera.Primitives;

namespace MinorShift.Emuera.Input;

/// <summary>
/// 上行输入翻译（ADR-0031 / spec-v3 §5.1）：把各宿主的<b>上行事件</b>写进
/// <see cref="IInputHost"/>。与下行帧形状权威 <c>TurnFrames</c> 对称。
/// <para>
/// <b>两个适配器 ⇒ 这是真接缝</b>（不是假设接缝）：
/// <list type="bullet">
/// <item><b>wire 入口</b> <see cref="Apply(InputStateEvent, IInputHost)"/> ——
/// Server / MAUI / JSONL 管道：前端/客户端把 <see cref="InputStateEvent"/> 捎带在
/// <c>input</c> 帧里上报。</item>
/// <item><b>指针入口</b> <see cref="ApplyPointerGeometry"/> / <see cref="ApplyPointerKey"/> ——
/// CLI：VT 解析器把终端鼠标事件直接交给宿主，不经 wire。</item>
/// </list>
/// 两者原先各写各的（前者在 <c>AgentJsonlProtocol</c>，后者在 <c>AgentCliProtocol</c>），
/// 且<b>门卫口径不一致</b>：CLI 判能力位、wire 侧不判。收进本模块后口径统一。
/// </para>
/// <para>
/// <b>位置</b>：<see cref="InputStateEvent"/> 下移后本模块只依赖输入侧类型，
/// 故住在 <c>Shared/UI/Game</c>（<see cref="IInputHost"/> 的邻域），
/// 不再让底层反向引用协议层。
/// </para>
/// </summary>
internal static class InputStateApplier
{
    /// <summary>
    /// wire 入口：把一帧捎带的状态字段写进宿主。
    /// <para>
    /// 全部字段都是<b>可选</b>的：一个都不带时本方法是空操作（旧客户端行为不变）。
    /// 宿主未声明对应能力位时该字段被丢弃（<see cref="NullInputHost"/> 全丢弃）。
    /// </para>
    /// <para>
    /// 🔴 <b>不做任何合并、不补对称的 up</b>：<c>down</c> 与 <c>up</c> 由宿主（前端）
    /// 分两条独立帧上报，本方法只做「把这一帧的内容写进宿主」一件事。
    /// 曾在此把一次点击合成 down+up 一对 —— 那会让 <c>GETKEYTRIGGERED</c> 永远不成立
    /// （它的原版语义要求「此刻按着」，见 <see cref="KeyStateSet"/> 注释）。
    /// </para>
    /// <para>
    /// 🔴 <b>写入顺序固定为 focus → geometry → hover → keys</b>：
    /// <see cref="IInputHost.NotifyFocusChanged"/> 会清键，故它必须先于本帧的 <c>keys</c>。
    /// 另一条顺序由调用方（<c>AgentJsonlProtocol.RunLoopAsync</c>）持有 ——
    /// 本方法必须<b>先于</b> <c>DispatchInput</c>，否则脚本本轮读到的仍是旧值。
    /// </para>
    /// </summary>
    internal static void Apply(InputStateEvent e, IInputHost host)
    {
        // 能力位门卫（与指针入口同口径）：宿主没声明的通道不通电，
        // 写了也无消费者 —— 只会让快照里留下一份无人读的状态。
        if (e.Focus is { } focused && host.Capabilities.Has(InputCapability.Focus))
            host.NotifyFocusChanged(focused);

        if (e.Geometry is { } g && host.Capabilities.Has(InputCapability.Geometry))
            host.SubmitGeometry(new EmuPoint(g.x, g.y));

        if (e.HoverValue != null && host.Capabilities.Has(InputCapability.Hover))
            host.SubmitHover(e.HoverValue);

        if (e.Keys == null || !host.Capabilities.Has(InputCapability.KeyboardState))
            return;

        foreach (var k in e.Keys)
        {
            if (k == null || k.code < 0 || k.code >= 256)
                continue;
            host.SubmitKey(k.code, k.down);
        }
    }

    /// <summary>
    /// 指针入口（CLI / VT）：上报 console 局部坐标（左上基准）。
    /// 宿主未声明 <see cref="InputCapability.Geometry"/> 时静默丢弃 ——
    /// 写了也无消费者（<c>MoveMouse</c> 不会被调用）。
    /// </summary>
    internal static void ApplyPointerGeometry(IInputHost host, EmuPoint point)
    {
        if (!host.Capabilities.Has(InputCapability.Geometry))
            return;

        host.SubmitGeometry(point);
    }

    /// <summary>
    /// 指针入口（CLI / VT）：上报鼠标键的按下/松开（VK 键码）。
    /// 宿主未声明 <see cref="InputCapability.KeyboardState"/> 时静默丢弃。
    /// </summary>
    internal static void ApplyPointerKey(IInputHost host, int virtualKey, bool down)
    {
        if (!host.Capabilities.Has(InputCapability.KeyboardState))
            return;

        host.SubmitKey(virtualKey, down);
    }
}
