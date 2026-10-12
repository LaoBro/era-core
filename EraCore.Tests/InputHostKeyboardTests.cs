using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0031 / spec-v3 **M2**脚本侧接缝测试 —— 键盘状态（`GETKEY` / `GETKEYTRIGGERED`）。
/// <para>
/// ⚠️ <b>本文件的核心不变量是「语义忠实」，不是「功能可用」</b>。曾有一版实现用
/// 「时间窗口」放宽 <c>GETKEYTRIGGERED</c>（让它在 100ms 内反复返回 1），
/// 目的是让触屏 E-min 补偿能解 <c>COMF413</c> 死循环 —— <b>已回退</b>。
/// 理由：为一个宿主的补偿 hack 污染所有游戏的输入语义，代价远大于收益
/// （ADR-0031「core 不模拟、不补偿、不放宽」）。
/// </para>
/// <para>
/// 照抄对象：<c>Creator.Method.System.cs</c> 的 <c>#if !HEADLESS</c> 分支
/// <c>(s &lt; 0) &amp;&amp; (toggle != keytoggle[keycode])</c>。
/// 唯一差别：<c>keytoggle</c> 由进程级 static 降为宿主级实例（消除跨会话泄漏）。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public sealed class InputHostKeyboardTests
{
    private const string ErbFileName = "KEYHOST.ERB";

    /// <summary>
    /// 连读两次 <c>GETKEYTRIGGERED(65)</c> —— 第二次读必须是 0，
    /// 这是「读到即失效」的直接观测，也是防今后再度放宽的回归锚点。
    /// </summary>
    private const string ProbeErb = """
        @SYSTEM_TITLE
        PRINTFORML K={GETKEY(65)}
        PRINTFORML T1={GETKEYTRIGGERED(65)}
        PRINTFORML T2={GETKEYTRIGGERED(65)}
        PRINTFORML M=[%MOUSEB()%]
        PRINTFORML O={GETKEY(300)}
        PRINTFORML A={ISACTIVE()}
        QUIT
        """;

    private static async Task<string> RunAsync(IInputHost? host)
    {
        // ⚠️ 构造陷阱（同 TimedWaitInputSwallowedTests）：harness 构造器同步开 GlobalStatic
        // scope，AsyncLocal 不跨 await 上浮 ⇒ 必须在测试方法体内 new。
        using var harness = new LoaderTestHarness(inputHost: host);
        harness.WriteErb(ErbFileName, ProbeErb);
        await harness.InitializeAsync();
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");
        return string.Join("\n", harness.Console.DisplayLineList.Select(l => l?.ToString() ?? ""));
    }

    // ---------------------------------------------------------------
    // ① 无能力 ⇒ 与骨架前逐项一致（spec-v3 §8 L1'）
    // ---------------------------------------------------------------
    [Fact]
    public async Task Null_host_keeps_pre_skeleton_observable_values()
    {
        var screen = await RunAsync(null);

        Assert.Contains("K=0", screen);
        Assert.Contains("T1=0", screen);
        Assert.Contains("T2=0", screen);
        Assert.Contains("M=[]", screen);
        Assert.Contains("O=0", screen);   // 越界键码
        Assert.Contains("A=1", screen);   // ISACTIVE 恒 1
    }

    /// <summary>
    /// 声明 <c>None</c> 的宿主：即便写入状态，脚本也不该看见（能力位门卫）。
    /// <para>
    /// 注：<c>MOUSEB</c> 不看能力位 —— 悬停值采用「宿主写什么引擎读什么」，
    /// 「全 false ⇒ <c>MOUSEB</c>=""」由 <see cref="NullInputHost"/> 那条断言守住。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Host_without_keyboard_capability_gates_key_state()
    {
        var host = new DefaultInputHost(InputCapability.None);
        host.SubmitKey(65, true);
        host.SubmitHover("42");

        var screen = await RunAsync(host);

        Assert.Contains("K=0", screen);
        Assert.Contains("T1=0", screen);
        Assert.Contains("M=[42]", screen);
    }

    // ---------------------------------------------------------------
    // ② 电平（GETKEY）—— 纯读取，无副作用
    // ---------------------------------------------------------------
    [Fact]
    public async Task KeyboardState_level_is_visible_to_GETKEY()
    {
        var host = new DefaultInputHost();
        host.SubmitKey(65, true);          // 保持按下

        var screen = await RunAsync(host);

        Assert.Contains("K=1", screen);
    }

    [Fact]
    public async Task KeyboardState_release_clears_level()
    {
        var host = new DefaultInputHost();
        host.SubmitKey(65, true);
        host.SubmitKey(65, false);

        var screen = await RunAsync(host);

        Assert.Contains("K=0", screen);
    }

    // ---------------------------------------------------------------
    // ② 边沿（GETKEYTRIGGERED）—— 原版 toggle 语义
    // ---------------------------------------------------------------

    /// <summary>
    /// 🔒 <b>回归锚点（用户指定）</b>：按下期间第一次读 = 1，**第二次读必须为 0**。
    /// 若有人再把边沿改成时间窗口/保留式，本测试转红。
    /// </summary>
    [Fact]
    public async Task GETKEYTRIGGERED_second_read_must_be_zero()
    {
        var host = new DefaultInputHost();
        host.SubmitKey(65, true);          // 键仍按着（对应原版 s < 0）

        var screen = await RunAsync(host);

        Assert.Contains("T1=1", screen);
        Assert.Contains("T2=0", screen);   // ← 读到即失效
    }

    /// <summary>
    /// 原版 <c>s &lt; 0</c> 条件：键已松开时读，即使版本号变过也必须是 0。
    /// （这正是"点击合成 down+up 后立刻读"拿不到触发的原因 —— 宿主必须分开发 down/up。）
    /// </summary>
    [Fact]
    public async Task GETKEYTRIGGERED_requires_key_currently_down()
    {
        var host = new DefaultInputHost();
        host.SubmitKey(65, true);
        host.SubmitKey(65, false);         // 已松开

        var screen = await RunAsync(host);

        Assert.Contains("T1=0", screen);
    }

    [Fact]
    public void ConsumeTrigger_retriggers_on_next_press()
    {
        var host = new DefaultInputHost();

        host.SubmitKey(65, true);
        Assert.True(host.ConsumeTrigger(65));    // 第一次读
        Assert.False(host.ConsumeTrigger(65));   // 读到即失效

        host.SubmitKey(65, false);
        Assert.False(host.ConsumeTrigger(65));   // 已松开

        host.SubmitKey(65, true);                // 再次按下
        Assert.True(host.ConsumeTrigger(65));    // 重新触发
    }

    /// <summary>长按自动重复：重复的 down 不递增版本号 ⇒ 不重复触发。</summary>
    [Fact]
    public void ConsumeTrigger_ignores_auto_repeat()
    {
        var host = new DefaultInputHost();

        host.SubmitKey(65, true);
        Assert.True(host.ConsumeTrigger(65));

        host.SubmitKey(65, true);                // 自动重复的重复 down
        host.SubmitKey(65, true);
        Assert.False(host.ConsumeTrigger(65));   // 不重复触发
    }

    [Fact]
    public void ConsumeTrigger_out_of_range_is_false()
    {
        var host = new DefaultInputHost();
        host.SubmitKey(65, true);

        Assert.False(host.ConsumeTrigger(-1));
        Assert.False(host.ConsumeTrigger(300));
    }

    // ---------------------------------------------------------------
    // MOUSEB 读悬停值
    // ---------------------------------------------------------------
    [Fact]
    public async Task SubmitHover_is_visible_to_MOUSEB()
    {
        var host = new DefaultInputHost();
        host.SubmitHover("42");

        var screen = await RunAsync(host);

        Assert.Contains("M=[42]", screen);
    }

    // ---------------------------------------------------------------
    // ResetState（会话重建 / 焦点丢失）
    // ---------------------------------------------------------------
    [Fact]
    public async Task ResetState_clears_everything()
    {
        var host = new DefaultInputHost();
        host.SubmitKey(65, true);
        host.SubmitHover("42");
        host.ResetState();

        var screen = await RunAsync(host);

        Assert.Contains("K=0", screen);
        Assert.Contains("T1=0", screen);
        Assert.Contains("M=[]", screen);
    }

    // ---------------------------------------------------------------
    // 快照一致性（spec-v3 §8 L1''）：一次 WaitInput 内高频轮询读到同一组值
    // ---------------------------------------------------------------
    [Fact]
    public void Snapshot_replacement_is_atomic_across_fields()
    {
        var host = new DefaultInputHost();
        host.SubmitKey(65, true);
        host.SubmitHover("7");

        // 连续 10k 次读：坐标/按键/悬停必须始终同属一次写入，不出现撕裂
        var first = host.Current;
        for (int i = 0; i < 10_000; i++)
        {
            var snap = host.Current;
            Assert.Same(first, snap);
            Assert.True(snap.Keys.IsDown(65));
            Assert.Equal("7", snap.HoverValue);
        }
    }
}
