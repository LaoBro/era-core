using System.Text.Json;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 05 的<b>第二产出者</b>闸门：致命错误帧（轴 A 的例外 B5，spec §2.1.3 / §5.2 第 5-6 项）。
///
/// <para>这条路径<b>不经过脚本</b>：MAUI 的 <c>BridgeHost.ShowError</c> 直接构造终态帧推给前端。
/// 它历史上与主流路径的序列化假设不一致（直接用 <c>EmueraJsonContext.Default.TurnRecord</c>
/// ——那是**没有 Converters** 的上下文，只因 <c>diff:null</c> 才侥幸不炸），
/// <c>awaiting</c> 一变成内嵌对象就必须单列验收。</para>
///
/// <para>两条闸门：<br/>
/// ① 形状：<c>awaiting.ended{terminal:"Error"}</c>，<c>input</c> / <c>wait</c> 不得同时出现；<br/>
/// ② 序列化：该路径确实能序列化新的内嵌对象（含**只经源生成上下文**这一支——
/// 新类型未注册时这里会退化成空壳或直接抛）。</para>
/// </summary>
public sealed class FatalTurnShapeTests
{
    [Fact]
    public void Fatal_turn_is_a_wellformed_terminal_frame()
    {
        var json = AgentJsonlProtocol.BuildFatalTurn("boom");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Error", root.GetProperty("state").GetString());
        Assert.Equal("boom", root.GetProperty("error").GetString());

        var awaiting = root.GetProperty("awaiting");
        Assert.True(awaiting.TryGetProperty("ended", out var ended),
            $"致命帧必须是终态形状（awaiting.ended）：{awaiting}");
        Assert.Equal("Error", ended.GetProperty("terminal").GetString());
        Assert.False(awaiting.TryGetProperty("input", out _), "终态帧不得带 input（互斥键）");
        Assert.False(awaiting.TryGetProperty("wait", out _), "终态帧不得带 wait（互斥键）");
    }

    /// <summary>
    /// 🔴 新的内嵌类型必须在源生成解析器里注册：<c>EmueraJsonContext.Default</c>（**无 Converters**）
    /// 也必须能把 <c>awaiting</c> 序列化成完整对象，而不是空壳/抛异常 ——
    /// 这正是 MAUI 那条历史路径的失效面（变异式 H）。
    /// </summary>
    [Fact]
    public void Source_generated_context_serializes_awaiting_fully()
    {
        var turn = new TurnRecord(
            state: "WaitInput",
            inputType: "Void",
            needValue: false,
            diff: null,
            awaiting: AwaitInfo.Of(
                new InputRequest { InputType = InputType.Void, Timelimit = 3000, Skippable = true },
                ConsoleState.WaitInput,
                timedOut: true));

        var json = JsonSerializer.Serialize(turn, EmueraJsonContext.Default.TurnRecord);

        using var doc = JsonDocument.Parse(json);
        var awaiting = doc.RootElement.GetProperty("awaiting");
        var wait = awaiting.GetProperty("wait");
        Assert.Equal(3000, wait.GetProperty("durationMs").GetInt64());
        Assert.True(wait.GetProperty("interruptible").GetBoolean());
        Assert.True(wait.GetProperty("timedOut").GetBoolean());
        Assert.False(awaiting.TryGetProperty("input", out _), "Void 不期待输入 ⇒ input 不得出现");
    }
}
