using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Sessions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-006 验收：<b>只引用公共 API</b> 的宿主能完成一次完整会话——
/// 用 <see cref="CoreSession"/> 启动测试游戏、经 <see cref="CoreSession.Files"/> 读 csv、
/// 经 <see cref="CoreSession.Input"/> 提交选择、经 <see cref="CoreSession.Output"/> 收到对应回合，
/// 最后 <see cref="CoreSession.StopAsync"/> 正常停止。
///
/// <para><b>本文件刻意不引用任何 internal 类型</b>（不构造输入宿主 / 显示状态 / 广播中枢），
/// 也不调用本仓测试助手——它是「宿主只依赖三轴」的可执行证明。改本文件前请保持这一约束。</para>
/// </summary>
[Collection("ServerState")]
public sealed class CoreSessionPublicApiTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Public_api_drives_a_full_session_round_trip()
    {
        var gameDir = FindTestGameDir();
        var session = new CoreSession(new CoreSessionOptions(gameDir, new FileSystemGameDirAccessor())
        {
            TeardownTimeout = TimeSpan.FromSeconds(15),
        });

        try
        {
            var start = await session.StartAsync();
            Assert.True(start.Succeeded, $"{start.ErrorCode}: {start.ErrorMessage}");

            // 文件轴：宿主用相对路径读游戏内容。
            var csvFiles = session.Files.ListFiles("csv");
            Assert.Contains("csv/GameBase.csv", csvFiles);
            Assert.NotEmpty(session.Files.ReadText("csv/GameBase.csv"));

            await WaitForStateAsync(session, CoreSessionState.WaitingForInput);

            // 输出轴：每次读取注册一个新观察者；提交前订阅，避免漏掉本轮回合。
            var turns = session.Output.Turns;
            Assert.NotNull(turns);

            // 输入轴：标题菜单选择 [0] → 脚本打印 "You entered: 0" 并再次等待输入。
            session.Input.Submit(new InputFrame(Value: "0"));

            using var cts = new CancellationTokenSource(TurnTimeout);
            var sawEcho = false;
            while (!sawEcho)
            {
                var turn = await turns!.ReadAsync(cts.Token);
                sawEcho = turn.Wire.Contains("You entered: 0", StringComparison.Ordinal);
            }

            Assert.True(await session.StopAsync(), "StopAsync 在上界内未等游戏循环退出");
            Assert.Equal(CoreSessionState.Stopped, session.State);

            // 停止后三轴给出明确终止态，不抛未处理异常。
            Assert.NotNull(session.Output.Snapshot);
            Assert.True(session.Files.Exists("csv/GameBase.csv"));
            session.Input.Submit(InputFrame.Empty);

            // 幂等。
            Assert.True(await session.StopAsync());
            Assert.Equal(CoreSessionState.Stopped, session.State);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task Missing_game_root_returns_readable_error_without_killing_process()
    {
        var missing = Path.Combine(Path.GetTempPath(), "eracore-missing-" + Guid.NewGuid().ToString("N"));
        var session = new CoreSession(new CoreSessionOptions(missing, new FileSystemGameDirAccessor()));

        var result = await session.StartAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("DIR_NOT_FOUND", result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Equal(CoreSessionState.Failed, session.State);
    }

    [Fact]
    public async Task Missing_csv_returns_readable_error()
    {
        var root = CreateTempGameDir(withCsv: false, withErb: true);
        try
        {
            var session = new CoreSession(new CoreSessionOptions(root, new FileSystemGameDirAccessor()));
            var result = await session.StartAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("MISSING_CSV", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_erb_returns_readable_error()
    {
        var root = CreateTempGameDir(withCsv: true, withErb: false);
        try
        {
            var session = new CoreSession(new CoreSessionOptions(root, new FileSystemGameDirAccessor()));
            var result = await session.StartAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("MISSING_ERB", result.ErrorCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_before_start_settles_into_stopped_and_cannot_restart()
    {
        var session = new CoreSession(new CoreSessionOptions(FindTestGameDir(), new FileSystemGameDirAccessor()));

        try
        {
            Assert.True(await session.StopAsync());
            Assert.Equal(CoreSessionState.Stopped, session.State);

            Assert.True(await session.StopAsync());
            Assert.Equal(CoreSessionState.Stopped, session.State);

            var start = await session.StartAsync();
            Assert.False(start.Succeeded);
            Assert.Equal("SESSION_STOPPED", start.ErrorCode);

            // 未启动会话没有输出轴：给出可读异常而不是空引用。
            var ex = Assert.Throws<InvalidOperationException>(() => session.Output);
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
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

    private static string CreateTempGameDir(bool withCsv, bool withErb)
    {
        var root = Path.Combine(Path.GetTempPath(), "eracore-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (withCsv)
            Directory.CreateDirectory(Path.Combine(root, "csv"));
        if (withErb)
            Directory.CreateDirectory(Path.Combine(root, "erb"));
        return root;
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
