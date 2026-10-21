using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MinorShift.Emuera.Files;

/// <summary>目录子项（一次 children Query 的一行）。Mime 为 Provider 原始字符串。</summary>
internal readonly record struct ChildEntry(string DocId, string Name, string Mime);

/// <summary>
/// O1 目录级子项缓存（saf-accel 计划）——目录 docId → 子项列表。
/// <para>
/// 目的：把 SAF 的跨进程 Binder IPC 从「按需逐次查询」降为「每目录一次」：
/// 命中路径零 IPC；写路径（本进程）精确失效，不设 TTL（进程外修改盲区待 O4 观察后定）。
/// </para>
/// <para>
/// 线程安全：<see cref="ConcurrentDictionary{TKey,TValue}"/> + 只整体替换 value 引用
/// （copy-on-write，不改动已放入的列表）——读线程可安全迭代旧快照，读路径无锁。
/// </para>
/// <para>key = 目录 docId（如 <c>primary:emuera/TK1.29.3/ERB</c>），大小写不敏感。</para>
/// </summary>
internal sealed class DirectoryChildCache
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ChildEntry>> _map
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取指定目录的子项列表快照；未缓存返回 null。</summary>
    public IReadOnlyList<ChildEntry>? Get(string directoryDocId)
        => _map.TryGetValue(directoryDocId, out var children) ? children : null;

    /// <summary>写入/覆盖指定目录的子项列表（防御性拷贝，调用方后续改动不影响缓存）。</summary>
    public void Put(string directoryDocId, IReadOnlyList<ChildEntry> children)
    {
        // 只出现在 miss 路径（成本远小于一次 IPC），防御性拷贝防调用方后续改动污染缓存
        _map[directoryDocId] = new List<ChildEntry>(children);
    }

    /// <summary>失效指定目录（写/删/建目录后调用）。</summary>
    public void Invalidate(string directoryDocId)
        => _map.TryRemove(directoryDocId, out _);

    /// <summary>清空全部缓存（切换树根时调用）。</summary>
    public void Clear()
        => _map.Clear();
}
