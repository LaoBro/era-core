using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0031 / spec-v3 §5.1「上行输入翻译」的边界测试 —— <see cref="InputStateApplier"/>。
/// <para>
/// <b>为什么单独开这个文件</b>：这段翻译原先是 <c>AgentJsonlProtocol</c> 的私有实例方法
/// <c>ApplyInputState</c>，只有起真实会话（Kestrel + Session + 脚本）才能覆盖到。
/// 现存唯一的接线测试 <c>KestrelGameServerEndpointTests.Input_accepts_state_only_frame_with_200</c>
/// 只断言 <c>200 / received:true</c>，<b>从不</b>断言宿主状态 —— 即「wire 字段有没有真的
/// 写进 <see cref="IInputHost"/>」此前完全没有守护。本文件补的就是这一层。
/// </para>
/// <para>
/// 2026-10-19（候选 5 深化）：模块下移到输入侧，断言补齐<b>能力位门卫</b>。
/// 2026-10-21（issue-002）：CLI 指针入口也改走 <see cref="InputPort"/>，指针专用入口已删除；
/// 指针形状的用例见 <c>InputPortTests</c>。
/// </para>
/// </summary>
public sealed class InputStateApplierTests
{
    /// <summary>Server / MAUI 宿主的口径（<c>Session.cs</c>）：无 <c>Geometry</c>（Web 尚无坐标语义）。</summary>
    private static DefaultInputHost NewServerLikeHost()
        => new(InputCapability.KeyboardState | InputCapability.Hover | InputCapability.Focus);

    /// <summary>CLI 宿主的口径（<c>HeadlessRunner.cs</c>）：几何 + 键盘，无悬停 / 焦点。</summary>
    private static DefaultInputHost NewCliLikeHost()
        => new(InputCapability.Geometry | InputCapability.KeyboardState);

    private static DefaultInputHost NewAllCapableHost()
        => new(InputCapability.Geometry | InputCapability.Hover | InputCapability.KeyboardState | InputCapability.Focus);

    // === 空操作（旧客户端行为不变）===

    [Fact]
    public void Empty_event_is_no_op()
    {
        var host = NewServerLikeHost();

        InputStateApplier.Apply(InputFrame.Empty, host);

        Assert.Same(InputSnapshot.Empty, host.Current);
    }

    [Fact]
    public void NullInputHost_discards_every_state_field()
    {
        var host = NullInputHost.Instance;

        InputStateApplier.Apply(
            new InputFrame(
                HoverValue: "42",
                Keys: new[] { new InputKeyEvent(65, true) },
                Geometry: new InputGeometry(1, 2),
                Focus: false),
            host);

        // 无能力即无效果：宿主不该被写出任何状态（骨架阶段的可观测不变式）
        Assert.Same(InputSnapshot.Empty, host.Current);
    }

    // === 字段映射 ===

    [Fact]
    public void Keys_are_written_as_level_and_release_clears_it()
    {
        var host = NewServerLikeHost();

        InputStateApplier.Apply(new InputFrame(Keys: new[] { new InputKeyEvent(65, true) }), host);
        Assert.True(host.Current.Keys.IsDown(65));

        // 松开：前端分帧上报 up，本模块不合成、不补全 —— 只做「这一帧写进宿主」
        InputStateApplier.Apply(new InputFrame(Keys: new[] { new InputKeyEvent(65, false) }), host);
        Assert.False(host.Current.Keys.IsDown(65));
    }

    [Fact]
    public void Out_of_range_key_codes_are_dropped()
    {
        var host = NewServerLikeHost();

        InputStateApplier.Apply(
            new InputFrame(Keys: new[]
            {
                new InputKeyEvent(-1, true),      // 下越界
                new InputKeyEvent(256, true),     // 上越界（KeyStateSet 只有 256 槽）
                new InputKeyEvent(65, true),      // 唯一合法项
                null!,                            // 反序列化可能留下的空元素
            }),
            host);

        Assert.True(host.Current.Keys.IsDown(65));
        Assert.False(host.Current.Keys.IsDown(0));
        Assert.False(host.Current.Keys.IsDown(255));
    }

    [Fact]
    public void Geometry_is_written_as_cursor_position()
    {
        var host = NewAllCapableHost();

        InputStateApplier.Apply(new InputFrame(Geometry: new InputGeometry(120, 100)), host);

        // console 局部坐标、左上基准（ADR-0031 §8）——翻译只做搬运，不做换算
        Assert.Equal(new EmuPoint(120, 100), host.Current.CursorPosition);
    }

    [Fact]
    public void Hover_value_is_written_verbatim()
    {
        var host = NewServerLikeHost();

        InputStateApplier.Apply(new InputFrame(HoverValue: "42"), host);

        Assert.Equal("42", host.Current.HoverValue);
    }

    // === 能力位门卫（wire 与指针两个入口同口径）===

    /// <summary>
    /// 2026-10-19 统一：此前 wire 入口<b>不判</b>能力位（只靠读侧门卫兜底），
    /// 指针入口判。未声明 <c>Geometry</c> 的 Server 宿主收到 geometry 会把它存进快照 ——
    /// 无人读，但快照里留着一份假状态。现在两侧一致：不通电的通道直接丢弃。
    /// </summary>
    [Fact]
    public void Geometry_is_dropped_when_capability_not_declared()
    {
        var host = NewServerLikeHost();   // 无 Geometry

        InputStateApplier.Apply(new InputFrame(Geometry: new InputGeometry(120, 100)), host);

        Assert.Null(host.Current.CursorPosition);
    }

    [Fact]
    public void Hover_is_dropped_when_capability_not_declared()
    {
        var host = NewCliLikeHost();      // 无 Hover

        InputStateApplier.Apply(new InputFrame(HoverValue: "42"), host);

        Assert.Null(host.Current.HoverValue);
    }

    [Fact]
    public void Keys_are_dropped_when_capability_not_declared()
    {
        var host = new DefaultInputHost(InputCapability.Focus);   // 无 KeyboardState

        InputStateApplier.Apply(new InputFrame(Keys: new[] { new InputKeyEvent(65, true) }), host);

        Assert.False(host.Current.Keys.IsDown(65));
    }

    [Fact]
    public void Focus_is_dropped_when_capability_not_declared()
    {
        var host = NewCliLikeHost();      // 无 Focus

        InputStateApplier.Apply(new InputFrame(Focus: false), host);

        Assert.True(host.Current.Focused);   // 快照默认 true，未被改写
    }

    // === 组合帧与顺序 ===

    [Fact]
    public void All_state_fields_in_one_event_are_applied()
    {
        var host = NewAllCapableHost();

        InputStateApplier.Apply(
            new InputFrame(
                HoverValue: "42",
                Keys: new[] { new InputKeyEvent(65, true) },
                Geometry: new InputGeometry(3, 4),
                Focus: true),
            host);

        var snap = host.Current;
        Assert.Equal("42", snap.HoverValue);
        Assert.Equal(new EmuPoint(3, 4), snap.CursorPosition);
        Assert.True(snap.Keys.IsDown(65));
        Assert.True(snap.Focused);
    }

    /// <summary>
    /// 失焦帧必须清键 —— 这是「幽灵按键」防线的宿主侧一半。
    /// <para>
    /// 前端 <c>useKeyMouseInput.reportFocusLost</c> 把 <c>focus:false</c> 单独发一帧；
    /// 宿主收到后清键 + 清 <c>_lastRead</c>，否则浏览器吞掉后续 keyup 后电平永久卡在 1。
    /// </para>
    /// </summary>
    [Fact]
    public void Focus_loss_event_clears_key_level()
    {
        var host = NewServerLikeHost();
        InputStateApplier.Apply(new InputFrame(Keys: new[] { new InputKeyEvent(65, true) }), host);
        Assert.True(host.Current.Keys.IsDown(65));

        InputStateApplier.Apply(new InputFrame(Focus: false), host);

        Assert.False(host.Current.Keys.IsDown(65));
        Assert.False(host.Current.Focused);
    }

    // === wire → 输入侧的形状转换 ===

    [Fact]
    public void JsonlCommand_ToFrame_carries_every_field()
    {
        var cmd = new JsonlCommand("input", "1", "42", new[] { new InputKeyEvent(65, true) }, new InputGeometry(3, 4), false);

        var e = cmd.ToFrame();

        Assert.Equal("1", e.Value);
        Assert.Equal("42", e.HoverValue);
        Assert.Equal(new InputGeometry(3, 4), e.Geometry);
        Assert.False(e.Focus!.Value);
        Assert.Single(e.Keys!);
        Assert.Equal(65, e.Keys![0].code);
        Assert.True(e.HasValue);
        Assert.True(e.HasState);
    }

    /// <summary>
    /// HTTP 结构化 body 与协议帧走同一个 <see cref="InputFrame.FromWire"/> ——
    /// 这是「不存在第二份 wire → 输入状态转换」的可执行断言。
    /// </summary>
    [Fact]
    public void Structured_http_input_builds_the_same_frame()
    {
        var input = new HttpRouteDispatcher.HttpInput
        {
            value = "1",
            hoverValue = "42",
            keys = new[] { new InputKeyEvent(65, true) },
            geometry = new InputGeometry(3, 4),
            focus = false,
        };

        var e = InputFrame.FromWire(
            input.value, input.hoverValue, input.keys, input.geometry, input.focus);

        Assert.Equal("1", e.Value);
        Assert.Equal("42", e.HoverValue);
        Assert.Equal(new InputGeometry(3, 4), e.Geometry);
        Assert.False(e.Focus!.Value);
        Assert.Single(e.Keys!);
        Assert.True(e.HasValue);
        Assert.True(e.HasState);
    }

    /// <summary>
    /// 上行帧的 wire 键名必须<b>扁平</b> —— 前端 <c>InputExtra</c> 与 HTTP body 都按这套键名发送，
    /// 把四个状态字段收进嵌套对象就会改 wire。本例是形状的回归锚点。
    /// </summary>
    [Fact]
    public void Serialized_input_frame_uses_flat_wire_keys()
    {
        var json = JsonSerializer.Serialize(
            new JsonlCommand("input", "1", "42", new[] { new InputKeyEvent(65, true) }, new InputGeometry(3, 4), true),
            EmueraJsonContext.Default.JsonlCommand);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(
            new[] { "focus", "geometry", "hoverValue", "keys", "type", "value" },
            root.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());

        Assert.Equal(65, root["keys"]![0]!["code"]!.GetValue<int>());
        Assert.Equal(3, root["geometry"]!["x"]!.GetValue<int>());
    }

    /// <summary>
    /// 序列化用的是 <c>EmueraJsonContext</c>（<c>WhenWritingNull</c>）⇒ 未携带的可选字段
    /// 不落 wire。这是「改手拼为序列化」不产生 wire 漂移的前提。
    /// </summary>
    [Fact]
    public void Serialized_input_frame_omits_absent_optional_fields()
    {
        var json = JsonSerializer.Serialize(new JsonlCommand("input", "1"), EmueraJsonContext.Default.JsonlCommand);

        Assert.Equal("{\"type\":\"input\",\"value\":\"1\"}", json);
    }
}
