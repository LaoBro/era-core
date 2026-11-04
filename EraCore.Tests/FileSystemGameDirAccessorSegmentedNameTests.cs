using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issues/20 后续：精确名容错从「仅最后一段」升级为<b>逐段</b>解析，并修好尾分隔符短路。
///
/// 平台说明：Windows/NTFS 不区分大小写，下列 IO 用例在 Windows 上「改前也绿」；真正的大小写
/// 敏感守卫在 WSL2 ext4（<c>docs/LESSONS/testing-and-ci.md</c> #12）。规则本身（精确优先 /
/// 歧义放弃 / 逐段重建 / 拒绝 <c>..</c>）由无 IO 的 <see cref="CaseInsensitivePathResolver"/>
/// 用例在所有平台断言。
/// </summary>
public class FileSystemGameDirAccessorSegmentedNameTests
{
    // ── 纯逻辑：精确优先 / 歧义规则 / 逐段重建 ─────────────────────────────

    [Fact]
    public void MatchSegment_prefers_exact_name_over_case_variants()
    {
        var entries = new[] { "CSV", "csv", "erb" };
        Assert.Equal("csv", CaseInsensitivePathResolver.MatchSegment(entries, "csv"));
        Assert.Equal("CSV", CaseInsensitivePathResolver.MatchSegment(entries, "CSV"));
    }

    [Fact]
    public void MatchSegment_gives_up_when_only_ambiguous_variants_match()
    {
        var entries = new[] { "CSV", "Csv" };
        Assert.Null(CaseInsensitivePathResolver.MatchSegment(entries, "cSv")); // 两个不同名 → 不猜
        Assert.Equal("CSV", CaseInsensitivePathResolver.MatchSegment(entries, "CSV")); // 精确名仍优先
    }

    [Fact]
    public void MatchSegment_returns_unique_case_insensitive_match()
    {
        Assert.Equal("GameBase.csv",
            CaseInsensitivePathResolver.MatchSegment(new[] { "GameBase.csv", "Abl.csv" }, "GAMEBASE.CSV"));
        Assert.Null(CaseInsensitivePathResolver.MatchSegment(new[] { "Abl.csv" }, "GAMEBASE.CSV"));
    }

    [Fact]
    public void Resolve_walks_every_segment()
    {
        var entries = new Dictionary<int, IReadOnlyList<string>>
        {
            [0] = new[] { "CSV", "ERB" },
            [1] = new[] { "Chara" },
            [2] = new[] { "A.csv", "b.csv" },
        };
        var resolved = CaseInsensitivePathResolver.Resolve(
            new[] { "csv", "chara", "a.csv" },
            i => entries.TryGetValue(i, out var e) ? e : null);
        Assert.Equal(new[] { "CSV", "Chara", "A.csv" }, resolved);
    }

    [Fact]
    public void Resolve_fails_when_any_segment_is_missing_or_ambiguous()
    {
        Assert.Null(CaseInsensitivePathResolver.Resolve(
            new[] { "csv", "missing" },
            i => i == 0 ? new[] { "CSV" } : new[] { "Chara" }));

        Assert.Null(CaseInsensitivePathResolver.Resolve(
            new[] { "csv", "chara" },
            i => i == 0 ? new[] { "CSV" } : new[] { "Chara", "CHARA" }));

        Assert.Null(CaseInsensitivePathResolver.Resolve(
            new[] { "csv" },
            _ => null)); // 父目录不可枚举
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    public void Resolve_rejects_traversal_and_empty_segments(string segment)
    {
        Assert.Null(CaseInsensitivePathResolver.MatchSegment(new[] { ".", "..", "x" }, segment));
    }

    // ── IO：尾分隔符 / 逐段 / 枚举入口 ─────────────────────────────────────

    [Fact]
    public void Trailing_separator_directory_resolves_and_supports_reads()
    {
        using var fx = new Fixture();
        var acc = new FileSystemGameDirAccessor();
        var trailingLower = Path.Combine(fx.Root, "csv") + Path.DirectorySeparatorChar;

        // 关键回归：改前尾分隔符使 Path.GetFileName 为空 → 直接放弃容错 → false
        Assert.True(acc.DirectoryExists(trailingLower));
        Assert.True(acc.FileExists(Path.Combine(fx.Root, "csv", "ABL.CSV")));
        Assert.Equal("abl-content", acc.ReadAllText(Path.Combine(fx.Root, "csv", "ABL.CSV")));
        Assert.Equal("abl-content", acc.ReadAllText(Path.Combine(fx.Root, "csv", "abl.csv")));
    }

    [Fact]
    public void Enumeration_resolves_theoretical_directory_argument()
    {
        using var fx = new Fixture();
        var acc = new FileSystemGameDirAccessor();
        var lowerCsv = Path.Combine(fx.Root, "csv") + Path.DirectorySeparatorChar;

        // 改前：Directory.GetFiles(".../csv/") 在只有 CSV 的挂载上抛 DirectoryNotFoundException
        var files = acc.GetFiles(lowerCsv, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Abl.csv", "Chara1.csv", "GameBase.csv" }, files);

        var dirs = acc.GetDirectories(lowerCsv).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "Chara" }, dirs);
    }

    [Fact]
    public void Intermediate_segments_are_all_resolved_mixed_case()
    {
        using var fx = new Fixture();
        var acc = new FileSystemGameDirAccessor();
        var mixed = Path.Combine(fx.Root, "csv", "chara") + Path.DirectorySeparatorChar;

        Assert.True(acc.DirectoryExists(mixed));
        Assert.Contains("Chara1.csv",
            acc.GetFiles(mixed, "CHARA*.CSV", SearchOption.TopDirectoryOnly).Select(Path.GetFileName));
    }

    [Fact]
    public void GamePaths_validation_passes_when_only_uppercase_dirs_exist()
    {
        using var fx = new Fixture();
        var paths = GamePaths.Resolve(fx.Root, new FileSystemGameDirAccessor());
        paths.Validate(); // 大小写敏感环境上：改前 MISSING_CSV，改后通过
    }

    /// <summary>
    /// 同目录存在 <c>CSV</c> 与 <c>Csv</c> 两个不同条目、请求第三种大小写时放弃容错。
    /// Windows NTFS 上第二次写会覆盖第一次（无法构造），故仅作大小写敏感环境的守卫。
    /// </summary>
    [Fact]
    public void Ambiguous_case_variants_are_not_guessed_on_case_sensitive_fs()
    {
        using var fx = new Fixture();
        var dir = Path.Combine(fx.Root, "ambiguous");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CSV"), "upper");
        File.WriteAllText(Path.Combine(dir, "Csv"), "mixed");

        if (!IsCaseSensitive(dir))
            return;

        var acc = new FileSystemGameDirAccessor();
        Assert.False(acc.FileExists(Path.Combine(dir, "cSv"))); // 歧义 → miss
        Assert.Equal("upper", acc.ReadAllText(Path.Combine(dir, "CSV"))); // 精确名仍可读
    }

    private static bool IsCaseSensitive(string dir)
    {
        var name = "case_probe_" + Guid.NewGuid().ToString("N") + ".tmp";
        var probe = Path.Combine(dir, name);
        File.WriteAllText(probe, "x");
        try
        {
            return !File.Exists(Path.Combine(dir, name.ToUpperInvariant()));
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "emuera-fs-segmented-" + Guid.NewGuid().ToString("N"));
            var csv = Path.Combine(Root, "CSV");
            Directory.CreateDirectory(csv);
            Directory.CreateDirectory(Path.Combine(Root, "ERB"));
            Directory.CreateDirectory(Path.Combine(csv, "Chara"));
            File.WriteAllText(Path.Combine(csv, "Abl.csv"), "abl-content");
            File.WriteAllText(Path.Combine(csv, "GameBase.csv"), "gamebase-content");
            File.WriteAllText(Path.Combine(csv, "Chara", "Chara1.csv"), "chara-content");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { /* best-effort */ }
        }
    }
}
