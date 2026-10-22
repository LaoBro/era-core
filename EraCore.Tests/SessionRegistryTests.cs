using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using MinorShift.Emuera.Terminal.Platform;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// SessionRegistry 单元测试（重构补测）：会话创建/删除/替换/WS 订阅的原子操作
/// 与 outcome 状态机（503/409/201、200/404、400/500、WS 订阅 null/成功）。
/// </summary>
[Collection("ServerState")]
public class SessionRegistryTests
{
    private static SessionRegistry CreateRegistry()
    {
        return new SessionRegistry(new NullTerminalSetup(), new GameConfigService(new ConfigData()));
    }

    [Fact]
    public async Task Create_returns_NoGameLoaded_when_empty()
    {
        var r = CreateRegistry();
        var result = await r.CreateNewSessionAsync();
        Assert.Equal(SessionCreateStatus.NoGameLoaded, result.Status);
        Assert.Null(result.SessionId);
    }

    [Fact]
    public async Task Delete_returns_NotFound_when_empty()
    {
        var r = CreateRegistry();
        var result = await r.DeleteSessionAsync();
        Assert.Equal(SessionDeleteStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task Load_game_success_returns_session_info()
    {
        var r = CreateRegistry();
        var result = await r.ReplaceForLoadGameAsync(FindTestGameDir());

        Assert.Equal(LoadGameStatus.Success, result.Status);
        Assert.NotNull(result.SessionId);
        Assert.NotNull(result.State);
        // GamePaths.Resolve 的 ExeDir 以目录分隔符结尾
        var expected = Path.GetFullPath(FindTestGameDir()) + Path.DirectorySeparatorChar;
        Assert.Equal(expected, result.GameDir);
        r.DisposeAll();
    }

    [Fact]
    public async Task Load_game_then_create_conflicts()
    {
        var r = CreateRegistry();
        await r.ReplaceForLoadGameAsync(FindTestGameDir());

        var result = await r.CreateNewSessionAsync();

        Assert.Equal(SessionCreateStatus.Conflict, result.Status);
        r.DisposeAll();
    }

    [Fact]
    public async Task Load_game_then_delete_removes_session()
    {
        var r = CreateRegistry();
        await r.ReplaceForLoadGameAsync(FindTestGameDir());

        var result = await r.DeleteSessionAsync();

        Assert.Equal(SessionDeleteStatus.Removed, result.Status);
        Assert.Null(r.CurrentSession);
        r.DisposeAll();
    }

    [Fact]
    public async Task Load_game_bad_path_returns_PathError_and_does_not_pollute_current()
    {
        var r = CreateRegistry();
        var badPath = Path.Combine(Path.GetTempPath(), "emuera_no_such_dir_" + Guid.NewGuid().ToString("N"));

        var result = await r.ReplaceForLoadGameAsync(badPath);

        Assert.Equal(LoadGameStatus.PathError, result.Status);
        Assert.Equal("DIR_NOT_FOUND", result.Code);
        Assert.NotNull(result.Message);
        // 路径校验失败时 server 状态零改动——不建会话（issue-009：也不再存在可被污染的进程级根）
        Assert.Null(r.CurrentSession);
    }

    [Fact]
    public async Task Load_game_empty_dir_returns_PathError()
    {
        var r = CreateRegistry();
        var emptyDir = Path.Combine(Path.GetTempPath(), "emuera_empty_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var result = await r.ReplaceForLoadGameAsync(emptyDir);
            Assert.Equal(LoadGameStatus.PathError, result.Status);
            Assert.Equal("MISSING_CSV", result.Code);
        }
        finally
        {
            Directory.Delete(emptyDir);
        }
    }

    [Fact]
    public async Task Ws_subscription_is_null_when_no_session()
    {
        var r = CreateRegistry();
        Assert.Null(await r.TryGetWsSubscriptionAsync());
    }

    [Fact]
    public async Task Ws_subscription_returns_reader_after_load()
    {
        var r = CreateRegistry();
        await r.ReplaceForLoadGameAsync(FindTestGameDir());

        var sub = await r.TryGetWsSubscriptionAsync();

        Assert.NotNull(sub);
        Assert.NotNull(sub!.Reader);
        Assert.Same(r.CurrentSession, sub.Session);
        r.DisposeAll();
    }

    /// <summary>查找 test_game 目录——从测试 bin 目录向上遍历找 repo 根的 test_game/。</summary>
    internal static string FindTestGameDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "test_game");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "test_game directory not found. Searched upward from: " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// 最小 ITerminalSetup 桩——与既有 NullTerminalSetup（KestrelGameServerIdleTests 等）同构。
    /// </summary>
    internal sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}

/// <summary>
/// 共享禁用并行的 collection——测试加载 test_game 会改写进程级引擎静态（GlobalStatic / Lang 等），
/// 相关测试类（SessionRegistryTests / KestrelGameServerEndpointTests）加入此 collection
/// 以串行化，避免互相污染静态状态。游戏根自 issue-009 起是会话作用域，不再是共享状态。
/// </summary>
[CollectionDefinition("ServerState", DisableParallelization = true)]
public class ServerStateCollection
{
}
