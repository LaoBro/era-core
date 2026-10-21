using System;
using System.Threading.Channels;

namespace MinorShift.Emuera.Output;

/// <summary>
/// 输出轴的宿主门面（issue-004 / spec §3.2）——宿主只经本类读取输出。
///
/// <para><b>两条读取途径，对应两种宿主复杂度</b>：</para>
/// <list type="bullet">
/// <item><b><see cref="Snapshot"/></b>（简单路径）：当前全量画面。宿主只需渲染「现在有哪些行」，
/// 不必理解 diff / op / generation。</item>
/// <item><b><see cref="Turns"/></b>（完整路径）：增量回合流。需要增量渲染的宿主编译期读取
/// <see cref="Turn.Record"/>，也可直接原样转发 <see cref="Turn.Wire"/>（O-4 同序、同实例）。</item>
/// </list>
///
/// <para><b>构造权留在 Core</b>：宿主从组装根拿到实例，拿不到 <see cref="DisplayState"/> / <see cref="OutputHub"/>
/// 等内部件，也无法伪造快照或帧（公开 DTO 只有只读属性、无公开构造器）。</para>
///
/// <para>⚠️ <b><see cref="Turns"/> 的语义</b>：每次读取都会向中枢注册一个<b>新观察者</b>
/// （与原 WS 订阅同一模型）——晚加入者只收之后的帧，不回放历史。需要「初始画面 + 后续增量」的宿主
/// 应先订阅 <see cref="Turns"/>，再读 <see cref="Snapshot"/>，或接受二者之间的丢帧。
/// 为 <c>null</c> 表示本宿主不消费增量流（CLI VT 渲染只走快照）。</para>
/// </summary>
public sealed class OutputPort
{
    private readonly DisplayState _state;
    private readonly OutputHub? _hub;

    internal OutputPort(DisplayState state, OutputHub? hub = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _hub = hub;
    }

    /// <summary>
    /// 简单路径：当前全量画面（读取时先按画面判据刷新，保证与引擎实时推进一致）。
    /// </summary>
    public DisplaySnapshot Snapshot => _state.Current;

    /// <summary>
    /// 完整路径：增量回合流。每次读取注册一个新观察者；<c>null</c> = 本宿主只消费快照。
    /// </summary>
    public ChannelReader<Turn>? Turns => _hub?.Subscribe();

    /// <summary>
    /// 帧级刷新：画面有变化时返回 true。CLI 渲染器据此跳过 no-op 帧；
    /// 宿主不需要（<see cref="Snapshot"/> 读取时已自动刷新）。
    /// </summary>
    internal bool TryUpdate() => _state.TryUpdate();
}
