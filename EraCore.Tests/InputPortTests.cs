using System.Collections.Generic;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Primitives;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-002 步骤 0：<see cref="InputPort"/> 的行为锁。
/// <para>
/// 门面必须同时覆盖两类帧：带 value 的提交类（先写状态、再派发一次）与只带状态的帧
/// （只写宿主、不派发）。能力位在构造期声明，未声明通道的字段在门面处即被丢弃；
/// <see cref="InputPort.Reset"/> 清键电平与触发状态，但不清能力声明。
/// </para>
/// <para>
/// 用例体只通过公共类型（<see cref="InputPort"/> / <see cref="InputFrame"/> /
/// <see cref="InputKeyEvent"/> / <see cref="InputGeometry"/>）与引擎交互；构造门面留在 Core
/// （宿主拿不到内部输入主机），测试经 InternalsVisibleTo 装配。
/// </para>
/// </summary>
public sealed class InputPortTests
{
    private const InputCapability AllCapabilities =
        InputCapability.KeyboardState | InputCapability.Hover | InputCapability.Geometry | InputCapability.Focus;

    [Fact]
    public void Value_frame_writes_state_before_dispatching_once()
    {
        var host = new DefaultInputHost(AllCapabilities);
        var dispatched = new List<string>();
        InputSnapshot? snapshotAtDispatch = null;
        var port = new InputPort(host, value =>
        {
            dispatched.Add(value);
            snapshotAtDispatch = host.Current;
        });

        port.Submit(new InputFrame(
            Value: "1",
            HoverValue: "42",
            Keys: new[] { new InputKeyEvent(65, true) },
            Geometry: new InputGeometry(3, 4)));

        Assert.Equal(new[] { "1" }, dispatched);
        Assert.Equal("42", snapshotAtDispatch!.HoverValue);
        Assert.True(snapshotAtDispatch.Keys.IsDown(65));
        Assert.Equal(new EmuPoint(3, 4), snapshotAtDispatch.CursorPosition);
    }

    [Fact]
    public void State_only_frame_does_not_dispatch()
    {
        var host = new DefaultInputHost(AllCapabilities);
        var dispatched = new List<string>();
        var port = new InputPort(host, dispatched.Add);

        port.Submit(new InputFrame(HoverValue: "42", Keys: new[] { new InputKeyEvent(65, true) }));

        Assert.Empty(dispatched);
        Assert.Equal("42", host.Current.HoverValue);
        Assert.True(host.Current.Keys.IsDown(65));
    }

    [Fact]
    public void Undeclared_capabilities_are_dropped_at_the_facade()
    {
        var host = new DefaultInputHost(InputCapability.KeyboardState);
        var port = new InputPort(host, _ => { });

        Assert.Equal(InputCapability.KeyboardState, port.Capabilities.Flags);

        port.Submit(new InputFrame(
            HoverValue: "42",
            Keys: new[] { new InputKeyEvent(65, true) },
            Geometry: new InputGeometry(7, 9),
            Focus: false));

        Assert.Null(host.Current.HoverValue);
        Assert.Null(host.Current.CursorPosition);
        Assert.True(host.Current.Focused);   // 未声明 Focus ⇒ 保持默认 true
        Assert.True(host.Current.Keys.IsDown(65));
    }

    [Fact]
    public void Reset_clears_key_levels_and_trigger_state_but_keeps_capabilities()
    {
        var host = new DefaultInputHost(AllCapabilities);
        var port = new InputPort(host, _ => { });

        port.Submit(new InputFrame(Keys: new[] { new InputKeyEvent(65, true) }));
        Assert.True(host.ConsumeTrigger(65));

        port.Reset();

        Assert.Same(InputSnapshot.Empty, host.Current);
        Assert.False(host.Current.Keys.IsDown(65));
        Assert.Equal(AllCapabilities, port.Capabilities.Flags);

        // 触发状态也必须归零：重置后再次按下要能重新触发（残留 _lastRead 会吞掉这一次）
        port.Submit(new InputFrame(Keys: new[] { new InputKeyEvent(65, true) }));
        Assert.True(host.ConsumeTrigger(65));
    }
}
