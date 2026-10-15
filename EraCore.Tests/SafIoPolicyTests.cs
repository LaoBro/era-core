using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 防止重新引入已知的、对 content URI 不安全的路径语义（<c>Path.GetRelativePath</c> 等
/// 按段切分 URI 的用法）。
/// <para>
/// 「直连本地 IO 例外清单」已迁至 <see cref="GameDirAccessorIoPolicyTests"/>——后者覆盖全 Core
/// （而不仅是本文件原先守的 Shared/Runtime）并带归属域登记。本类只保留路径语义一侧的规则，
/// 避免两处例外清单各自漂移。
/// </para>
/// </summary>
public class SafIoPolicyTests
{
    private static readonly Regex UnsafeContentPathSemantics = new(
        @"\bPath\.GetRelativePath\s*\(|\bPath\.GetDirectoryName\s*\(\s*csvPath\s*\)|\bPath\.GetFileNameWithoutExtension\s*\(\s*csvPath\s*\)",
        RegexOptions.Compiled);

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
