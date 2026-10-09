namespace MinorShift.Emuera.GameView;

/// <summary>
/// CLI 宿主与引擎之间的窄接口：CLI 主循环（<see cref="CliGameLoop"/>）拿不到 <c>EmueraConsole</c>，
/// 只经本接口触达引擎的等待状态与两条续跑路径（超时 / 让路）。
/// </summary>
internal interface IInputTimer
{
    long InputTimelimit { get; }
    bool IsWaitingInput { get; }
    string? TimeUpMessage { get; }

    /// <summary>
    /// 让路点判据（spec v5 §5.1b W1-6 / §3.2 ④）：
    /// <c>State=WaitInput</c> + <see cref="InputType.Void"/> + 无时长（<c>Timelimit&lt;=0</c>）。
    ///
    /// <para>这正是无参 <c>AWAIT</c> / <c>AWAIT 0</c>（零时长让路点）在引擎里的形态。
    /// CLI 的「交付一帧」与 server 的 drain 帧同源，CLI 主循环据此在<b>超时处理之后、读取终端输入之前</b>
    /// 做同步 drain —— 缺了它，无参 <c>AWAIT</c> 改挂起后 CLI 没有任何续跑入口，必然停住。</para>
    /// </summary>
    bool IsYieldPoint { get; }

    void SubmitTimeout();
    void ClearInputBuffer();

    /// <summary>
    /// 让路点续跑（spec v5 §5.1b W1-7）：转发到引擎 <c>EmueraConsole.EndYield</c>，
    /// 与 <see cref="SubmitTimeout"/> 的唯一差别是不置 <c>isTimeout</c>、不打印 <c>TimeUpMes</c>
    /// —— 本次等待没有「超时」语义（<c>ISTIMEOUT</c> / <c>IsRunningTimer</c> 不可被污染）。
    /// CLI 侧的存活条件：只能在 <see cref="IsYieldPoint"/> 为真时调用。
    /// </summary>
    void EndYield();
}
