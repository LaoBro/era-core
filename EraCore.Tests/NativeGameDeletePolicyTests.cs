using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using EraCore.Native;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 游戏库删除的平台无关安全边界：判定、路径规范化、真删与 library.json 清理。
/// 用临时目录造真实文件系统，断言「隔壁不动」与越界拒绝。
/// </summary>
public sealed class NativeGameDeletePolicyTests : IDisposable
{
    private const string LegacyBareKey = "alpha";

    private readonly string _root;
    private readonly string _games;
    private readonly string _library;

    public NativeGameDeletePolicyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eracore-delete-" + Guid.NewGuid().ToString("N"));
        _games = Path.Combine(_root, "games");
        _library = Path.Combine(_root, "library.json");
        Directory.CreateDirectory(_games);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清理失败不影响断言结论。
        }
    }

    [Fact]
    public void PathSource_IsAlwaysRejected()
    {
        var decision = GameLibraryDeletePolicy.Evaluate(_games, GameDeleteSource.Path, "alpha");

        Assert.False(decision.Allowed);
        Assert.Equal(GameDeleteRejectReason.NotInternal, decision.Reason);
        Assert.Contains("移除记录", decision.Message);
    }

    [Fact]
    public void InternalDelete_RemovesTargetAndRecord_ButKeepsNeighbors()
    {
        var alpha = Path.Combine(_games, "alpha");
        Directory.CreateDirectory(Path.Combine(alpha, "save"));
        File.WriteAllText(Path.Combine(alpha, ".extracted-v1"), "v1");
        File.WriteAllText(Path.Combine(alpha, "save", "data.txt"), "save-data");

        var neighbor = Path.Combine(_games, "neighbor");
        Directory.CreateDirectory(neighbor);
        File.WriteAllText(Path.Combine(neighbor, "keep.txt"), "keep");

        WriteLibrary(new Dictionary<string, string>
        {
            ["dir:alpha"] = "2026-11-01T00:00:00.0000000+00:00",
            ["dir:neighbor"] = "2026-11-02T00:00:00.0000000+00:00",
            ["path:/somewhere"] = "2026-11-03T00:00:00.0000000+00:00",
            [LegacyBareKey] = "2026-11-04T00:00:00.0000000+00:00",
        });

        var decision = GameLibraryDeletePolicy.Evaluate(_games, GameDeleteSource.Internal, "alpha");
        Assert.True(decision.Allowed, decision.Message);
        Assert.Equal(alpha, decision.TargetFullPath);

        var outcome = GameLibraryDeletePolicy.Delete(decision, _library);
        Assert.True(outcome.Success, outcome.Error);
        Assert.True(outcome.PlayedRecordRemoved);

        Assert.False(Directory.Exists(alpha));
        Assert.False(File.Exists(Path.Combine(alpha, ".extracted-v1")));
        Assert.True(Directory.Exists(neighbor));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(neighbor, "keep.txt")));

        var remaining = ReadLibrary();
        Assert.DoesNotContain("dir:alpha", remaining.Keys);
        Assert.Contains("dir:neighbor", remaining.Keys);
        Assert.Contains("path:/somewhere", remaining.Keys);
        // 既知行为：旧版裸名字键不参与当前 PlayedKey（dir:<name>），删除时刻意保留。
        Assert.Contains(LegacyBareKey, remaining.Keys);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" evil ")]
    [InlineData("evil/../x")]
    public void InvalidDirectoryName_IsRejected(string name)
    {
        var decision = GameLibraryDeletePolicy.Evaluate(_games, GameDeleteSource.Internal, name);

        Assert.False(decision.Allowed);
        Assert.Equal(GameDeleteRejectReason.InvalidName, decision.Reason);
    }

    [Fact]
    public void EscapingTargetPath_IsRejected()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);

        var escaped = GameLibraryDeletePolicy.EvaluateTarget(
            _games,
            GameDeleteSource.Internal,
            Path.Combine(_games, "..", "outside"));
        Assert.False(escaped.Allowed);
        Assert.Equal(GameDeleteRejectReason.OutsideRoot, escaped.Reason);
        Assert.True(Directory.Exists(outside));

        var rootItself = GameLibraryDeletePolicy.EvaluateTarget(
            _games,
            GameDeleteSource.Internal,
            _games);
        Assert.False(rootItself.Allowed);
        Assert.Equal(GameDeleteRejectReason.OutsideRoot, rootItself.Reason);
    }

    [Fact]
    public void TargetMustBeDirectChildOfGamesRoot()
    {
        var grandchild = Path.Combine(_games, "nested", "child");
        Directory.CreateDirectory(grandchild);

        var decision = GameLibraryDeletePolicy.EvaluateTarget(
            _games,
            GameDeleteSource.Internal,
            grandchild);
        Assert.False(decision.Allowed);
        Assert.Equal(GameDeleteRejectReason.NotDirectChild, decision.Reason);

        // 用条目名走公共入口时更早被名字校验拦下，同样不会删。
        var byName = GameLibraryDeletePolicy.Evaluate(_games, GameDeleteSource.Internal, "nested/child");
        Assert.False(byName.Allowed);
        Assert.Equal(GameDeleteRejectReason.InvalidName, byName.Reason);
        Assert.True(Directory.Exists(grandchild));
    }

    [Fact]
    public void MissingDirectory_ReturnsReasonWithoutThrowing()
    {
        var decision = GameLibraryDeletePolicy.Evaluate(_games, GameDeleteSource.Internal, "ghost");

        Assert.False(decision.Allowed);
        Assert.Equal(GameDeleteRejectReason.Missing, decision.Reason);
        Assert.Contains("目录不存在", decision.Message);
    }

    [Theory]
    [InlineData("test_game", true)]
    [InlineData("test_game_b", true)]
    [InlineData("other_game", false)]
    [InlineData("Test_Game", false)]
    public void BuiltInDetection_MatchesExactNames(string name, bool expected)
    {
        Assert.Equal(expected, GameLibraryDeletePolicy.IsBuiltInGame(name));
    }

    [Fact]
    public void RemovePlayed_OnlyRemovesTargetKey()
    {
        WriteLibrary(new Dictionary<string, string>
        {
            ["dir:alpha"] = "2026-11-01T00:00:00.0000000+00:00",
            ["dir:beta"] = "2026-11-02T00:00:00.0000000+00:00",
            [LegacyBareKey] = "2026-11-03T00:00:00.0000000+00:00",
        });

        Assert.True(GameLibraryDeletePolicy.RemovePlayed(_library, "dir:alpha"));
        var remaining = ReadLibrary();
        Assert.DoesNotContain("dir:alpha", remaining.Keys);
        Assert.Contains("dir:beta", remaining.Keys);
        Assert.Contains(LegacyBareKey, remaining.Keys);

        Assert.False(GameLibraryDeletePolicy.RemovePlayed(_library, "dir:alpha"));
        Assert.False(GameLibraryDeletePolicy.RemovePlayed(Path.Combine(_root, "missing.json"), "dir:alpha"));
    }

    [Fact]
    public void SymlinkEscapingGamesRoot_IsRejected()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "outside.txt"), "outside");

        var link = Path.Combine(_games, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows 非管理员 / 未开发者模式无法创建目录符号链接：跳过该场景（已知限制）。
            return;
        }

        try
        {
            var decision = GameLibraryDeletePolicy.Evaluate(_games, GameDeleteSource.Internal, "escape");
            Assert.False(decision.Allowed);
            Assert.Equal(GameDeleteRejectReason.OutsideRoot, decision.Reason);
            Assert.True(Directory.Exists(outside));
            Assert.True(File.Exists(Path.Combine(outside, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    private void WriteLibrary(Dictionary<string, string> values) =>
        File.WriteAllText(_library, JsonSerializer.Serialize(values));

    private Dictionary<string, string> ReadLibrary() =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_library))
        ?? new Dictionary<string, string>();
}
