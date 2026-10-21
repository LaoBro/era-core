using System;
using System.IO;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// GameConfigService 单元测试（重构补测）：默认值读取、窗口元信息计算（issue 12）、
/// Reload 引用替换（issue 05 热替换）。
/// </summary>
public class GameConfigServiceTests
{
    private static GameConfigService CreateService()
    {
        return new GameConfigService(new ConfigData());
    }

    /// <summary>临时游戏目录模板：建目录 → 跑用例 → finally 删除。</summary>
    private static void WithTempGameDir(Action<string> action)
    {
        var gameDir = Path.Combine(Path.GetTempPath(), "emuera_tmp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gameDir);
        try
        {
            action(gameDir);
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void GetValue_reads_default_maxlog()
    {
        var svc = CreateService();
        Assert.Equal(5000, svc.GetValue<int>(ConfigCode.MaxLog));
    }

    [Fact]
    public void GetValue_reads_default_font_size()
    {
        var svc = CreateService();
        Assert.Equal(18, svc.GetValue<int>(ConfigCode.FontSize));
    }

    [Fact]
    public void Window_metrics_matches_cli_derivation()
    {
        var svc = CreateService();
        // 默认配置：WindowX=760, WindowY=480, FontSize=18, LineHeight=19, FontName="ＭＳ ゴシック"
        // shapeShift = Max(2, 18/6=3) = 3；charWidth = Max(18/2=9, 1) = 9
        // gameColumns = (760 - 3) / 9 = 84；visibleRows = 480 / 19 = 25
        var m = svc.GetWindowMetrics();

        Assert.Equal(760, m.WindowWidth);
        Assert.Equal(18, m.FontSize);
        Assert.Equal(19, m.LineHeight);
        Assert.Equal(84, m.GameColumns);
        Assert.Equal("ＭＳ ゴシック", m.FontName);
        Assert.Equal(25, m.VisibleRows);
        // 默认文字色（文字色 = LIGHTGRAY）——渲染端靠它画「color 为 null」的普通文字段
        Assert.Equal("#C0C0C0", m.ForeColor);
    }

    /// <summary>
    /// 游戏 emuera.config 改「文字色」时 foreColor 随之变化——前端因而能画出游戏
    /// 自定义的默认文字色（原版 Emuera / CLI 一直如此，Web/MAUI 此前落回主题色）。
    /// </summary>
    [Fact]
    public void Window_metrics_fore_color_follows_game_config()
    {
        WithTempGameDir(gameDir =>
        {
            // 颜色三源格式：R, G, B（见 ConfigItem.tryStringsToColor）
            File.WriteAllText(
                Path.Combine(gameDir, "emuera.config"),
                "; test\n文字色 : 127, 0, 0\n",
                System.Text.Encoding.UTF8);

            var svc = CreateService();
            svc.Reload(gameDir);

            Assert.Equal("#7F0000", svc.GetWindowMetrics().ForeColor);
        });
    }

    [Fact]
    public void Window_metrics_visible_rows_has_floor_guard()
    {
        // 行高大于窗高的异常配置也有下限 1，不产出 0（agent 侧用 pageRows 乘页数）。
        WithTempGameDir(gameDir =>
        {
            File.WriteAllText(
                Path.Combine(gameDir, "emuera.config"),
                "; test\nウィンドウ高さ : 10\n一行の高さ : 50\n",
                System.Text.Encoding.UTF8);

            var svc = CreateService();
            svc.Reload(gameDir);

            Assert.Equal(1, svc.GetWindowMetrics().VisibleRows);
        });
    }

    [Fact]
    public void Reload_replaces_current_reference()
    {
        var svc = CreateService();
        var before = svc.Current;
        var gameDir = SessionRegistryTests.FindTestGameDir();

        var reloaded = svc.Reload(gameDir);

        Assert.NotSame(before, reloaded);
        Assert.Same(reloaded, svc.Current);
    }

    [Fact]
    public void Current_is_same_instance_until_reload()
    {
        var svc = CreateService();
        var first = svc.Current;
        var second = svc.Current;
        Assert.Same(first, second);
    }

    /// <summary>
    /// --no-loading-report 覆盖：临时游戏目录的 emuera.config 把 DisplayReport 置 true，
    /// Reload 带 overrideDisplayReport=false（强制关）时被压回 false；不带（null）时保持 true。
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(null, true)]
    public void Reload_override_display_report_off(bool? overrideReport, bool expected)
    {
        WithTempGameDir(gameDir =>
        {
            // 配置行格式：显示名 : 值（; 是注释）。DisplayReport 显示名见 ConfigData.setDefault。
            File.WriteAllText(
                Path.Combine(gameDir, "emuera.config"),
                "; test\nロード時にレポートを表示する : TRUE\n",
                System.Text.Encoding.UTF8);

            var svc = new GameConfigService(new ConfigData(), overrideReport);
            var reloaded = svc.Reload(gameDir);

            Assert.Equal(expected, reloaded.GetConfigValue<bool>(ConfigCode.DisplayReport));
        });
    }

    /// <summary>
    /// --loading-report 覆盖：emuera.config 未开启 DisplayReport（默认 false）时，
    /// Reload 带 overrideDisplayReport=true（强制开）被抬起为 true——time.log 阶段计时的入口。
    /// </summary>
    [Fact]
    public void Reload_override_display_report_on()
    {
        WithTempGameDir(gameDir =>
        {
            File.WriteAllText(
                Path.Combine(gameDir, "emuera.config"),
                "; test\n",
                System.Text.Encoding.UTF8);

            var svc = new GameConfigService(new ConfigData(), displayReportOverride: true);
            var reloaded = svc.Reload(gameDir);

            Assert.True(reloaded.GetConfigValue<bool>(ConfigCode.DisplayReport));
        });
    }
}

/// <summary>
/// OutputHub 单元测试（重构补测）：Subscribe/Publish/Unsubscribe/Complete 行为与幂等性。
/// </summary>
public class OutputHubTests
{
    [Fact]
    public async Task Subscribe_receives_published_turns()
    {
        var hub = new OutputHub();
        var reader = hub.Subscribe();

        hub.Publish(TestTurnFactory.Wire("a"));
        hub.Publish(TestTurnFactory.Wire("b"));
        hub.Complete();

        Assert.Equal("a", (await reader.ReadAsync()).Wire);
        Assert.Equal("b", (await reader.ReadAsync()).Wire);
    }

    [Fact]
    public async Task Unsubscribe_stops_delivery_and_completes_reader()
    {
        var hub = new OutputHub();
        var r1 = hub.Subscribe();
        var r2 = hub.Subscribe();

        hub.Publish(TestTurnFactory.Wire("x"));
        hub.Unsubscribe(r1);
        hub.Publish(TestTurnFactory.Wire("y"));
        hub.Complete();

        // r1 只收到退订前的 x；之后再读抛 ChannelClosedException
        Assert.Equal("x", (await r1.ReadAsync()).Wire);
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await r1.ReadAsync());

        // r2 收到全部
        Assert.Equal("x", (await r2.ReadAsync()).Wire);
        Assert.Equal("y", (await r2.ReadAsync()).Wire);
    }

    [Fact]
    public async Task Subscribe_after_complete_returns_completed_reader()
    {
        var hub = new OutputHub();
        hub.Complete();

        var reader = hub.Subscribe();

        Assert.False(reader.TryRead(out _));
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await reader.ReadAsync());
    }

    [Fact]
    public void Complete_is_idempotent()
    {
        var hub = new OutputHub();
        hub.Subscribe();
        hub.Complete();
        hub.Complete(); // 不抛
    }

    [Fact]
    public void Unsubscribe_unknown_reader_is_noop()
    {
        var hub = new OutputHub();
        var r1 = hub.Subscribe();
        var r2 = hub.Subscribe();

        hub.Unsubscribe(r1);
        hub.Unsubscribe(r1); // 重复退订：安全无操作
        hub.Complete();
        hub.Unsubscribe(r2); // 已 Complete 后退订：安全无操作
    }
}
