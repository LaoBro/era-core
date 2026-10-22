using MinorShift.Emuera.Files;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.UI;
using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 候选 2 / ADR-0009 回归测试：Config 静态上帝类 → 薄视图 + ambient Current（AsyncLocal）。
/// 验证 scope 生命周期、并行隔离、以及视图从注入的 ConfigData 读值（含 FontFactory 集成）。
/// </summary>
public class ConfigScopeTests
{
    /// <summary>
    /// T-a：scope 生命周期契约——OpenScope(configData) 后 Config.Current / ConfigData.Current 均非 null，
    /// 且 Config 静态属性回读注入的 ConfigData；Dispose 后两者归 null。
    /// </summary>
    [Fact]
    public void OpenScope_binds_Config_and_Dispose_clears_it()
    {
        Assert.Null(Config.Current);
        Assert.Null(ConfigData.Current);

        var cd = new ConfigData();
        using (var scope = GlobalStatic.OpenScope(cd))
        {
            Assert.Same(cd, ConfigData.Current);
            Assert.NotNull(Config.Current);
            // 默认 FontName 经薄视图转发，等于 ConfigData 默认值。
            Assert.Equal("ＭＳ ゴシック", Config.FontName);
            Assert.Equal(18, Config.FontSize);
        }

        Assert.Null(Config.Current);
        Assert.Null(ConfigData.Current);
    }

    /// <summary>
    /// T-b：并行配置隔离契约——两并行 task 各自开 scope、注入不同 ConfigData（不同 FontName），
    /// 验证 Config.FontName 互不污染（AsyncLocal 在并行 async 上下文间隔离）。
    /// </summary>
    [Fact]
    public async Task Parallel_config_scopes_do_not_pollute_each_other()
    {
        var cdA = new ConfigData();
        cdA.GetConfigItem(ConfigCode.FontName).SetValue("FontA");
        var cdB = new ConfigData();
        cdB.GetConfigItem(ConfigCode.FontName).SetValue("FontB");

        // 不在测试线程开 scope——子 task 各自开，避免 AsyncLocal 继承。
        var results = await Task.WhenAll(
            Task.Run(async () =>
            {
                using var scope = GlobalStatic.OpenScope(cdA);
                await Task.Yield();
                return Config.FontName;
            }),
            Task.Run(async () =>
            {
                using var scope = GlobalStatic.OpenScope(cdB);
                await Task.Yield();
                return Config.FontName;
            })
        );

        Assert.Equal("FontA", results[0]);
        Assert.Equal("FontB", results[1]);
        Assert.Null(Config.Current); // 测试线程未被污染
    }

    /// <summary>
    /// T-c：视图集成契约——注入伪造 ConfigData（FontName/FrontSize 改写）后，FontFactory.GetFont("")
    /// 回读 Config.FontName / Config.FontSize，无需真实字体文件（EmuFont 为 GDI-free 值类型）。
    /// </summary>
    [Fact]
    public void FontFactory_reads_injected_Config_FontName_and_Size()
    {
        FontFactory.ClearFont();
        try
        {
            var cd = new ConfigData();
            cd.GetConfigItem(ConfigCode.FontName).SetValue("FakeFont");
            cd.GetConfigItem(ConfigCode.FontSize).SetValue(24);

            using (var scope = GlobalStatic.OpenScope(cd))
            {
                // 请求空字体名 → 回落到 Config.FontName。
                EmuFont font = FontFactory.GetFont("", EmuFontStyle.Regular);
                Assert.Equal("FakeFont", font.Name);
                Assert.Equal(24f, font.Size);
                Assert.Equal(EmuFontStyle.Regular, font.Style);
            }
        }
        finally
        {
            FontFactory.ClearFont();
        }
    }

    /// <summary>
    /// T-d：派生/计算属性契约——DrawableWidth = WindowX - DrawingParam_ShapePositionShift，
    /// SavDir 随 UseSaveFolder 变化，均现算现用（无静态副本）。
    /// </summary>
    [Fact]
    public void Derived_properties_computed_from_injected_ConfigData()
    {
        // SavDir 读取会话根（issue-009），夹具自带临时根。
        var root = Path.Combine(Path.GetTempPath(), "emuera-cfgscope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var rootScope = new RootScope(root, new FileSystemGameDirAccessor());
        var cd = new ConfigData(rootScope.Paths);
        cd.GetConfigItem(ConfigCode.WindowX).SetValue(760);
        cd.GetConfigItem(ConfigCode.FontSize).SetValue(18);
        cd.GetConfigItem(ConfigCode.UseSaveFolder).SetValue(false);

        using (var scope = GlobalStatic.OpenScope(cd))
        {
            // TEXTRENDERER 模式下 DrawingParam_ShapePositionShift = Max(2, FontSize/6) = 3
            Assert.Equal(3, Config.DrawingParam_ShapePositionShift);
            Assert.Equal(760 - 3, Config.DrawableWidth);
            // UseSaveFolder=false → SavDir == ExeDir（不含 sav 后缀）
            Assert.DoesNotContain("sav" + System.IO.Path.DirectorySeparatorChar, Config.SavDir);
        }
    }

    /// <summary>
    /// T-e（PRD/ADR-0009 回退回归）：DrawLineString 经薄视图转发时，若 _Replace.csv 将其置空，
    /// 须回落到 "-"（原 SetReplace 的 `if (string.IsNullOrEmpty) DrawLineString = "-"` 行为，
    /// 坍缩为计算属性后不能丢失）。
    /// </summary>
    [Fact]
    public void DrawLineString_falls_back_to_dash_when_empty()
    {
        var cd = new ConfigData();
        // 模拟用户在 _Replace.csv 中将 DRAWLINE 文字显式置空。
        cd.GetConfigItem(ConfigCode.DrawLineString).SetValue("");

        using (var scope = GlobalStatic.OpenScope(cd))
        {
            Assert.Equal("-", Config.DrawLineString);
        }
    }
}
