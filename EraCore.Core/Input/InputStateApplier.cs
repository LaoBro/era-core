using MinorShift.Emuera.Primitives;

namespace MinorShift.Emuera.Input;

/// <summary>
/// 上行输入翻译（ADR-0031 / spec-v3 §5.1）：把各宿主的<b>上行事件</b>写进
/// <see cref="IInputHost"/>。与下行帧形状权威 <c>TurnFrames</c> 对称。
/// <para>
/// <b>唯一的状态写入点</b>（issue-002）：宿主提交经 <see cref="InputPort.Submit"/> 汇入
/// <see cref="Apply(InputFrame, IInputHost)"/>；wire 入口（<c>AgentJsonlProtocol</c>）也调用同一份。
/// CLI 的 VT 指针事件不再另写一套 —— 它们同样先构造成 <see cref="InputFrame"/> 再走门面。
/// 曾经的指针专用入口（各写各的、门卫口径不一致）已删除。
/// </para>
/// <para>
/// <b>位置</b>：本模块只依赖输入侧类型，住在 <c>Input/</c>（<see cref="IInputHost"/> 的邻域），
/// 不让底层反向引用协议层。
/// </para>
/// </summary>
internal static class InputStateApplier
{
    /// <summary>
    /// wire 入口：把一帧的状态字段写进宿主。提交值 <see cref="InputFrame.Value"/> 由调用方
    /// 决定是否派发，本方法不读它。
    /// <para>
    /// 全部状态字段都是<b>可选</b>的：一个都不带时本方法是空操作（旧客户端行为不变）。
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
    internal static void Apply(InputFrame e, IInputHost host)
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
}
