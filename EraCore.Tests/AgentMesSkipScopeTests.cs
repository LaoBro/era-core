using System;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 02（请求侧快进的 MesSkip 作用域化）的验收闸门。
///
/// <para><b>接缝</b>：直接驱动改动点本身 —— <c>AgentProtocolBase.DispatchInput</c> 的
/// <see cref="InputType.Void"/> 分支（spec v5 §4.2，唯一改动处）。脚本经真实的
/// <c>EmueraConsole.RunEmueraProgram</c> 跑，打断经真实的 <c>SubmitTimeout → EndTimerCore</c>。
/// 不走任何宿主 drain / while 快进链 —— 本文有意收窄到「单个续跑区间」（§4.4 ⚠️）。</para>
///
/// <para><b>防假绿</b>：核心断言不是「脚本最终能跑完」（改动前也能），而是「续跑期间脚本里那句
/// <c>SIF MESSKIP() RETURN</c> 被真正执行到」——<c>MESSKIP()</c> 在续跑期读到 1、且整段动画
/// 在一个续跑区间内跳完。改动前 <c>DispatchInput</c> 只调 <c>SubmitTimeout</c>，从不置
/// <c>MesSkip</c> ⇒ 该分支是死代码、<c>MESSKIP()</c> 恒 0，故 T1/T2/T3 必然红。</para>
///
/// <para>⚠️ <b>构造陷阱</b>（同 <see cref="ScriptYieldSuspendTests"/>）：<see cref="LoaderTestHarness"/>
/// 构造器同步开 <c>GlobalStatic</c> scope，AsyncLocal 不跨 await 上浮 —— harness 必须在测试方法体内
/// <c>new</c>，不能藏在被 await 的 helper 里。故用 <see cref="NewHarness"/>（同步）+
/// <see cref="StartAsync"/>（异步）两段式。</para>
/// </summary>
[Collection("LoaderTests")]
public sealed class AgentMesSkipScopeTests
{
    private const string ErbFileName = "MESKIPSCOPE.ERB";

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

    /// <summary>是否停在「可打断的长动画」：State=WaitInput + Void + 有时限 + Skippable。</summary>
    private static bool IsAtInterruptibleAwait(EmueraConsole console)
        => console.State == ConsoleState.WaitInput
           && console.CurrentRequest is { InputType: InputType.Void, Timelimit: > 0, Skippable: true };

    private static string RequestShape(InputRequest? req)
        => req == null
            ? "<null>"
            : $"{{ InputType={req.InputType}, Timelimit={req.Timelimit}, " +
              $"Skippable={req.Skippable}, IsYield={req.IsYield} }}";

    private static string ScreenText(EmueraConsole console)
        => string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

    private static string Snapshot(EmueraConsole console)
        => $"State={console.State}；MesSkip={console.MesSkip}；Request={RequestShape(console.CurrentRequest)}；\n画面：\n{ScreenText(console)}";

    // ---------------------------------------------------------------
    // T1：MESSKIP() 解锁 —— 带 \e 的输入打断后，续跑期间脚本读到 MESSKIP()=1
    // ---------------------------------------------------------------
    [Fact]
    public async Task Interruptible_await_with_escape_unlocks_messkip_during_resume()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL SKIP_HEAD
            AWAIT 8000
            PRINTFORML RESUME_MESSKIP={MESSKIP()}
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.True(IsAtInterruptibleAwait(console), "脚本未停在可打断的 AWAIT 上。\n" + Snapshot(console));

        // 带 \e 的请求侧输入：打断 + 置 MesSkip，再走一次续跑。
        new DispatchProbe(console).Dispatch("\\e");

        Assert.Contains("RESUME_MESSKIP=1", ScreenText(console));
        Assert.Equal(ConsoleState.Quit, console.State);
    }

    // ---------------------------------------------------------------
    // T2：MesSkip 不外泄 —— 续跑停稳后回到置位前的值（正常路径即 0），
    //     且不污染后续普通输入等待
    // ---------------------------------------------------------------
    [Fact]
    public async Task MesSkip_does_not_leak_after_resume_settles()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL LEAK_HEAD
            AWAIT 8000
            PRINTFORML RESUME1_MESSKIP={MESSKIP()}
            TINPUT 3000, 5, 0
            PRINTFORML AFTER_TINPUT_MESSKIP={MESSKIP()}
            QUIT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.True(IsAtInterruptibleAwait(console), "脚本未停在可打断的 AWAIT 上。\n" + Snapshot(console));

        new DispatchProbe(console).Dispatch("\\e");

        // 续跑期确实置位过（T1 已证），但停稳后必须归零。
        Assert.Contains("RESUME1_MESSKIP=1", ScreenText(console));
        Assert.False(console.MesSkip,
            "续跑停稳后 MesSkip 残留为真 —— 后续可跳过的输入会被静默填默认值。\n" + Snapshot(console));

        // 续跑停在 TINPUT 上，再走一次普通输入：本次期间 MESSKIP() 仍为 0。
        Assert.Equal(ConsoleState.WaitInput, console.State);
        console.RunEmueraProgram("5");

        Assert.Contains("AFTER_TINPUT_MESSKIP=0", ScreenText(console));
        Assert.False(console.MesSkip);
        Assert.Equal(ConsoleState.Quit, console.State);
    }

    // ---------------------------------------------------------------
    // T3：整段动画跳过 —— 照 ANIMATION_BGCOLOR_TRANSITION 结构写的脚本，
    //     SIF MESSKIP() RETURN 分支真正被走到（单个续跑区间内跳完）
    //     注：spec 的 `SIF MESSKIP() RETURN` 是简写；本引擎里 RETURN 不能内联在 SIF 同行
    //     （命令名 RETURN 被当变量 → CodeEE），故用 eraTW 同款两行式 `SIF <cond>` + 缩进 RETURN。
    // ---------------------------------------------------------------
    [Fact]
    public async Task Whole_animation_is_skipped_within_single_resume()
    {
        using var harness = NewHarness("""
            @SYSTEM_TITLE
            PRINTL ANIM_HEAD
            CALL ANIM_BODY
            PRINTL ANIM_TAIL
            QUIT

            @ANIM_BODY
            #DIM ANIMI
            FOR ANIMI, 0, 5
                SIF MESSKIP()
                    RETURN
                PRINTFORML ANIM_STEP-{ANIMI}
                AWAIT 100
            NEXT
            """);
        await StartAsync(harness);
        var console = harness.Console;

        Assert.True(IsAtInterruptibleAwait(console), "脚本未停在动画首个 AWAIT 上。\n" + Snapshot(console));
        Assert.Contains("ANIM_STEP-0", ScreenText(console));
        Assert.DoesNotContain("ANIM_STEP-1", ScreenText(console));

        new DispatchProbe(console).Dispatch("\\e");

        // MesSkip 为真 ⇒ 下一次循环体开头 SIF MESSKIP() RETURN 命中 ⇒ 整段动画一次跳完。
        Assert.Contains("ANIM_TAIL", ScreenText(console));
        Assert.DoesNotContain("ANIM_STEP-1", ScreenText(console));
        Assert.Equal(ConsoleState.Quit, console.State);
    }

    // ---------------------------------------------------------------
    // T4：不可打断 —— TWAIT t,1 期间投 \e ⇒ 输入被吞、不报错、不推进
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
        Assert.False(console.CurrentRequest!.Skippable, "TWAIT t,1 不应是可打断的。");
        Assert.Equal(8000L, console.CurrentRequest!.Timelimit);

        // 不抛异常（改动前 Void 落 default 会抛 ArgumentOutOfRangeException）。
        new DispatchProbe(console).Dispatch("\\e");

        Assert.DoesNotContain("TW_DONE", ScreenText(console));
        Assert.Equal(ConsoleState.WaitInput, console.State);
        // 仍是同一个等待：帧没有推进。
        Assert.Equal(InputType.Void, console.CurrentRequest!.InputType);
        Assert.Equal(8000L, console.CurrentRequest!.Timelimit);
        Assert.False(console.MesSkip);
    }
}
