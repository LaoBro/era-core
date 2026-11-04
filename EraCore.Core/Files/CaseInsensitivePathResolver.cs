using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Files;

/// <summary>
/// <see cref="FileSystemGameDirAccessor"/> 的逐段大小写解析判定——<b>不含 IO</b>，供单测直接驱动
/// （「请求段 + 各级父目录条目名 → 真实段」）。
/// <para>
/// 每个请求段在其父目录的条目名里解析，规则依次为：
/// <b>1. 精确名（<see cref="StringComparison.Ordinal"/>）优先</b>；
/// 2. 否则按 <see cref="StringComparison.OrdinalIgnoreCase"/> 找匹配——唯一则采用；
/// 3. 忽略大小写命中多个<b>不同</b>名字时<b>放弃容错</b>（不猜，如实 miss）。
/// </para>
/// <para>
/// <c>.</c> / <c>..</c> / 空段一律拒绝：每段的真实名都取自其父目录的枚举结果，因此解析结果
/// 恒在请求路径原本的父级之下，不产生越界或符号链接扩张。
/// </para>
/// </summary>
internal static class CaseInsensitivePathResolver
{
    /// <param name="segments">相对「已存在的可信前缀」的请求段（不含分隔符）。</param>
    /// <param name="entriesOf">
    /// 第 i 段父目录的条目名；返回 <see langword="null"/> 表示该父目录不可枚举（解析失败）。
    /// </param>
    /// <returns>各级真实名；任一段无唯一匹配时返回 <see langword="null"/>。</returns>
    public static string[]? Resolve(
        IReadOnlyList<string> segments,
        Func<int, IReadOnlyList<string>?> entriesOf)
    {
        var result = new string[segments.Count];
        for (var i = 0; i < segments.Count; i++)
        {
            var entries = entriesOf(i);
            var match = entries == null ? null : MatchSegment(entries, segments[i]);
            if (match == null)
                return null;
            result[i] = match;
        }
        return result;
    }

    /// <summary>单段解析：精确名优先；忽略大小写唯一匹配；多候选放弃容错。</summary>
    public static string? MatchSegment(IReadOnlyList<string> entries, string requested)
    {
        if (string.IsNullOrEmpty(requested) || requested == "." || requested == "..")
            return null;

        string? insensitive = null;
        var ambiguous = false;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (string.IsNullOrEmpty(entry))
                continue;
            if (string.Equals(entry, requested, StringComparison.Ordinal))
                return entry; // 精确名优先：存在同名条目时不再看大小写变体
            if (!string.Equals(entry, requested, StringComparison.OrdinalIgnoreCase))
                continue;
            if (insensitive == null)
                insensitive = entry;
            else if (!string.Equals(insensitive, entry, StringComparison.Ordinal))
                ambiguous = true; // 同一目录内有多个仅大小写不同的条目 → 不猜
        }
        return ambiguous ? null : insensitive;
    }
}
