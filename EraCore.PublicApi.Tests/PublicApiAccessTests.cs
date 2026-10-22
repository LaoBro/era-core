using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Sessions;
using Xunit;

namespace EraCore.PublicApi.Tests;

/// <summary>
/// issue-010 接入守卫：本程序集只引用 <c>EraCore.Core</c>，且不在 Core 的
/// <c>InternalsVisibleTo</c> 名单中——它能编译并跑通，就证明宿主接入不需要内部访问。
///
/// <para>本文件只允许使用 <c>Input</c> / <c>Output</c> / <c>Files</c> / <c>Sessions</c>
/// 四个公共命名空间；不要引入测试夹具或引擎内部类型。</para>
/// </summary>
public sealed class PublicApiAccessTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Public_axes_drive_a_full_session_round_trip()
    {
        var session = new CoreSession(new CoreSessionOptions(FindTestGameDir(), new FileSystemGameDirAccessor())
        {
            TeardownTimeout = TimeSpan.FromSeconds(15),
        });

        try
        {
            var start = await session.StartAsync();
            Assert.True(start.Succeeded, $"{start.ErrorCode}: {start.ErrorMessage}");

            var csv = session.Files.ListFiles("csv");
            Assert.Contains("csv/GameBase.csv", csv);
            Assert.NotEmpty(session.Files.ReadText("csv/GameBase.csv"));

            await WaitForStateAsync(session, CoreSessionState.WaitingForInput);

            var turns = session.Output.Turns;
            Assert.NotNull(turns);
            session.Input.Submit(new InputFrame(Value: "0"));

            using var cts = new CancellationTokenSource(TurnTimeout);
            var sawEcho = false;
            while (!sawEcho)
            {
                var turn = await turns!.ReadAsync(cts.Token);
                sawEcho = turn.Wire.Contains("You entered: 0", StringComparison.Ordinal);
            }

            Assert.True(await session.StopAsync(), "StopAsync did not finish within the teardown bound");
            Assert.Equal(CoreSessionState.Stopped, session.State);
        }
        finally
        {
            session.Dispose();
        }
    }

    private static async Task WaitForStateAsync(CoreSession session, CoreSessionState expected)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline && session.State != expected)
            await Task.Delay(50);

        Assert.Equal(expected, session.State);
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
