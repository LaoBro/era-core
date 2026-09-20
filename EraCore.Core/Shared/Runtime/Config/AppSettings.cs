using System;
using System.Collections.Generic;
using MinorShift.Emuera.GameView;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// 全局设置（ADR-0030）的存储抽象——<see cref="AppSettings"/> 服务经它读写，宿主提供实现：
/// MAUI = Preferences（<c>app.perf.*</c>/<c>app.diag.*</c>，持久化）；headless = 内存字典
/// （<c>--setting</c> 启动参数种子，不持久化）。<c>Get</c> 未设置时返回 <c>null</c>；
/// <c>Set(key, null)</c> 清除该键。
/// </summary>
internal interface IAppSettingsStore
{
    /// <summary>读取单键——未设置返回 <c>null</c>（与「显式设了默认值」区分，agentLog 未设置时保持宿主启动期语义）。</summary>
    string? Get(string key);

    /// <summary>写入单键（<c>null</c> = 清除）。</summary>
    void Set(string key, string? value);

    /// <summary>当前已设置的键值快照（不含未设置键）。</summary>
    IEnumerable<KeyValuePair<string, string>> GetAll();

    /// <summary>清空全部——reset 语义（回到未设置=出厂默认）。</summary>
    void Reset();
}

/// <summary>
/// 全局设置语义单点（ADR-0030）：canonical 键表 + 词表校验/规范化 + <see cref="ApplyTo"/> +
/// <see cref="Reset"/>。存储经 <see cref="IAppSettingsStore"/> 抽象，与宿主解耦。
///
/// canonical 键 = 13 个（与前端 <c>game</c> store wire 字段同名，<c>/settings</c> 端点与
/// <c>eracore_agent settings</c> 子命令零转换复用）：displayReportOverride、agentLogEnabled、
/// 4 枚诊断旋钮（sweepGatePolicy / safReadDegree / maxParallelErbFiles / dimLockPolicy）+
/// 7 个性能覆盖（lazyFunctionLoad / irDiskCacheLoad / irFirstDirList / irFileListCache /
/// srcContentCache / parallelErbLoad / parallelParseScript）。
///
/// 生效语义（与 MAUI 现状逐位对齐）：<see cref="ApplyTo"/> 在 load-game 时应用全部——
/// perf 覆盖/诊断旋钮「下次进游戏生效」；displayReportOverride / agentLogEnabled「即时生效」
/// （agentLog 仅显式设置时应用，未设置保持宿主启动期语义——MAUI Preferences 初值 / headless 环境变量）。
/// </summary>
internal sealed class AppSettings
{
    public const string DisplayReportOverrideKey = "displayReportOverride";
    public const string AgentLogEnabledKey = "agentLogEnabled";
    public const string SweepGatePolicyKey = "sweepGatePolicy";
    public const string SafReadDegreeKey = "safReadDegree";
    public const string MaxParallelErbFilesKey = "maxParallelErbFiles";
    public const string DimLockPolicyKey = "dimLockPolicy";
    public const string LazyFunctionLoadKey = "lazyFunctionLoad";
    public const string IrDiskCacheLoadKey = "irDiskCacheLoad";
    public const string IrFirstDirListKey = "irFirstDirList";
    public const string IrFileListCacheKey = "irFileListCache";
    public const string SrcContentCacheKey = "srcContentCache";
    public const string ParallelErbLoadKey = "parallelErbLoad";
    public const string ParallelParseScriptKey = "parallelParseScript";

    /// <summary>canonical 键全集（枚举顺序 = <c>/settings</c> 响应字段顺序，稳定）。</summary>
    internal static readonly string[] CanonicalKeys =
    [
        DisplayReportOverrideKey,
        AgentLogEnabledKey,
        SweepGatePolicyKey,
        SafReadDegreeKey,
        MaxParallelErbFilesKey,
        DimLockPolicyKey,
        LazyFunctionLoadKey,
        IrDiskCacheLoadKey,
        IrFirstDirListKey,
        IrFileListCacheKey,
        SrcContentCacheKey,
        ParallelErbLoadKey,
        ParallelParseScriptKey,
    ];

    /// <summary>canonical 键 → <see cref="ConfigCode"/>（perf 覆盖压 emuera.config 项的映射）。</summary>
    private static readonly IReadOnlyDictionary<string, ConfigCode> PerfKeys = new Dictionary<string, ConfigCode>(StringComparer.Ordinal)
    {
        [LazyFunctionLoadKey] = ConfigCode.LazyFunctionLoad,
        [IrDiskCacheLoadKey] = ConfigCode.IrDiskCacheLoad,
        [IrFirstDirListKey] = ConfigCode.IrFirstDirList,
        [IrFileListCacheKey] = ConfigCode.IrFileListCache,
        [SrcContentCacheKey] = ConfigCode.SrcContentCache,
        [ParallelErbLoadKey] = ConfigCode.ParallelErbLoad,
        [ParallelParseScriptKey] = ConfigCode.ParallelParseScript,
    };

    private readonly IAppSettingsStore _store;

    internal AppSettings()
        : this(new InMemoryAppSettingsStore())
    {
    }

    internal AppSettings(IAppSettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>键是否属于 canonical 集。</summary>
    internal static bool IsCanonicalKey(string key)
    {
        foreach (var k in CanonicalKeys)
            if (string.Equals(k, key, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>未设置时的有效默认值——除 agentLog 外一律 "auto"（跟随游戏/默认）。</summary>
    internal static string DefaultValue(string key) => key == AgentLogEnabledKey ? "false" : "auto";

    /// <summary>
    /// 读取单键的有效值（未设置回默认）。agentLog 报告<em>运行时真实状态</em>
    /// （<see cref="AgentLog.Enabled"/>，与 MAUI <c>config</c> 消息推送的权威态一致），
    /// 而非存储原始值——显式设置会即时写进 AgentLog，故两者天然一致。
    /// </summary>
    internal string Get(string key)
    {
        if (string.Equals(key, AgentLogEnabledKey, StringComparison.Ordinal))
            return AgentLog.Instance.Enabled ? "true" : "false";
        return _store.Get(key) ?? DefaultValue(key);
    }

    /// <summary>
    /// 全量快照（13 键齐备）——<c>GET /settings</c> 与 <c>settings list</c> 的响应源。
    /// </summary>
    internal IReadOnlyDictionary<string, string> GetAll()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in CanonicalKeys)
            dict[key] = Get(key);
        return dict;
    }

    /// <summary>
    /// 设置单键。canonical 键 + 词表校验 + 规范化（safReadDegree 钳制 1–64），
    /// 非法返回 <c>false</c>（不写 store）。合法写入 store；agentLogEnabled 同时即时应用
    /// <see cref="AgentLog.Enabled"/>（与 MAUI 运行时切换一致）。
    /// </summary>
    internal bool Set(string key, string? value)
    {
        if (!IsCanonicalKey(key))
            return false;
        if (string.Equals(key, AgentLogEnabledKey, StringComparison.Ordinal))
        {
            if (value is not ("true" or "false"))
                return false;
            _store.Set(key, value);
            AgentLog.Instance.Enabled = value == "true";
            return true;
        }
        var canonical = Canonicalize(key, value);
        if (canonical == null)
            return false;
        _store.Set(key, canonical);
        return true;
    }

    /// <summary>重置为出厂默认（store 清空；agentLog 显式态清除，后续 ApplyTo 不再干预）。</summary>
    internal void Reset() => _store.Reset();

    /// <summary>
    /// load-game 时应用全局设置到新 ConfigData（ADR-0030，headless 在 <see cref="GameConfigService.Reload"/>、
    /// MAUI 在 <c>MainPage.OnReloadGame</c>）。压过三源合并结果；保留既有取证日志
    /// （requested/effective/fixed + platform tag），与旧 BridgeHost.Apply* 逐位一致。
    /// </summary>
    internal void ApplyTo(ConfigData configData)
    {
        var platform = PlatformTag;

        // ---- perf 覆盖（7 键）：仅内存压 emuera.config 项，不落盘 ----
        foreach (var kv in PerfKeys)
        {
            var requested = Get(kv.Key);
            try
            {
                var item = configData.GetItem(kv.Value);
                if (requested != "auto")
                    item.SetValue(requested == "on");
                Log($"perf override {kv.Value}: platform={platform} requested={requested} " +
                    $"effective={(item.GetValue<bool>() ? "on" : "off")} fixed={item.Fixed}");
            }
            catch (Exception ex)
            {
                Log($"perf override {kv.Value}: platform={platform} requested={requested} error={ex.Message}");
            }
        }

        // ---- 诊断旋钮（4 枚）：仅内存覆盖写 Override*，ErbLoader/Preload 读取 ----
        var sweep = Get(SweepGatePolicyKey);
        var sweepPolicy = SweepGatePolicyCodec.Parse(sweep);
        configData.OverrideSweepGatePolicy = sweepPolicy == SweepGatePolicy.Auto ? null : sweepPolicy;
        Log($"sweep gate policy override: platform={platform} requested={sweep} " +
            $"effective={SweepGatePolicyCodec.ToConfigString(sweepPolicy)}");

        var saf = SafReadDegreeCodec.Parse(Get(SafReadDegreeKey));
        configData.OverrideSafReadDegree = saf;
        Log($"saf read degree override: platform={platform} requested={Get(SafReadDegreeKey)} " +
            $"effective={SafReadDegreeCodec.ToConfigString(saf)}");

        var maxParallel = MaxParallelErbFilesCodec.Parse(Get(MaxParallelErbFilesKey));
        configData.OverrideMaxParallelErbFiles = maxParallel;
        Log($"max parallel erb files override: platform={platform} requested={Get(MaxParallelErbFilesKey)} " +
            $"effective={MaxParallelErbFilesCodec.ToConfigString(maxParallel)}");

        var dim = DimLockPolicyCodec.Parse(Get(DimLockPolicyKey));
        configData.OverrideDimLockPolicy = dim == DimLockPolicy.NoLock ? DimLockPolicy.NoLock : null;
        Log($"dim lock policy override: platform={platform} requested={Get(DimLockPolicyKey)} " +
            $"effective={DimLockPolicyCodec.ToConfigString(dim)}");

        // ---- 启动日志覆盖（三态）：先置覆盖标志，再压 DisplayReport 项（auto 不压，保持游戏自身值）----
        var dr = ParseDisplayReportOverride(Get(DisplayReportOverrideKey));
        configData.OverrideDisplayReport = dr;
        if (dr.HasValue)
            configData.GetItem(ConfigCode.DisplayReport).SetValue(dr.Value);
        Log($"display report override: platform={platform} requested={Get(DisplayReportOverrideKey)} " +
            $"effective={DisplayReportOverrideToString(dr)}");

        // ---- agent 文件日志：仅显式设置时应用（未设置保持宿主启动期语义）----
        if (_store.Get(AgentLogEnabledKey) is { } agentLog)
        {
            AgentLog.Instance.Enabled = agentLog == "true";
            Log($"agent log enabled override: platform={platform} requested={agentLog} " +
                $"effective={(AgentLog.Instance.Enabled ? "true" : "false")}");
        }
    }

    /// <summary>
    /// 单键词表校验 + 规范化。非法值返回 <c>null</c>；
    /// safReadDegree 经 <see cref="SafReadDegreeCodec"/> 规范化（数值钳制 1–64）。
    /// </summary>
    private static string? Canonicalize(string key, string? value)
    {
        if (value == null)
            return null;
        switch (key)
        {
            case DisplayReportOverrideKey:
            case LazyFunctionLoadKey:
            case IrDiskCacheLoadKey:
            case IrFirstDirListKey:
            case IrFileListCacheKey:
            case SrcContentCacheKey:
            case ParallelErbLoadKey:
            case ParallelParseScriptKey:
                return value is "auto" or "off" or "on" ? value : null;
            case SweepGatePolicyKey:
                return value is "auto" or "forceon" or "forceoff" ? value : null;
            case SafReadDegreeKey:
                return value == "auto" ? value : SafReadDegreeCodec.Parse(value) is { } d ? SafReadDegreeCodec.ToConfigString(d) : null;
            case MaxParallelErbFilesKey:
                return value is "auto" or "4" or "8" or "16" ? value : null;
            case DimLockPolicyKey:
                return value is "auto" or "nolock" ? value : null;
            default:
                return null;
        }
    }

    private static bool? ParseDisplayReportOverride(string value) => value switch
    {
        "off" => false,
        "on" => true,
        _ => null,
    };

    private static string DisplayReportOverrideToString(bool? value) => value switch
    {
        false => "off",
        true => "on",
        _ => "auto",
    };

    private static string PlatformTag
    {
        get
        {
            // 运行时判定（net10 提供）——不能依赖 #if ANDROID：该宏只在 Maui 工程定义，
            // Core 在 Android 构建里恒编译成 desktop，取证日志的 platform= 会误导。
            return OperatingSystem.IsAndroid() ? "android" : "desktop";
        }
    }

    private static void Log(string message) => EmueraLog.Info("config", message);
}
