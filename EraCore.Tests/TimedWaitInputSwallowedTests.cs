using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue 10（撤回 <c>Skippable</c> / <c>wait.interruptible</c>）的验收闸门 —— spec §10.1 / §10.5。
///
/// <para><b>判据（写死）</b>：参考实现不能打断的指令，era-core 也不得能打断。
/// <c>AWAIT t</c> / <c>TWAIT t,1</c> / 让路点三者的可观测行为必须<b>逐字相同</b>：
/// 输入一律吞掉 —— 不推进、不报错、不发 <c>error</c>，等待只由定时器正常到期结束。</para>
///
/// <para><b>接缝</b>：直接驱动改动点 <c>AgentProtocolBase.DispatchInput</c> 的
/// <see cref="InputType.Void"/> 分支。脚本经真实 <c>EmueraConsole.RunEmueraProgram</c> 跑。</para>
///
/// <para><b>防假绿</b>：
/// <list type="bullet">
/// <item>核心断言不是「没抛异常」（改动前 Void 分支已存在、同样不抛），而是「等待<b>没有被提前结束</b>」
/// ——以 <c>InputRequest.ID</c> 未变 + 后续 marker 未打印双重钉住；</item>
/// <item>T1/T2 在撤回前必然红：<c>AWAIT</c> 当时置 <c>Skippable=true</c>，任意输入（T1）与
/// <c>\e</c>（T2）都会走 <c>SubmitTimeout</c> 提前续跑；</item>
/// <item>T3/T4 是回归锚点：它们撤回前就绿，用来坐实「撤回后 <c>AWAIT</c> 与 <c>TWAIT t,1</c>
/// 同待遇」这一条，而不是把 <c>AWAIT</c> 单独改成了别的东西。</item>
/// </list></para>
///
/// <para>⚠️ <b>构造陷阱</b>（同 <see cref="ScriptYieldSuspendTests"/>）：<see cref="LoaderTestHarness"/>
/// 构造器同步开 <c>GlobalStatic</c> scope，AsyncLocal 不跨 await 上浮 —— harness 必须在测试方法体内
/// <c>new</c>，不能藏在被 await 的 helper 里。故用 <see cref="NewHarness"/>（同步）+
/// <see cref="StartAsync"/>（异步）两段式。</para>
/// </summary>
[Collection("LoaderTests")]
public sealed class TimedWaitInputSwallowedTests
{
    private const string ErbFileName = "TIMEDSWALLOW.ERB";

    /// <summary>把 <c>DispatchInput</c>（protected）暴露成 public，供引擎层直接驱动。</summary>
    private sealed class DispatchProbe : AgentProtocolBase
    {
        public DispatchProbe(EmueraConsole console) : base(console, console.UIAdapter) { }
        public void Dispatch(string input) => DispatchInput(input);
    }

    /// <summary>建 harness 并写脚本。必须在测试方法体内同步调用（见类注释的构造陷阱）。</summary>
    private static LoaderTestHarness NewHarness(string erb)
    {
        var harness = new LoaderTestHarness();
        harness.WriteErb(ErbFileName, erb);
        return harness;
    }

    /// <summary>启动引擎并跑一次 <c>@SYSTEM_TITLE</c>，使脚本停在首个停稳点（AWAIT / TWAIT）。</summary>
    private static async Task StartAsync(LoaderTestHarness harness)
    {
        await harness.InitializeAsync();
        // 复刻 ConsoleStateManager.Initialize 的 process 装配（harness 只装 GlobalStatic）。
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");
    }

    private static string RequestShape(InputRequest? req)
        => req == null
            ? "<null>"
            : $"{{ ID={req.ID}, InputType={req.InputType}, Timelimit={req.Timelimit}, IsYield={req.IsYield} }}";

    private static string ScreenText(EmueraConsole console)
        => string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

    private static string Snapshot(EmueraConsole console)
        => $"State={console.State}；MesSkip={console.MesSkip}；Request={RequestShape(console.CurrentRequest)}；\n画面：\n{ScreenText(console)}";

    // ---------------------------------------------------------------
    // T1：AWAIT t 期间投任意输入 ⇒ 吞掉（不推进、不报错、MesSkip 不置位）
    //     —— 撤回前必然红：Skippable=true 时任意输入都会 SubmitTimeout 提前续跑
    // ---------------------------------------------------------------
    [Fact]
    public async Task Timed_await_swallows_arbitrary_client_input()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL SWALLOW_HEAD
            AWAIT 8000
            PRINTL SWALLOW_DONE
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(InputType.Void, console.CurrentRequest!.InputType);
        Assert.Equal(8000L, console.CurrentRequest!.Timelimit);
        long requestIdBefore = console.CurrentRequest!.ID;

        new DispatchProbe(console).Dispatch("");

        Assert.DoesNotContain("SWALLOW_DONE", ScreenText(console));
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(requestIdBefore, console.CurrentRequest!.ID);
        Assert.Equal(8000L, console.CurrentRequest!.Timelimit);
        Assert.False(console.MesSkip);
    }

    // ---------------------------------------------------------------
    // T2：AWAIT t 期间投 \e ⇒ 同样吞掉，且不启动快进
    //     （参考实现：Void 等待上不能启动跳过 —— PressEnterKey 的 Void 早退在 \e 检测之前）
    //     —— 撤回前必然红：\e 会置 MesSkip 并 SubmitTimeout，脚本读到 MESSKIP()=1
    // ---------------------------------------------------------------
    [Fact]
    public async Task Timed_await_swallows_escape_and_never_starts_skipping()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL ESC_HEAD
            AWAIT 8000
            PRINTFORML ESC_MESSKIP={MESSKIP()}
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.Equal(ConsoleState.WaitInput, console.State);
        long requestIdBefore = console.CurrentRequest!.ID;

        new DispatchProbe(console).Dispatch("\\e");

        Assert.DoesNotContain("ESC_MESSKIP", ScreenText(console));
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(requestIdBefore, console.CurrentRequest!.ID);
        Assert.False(console.MesSkip);
    }

    // ---------------------------------------------------------------
    // T3：TWAIT t,1 期间投 \e ⇒ 吞掉（回归锚点：撤回前就绿）
    //     与 T2 逐字同待遇 —— 这正是撤回要消除的差别
    // ---------------------------------------------------------------
    [Fact]
    public async Task Non_interruptible_twait_swallows_escape_input()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL TW_HEAD
            TWAIT 8000, 1
            PRINTL TW_DONE
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(InputType.Void, console.CurrentRequest!.InputType);
        Assert.Equal(8000L, console.CurrentRequest!.Timelimit);
        long requestIdBefore = console.CurrentRequest!.ID;

        // 不抛异常（Void 分支存在；改动前 Void 落 default 会抛 ArgumentOutOfRangeException）。
        new DispatchProbe(console).Dispatch("\\e");

        Assert.DoesNotContain("TW_DONE", ScreenText(console));
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(requestIdBefore, console.CurrentRequest!.ID);
        Assert.False(console.MesSkip);
    }

    // ---------------------------------------------------------------
    // T4：零时长让路点期间投 \e ⇒ 吞掉（回归锚点：撤回前就绿）
    //     且不阻塞 drain —— 让路点的续跑入口是 EmueraConsole.EndYield，不是输入
    // ---------------------------------------------------------------
    [Fact]
    public async Task Yield_point_swallows_escape_input()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL YIELD_HEAD
            AWAIT
            PRINTL YIELD_DONE
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.True(console.CurrentRequest!.IsYield, "本例应停在零时长让路点上。\n" + Snapshot(console));
        long requestIdBefore = console.CurrentRequest!.ID;

        new DispatchProbe(console).Dispatch("\\e");

        Assert.DoesNotContain("YIELD_DONE", ScreenText(console));
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(requestIdBefore, console.CurrentRequest!.ID);
        Assert.False(console.MesSkip);
    }
}
