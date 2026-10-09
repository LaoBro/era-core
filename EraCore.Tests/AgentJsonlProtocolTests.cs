using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0016 落地单测：验证 AgentJsonlProtocol.BuildTurn 填充 v6 timer 字段。
/// - TINPUT 期间 turn JSON 含 timeLimit / displayTime / timeUpMessage / timedOut=false
/// - 非 TINPUT 期间三 nullable 字段 WhenWritingNull 不出现，timedOut=false 仍出现
/// - SubmitTimeoutAsync 路径置 _pendingTimeoutFlag=true → turn.timedOut=true
///
/// 不走真实游戏脚本——直接操纵 _console._state 的 State/inputReq 字段，
/// 让 SubmitTimeout 在条件不满足时直接 return（避免触发 EndTimerCore 的 RunEmueraProgram）。
/// BuildTurn 经 GetInitialTurnAsync 或 SubmitTimeoutAsync 间接调用。
/// </summary>
public class AgentJsonlProtocolTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _displayState;
    private readonly AgentJsonlProtocol _protocol;
    private readonly FakeSessionIO _io;

    public AgentJsonlProtocolTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        var ui = new HeadlessConsole();
        _console = new EmueraConsole(ui, new NullTerminalSetup());
        _displayState = new DisplayState(_console, "MS Gothic");
        _io = new FakeSessionIO();
        _protocol = new AgentJsonlProtocol(_console, ui, _io, _displayState);
    }

    public void Dispose() => _scope.Dispose();

    /// <summary>设置 _console._state 的 State 与 inputReq，绕过 Process 脚本路径。</summary>
    private void SetWaitInput(InputRequest? req)
    {
        _console._state.State = ConsoleState.WaitInput;
        _console._state.inputReq = req;
    }

    private static InputRequest TinputReq(long timelimit, bool displayTime, string? timeUpMes = null) =>
        new()
        {
            InputType = InputType.EnterKey,
            Timelimit = timelimit,
            DisplayTime = displayTime,
            TimeUpMes = timeUpMes ?? "",
        };

    // ---------- T_buildturn_tinput_populates_timer_fields ----------

    [Fact]
    public async Task T_buildturn_tinput_populates_timer_fields()
    {
        SetWaitInput(TinputReq(timelimit: 5000, displayTime: true, timeUpMes: "时间到"));

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.Equal(12, root.GetProperty("protocolVersion").GetInt32()); // v12：awaiting 容器

        // v12：七个旧顶层字段全部退场——顶层不得再出现它们。
        Assert.False(root.TryGetProperty("state", out _), "v12 起顶层 state 不得再出现");
        Assert.False(root.TryGetProperty("inputType", out _), "v12 起顶层 inputType 不得再出现");
        Assert.False(root.TryGetProperty("needValue", out _), "v12 起顶层 needValue 不得再出现");
        Assert.False(root.TryGetProperty("timeLimit", out _), "v12 起顶层 timeLimit 不得再出现");
        Assert.False(root.TryGetProperty("displayTime", out _), "v12 起顶层 displayTime 不得再出现");
        Assert.False(root.TryGetProperty("timeUpMessage", out _), "v12 起顶层 timeUpMessage 不得再出现");
        Assert.False(root.TryGetProperty("timedOut", out _), "v12 起顶层 timedOut 不得再出现");

        // TINPUT 的计时元数据只挂在 awaiting.wait 上。
        var awaiting = root.GetProperty("awaiting");
        Assert.Equal("EnterKey", awaiting.GetProperty("input").GetProperty("type").GetString());
        var wait = awaiting.GetProperty("wait");
        Assert.Equal(5000L, wait.GetProperty("durationMs").GetInt64());
        Assert.True(wait.GetProperty("displayTime").GetBoolean());
        Assert.Equal("时间到", wait.GetProperty("timeUpMessage").GetString());
        // 尚未超时 ⇒ wait.timedOut 键缺省（不是 false）。
        Assert.False(wait.TryGetProperty("timedOut", out _));
    }

    // ---------- T_buildturn_non_tinput_has_no_wait ----------

    [Fact]
    public async Task T_buildturn_non_tinput_has_no_wait()
    {
        // inputReq=null：非 TINPUT 期间，两个正交维度皆无（`{}`）；顶层不再有任何 time* 键。
        SetWaitInput(req: null);

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("timeLimit", out _), "顶层 timeLimit 已退场");
        Assert.False(root.TryGetProperty("displayTime", out _), "顶层 displayTime 已退场");
        Assert.False(root.TryGetProperty("timeUpMessage", out _), "顶层 timeUpMessage 已退场");
        Assert.False(root.TryGetProperty("timedOut", out _), "顶层 timedOut 已退场");

        var awaiting = root.GetProperty("awaiting");
        Assert.False(awaiting.TryGetProperty("input", out _));
        Assert.False(awaiting.TryGetProperty("wait", out _));
    }

    // ---------- T_buildturn_tinput_without_displaytime_omits_displaytime ----------

    [Fact]
    public async Task T_buildturn_tinput_without_displaytime_omits_displaytime_field()
    {
        // Timelimit>0 但 DisplayTime=false：ERB 脚本要求前端不显示倒计时
        // durationMs 仍出现，displayTime 键缺省（false→null）
        // TimeUpMes 空字符串 → timeUpMessage 键缺省
        SetWaitInput(TinputReq(timelimit: 3000, displayTime: false, timeUpMes: ""));

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var wait = doc.RootElement.GetProperty("awaiting").GetProperty("wait");

        Assert.Equal(3000L, wait.GetProperty("durationMs").GetInt64());
        Assert.False(wait.TryGetProperty("displayTime", out _),
            "DisplayTime=false 时 displayTime 键缺省（WhenWritingNull）");
        Assert.False(wait.TryGetProperty("timeUpMessage", out _),
            "TimeUpMes 空字符串时 timeUpMessage 键缺省");
    }

    // ---------- T_buildturn_negative_timelimit_has_no_wait ----------

    [Fact]
    public async Task T_buildturn_negative_timelimit_has_no_wait()
    {
        // Timelimit=-1（InputRequest 默认值）：等同于"无 TINPUT" → wait 键缺省（= 无限期）
        SetWaitInput(TinputReq(timelimit: -1, displayTime: true, timeUpMes: "x"));

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("timeLimit", out _), "顶层 timeLimit 已退场");
        Assert.False(root.TryGetProperty("displayTime", out _), "顶层 displayTime 已退场");
        Assert.False(root.TryGetProperty("timeUpMessage", out _), "顶层 timeUpMessage 已退场");
        Assert.False(root.GetProperty("awaiting").TryGetProperty("wait", out _),
            "Timelimit=-1 时 wait 键缺省");
    }

    // ---------- T_submit_timeout_flag_is_one_shot ----------

    [Fact]
    public async Task T_submit_timeout_leaves_no_top_level_timed_out()
    {
        // v12：timedOut 只挂在 awaiting.wait 上（一次性、仅 Timelimit>0 时出现），顶层不再有该键。
        // 本用例绕开 EndTimerCore（会 RunEmueraProgram 卡住）：
        //   - State=Quit → WaitForInputAsync 立即返回 true
        //   - inputReq=null → SubmitTimeout 内部 guard 直接 return（不调 EndTimerCore）
        // 于是本帧是终态帧：`_pendingTimeoutFlag` 无处可落，只验证「顶层无 timedOut 痕迹 +
        // 旗标一次性」。真实超时续跑的**正面**路径（wait.timedOut=true）由
        // TurnAwaitingV12Tests.Timeout_resume_puts_timed_out_on_wait（真实脚本）覆盖。
        _console._state.State = ConsoleState.Quit;
        _console._state.inputReq = null;

        var json = await _protocol.SubmitTimeoutAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.False(doc.RootElement.TryGetProperty("timedOut", out _),
            "v12 起顶层 timedOut 已退场");

        // flag 一次性：再调 GetInitialTurnAsync 不应残留
        _console._state.State = ConsoleState.WaitInput;
        var json2 = await _protocol.GetInitialTurnAsync();
        Assert.NotNull(json2);
        using var doc2 = JsonDocument.Parse(json2!);
        Assert.False(doc2.RootElement.TryGetProperty("timedOut", out _),
            "_pendingTimeoutFlag 必须一次性——读出后清空");
    }

    // ---------- T_buildturn_initial_includes_protocol_version ----------

    [Fact]
    public async Task T_buildturn_initial_includes_protocol_version()
    {
        SetWaitInput(req: null);

        var json = await _protocol.GetInitialTurnAsync();

        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal(12, doc.RootElement.GetProperty("protocolVersion").GetInt32()); // v12：awaiting 容器
    }

    [Fact]
    public async Task T_buildturn_non_initial_omits_protocol_version()
    {
        // StepAsync 调用走 BuildTurn(isInitial=false)，protocolVersion 应为 null（WhenWritingNull 不出现）。
        // 本测试不初始化 _console（_state.process 为 null）——DispatchInput 若真实派发输入
        // 会触发 PressEnterKey → RunEmueraProgram → DoScript 的 NRE（曾以 fatal 日志形式出现）。
        // 故 SetWaitInput(req: null)：DispatchInput 在 req==null 时直接返回，不执行脚本；
        // 断言目标（非初始帧无 protocolVersion）与 InputType 无关，不受影响。
        SetWaitInput(req: null);

        var initial = await _protocol.GetInitialTurnAsync();
        Assert.NotNull(initial);

        // StepAsync = DispatchInput（req==null 直接 return）+ WaitForInputAsync + BuildTurn
        var stepJson = await _protocol.StepAsync("");
        Assert.NotNull(stepJson);
        using var doc = JsonDocument.Parse(stepJson!);
        Assert.False(doc.RootElement.TryGetProperty("protocolVersion", out _),
            "非初始帧 protocolVersion 应为 null（WhenWritingNull 不出现）");
    }

    /// <summary>
    /// 测试用 SessionIO：空实现，IsConnected 永远 true，
    /// 不实际读/写——AgentJsonlProtocolTests 不依赖 IO 层交互。
    /// </summary>
    private sealed class FakeSessionIO : SessionIO
    {
        public override Task<string?> ReadLineAsync(CancellationToken ct)
            => Task.FromResult<string?>(null);
        public override void WriteLine(string text) { }
        public override void Close() { }
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
