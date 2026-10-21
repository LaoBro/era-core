using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;

namespace MinorShift.Emuera.Output;

/// <summary>
/// v12 的唯一契约字段：<b>服务端此刻在等什么</b>（spec v5 §1 / §5.4）。
///
/// <para>结构是「两个<b>正交维度</b> + 一个<b>互斥终态键</b>」，全部由
/// <see cref="Of"/> 单一工厂产出，故互斥/共存不变式只在一处保证：</para>
/// <list type="table">
/// <item><term><c>input</c></term><description>是否期待客户端发输入（有 <c>type</c>）</description></item>
/// <item><term><c>wait</c></term><description>是否有一段<b>有上界</b>的等待（有 <c>durationMs</c>）</description></item>
/// <item><term><c>ended</c></term><description>终态；出现时前两者必不出现</description></item>
/// </list>
///
/// <para>🔒 <b>硬规则</b>（§1.2 规则 1）：<c>ended</c> 未出现时，<c>input</c> 与 <c>wait</c>
/// 至少其一必现 —— 两者都空意味着这个等待「既不等输入也不等定时器」，
/// 没有任何东西能推进它（服务端挂死）。</para>
///
/// <para>🔒 <b><c>wait</c> 的三态</b>（§1.2 规则 5，写死：不得再加第四个取值）：</para>
/// <list type="table">
/// <item><term><c>wait</c> 缺省</term><description><b>无限期</b>：<c>Timelimit &lt; 0</c>（<see cref="InputRequest.Timelimit"/> 默认 -1），没有定时器</description></item>
/// <item><term><c>durationMs == 0</c></term><description><b>零时长让路点</b>（无参 <c>AWAIT</c> / <c>AWAIT 0</c>）：语义是「下一帧立刻到」</description></item>
/// <item><term><c>durationMs &gt; 0</c></term><description><b>定时等待</b>：带倒计时元数据；<c>interruptible</c> 为真时给「跳过」affordance</description></item>
/// </list>
///
/// <para><b>为什么不用 .NET 多态</b>（<c>JsonDerivedType</c>）：会撞上 <c>EmueraJsonContext</c>
/// 无 Converters 的既有坑，而收益为零 —— wire 上就是一个普通对象带三个可空子对象。</para>
///
/// <para><b>不上 wire 的内部标记</b>：<c>InputRequest.IsYield</c> 只是服务端让路识别（drain）的判据，
/// 永不序列化；客户端在意的是「下一帧立刻到」（<c>durationMs:0</c>），不是那个旗标叫什么。</para>
///
/// <para>公开只读：宿主可读三个维度，但构造权留在 Core（无公开构造器与公开工厂）。</para>
/// </summary>
public sealed record AwaitInfo
{
    [System.Text.Json.Serialization.JsonConstructor]
    internal AwaitInfo()
    {
    }

    /// <summary>是否期待客户端发输入（<c>InputType != Void</c> 时出现）。</summary>
    public AwaitInput? input { get; internal init; }

    /// <summary>是否有一段有上界的等待（<c>Timelimit &gt;= 0</c> 时出现）。三态见类型注释。</summary>
    public AwaitWait? wait { get; internal init; }

    /// <summary>终态（<c>Quit</c> / <c>Error</c>）。出现时 <see cref="input"/> / <see cref="wait"/> 必为 null。</summary>
    public AwaitEnded? ended { get; internal init; }

    /// <summary>
    /// 单一工厂（§2.2 的两条维度判据）：只看两个问题 ——
    /// <b>「这次等待期待输入吗」</b>（<c>inputType != Void</c>）与
    /// <b>「这次等待有上界吗」</b>（<c>Timelimit &gt;= 0</c>）。
    ///
    /// <para>判据<b>不再四选一</b>：<c>input</c> 与 <c>wait</c> 各自独立判定，
    /// 于是 <c>TINPUT</c>（既等输入又有期限）第一次能被表达。</para>
    ///
    /// <para><paramref name="timedOut"/> 是服务端的一次性旗标（B3 超时续跑），
    /// 只挂在 <c>wait</c> 上且仅在 <c>Timelimit &gt; 0</c> 时出现（§1.1 ②）。</para>
    ///
    /// <para>入参是引擎内部状态（<see cref="InputRequest"/> / <see cref="ConsoleState"/>），
    /// 故工厂保持 internal——构造权留在 Core。</para>
    /// </summary>
    internal static AwaitInfo Of(InputRequest? req, ConsoleState state, bool timedOut)
    {
        // 终态：input / wait 保持 null（互斥键先行，杜绝「Quit 帧还带 inputType」的残留形态）。
        if (state is ConsoleState.Quit or ConsoleState.Error)
            return new AwaitInfo { ended = new AwaitEnded(state.ToString()) };

        var info = new AwaitInfo
        {
            input = req is { } r && r.InputType != InputType.Void
                ? new AwaitInput(r.InputType.ToString())
                : null,
            wait = req is { Timelimit: >= 0 } w
                ? new AwaitWait(w.Timelimit)
                {
                    // 🔴 issue 10 撤回：这里曾有一个 `interruptible = w.Skippable ? true : null`。
                    // 它把「引擎替玩家结束定时等待」这套 era-core 自创语义搬上了 wire，前端据此
                    // 渲染「跳过」按钮 —— 实测在 eraTW @BADAPPLE 上逐帧闪烁。参考实现无此语义
                    // （spec §10.1）⇒ 字段与 wire 投影一并撤回，wire 上不再有任何「可打断」标记。
                    //
                    // displayTime / timedOut 仅在 true 时序列化（WhenWritingNull ⇒ 缺省即 false）。
                    displayTime = w.Timelimit > 0 && w.DisplayTime ? true : null,
                    // ⚠️ 空串与非空一样要「有值才出现」：WhenWritingNull 只抑制 null，
                    // 不归一的话 TINPUT 未设 TimeUpMes（默认 null! 但 ERB 常置 ""）时会写出
                    // `"timeUpMessage":""`，前端据此会显示空文案而非默认提示。旧顶层字段走
                    // IsNullOrEmpty 归一，容器化时必须保留同一条归一。
                    timeUpMessage = w.Timelimit > 0 && !string.IsNullOrEmpty(w.TimeUpMes) ? w.TimeUpMes : null,
                    timedOut = w.Timelimit > 0 && timedOut ? true : null,
                }
                : null,
        };

        // 🔒 §1.2 规则 1：非终态时两者至少其一必现（都空 ⇒ 这个等待没有任何东西能推进它）。
        // 该不变式由验收闸住（TurnAwaitingV12Tests.AssertContract 对<b>每一帧</b>断言），
        // 不在此处 Debug.Assert：那会把「非法等待」变成测试宿主的进程级失败，
        // 而它是 wire 契约问题，应当由契约测试报告（spec §5.4 同样只留了注释）。
        return info;
    }
}

/// <summary>「是否期待客户端发输入」维度（spec §2.2）。</summary>
public sealed record AwaitInput
{
    [System.Text.Json.Serialization.JsonConstructor]
    internal AwaitInput(string type) => this.type = type;

    public string type { get; }
}

/// <summary>
/// 「是否有一段有上界的等待」维度（spec §2.2 / §1.2 规则 5）。
/// <paramref name="durationMs"/> 的三态 = wait 的三态（缺省 = 无 wait 对象）。
/// </summary>
public sealed record AwaitWait
{
    [System.Text.Json.Serialization.JsonConstructor]
    internal AwaitWait(long durationMs) => this.durationMs = durationMs;

    public long durationMs { get; }

    /// <summary>是否显示倒计时（仅 true 时出现；ERB 可用 <c>TINPUT t,d,0</c> 要求不显示）。</summary>
    public bool? displayTime { get; internal init; }

    /// <summary>超时文案（仅 <c>durationMs &gt; 0</c> 时可能携带）。</summary>
    public string? timeUpMessage { get; internal init; }

    /// <summary>本帧是超时续跑驱动（仅 true 时出现；一次性，挂在 wait 上而不是顶层）。</summary>
    public bool? timedOut { get; internal init; }
}

/// <summary>终态键（与 <see cref="AwaitInfo.input"/> / <see cref="AwaitInfo.wait"/> 互斥）。</summary>
public sealed record AwaitEnded
{
    [System.Text.Json.Serialization.JsonConstructor]
    internal AwaitEnded(string terminal) => this.terminal = terminal;

    public string terminal { get; }
}
