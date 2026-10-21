using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-001 步骤 0：上行输入链路的<b>行为锁</b>。
/// <para>
/// 锁的是 <see cref="AgentJsonlProtocol.RunLoopAsync"/> 对一条 input 帧的完整处理：
/// 状态字段写进 <see cref="IInputHost"/> 的<b>顺序</b>与能力门控、状态类帧不派发/不产帧、
/// 带 value 的帧派发并产回音，以及「HTTP 结构化入口」与「预构建命令帧入口」对同一组字段
/// 得出一致状态。后续把 wire 转换收口成 <c>InputFrame</c> 时，本文件必须保持逐项绿。
/// </para>
/// </summary>
public sealed class InputRoutingBehaviorLockTests
{
    private const InputCapability AllCapabilities =
        InputCapability.KeyboardState | InputCapability.Hover | InputCapability.Geometry | InputCapability.Focus;

    /// <summary>最小可用宿主：WaitInput 停稳、无真实脚本；只记录派发。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;
        public DefaultInputHost Host { get; }
        public ScriptedSessionIO IO { get; } = new();
        public RecordingProtocol Protocol { get; }

        public Harness(InputCapability capabilities)
        {
            _scope = GlobalStatic.OpenScope(new ConfigData());
            Host = new DefaultInputHost(capabilities);
            var ui = new HeadlessConsole(Host);
            var console = new EmueraConsole(ui, new NullTerminalSetup());
            console._state.State = ConsoleState.WaitInput;
            console._state.inputReq = null;
            Protocol = new RecordingProtocol(console, ui, IO, new DisplayState(console, "MS Gothic"));
        }

        /// <summary>把线路逐条喂给协议循环；输入通道关闭即自然退出。</summary>
        public async Task RunAsync(params string[] lines)
        {
            foreach (var line in lines)
                IO.Feed(line);
            IO.Complete();
            await Protocol.RunLoopAsync(enableTimeout: false, CancellationToken.None);
        }

        public void Dispose() => _scope.Dispose();
    }

    [Fact]
    public async Task State_only_frame_writes_host_but_does_not_dispatch_or_emit_turn()
    {
        using var h = new Harness(AllCapabilities);

        await h.RunAsync(
            "{\"type\":\"input\",\"hoverValue\":\"42\",\"keys\":[{\"code\":65,\"down\":true}],\"geometry\":{\"x\":7,\"y\":9}}");

        Assert.Empty(h.Protocol.Dispatched);
        Assert.Single(h.IO.Output);   // 只有首帧，没有回音
        Assert.Equal("42", h.Host.Current.HoverValue);
        Assert.True(h.Host.Current.Keys.IsDown(65));
        Assert.Equal(new EmuPoint(7, 9), h.Host.Current.CursorPosition);
    }

    [Fact]
    public async Task Value_frame_dispatches_once_and_emits_one_echo_turn()
    {
        using var h = new Harness(AllCapabilities);

        await h.RunAsync("{\"type\":\"input\",\"value\":\"1\",\"hoverValue\":\"42\"}");

        Assert.Equal(new[] { "1" }, h.Protocol.Dispatched);
        Assert.Equal(2, h.IO.Output.Count);   // 首帧 + 一次回音
        Assert.Equal("42", h.Host.Current.HoverValue);
    }

    /// <summary>
    /// 状态字段先写、再派发：同一帧里 value 与状态并存时，脚本本轮读到的必须是新状态。
    /// 这里用「派发时快照已更新」把顺序钉住。
    /// </summary>
    [Fact]
    public async Task Host_state_is_written_before_dispatch()
    {
        using var h = new Harness(AllCapabilities);

        await h.RunAsync("{\"type\":\"input\",\"value\":\"1\",\"hoverValue\":\"42\",\"keys\":[{\"code\":65,\"down\":true}]}");

        var snapshotAtDispatch = Assert.Single(h.Protocol.SnapshotsAtDispatch);
        Assert.Equal("42", snapshotAtDispatch.HoverValue);
        Assert.True(snapshotAtDispatch.Keys.IsDown(65));
    }

    [Fact]
    public async Task Capability_gating_drops_fields_the_host_did_not_declare()
    {
        using var h = new Harness(InputCapability.KeyboardState);

        await h.RunAsync(
            "{\"type\":\"input\",\"hoverValue\":\"42\",\"geometry\":{\"x\":7,\"y\":9},\"focus\":false,\"keys\":[{\"code\":65,\"down\":true}]}");

        Assert.Null(h.Host.Current.HoverValue);
        Assert.Null(h.Host.Current.CursorPosition);
        Assert.True(h.Host.Current.Focused);   // 未声明 Focus ⇒ 保持默认 true
        Assert.True(h.Host.Current.Keys.IsDown(65));
    }

    /// <summary>
    /// 一帧内 focus 必须<b>先于</b> keys 写入：失焦清键后，本帧的按下才不会被一起清掉。
    /// </summary>
    [Fact]
    public async Task Focus_is_applied_before_keys_in_the_same_frame()
    {
        using var h = new Harness(AllCapabilities);

        await h.RunAsync("{\"type\":\"input\",\"focus\":false,\"keys\":[{\"code\":65,\"down\":true}]}");

        Assert.False(h.Host.Current.Focused);
        Assert.True(h.Host.Current.Keys.IsDown(65));
        Assert.Empty(h.Protocol.Dispatched);
        Assert.Single(h.IO.Output);
    }

    /// <summary>失焦帧清空上一帧的键电平（幽灵按键防线）。</summary>
    [Fact]
    public async Task Focus_loss_clears_previous_key_levels()
    {
        using var h = new Harness(AllCapabilities);

        await h.RunAsync(
            "{\"type\":\"input\",\"keys\":[{\"code\":65,\"down\":true}]}",
            "{\"type\":\"input\",\"focus\":false}");

        Assert.False(h.Host.Current.Focused);
        Assert.False(h.Host.Current.Keys.IsDown(65));
        Assert.Empty(h.Protocol.Dispatched);
        Assert.Single(h.IO.Output);
    }

    /// <summary>
    /// issue-002：server 的接线口径 —— <see cref="InputPort"/> 先写状态，value 经
    /// <c>BuildInputJsonl</c> 入队，协议循环读回后派发并产回音。门面与循环之间不出现第二份状态翻译。
    /// </summary>
    [Fact]
    public async Task Port_value_submission_reaches_the_loop_and_emits_echo_turn()
    {
        using var h = new Harness(AllCapabilities);
        var port = new InputPort(h.Host, value => h.IO.Feed(GameServerProtocol.BuildInputJsonl(value)));

        port.Submit(new InputFrame(Value: "1", HoverValue: "42"));
        h.IO.Complete();
        await h.Protocol.RunLoopAsync(false, CancellationToken.None);

        Assert.Equal(new[] { "1" }, h.Protocol.Dispatched);
        Assert.Equal(2, h.IO.Output.Count);   // 首帧 + 一次回音
        Assert.Equal("42", h.Host.Current.HoverValue);
        Assert.Equal("42", Assert.Single(h.Protocol.SnapshotsAtDispatch).HoverValue);
    }

    [Fact]
    public async Task Port_state_only_submission_writes_state_without_turn()
    {
        using var h = new Harness(AllCapabilities);
        var port = new InputPort(h.Host, value => h.IO.Feed(GameServerProtocol.BuildInputJsonl(value)));

        port.Submit(new InputFrame(
            HoverValue: "42",
            Keys: new[] { new InputKeyEvent(65, true) },
            Geometry: new InputGeometry(7, 9)));
        h.IO.Complete();
        await h.Protocol.RunLoopAsync(false, CancellationToken.None);

        Assert.Empty(h.Protocol.Dispatched);
        Assert.Single(h.IO.Output);   // 只有首帧，没有回音
        Assert.Equal("42", h.Host.Current.HoverValue);
        Assert.True(h.Host.Current.Keys.IsDown(65));
        Assert.Equal(new EmuPoint(7, 9), h.Host.Current.CursorPosition);
    }

    /// <summary>
    /// 两条 HTTP 入口的帧形状：结构化字段序列化出的命令帧，与客户端预构建的命令帧，
    /// 进入同一条循环后必须得到逐项一致的状态。
    /// </summary>
    [Fact]
    public async Task Structured_and_prebuilt_command_frames_apply_identically()
    {
        var keys = new[] { new InputKeyEvent(65, true) };
        var structured = JsonSerializer.Serialize(
            new JsonlCommand("input", "1", "42", keys, new InputGeometry(3, 4), false),
            EmueraJsonContext.Default.JsonlCommand);
        const string prebuilt =
            "{\"type\":\"input\",\"value\":\"1\",\"hoverValue\":\"42\",\"keys\":[{\"code\":65,\"down\":true}],\"geometry\":{\"x\":3,\"y\":4},\"focus\":false}";

        InputSnapshot structuredSnapshot;
        List<string> structuredDispatched;
        int structuredTurns;
        using (var h = new Harness(AllCapabilities))
        {
            await h.RunAsync(structured);
            structuredSnapshot = h.Host.Current;
            structuredDispatched = new List<string>(h.Protocol.Dispatched);
            structuredTurns = h.IO.Output.Count;
        }

        using (var h = new Harness(AllCapabilities))
        {
            await h.RunAsync(prebuilt);

            Assert.Equal(structuredSnapshot.HoverValue, h.Host.Current.HoverValue);
            Assert.Equal(structuredSnapshot.CursorPosition, h.Host.Current.CursorPosition);
            Assert.Equal(structuredSnapshot.Focused, h.Host.Current.Focused);
            Assert.Equal(structuredSnapshot.Keys.IsDown(65), h.Host.Current.Keys.IsDown(65));
            Assert.Equal(structuredDispatched, h.Protocol.Dispatched);
            Assert.Equal(structuredTurns, h.IO.Output.Count);
        }
    }

    /// <summary>记录派发值与派发瞬间的宿主快照；不执行脚本。</summary>
    private sealed class RecordingProtocol : AgentJsonlProtocol
    {
        public List<string> Dispatched { get; } = new();
        public List<InputSnapshot> SnapshotsAtDispatch { get; } = new();

        public RecordingProtocol(EmueraConsole console, IConsoleUI ui, SessionIO io, DisplayState displayState)
            : base(console, ui, io, displayState)
        {
        }

        protected override void DispatchInput(string input)
        {
            Dispatched.Add(input);
            SnapshotsAtDispatch.Add(ui.Input.Current);
        }
    }

    private sealed class ScriptedSessionIO : SessionIO
    {
        private readonly Channel<string> _input = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public List<string> Output { get; } = new();

        public void Feed(string line) => _input.Writer.TryWrite(line);

        public void Complete() => _input.Writer.TryComplete();

        public override async Task<string?> ReadLineAsync(CancellationToken ct)
        {
            try
            {
                return await _input.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override void WriteLine(Turn turn) => Output.Add(turn.Wire);
        public override void Close() => _input.Writer.TryComplete();
        public override bool IsConnected => true;
    }

    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
