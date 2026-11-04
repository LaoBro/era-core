using System;
using System.Collections.Generic;
using System.IO;

namespace EraCore.Native;

/// <summary>导入根目录结构校验的问题分类。</summary>
public enum SafImportStructureIssue
{
    /// <summary>结构完整。</summary>
    None,

    /// <summary>根目录不存在或不可读。</summary>
    NotDirectory,

    /// <summary>缺少 <c>csv/</c>（或目录为空）。</summary>
    MissingCsv,

    /// <summary>缺少 <c>erb/</c>。</summary>
    MissingErb,

    /// <summary><c>erb/</c> 存在但没有 <c>.erb</c> 文件。</summary>
    EmptyErb,
}

/// <summary>导入根目录结构校验结果：区分「源目录本身缺 csv / 缺 erb」，供日志与用户文案共用。</summary>
public sealed record SafImportStructure(SafImportStructureIssue Issue, int CsvFiles, int ErbFiles)
{
    /// <summary>结构是否满足引擎启动的最低要求（<c>csv/</c> 有文件、<c>erb/</c> 有 <c>.erb</c>）。</summary>
    public bool IsValid => Issue == SafImportStructureIssue.None;

    /// <summary>可展示的失败文案；成功时为空串。</summary>
    public string Message => Issue switch
    {
        SafImportStructureIssue.NotDirectory => "导入目录无效。",
        SafImportStructureIssue.MissingCsv => "源目录不完整：缺少 csv/，请确认选择的是游戏根目录。",
        SafImportStructureIssue.MissingErb or SafImportStructureIssue.EmptyErb =>
            "源目录不完整：缺少 erb/ 或其中没有 .erb 文件，请确认选择的是游戏根目录。",
        _ => string.Empty,
    };
}

/// <summary>
/// SAF 导入的平台无关布局规则：根目录大小写规范化与导入后结构校验。
///
/// <para><b>为什么需要规范化</b>：引擎把 <c>csv/</c> / <c>erb/</c> 等根目录名硬编码为小写
/// （<c>GamePaths</c>），而部分 provider（如 MuMu 共享目录的 ExternalStorageProvider）
/// 只返回大写拼写 <c>CSV</c> / <c>ERB</c>。私有存储是大小写敏感的 ext4，原样复制后
/// 引擎会报 <c>MISSING_CSV</c>。根层按引擎约定统一成小写，深层目录保持原名。</para>
/// </summary>
public static class SafImportLayout
{
    /// <summary>根层目录名 → 引擎期望的规范拼写（大小写不敏感匹配）。</summary>
    private static readonly Dictionary<string, string> RootDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csv"] = "csv",
        ["erb"] = "erb",
        ["resources"] = "resources",
        ["sound"] = "sound",
        ["font"] = "font",
        ["dat"] = "dat",
        ["debug"] = "debug",
    };

    /// <summary>
    /// 计算导入时使用的目录名：<paramref name="depth"/> 为 0（游戏根层）时，把引擎硬编码的
    /// 小写目录名规范成小写；其余层原样返回（调用方仍负责路径净化）。
    /// </summary>
    public static string MapDirectoryName(string displayName, int depth)
    {
        if (depth != 0 || string.IsNullOrEmpty(displayName))
            return displayName;
        return RootDirectoryNames.TryGetValue(displayName, out var canonical) ? canonical : displayName;
    }

    /// <summary>根层目录名是否被规范化（用于单次导入诊断日志，避免逐项刷屏）。</summary>
    public static bool IsNormalizedName(string displayName, int depth)
        => depth == 0 && !string.Equals(MapDirectoryName(displayName, depth), displayName, StringComparison.Ordinal);

    /// <summary>
    /// 校验导入根目录：<c>csv/</c> 必须存在且至少有一个文件，<c>erb/</c> 必须存在且至少有一个
    /// <c>.erb</c>（递归统计）。失败时调用方不得写 marker，也不得把半成品当作成功导入。
    /// </summary>
    public static SafImportStructure Validate(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || !Directory.Exists(trimmed))
            return new SafImportStructure(SafImportStructureIssue.NotDirectory, 0, 0);

        var csvDir = Path.Combine(trimmed, "csv");
        var erbDir = Path.Combine(trimmed, "erb");
        var csvFiles = CountFiles(csvDir, extension: null);
        var erbFiles = CountFiles(erbDir, extension: ".erb");

        if (!Directory.Exists(csvDir) || csvFiles == 0)
            return new SafImportStructure(SafImportStructureIssue.MissingCsv, csvFiles, erbFiles);
        if (!Directory.Exists(erbDir))
            return new SafImportStructure(SafImportStructureIssue.MissingErb, csvFiles, erbFiles);
        if (erbFiles == 0)
            return new SafImportStructure(SafImportStructureIssue.EmptyErb, csvFiles, erbFiles);

        return new SafImportStructure(SafImportStructureIssue.None, csvFiles, erbFiles);
    }

    private static int CountFiles(string dir, string? extension)
    {
        if (!Directory.Exists(dir))
            return 0;
        try
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (extension is null
                    || Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不可枚举 = 视为 0：调用方按结构不完整处理（与「源目录缺 csv」区分靠日志里的异常）。
            return 0;
        }
    }
}
