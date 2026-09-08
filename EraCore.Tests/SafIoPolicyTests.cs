using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 防止 Shared Runtime 新增未经审查的本地 I/O。
/// 已知遗留点在基线中按文件和数量登记；删除无需改基线，新增必须先作出明确的 SAF 设计决定。
/// </summary>
public class SafIoPolicyTests
{
    private static readonly Regex DirectIo = new(
        @"\b(?:File|Directory)\.[A-Za-z_][A-Za-z0-9_]*\b|\bnew\s+(?:(?:System\.IO\.)?FileStream|(?:System\.IO\.)?FileInfo|(?:System\.IO\.)?DirectoryInfo)\b",
        RegexOptions.Compiled);

    private static readonly Regex UnsafeContentPathSemantics = new(
        @"\bPath\.GetRelativePath\s*\(|\bPath\.GetDirectoryName\s*\(\s*csvPath\s*\)|\bPath\.GetFileNameWithoutExtension\s*\(\s*csvPath\s*\)",
        RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> KnownDirectIoFiles =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Config/ConfigData.cs"] = "阶段 3：本地目录迁移与更新 key",
            ["Config/JSON/JSONConfig.cs"] = "应用私有 setting.json 存储",
            ["Script/Data/ConstantData.cs"] = "仅 accessor 缺失时的本地回退",
            ["Script/Loader/ErbIrCache.cs"] = "issues/05 方案 B：app 私有存储（AppDataPaths）IRCache 孤儿清理——非游戏内容路径（content-URI 永不落此分支）",
            ["Script/Statements/Instraction.Child.cs"] = "阶段 3：声音文件访问",
            ["Utils/EncodingHandler.cs"] = "阶段 1：编码探测",
            ["Utils/SafCompat.cs"] = "本地路径分支的合法实现",
            ["Utils/Sys.cs"] = "进程工作目录，不解析游戏文件",
            ["Utils/EvilMask/Lang.cs"] = "阶段 3：自定义语言文件",
            ["Utils/EvilMask/Utils.cs"] = "非 HEADLESS 图像加载",
            ["Utils/PluginSystem/PluginManager.cs"] = "阶段 3：Android 插件策略",
        };

    // 行号基线是有意严格的：修改含裸 I/O 的代码时，必须重新确认其 SAF 归属。
    private static readonly IReadOnlyDictionary<string, int> KnownDirectIoCallSites =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // （issues/12 M4 前移行号基线：ConfigData +3 行（IrFirstDirList 两处 ConfigItem）、
            //   ErbIrCache +21 行（v2 头/清单区/文档）——直连 I/O 调用点归属逐一复核未变）
            // （issues/13 M5 前移行号基线：ConfigData +2 行（IrFileListCache ConfigItem）——
            //   直连 I/O 调用点归属逐一复核未变，纯行号平移）
            ["Config/ConfigData.cs:941"] = 1,
            ["Config/ConfigData.cs:988"] = 1,
            ["Config/ConfigData.cs:989"] = 1,
            ["Config/ConfigData.cs:993"] = 1,
            ["Config/ConfigData.cs:996"] = 1,
            ["Config/ConfigData.cs:1053"] = 1,
            ["Config/ConfigData.cs:1060"] = 1,
            ["Config/ConfigData.cs:1061"] = 1,
            ["Config/ConfigData.cs:1067"] = 1,
            ["Config/ConfigData.cs:1074"] = 1,
            ["Config/ConfigData.cs:1075"] = 1,
            ["Config/ConfigData.cs:1076"] = 1,
            ["Config/ConfigData.cs:1078"] = 1,
            ["Config/JSON/JSONConfig.cs:20"] = 1,
            ["Config/JSON/JSONConfig.cs:22"] = 1,
            ["Config/JSON/JSONConfig.cs:26"] = 1,
            ["Config/JSON/JSONConfig.cs:30"] = 1,
            ["Config/JSON/JSONConfig.cs:34"] = 1,
            ["Config/JSON/JSONConfig.cs:36"] = 1,
            ["Config/JSON/JSONConfig.cs:48"] = 1,
            ["Config/JSON/JSONConfig.cs:49"] = 1,
            ["Script/Data/ConstantData.cs:224"] = 1,
            ["Script/Data/ConstantData.cs:1714"] = 1,
            // （issues/13 M5：ErbIrCache +清单 sidecar 读写与 TryDeleteCache——全部落在 appdata
            //   IRCache 布局与本地 _IRCache（真实文件系统），content-URI 永不直连，归属同孤儿清理；
            //   +gameKey 解码规范化 +10 行（NormalizeExeDirForKey），直连 I/O 行号整体平移：
            //   CleanupOrphans 四调用点现居 356/359/366/372）
            ["Script/Loader/ErbIrCache.cs:219"] = 1,
            ["Script/Loader/ErbIrCache.cs:221"] = 1,
            ["Script/Loader/ErbIrCache.cs:265"] = 1,
            ["Script/Loader/ErbIrCache.cs:279"] = 1,
            ["Script/Loader/ErbIrCache.cs:309"] = 2,
            ["Script/Loader/ErbIrCache.cs:321"] = 1,
            ["Script/Loader/ErbIrCache.cs:322"] = 1,
            ["Script/Loader/ErbIrCache.cs:356"] = 1,
            ["Script/Loader/ErbIrCache.cs:359"] = 1,
            ["Script/Loader/ErbIrCache.cs:366"] = 1,
            ["Script/Loader/ErbIrCache.cs:372"] = 1,
            ["Script/Statements/Instraction.Child.cs:2715"] = 1,
            ["Script/Statements/Instraction.Child.cs:2779"] = 1,
            ["Utils/EncodingHandler.cs:22"] = 1,
            ["Utils/EncodingHandler.cs:84"] = 1,
            ["Utils/SafCompat.cs:28"] = 1,
            ["Utils/SafCompat.cs:34"] = 1,
            ["Utils/SafCompat.cs:40"] = 1,
            ["Utils/SafCompat.cs:46"] = 1,
            ["Utils/SafCompat.cs:65"] = 1,
            ["Utils/SafCompat.cs:78"] = 1,
            ["Utils/SafCompat.cs:79"] = 1,
            ["Utils/SafCompat.cs:88"] = 2,
            ["Utils/SafCompat.cs:94"] = 1,
            ["Utils/SafCompat.cs:101"] = 1,
            ["Utils/SafCompat.cs:107"] = 1,
            ["Utils/SafCompat.cs:113"] = 1,
            ["Utils/SafCompat.cs:120"] = 1,
            ["Utils/SafCompat.cs:128"] = 1,
            ["Utils/SafCompat.cs:129"] = 1,
            ["Utils/SafCompat.cs:210"] = 1,
            ["Utils/SafCompat.cs:233"] = 1,
            ["Utils/SafCompat.cs:234"] = 1,
            ["Utils/SafCompat.cs:235"] = 1,
            ["Utils/Sys.cs:16"] = 1,
            ["Utils/EvilMask/Utils.cs:243"] = 1,
            ["Utils/PluginSystem/PluginManager.cs:288"] = 1,
            ["Utils/PluginSystem/PluginManager.cs:294"] = 1,
            ["Utils/PluginSystem/PluginManager.cs:295"] = 1,
        };

    [Fact]
    public void Shared_runtime_direct_IO_does_not_exceed_documented_SAF_baseline()
    {
        var runtimeDir = Path.Combine(FindRepositoryRoot(), "EraCore.Core", "Shared", "Runtime");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(runtimeDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            var relativePath = Path.GetRelativePath(runtimeDir, path).Replace('\\', '/');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("/*", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal))
                    continue;
                var directIoCalls = DirectIo.Matches(line).Count;
                if (directIoCalls == 0)
                    continue;
                if (!KnownDirectIoFiles.TryGetValue(relativePath, out var reason))
                {
                    violations.Add($"{relativePath}:{index + 1}: {directIoCalls} new direct I/O call(s); no SAF classification exists.");
                    continue;
                }

                var callSite = $"{relativePath}:{index + 1}";
                if (!KnownDirectIoCallSites.TryGetValue(callSite, out var allowedCalls)
                    || directIoCalls > allowedCalls)
                {
                    violations.Add($"{callSite}: {directIoCalls} call(s) is outside the explicit baseline ({reason}).");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "Direct I/O in Shared/Runtime exceeds the documented SAF baseline:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void Shared_runtime_does_not_reintroduce_known_content_path_semantics()
    {
        var runtimeDir = Path.Combine(FindRepositoryRoot(), "EraCore.Core", "Shared", "Runtime");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(runtimeDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(path);
            var relativePath = Path.GetRelativePath(runtimeDir, path).Replace('\\', '/');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].TrimStart();
                if (line.StartsWith("//", StringComparison.Ordinal)
                    || line.StartsWith("/*", StringComparison.Ordinal)
                    || line.StartsWith("*", StringComparison.Ordinal))
                    continue;
                if (UnsafeContentPathSemantics.IsMatch(line))
                    violations.Add($"{relativePath}:{index + 1}: {line.Trim()}");
            }
        }

        Assert.True(violations.Count == 0,
            "Known unsafe content URI path semantics were reintroduced:\n" + string.Join('\n', violations));
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EraCore.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root from test output directory.");
    }
}
