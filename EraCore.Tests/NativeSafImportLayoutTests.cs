using System;
using System.IO;
using EraCore.Native;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// SAF 导入布局规则：根目录大小写规范化 + 结构校验；以及 tree URI 卷映射规则
/// （含 MuMu 共享卷「根路径不含 UUID、只能靠 fsUuid 匹配」的形态）。
/// </summary>
public sealed class NativeSafImportLayoutTests : IDisposable
{
    private readonly string _root;

    public NativeSafImportLayoutTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "eracore-saf-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    // ===== 根目录规范化 =====

    [Theory]
    [InlineData("CSV", "csv")]
    [InlineData("csv", "csv")]
    [InlineData("ERB", "erb")]
    [InlineData("Resources", "resources")]
    [InlineData("DAT", "dat")]
    public void RootDirectoryName_IsNormalizedToEngineSpelling(string source, string expected)
    {
        Assert.Equal(expected, SafImportLayout.MapDirectoryName(source, depth: 0));
        // 只有拼写确实发生变化才应上报「已规范化」。
        Assert.Equal(!string.Equals(source, expected, StringComparison.Ordinal),
            SafImportLayout.IsNormalizedName(source, depth: 0));
    }

    [Fact]
    public void RootDirectoryName_UnknownNameIsKept()
    {
        Assert.Equal("GUIDE", SafImportLayout.MapDirectoryName("GUIDE", depth: 0));
        Assert.False(SafImportLayout.IsNormalizedName("GUIDE", depth: 0));
    }

    [Fact]
    public void NestedDirectoryName_IsNeverNormalized()
    {
        // 深层目录保持原名：只有根层是引擎硬编码契约。
        Assert.Equal("CSV", SafImportLayout.MapDirectoryName("CSV", depth: 1));
        Assert.False(SafImportLayout.IsNormalizedName("CSV", depth: 1));
    }

    // ===== 结构校验 =====

    [Fact]
    public void Validate_CompleteStructureIsValid()
    {
        var game = CreateGame("csv", "erb");
        File.WriteAllText(Path.Combine(game, "csv", "GameBase.csv"), "x");
        Directory.CreateDirectory(Path.Combine(game, "csv", "Chara"));
        File.WriteAllText(Path.Combine(game, "csv", "Chara", "Chara1.csv"), "x");
        File.WriteAllText(Path.Combine(game, "erb", "main.erb"), "x");

        var structure = SafImportLayout.Validate(game);

        Assert.Equal(SafImportStructureIssue.None, structure.Issue);
        Assert.True(structure.IsValid);
        Assert.Equal(2, structure.CsvFiles);
        Assert.Equal(1, structure.ErbFiles);
    }

    [Fact]
    public void Validate_MissingCsvIsReported()
    {
        var game = CreateGame("erb");
        File.WriteAllText(Path.Combine(game, "erb", "main.erb"), "x");

        var structure = SafImportLayout.Validate(game);

        Assert.Equal(SafImportStructureIssue.MissingCsv, structure.Issue);
        Assert.Contains("csv/", structure.Message);
        Assert.False(structure.IsValid);
    }

    [Fact]
    public void Validate_EmptyCsvIsReportedAsMissingCsv()
    {
        var game = CreateGame("csv", "erb");
        Directory.CreateDirectory(Path.Combine(game, "csv"));
        File.WriteAllText(Path.Combine(game, "erb", "main.erb"), "x");

        var structure = SafImportLayout.Validate(game);

        Assert.Equal(SafImportStructureIssue.MissingCsv, structure.Issue);
    }

    [Fact]
    public void Validate_MissingErbIsReported()
    {
        var game = CreateGame("csv");
        File.WriteAllText(Path.Combine(game, "csv", "GameBase.csv"), "x");

        var structure = SafImportLayout.Validate(game);

        Assert.Equal(SafImportStructureIssue.MissingErb, structure.Issue);
        Assert.Contains("erb/", structure.Message);
    }

    [Fact]
    public void Validate_ErbWithoutErbFilesIsReported()
    {
        var game = CreateGame("csv", "erb");
        File.WriteAllText(Path.Combine(game, "csv", "GameBase.csv"), "x");
        File.WriteAllText(Path.Combine(game, "erb", "readme.txt"), "x");

        var structure = SafImportLayout.Validate(game);

        Assert.Equal(SafImportStructureIssue.EmptyErb, structure.Issue);
        Assert.Equal(0, structure.ErbFiles);
    }

    [Fact]
    public void Validate_MissingRootIsNotDirectory()
    {
        var structure = SafImportLayout.Validate(Path.Combine(_root, "nope"));

        Assert.Equal(SafImportStructureIssue.NotDirectory, structure.Issue);
    }

    private string CreateGame(params string[] directories)
    {
        var game = Path.Combine(_root, "game-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(game);
        foreach (var directory in directories)
            Directory.CreateDirectory(Path.Combine(game, directory));
        return game;
    }
}
