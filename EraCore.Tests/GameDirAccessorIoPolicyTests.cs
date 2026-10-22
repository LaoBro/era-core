using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MinorShift.Emuera.Runtime.Script.Loader;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 不变量 F-1 的清单守卫：<see cref="IGameDirAccessor"/> 是游戏树 IO 的唯一接缝，
/// Core 内任何直连 <c>File.*</c> / <c>Directory.*</c> / <c>new FileStream/FileInfo/DirectoryInfo</c>
/// 都必须<b>在这里显式登记并说明理由</b>。登记项带调用数上限＝快照，
/// <b>减少调用无需改清单（Cap 是上限），但计数归零必须删行</b>；<b>新增或换(new)文件必转红</b>——让「例外蔓延」在 CI 上被拦下。
/// <para>
/// ⚠️ <b>本表是例外清单的唯一真值</b>：<see cref="KnownDirectIo"/> 的 <c>Reason</c> / <c>Domain_</c> / <c>Cap</c>
/// 就是那条清单本身，不再向任何文档镜像。ARCHITECTURE.md §4 只保留不变量 F-1 / F-4 / F-5 与指向本类的指针——
/// 「两处维护」正是过去清单与文档悄悄漂移的根因。
/// </para>
/// <para>
/// 与 <see cref="SafIoPolicyTests"/> 的分工：本类守「全 Core 例外清单」；
/// 后者守 SAF content URI 的路径语义（特定的非法 <c>Path.*</c> 用法），二者不重叠。
/// </para>
/// </summary>
public class GameDirAccessorIoPolicyTests
{
    /// <summary>
    /// 直连 IO 识别——与旧 SafIoPolicyTests 同款规则，避免两处口径漂移。
    /// </summary>
    private static readonly Regex DirectIo = new(
        @"\b(?:File|Directory)\.[A-Za-z_][A-Za-z0-9_]*\b|\bnew\s+(?:(?:System\.IO\.)?FileStream|(?:System\.IO\.)?FileInfo|(?:System\.IO\.)?DirectoryInfo)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// 豁免域 itself —— 直连 IO 在这些文件里是<c>设计意图</c>，不计入例外清单：
    /// <list type="bullet">
    /// <item><c>FileSystemGameDirAccessor.cs</c>：<see cref="IGameDirAccessor"/> 的文件系统实现本体（合规调用）。</item>
    /// <item><c>AppDataIo.cs</c>：F-4 应用私有域的唯一 IO 窄口子。</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> ImplementorsOfTheExemptSeams = new(StringComparer.Ordinal)
    {
        "FileSystemGameDirAccessor.cs",
        "AppDataIo.cs",
        "AppDataPaths.cs",
    };

    /// <summary>归属域。</summary>
    private enum Domain
    {
        /// <summary>游戏树 IO——仍绕过 accessor，属待收敛欠账。</summary>
        GameTree,
        /// <summary>应用私有域 / 引擎自有派生数据（F-4 豁免）。</summary>
        AppPrivate,
        /// <summary>与具体运行形态绑定（进程工作目录、程序集探测、平台兼容包装），与游戏树无关。</summary>
        Platform,
    }

    private readonly record struct Exemption(string Reason, Domain Domain_, int Cap);

    /// <summary>
    /// AppPrivate 域的「锚点」——源文件出现任一符号即可证明其直连 IO 确实挂在本域的根上。
    /// </summary>
    private static readonly string[] AppPrivateRoots = ["AppDataPaths", "AppDataIo", "ErbIrCache"];

    /// <summary>
    /// 例外清单本身。键 = 相对 <c>EraCore.Core</c> 的路径。
    /// <para>
    /// 本表就是那条清单（见类 remarks）：文档不复制它，改这个 Dictionary 即可。
    /// </para>
    /// <para>
    /// <b>Cap 是当前实测调用数：</b>删掉若干处调用使实际数下降时无需改本表；新增一处即超出 → 转红。
    /// 故本表只减不增（不变量 F-5 的实践形态：新增即转红）。
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Exemption> KnownDirectIo = new Dictionary<string, Exemption>(StringComparer.Ordinal)
    {
        // ── GameTree：游戏树 IO，仍绕过 accessor —— 真正的待收敛欠账 ────────────────
        // ADR-0036（不可编译分支）+ ADR-0037（可编译读缝）+ ADR-0038（sav 挪档）后**已归零**：
        // 新增游戏树直连 IO 会落进下面的「无登记 ⇒ 违规」分支，立即转红。

        // ── 待收敛（AppPrivate）：**已清空** ───────────────────────────────────────
        // ADR-0048（2026-10-18，14 票批 B/C）：ErbIrCache（10 处）与 SrcContentCache（8 处）全部归零——
        // 文件级走 SafCompat、目录级走 AppDataIo，两行均已删除。AppPrivate 域此后无欠账：
        // 新增任何该域的直连 IO 会落进「无登记 ⇒ 违规」分支，立即转红。

        // ── 永久豁免（Platform）：依附于具体运行形态，与游戏树无关 —— 不打算收敛 ────────
        ["Files/GamePaths.cs"] = new(
            "宿主安装目录探测（AppContext.BaseDirectory 下的 Data/erb）+ 本地路径归一化 new DirectoryInfo——发生在 accessor 上任之前；游戏树判定已在 Validate 收敛", Domain.Platform, 2),
        ["Shared/Runtime/Utils/SafCompat.cs"] = new(
            "ADR-0019 兼容包装本体：content URI 的本地分支实现（有意保留）", Domain.Platform, 18),
        ["Shared/Runtime/Utils/Sys.cs"] = new(
            "进程工作目录（Directory.GetCurrentDirectory），不解析游戏文件", Domain.Platform, 1),
    };

    [Fact]
    public void Core_direct_IO_does_not_exceed_the_documented_exception_list()
    {
        var coreDir = Path.Combine(FindRepositoryRoot(), "EraCore.Core");
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in EnumerateSourceFiles(coreDir))
            counts[path] = CountDirectIo(Path.Combine(coreDir, path));

        var violations = new List<string>();
        foreach (var (relativePath, actual) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (actual == 0)
                continue; // 无直连 IO 的文件不进入判定——否则全仓干净文件都会误报
            if (ImplementorsOfTheExemptSeams.Contains(Path.GetFileName(relativePath)))
                continue; // 接缝实现本体：合规调用

            if (!KnownDirectIo.TryGetValue(relativePath, out var exemption))
            {
                violations.Add($"{relativePath}: {actual} direct I/O call(s); no exemption is registered " +
                               "(登记前先判断它属 GameTree / AppPrivate / 还是 Platform 域).");
                continue;
            }

            if (actual > exemption.Cap)
            {
                violations.Add($"{relativePath}: {actual} call(s) exceeds registered cap {exemption.Cap} " +
                               $"[{exemption.Domain_}] ({exemption.Reason}).");
            }
        }

        // 陈旧登记：清单必须是「现役欠账」而非历史名单——两种情形都必须删行：
        // (a) 文件已消失（被删/被改名）；
        // (b) 文件还在，但直连 IO 已**清零**（被收敛干净，或随不可编译分支一并删除——ADR-0036）。
        // (b) 曾经是漏洞：AppContents.cs 的 3 处 IO 连同其 #else 半支被删后，行仍躺在清单里，
        //     直到人工勘察才发现「有一条欠账其实已经不在构建里」。
        foreach (var relativePath in KnownDirectIo.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!counts.TryGetValue(relativePath, out var listed))
            {
                violations.Add($"{relativePath}: exemption row is stale (file no longer exists) — remove it.");
                continue;
            }
            if (listed == 0)
                violations.Add($"{relativePath}: exemption row is stale (no direct I/O left — converged, or removed " +
                               "with a dead compile branch) — remove the row.");
        }

        Assert.True(violations.Count == 0,
            "Direct file I/O escapes the IGameDirAccessor seam:\n" + string.Join('\n', violations));
    }

    /// <summary>
    /// 登记为 <see cref="Domain.AppPrivate"/> 的例外必须<b>确有依据</b>：源文件需援引本域的根
    /// （<c>AppDataPaths</c> / <c>AppDataIo</c> / <c>ErbIrCache</c> 的缓存布局根），
    /// 且其中之一确实落在私有域内——这一步把 F-4 的「靠开发者记得这是私有目录」
    /// 换成可在 CI 上执行的事实判定。
    /// <para>
    /// <c>ErbIrCache</c> 之所以算作根基：IR / 源内容缓存的布局由它<b>单点决定</b>
    /// （<c>IrCacheRoot = AppDataPaths.Directory/IRCache</c>，SrcContentCache 只是复用同一布局）。
    /// 允许它作为间接根基，是为了不强迫调用文件重复写出同一条路径表达式。
    /// </para>
    /// </summary>
    [Fact]
    public void App_private_exemptions_are_grounded_in_the_private_root()
    {
        var coreDir = Path.Combine(FindRepositoryRoot(), "EraCore.Core");
        Assert.True(AppDataPaths.IsUnder(AppDataPaths.CombinePath("anything.dat")),
            "sanity: AppDataPaths.IsUnder must recognize its own root.");
        Assert.True(AppDataPaths.IsUnder(ErbIrCache.IrCacheRoot),
            "sanity: the IR cache layout root must live inside the private root.");

        var violations = new List<string>();
        foreach (var (relativePath, exemption) in KnownDirectIo.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (exemption.Domain_ != Domain.AppPrivate)
                continue;
            var text = File.ReadAllText(Path.Combine(coreDir, relativePath));
            var grounded = AppPrivateRoots.Any(root => text.Contains(root, StringComparison.Ordinal));
            if (!grounded)
            {
                violations.Add($"{relativePath}: registered as AppPrivate but never references any private-root anchor " +
                               $"({string.Join(" / ", AppPrivateRoots)}).");
            }
        }

        Assert.True(violations.Count == 0,
            "App-private exemptions must be grounded in the app-private root:\n" + string.Join('\n', violations));
    }

    /// <summary>
    /// 全约束本身的自证：<see cref="AppDataIo"/> 的窄口子必须拒绝绝对路径与 content URI——
    /// 「越出私有域」要在调用方就不可表达，不能靠运行时检查补漏。
    /// </summary>
    [Fact]
    public void App_data_IO_port_rejects_paths_outside_the_private_root()
    {
        Assert.True(AppDataPaths.IsUnder(AppDataIo.Resolve("setting.json").Value));
        Assert.Throws<ArgumentException>(() => AppDataIo.Resolve("content://com.android/tree/era_ir.dat"));
        Assert.Throws<ArgumentException>(() => AppDataIo.Resolve(Path.Combine(AppDataPaths.Directory, "x.dat")));
        Assert.Throws<ArgumentException>(() => AppDataIo.Resolve("  "));
    }

    internal static bool IsRegisteredDirectIoExemption(string relativePath)
        => KnownDirectIo.ContainsKey(relativePath);

    internal static IEnumerable<string> EnumerateSourceFiles(string coreDir)
    {
        foreach (var path in Directory.EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(coreDir, path).Replace('\\', '/');
            // 构建产物 / 生成代码不参与判定——obj 下还有 AssemblyInfo 等生成文件。
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
                continue;
            yield return relative;
        }
    }

    internal static int CountDirectIo(string absolutePath)
    {
        var count = 0;
        foreach (var rawLine in File.ReadAllLines(absolutePath))
        {
            var line = rawLine.TrimStart();
            if (line.StartsWith("//", StringComparison.Ordinal)
                || line.StartsWith("/*", StringComparison.Ordinal)
                || line.StartsWith("*", StringComparison.Ordinal))
                continue;
            count += DirectIo.Matches(line).Count;
        }
        return count;
    }

    internal static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
