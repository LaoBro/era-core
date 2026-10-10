using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 01（让路挂起与续跑·引擎层）的勘探性闸门：spec §7.1 的 E1–E4。
///
/// <para><b>接缝</b>：引擎层最高接缝 —— <see cref="LoaderTestHarness"/> 起一个最小真实引擎，
/// 脚本经真实的 <c>EmueraConsole.RunEmueraProgram</c> 跑，续跑经新的
/// <c>EmueraConsole.EndYield</c>。<b>不经过任何宿主 drain</b> —— 宿主 drain（W1-5 ~ W1-8）
/// 是后续票，本票只做 §3.2 的 ①②③（W1-1 ~ W1-4）。</para>
///
/// <para><b>防假绿</b>：E1 的核心断言不是「脚本最终能跑完」（改动前也成立），而是
/// 「脚本<b>停在</b>了无参 <c>AWAIT</c> 处、且要一次续跑才能走完剩余部分」。改动前
/// <c>Await(time&lt;=0)</c> 是早退（只刷新画面就继续跑），脚本一口气到 <c>Quit</c>，
/// 故 E1–E4 必然红（已实测：E1/E2 停在 <c>Quit</c>/<c>TINPUT</c>，E3/E4 续跑次数为 0）。</para>
///
/// <para><b>红测阶段说明</b>：新 API（<c>IsYield</c> / <c>EndYield</c> / <c>EndYieldCore</c>）
/// 本身是惰性的（无人调用即零行为改动），故先落地它们让用例可编译，红/绿判据只看行为 ——
/// 唯一的行为改动入口是 <c>ConsoleStateManager.Await(time&lt;=0)</c>。</para>
///
/// <para>⚠️ <b>构造陷阱</b>：<see cref="LoaderTestHarness"/> 构造器同步开 <c>GlobalStatic</c>
/// scope，而 AsyncLocal 不跨 await 上浮 —— harness 必须在测试方法体内 <c>new</c>，
/// 不能藏在被 <c>await</c> 的 helper 里（否则测试方法体读不到 Config/GlobalStatic，
/// 续跑期会以 <c>Config.get_FontSize → NRE</c> 的形式炸出来）。故本文件用
/// <see cref="NewHarness"/>（同步）+ <see cref="StartAsync"/>（异步）两段式。</para>
/// </summary>
[Collection("LoaderTests")]
public sealed class ScriptYieldSuspendTests
{
    private const string ErbFileName = "YIELDGATE.ERB";

    /// <summary>E3 的闭包性步数。</summary>
    private const int ClosureSteps = 1000;

    /// <summary>建 harness 并写脚本。必须在测试方法体内同步调用（见类注释的构造陷阱）。</summary>
    private static LoaderTestHarness NewHarness(string erb)
    {
        var harness = new LoaderTestHarness();
        harness.WriteErb(ErbFileName, erb);
        return harness;
    }

    /// <summary>启动引擎并跑一次 <c>@SYSTEM_TITLE</c>，使脚本停在首个停稳点。</summary>
    private static async Task StartAsync(LoaderTestHarness harness)
    {
        await harness.InitializeAsync();
        // 复刻 ConsoleStateManager.Initialize 的 process 装配（harness 只装 GlobalStatic）。
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");
    }

    /// <summary>是否停在让路点：State=WaitInput + Void + 无时长 + IsYield。</summary>
    private static bool IsAtYieldPoint(EmueraConsole console)
        => console.State == ConsoleState.WaitInput
           && console.CurrentRequest is { InputType: InputType.Void, Timelimit: <= 0 } req
           && req.IsYield;

    private static string RequestShape(InputRequest? req)
        => req == null
            ? "<null>"
            : $"{{ InputType={req.InputType}, Timelimit={req.Timelimit}, " +
              $"IsYield={req.IsYield} }}";

    /// <summary>当前画面文本（拼接全部显示行）。</summary>
    private static string ScreenText(EmueraConsole console)
        => string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

    /// <summary>断言失败时的诊断信息：状态 + 当前请求 + 画面。</summary>
    private static string Snapshot(EmueraConsole console)
        => $"State={console.State}；Request={RequestShape(console.CurrentRequest)}；\n画面：\n{ScreenText(console)}";

    /// <summary>
    /// 从 <paramref name="marker"/> 首次出现处起的画面文本。
    /// 加载期会打若干带真实耗时的诊断行（<c>経過時間: x ms</c>），运行才有随机性 ——
    /// 逐字节比较必须把它们排除在外（它们不是脚本产出的画面）。
    /// </summary>
    private static string ScreenTextFrom(EmueraConsole console, string marker)
    {
        var text = ScreenText(console);
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        return idx < 0 ? text : text[idx..];
    }

    /// <summary>连续续跑直到离开让路点（> <paramref name="bound"/> 次视为 drain 不终止）。</summary>
    private static int DrainYields(EmueraConsole console, int bound)
    {
        var resumes = 0;
        while (IsAtYieldPoint(console))
        {
            console.EndYield();
            if (++resumes > bound)
                throw new Xunit.Sdk.XunitException(
                    $"让路 drain 未终止（已续跑 {resumes} 次）。\n{Snapshot(console)}");
        }
        return resumes;
    }

    // ---------------------------------------------------------------
    // E1：零时长挂起可续跑 —— 脚本剩余部分在一次续跑后执行
    // ---------------------------------------------------------------
    [Fact]
    public async Task E1_bare_await_suspends_and_one_resume_finishes_the_rest()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL YIELD_BEFORE
            AWAIT
            PRINTL YIELD_AFTER
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.True(IsAtYieldPoint(console),
            "无参 AWAIT 未形成零时长挂起点（脚本疑似一口气跑完）。\n" + Snapshot(console));
        Assert.Contains("YIELD_BEFORE", ScreenText(console));
        Assert.DoesNotContain("YIELD_AFTER", ScreenText(console));

        console.EndYield();

        Assert.Contains("YIELD_AFTER", ScreenText(console));
        Assert.Equal(ConsoleState.Quit, console.State);
    }

    // ---------------------------------------------------------------
    // E2：无副作用 —— 五项全否（① isTimeout 未置、② TimeUpMes 未打印、
    //     ③ 无定时器被武装、④ ISTIMEOUT 仍为 0、⑤ IsRunningTimer 未被翻转）
    // ---------------------------------------------------------------
    [Fact]
    public async Task E2_resume_leaves_no_timeout_traces()
    {
        // AWAIT 之后先读一次 ISTIMEOUT（④ 的外部可观测面），再进入一个带定时器的输入等待
        // （⑤ 的观测面：让路若误置 isTimeout，这里会表现为倒计时被判定为「已结束」）。
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL YIELD_BEFORE
            AWAIT
            PRINTFORML AFTER_ISTIMEOUT={ISTIMEOUT}
            TINPUT 3000, 5, 0
            PRINTL TINPUT_DONE
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.True(IsAtYieldPoint(console), "无参 AWAIT 未形成零时长挂起点。\n" + Snapshot(console));

        // ③ 让路请求 Timelimit=0 ⇒ 不武装定时器（宿主侧观测面：InputTimeoutMs 为 null）。
        Assert.Null(console.InputTimeoutMs);
        Assert.Equal(0L, console.InputTimelimit);
        // ① 挂起时 isTimeout 就不该被置。
        Assert.False(console._state.isTimeout);

        console.EndYield();

        // ④ 脚本变量 ISTIMEOUT 仍为 0（外部可观测：误用 EndTimerCore 会让它变 1）。
        Assert.Contains("AFTER_ISTIMEOUT=0", ScreenText(console));
        // ①
        Assert.False(console._state.isTimeout, "续跑把 isTimeout 置了（疑似复用了 EndTimerCore）。");
        // ② 未打印超时文案：本次让路请求没有 TimeUpMes，且 TINPUT 的超时文案也不该被提前打出。
        Assert.DoesNotContain("TINPUT_DONE", ScreenText(console));
        // 续跑停在 TINPUT 上：定时等待确实成立，⑤ 才有观测面。
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(3000L, console.InputTimelimit);
        // ⑤ IsRunningTimer 未被翻转（isTimeout 若被误置，这里会是 false）。
        Assert.True(console.IsRunningTimer, "IsRunningTimer 被翻转（isTimeout 疑似被让路置位）。");
    }

    // ---------------------------------------------------------------
    // E3：闭包性 —— 连续 1000 次让路 → 续跑后，画面与一次性跑完逐字节相同
    // ---------------------------------------------------------------
    [Fact]
    public async Task E3_thousand_yields_produce_byte_identical_screen()
    {
        string yieldedScreen;
        using (var yielded = NewHarness(ClosureErb(withAwait: true)))
        {
            await StartAsync(yielded);
            var resumes = DrainYields(yielded.Console, ClosureSteps * 2);
            Assert.True(resumes == ClosureSteps,
                $"续跑次数 {resumes} ≠ {ClosureSteps}。\n{Snapshot(yielded.Console)}");
            yieldedScreen = ScreenTextFrom(yielded.Console, "E3_HEAD");
        }

        string straightScreen;
        using (var straight = NewHarness(ClosureErb(withAwait: false)))
        {
            await StartAsync(straight);
            straightScreen = ScreenTextFrom(straight.Console, "E3_HEAD");
        }

        Assert.Contains("E3_DONE", yieldedScreen);
        Assert.Equal(straightScreen, yieldedScreen);
    }

    /// <summary>E3 的两种变体：仅差每轮是否夹一条无参 <c>AWAIT</c>。</summary>
    private static string ClosureErb(bool withAwait)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@SYSTEM_TITLE");
        // #DIM 必须紧贴函数声明（否则 Lv1 警告 + 该标识符不可解析 → 脚本报错）。
        sb.AppendLine("#DIM E3IDX");
        sb.AppendLine("PRINTL E3_HEAD");
        sb.AppendLine($"FOR E3IDX, 0, {ClosureSteps}");
        sb.AppendLine("\tPRINTFORML E3-{E3IDX}");
        if (withAwait)
            sb.AppendLine("\tAWAIT");
        sb.AppendLine("NEXT");
        sb.AppendLine("PRINTL E3_DONE");
        sb.AppendLine("QUIT");
        return sb.ToString();
    }

    // ---------------------------------------------------------------
    // E4：泄漏是长期的 —— 走完 E1–E3 的让路后，进入带定时器的输入等待
    //     并正常输入，该次期间 ISTIMEOUT 为 0
    // ---------------------------------------------------------------
    [Fact]
    public async Task E4_istimeout_stays_zero_across_a_later_timed_wait()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            REPEAT 5
            PRINTFORML E4_STEP
            AWAIT
            REND
            PRINTFORML E4_AFTER_YIELD={ISTIMEOUT}
            TINPUT 3000, 5, 0
            PRINTFORML E4_AFTER_TINPUT={ISTIMEOUT}
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.Equal(5, DrainYields(console, bound: 100));

        // 让路段的脚本观察面：一次让路都没污染 isTimeout。
        Assert.Contains("E4_AFTER_YIELD=0", ScreenText(console));

        // 本次带定时器的输入等待期间：isTimeout 为 0（倒计时仍在跑）。
        Assert.Equal(ConsoleState.WaitInput, console.State);
        Assert.Equal(3000L, console.InputTimelimit);
        Assert.False(console._state.isTimeout);
        Assert.True(console.IsRunningTimer);

        // 正常输入 ⇒ 脚本推进，该次等待期间 ISTIMEOUT 仍为 0。
        console.RunEmueraProgram("5");

        Assert.Contains("E4_AFTER_TINPUT=0", ScreenText(console));
        Assert.Equal(ConsoleState.Quit, console.State);
    }
}
