using System;
using System.Collections.Generic;
using Microsoft.Maui.Storage;
using MinorShift.Emuera.Runtime.Config;

namespace EraCore.Maui.Services;

/// <summary>
/// MAUI 全局设置存储（ADR-0030）——<see cref="IAppSettingsStore"/> 的 Preferences 实现。
/// 键常量沿用 <see cref="BridgeHost"/>（<c>app.perf.*</c>/<c>app.diag.*</c> 等），数据格式与旧
/// BridgeHost 直接读写逐位一致（agentLogEnabled 存 bool，其余存字符串，缺省 "auto"）。
/// 行为兼容：Preferences 不可用时静默回退（Get→默认，Set→丢弃，与旧 try/catch 一致）。
/// </summary>
internal sealed class PreferencesAppSettingsStore : IAppSettingsStore
{
    private static readonly IReadOnlyDictionary<string, string> KeyMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AppSettings.DisplayReportOverrideKey] = BridgeHost.DisplayReportOverrideKey,
        [AppSettings.AgentLogEnabledKey] = BridgeHost.AgentLogEnabledKey,
        [AppSettings.SweepGatePolicyKey] = BridgeHost.DiagSweepGatePolicyKey,
        [AppSettings.SafReadDegreeKey] = BridgeHost.DiagSafReadDegreeKey,
        [AppSettings.MaxParallelErbFilesKey] = BridgeHost.DiagMaxParallelErbFilesKey,
        [AppSettings.DimLockPolicyKey] = BridgeHost.DiagDimLockPolicyKey,
        [AppSettings.LazyFunctionLoadKey] = BridgeHost.PerfLazyFunctionLoadKey,
        [AppSettings.IrDiskCacheLoadKey] = BridgeHost.PerfIrDiskCacheLoadKey,
        [AppSettings.IrFirstDirListKey] = BridgeHost.PerfIrFirstDirListKey,
        [AppSettings.IrFileListCacheKey] = BridgeHost.PerfIrFileListCacheKey,
        [AppSettings.SrcContentCacheKey] = BridgeHost.PerfSrcContentCacheKey,
        [AppSettings.ParallelErbLoadKey] = BridgeHost.PerfParallelErbLoadKey,
        [AppSettings.ParallelParseScriptKey] = BridgeHost.PerfParallelParseScriptKey,
    };

    private static bool IsBoolKey(string key) => key == AppSettings.AgentLogEnabledKey;

    public string? Get(string key)
    {
        if (!KeyMap.TryGetValue(key, out var prefKey))
            return null;
        try
        {
            // 契约：未设置返回 null（与 InMemory store 一致）——agentLog 的「仅显式设置时应用」
            // 守卫依赖此语义（settingsReset 后未设置 → ApplyTo 不再干预）。
            if (!Preferences.ContainsKey(prefKey))
                return null;
            return IsBoolKey(key)
                ? (Preferences.Get(prefKey, false) ? "true" : "false")
                : Preferences.Get(prefKey, "auto");
        }
        catch
        {
            return null;
        }
    }

    public void Set(string key, string? value)
    {
        if (!KeyMap.TryGetValue(key, out var prefKey))
            return;
        try
        {
            if (value == null)
            {
                Preferences.Remove(prefKey);
                return;
            }
            if (IsBoolKey(key))
                Preferences.Set(prefKey, value == "true");
            else
                Preferences.Set(prefKey, value);
        }
        catch
        {
            // Preferences 不可用——仅持久化失败，运行时状态仍由 AppSettings 维护
        }
    }

    public IEnumerable<KeyValuePair<string, string>> GetAll()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in AppSettings.CanonicalKeys)
        {
            if (Get(key) is { } value)
                dict[key] = value;
        }
        return dict;
    }

    public void Reset()
    {
        foreach (var prefKey in KeyMap.Values)
        {
            try
            {
                Preferences.Remove(prefKey);
            }
            catch
            {
                // Preferences 不可用——忽略
            }
        }
    }
}
