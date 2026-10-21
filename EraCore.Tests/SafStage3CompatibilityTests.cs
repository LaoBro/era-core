using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using Xunit;

namespace MinorShift.Emuera.Tests;

[Collection("LoaderTests")]
public sealed class SafStage3CompatibilityTests
{
	private const string Root =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FStage3";

	// 原 `Saf_update_key_is_stable_and_tracks_logical_file_set` / `Saf_update_key_failure_does_not_block_startup`
	// 随 ConfigData.getSafUpdateKey 一起移除：ONCE 的变更判据已寄生到 IR 指纹（ErbIrCache.ComputeContentFingerprint
	// + ErbLoader 的 pending 清单），SAF 侧的覆盖见 ErbIrCacheTests（fromTree vs fromCache 指纹一致）
	// 与 FileSystemGameDirAccessorPatternTests。原 `Saf_once_update_sets_the_runtime_reduction_flag`
	// 改写到 ConfigWritePolicyTests（新口径：NR 由 IR 命中结果决定，且任何加载路径都不得写盘）。

	[Fact]
	public void Saf_language_generation_writes_through_the_game_accessor()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);

		Lang.GenerateDefaultLangFile();

		var langDir = SafCompat.ResolveSubPath(Root, "lang");
		var generated = SafCompat.CombinePath(langDir, "emuera-default-lang.xml");
		Assert.True(accessor.FileExists(generated));
		Assert.Contains("<name>日本語</name>",
			System.Text.Encoding.UTF8.GetString(accessor.ReadAllBytes(generated)!));
	}

	[Fact]
	public async Task Saf_preload_caches_erd_and_als_files()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);
		var erbDir = SafCompat.ResolveSubPath(Root, "erb");
		SafCompat.CreateDirectory(erbDir);
		var erd = SafCompat.CombinePath(erbDir, "DVAR.erd");
		var als = SafCompat.CombinePath(erbDir, "DVAR.als");
		accessor.Seed(erd, Encoding.UTF8.GetBytes("DVAR\n"));
		accessor.Seed(als, Encoding.UTF8.GetBytes("1,alias\n"));

		Preload.Clear();
		try
		{
			await Preload.Load(erbDir, accessor);
			Assert.Contains(erd, Preload.GetAllCachedKeys());
			Assert.Contains(als, Preload.GetAllCachedKeys());
		}
		finally
		{
			Preload.Clear();
		}
	}

	[Fact]
	public void Saf_plugins_are_rejected_with_an_explicit_message()
	{
		var accessor = new InMemoryContentDirAccessor(Root);
		using var scope = new GamePathsScope(Root, accessor);
		var pluginDir = SafCompat.ResolveSubPath(Root, "Plugins");
		SafCompat.CreateDirectory(pluginDir);
		accessor.Seed(SafCompat.CombinePath(pluginDir, "sample.dll"), [0x4D, 0x5A]);

		var error = Assert.Throws<ExeEE>(() => PluginManager.GetInstance().LoadPlugins());
		Assert.Contains("does not support loading game DLL plugins", error.Message, StringComparison.Ordinal);
	}

	private sealed class GamePathsScope : IDisposable
	{
		private readonly GamePaths previous = GamePaths.Current;

		public GamePathsScope(string root, IGameDirAccessor accessor)
			=> _ = GamePaths.Resolve(root, accessor);

		public void Dispose() => GamePaths.SetCurrent(previous);
	}
}
