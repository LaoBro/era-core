using System;
using MinorShift.Emuera.Output;

namespace EraCore.Hosting;

/// <summary>宿主输入 UI 的模式（由 <c>awaiting.input.type</c> 派生，不读引擎内部状态）。</summary>
public enum HostInputMode
{
    None,
    Text,
    AnyKey,
    EnterKey,
    ButtonOnly,
    Ended,
}

/// <summary>
/// <c>awaiting</c> → 输入界面状态机的可复用结果。
///
/// <para>覆盖：输入框是否出现、是否禁止空提交、倒计时/超时文案、终态、按钮是否仍有效。
/// 不包含布局/Avalonia 控件；Native M2 直接消费这些字段。</para>
/// </summary>
public sealed record AwaitingView(
    HostInputMode Mode,
    string? InputType,
    bool IsWaitingForInput,
    bool RequiresValue,
    bool AllowsEmpty,
    bool IsTerminal,
    string? Terminal,
    long? WaitDurationMs,
    bool DisplayCountdown,
    string? TimeUpMessage,
    bool TimedOut,
    long? RemainingMs)
{
    /// <summary>
    /// 从 <see cref="AwaitInfo"/> 构造。倒计时需要调用方提供等待起点（宿主收到该 wait 的时间）
    /// 与当前时间；缺失时 <see cref="RemainingMs"/> 为 null。
    /// </summary>
    public static AwaitingView From(
        AwaitInfo awaiting,
        DateTimeOffset? waitStartedAt = null,
        DateTimeOffset? now = null,
        string? defaultTimeUpMessage = null)
    {
        ArgumentNullException.ThrowIfNull(awaiting);

        if (awaiting.ended is { } ended)
        {
            return new AwaitingView(
                HostInputMode.Ended, null, false, false, true, true,
                ended.terminal, null, false, null, false, null);
        }

        var inputType = awaiting.input?.type;
        var mode = inputType switch
        {
            "IntValue" or "StrValue" => HostInputMode.Text,
            "AnyValue" => HostInputMode.Text,
            "AnyKey" => HostInputMode.AnyKey,
            "EnterKey" => HostInputMode.EnterKey,
            null => HostInputMode.None,
            _ => HostInputMode.ButtonOnly,
        };
        var requiresValue = inputType is "IntValue" or "StrValue";
        var allowsEmpty = !requiresValue;

        var wait = awaiting.wait;
        var duration = wait?.durationMs;
        var displayCountdown = duration is > 0 && wait?.displayTime == true;
        var timedOut = wait?.timedOut == true;
        var timeUpMessage = timedOut ? (wait?.timeUpMessage ?? defaultTimeUpMessage) : null;

        long? remaining = null;
        if (duration is > 0 && waitStartedAt is { } started)
        {
            var elapsed = ((now ?? DateTimeOffset.UtcNow) - started).TotalMilliseconds;
            remaining = Math.Max(0, (long)(duration.Value - elapsed));
        }

        return new AwaitingView(
            mode,
            inputType,
            inputType is not null,
            requiresValue,
            allowsEmpty,
            false,
            null,
            duration,
            displayCountdown,
            timeUpMessage,
            timedOut,
            remaining);
    }

    /// <summary>
    /// 按钮是否可以提交其 value：必须正在等输入、不是 EnterKey/AnyKey 惰性态，
    /// 且按钮 generation 与当前回合一致（与 Web `inputRouting.isLiveButton` 同义）。
    /// </summary>
    public bool CanSubmitButton(ButtonRef button, long currentGeneration)
    {
        ArgumentNullException.ThrowIfNull(button);
        return CanSubmitButton(button.generation, currentGeneration);
    }

    /// <summary>按 generation 判定（供已投影为宿主自有按钮类型的调用方使用）。</summary>
    public bool CanSubmitButton(long buttonGeneration, long currentGeneration)
    {
        if (!IsWaitingForInput || Mode is HostInputMode.AnyKey or HostInputMode.EnterKey or HostInputMode.Ended)
            return false;
        return buttonGeneration == currentGeneration;
    }

    /// <summary>EnterKey/AnyKey 的“点击任意位置推进”语义。</summary>
    public bool AdvancesOnAnyClick => Mode is HostInputMode.AnyKey or HostInputMode.EnterKey;
}
