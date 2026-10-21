using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Tests;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 **M4** 脚本侧测试：<c>MOUSEX</c> / <c>MOUSEY</c> / <c>MOUSEB</c>。
/// <para>
/// ⚠️ <b>本类不开类级 <c>GlobalStatic</c> scope</b> —— <c>LoaderTestHarness</c> 构造器自己开，
/// 而 <c>OpenScope</c> 不允许在同一 async 上下文嵌套。纯接口级的断言见
/// <see cref="ViewportDecouplingTests"/>。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public sealed class MouseGeometryTests
{
    private const string ErbFileName = "MOUSEGEO.ERB";

    // ⚠️ 无参系统函数必须带括号：`{MOUSEX}` 会报「関数名"MOUSEX"が変数のように使われています」，
    //    `MOUSEB()` 是字符串函数故须 %…%（与 InputHostKeyboardTests 的 ISACTIVE() 同坑）。
    private const string ProbeErb = """
        @SYSTEM_TITLE
        PRINTFORML X={MOUSEX()}
        PRINTFORML Y={MOUSEY()}
        PRINTFORML M=[%MOUSEB()%]
        QUIT
        """;

    /// <summary>
    /// 跑探针脚本，返回画面文本与 <c>Config.WindowY</c>。
    /// <para>
    /// ⚠️ <c>WindowY</c> 必须在 harness 构造<b>之后</b>同步读取 —— harness 构造器才开
    /// <c>GlobalStatic</c> scope，测试方法体内在它之前读 <c>Config</c> 会 NRE。
    /// </para>
    /// </summary>
    private static async Task<(string Screen, int WindowY)> RunAsync(IInputHost? host)
    {
        // ⚠️ 构造陷阱（同 TimedWaitInputSwallowedTests）：harness 构造器同步开 GlobalStatic
        // scope，AsyncLocal 不跨 await 上浮 ⇒ 必须在测试方法体内 new。
        using var harness = new LoaderTestHarness(inputHost: host);
        int windowY = Config.WindowY;
        harness.WriteErb(ErbFileName, ProbeErb);
        await harness.InitializeAsync();
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");
        var screen = string.Join("\n", harness.Console.DisplayLineList.Select(l => l?.ToString() ?? ""));
        return (screen, windowY);
    }

    // ---------------------------------------------------------------
    // ① 无 Geometry 能力 ⇒ 降级为恒 0 / ""（spec-v3 §3.5）
    // ---------------------------------------------------------------
    [Fact]
    public async Task No_geometry_capability_degrades_to_zero()
    {
        var (screen, _) = await RunAsync(new DefaultInputHost(InputCapability.None));

        Assert.Contains("X=0", screen);
        Assert.Contains("Y=0", screen);
        Assert.Contains("M=[]", screen);
    }

    // ---------------------------------------------------------------
    // ② 坐标基准换算（M0 标定的核心不变量）
    // ---------------------------------------------------------------

    /// <summary>
    /// 🔒 <b>回归锚点</b>：宿主上报**左上基准**（<c>0..H</c>），
    /// <c>MOUSEX/Y</c> 输出**左下基准**（<c>MY = rawY − H</c>）⇒ 窗口内 <c>MY ≤ 0</c>。
    /// <para>
    /// spec-v3 明写「M4 不得先于 M0」—— 坐标系搞反会让原生自绘整体镜像偏移，
    /// 本测试就是那句话的机器检查。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Geometry_is_converted_to_bottom_left_origin()
    {
        var host = new DefaultInputHost(InputCapability.Geometry);
        host.SubmitGeometry(new EmuPoint(120, 100));

        var (screen, h) = await RunAsync(host);

        Assert.Contains("X=120", screen);
        Assert.Contains($"Y={100 - h}", screen);   // 100 − 480 = −380
    }

    /// <summary>
    /// 越界坐标**不钳制**：原版在鼠标移出窗口后仍返回真实换算值
    /// （M0 标定：屏幕四角实测 <c>MX ∈ {-660, 1231, …}</c>）。引擎只做命中判定、不做内外判定。
    /// </summary>
    [Fact]
    public async Task Geometry_out_of_bounds_is_not_clamped()
    {
        var host = new DefaultInputHost(InputCapability.Geometry);
        host.SubmitGeometry(new EmuPoint(-660, 1231));

        var (screen, h) = await RunAsync(host);

        Assert.Contains("X=-660", screen);
        Assert.Contains($"Y={1231 - h}", screen);
    }

    // ---------------------------------------------------------------
    // ③ MOUSEB 的两条通道（spec-v3 §3.5 降级矩阵）
    // ---------------------------------------------------------------

    /// <summary>
    /// Geometry 有能力但坐标在可绘制区外 ⇒ 不跑 <c>MoveMouse</c> ⇒ 回落悬停值。
    /// <para>
    /// 同时守住「<b>不读陈旧 <c>PointingSring</c></b>」：无 Geometry 时 <c>MoveMouse</c>
    /// 从不调用，那个字段会保留上一轮结果，被误当成悬停值。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Geometry_outside_client_rect_falls_back_to_hover()
    {
        var host = new DefaultInputHost(InputCapability.Geometry | InputCapability.Hover);
        host.SubmitGeometry(new EmuPoint(5000, 5000));   // 远超 ClientRectangle(0,0,760,480)
        host.SubmitHover("42");

        var (screen, _) = await RunAsync(host);

        Assert.Contains("M=[42]", screen);
    }

    /// <summary>
    /// 🔒 <b>回归锚点</b>：声明了 Geometry 但**从未上报坐标** ⇒ 不得拿 <c>(0,0)</c> 去命中。
    /// <para>
    /// 这正是 M1 修掉的「幽灵命中」：两处 pull 点原本恒用 <c>GetCursorPosition()</c>
    /// （headless 下是 <see cref="EmuPoint.Empty"/>）调 <c>MoveMouse</c>。M4 让 CLI 有了
    /// Geometry 能力，若只判能力位不判坐标非空，该问题会在 CLI 上复活。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Geometry_capability_without_coordinates_reads_empty()
    {
        var host = new DefaultInputHost(InputCapability.Geometry);   // 从未 SubmitGeometry

        var (screen, _) = await RunAsync(host);

        Assert.Contains("M=[]", screen);
    }

    /// <summary>无 Geometry 能力 ⇒ 走悬停值通道，不跑命中判定。</summary>
    [Fact]
    public async Task Without_geometry_MOUSEB_reads_hover_value()
    {
        var host = new DefaultInputHost(InputCapability.Hover);
        host.SubmitHover("42");

        var (screen, _) = await RunAsync(host);

        Assert.Contains("M=[42]", screen);
    }
}
