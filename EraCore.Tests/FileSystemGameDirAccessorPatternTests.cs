using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// <see cref="FileSystemGameDirAccessor"/> 通配重载（<c>GetFiles/GetDirectories(path, pattern, option)</c>）
/// 必须镜像 SAF 语义：**大小写不敏感、按条目名匹配**（见 <see cref="Wildcard"/> / SafGameDirAccessor.EnumerateUri）。
///
/// 背景：Android 真实路径（legacy）下 .NET 由托管代码做 glob，<c>MatchCasing.PlatformDefault</c> 解析为
/// 大小写敏感，会把引擎的固定大写模式（如 <c>CHARA*.CSV</c>）挡在磁盘真实名（如 <c>Chara1 霊夢.csv</c>）之外，
/// 导致角色 CSV 一个都枚举不到（eraTK 重新开始时报 NAME_TO_CHARA=-1）。
///
/// 平台说明：Windows 的 FS 与托管 matcher 都不敏感，故「大小写」用例在 Windows 上改前也通过
/// （真守卫在 WSL/Linux 大小写敏感环境，见计划）；但 <c>*.</c> 是确定性差异——
/// <c>Directory.GetFiles(dir, "*.")</c> 会返回无扩展名文件，而 <see cref="Wildcard.Matches"/> 拒绝，
/// 因此等价性用例在 Windows 上改前即红，是本机可用的回归哨兵。
/// </summary>
public class FileSystemGameDirAccessorPatternTests
{
    /// <summary>条目名大小写不敏感——eraTK 的真实形态：磁盘 <c>Chara1 霊夢.csv</c> vs 引擎 <c>CHARA*.CSV</c>。</summary>
    [Fact]
    public void PatternOverloads_MatchNameCaseInsensitively()
    {
        using var fx = new Fixture();
        var acc = new FileSystemGameDirAccessor();

        var chara = acc.GetFiles(fx.Root, "CHARA*.CSV", SearchOption.TopDirectoryOnly);
        Assert.Equal(new[] { Path.Combine(fx.Root, "Chara1 霊夢.csv") }, chara);

        // firstDir 扫描同款：*#* 只按目录名匹配（大小写无关，'#' 亦非特殊字符）
        var starDirs = acc.GetDirectories(fx.Root, "*#*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "1#EV", "x#y" }, starDirs);
    }

    /// <summary>
    /// 主守卫：accessor 的结果必须等于「全量枚举 + <see cref="Wildcard.Matches"/> 按条目名过滤」。
    /// 其中 <c>*.</c> 在 Windows 上改前即红（OS 返回无扩展名文件、Wildcard 拒绝）；
    /// 大小写类模式在 WSL/Linux 上改前即红。两平台都覆盖 TopDirectoryOnly / AllDirectories。
    /// </summary>
    [Theory]
    [InlineData("CHARA*.CSV")]
    [InlineData("*.csv")]
    [InlineData("VarExt*.csv")]
    [InlineData("emuera.*.xml")]
    [InlineData("*#*")]
    [InlineData("*.")]
    [InlineData("a?.erb")]
    public void PatternOverloads_EqualManagedWildcardFilter(string pattern)
    {
        using var fx = new Fixture();
        var acc = new FileSystemGameDirAccessor();

        foreach (var option in new[] { SearchOption.TopDirectoryOnly, SearchOption.AllDirectories })
        {
            // 参考枚举用 System.IO 直取（不经过 accessor 的 pattern 分支），过滤用 Wildcard —— 与被测实现独立。
            var expectedFiles = Directory.GetFiles(fx.Root, "*", option)
                .Where(f => Wildcard.Matches(Path.GetFileName(f), pattern))
                .ToArray();
            Assert.Equal(expectedFiles, acc.GetFiles(fx.Root, pattern, option));

            var expectedDirs = Directory.GetDirectories(fx.Root, "*", option)
                .Where(d => Wildcard.Matches(Path.GetFileName(d), pattern))
                .ToArray();
            Assert.Equal(expectedDirs, acc.GetDirectories(fx.Root, pattern, option));
        }
    }

    /// <summary>
    /// AllDirectories 递归不受目录名匹配影响（镜像 SAF EnumerateUri：目录名不参与过滤，只决定是否下钻）。
    /// <c>1#EV</c> / <c>x#y</c> / <c>sub no match</c> 都不匹配 <c>VarExt*.csv</c>，但其下文件仍须被返回。
    /// </summary>
    [Fact]
    public void PatternOverload_AllDirectories_RecursesAllDirs()
    {
        using var fx = new Fixture();
        var acc = new FileSystemGameDirAccessor();

        var names = acc.GetFiles(fx.Root, "VarExt*.csv", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "VarExt0.csv", "VarExt1.csv", "VarExt2.csv", "VarExt3.csv" }, names);
    }

    /// <summary>保留既有枚举语义：<c>FromSearchOption</c> 不跳 dotfile、目录不存在仍抛（不静默返回空）。</summary>
    [Fact]
    public void PatternOverloads_PreserveEnumerationSemantics()
    {
        using var fx = new Fixture();
        var acc = new FileSystemGameDirAccessor();
        var dotfile = Path.Combine(fx.Root, ".hidden.erb");

        Assert.Contains(dotfile, acc.GetFiles(fx.Root, "*", SearchOption.TopDirectoryOnly));
        Assert.Contains(dotfile, acc.GetFiles(fx.Root, "*.erb", SearchOption.TopDirectoryOnly));

        // '?' 恰匹配 1 个字符：a?.erb 命中 ab.erb，不命中 a.erb
        var q = acc.GetFiles(fx.Root, "a?.erb", SearchOption.TopDirectoryOnly).Select(Path.GetFileName).ToArray();
        Assert.Equal(new[] { "ab.erb" }, q);

        var missing = Path.Combine(fx.Root, "nope");
        Assert.Throws<DirectoryNotFoundException>(() => acc.GetFiles(missing, "*.csv", SearchOption.TopDirectoryOnly));
        Assert.Throws<DirectoryNotFoundException>(() => acc.GetDirectories(missing, "*#*", SearchOption.TopDirectoryOnly));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "emuera-fs-pattern-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);

            File.WriteAllText(Path.Combine(Root, "Chara1 霊夢.csv"), "NO,1\n");
            File.WriteAllText(Path.Combine(Root, "GAMEBASE.csv"), "コード,1\n");
            File.WriteAllText(Path.Combine(Root, "README"), "no extension\n"); // "*." 哨兵
            File.WriteAllText(Path.Combine(Root, "a.erb"), "@A\n");
            File.WriteAllText(Path.Combine(Root, "ab.erb"), "@B\n");
            File.WriteAllText(Path.Combine(Root, ".hidden.erb"), "@H\n");
            File.WriteAllText(Path.Combine(Root, "VarExt0.csv"), "x\n");

            Directory.CreateDirectory(Path.Combine(Root, "1#EV"));
            File.WriteAllText(Path.Combine(Root, "1#EV", "A.ERB"), "@A\n");
            File.WriteAllText(Path.Combine(Root, "1#EV", "VarExt1.csv"), "x\n");

            Directory.CreateDirectory(Path.Combine(Root, "x#y", "deep"));
            File.WriteAllText(Path.Combine(Root, "x#y", "deep", "VarExt2.csv"), "x\n");

            Directory.CreateDirectory(Path.Combine(Root, "sub no match"));
            File.WriteAllText(Path.Combine(Root, "sub no match", "VarExt3.csv"), "x\n");
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
