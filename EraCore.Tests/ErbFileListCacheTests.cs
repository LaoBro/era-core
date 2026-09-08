using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issues/13 M5：清单 sidecar 的命中轮集成钉子。AppDataIsolated 串行集合——IR/sidecar 写入
/// 经 AppDataPaths.Configure 重定向到临时 appdata，不污染真实数据（content-URI 游戏的
/// GetIrCachePath 落点）。覆盖：建库轮写 sidecar → 命中轮 IsIrHitRound 读 sidecar 回放清单 +
/// 播种树枚举 memo → IndexTree 的 pathIndex 免枚举可用（含 .ERH——ErhLoader 枚举同走 memo 的
/// 下游实证，命中轮免 15.6s 冷 TreeEnum 的机制闭环）。
/// </summary>
[Collection("AppDataIsolated")]
public class ErbFileListCacheTests
{
	[Fact]
	public void IsIrHitRound_seeds_tree_memo_from_sidecar_on_hit()
	{
		var tempAppData = Path.Combine(Path.GetTempPath(), "emuera-flappdata-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempAppData);
		var previousAppData = AppDataPaths.Directory;
		AppDataPaths.Configure(tempAppData);
		try
		{
			const string Root = "content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FFileListMemo";
			var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
			var previousPaths = GamePaths.Current;
			GamePaths.Resolve(Root, accessor);
			var config = new ConfigData();
			try
			{
				// IsIrHitRound 的参与门槛（IrFirstDirList / IrFileListCache 走 ConfigData 默认 true）
				config.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(true);
				config.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
				using var _scope = GlobalStatic.OpenScope(config);

				var erbDir = SafCompat.ResolveSubPath(Root, "erb");
				SafCompat.CreateDirectory(erbDir); // stub 走显式注册集合（M4 同款）
				// stub 的 GetFiles 只返回直接子项（M4 同款限制）——平铺种子，嵌套递归由本地 FS 等价测试覆盖
				accessor.Seed(SafCompat.CombinePath(erbDir, "TOP.ERB"), Encoding.UTF8.GetBytes("@T\nRETURN\n"));
				accessor.Seed(SafCompat.CombinePath(erbDir, "A.ERB"), Encoding.UTF8.GetBytes("@A\nRETURN\n"));
				accessor.Seed(SafCompat.CombinePath(erbDir, "SUB.ERH"), Encoding.UTF8.GetBytes("#DIM X\n"));

				var env = new LoaderEnv(erbDir, erbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false)
				{ DirAccessor = accessor };

				// 建库轮：扫描 → fp → IR + sidecar（镜像 LoadErbDir Complete 的写侧逻辑）
				var pending = ErbLoader.BuildPendingList(env, null, out var firstDirs, out _);
				Assert.Equal(2, pending.Count);
				long fp = ErbIrCache.ComputeContentFingerprint(pending.Select(p => (p.Filename, p.File)).ToList(), null, firstDirs);
				string irPath = ErbIrCache.GetIrCachePath(Root);
				Directory.CreateDirectory(Path.GetDirectoryName(irPath)!);
				using (var writer = new ErbIrCache.IrCacheWriter(irPath))
					Assert.True(writer.Complete(fp, firstDirs));
				Assert.True(Preload.TryGetTreeFiles(erbDir, out var treeFiles));
				var pendingRels = pending.Select(p => (p.Filename, SafPath.GetRelativePathFromRoot(erbDir, p.File))).ToList();
				var treeRels = treeFiles.Select(f => SafPath.GetRelativePathFromRoot(erbDir, f)).ToList();
				Assert.Contains("SUB.ERH", treeRels); // .erh 进全树清单——memo 播种喂 ErhLoader 的载体
				Assert.True(ErbIrCache.TryWriteFileList(irPath, pendingRels, treeRels));

				// 命中轮：memo 清空后 IsIrHitRound 回放 sidecar 清单 + fp 一致 + 播种 memo
				Preload.Clear();
				Assert.True(ErbLoader.IsIrHitRound(env), "sidecar 回放 → fp 一致 → 命中轮");
				Assert.True(Preload.TryGetTreeFiles(erbDir, out var seeded), "命中轮播种树枚举 memo");
				Assert.Equal(treeRels.Count, seeded.Count);

				// IndexTree 走播种 memo（免枚举）→ pathIndex 立即可用（含 .ERH 条目）
				Preload.IndexTree(erbDir, accessor);
				Assert.True(Preload.TryGetFullPath("TOP.ERB", out var topPath));
				Assert.Equal(accessor.CombinePath(erbDir, "TOP.ERB"), topPath);
				Assert.True(Preload.TryGetFullPath("SUB.ERH", out _));
			}
			finally
			{
				Preload.Clear();
				GamePaths.SetCurrent(previousPaths);
			}
		}
		finally
		{
			AppDataPaths.Configure(previousAppData);
			try { Directory.Delete(tempAppData, recursive: true); } catch { /* 临时目录清理失败不掩盖断言 */ }
		}
	}
}
