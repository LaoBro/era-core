using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0012 / Issue 02：ErhLoader 隔离单测。
/// 直接以显式依赖（console / idDic / vEvaluator / LoaderEnv）构造，不持 Process 反向引用（F2）。
/// 不开 GlobalStatic.Process scope（ErhLoader 的 load 路径不读它；EE_ERD 由 UseERD=false 跳过）。
/// 只验证可观察行为：宏注册、返回值、错误路径。
/// </summary>
[Collection("LoaderTests")]
public class ErhLoaderTests
{
	[Fact]
	public async Task LoadHeaderFiles_registers_DEFINE_macro_and_returns_true()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErh("MACROS.ERH", "#DEFINE FOO (1+2)\n");
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
		Assert.NotNull(h.IdDic.GetMacro("FOO"));
	}

	[Fact]
	public async Task LoadHeaderFiles_empty_dir_returns_true_with_no_macros()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
	}

	[Fact]
	public async Task LoadHeaderFiles_malformed_line_without_sharp_returns_false()
	{
		// ERH 每个有效行必须以 '#' 开头；裸行触发 CodeEE → 警告 → 返回 false。
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErh("BAD.ERH", "NOT_A_SHARP_LINE\n");
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.False(ok);
	}

	[Fact]
	public async Task LoadHeaderFiles_multiple_DEFINE_all_registered()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		h.WriteErh("MULTI.ERH", "#DEFINE ALPHA (1)\n#DEFINE BETA (2)\n#DEFINE GAMMA (3)\n");
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
		Assert.NotNull(h.IdDic.GetMacro("ALPHA"));
		Assert.NotNull(h.IdDic.GetMacro("BETA"));
		Assert.NotNull(h.IdDic.GetMacro("GAMMA"));
	}

	// ── erb-loader-parallel Issue 02：UseERD 开/关两态验收 ──────────────
	// ERD 机制：ERH 内 #DIM DVAR, 2 + ErbDir 下 DVAR.erd（行格式 "index,名前"）→
	// ConstantData 建立 DVAR 名→索引字典（isUserDefined 可查）。三条测试分别锁
	// PrepareERDFileNames 的缓存命中跳过兜底（02 新行为）、缓存未命中走兜底、UseERD 关。
	// 注意：Config scope 是 AsyncLocal——harness 必须在测试方法自身的 async 流内 new
	// （不能收进共享 async 工厂，否则 scope 不上浮，Config.Current 为 null）。

	[Fact]
	public async Task LoadHeaderFiles_UseERD_on_cached_erd_still_builds_dictionary()
	{
		// UseERD 开 + .erd 已在 Preload 缓存：02 改动跳过全树兜底枚举，
		// erdFileNames 仍须收齐（否则 ERD 名字典缺失，isUserDefined 抛 NotDefinedErdKey）
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.UseERD).SetValue(true);
		h.WriteErh("DVARDEF.ERH", "#DIM DVAR, 2\n");
		h.WriteErd("DVAR.erd", "0,ALPHA\n1,BETA\n");
		await h.PreloadAsync(); // DVAR.erd 进缓存 → PrepareERDFileNames 命中缓存路径
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
		GlobalStatic.ConstantData.isUserDefined("DVAR", "ALPHA", 1);
		GlobalStatic.ConstantData.isUserDefined("DVAR", "BETA", 1);
	}

	[Fact]
	public async Task LoadHeaderFiles_UseERD_on_cache_miss_fallback_still_builds_dictionary()
	{
		// UseERD 开 + 缓存未含 .erd（只 Preload 了 CsvDir）：兜底全树枚举必须保留，
		// DVAR.erd 仍被发现（ERH/ERD 读取走 OpenOnCache 的直读回退）
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.UseERD).SetValue(true);
		h.WriteErh("DVARDEF.ERH", "#DIM DVAR, 2\n");
		h.WriteErd("DVAR.erd", "0,ALPHA\n1,BETA\n");
		await Preload.Load(h.CsvDir, new FileSystemGameDirAccessor()); // 缓存无 .erd → 走兜底枚举
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
		GlobalStatic.ConstantData.isUserDefined("DVAR", "ALPHA", 1);
	}

	[Fact]
	public async Task LoadHeaderFiles_UseERD_off_skips_erd_loads_normally()
	{
		// UseERD 关：ERD 链路整体跳过，ERH 照常装载；ERD 名字典不建立
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.UseERD).SetValue(false);
		h.WriteErh("DVARDEF.ERH", "#DIM DVAR, 2\n");
		h.WriteErd("DVAR.erd", "0,ALPHA\n1,BETA\n");
		await h.PreloadAsync();
		var loader = new ErhLoader(h.Console, h.IdDic, h.VEvaluator, h.Env);

		var ok = loader.LoadHeaderFiles(h.ErbDir, displayReport: false);

		Assert.True(ok);
		Assert.Throws<CodeEE>(() => GlobalStatic.ConstantData.isUserDefined("DVAR", "ALPHA", 1));
	}
}
