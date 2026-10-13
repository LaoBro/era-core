using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Tests;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 **M3**：<c>ConsoleInputHandler.MoveMouse</c> 的视口来源断言。
/// <para>
/// ⚠️ <b>本类不开类级 <c>GlobalStatic</c> scope</b> —— <c>LoaderTestHarness</c>
/// 构造器自己开，而 <c>OpenScope</c> 不允许在同一 async 上下文嵌套
/// （会抛 <c>InvalidOperationException</c>）。视口侧的纯契约测试见
/// <see cref="ViewportDecouplingTests"/>。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public sealed class MoveMouseViewportTests
{
    private const string ErbFileName = "VIEWPORT.ERB";

    private const string ProbeErb = """
        @SYSTEM_TITLE
        PRINTFORML A
        PRINTFORML B
        PRINTBUTTON "42", 42
        PRINTFORML C
        QUIT
        """;

    /// <summary>
    /// 核心断言：<c>MoveMouse</c> 的三个视口量全部取自 <see cref="IViewportProvider"/>。
    /// <para>
    /// 改动前它读 <c>ScrollBar.Value</c> / <c>MainPicBox.Height</c> / <c>Config.LineHeight</c>，
    /// <see cref="RecordingViewport"/> 的三个计数会全为 0。这是接缝级断言（不依赖具体几何），
    /// 也是防今后再度直读 UI 控件的回归锚点。
    /// </para>
    /// </summary>
    [Fact]
    public async Task MoveMouse_reads_all_three_viewport_quantities()
    {
        var probe = new RecordingViewport();

        using var harness = new LoaderTestHarness(viewport: probe);
        probe.VisibleHeight = Config.WindowY;
        probe.LineHeight = Config.LineHeight;

        await RunProbeAsync(harness);
        harness.Console.MoveMouse(new(0, 400));

        Assert.True(probe.ScrollOffsetReads > 0, "MoveMouse 应读 IViewportProvider.ScrollOffset");
        Assert.True(probe.VisibleHeightReads > 0, "MoveMouse 应读 IViewportProvider.VisibleHeight");
        Assert.True(probe.LineHeightReads > 0, "MoveMouse 应读 IViewportProvider.LineHeight");
    }

    /// <summary>
    /// 滚动量参与命中判定：<c>ScrollOffset</c> 超出内容行数 ⇒ 可视区底部落在第 0 行之上
    /// ⇒ 扫描循环一次都不进 ⇒ 无命中（而非抛异常或命中错误行）。
    /// <para>
    /// ⚠️ <b>为什么没有配对的「offset=0 应命中」断言</b>：headless 无字体测量，
    /// 按钮 <c>Width</c> 退化为 0，于是 <c>pointX == PointX</c> 命中<b>每一行</b> ——
    /// 命中结果不反映真实几何，写出来只会锁死一个退化行为。真正可靠的「滚动影响命中」
    /// 端到端验收要等 M4（CLI 打通 <c>Geometry</c>）在真机上做。
    /// </para>
    /// </summary>
    [Fact]
    public async Task MoveMouse_with_offset_beyond_content_hits_nothing()
    {
        var probe = new RecordingViewport { ScrollOffset = int.MaxValue / 4 };

        using var harness = new LoaderTestHarness(viewport: probe);
        probe.VisibleHeight = Config.WindowY;
        probe.LineHeight = Config.LineHeight;

        await RunProbeAsync(harness);
        harness.Console.MoveMouse(new(0, 400));

        Assert.Null(harness.Console.PointingSring);
    }

    /// <summary>宿主把 <c>LineHeight</c> 报成 0 时不得除零崩溃（回退 <c>Config.LineHeight</c>）。</summary>
    [Fact]
    public async Task MoveMouse_survives_zero_line_height()
    {
        var probe = new RecordingViewport();

        using var harness = new LoaderTestHarness(viewport: probe);
        probe.VisibleHeight = Config.WindowY;
        probe.LineHeight = 0;

        await RunProbeAsync(harness);
        harness.Console.MoveMouse(new(0, 400));     // 不得抛 DivideByZeroException
    }

    private static async Task RunProbeAsync(LoaderTestHarness harness)
    {
        // ⚠️ 构造陷阱（同 TimedWaitInputSwallowedTests）：harness 构造器同步开 GlobalStatic
        // scope，AsyncLocal 不跨 await 上浮 ⇒ 必须在测试方法体内 new（已在各测试方法完成）。
        harness.WriteErb(ErbFileName, ProbeErb);
        await harness.InitializeAsync();
        harness.Console._state.process = harness.Process;
        harness.Console.RunEmueraProgram("");
    }

    /// <summary>
    /// 记录型视口：三个量各读几次。显式接口实现与 public setter 同名并存 ——
    /// 引擎经接口读取走显式实现（计数），测试经 public 属性写入（设值）。
    /// </summary>
    private sealed class RecordingViewport : IViewportProvider
    {
        public int ScrollOffsetReads { get; private set; }
        public int VisibleHeightReads { get; private set; }
        public int LineHeightReads { get; private set; }

        public int ScrollOffset { get; set; }
        public int VisibleHeight { get; set; }
        public int LineHeight { get; set; }

        int IViewportProvider.ScrollOffset { get { ScrollOffsetReads++; return ScrollOffset; } }
        int IViewportProvider.VisibleHeight { get { VisibleHeightReads++; return VisibleHeight; } }
        int IViewportProvider.LineHeight { get { LineHeightReads++; return LineHeight; } }
    }
}
