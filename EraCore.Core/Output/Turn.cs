namespace MinorShift.Emuera.Output;

/// <summary>
/// 回合信封（issue-003）：类型化记录 + <b>那一次</b>序列化结果。
///
/// <para>
/// <b>为什么两样都带</b>：想读结构的宿主消费 <see cref="Record"/>；原样转发的宿主消费
/// <see cref="Wire"/>（HTTP 长轮询、WS、MAUI 桥）。二者来自同一实例，因此广播与长轮询拿到的是
/// 同一份字节——不变量 O-4（同序、同实例）由类型本身承载，而不是靠两条路径各自小心序列化。
/// </para>
///
/// <para>
/// 🔒 <b>构造权留在 Core</b>：外部宿主只读不造。帧形状由 <see cref="TurnFrames"/> 的单一
/// 序列化路径产出；任何“拿到 Record 再自己序列化一次”的写法都会绕过本类型的同一性保证。
/// </para>
/// </summary>
internal sealed class Turn
{
    internal Turn(TurnRecord record, string wire)
    {
        Record = record;
        Wire = wire;
    }

    /// <summary>类型化回合记录（协议 v12 形状）。</summary>
    internal TurnRecord Record { get; }

    /// <summary>与 <see cref="Record"/> 对应的序列化结果；转发的唯一读取面。</summary>
    internal string Wire { get; }
}
