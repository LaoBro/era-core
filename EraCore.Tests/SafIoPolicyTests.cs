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
            ["Script/Loader/SrcContentCache.cs"] = "issues/10 10-B：CSV/ERH 内容缓存——appdata IRCache 布局（真实文件系统 tmp 原子替换/自愈删除），游戏内容永经 accessor",
            ["Script/Statements/Instraction.Child.cs"] = "阶段 3：声音文件访问",
            ["Utils/EncodingHandler.cs"] = "阶段 1：编码探测",
            ["Utils/SafCompat.cs"] = "本地路径分支的合法实现",
            ["Utils/Sys.cs"] = "进程工作目录，不解析游戏文件",
            ["Utils/EvilMask/Lang.cs"] = "阶段 3：自定义语言文件",
            ["Utils/EvilMask/Utils.cs"] = "非 HEADLESS 图像加载",
            ["Utils/PluginSystem/PluginManager.cs"] = "阶段 3：Android 插件策略",
        };

    // 每文件调用总数基线（issues/14 裁定降级，2026-09-09）：原行号级基线在每次平移时产生
    // 纯机械警报（issues/12 M4 / 13 M5 / 10-B / 14 四次平移，直连 I/O 归属从未变化）。
    // 降级为每文件总数后仍能抓住两类真问题——「白名单文件内新增直连调用」（计数上涨）、
    // 「新文件直连」（文件级白名单拦下）；行号平移不再触发。基线 = 上限：删除调用（计数
    // 下降）无需改基线（与本文件原注释惯例一致）；超出必须先作出明确的 SAF 设计决定。
    // 基线值 = 2026-09-09 用测试同款规则实测（scan 脚本见 .scratch/diagnostic-knobs/scan_direct_io.py）。
    private static readonly IReadOnlyDictionary<string, int> KnownDirectIoCallCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Config/ConfigData.cs"] = 13,
            ["Config/JSON/JSONConfig.cs"] = 8,
            ["Script/Data/ConstantData.cs"] = 2,
            ["Script/Loader/ErbIrCache.cs"] = 12,
            ["Script/Loader/SrcContentCache.cs"] = 8,
            // 历史遗留：行号基线曾登记 2 处（:2715/:2779），现实际为 0（调用已移除、行号基线陈旧）。
            // 显式登记 0：未来该文件新增调用时按「超出基线」报错并附带归属理由。
            ["Script/Statements/Instraction.Child.cs"] = 0,
            ["Utils/EncodingHandler.cs"] = 2,
            ["Utils/SafCompat.cs"] = 20,
            ["Utils/Sys.cs"] = 1,
            ["Utils/EvilMask/Lang.cs"] = 0,
            ["Utils/EvilMask/Utils.cs"] = 1,
            ["Utils/PluginSystem/PluginManager.cs"] = 3,
        };

    [Fact]
    public void Shared_runtime_direct_IO_does_not_exceed_documented_SAF_baseline()
    {
        var runtimeDir = Path.Combine(FindRepositoryRoot(), "EraCore.Core", "Shared", "Runtime");
        var violations = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
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
                counts[relativePath] = counts.GetValueOrDefault(relativePath) + DirectIo.Matches(line).Count;
            }
        }

        foreach (var (relativePath, actual) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            // 0 调用文件不进入判定（旧实现的同款跳过）——否则全仓未白名单文件都会误报。
            if (actual == 0)
                continue;
            if (!KnownDirectIoFiles.TryGetValue(relativePath, out var reason))
            {
                violations.Add($"{relativePath}: {actual} new direct I/O call(s); no SAF classification exists.");
                continue;
            }

            // 未登记文件 = 基线 0；实际 ≤ 基线（删除无需改基线）不报，超出才拦。
            var allowed = KnownDirectIoCallCounts.GetValueOrDefault(relativePath);
            if (actual > allowed)
            {
                violations.Add($"{relativePath}: {actual} call(s) exceeds documented baseline {allowed} ({reason}).");
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
