using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Input;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0031 / spec-v3 **M6**脚本侧接缝测试 —— 焦点（`ISACTIVE`）与失焦清键。
/// <para>
/// 本项真正要防的不是「<c>ISACTIVE</c> 读不到真值」，而是**幽灵按键**：
/// 窗口失焦后浏览器/系统会吞掉后续 keyup / mouseup，电平若停在 1，
/// 脚本侧就变成「玩家一直按着」—— 表现为 <c>GETKEY</c> 恒 1、每圈都触发。
/// </para>
/// <para>
/// 双保险：前端补发全量 up（M6 前端侧）+ 宿主 <c>NotifyFocusChanged(false)</c> 清键（本文件）。
/// 任一半失效都不该留下幽灵电平。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public sealed class InputHostFocusTests
{
    private const string ErbFileName = "FOCUSHOST.ERB";

    private const string ProbeErb = """
        @SYSTEM_TITLE
        PRINTFORML A={ISACTIVE()}
        PRINTFORML K={GETKEY(65)}
        PRINTFORML T1={GETKEYTRIGGERED(65)}
        PRINTFORML M=[%MOUSEB()%]
        QUIT
        """;

    private static async Task<string> RunAsync(IInputHost? host)
    {
        // ⚠️ 构造陷阱（同 InputHostKeyboardTests）：harness 构造器同步开 GlobalStatic
        // scope，AsyncLocal 不跨 await 上浮 ⇒ 必须在测试方法体内 new。
        using var harness = new LoaderTestHarness(inputHost: host);
        harness.WriteErb(ErbFileName, ProbeErb);
        await harness.InitializeAsync();
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");
        return string.Join("\n", harness.Console.DisplayLineList.Select(l => l?.ToString() ?? ""));
    }

    // ---------------------------------------------------------------
    // ① ISACTIVE 的取值（能力位门卫，spec-v3 §3.5 降级矩阵）
    // ---------------------------------------------------------------

    /// <summary>无宿主 / 无 Focus 能力 ⇒ 恒 1（= 接入前行为，逐项一致）。</summary>
    [Fact]
    public async Task Without_focus_capability_ISACTIVE_is_always_one()
    {
        Assert.Contains("A=1", await RunAsync(null));

        var host = new DefaultInputHost(InputCapability.KeyboardState);
        host.NotifyFocusChanged(false);          // 即便写入，能力位未声明也不该被看见

        Assert.Contains("A=1", await RunAsync(host));
    }

    [Fact]
    public async Task Focus_capability_reads_snapshot()
    {
        var focused = new DefaultInputHost(InputCapability.Focus);
        Assert.Contains("A=1", await RunAsync(focused));

        var blurred = new DefaultInputHost(InputCapability.Focus);
        blurred.NotifyFocusChanged(false);
        Assert.Contains("A=0", await RunAsync(blurred));

        // 焦点回来 ⇒ 恢复 1
        var regained = new DefaultInputHost(InputCapability.Focus);
        regained.NotifyFocusChanged(false);
        regained.NotifyFocusChanged(true);
        Assert.Contains("A=1", await RunAsync(regained));
    }

    // ---------------------------------------------------------------
    // ② 失焦清键 —— 幽灵按键的正面拦截
    // ---------------------------------------------------------------

    /// <summary>
    /// 🔒 <b>失焦必须清键，且重新聚焦后不该复活</b>。
    /// <para>
    /// 判据刻意在「重新聚焦之后」读：此时 <c>ISACTIVE</c> 已是 1，
    /// <c>GETKEY</c> 的 IsActive 门卫不再替我们兜底 —— 若宿主没真清键，
    /// <c>K</c> 会读到 1（电平从失焦前残留下来）。所以这条测的是**清键本身**，
    /// 不是「失焦时读不到」这个白送的效果。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Focus_loss_clears_keys_and_they_do_not_come_back()
    {
        var host = new DefaultInputHost(InputCapability.KeyboardState | InputCapability.Focus);
        host.SubmitKey(65, true);                 // 玩家按着 A
        host.NotifyFocusChanged(false);           // 失焦（浏览器不会再送 keyup）
        host.NotifyFocusChanged(true);            // 重新聚焦

        var screen = await RunAsync(host);

        Assert.Contains("A=1", screen);           // 已重新聚焦
        Assert.Contains("K=0", screen);           // ⇒ 键必须是清的（幽灵按键拦截点）
    }

    /// <summary>失焦本身也立即生效：此时 <c>ISACTIVE</c>=0，且原版门卫让按键一律读 0。</summary>
    [Fact]
    public async Task Focus_loss_is_visible_to_ISACTIVE_and_gates_keys()
    {
        var host = new DefaultInputHost(InputCapability.KeyboardState | InputCapability.Focus);
        host.SubmitKey(65, true);
        host.NotifyFocusChanged(false);

        var screen = await RunAsync(host);

        Assert.Contains("A=0", screen);
        Assert.Contains("K=0", screen);
        Assert.Contains("T1=0", screen);
    }

    /// <summary>
    /// 🔒 <b>失焦清键必须一并归零「上次读到的版本号」</b>，否则重新聚焦后的首次按下会
    /// <b>漏掉一次触发</b>：清键让 <c>version</c> 回到 0，残留的 <c>_lastRead</c>=1 会让
    /// 按下后的 <c>version</c>=1 被判成「与上次读到相同」。
    /// <para>
    /// 实现里 <c>_lastRead</c> 是宿主私有字段，脚本侧观测不到 ⇒ 只能直接驱动宿主断言。
    /// </para>
    /// </summary>
    [Fact]
    public void Focus_cycle_does_not_swallow_the_next_trigger()
    {
        var host = new DefaultInputHost(InputCapability.KeyboardState | InputCapability.Focus);

        host.SubmitKey(65, true);
        Assert.True(host.ConsumeTrigger(65));      // 引擎读过一次 ⇒ _lastRead[65] = 1

        host.NotifyFocusChanged(false);            // 失焦：清键 + 清 _lastRead
        host.NotifyFocusChanged(true);

        host.SubmitKey(65, true);                  // 重新聚焦后按下

        Assert.True(
            host.ConsumeTrigger(65),
            "失焦清键后首次按下必须仍能触发（_lastRead 未清零时会漏一次）");
    }

    /// <summary>
    /// 失焦**不**抹掉悬停值 —— 它是「指针在哪」而不是「窗口活不活跃」，
    /// 且 <c>MOUSEB</c> 无 <c>IsActive</c> 门卫（与原版一致：原版鼠标位置照常有值）。
    /// 清键只清按键，不要顺手动别的字段。
    /// </summary>
    [Fact]
    public async Task Focus_loss_keeps_hover_value()
    {
        var host = new DefaultInputHost(InputCapability.Hover | InputCapability.Focus);
        host.SubmitHover("42");
        host.NotifyFocusChanged(false);

        var screen = await RunAsync(host);

        Assert.Contains("A=0", screen);
        Assert.Contains("M=[42]", screen);
    }
}
