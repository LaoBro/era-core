using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// AppSettings 全局设置服务单测（ADR-0030）：canonical 键表、词表校验/规范化、Reset、
/// ApplyTo（perf/诊断旋钮/displayReport/agentLog）、与 GameConfigService.Reload 的联动。
/// </summary>
public class AppSettingsTests
{
    private static AppSettings CreateSettings(Dictionary<string, string>? seed = null)
    {
        var settings = new AppSettings(new InMemoryAppSettingsStore());
        if (seed != null)
            foreach (var kv in seed)
                Assert.True(settings.Set(kv.Key, kv.Value), $"seed {kv.Key}={kv.Value} 应合法");
        return settings;
    }

    [Fact]
    public void Canonical_keys_has_13_entries()
    {
        Assert.Equal(13, AppSettings.CanonicalKeys.Length);
    }

    [Fact]
    public void Get_returns_defaults_when_unset()
    {
        var settings = CreateSettings();
        Assert.Equal("auto", settings.Get(AppSettings.SweepGatePolicyKey));
        Assert.Equal("auto", settings.Get(AppSettings.LazyFunctionLoadKey));
        Assert.Equal("auto", settings.Get(AppSettings.DisplayReportOverrideKey));
    }

    [Fact]
    public void Set_rejects_unknown_key()
    {
        var settings = CreateSettings();
        Assert.False(settings.Set("not-a-key", "auto"));
    }

    [Theory]
    [InlineData("displayReportOverride", "bogus")]
    [InlineData("sweepGatePolicy", "forceanything")]
    [InlineData("dimLockPolicy", "lock")]
    [InlineData("maxParallelErbFiles", "32")]
    [InlineData("lazyFunctionLoad", "true")]
    public void Set_rejects_invalid_values(string key, string value)
    {
        var settings = CreateSettings();
        Assert.False(settings.Set(key, value));
    }

    [Theory]
    [InlineData("displayReportOverride", "off")]
    [InlineData("sweepGatePolicy", "forceoff")]
    [InlineData("safReadDegree", "8")]
    [InlineData("maxParallelErbFiles", "16")]
    [InlineData("dimLockPolicy", "nolock")]
    [InlineData("parallelErbLoad", "on")]
    public void Set_accepts_canonical_values(string key, string value)
    {
        var settings = CreateSettings();
        Assert.True(settings.Set(key, value));
        Assert.Equal(value, settings.Get(key));
    }

    [Fact]
    public void Saf_read_degree_clamped_and_normalized()
    {
        var settings = CreateSettings();
        Assert.True(settings.Set(AppSettings.SafReadDegreeKey, "999"));
        Assert.Equal("64", settings.Get(AppSettings.SafReadDegreeKey));
        Assert.True(settings.Set(AppSettings.SafReadDegreeKey, "08"));
        Assert.Equal("8", settings.Get(AppSettings.SafReadDegreeKey));
        Assert.False(settings.Set(AppSettings.SafReadDegreeKey, "abc"));
    }

    [Fact]
    public void Reset_clears_store_and_returns_defaults()
    {
        var settings = CreateSettings(new Dictionary<string, string>
        {
            [AppSettings.SweepGatePolicyKey] = "forceoff",
            [AppSettings.SafReadDegreeKey] = "4",
        });
        settings.Reset();
        Assert.Equal("auto", settings.Get(AppSettings.SweepGatePolicyKey));
        Assert.Equal("auto", settings.Get(AppSettings.SafReadDegreeKey));
    }

    [Fact]
    public void GetAll_returns_all_13_keys()
    {
        var settings = CreateSettings(new Dictionary<string, string> { [AppSettings.SweepGatePolicyKey] = "forceoff" });
        var all = settings.GetAll();
        Assert.Equal(13, all.Count);
        Assert.Equal("forceoff", all[AppSettings.SweepGatePolicyKey]);
        Assert.Equal("auto", all[AppSettings.LazyFunctionLoadKey]);
    }

    // ---- ApplyTo ----

    [Fact]
    public void ApplyTo_applies_perf_override_to_config_item()
    {
        var settings = CreateSettings(new Dictionary<string, string> { [AppSettings.LazyFunctionLoadKey] = "off" });
        var config = new ConfigData();
        Assert.True(config.GetConfigValue<bool>(ConfigCode.LazyFunctionLoad)); // 默认 true

        settings.ApplyTo(config);

        Assert.False(config.GetConfigValue<bool>(ConfigCode.LazyFunctionLoad));
    }

    [Fact]
    public void ApplyTo_does_not_touch_perf_when_auto()
    {
        var settings = CreateSettings();
        var config = new ConfigData();
        settings.ApplyTo(config);
        Assert.True(config.GetConfigValue<bool>(ConfigCode.LazyFunctionLoad)); // 保持默认
    }

    [Fact]
    public void ApplyTo_applies_diagnostic_knobs()
    {
        var settings = CreateSettings(new Dictionary<string, string>
        {
            [AppSettings.SweepGatePolicyKey] = "forceoff",
            [AppSettings.SafReadDegreeKey] = "4",
            [AppSettings.MaxParallelErbFilesKey] = "16",
            [AppSettings.DimLockPolicyKey] = "nolock",
        });
        var config = new ConfigData();

        settings.ApplyTo(config);

        Assert.Equal(SweepGatePolicy.ForceOff, config.OverrideSweepGatePolicy);
        Assert.Equal(4, config.OverrideSafReadDegree);
        Assert.Equal(16, config.OverrideMaxParallelErbFiles);
        Assert.Equal(DimLockPolicy.NoLock, config.OverrideDimLockPolicy);
    }

    [Fact]
    public void ApplyTo_auto_knobs_leave_overrides_null()
    {
        var settings = CreateSettings();
        var config = new ConfigData();
        settings.ApplyTo(config);
        Assert.Null(config.OverrideSweepGatePolicy);
        Assert.Null(config.OverrideSafReadDegree);
        Assert.Null(config.OverrideMaxParallelErbFiles);
        Assert.Null(config.OverrideDimLockPolicy);
    }

    [Fact]
    public void ApplyTo_display_report_override_off_presses_item()
    {
        var settings = CreateSettings(new Dictionary<string, string> { [AppSettings.DisplayReportOverrideKey] = "off" });
        var config = new ConfigData();

        settings.ApplyTo(config);

        Assert.False(config.OverrideDisplayReport);
        Assert.False(config.GetConfigValue<bool>(ConfigCode.DisplayReport));
    }

    [Fact]
    public void ApplyTo_agent_log_only_when_explicitly_set()
    {
        // 未设置——不干预（保持宿主启动期语义）
        var prev = AgentLog.Instance.Enabled;
        try
        {
            var settings = CreateSettings();
            var config = new ConfigData();
            settings.ApplyTo(config);
            Assert.Equal(prev, AgentLog.Instance.Enabled);

            // 显式设置——即时应用
            var on = CreateSettings(new Dictionary<string, string> { [AppSettings.AgentLogEnabledKey] = "true" });
            on.ApplyTo(config);
            Assert.True(AgentLog.Instance.Enabled);
        }
        finally
        {
            AgentLog.Instance.Enabled = prev;
        }
    }

    [Fact]
    public void InMemory_store_is_thread_safe_under_concurrent_writes()
    {
        var store = new InMemoryAppSettingsStore();
        var errors = new System.Collections.Concurrent.ConcurrentBag<System.Exception>();
        Parallel.For(0, 64, i =>
        {
            try
            {
                if (i % 2 == 0)
                    store.Set("key" + (i % 8), "v" + i);
                else
                {
                    _ = store.Get("key" + (i % 8));
                    _ = store.GetAll();
                }
            }
            catch (System.Exception ex)
            {
                errors.Add(ex);
            }
        });
        Assert.Empty(errors);
    }

    // ---- 与 GameConfigService.Reload 联动 ----

    [Fact]
    public void Reload_applies_store_seeded_display_report_override()
    {
        // 等价于旧 --no-loading-report（seed 走 store，不传 bool 参数）
        using var tmp = new TempGameDir();
        File.WriteAllText(
            Path.Combine(tmp.Root, "emuera.config"),
            "; test\nロード時にレポートを表示する : TRUE\n",
            System.Text.Encoding.UTF8);

        var settings = CreateSettings(new Dictionary<string, string> { [AppSettings.DisplayReportOverrideKey] = "off" });
        var svc = new GameConfigService(new ConfigData(), appSettings: settings);

        var reloaded = svc.Reload(tmp.Root);

        Assert.False(reloaded.GetConfigValue<bool>(ConfigCode.DisplayReport));
    }

    [Fact]
    public void Reload_applies_store_seeded_diagnostic_knob()
    {
        using var tmp = new TempGameDir();
        File.WriteAllText(Path.Combine(tmp.Root, "emuera.config"), "; test\n");

        var settings = CreateSettings(new Dictionary<string, string> { [AppSettings.SweepGatePolicyKey] = "forceoff" });
        var svc = new GameConfigService(new ConfigData(), appSettings: settings);

        var reloaded = svc.Reload(tmp.Root);

        Assert.Equal(SweepGatePolicy.ForceOff, reloaded.OverrideSweepGatePolicy);
    }

    private sealed class TempGameDir : System.IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "emuera_appsettings_" + System.Guid.NewGuid().ToString("N"));
        public TempGameDir() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* 临时目录清理失败不致测试失败 */ }
        }
    }
}
