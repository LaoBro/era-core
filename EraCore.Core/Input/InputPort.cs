using System;

namespace MinorShift.Emuera.Input;

/// <summary>
/// 输入轴的宿主门面（issue-002 / spec §3.2）——宿主只经本类与引擎交互。
/// <para>
/// <b>能力位在构造时声明</b>，逐帧不携带（spec 决策 6）：未声明的通道在门面处即被丢弃，
/// 不会在快照里留下无人读取的状态。
/// </para>
/// <para>
/// <b>一帧覆盖两类输入</b>：
/// <list type="bullet">
/// <item><b>提交类</b>（<see cref="InputFrame.Value"/> 非 null）：先按
/// focus → geometry → hover → keys 写入状态，再经组装期注入的派发通道推进游戏循环。</item>
/// <item><b>状态类</b>（value 缺省）：只写状态，不派发、不产帧。</item>
/// </list>
/// 「先写状态、再派发」是行为不变量：脚本本轮读到的必须是本帧携带的新状态。
/// </para>
/// <para>
/// <b>构造权留在 Core</b>：宿主从组装根（CLI 协议 / 会话）拿到实例，拿不到内部输入主机、
/// 状态应用器或空实现。派发通道按宿主线程模型注入（CLI 同步派发、server 入队给游戏循环线程）。
/// </para>
/// </summary>
public sealed class InputPort
{
    private readonly IInputHost _host;
    private readonly Action<string> _dispatchValue;

    internal InputPort(IInputHost host, Action<string> dispatchValue)
    {
        _host = host;
        _dispatchValue = dispatchValue;
    }

    /// <summary>构造期声明的能力位；运行期不变。</summary>
    public InputCapabilities Capabilities => _host.Capabilities;

    /// <summary>
    /// 提交一帧输入：状态字段先写宿主（能力门控与顺序由 <see cref="InputStateApplier"/> 单点实现），
    /// 带 value 时再派发一次。
    /// </summary>
    public void Submit(InputFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        InputStateApplier.Apply(frame, _host);

        if (frame.HasValue)
            _dispatchValue(frame.Value!);
    }

    /// <summary>清空键电平与触发读取位置；能力声明保持不变（会话重建 / 焦点丢失时调用）。</summary>
    public void Reset() => _host.ResetState();
}
