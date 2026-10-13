using System;
using MinorShift.Emuera;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// ADR-0031 / spec-v3 **M1 骨架**接缝测试。
/// <para>
/// M1 的验收判据是「可观测行为零变化」：骨架只新增接口 + 能力声明 + 一处门卫，
/// 不接任何真实输入源。本文件断言的正是**这个不变量** —— 默认宿主无任何能力，
/// 因此所有降级路径退化为骨架前的恒值行为。
/// </para>
/// </summary>
public class InputHostSkeletonTests : IDisposable
{
    private readonly IDisposable _scope;

    public InputHostSkeletonTests() => _scope = GlobalStatic.OpenScope(new ConfigData());

    public void Dispose() => _scope.Dispose();

    // === 能力位 ===

    [Fact]
    public void NullInputHost_DeclaresNoCapabilities()
    {
        Assert.Equal(InputCapability.None, NullInputHost.Instance.Capabilities.Flags);

        foreach (var bit in new[]
                 {
                     InputCapability.Geometry, InputCapability.Hover,
                     InputCapability.KeyboardState, InputCapability.Focus,
                 })
        {
            Assert.False(NullInputHost.Instance.Capabilities.Has(bit));
        }
    }

    [Fact]
    public void InputCapabilities_Has_IsBitwise()
    {
        var caps = new InputCapabilities(InputCapability.Geometry | InputCapability.Hover);

        Assert.True(caps.Has(InputCapability.Geometry));
        Assert.True(caps.Has(InputCapability.Hover));
        Assert.False(caps.Has(InputCapability.KeyboardState));
        Assert.False(caps.Has(InputCapability.Focus));
    }

    // === 快照 ===

    [Fact]
    public void NullInputHost_SnapshotIsEmpty()
    {
        var snap = NullInputHost.Instance.Current;

        Assert.Null(snap.CursorPosition);
        Assert.Null(snap.HoverValue);
    }

    [Fact]
    public void InputSnapshot_IsImmutable_WithProducesNewInstance()
    {
        var a = InputSnapshot.Empty;
        var b = a with { HoverValue = "0", CursorPosition = new EmuPoint(3, 4) };

        // 不可变：原实例不受影响，且是不同引用（宿主用 Volatile 整体换引用，ADR-0031 决策 2）
        Assert.Null(a.HoverValue);
        Assert.Null(a.CursorPosition);
        Assert.Equal("0", b.HoverValue);
        Assert.Equal(new EmuPoint(3, 4), b.CursorPosition);
        Assert.NotSame(a, b);
    }

    [Fact]
    public void NullInputHost_PushMethods_AreNoOps()
    {
        // 骨架阶段 push 无副作用 —— 变异测试：任一方法产生状态即应转红
        var host = NullInputHost.Instance;

        host.SubmitValue("1");
        host.SubmitHover("0");
        host.SubmitGeometry(new EmuPoint(10, 20));
        host.NotifyFocusChanged(false);

        Assert.Same(InputSnapshot.Empty, host.Current);
        Assert.Equal(InputCapability.None, host.Capabilities.Flags);
    }

    // === 宿主装配 ===

    [Fact]
    public void HeadlessConsole_DefaultsToNullInputHost()
    {
        var ui = new HeadlessConsole();

        Assert.Same(NullInputHost.Instance, ui.Input);
        Assert.False(ui.Input.Capabilities.Has(InputCapability.Geometry));
    }

    [Fact]
    public void HeadlessConsole_HonorsInjectedInputHost()
    {
        var injected = new StubInputHost(new InputCapabilities(InputCapability.Geometry | InputCapability.Hover));
        var ui = new HeadlessConsole(injected);

        Assert.Same(injected, ui.Input);
        Assert.True(ui.Input.Capabilities.Has(InputCapability.Geometry));
    }

    // === 视口（ADR-0031 决策 4）===

    [Fact]
    public void HeadlessConsole_Viewport_MatchesConfigAndPictureBox()
    {
        var ui = new HeadlessConsole();

        // M0 标定：H == Config.WindowY（ADR-0031 §8）。M3 起 MoveMouse 只读 Viewport，
        // 故它必须与旧视口源 MainPicBox.Height / Config.LineHeight 同源，否则引入行为差异。
        Assert.Equal(Config.WindowY, ui.Viewport.VisibleHeight);
        Assert.Equal(ui.ClientHeight, ui.Viewport.VisibleHeight);
        Assert.Equal(ui.MainPicBox.Height, ui.Viewport.VisibleHeight);
        Assert.Equal(Config.LineHeight, ui.Viewport.LineHeight);
    }

    [Fact]
    public void HeadlessConsole_Viewport_ScrollOffsetIsDistanceFromBottom()
    {
        var ui = new HeadlessConsole();

        // M3：语义由「ScrollBar.Value（底行号+1）」改为「距底偏移」。
        // 后者才是不依赖总行数的宿主可答复量（ScrollController 的 lineCount 全是参数）。
        // 换算 Maximum - Value：Maximum 由 VerticalScrollBarUpdate 维护为 displayLineList.Count。
        ui.ScrollBar.Maximum = 100;
        ui.ScrollBar.Value = 100;              // 跟随底部
        Assert.Equal(0, ui.Viewport.ScrollOffset);

        ui.ScrollBar.Value = 92;               // 回看 8 行
        Assert.Equal(8, ui.Viewport.ScrollOffset);
    }

    [Fact]
    public void HeadlessConsole_Viewport_IsCachedAtConstruction()
    {
        // 与 ClientWidth/ClientHeight 同策略：构造时缓存，避免 scope 外读 Config 触发 NRE
        var ui = new HeadlessConsole();
        int h = ui.Viewport.VisibleHeight;
        int lh = ui.Viewport.LineHeight;

        Assert.Equal(h, ui.Viewport.VisibleHeight);
        Assert.Equal(lh, ui.Viewport.LineHeight);
    }

    // === M1 门卫的不变量 ===

    [Fact]
    public void DefaultHost_HasNoGeometry_SoPullPointsSkipHitTest()
    {
        // 两处 pull 点（ConsoleInputHandler.EndMacro / DoInputToEmueraProgram）以
        // `Capabilities.Has(Geometry)` 为门卫。默认宿主无该能力 ⇒ MoveMouse 不被调用，
        // 旧「恒用 (0,0) 命中左上角」的幽灵命中消失。
        //
        // 注：端到端断言（PointToClient 零调用）需真实几何宿主，随 M4（CLI 打通 Geometry）落地。
        var ui = new HeadlessConsole();

        Assert.False(ui.Input.Capabilities.Has(InputCapability.Geometry));
        Assert.Null(ui.Input.Current.CursorPosition);
    }

    private sealed class StubInputHost : IInputHost
    {
        public StubInputHost(InputCapabilities caps) => Capabilities = caps;

        public InputCapabilities Capabilities { get; }

        public InputSnapshot Current { get; set; } = InputSnapshot.Empty;

        public void SubmitValue(string value) { }

        public void SubmitHover(string? value) { }

        public void SubmitGeometry(EmuPoint point) { }

        public void NotifyFocusChanged(bool focused) { }

        public void SubmitKey(int keycode, bool down) { }

        public bool ConsumeTrigger(int keycode) => false;

        public void ResetState() { }
    }
}
