using MinorShift.Emuera.Files;
using System;
using System.IO;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 05：GamePaths.Validate() 改抛 GamePathValidationException（旧实现 Environment.Exit(1)）。
///
/// 验证三个错误码契约（与前端 /load-game 错误映射对称）：
/// - DIR_NOT_FOUND：ExeDir 不存在
/// - MISSING_CSV：ExeDir 存在但 csv/ 缺失
/// - MISSING_ERB：ExeDir + csv/ 存在但 erb/ 缺失
/// 校验通过时不抛异常。
///
/// 注：GamePaths.Resolve 会重赋静态 GamePaths.Current——测试间无隔离，但每个 case
/// 各自 Resolve 独立临时目录，互不读对方的 paths 字段。
/// </summary>
[Collection("LoaderTests")]
public class GamePathsTests
{
    [Fact]
    public void Validate_passes_when_csv_and_erb_exist()
    {
        using var tmp = new TempGameDir();
        var paths = GamePaths.Resolve(tmp.Root, new FileSystemGameDirAccessor());
        paths.Validate(); // 不抛异常即通过
    }

    [Fact]
    public void Validate_throws_DIR_NOT_FOUND_when_ExeDir_missing()
    {
        // 选一个保证不存在的路径——Path.GetTempPath + 不存在的 GUID 子目录
        var missing = Path.Combine(Path.GetTempPath(), "emuera-missing-" + Guid.NewGuid().ToString("N"));
        var paths = GamePaths.Resolve(missing, new FileSystemGameDirAccessor());

        var ex = Assert.Throws<GamePathValidationException>(() => paths.Validate());
        Assert.Equal("DIR_NOT_FOUND", ex.Code);
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Validate_throws_MISSING_CSV_when_csv_dir_absent()
    {
        using var tmp = new TempGameDir(createCsv: false, createErb: true);
        var paths = GamePaths.Resolve(tmp.Root, new FileSystemGameDirAccessor());

        var ex = Assert.Throws<GamePathValidationException>(() => paths.Validate());
        Assert.Equal("MISSING_CSV", ex.Code);
        Assert.Contains("csv", ex.Message);
    }

    [Fact]
    public void Validate_throws_MISSING_ERB_when_erb_dir_absent()
    {
        using var tmp = new TempGameDir(createCsv: true, createErb: false);
        var paths = GamePaths.Resolve(tmp.Root, new FileSystemGameDirAccessor());

        var ex = Assert.Throws<GamePathValidationException>(() => paths.Validate());
        Assert.Equal("MISSING_ERB", ex.Code);
        Assert.Contains("erb", ex.Message);
    }

    private sealed class TempGameDir : IDisposable
    {
        public string Root { get; }
        private bool _disposed;

        public TempGameDir(bool createCsv = true, bool createErb = true)
        {
            Root = Path.Combine(Path.GetTempPath(), "emuera-paths-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            if (createCsv) Directory.CreateDirectory(Path.Combine(Root, "csv"));
            if (createErb) Directory.CreateDirectory(Path.Combine(Root, "erb"));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* 测试临时目录清理失败不致测试失败 */ }
        }
    }
}
