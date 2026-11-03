using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EraCore.Hosting;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Sessions;
using Xunit;

namespace EraCore.Hosting.Tests;

/// <summary>
/// 公共面冒烟：只引用 Hosting/Core 公共类型，不依赖 InternalsVisibleTo。
/// 用真实 test_game 证明会话、Metrics、Snapshot、OpenStream、按钮列几何、awaiting 派生可用。
/// </summary>
public sealed class HostingSmokeTests
{
    [Fact]
    public async Task GameHost_starts_real_game_and_exposes_public_surface()
    {
        var gameRoot = FindTestGameDir();
        await using var host = new GameHost(
            new CoreSessionOptions(gameRoot, new FileSystemGameDirAccessor())
            {
                TeardownTimeout = TimeSpan.FromSeconds(15),
            },
            RenderCapability.Text);

        var start = await host.StartAsync();
        Assert.True(start.Succeeded, $"{start.ErrorCode}: {start.ErrorMessage}");
        Assert.Equal(13, HostContract.DataVersion);
        Assert.Equal(1, HostContract.ApiVersion);
        Assert.True(host.Metrics.FontSize > 0);
        Assert.True(host.Metrics.LineHeight > 0);
        Assert.True(host.Metrics.MaxLog > 0);
        Assert.False(host.Capabilities.Has(RenderCapability.Images));

        var snapshot = host.Snapshot;
        Assert.Equal(TurnRecord.CurrentProtocolVersion, snapshot.protocolVersion);
        Assert.True(snapshot.revision > 0);
        Assert.NotEmpty(snapshot.lines);

        using var stream = host.OpenStream();
        Assert.Equal(snapshot.revision, stream.InitialSnapshot.revision);
        Assert.NotNull(stream.Turns);

        // awaiting → 输入界面状态（test_game 标题画面是数字 INPUT）。
        var awaiting = AwaitingView.From(snapshot.awaiting);
        Assert.True(awaiting.IsWaitingForInput);
        Assert.Equal(HostInputMode.Text, awaiting.Mode);
        Assert.True(awaiting.RequiresValue);
        Assert.False(awaiting.AllowsEmpty);

        // 公共 snapshot 行 → 按钮列命中。
        var buttonLine = snapshot.lines.FirstOrDefault(l => l.entries.Any(e => e.button != null));
        Assert.NotNull(buttonLine);
        var hits = ColumnHitGeometry.FromLine(buttonLine!);
        Assert.NotEmpty(hits);
        Assert.True(hits[0].Span.Width > 0);

        // PrintSegment → 样式解析。
        var segment = snapshot.lines
            .SelectMany(l => l.entries)
            .SelectMany(e => e.segments)
            .FirstOrDefault(s => s.text.Length > 0);
        Assert.NotNull(segment);
        var style = StyleResolver.Resolve(segment!, host.Metrics.ForeColor);
        Assert.NotNull(style.Color);

        // 状态等待接口可用；这里只验证不抛。
        Assert.True(await host.WaitForStateAsync(CoreSessionState.WaitingForInput, TimeSpan.FromMilliseconds(100)));
    }

    private static string FindTestGameDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
                return Path.Combine(dir.FullName, "test_game");
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
