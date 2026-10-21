using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// ADR-0033（第四刀前置子集）：IR 磁盘缓存的**装载期会话状态**。
/// <para>
/// 非空 = IR 参与本会话（开 IrDiskCacheLoad 且 lazy 且非 AnalysisMode）。
/// <see cref="Available"/> = 启动校验命中（第二次启动走 IR 后背物化）；否则首轮走 B1 源并在此轮生成 IR 落盘。
/// </para>
/// <para>
/// <b>所有权</b>：实例由 <see cref="ErbIrLoadPipeline"/> 在装载期<b>产出</b>，交回
/// <see cref="ErbLoader"/> 持有并跨装载存活——运行期物化（<c>MaterializeOnRuntime</c> →
/// <c>MaterializeLabelFromIr</c>）在<b>加载完成后</b>仍读取本对象，故它不能随流水线实例一同消亡。
/// </para>
/// </summary>
internal sealed class IrState
{
	/// <summary>本进程加载轮是否命中 IR（true = 第二次启动，索引已驻留）。</summary>
	public bool Available;
	/// <summary>IR 文件路径（缓存目录）。</summary>
	public string Path = null!;
	/// <summary>
	/// 常驻读句柄（issues/05 句柄常驻）：hit 会话 blob 读取的 open/close 折叠为 1 次，
	/// 全部经 seek+局部读（content-URI 镜像在 app 私有存储，真实文件系统）。miss 会话为 null
	/// （IR 增量写器写侧与读句柄天然互斥）。生命周期 = IrState 生命周期：reload / 加载失败 /
	/// 部分重载（<c>IrState</c> 的置空点统一走 ResetIrState）显式关闭——置空后没有第二次机会。
	/// Windows 桌面跨会话时序（issues/05「留意」）：旧 loader 的读句柄在游戏退出后靠 GC 终结器
	/// 释放，若新会话在终结前 miss 重建，delete-dest 可能被旧句柄锁住 → IR 写被静默跳过、下次再建，
	/// 自愈且非致命；同会话内 reload 的锁冲突已由「先关后建」消除。
	/// </summary>
	public Stream? ReadStream;
	/// <summary>（逻辑文件名, 标签名, 行号）→ 索引条目。行号消歧同文件同名多定义。驻留（几 MB 量级），等价 labelDic。</summary>
	public Dictionary<(string File, string Label, int LineNo), IrIndexEntry> Index = null!;
	/// <summary>逻辑文件名 → 该文件内按 @ 出现序的索引条目（TryGenerateFromIr 用，免 O(n·files) 逐文件扫全表）。</summary>
	public Dictionary<string, List<IrIndexEntry>> ByFile = new(StringComparer.OrdinalIgnoreCase);
	/// <summary>
	/// issues/17 A′：命中轮的待载文件全集（逻辑名，与 <see cref="ByFile"/> 同比较器）。
	/// <para>
	/// 用途：补出"该文件确实没有函数"这一信息。IR 索引只记录<b>有函数</b>的文件
	///（<see cref="ErbIrCache.IrCacheWriter.AppendFile"/> 对 <c>blobs.Count == 0</c> 直接 return），
	/// 故 0 函数/空文件/纯注释文件在命中轮查不到条目，旧判据下只能回落读源——SAF 下每文件约 25ms，
	/// 真机 25 个文件累计 ~620ms，且因主循环按序 <c>await</c> 而串行阻塞整条流水线。
	/// </para>
	/// <para>
	/// 正确性：指纹已把文件集合纳入（<see cref="IrLoadContext.ComputeContentFingerprint"/>），
	/// 文件增删必改 fp 必 miss 重建——故"命中轮 pending 全集"与"建库轮实际处理过的文件集合"必然一致。
	/// 仅命中（<see cref="Available"/>）时非 null。
	/// </para>
	/// <para>
	/// ADR-0027 第四刀（issue 05）：原为 <c>ErbLoader._irAllFiles</c> 独立宿主字段——它只对应当前
	/// <see cref="IrState"/>，作为独立字段存活需两道人工同步（赋值 + <c>ResetIrState</c> 置空），
	/// 属"清理滞后"异味；收进本对象后随 <c>_ir</c> 一起生灭，赋值点与失效点各只剩一处。
	/// </para>
	/// </summary>
	public HashSet<string>? AllFiles;

	/// <summary>读单个函数 blob：优先常驻句柄 seek+局部读；句柄缺失（异常路径）临时开流读一次。</summary>
	public IrBlob ReadBlob(in IrIndexEntry entry)
	{
		var stream = ReadStream;
		if (stream != null)
			return ErbIrCache.ReadBlob(stream, entry);
		using var fresh = SafCompat.OpenRead(Path) ?? throw new IOException("IR file missing");
		return ErbIrCache.ReadBlob(fresh, entry);
	}

	/// <summary>显式关闭常驻读句柄（幂等；reload/失败/部分重载的置空点调用）。</summary>
	public void CloseHandle()
	{
		var s = ReadStream;
		ReadStream = null;
		try { s?.Dispose(); } catch { /* 关闭失败不掩盖主流程 */ }
	}
}

/// <summary>
/// ADR-0033：IR 磁盘缓存的**装载期流水线**深模块（原 <see cref="ErbLoader"/> 内联编排的收口点）。
/// <para>
/// 职责边界（决策一）：IR 参与判定与缓存路径、待载清单构建（IR 清单回放路径）、命中预判定、
/// 装载期编排（读头 / sidecar / 命中或 miss 建库）、写盘收尾（<c>Complete</c> + sidecar 写 + legacy
/// 自愈）、blob 分账。
/// </para>
/// <para>
/// <b>不在本模块内</b>（决策一）：运行期物化（<c>_ir</c> 读点与 <c>MaterializeLabelFromIr</c>）、
/// 装载调度器、单文件生成、跨文件校验、<c>_materializeDiag</c> 与 scan-line 共享通道——
/// 后者归 ADR-0027 的第二刀（ParseContext）。
/// </para>
/// <para>
/// <b>依赖方向</b>：依赖 <see cref="ErbIrCache"/> 静态成员、<see cref="Preload"/> 语义枚举、
/// <see cref="SafPath"/> 与平台访问器；<b>不引用 <see cref="ErbLoader"/> 的任何成员</b>
///（仅 XML cref 提及），故与宿主之间无双向依赖，也不持有 <see cref="IrState"/> 实例（决策二）。
/// </para>
/// </summary>
internal sealed class ErbIrLoadPipeline
{
	/// <summary>
	/// b2-irdiskcache 参与判定唯一决策点（真值表穷举于 ErbIrCacheTests）。
	/// 2026-09-04 方案 B（issues/05）后不再含 content-URI 维度：content-URI（SAF）游戏经
	/// <see cref="ErbIrCache.GetIrCachePath"/> 把缓存镜像到 app 私有存储（真实文件系统，可 seek +
	/// 原子替换），读放大止血解除——判定只看 开关 × lazy × 非 AnalysisMode。
	/// </summary>
	internal static bool ShouldUseIrCache(bool irDiskCacheConfigured, bool lazyFunctionLoad, bool analysisMode)
		=> irDiskCacheConfigured && lazyFunctionLoad && !analysisMode;

	// IR 缓存文件路径取得点已随第四刀（ADR-0027 决策二.4）迁至 IrLoadContext.IrCachePath()——
	// 原先本处硬编码 Program.ExeDir，是 IR 路径里唯一无法注入的 ambient；现在装载入口一次快照携带。

	/// <summary>
	/// issue 09：ERB 待载清单构建（唯一路径——<see cref="ErbLoader.LoadErbDir"/> 与启动 IR 命中预判定共用同一语义）。
	/// firstDir（*#* 目录）序在前、主枚举去重序在后（erb-loader-parallel 03 合并序）。
	/// <paramref name="firstDirOverride"/> 非 null（issues/12 M4 命中轮）= 清单来自 IR 头存区
	///（相对名，经 <see cref="ResolveFirstDirPath"/> 重建目录路径），跳过 SAF
	/// <c>GetDirectories("*#*")</c> 冷树通配扫描（真机 ≈15.9s/进程）；清单陈旧由指纹兜底
	///（firstDirs 进 <see cref="ErbIrCache.ComputeContentFingerprint"/>，miss → 调用方回退全扫）。
	/// 缓存空（命中轮/预判定）时 <see cref="Preload.GetFilesFromCache"/> 返回 null →
	/// 走 <see cref="Preload.EnumerateFilesFromTree"/>（与缓存键同源的 Preload 语义全树枚举——
	/// launch7 真机实测 Config.GetFiles 的 SAF 通配/displayName 差异会丢文件/路径变形，弃用）。
	/// <para>
	/// ADR-0027 第四刀：扫描深度（<c>SearchSubdirectory</c>）经 <paramref name="ctx"/> 显式传入，
	/// 本方法内不再直读 <c>Config.Config.*</c>。
	/// </para>
	/// </summary>
	internal static List<(string Filename, string File)> BuildPendingList(LoaderEnv env, IrLoadContext ctx, List<string>? firstDirOverride, out List<string> firstDirs, out int erbFileCount,
		IReadOnlyList<(string Logical, string RelToRoot)>? fileListOverride = null)
	{
		// S0-M3（2026-09-05）：命中轮清单枚举计时——预判定 / LoadErbDir 各打一条，配合 Preload 的
		// TreeEnum memo=miss/hit 行判读「2388 文件重复枚举」是否构成间隙②（issues/11 M3）。
		var sw = Stopwatch.StartNew();
		// issues/13 M5：清单 sidecar 回放（命中轮）——pending 按建库轮存储序直接重建（零枚举 IPC，
		// 真机砍掉 15.6s 冷 TreeEnum）；存储序即权威合并序（建库轮写的即最终合并序）；物理路径经
		// CombinePath(root, 相对根名) 重建（URI 基座跨重授稳定）。陈旧清单由 fp 兜底（调用方 miss →
		// 丢弃重扫）。新鲜度 = 路线 2 显式失效（用户重扫，issues/13 裁定）。
		// issues/12 M4 取证①：GetDirectories 单独计时（确认真机 16.4s 差值 ≈15.9s 的归属；override 轮 -1）
		long firstDirScanMs;
		string[] firstDirFull;
		if (fileListOverride != null)
		{
			firstDirScanMs = -1;
			firstDirs = firstDirOverride ?? [];
			erbFileCount = fileListOverride.Count;
			// S0-M6（空档取证，2026-09-09）：回放 = 2388 次 DirAccessor.CombinePath（SAF URI 重建，
			// 真机 ~0.3ms/次 ≈ 750ms/轮 × 命中轮两次 prehit+LoadErbDir）——单独计时验证是否值得做
			// tree URI 前缀缓存（同一父目录只 BuildDocumentUriUsingTree 一次，尾段 documentId 拼接）。
			// issues/14 A1：取证确认（两轮 combine=1135/364ms 全归属）→ 换 CombinePaths 批量 +
			// SAF 基座记忆化，探针保留作前后对照。
			var combineSw = Stopwatch.StartNew();
			var rels = new string[fileListOverride.Count];
			for (int i = 0; i < fileListOverride.Count; i++)
				// issues/15 §0.3：一律归一到本平台分隔符。原写法只在 content URI 分支归一、
				// 本地分支原样使用；而 sidecar 的存储契约是 `\` 分隔，Android legacy 下 `\`
				// 不是分隔符 → 拼出 `erb/ABL\ABL.ERB` 死路径，且物理路径进指纹会让 fp 分叉永 miss。
				rels[i] = SafPath.RelToPhysical(fileListOverride[i].RelToRoot);
			var physical = env.DirAccessor.CombinePaths(env.ErbDir, rels);
			var replayed = new List<(string Filename, string File)>(fileListOverride.Count);
			for (int i = 0; i < fileListOverride.Count; i++)
				replayed.Add((fileListOverride[i].Logical, physical[i]));
			combineSw.Stop();
			sw.Stop();
			EmueraLog.Info("ErbIrLoadPipeline",
				$"BuildPendingList ms={sw.ElapsedMilliseconds} files={replayed.Count} firstDirs={firstDirs.Count} " +
				$"firstDirScanMs=-1 src=ir fileListSrc=list({fileListOverride.Count}) " +
				$"combine={combineSw.ElapsedMilliseconds}ms(n={fileListOverride.Count})");
			return replayed;
		}
		if (firstDirOverride != null)
		{
			firstDirScanMs = -1;
			firstDirs = firstDirOverride;
			firstDirFull = new string[firstDirOverride.Count];
			for (int i = 0; i < firstDirOverride.Count; i++)
				firstDirFull[i] = ResolveFirstDirPath(env, firstDirOverride[i]);
		}
		else
		{
			var scanSw = Stopwatch.StartNew();
			firstDirFull = env.DirAccessor.GetDirectories(env.ErbDir, "*#*",
				ctx.Fingerprint.SearchSubdirectory ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly) ?? [];
			scanSw.Stop();
			firstDirScanMs = scanSw.ElapsedMilliseconds;
			// 存 IR 的清单用相对名（GetRelativePathFromRoot 形态，'\' 分隔）——跨授权重授稳定；
			// 加载序 = 扫描返回序（权威序，建库轮原样写 IR）
			firstDirs = new List<string>(firstDirFull.Length);
			foreach (var dir in firstDirFull)
				firstDirs.Add(SafPath.GetRelativePathFromRoot(env.ErbDir, dir));
		}
		// erb-loader-parallel 02：firstDir 已载文件查重 O(n²)（List 线性扫）→ 集合查找；
		// 文件系统路径大小写不敏感，与既有 SCIgnore 语义一致
		HashSet<string> loadedFiles = new(StringComparer.OrdinalIgnoreCase);
		var erbFiles = Preload.GetFilesFromCache(env.ErbDir, ".ERB", "ErbIrLoadPipeline")
			?? Preload.EnumerateFilesFromTree(env.ErbDir, env.DirAccessor, ".ERB");
		erbFileCount = erbFiles.Count;
		// erb-loader-parallel 03：统一有序待载清单（firstDir 目录序在前、主枚举去重序在后），
		// 串行 / 并行两条调度共用同一清单与合并序
		List<(string Filename, string File)> pending = [];
		foreach (var dir in firstDirFull)
		{
			var firstErbFiles = Preload.GetFilesFromCache(dir, ".ERB", "ErbIrLoadPipeline")
				?? Preload.EnumerateFilesFromTree(dir, env.DirAccessor, ".ERB");
			foreach (var erb in firstErbFiles ?? [])
			{
				loadedFiles.Add(erb.Value);
				pending.Add((erb.Key, erb.Value));
			}
		}
		foreach (var erb in erbFiles)
		{
			if (loadedFiles.Contains(erb.Value))
				continue;
			pending.Add((erb.Key, erb.Value));
		}
		sw.Stop();
		EmueraLog.Info("ErbIrLoadPipeline",
			$"BuildPendingList ms={sw.ElapsedMilliseconds} files={erbFileCount} firstDirs={firstDirs.Count} " +
			$"firstDirScanMs={firstDirScanMs} src={(firstDirOverride != null ? "ir" : "scan")} fileListSrc=enum");
		return pending;
	}

	/// <summary>
	/// issues/12 M4：firstDir 相对名（<see cref="SafPath.GetRelativePathFromRoot"/> 形态，'\' 分隔）
	/// → 目录全路径。content URI 必须经 <see cref="IGameDirAccessor.CombinePath"/>（SAF 实现走
	/// DocumentsContract.BuildDocumentUriUsingTree 正确编码 documentId——裸拼接 URI 会被 Android
	/// 解析成目录本身，AssetChannel 同款坑）；本地路径走 Path.Combine。
	/// 分隔符一律先经 <see cref="SafPath.RelToPhysical"/> 归一到本平台——sidecar/IR 存储契约是 '\'，
	/// Android 真实路径下 '\' 不是分隔符，原样拼接会得到 `erb/1#EV\2#NEST` 死路径（issues/15 §0.3
	/// 已修另两处重建点，此处同源补齐）。
	/// </summary>
	private static string ResolveFirstDirPath(LoaderEnv env, string relativeName)
		=> env.DirAccessor.CombinePath(env.ErbDir, SafPath.RelToPhysical(relativeName));

	/// <summary>
	/// issue 09：IR 命中预判定（启动内容 Preload 门控）。不读源内容——文件清单走
	/// Preload 语义枚举（缓存空即命中轮语义），指纹计算只读文件
	/// stat（长度/mtime，content-URI 降级 0，见 <see cref="ErbIrCache.ComputeContentFingerprint"/>），
	/// firstDir 清单优先读 IR 头存区（issues/12 M4，命中轮零 SAF 通配扫描），
	/// <see cref="ErbIrCache.TryLoad"/> 只读 IR 头 + 索引区。
	/// 命中 → 调用方可跳过 <see cref="Preload.LoadTreesAsync"/> 的内容读取（物化走 blob、源回退走懒读）；
	/// 未命中 / IR 关闭（<see cref="ShouldUseIrCache"/>）/ 任何异常 → false，调用方走完整 Preload。
	/// 与 LoadErbDir 内判定共享 BuildPendingList + 指纹逻辑（顺序无关），LoadErbDir 会再次核对。
	/// <para>
	/// ADR-0027 第四刀：本方法是**独立的装载入口**（启动内容 Preload 门控，早于 <c>LoadErbDir</c>），
	/// 故它自行在入口快照一次 <see cref="IrLoadContext"/>——与第一刀「两个装载入口各构造一次策略」同款；
	/// 方法体内不再直读 <c>Config.Config.*</c>。
	/// </para>
	/// </summary>
	internal static bool IsIrHitRound(LoaderEnv env)
	{
		try
		{
			var ctx = IrLoadContext.FromCurrent(env);
			if (!ctx.ShouldUseIrCache)
			{
				// issue 09 真机排障（2026-09-05 launch5 分析）：静默 false 曾使「未命中」无法与旧构建区分——
				// 此处显式记录，任何 false 分支都有日志可查（hit / 关闭 / 异常三态齐全）。
				EmueraLog.Info("ErbIrCache",
					$"pre-hit check skipped: ir-disabled (irDisk={ctx.IrDiskCacheConfigured} lazy={ctx.LazyFunctionLoad} analysis={ctx.AnalysisMode})");
				return false;
			}
			var irPath = ctx.IrCachePath();
			// issues/12 M4：优先读 IR 头存 firstDir 清单替代 SAF 通配扫描（读侧受回退旋钮门控，
			// FALSE = 恒扫描）；无 v2 IR / 损坏 → null → 照旧扫描。清单正确性由 fp（含清单）判定。
			List<string>? irFirstDirs =
				ctx.FirstDirListEnabled && ErbIrCache.TryReadFirstDirs(irPath, out var irFd) ? irFd : null;
			// issues/13 M5：清单 sidecar（有序 pending 对 + 全树清单）。读侧受旋钮 + 形态门双门控。
			// issues/15 §0.3：形态门由 ErbIrCache.UseFileListSidecar 统一给出——Android legacy
			// （真实路径）也开，因为其"本地路径"是 FUSE 挂载，枚举与 SAF 同量级（真机 569ms）；
			// 桌面仍关（2026-09-09 裁定「Windows/CLI 保留自动失效」）。
			List<(string Logical, string RelToRoot)>? irFileList = null;
			List<string>? irTreeRels = null;
			List<string>? irCsvTreeRels = null;
			if (ctx.FileListCacheEnabled && ErbIrCache.UseFileListSidecar(env.ErbDir)
				&& ErbIrCache.TryReadFileListFull(irPath, out var flPending, out var flTree, out var flCsv, out _))
			{
				irFileList = flPending;
				irTreeRels = flTree;
				irCsvTreeRels = flCsv;
			}
		var pending = BuildPendingList(env, ctx, irFirstDirs, out var firstDirsUsed, out _, irFileList);
		// S0-M6（空档取证，2026-09-09）：BPL#1 done → pre-hit 行之间真机实测 ~1.4s 无日志盲区——
		// 三步分账：fp（content URI 走 stat 降级，预期纯内存毫秒级）/ TryLoad（IR 索引区 ~14647 条
		// entry 读取）/ SeedTreeMemo（逐条 CombinePath 播种，与 BPL replay 同款开销模式）。
		var fpSw = System.Diagnostics.Stopwatch.StartNew();
		long fp = ctx.ComputeContentFingerprint(pending, firstDirsUsed);
		fpSw.Stop();
		var tryLoadSw = System.Diagnostics.Stopwatch.StartNew();
		bool hit = ErbIrCache.TryLoad(irPath, fp, out _);
		tryLoadSw.Stop();
		long seedMs = -1;
		int seedCount = 0;
		if (hit && irTreeRels != null)
		{
			// issues/13 M5：命中轮播种树枚举 memo——IndexTree / ErhLoader（.erh 同走 memo）免冷枚举。
			// 唯一播种点（miss 轮不播种，防陈旧清单污染后续真实枚举；Preload.Clear 已在本判定前跑过）。
			// issues/14 B：双根播种——ERB 根 + CSV 根（兄弟目录，sidecar CSV 尾段非 null 时；
			// legacy sidecar 无尾段 → CSV 维持冷枚举，命中轮收尾自愈重写）。SeedTreeMemo 幂等（已有 memo 跳过）。
			var seedSw = System.Diagnostics.Stopwatch.StartNew();
			Preload.SeedTreeMemo(env.ErbDir, irTreeRels, env.DirAccessor);
			if (irCsvTreeRels != null)
				Preload.SeedTreeMemo(env.CsvDir, irCsvTreeRels, env.DirAccessor);
			seedSw.Stop();
			seedMs = seedSw.ElapsedMilliseconds;
			seedCount = irTreeRels.Count + (irCsvTreeRels?.Count ?? 0);
		}
		EmueraLog.Info("ErbIrCache",
			$"prehit breakdown: fp={fpSw.ElapsedMilliseconds}ms tryload={tryLoadSw.ElapsedMilliseconds}ms " +
			$"seed={seedMs}ms(n={seedCount})");
			// issue 09 真机排障（launch6 fp 分歧）：指纹 = 文件集合 + firstDir 清单 + 解析配置集 + Rename 表——
			// 打印输入快照便于与 LoadErbDir 的 ir hit 行逐项对比定位漂移来源。
			EmueraLog.Info("ErbIrCache",
				$"pre-hit check hit={hit} files={pending.Count} fp={fp:x16} " +
				$"firstDirSrc={(irFirstDirs != null ? $"ir({irFirstDirs.Count})" : "scan")} " +
				$"fileListSrc={(irFileList != null ? $"ir({irFileList.Count})" : "enum")} " +
				$"ic={ctx.Fingerprint.IgnoreCase} rf={ctx.Fingerprint.UseRenameFile} rp={ctx.Fingerprint.UseReplaceFile} " +
				$"ra={(int)ctx.Fingerprint.ReduceArgumentOnLoad} " +
				$"t3={ctx.Fingerprint.SystemIgnoreTripleSymbol} rename={ctx.RenameDic?.Count ?? 0}");
			return hit;
		}
		catch (Exception ex)
		{
			// 枚举/stat 等任何异常 → 保守走完整 Preload，不阻断启动。
			EmueraLog.Warn("ErbIrCache", $"pre-hit check skipped: {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	// ===== 装载期流水线（决策一/二：编排内化，状态产出交回宿主）=====

	/// <summary>
	/// 装载期判定与建库的结果（ADR-0033 决策一「返回结果对象」，由 <see cref="ErbLoader"/> 负责并入自身状态）。
	/// <para>
	/// <see cref="Pending"/>/<see cref="FirstDirs"/>/<see cref="ErbFileCount"/> 是**最终值**：miss 且 IR 清单被
	/// 内容指纹判为陈旧时会触发丢弃重扫（issues/12 M4 + issues/13 M5 自愈兜底），此后宿主必须以此为准
	///（如 <c>_filePathByLogical</c> 的建立）。
	/// </para>
	/// </summary>
	internal readonly struct IrPrepareResult
	{
		/// <summary>IR 是否参与本轮（<see cref="ShouldUseIrCache"/>）。</summary>
		internal readonly bool Enabled;
		/// <summary>本轮是否命中 IR（= 新建 <see cref="IrState.Available"/>）。</summary>
		internal readonly bool Hit;
		/// <summary>新建的 IR 会话状态（<see cref="Enabled"/> 时非 null，交回宿主持有供运行期物化；
		/// 命中轮待载全集见 <see cref="IrState.AllFiles"/>）。</summary>
		internal readonly IrState? State;
		/// <summary>本轮内容指纹（日志口径）。</summary>
		internal readonly long Fingerprint;
		/// <summary>最终待载清单。</summary>
		internal readonly List<(string Filename, string File)> Pending;
		/// <summary>最终 firstDir 清单。</summary>
		internal readonly List<string> FirstDirs;
		/// <summary>最终 ERB 文件计数。</summary>
		internal readonly int ErbFileCount;

		internal IrPrepareResult(bool enabled, bool hit, IrState? state, long fingerprint,
			List<(string Filename, string File)> pending, List<string> firstDirs, int erbFileCount)
		{
			Enabled = enabled;
			Hit = hit;
			State = state;
			Fingerprint = fingerprint;
			Pending = pending;
			FirstDirs = firstDirs;
			ErbFileCount = erbFileCount;
		}
	}

	private readonly LoaderEnv env;
	/// <summary>本轮装载的 IR 显式上下文（ADR-0027 第四刀）：开关 / 指纹配置 / Rename 表 / 缓存路径基座
	/// 一次快照，本类方法体内不再直读 <c>Config.Config.*</c> 与 <c>Program.ExeDir</c>。</summary>
	private readonly IrLoadContext ctx;
	/// <summary>IR 生成期增量写器（按文件批量落盘，内存 O(单文件大小)，design §5.1）。仅生成 IR（未命中/失效）时非空。</summary>
	private ErbIrCache.IrCacheWriter? _writer;

	/// <summary>每次装载创建一个实例（决策二：不跨装载存活）；<paramref name="ctx"/> 由调用方在装载入口快照。</summary>
	internal ErbIrLoadPipeline(LoaderEnv env, IrLoadContext ctx)
	{
		this.env = env;
		this.ctx = ctx;
	}

	/// <summary>
	/// 装载期判定与建库：命中 → 建 <see cref="IrState"/>（索引驻留 + 常驻读句柄 + 命中全集）；
	/// 未命中/失效 → 丢弃陈旧 IR 清单自愈重扫、建 miss 态 <see cref="IrState"/>、开增量写器。
	/// 调用方须在本方法**之前**先跑 <see cref="ErbLoader.ResetIrState"/> 语义的清理。
	/// </summary>
	internal IrPrepareResult Prepare(bool irEnabled, string irPath,
		List<(string Filename, string File)> pending, List<string> firstDirs, int erbFileCount,
		List<string>? irFirstDirs, IReadOnlyList<(string Logical, string RelToRoot)>? irFileList)
	{
		if (!irEnabled)
			return new IrPrepareResult(false, false, null, 0, pending, firstDirs, erbFileCount);

		long irFp = ctx.ComputeContentFingerprint(pending, firstDirs);
		IrState state;
		if (ErbIrCache.TryLoad(irPath, irFp, out var index))
		{
			var ir = new IrState { Available = true, Path = irPath, Index = index };
			foreach (var kv in index)
			{
				if (!ir.ByFile.TryGetValue(kv.Key.File, out var list))
					ir.ByFile[kv.Key.File] = list = new List<IrIndexEntry>();
				list.Add(kv.Value);
			}
			// 索引区 = 文件加载序写盘；单文件内按 @ 出现序。按 BlobOffset 稳定此序（Dictionary 迭代序不保证）。
			foreach (var list in ir.ByFile.Values)
				list.Sort(static (a, b) => a.BlobOffset.CompareTo(b.BlobOffset));
			// 常驻读句柄（issues/05 句柄常驻）：hit 会话 blob 读取 open/close 折叠为 1 次；
			// appdata 为真实文件系统，长持读句柄无 SAF 流寿命问题。open 失败（文件恰被删）→
			// ReadBlob 回落临时开流。
			ir.ReadStream = SafCompat.OpenRead(irPath);
			state = ir;
			// issues/17 A′：命中轮全集。比较器与 IrState.ByFile 一致（OrdinalIgnoreCase），
			// 保证 Contains 与 TryGetValue 同口径——否则会出现"两边都查不到/口径不一"的静默错判。
			// 第四刀：随 IrState 一同生灭（原宿主独立字段 _irAllFiles 已删）。
			ir.AllFiles = new HashSet<string>(pending.Count, StringComparer.OrdinalIgnoreCase);
			foreach (var (logical, _) in pending)
				ir.AllFiles.Add(logical);
		}
		else
		{
			if (irFirstDirs != null || irFileList != null)
			{
				// issues/12 M4 + issues/13 M5 自愈兜底：IR 头存 firstDir 或 sidecar 清单被 fp 判为
				// 陈旧（增删/改名/清单与 IR 不同步）→ 不得用于实际加载（新文件永不进清单、
				// fp 永不收敛）→ 丢弃全量重扫，重算 fp 与清单；收尾 Complete 重写 sidecar。
				EmueraLog.Info("ErbIrCache",
					$"ir miss with ir-firstDirs({irFirstDirs?.Count ?? 0})/ir-fileList({irFileList?.Count ?? 0}) → discard and rescan");
				pending = BuildPendingList(env, ctx, null, out firstDirs, out erbFileCount);
				irFp = ctx.ComputeContentFingerprint(pending, firstDirs);
			}
			state = new IrState { Available = false, Path = irPath, Index = [] };
			// 未命中/失效 → 本轮生成 IR：开增量写器（tmp + 头占位），AppendFile 逐文件落盘。
			_writer = new ErbIrCache.IrCacheWriter(irPath);
		}
		EmueraLog.Info("ErbIrCache", $"ir hit={state.Available} files={pending.Count} fp={irFp:x16}");
		return new IrPrepareResult(true, state.Available, state, irFp, pending, firstDirs, erbFileCount);
	}

	/// <summary>
	/// 生成期是否收集 IR blob（= 增量写器存活）。这是本模块唯一的**并行 worker 共享面**：只读、
	/// 装载期内单值，无需同步（worker 只写各自的 <c>art.IrBlobs</c>，落盘由主 async 流经
	/// <see cref="AppendFile"/> 收口）。
	/// </summary>
	internal bool CollectIr => _writer != null;

	/// <summary>逐文件批量落盘（本文件 image 已并入共享状态，blob 写完即由调用方清空释放——内存 O(单文件)）。</summary>
	internal void AppendFile(string logical, IReadOnlyList<IrBlob> blobs) => _writer?.AppendFile(logical, blobs);

	/// <summary>
	/// 写盘收尾：Complete 增量写器（回填索引/firstDir 清单/头 + fsync + 原子替换）并写清单 sidecar；
	/// 命中轮的 legacy sidecar 自愈升级亦在此。
	/// <para>
	/// <b>必须在 <c>ParseScript</c> 之后显式调用</b>（决策三）：禁用 <c>using</c>/<c>Dispose</c> 收尾——
	/// 生成期写器要先收完全部文件 blob，提前收尾会产出半截索引。
	/// </para>
	/// </summary>
	internal void Complete(string irPath, long irFp, List<string> firstDirs, List<(string Filename, string File)> pending,
		bool irHit, bool irSidecarLegacy)
	{
		if (_writer is { } writer)
		{
			bool irWritten = writer.Complete(irFp, firstDirs);
			_writer = null;
			// issues/13 M5：IR 落盘成功后写清单 sidecar（pending 有序对 + 全树相对名清单）。
			// 门控 = ErbIrCache.UseFileListSidecar（issues/15 §0.3 后与读侧同口径：SAF 恒开，
			// Android legacy 也开，桌面关）；树清单取自本轮枚举 memo（BuildPendingList
			// 或 LoadTreesAsync 填充）。写失败无害（命中轮缺 sidecar → 回退枚举，收敛）。
			if (irWritten && ErbIrCache.UseFileListSidecar(env.ErbDir)
				&& Preload.TryGetTreeFiles(env.ErbDir, out var treeFiles))
			{
				var pendingRels = new List<(string Logical, string RelToRoot)>(pending.Count);
				foreach (var (logical, physical) in pending)
					pendingRels.Add((logical, SafPath.GetRelativePathFromRoot(env.ErbDir, physical)));
				var treeRels = new List<string>(treeFiles.Count);
				foreach (var f in treeFiles)
					treeRels.Add(SafPath.GetRelativePathFromRoot(env.ErbDir, f));
				// issues/14 B：CSV 树尾段——建库轮 CSV memo 已由 LoadTreesAsync 填充，顺手取相对名；
				// memo 未填充（CSV 目录缺失等）→ 传 null 写 legacy 布局，命中轮自愈兜底。
				List<string>? csvTreeRels = null;
				if (Preload.TryGetTreeFiles(env.CsvDir, out var csvTreeFiles))
				{
					csvTreeRels = new List<string>(csvTreeFiles.Count);
					foreach (var f in csvTreeFiles)
						csvTreeRels.Add(SafPath.GetRelativePathFromRoot(env.CsvDir, f));
				}
				if (ErbIrCache.TryWriteFileList(irPath, pendingRels, treeRels, csvTreeRels))
					EmueraLog.Info("ErbIrCache",
						$"ir fileList sidecar written: pending={pendingRels.Count} tree={treeRels.Count} csvTree={csvTreeRels?.Count.ToString() ?? "absent"}");
			}
		}
		else if (irHit && irSidecarLegacy && ErbIrCache.UseFileListSidecar(env.ErbDir)
			&& Preload.TryGetTreeFiles(env.ErbDir, out var healTree)
			&& Preload.TryGetTreeFiles(env.CsvDir, out var healCsv))
		{
			// issues/14 B：legacy sidecar 命中轮自愈——本命中轮 CSV 冷枚举（484ms）已把 CSV memo
			// 填好（ERB memo 由播种填充），数据齐了就地升级格式，legacy 用户只付一次冷枚举。
			// 失败无害（下次命中轮再试；legacy 读语义永不劣化于 14 之前）。
			var pendingRels = new List<(string Logical, string RelToRoot)>(pending.Count);
			foreach (var (logical, physical) in pending)
				pendingRels.Add((logical, SafPath.GetRelativePathFromRoot(env.ErbDir, physical)));
			var treeRels = new List<string>(healTree.Count);
			foreach (var f in healTree)
				treeRels.Add(SafPath.GetRelativePathFromRoot(env.ErbDir, f));
			var csvRels = new List<string>(healCsv.Count);
			foreach (var f in healCsv)
				csvRels.Add(SafPath.GetRelativePathFromRoot(env.CsvDir, f));
			if (ErbIrCache.TryWriteFileList(irPath, pendingRels, treeRels, csvRels))
				EmueraLog.Info("ErbIrCache",
					$"ir fileList sidecar upgraded (legacy→csv): pending={pendingRels.Count} tree={treeRels.Count} csvTree={csvRels.Count}");
		}
	}

	/// <summary>放弃本次生成并清 tmp（加载异常/失败路径；幂等）。写器清空后 <see cref="CollectIr"/> 转 false。</summary>
	internal void Abort()
	{
		_writer?.Abort();
		_writer = null;
	}
}
