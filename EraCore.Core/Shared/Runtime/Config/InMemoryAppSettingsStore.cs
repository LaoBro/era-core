using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// headless / Web 模式的全局设置存储（ADR-0030）——内存字典，<c>--setting</c> 启动参数种子，
/// <b>不持久化</b>（server 重启回默认，对自动测试是特性：可重复、无污染）。线程安全用
/// <see cref="lock"/> 语句保护（/settings 端点与 CLI 种子可能跨线程读写）。
/// </summary>
internal sealed class InMemoryAppSettingsStore : IAppSettingsStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Get(string key)
    {
        lock (_lock)
            return _values.TryGetValue(key, out var value) ? value : null;
    }

    public void Set(string key, string? value)
    {
        lock (_lock)
        {
            if (value == null)
                _values.Remove(key);
            else
                _values[key] = value;
        }
    }

    public IEnumerable<KeyValuePair<string, string>> GetAll()
    {
        lock (_lock)
            return new Dictionary<string, string>(_values, StringComparer.Ordinal);
    }

    public void Reset()
    {
        lock (_lock)
            _values.Clear();
    }
}
