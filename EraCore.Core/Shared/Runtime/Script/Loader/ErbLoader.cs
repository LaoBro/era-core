using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Sub;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.Runtime.Script.Loader;

internal sealed class ErbLoader
{
	// ADR-0012：F2——构造注入显式依赖，不持 Process 反向引用。
	//   scaningLine 写 → Action<LogicalLine> sink
	//   进程级目录 / Analysis / Debug 标志 → LoaderEnv
	//   标识符字典 → IdentifierDictionary idDic（原走 GlobalStatic 兼容层）
	public ErbLoader(EmueraConsole main, ExpressionMediator exm, IdentifierDictionary idDic,
		LoaderEnv env, Action<LogicalLine> setScanLine)
	{
		output = main;
		this.exm = exm;
		this.idDic = idDic;
		this.env = env;
		this.setScanLine = setScanLine;
	}
	readonly ExpressionMediator exm;
	readonly IdentifierDictionary idDic;
	readonly LoaderEnv env;
	readonly Action<LogicalLine> setScanLine;
	readonly EmueraConsole output;
	readonly HashSet<string> ignoredFNFWarningFiles = new(StringComparer.OrdinalIgnoreCase);
	int ignoredFNFWarningCount;

	// 决策三-a：nestCheck 栈/列表复用 — 1000 函数避免 3000+ 次堆分配。
	// loadErb/ParseScript 为单线程调用，字段在 nestCheck 入口 Clear() 即可安全复用。
	private readonly Stack<InstructionLine> _nestStackReuse = new();
	private readonly Stack<InstructionLine> _selectcaseStackReuse = new();
	private readonly List<InstructionLine> _tempLineListReuse = new();

	int enabledLineCount;
	LabelDictionary labelDic = null!;

	// lazy-function-load（B 线 B1）：逻辑文件名 → 物理路径映射，供物化接缝回读源文件用。
	// 在 LoadErbDir / LoadErbList 构建（fname 即 Position.Filename 同源），运行时物化据此取 filepath。
	private Dictionary<string, string> _filePathByLogical = new(StringComparer.OrdinalIgnoreCase);

	// ===== b2-irdiskcache（B 线 B2，阶段① blob 库）=====
	// 本 loader 会话的 IR 状态。<see cref="_ir"/> 非空 = IR 参与本会话（开 IrDiskCacheLoad 且 lazy 且非 AnalysisMode）。
	//  <see cref="IrState.Available"/> = 启动校验命中（第二次启动走 IR 后背物化）；否则首轮走 B1 源并在此轮生成 IR 落盘。
	//  运行时物化（RuntimeLoader 保留本实例）在 <see cref="MaterializeLabel"/> 依此决定 blob / 源后背。
	private IrState? _ir;
	/// <summary>IR 生成期增量写器（按文件批量落盘，内存 O(单文件大小)，design §5.1）。仅生成 IR（未命中/失效）时非空。</summary>
	private ErbIrCache.IrCacheWriter? _irWriter;

	/// <summary>
	/// issues/17 A′：命中轮的待载文件全集（逻辑名，与 <see cref="IrState.ByFile"/> 同比较器）。
	/// <para>
	/// 用途：补出"该文件确实没有函数"这一信息。IR 索引只记录<b>有函数</b>的文件
	///（<see cref="ErbIrCache.IrCacheWriter.AppendFile"/> 对 <c>blobs.Count == 0</c> 直接 return），
	/// 故 0 函数/空文件/纯注释文件在命中轮查不到条目，旧判据下只能回落读源——SAF 下每文件约 25ms，
	/// 真机 25 个文件累计 ~620ms，且因主循环按序 <c>await</c> 而串行阻塞整条流水线。
	/// </para>
	/// <para>
	/// 正确性：<c>fp = ComputeContentFingerprint(pending, …)</c> 已把文件集合纳入内容指纹，
	/// 文件增删必改 fp 必 miss 重建——故"命中轮 pending 全集"与"建库轮实际处理过的文件集合"必然一致。
	/// 仅命中（<see cref="IrState.Available"/>）时非 null；<see cref="ResetIrState"/> 清空。
	/// </para>
	/// </summary>
	private HashSet<string>? _irAllFiles;

	// ===== S0-M2 取证探针（2026-09-05，diagnostic-knobs issues/11）：加载窗 blob I/O 分账 =====
	// parsescript 段内所有 blob 读取都经 ReadBlobMeasured（ComputeSignature / ReparseFunctionFromIr
	// 两处咽喉）累计；LoadErbDir 起始清零，phase=parsescript 日志行输出 blobReads/blobBytes/blobMs。
	// 运行期（加载完成后）懒物化同样吃累计，但该日志只在加载窗打一次，不影响「blob I/O vs 物化」口径。
	private long _blobReadTicks;
	private long _blobReadBytes;
	private int _blobReadCount;

	private sealed class IrState
	{
		/// <summary>本进程加载轮是否命中 IR（true = 第二次启动，索引已驻留）。</summary>
		public bool Available;
		/// <summary>IR 文件路径（缓存目录）。</summary>
		public string Path = null!;
		/// <summary>
		/// 常驻读句柄（issues/05 句柄常驻）：hit 会话 blob 读取的 open/close 折叠为 1 次，
		/// 全部经 seek+局部读（content-URI 镜像在 app 私有存储，真实文件系统）。miss 会话为 null
		/// （_irWriter 写侧与读句柄天然互斥）。生命周期 = IrState 生命周期：reload / 加载失败 /
		/// 部分重载（<see cref="ErbLoader.ResetIrState"/>）显式关闭——置空后没有第二次机会。
		/// Windows 桌面跨会话时序（issues/05「留意」）：旧 loader 的读句柄在游戏退出后靠 GC 终结器
		/// 释放，若新会话在终结前 miss 重建，delete-dest 可能被旧句柄锁住 → IR 写被静默跳过、下次再建，
		/// 自愈且非致命；同会话内 reload 的锁冲突已由 ResetIrState 先关后建消除。
		/// </summary>
		public Stream? ReadStream;
		/// <summary>（逻辑文件名, 标签名）→ 索引条目。驻留（几 MB 量级），等价 labelDic。</summary>
		public Dictionary<(string File, string Label), IrIndexEntry> Index = null!;
		/// <summary>逻辑文件名 → 该文件内按 @ 出现序的索引条目（TryGenerateFromIr 用，免 O(n·files) 逐文件扫全表）。</summary>
		public Dictionary<string, List<IrIndexEntry>> ByFile = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>读单个函数 blob：优先常驻句柄 seek+局部读；句柄缺失（异常路径）临时开流读一次。</summary>
		public IrBlob ReadBlob(in IrIndexEntry entry)
		{
			var stream = ReadStream;
			if (stream != null)
				return ErbIrCache.ReadBlob(stream, entry);
			using var fresh = SafCompat.OpenRead(Path) ?? throw new IOException("IR file missing");
			return ErbIrCache.ReadBlob(fresh, entry);
		}

		/// <summary>显式关闭常驻读句柄（幂等；reload/失败/部分重载的 <c>_ir = null</c> 置空点调用）。</summary>
		public void CloseHandle()
		{
			var s = ReadStream;
			ReadStream = null;
			try { s?.Dispose(); } catch { /* 关闭失败不掩盖主流程 */ }
		}
	}

	private static string IrCachePath() => ErbIrCache.GetIrCachePath(Program.ExeDir);

	/// <summary>
	/// S0-M2 取证（2026-09-05）：带分账的 blob 读取——所有加载窗 blob 读统一经此包装，累计
	/// ticks/字节/次数到 <see cref="_blobReadTicks"/> 等字段（finally 保证异常路径也计入）。
	/// </summary>
	private IrBlob ReadBlobMeasured(IrState ir, in IrIndexEntry entry)
	{
		long t0 = Stopwatch.GetTimestamp();
		try
		{
			return ir.ReadBlob(entry);
		}
		finally
		{
			_blobReadTicks += Stopwatch.GetTimestamp() - t0;
			_blobReadBytes += entry.BlobLength;
			_blobReadCount++;
		}
	}

	/// <summary>
	/// b2-irdiskcache 参与判定唯一决策点（真值表穷举于 ErbIrCacheTests）。
	/// 2026-09-04 方案 B（issues/05）后不再含 content-URI 维度：content-URI（SAF）游戏经
	/// <see cref="ErbIrCache.GetIrCachePath"/> 把缓存镜像到 app 私有存储（真实文件系统，可 seek +
	/// 原子替换），读放大止血解除——判定只看 开关 × lazy × 非 AnalysisMode。
	/// </summary>
	internal static bool ShouldUseIrCache(bool irDiskCacheConfigured, bool lazyFunctionLoad, bool analysisMode)
		=> irDiskCacheConfigured && lazyFunctionLoad && !analysisMode;

	/// <summary>
	/// issue 09：ERB 待载清单构建（唯一路径——LoadErbDir 与启动 IR 命中预判定共用同一语义）。
	/// firstDir（*#* 目录）序在前、主枚举去重序在后（erb-loader-parallel 03 合并序）。
	/// <paramref name="firstDirOverride"/> 非 null（issues/12 M4 命中轮）= 清单来自 IR 头存区
	///（相对名，经 <see cref="ResolveFirstDirPath"/> 重建目录路径），跳过 SAF
	/// <c>GetDirectories("*#*")</c> 冷树通配扫描（真机 ≈15.9s/进程）；清单陈旧由指纹兜底
	///（firstDirs 进 <see cref="ErbIrCache.ComputeContentFingerprint"/>，miss → 调用方回退全扫）。
	/// 缓存空（命中轮/预判定）时 <see cref="Preload.GetFilesFromCache"/> 返回 null →
	/// 走 <see cref="Preload.EnumerateFilesFromTree"/>（与缓存键同源的 Preload 语义全树枚举——
	/// launch7 真机实测 Config.GetFiles 的 SAF 通配/displayName 差异会丢文件/路径变形，弃用）。
	/// </summary>
	internal static List<(string Filename, string File)> BuildPendingList(LoaderEnv env, List<string>? firstDirOverride, out List<string> firstDirs, out int erbFileCount,
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
			EmueraLog.Info("ErbLoader",
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
				Config.Config.SearchSubdirectory ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly) ?? [];
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
		var erbFiles = Preload.GetFilesFromCache(env.ErbDir, ".ERB", "ErbLoader")
			?? Preload.EnumerateFilesFromTree(env.ErbDir, env.DirAccessor, ".ERB");
		erbFileCount = erbFiles.Count;
		// erb-loader-parallel 03：统一有序待载清单（firstDir 目录序在前、主枚举去重序在后），
		// 串行 / 并行两条调度共用同一清单与合并序
		List<(string Filename, string File)> pending = [];
		foreach (var dir in firstDirFull)
		{
			var firstErbFiles = Preload.GetFilesFromCache(dir, ".ERB", "ErbLoader")
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
		EmueraLog.Info("ErbLoader",
			$"BuildPendingList ms={sw.ElapsedMilliseconds} files={erbFileCount} firstDirs={firstDirs.Count} " +
			$"firstDirScanMs={firstDirScanMs} src={(firstDirOverride != null ? "ir" : "scan")} fileListSrc=enum");
		return pending;
	}

	/// <summary>
	/// issues/12 M4：firstDir 相对名（<see cref="SafPath.GetRelativePathFromRoot"/> 形态，'\' 分隔）
	/// → 目录全路径。content URI 必须经 <see cref="IGameDirAccessor.CombinePath"/>（SAF 实现走
	/// DocumentsContract.BuildDocumentUriUsingTree 正确编码 documentId——裸拼接 URI 会被 Android
	/// 解析成目录本身，AssetChannel 同款坑）；本地路径走 Path.Combine。
	/// </summary>
	private static string ResolveFirstDirPath(LoaderEnv env, string relativeName)
		=> env.DirAccessor.CombinePath(env.ErbDir,
			SafPath.IsContentUri(env.ErbDir) ? relativeName.Replace('\\', '/') : relativeName);

	/// <summary>
	/// issue 09：IR 命中预判定（启动内容 Preload 门控）。不读源内容——文件清单走
	/// Preload 语义枚举（缓存空即命中轮语义），指纹计算只读文件
	/// stat（长度/mtime，content-URI 降级 0，见 <see cref="ErbIrCache.ComputeContentFingerprint"/>），
	/// firstDir 清单优先读 IR 头存区（issues/12 M4，命中轮零 SAF 通配扫描），
	/// <see cref="ErbIrCache.TryLoad"/> 只读 IR 头 + 索引区。
	/// 命中 → 调用方可跳过 <see cref="Preload.LoadTreesAsync"/> 的内容读取（物化走 blob、源回退走懒读）；
	/// 未命中 / IR 关闭（<see cref="ShouldUseIrCache"/>）/ 任何异常 → false，调用方走完整 Preload。
	/// 与 LoadErbDir 内判定共享 BuildPendingList + 指纹逻辑（顺序无关），LoadErbDir 会再次核对。
	/// </summary>
	internal static bool IsIrHitRound(LoaderEnv env)
	{
		try
		{
			if (!ShouldUseIrCache(Config.Config.IrDiskCacheLoad, Config.Config.LazyFunctionLoad, env.AnalysisMode))
			{
				// issue 09 真机排障（2026-09-05 launch5 分析）：静默 false 曾使「未命中」无法与旧构建区分——
				// 此处显式记录，任何 false 分支都有日志可查（hit / 关闭 / 异常三态齐全）。
				EmueraLog.Info("ErbIrCache",
					$"pre-hit check skipped: ir-disabled (irDisk={Config.Config.IrDiskCacheLoad} lazy={Config.Config.LazyFunctionLoad} analysis={env.AnalysisMode})");
				return false;
			}
			var irPath = IrCachePath();
			// issues/12 M4：优先读 IR 头存 firstDir 清单替代 SAF 通配扫描（读侧受回退旋钮门控，
			// FALSE = 恒扫描）；无 v2 IR / 损坏 → null → 照旧扫描。清单正确性由 fp（含清单）判定。
			List<string>? irFirstDirs =
				Config.Config.IrFirstDirList && ErbIrCache.TryReadFirstDirs(irPath, out var irFd) ? irFd : null;
			// issues/13 M5：清单 sidecar（有序 pending 对 + 全树清单）。读侧受旋钮 + 形态门双门控。
			// issues/15 §0.3：形态门由 ErbIrCache.UseFileListSidecar 统一给出——Android legacy
			// （真实路径）也开，因为其"本地路径"是 FUSE 挂载，枚举与 SAF 同量级（真机 569ms）；
			// 桌面仍关（2026-09-09 裁定「Windows/CLI 保留自动失效」）。
			List<(string Logical, string RelToRoot)>? irFileList = null;
			List<string>? irTreeRels = null;
			List<string>? irCsvTreeRels = null;
			if (Config.Config.IrFileListCache && ErbIrCache.UseFileListSidecar(env.ErbDir)
				&& ErbIrCache.TryReadFileListFull(irPath, out var flPending, out var flTree, out var flCsv, out _))
			{
				irFileList = flPending;
				irTreeRels = flTree;
				irCsvTreeRels = flCsv;
			}
		var pending = BuildPendingList(env, irFirstDirs, out var firstDirsUsed, out _, irFileList);
		// S0-M6（空档取证，2026-09-09）：BPL#1 done → pre-hit 行之间真机实测 ~1.4s 无日志盲区——
		// 三步分账：fp（content URI 走 stat 降级，预期纯内存毫秒级）/ TryLoad（IR 索引区 ~14647 条
		// entry 读取）/ SeedTreeMemo（逐条 CombinePath 播种，与 BPL replay 同款开销模式）。
		var fpSw = System.Diagnostics.Stopwatch.StartNew();
		long fp = ErbIrCache.ComputeContentFingerprint(pending, ParserMediator.RenameDic, firstDirsUsed);
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
				$"ic={Config.Config.IgnoreCase} rf={Config.Config.UseRenameFile} rp={Config.Config.UseReplaceFile} " +
				$"ra={(int)Config.Config.ReduceArgumentOnLoad} nr={Config.Config.NeedReduceArgumentOnLoad} " +
				$"t3={Config.Config.SystemIgnoreTripleSymbol} rename={ParserMediator.RenameDic.Count}");
			return hit;
		}
		catch (Exception ex)
		{
			// 枚举/stat 等任何异常 → 保守走完整 Preload，不阻断启动。
			EmueraLog.Warn("ErbIrCache", $"pre-hit check skipped: {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// b2-irdiskcache：清空本会话 IR 状态并显式关闭常驻读句柄（issues/05 句柄常驻的关闭点——
	/// reload / 加载失败 / 部分重载的既有 <c>_ir = null</c> 置空处统一走这里）。生成期写器一并
	/// <see cref="ErbIrCache.IrCacheWriter.Abort"/>（清 tmp，异常路径不留下半截缓存）。
	/// </summary>
	private void ResetIrState()
	{
		_ir?.CloseHandle();
		_ir = null;
		_irWriter?.Abort();
		_irWriter = null;
		_irAllFiles = null; // issues/17 A′：全集只对应当前 _ir，随之一并失效
	}

	/// <summary>erb-loader-parallel 03：并行生成度上限——窗口内产物同时驻留 = 上限文件数的解析产物，
	/// 封顶防内存峰值（spec 实测 eraTW 规模 12 核理想并行度 ~8-10x，取 8；默认度 = ProcessorCount）。
	/// 06-K1：壳层取证旋钮（<see cref="ConfigData.OverrideMaxParallelErbFiles"/>）可覆盖，见下。</summary>
	internal const int MaxParallelErbFiles = 8;

	/// <summary>
	/// 06-K1 决策点（真值表穷举于 MaxParallelErbFilesTests）：auto（null）= min(核数, 8) 与现状逐位一致；
	/// 显式 4/8/16 = 直接生效（与核数解耦的取证档——16 档的价值正在于超订阅/更多未合并产物驻留观测）。
	/// 两处消费同源同拨：LoadPendingParallel 窗口（文件数语义）+ RunUncalledSweepParallel 清扫度
	/// （线程数语义）——修订原 R3-Q11「度数硬编码不可配」（diagnostic-knobs/issues/06）。
	/// </summary>
	internal static int ResolveMaxParallelErbFiles(int? requested, int processorCount)
		=> requested ?? Math.Min(processorCount, MaxParallelErbFiles);

	/// <summary>
	/// erb-loader-parallel 06：WP0 局部性实验用 GC 快照（累计分配量 / 各代收集次数）。
	/// <see cref="Diff"/> 输出段内增量 + 当前堆大小，挂在 phase=genmerge / phase=parsescript
	/// 日志行尾——判别"ParseScript 回归来自 GC 压力还是堆布局交错"（两段 GC 增量相近而耗时
	/// 持续偏高 ⇒ GC 压力说排除）。调用均为廉价计数器读取，常态开销可忽略。
	/// </summary>
	private readonly struct GcSnapshot
	{
		readonly long _alloc;
		readonly int _g0, _g1, _g2;

		GcSnapshot(long alloc, int g0, int g1, int g2) { _alloc = alloc; _g0 = g0; _g1 = g1; _g2 = g2; }

		internal static GcSnapshot Take() => new(
			GC.GetTotalAllocatedBytes(precise: false),
			GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

		internal static string Diff(in GcSnapshot before)
		{
			var after = Take();
			return $"gc dAllocMB={(after._alloc - before._alloc) >> 20} dG0={after._g0 - before._g0} dG1={after._g1 - before._g1} dG2={after._g2 - before._g2} heapMB={GC.GetTotalMemory(false) >> 20}";
		}
	}

	bool noError = true;
	/// <summary>
	/// 複数のファイルを読む
	/// </summary>
	/// <param name="filepath"></param>
	/// <param name="externalCt">erb-bounded-lifecycle：外部取消令牌（预留 reload/会话退出接线；当前调用方未传即 default）。
	/// 与调度内部首错 CTS 链接——取消后不再启动新 worker，已启动 worker 收束后才返回。</param>
	public async Task<bool> LoadErbDir(string erbDir, bool displayReport, LabelDictionary labelDictionary,
		CancellationToken externalCt = default)
	{
		//1.713 labelDicをnewする位置を変更。
		//checkScript();の時点でExpressionPerserがProcess.instance.LabelDicを必要とするから。
		labelDic = labelDictionary;
		labelDic.Initialized = false;
		// lazy-function-load：加载开始即失效运行时物化桥（首次加载本就 null；重载期间禁用运行时物化，
		// 主循环用「本实例」就地物化；成功结束再重新挂载）。失败路径不清——保留旧桥状态亦无害。
		GlobalStatic.Process!.RuntimeLoader = null;
		// b2-irdiskcache：IR 参与条件 = 开 IrDiskCacheLoad && lazy && 非 AnalysisMode（唯一决策点
		// <see cref="ShouldUseIrCache"/>；content-URI 游戏经方案 B 镜像到 app 私有存储，读放大止血已解除，
		// 见 ErbIrCache 的 GetIrCachePath 注释）。启动进入文件读取前一次性判定：命中 IR（Available=true，
		// 索引驻留）→ 本轮免源扫描直建桩；未命中/失效 → B1 源路径跑本轮，收尾重建 IR。
		// 指纹基于 pending 文件集合 + firstDir 清单 + 解析相关配置（design §6）。
		string irPath = IrCachePath();
		bool irEnabled = ShouldUseIrCache(Config.Config.IrDiskCacheLoad, Config.Config.LazyFunctionLoad, env.AnalysisMode);
		// issues/12 M4：命中轮清单构建前先读 IR 头存 firstDir 清单（读侧受回退旋钮门控，FALSE = 恒扫描；
		// 无 v2 IR / 损坏 → null → 照旧扫描）——与 IsIrHitRound 预判定同源同拨。
		List<string>? irFirstDirs =
			irEnabled && Config.Config.IrFirstDirList && ErbIrCache.TryReadFirstDirs(irPath, out var loadFd) ? loadFd : null;
		// issues/13 M5：sidecar 清单（与 IsIrHitRound 同源同拨；陈旧自愈见下方 discard-rescan 分支）。
		// issues/14 B：legacy 标记供命中轮收尾自愈重写（CSV 尾段缺失 → CSV 冷枚举一次后升级格式）。
		List<(string Logical, string RelToRoot)>? irFileList = null;
		bool irSidecarLegacy = false;
		if (irEnabled && Config.Config.IrFileListCache && SafPath.IsContentUri(env.ErbDir)
			&& ErbIrCache.TryReadFileListFull(irPath, out var loadFl, out _, out _, out var loadLegacy))
		{
			irFileList = loadFl;
			irSidecarLegacy = loadLegacy;
		}
		// issue 09：待载清单构建唯一路径（缓存命中轮与启动 IR 预判定共用；命中轮无缓存 →
		// Preload 语义枚举，见 <see cref="BuildPendingList"/>）。
		List<(string Filename, string File)> pending = BuildPendingList(env, irFirstDirs, out List<string> firstDirs, out int erbFileCount, irFileList);
		List<string> isOnlyEvent = [];
		noError = true;
		var starttime = DateTime.Now;
		EmueraLog.Info("ErbLoader", $"LoadErbDir start: dir={erbDir}, files={erbFileCount}, firstDirs={firstDirs.Count}");
		try
		{
			labelDic.RemoveAll();
			// S0-M2：分账累计清零（reload / 失败路径的残留运行期累计一并归零；blob 读只发生在 ParseScript 及之后）。
			_blobReadTicks = 0;
			_blobReadBytes = 0;
			_blobReadCount = 0;
			ResetIrState();
			long irFp = 0;
			if (irEnabled)
			{
				irFp = ErbIrCache.ComputeContentFingerprint(pending, ParserMediator.RenameDic, firstDirs);
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
					_ir = ir;
					// issues/17 A′：命中轮全集。比较器与 IrState.ByFile 一致（OrdinalIgnoreCase），
					// 保证 Contains 与 TryGetValue 同口径——否则会出现"两边都查不到/口径不一"的静默错判。
					_irAllFiles = new HashSet<string>(pending.Count, StringComparer.OrdinalIgnoreCase);
					foreach (var (logical, _) in pending)
						_irAllFiles.Add(logical);
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
						pending = BuildPendingList(env, null, out firstDirs, out erbFileCount);
						irFp = ErbIrCache.ComputeContentFingerprint(pending, ParserMediator.RenameDic, firstDirs);
					}
					_ir = new IrState { Available = false, Path = irPath, Index = [] };
					// 未命中/失效 → 本轮生成 IR：开增量写器（tmp + 头占位），MergeErbFile 逐文件落盘。
					_irWriter = new ErbIrCache.IrCacheWriter(irPath);
				}
				EmueraLog.Info("ErbIrCache", $"ir hit={_ir.Available} files={pending.Count} fp={irFp:x16}");
			}
			// lazy-function-load：建立 逻辑文件名→物理路径 映射（Position.Filename 与 fname 同源）。
			// 须用 IR 判定后的最终 pending——issues/12 M4 的 miss 兜底可能在上方重建过清单。
			_filePathByLogical = new(StringComparer.OrdinalIgnoreCase);
			foreach (var (logical, physical) in pending)
				_filePathByLogical[logical] = physical;
			// erb-loader-parallel 03：默认并行；配置串行或 AnalysisMode 时走 01 完成时的串行路径
			//（fallback = 语义偏差排查 / 单线程调试的 abort 杠杆；AnalysisMode 的 PrintC 输出与
			// funcCount 进度节奏依赖串行即时检查，一律强制串行）
			// erb-loader-parallel 03：阶段计时（genmerge = 生成+合并，与 ParseScript 分开观测）
			// erb-loader-parallel 06：WP0 结论落地——附 GC 增量统计（分配量 / 各代收集次数）。
			// 机制实验已证并行 ParseScript 回归来自线程池分配上下文交错（GC 增量与串行一致而耗时
			// 高 25-45%，紧凑化 GC 修复证伪），接受回归；统计留作后续并行化工作的观测基线。
			//（详见 .scratch/erb-loader-parallel/spec-phase2.md WP0 机制结论节）
			var phaseSw = Stopwatch.StartNew();
			var gcBeforeGenmerge = GcSnapshot.Take();
			if (Config.Config.ParallelErbLoad && !env.AnalysisMode)
				await LoadPendingParallel(pending, isOnlyEvent, displayReport, starttime, externalCt);
			else
				await LoadPendingSerial(pending, isOnlyEvent, displayReport, starttime);
			phaseSw.Stop();
			EmueraLog.Info("ErbLoader", $"phase=genmerge parallel={Config.Config.ParallelErbLoad && !env.AnalysisMode} files={pending.Count} ms={phaseSw.ElapsedMilliseconds} {GcSnapshot.Diff(gcBeforeGenmerge)}");
			ParserMediator.FlushWarningList();
#if DEBUG
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, (DateTime.Now - starttime).TotalMilliseconds));
#endif
			if (displayReport)
				output.PrintSystemLine(trsl.BuildingUserFunc.Text);
			setLabelsArg();
			ParserMediator.FlushWarningList();
			labelDic.Initialized = true;
#if DEBUG
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, (DateTime.Now - starttime).TotalMilliseconds));
#endif
			if (displayReport)
				output.PrintSystemLine(trsl.CheckingSyntax.Text);

			// 决策四：ParseScript 阶段计时（Stopwatch 精度，displayReport 时可见）
			// erb-loader-parallel 06：附 GC 增量统计（WP0 判别数据：期间 GC 增量 vs 耗时）
			var parseSw = Stopwatch.StartNew();
			var gcBeforeParse = GcSnapshot.Take();
			// CA2016：ParseScript 本轮不做取消感知（erb-bounded-lifecycle 只覆盖生成/合并调度），显式不传播令牌
			await Task.Run(() => ParseScript(), CancellationToken.None);
			parseSw.Stop();
			EmueraLog.Info("ErbLoader", $"phase=parsescript ms={parseSw.ElapsedMilliseconds} {GcSnapshot.Diff(gcBeforeParse)}" +
				$" blobReads={_blobReadCount} blobBytes={_blobReadBytes} blobMs={(_blobReadTicks * 1000) / Stopwatch.Frequency}");
			if (displayReport)
				output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, parseSw.ElapsedMilliseconds));

			ParserMediator.FlushWarningList();

			// preload-cache-lifecycle 01（ADR-0023）：加载管线收尾清空源文本缓存。
			// 位于并行解析合并与 ParseScript 全部完成之后、单线程执行；ERH/CSV 条目随同一扫清空
			//（它们在更早阶段已消费完毕）。失败路径（上方 catch）不清空——保留新鲜缓存辅助排障。
			Preload.ClearCachedLines();

			// b2-irdiskcache：本轮生成期收集完毕 → Complete 增量写器（回填索引/firstDir 清单/头 + fsync +
			// 原子替换；issues/12 M4：firstDirs = 本轮实际清单——兜底重扫后 firstDirs 已是新鲜值）。
			// 生成失败静默（进程继续走 B1 路径，下次再试；design §5.1 异常策略），成功即下次启动命中。
			if (_irWriter is { } writer)
			{
				bool irWritten = writer.Complete(irFp, firstDirs);
				_irWriter = null;
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
			else if (_ir is { Available: true } && irSidecarLegacy && ErbIrCache.UseFileListSidecar(env.ErbDir)
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

#if DEBUG
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, (DateTime.Now - starttime).TotalMilliseconds));
#endif
			if (displayReport)
				output.PrintSystemLine(trsl.LoadComplete.Text);
		}
		catch (Exception e)
		{
			ParserMediator.FlushWarningList();
			// b2-irdiskcache：加载异常 → 放弃本次 IR 生成并清 tmp（不留下半截缓存），同时显式关闭
			// 常驻读句柄（issues/05：加载失败路径的关闭点，置空后没有第二次机会）。
			ResetIrState();
#if !HEADLESS
			System.Media.SystemSounds.Hand.Play();
#endif
			var msg = $"[ErbLoader] EXCEPTION: {e.GetType().Name}: {e.Message}\n{e.StackTrace}";
			EmueraLog.Error("ErbLoader", msg);
			output.PrintError(string.Format(trerror.UnexpectedErrorFrom.Text, AssemblyData.EmueraVersionText));
			output.PrintError(e.GetType().ToString() + ":" + e.Message);
			return false;
		}
		finally
		{
			// mem-leak-fix：ParseScript 主循环挂载的 _activeArgLoader 静态槽在异常路径不经过
			// L1593 的正常收尾——加载失败时最后一个 ErbLoader 实例（含 IR 索引 / 路径映射）会被
			// 静态字段钉住直到下次成功加载。此处 finally 统一清理（正常路径已置 null，幂等）。
			_activeArgLoader = null;
			// finally exception bypasses catch above and bubbles to Process.Initialize
			try
			{
				setScanLine(null!);
			}
			catch (Exception ex)
			{
				var msg = $"[ErbLoader] finally exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
				EmueraLog.Error("ErbLoader", msg);
			}
		}
		EmueraLog.Info("ErbLoader", $"LoadErbDir done: noError={noError}, labels={labelDic?.Count ?? 0}");
		isOnlyEvent.Clear();
		// lazy-function-load：保留本 loader（含 _filePathByLogical / env / setScanLine）供运行时物化接缝复用。
		GlobalStatic.Process.RuntimeLoader = this;
		return noError;
	}

	/// <summary>
	/// 指定されたファイルを読み込む
	/// </summary>
	/// <param name="filename"></param>
	public async Task<bool> LoadErbList(List<string> paths, LabelDictionary labelDictionary)
	{
		string fname;
		List<string> isOnlyEvent = [];
		noError = true;
		labelDic = labelDictionary;
		labelDic.Initialized = false;
		// lazy-function-load：部分重载加载期间禁用运行时物化桥（成功结束再挂载，见方法尾部）。
		GlobalStatic.Process!.RuntimeLoader = null;
		// b2-irdiskcache：部分重载（指定文件集）不走 IR——ResetIrState 置空并使本文件集走源生成而非
		// TryGenerateFromIr 直建桩（须重读源反映新内容），同时关闭上一轮常驻读句柄（issues/05 关闭点）；
		// 单文件增量失效不在 v1 范围（design §6「不做」），本路不生成 _irWriter。
		ResetIrState();

		await Task.Run(() =>
		{
			foreach (var fpath in paths)
			{
				if (fpath.StartsWith(env.ErbDir, Config.Config.SCIgnoreCase) && !env.AnalysisMode)
					fname = SafPath.GetRelativePathFromRoot(env.ErbDir, fpath);
				else
					fname = fpath;
				if (env.AnalysisMode)
				{
					output.PrintSystemLine(string.Format(trsl.LoadingFile.Text, fname));
				}
				// lazy-function-load：部分重载路径的物理路径回读映射
				_filePathByLogical[fname] = fpath;
				// erb-loader-parallel 01/03：与 LoadErbDir 串行调度同款“生成 → 按序合并”两步
				//（部分重载文件数少，不参与并行化；跨文件检查保持串行即时）
				var art = GenerateErbFile(fpath, fname, isOnlyEvent, deferCrossFileChecks: false);
				MergeErbFile(art, isOnlyEvent);
			};
		});
		if (env.AnalysisMode)
			output.NewLine();
		ParserMediator.FlushWarningList();
		setLabelsArg();
		ParserMediator.FlushWarningList();
		labelDic.Initialized = true;

		// 决策四：ParseScript 阶段计时（AnalysisMode 时可见）
		var parseSw = Stopwatch.StartNew();
		await Task.Run(() => ParseScript());
		parseSw.Stop();
		if (env.AnalysisMode)
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, parseSw.ElapsedMilliseconds));

		ParserMediator.FlushWarningList();
		setScanLine(null!);
		// preload-cache-lifecycle 01：部分重载收尾同款清空（范围同 LoadErbDir——全量，见 spec 清空范围）
		Preload.ClearCachedLines();
		isOnlyEvent.Clear();
		// lazy-function-load：部分重载后保留最新 loader 供运行时物化接缝复用。
		GlobalStatic.Process.RuntimeLoader = this;
		return noError;
	}

	private sealed class PPState
	{
		// ADR-0012：原走 Program.DebugMode / GlobalStatic.IdentifierDictionary（static），
		// 现经构造注入从外层 ErbLoader 传入。
		readonly LoaderEnv env;
		readonly IdentifierDictionary idDic;
		internal PPState(LoaderEnv env, IdentifierDictionary idDic) { this.env = env; this.idDic = idDic; }

		bool skip;
		bool done;
		public bool Disabled;
		readonly Stack<bool> disabledStack = new();
		readonly Stack<bool> doneStack = new();
		readonly Stack<string> ppMatch = new();

		internal void AddKeyWord(string token, string token2, ScriptPosition? position)
		{
			//bool token2enabled = string.IsNullOrEmpty(token2);
			switch (token)
			{
				case "SKIPSTART":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					if (skip)
					{
						ParserMediator.Warn(trerror.DuplicateSkipstart.Text, position, 1);
						break;
					}
					ppMatch.Push("SKIPEND");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					skip = true;
					Disabled = true;
					done = false;
					break;
				case "IF_DEBUG":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					Disabled = !env.DebugMode;
					done = !Disabled;
					break;
				case "IF_NDEBUG":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					Disabled = env.DebugMode;
					done = !Disabled;
					break;
				case "IF":
					if (string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.MissingArguments.Text, token), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					Disabled = idDic.GetMacro(token2) == null;
					done = !Disabled;
					break;
				case "ELSEIF":
					if (string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.MissingArguments.Text, token), position, 1);
						break;
					}
					if (ppMatch.Count == 0 || ppMatch.Pop() != "ELSEIF")
					{
						ParserMediator.Warn(string.Format(trerror.IsInvalid.Text, "[ELSEIF]"), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					Disabled = done || idDic.GetMacro(token2) == null;
					done |= !Disabled;
					break;
				case "ELSE":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					if (ppMatch.Count == 0 || ppMatch.Pop() != "ELSEIF")
					{
						ParserMediator.Warn(string.Format(trerror.IsInvalid.Text, "[ELSE]"), position, 1);
						break;
					}
					ppMatch.Push("ENDIF");
					Disabled = done;
					done = true;
					break;

				case "SKIPEND":
					{
						if (!string.IsNullOrEmpty(token2))
						{
							ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
							break;
						}
						string match = ppMatch.Count == 0 ? "" : ppMatch.Pop();
						if (match != "SKIPEND")
						{
							ParserMediator.Warn(trerror.UnexpectedSkipend.Text, position, 1);
							break;
						}
						skip = false;
						Disabled = disabledStack.Pop();
						done = doneStack.Pop();
					}
					break;
				case "ENDIF":
					{
						if (!string.IsNullOrEmpty(token2))
						{
							ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
							break;
						}
						string match = ppMatch.Count == 0 ? "" : ppMatch.Pop();
						if (match != "ENDIF" && match != "ELSEIF")
						{
							ParserMediator.Warn(trerror.UnexpectedMacroEndif.Text, position, 1);
							break;
						}
						Disabled = disabledStack.Pop();
						done = doneStack.Pop();
					}
					break;
				default:
					ParserMediator.Warn(trerror.UnrecognizedPreprosessor.Text, position, 1);
					break;
			}
			if (skip)
				Disabled = true;
		}

		internal void FileEnd(ScriptPosition? position)
		{
			if (ppMatch.Count != 0)
			{
				string match = ppMatch.Pop();
				if (match == "ELSEIF")
					match = "ENDIF";
				ParserMediator.Warn(string.Format(trerror.TheresNo.Text, match), position, 1);
			}
		}
	}

	/// <summary>
	/// 并行生成期的跨文件重名检查待办（erb-loader-parallel 03）。并行阶段 labelDic 不接受并发读，
	/// 该检查延后到合并期（<see cref="ErbLoader.MergeErbFile"/>）执行；为保证警告仍出现在
	/// 原 @ 行的行序位置，生成期先在 <see cref="ErbFileArtifact.Warnings"/> 放占位槽，
	/// 合并期替换为真实警告或删除。
	/// </summary>
	internal readonly record struct PendingOverloadCheck(int Slot, FunctionLabelLine Label, FunctionLabelLine? InFileSenior);

	/// <summary>并行生成期的跨文件 #ONLY 重複宣言检查待办（同上，占位槽机制）。</summary>
	internal readonly record struct PendingOnlyCheck(int Slot, string LabelName, ScriptPosition Position);

	/// <summary>
	/// 单文件解析产物（erb-loader-parallel Issue 01）。
	/// 生成阶段（<see cref="GenerateErbFile"/>）只写本对象：行链挂在各标签线上、警告经
	/// ParserMediator 重定向进 <see cref="Warnings"/>、控制台输出（AnalysisMode PrintC/间隔行、
	/// 开文件失败 PrintError）缓冲为 <see cref="ConsoleOps"/>——生成期间无任何 LabelDictionary
	/// 写入 / 控制台直写 / 共享计数写。合并阶段（<see cref="MergeErbFile"/>）按原文件顺序执行
	/// 全部共享状态写入与警告发射，03 并行化以本对象为执行单元。
	/// </summary>
	private sealed class ErbFileArtifact
	{
		public string Filename = null!;
		/// <summary>@ 标签（含 InvalidLabelLine），按出现序；合并阶段逐个 AddLabel / AddInvalidLabel。</summary>
		public readonly List<FunctionLabelLine> FunctionLabels = [];
		/// <summary>$ 标签（ParentLabelLine 生成期已设），按出现序；仅收录 parent 非空者。</summary>
		public readonly List<GotoLabelLine> GotoLabels = [];
		/// <summary>#ONLY 宣言名，按出现序；合并阶段 AddRange 进共享 isOnlyEvent 列表。</summary>
		public readonly List<string> OnlyDeclarations = [];
		/// <summary>逐文件缓冲的 ParserMediator 警告，合并阶段按序发射。</summary>
		public readonly List<ParserMediator.ParserWarning> Warnings = [];
		/// <summary>控制台输出回放（AnalysisMode PrintC/间隔行、开文件失败 PrintError）。</summary>
		public readonly List<Action<EmueraConsole>> ConsoleOps = [];
		/// <summary>本文件最后一次 scaningLine 写（VARI 初期值解析镜像）；null = 本文件未写。</summary>
		public LogicalLine? ScaningLine;
		/// <summary>并行生成期的跨文件重名检查待办（占位槽）；串行路径恒空。</summary>
		public readonly List<PendingOverloadCheck> PendingOverloads = [];
		/// <summary>并行生成期的跨文件 #ONLY 重複宣言检查待办（占位槽）；串行路径恒空。</summary>
		public readonly List<PendingOnlyCheck> PendingOnlys = [];
		public int EnabledLineCount;
		public bool HasError;
		/// <summary>b2-irdiskcache：本文件的函数 IR blob（按 @ 出现序），仅 IR 生成期（_irBuildEnabled）非空。</summary>
		public readonly List<IrBlob> IrBlobs = [];
	}

	/// <summary>
	/// ファイル一つを読む（生成阶段）：产出全部收进文件产物，不触碰 LabelDictionary / 控制台 / 共享计数。
	/// 串行路径下紧随 <see cref="MergeErbFile"/>；生成期对共享状态只读（labelDic 重载检查的合并序
	/// 快照、isOnlyEvent 已合并宣言、IdentifierDictionary 宏表）。
	/// </summary>
	/// <param name="deferCrossFileChecks">erb-loader-parallel 03：并行调度下为 true——跨文件重名与
	/// #ONLY 重複检查延后到合并期（占位槽保行序），生成期不读 labelDic / isOnlyEvent 共享快照。</param>
	/// <param name="replayOnFailure">串行调度为 true（异常时本方法内回放控制台输出与警告再抛，与旧路径
	/// 一致）；并行调度为 false（缓冲清理照常，回放由主线程按窗口序统一执行，避免并发控制台写）。</param>
	private ErbFileArtifact GenerateErbFile(string filepath, string filename, List<string> isOnlyEvent,
		bool deferCrossFileChecks, bool replayOnFailure = true)
	{
		var art = new ErbFileArtifact { Filename = filename };
		ParserMediator.BeginWarningBuffer(art.Warnings);
		LogicalLineParser.ScanLineMirror.Value = line => art.ScaningLine = line;
		try
		{
			GenerateErbFileCore(filepath, filename, isOnlyEvent, art, deferCrossFileChecks);
		}
		catch
		{
			// 异常路径输出与旧路径一致：已产生的控制台输出与警告照常发射（旧路径为直写/直入全局列表）
			if (replayOnFailure)
			{
				ReplayConsoleOps(art);
				ParserMediator.EmitWarningBuffer(art.Warnings);
			}
			throw;
		}
		finally
		{
			LogicalLineParser.ScanLineMirror.Value = null;
			ParserMediator.EndWarningBuffer();
		}
		return art;
	}

	private void GenerateErbFileCore(string filepath, string filename, List<string> isOnlyEvent, ErbFileArtifact art,
		bool deferCrossFileChecks)
	{
		// b2-irdiskcache：IR 命中 → 本文件由索引直建桩，免读源文件（design §5.2）——第二次启动的大头收益所在。
		// issues/17 A′：命中判定分三段（旧实现只有 (1)(3)，把 (2) 误并进 (3)）——
		//   (1) ByFile 命中且有条目 → IR 桩。真机 2363/2388 走此路，SAF 下总耗仅 38ms，本就无问题；
		//   (2) ByFile 未命中、但文件在命中轮全集内 → **该文件确实没有函数**（IR 索引只记有函数的文件），
		//       直接建空产物 return。旧实现落到 (3)：SAF 下每个白付 ~25ms 读源解析（25 个文件 ≈ 620ms），
		//       且主循环按序 await 使这 25 次阻塞串行放大到整轮 genmerge 的 100%；
		//   (3) 不在全集内（异常/陈旧路径）→ 回落源路径，保持既有安全语义。
		if (_ir is { Available: true } irHit)
		{
			if (irHit.ByFile.TryGetValue(filename, out var irEntries) && irEntries.Count > 0)
			{
				Interlocked.Increment(ref _irStubFiles); // issues/17 观测
				TryGenerateFromIr(filename, irEntries, art);
				return;
			}
			if (_irAllFiles?.Contains(filename) == true)
			{
				Interlocked.Increment(ref _irNoFuncFiles); // issues/17 取证：A′ 新省下的文件数
				return; // 空产物——IR 已证明此文件无函数，无需读源
			}
		}

		// issues/17：走到此处即回落读源。IR 可用时本不该到达（判据漏了 → fallbackOnIrHit 非 0 可即时
		// 发现）；IR 不可用（冷枚举轮）则是常态——SAF 下逐文件走跨进程读，正是修复前 620ms 的来源。
		Interlocked.Increment(ref _sourceFallbackFiles);
		if (_ir is { Available: true })
			Interlocked.Increment(ref _sourceFallbackOnIrHit);

		using var eReader = new EraStreamReader(Config.Config.UseRenameFile && ParserMediator.RenameDic != null);

		if (!eReader.OpenOnCache(filepath, filename))
		{
			art.ConsoleOps.Add(c => c.PrintError(string.Format(trerror.FailedOpenFile.Text, eReader.Filename)));
			return; // ADR-0019：文件打不开直接跳过，避免空 eReader 进入解析循环
		}
		var ppstate = new PPState(env, idDic);
		// 并行调度：跨文件 #ONLY 重複检查延后（共享 isOnlyEvent 是同窗口其他文件未合并的不完整快照），
		// 传独立空视图禁用即时检查；串行调度传共享快照即时检查（01 行为）。
		List<string> mergedOnlyView = deferCrossFileChecks ? [] : isOnlyEvent;
		LogicalLine nextLine = new NullLine();
		LogicalLine lastLine = new NullLine();
		FunctionLabelLine lastLabelLine = null!;
		CharStream st = null!;
		ScriptPosition? position = null;
		int funcCount = 0;
		// 生成期重名判定的文件内视图：跨文件同名经 labelDic.GetEarliestLabelWithName 按合并序读
		var firstValidLabelByName = new Dictionary<string, FunctionLabelLine>(Config.Config.StrComper);
		var firstGotoByParent = new Dictionary<FunctionLabelLine, Dictionary<string, GotoLabelLine>>();
		// lazy-function-load（B 线 B1）：惰性模式 = 开 LazyFunctionLoad 且非 AnalysisMode（AnalysisMode 强制 eager）。
		// 惰性下函数体行链不解析——labelDic 只注册全量标签桩（IsStub），仅 # 签名行处理（保 Argument/Def/IsMethod）。
		bool lazy = Config.Config.LazyFunctionLoad && !env.AnalysisMode;
		// b2-irdiskcache：收集本文件函数 IR blob（仅 IR 生成期，即增量写器非空）。
		bool collectIr = _irWriter != null;
		FunctionLabelLine? irCurLabel = null;
		List<IrLine>? irCurLines = null;
		// 结束当前函数的 blob（@ 边界 / EOF 时调用）。
		void FinishIrBlob()
		{
			if (!collectIr || irCurLabel == null)
			{
				irCurLabel = null;
				irCurLines = null;
				return;
			}
			if (irCurLabel is InvalidLabelLine bad)
				art.IrBlobs.Add(IrBlob.InvalidFunc(bad.LabelName, bad.Position!.Value.LineNo, bad.ErrMes));
			else
				art.IrBlobs.Add(IrBlob.Valid(irCurLabel.LabelName, irCurLabel.Position!.Value.LineNo, irCurLines!));
			irCurLabel = null;
			irCurLines = null;
		}
		if (env.AnalysisMode)
			art.ConsoleOps.Add(c => c.PrintSystemLine(" "));
		while ((st = eReader.ReadEnabledLine(ppstate.Disabled)) != null)
		{
			position = new ScriptPosition(eReader.Filename, eReader.LineNo);
			//rename処理をEraStreamReaderに移管
			//変換できなかった[[～～]]についてはLexAnalyzerがエラーを投げる
			if (st.Current == '[' && st.Next != '[')
			{
				st.ShiftNext();
				string token = LexicalAnalyzer.ReadSingleIdentifier(st);
				LexicalAnalyzer.SkipWhiteSpace(st);
				string token2 = LexicalAnalyzer.ReadSingleIdentifier(st);
				if (string.IsNullOrEmpty(token) || st.Current != ']')
					ParserMediator.Warn(trerror.InvalidSBrackets.Text, position, 1);
				ppstate.AddKeyWord(token, token2, position);
				st.ShiftNext();
				if (!st.EOS)
					ParserMediator.Warn(string.Format(trerror.IgnoreAfterPreprosessor.Text, token), position, 1);
				continue;
			}
			//if ((skip) || (env.DebugMode && ifndebug) || (!env.DebugMode && ifdebug))
			//	continue;
			if (ppstate.Disabled)
				continue;
			//ここまでプリプロセッサ

			if (st.Current == '#')
			{
				if (lastLine == null || lastLine is not FunctionLabelLine funcLine)
				{
					ParserMediator.Warn(trerror.InvalidSharp.Text, position, 1);
					continue;
				}
				// 占位锚：本 # 行首个警告的槽位（跨文件 ONLY 检查占位须插到这里——
				// 串行路径该警告在 ParseSharpLine 的 Contains 检查点发射，先于同行的 BeIgnore* 警告）
				int lineWarnStart = art.Warnings.Count;
				int onlyCountBefore = art.OnlyDeclarations.Count;
				// b2-irdiskcache：收集 # 签名行文本（物化时重跑 ParseSharpLine 重建签名）。
				if (collectIr && irCurLabel != null)
					irCurLines?.Add(new IrLine(position!.Value.LineNo, st.RowString));
				if (!LogicalLineParser.ParseSharpLine(funcLine, st, position, mergedOnlyView, art.OnlyDeclarations))
					art.HasError = true;
				if (deferCrossFileChecks && art.OnlyDeclarations.Count > onlyCountBefore)
				{
					int insertAt = lineWarnStart;
					for (int j = onlyCountBefore; j < art.OnlyDeclarations.Count; j++)
					{
						art.Warnings.Insert(insertAt, PlaceholderWarning());
						art.PendingOnlys.Add(new PendingOnlyCheck(insertAt, art.OnlyDeclarations[j], position!.Value));
						insertAt++;
					}
				}
				continue;
			}
			if (st.Current == '$' || st.Current == '@')
			{
				bool isFunction = st.Current == '@';
				if (!isFunction && lazy)
				{
					// b2-irdiskcache：$ goto 属函数体，物化时随 ReparseFunction 重建——blob 收集进本函数行集。
					if (collectIr && irCurLabel != null)
						irCurLines?.Add(new IrLine(eReader.LineNo, st.RowString));
					continue; // 惰性：$ goto 属函数体，物化时随 ReparseFunction 重建，此处跳过
				}
				nextLine = LogicalLineParser.ParseLabelLine(st, position, output, art.ConsoleOps);
				if (isFunction)
				{
					var label = nextLine as FunctionLabelLine;
					lastLabelLine = label!;
					// b2-irdiskcache：@ 边界——结束上一函数 blob、开启本函数 blob（@ 行文本作首行，物化重建 wc）。
					FinishIrBlob();
					if (collectIr)
					{
						irCurLabel = label;
						irCurLines = new() { new IrLine(eReader.LineNo, st.RowString) };
					}
					// 惰性：注册标签桩——保签名头、函数体行链留空；函数体在首调用/主循环时物化。
					if (lazy)
						label!.IsStub = true;
					art.FunctionLabels.Add(label!);
					if (label is InvalidLabelLine)
					{
						art.HasError = true;

						ParserMediator.Warn(nextLine.ErrMes, position, 2);
					}
					else// if (label is FunctionLabelLine)
					{
						var inFileSenior = firstValidLabelByName.GetValueOrDefault(label!.LabelName);
						if (!label!.IsEvent && (Config.Config.WarnNormalFunctionOverloading || env.AnalysisMode))
						{
							if (deferCrossFileChecks)
							{
								// 并行调度：跨文件 senior 合并期查 labelDic（此处不可并发读）；
								// 文件内首现表作 InFileSenior 一并延后——合并期 senior 判定序
								// （跨文件最早 ?? 文件内首现）与串行即时检查一致，警告仍在 @ 行行序位置
								art.PendingOverloads.Add(new PendingOverloadCheck(art.Warnings.Count, label, inFileSenior));
								art.Warnings.Add(PlaceholderWarning());
							}
							else
							{
								// 重载检查（生成期合并序只读视图 + 文件内首现表，与旧“先清同文件旧标签、
								// AddLabel 后 GetSameNameLabel 取最早同名”语义一致：
								// excludingFilename = 本文件——重载场景旧标签要到合并阶段才被清掉，须排除）
								FunctionLabelLine? seniorLabel = labelDic.GetEarliestLabelWithName(label.LabelName, filename);
								seniorLabel ??= inFileSenior;
								if (seniorLabel != null)
								{
									//output.NewLine();
									ParserMediator.Warn(string.Format(trerror.FuncIsAlreadyDefined.Text, label.LabelName, seniorLabel.Position!.Value.Filename, seniorLabel.Position!.Value.LineNo.ToString()), position, 1);
									funcCount = -1;
								}
							}
						}
						funcCount++;
						firstValidLabelByName.TryAdd(label.LabelName, label);
						if (env.AnalysisMode && Config.Config.PrintCPerLine > 0 && funcCount % Config.Config.PrintCPerLine == 0)
						{
							art.ConsoleOps.Add(c => c.NewLine());
							art.ConsoleOps.Add(c => c.PrintSystemLine(" "));
						}
					}
				}
				else
				{
					if (nextLine is GotoLabelLine gotoLabel)
					{
						gotoLabel.ParentLabelLine = lastLabelLine!;
						if (lastLabelLine != null)
						{
							art.GotoLabels.Add(gotoLabel);
							// 同関数内 $ 重複判定（AddLabelDollar 的 TryAdd 语义：同 parent+名保留首个）
							if (!firstGotoByParent.TryGetValue(lastLabelLine, out var byName))
							{
								byName = new Dictionary<string, GotoLabelLine>(Config.Config.StrComper);
								firstGotoByParent.Add(lastLabelLine, byName);
							}
							if (byName.TryGetValue(gotoLabel.LabelName, out GotoLabelLine? firstGoto))
							{
								ScriptPosition pos = firstGoto.Position!.Value;
								ParserMediator.Warn(string.Format(trerror.LabelIsAlreadyDefined.Text, gotoLabel.LabelName, pos.Filename, pos.LineNo.ToString()), position, 2);
							}
							else
								byName.Add(gotoLabel.LabelName, gotoLabel);
						}
					}
				}
				if (nextLine is InvalidLine)
				{
					art.HasError = true;
					ParserMediator.Warn(nextLine.ErrMes, position, 2);
				}
			}
			else
			{
				// b2-irdiskcache：函数体指令行文本收集（blob 物化输入）。
				if (collectIr && irCurLabel != null)
					irCurLines?.Add(new IrLine(eReader.LineNo, st.RowString));
				// 惰性：函数体指令行不解析（物化时随 ReparseFunction 重建），直接跳过
				if (lazy)
					continue;
				//1808alpha006 処理位置変更
				////全置換はここで対応
				////1756beta1+++ 最初に全置換してしまうと関数定義を_Renameでとか論外なことができてしまうので永久封印した
				//if (ParserMediator.RenameDic != null && st.CurrentEqualTo("[[") && (rowLine.TrimEnd().IndexOf("]]") == rowLine.TrimEnd().Length - 2))
				//{
				//    string replacedLine = st.Substring();
				//    foreach (KeyValuePair<string, string> pair in ParserMediator.RenameDic)
				//        replacedLine = replacedLine.Replace(pair.Key, pair.Value);
				//    st = new StringStream(replacedLine);
				//}
				if (lastLabelLine == null)
					ParserMediator.Warn(trerror.LineBeforeFunc.Text, position, 1);
				nextLine = LogicalLineParser.ParseLine(st, position, output, lastLabelLine!);


				if (nextLine == null)
					continue;
				if (nextLine is InvalidLine)
				{
					art.HasError = true;
					ParserMediator.Warn(nextLine.ErrMes, position, 2);
				}
				else if (JSONConfig.Data.UseNewRandom &&
										nextLine is InstructionLine instruction)
				{
					switch (instruction.FunctionCode)
					{
						case FunctionCode.RANDOMIZE:
							ParserMediator.Warn(trerror.IgnoreRandomize.Text, position, 0);
							break;
						case FunctionCode.DUMPRAND:
							ParserMediator.Warn(trerror.CanNotUseDumprand.Text, position, 0);
							break;
						case FunctionCode.INITRAND:
							ParserMediator.Warn(trerror.CanNotUseInitrand.Text, position, 0);
							break;
						default:
							break;
					}
				}
			}
			nextLine.ParentLabelLine = lastLabelLine!;

			lastLine = addLine(art, nextLine, lastLine!);
		}
		addLine(art, new NullLine(), lastLine!);
		FinishIrBlob(); // b2-irdiskcache：EOF 收尾最后函数 blob
		position = new ScriptPosition(eReader.Filename, -1);
		ppstate.FileEnd(position);
		return;
	}

	/// <summary>
	/// b2-irdiskcache：IR 命中路径的最小桩直建（design §5.2，实现时对账项 B）。免读源文件、免全文件词法扫描。
	/// 桩只带 LabelName + Position + 最小签名（IsEvent/IsSystem/Depth 由标签名经 IdentifierDictionary 判同节点，
	/// 与 ParseLabelLine 一致）；Arg/Def/私有变量/IsMethod/LocalLength 留空 → setLabelsArg 跳过、物化时从 blob
	/// 重建（@ 行重建 wc → # 行重跑 ParseSharpLine → parseLabel → resizeLocalVars）。警告由物化期补发，生成期跳过
	/// 源 ParsSharpLine/重载检查（指纹保证 IR 与源的等价性不变，改动源 → 指纹变 → 全量重建 → 警告照常）。
	/// </summary>
	private static void TryGenerateFromIr(string filename, List<IrIndexEntry> entries, ErbFileArtifact art)
	{
		foreach (var e in entries)
		{
			var pos = new ScriptPosition(e.File, e.LineNo);
			if (e.Invalid)
			{
				art.FunctionLabels.Add(new InvalidLabelLine(pos, e.Label, e.ErrMes ?? ""));
				art.HasError = true;
				continue;
			}
			var label = new FunctionLabelLine(pos, e.Label, null!);
			label.IsStub = true;
			label.IrBacked = true;
			if (IdentifierDictionary.IsEventLabelName(e.Label))
			{
				label.IsEvent = true;
				label.IsSystem = true;
				label.Depth = 0;
			}
			else if (IdentifierDictionary.IsSystemLabelName(e.Label))
			{
				label.IsSystem = true;
				label.Depth = 0;
			}
			art.FunctionLabels.Add(label);
		}
	}

	/// <summary>
	/// 合并阶段（erb-loader-parallel Issue 01）：按原文件顺序把文件产物并入共享状态——
	/// 缓冲警告按序发射、全部 LabelDictionary 写入（IfFileLoadClearLabelWithPath / AddLabel /
	/// AddInvalidLabel / AddLabelDollar）、#ONLY 宣言入共享列表、控制台输出回放、计数聚合。
	/// Index / FileIndex 分配序与警告顺序由“逐文件生成后立即按序合并”保证与旧串行路径一致。
	/// 03：并行产物先经 <see cref="ResolvePendingCrossFileChecks"/> 补齐跨文件检查（占位槽替换）。
	/// </summary>
	private void MergeErbFile(ErbFileArtifact art, List<string> isOnlyEvent)
	{
		ResolvePendingCrossFileChecks(art, isOnlyEvent);
		// 警告最先入列：等价于旧路径“解析期即入全局列表”（共享写入若异常也不丢已产出警告）
		ParserMediator.EmitWarningBuffer(art.Warnings);
		labelDic.IfFileLoadClearLabelWithPath(art.Filename);
		ReplayConsoleOps(art);
		var labels = art.FunctionLabels;
		for (int i = 0; i < labels.Count; i++)
		{
			if (labels[i] is InvalidLabelLine)
				labelDic.AddInvalidLabel(labels[i]);
			else
				labelDic.AddLabel(labels[i]);
		}
		var gotoLabels = art.GotoLabels;
		for (int i = 0; i < gotoLabels.Count; i++)
			labelDic.AddLabelDollar(gotoLabels[i]);
		if (art.OnlyDeclarations.Count > 0)
			isOnlyEvent.AddRange(art.OnlyDeclarations);
		if (art.ScaningLine != null)
			setScanLine(art.ScaningLine);
		enabledLineCount += art.EnabledLineCount;
		if (art.HasError)
			noError = false;
		// b2-irdiskcache：逐文件批量落盘到 IR（本文件 image 已并入共享状态，blob 写完即清空释放——内存 O(单文件)）。
		if (_irWriter != null && art.IrBlobs.Count > 0)
		{
			_irWriter.AppendFile(art.Filename, art.IrBlobs);
			art.IrBlobs.Clear();
		}
	}

	/// <summary>并行产物的跨文件检查占位（合并期由 <see cref="ResolvePendingCrossFileChecks"/> 替换或删除）。</summary>
	private static ParserMediator.ParserWarning PlaceholderWarning() => new("", null, 1, null!);

	/// <summary>
	/// erb-loader-parallel 03：合并期补检——并行生成期延后的跨文件重名（FuncIsAlreadyDefined）与
	/// #ONLY 重複宣言（AlreadyDeclaredOnly）检查。此时 labelDic / isOnlyEvent 恰含“更早合并文件”
	/// 的全部内容（本文件的 IfFileLoadClearLabelWithPath 尚未执行、宣言尚未 AddRange），与串行路径
	/// 生成期快照的可见性一致，判定结果与警告序（占位槽位置）均与串行一致。串行产物无待办，no-op。
	/// </summary>
	private void ResolvePendingCrossFileChecks(ErbFileArtifact art, List<string> isOnlyEvent)
	{
		if (art.PendingOverloads.Count == 0 && art.PendingOnlys.Count == 0)
			return;
		List<int> deadSlots = [];
		foreach (var check in art.PendingOverloads)
		{
			// senior 判定序与串行即时检查一致：跨文件最早（排除本文件名，重载场景旧标签
			// 由本文件稍后的 IfFileLoadClearLabelWithPath 清理）?? 文件内首现
			FunctionLabelLine? seniorLabel = labelDic.GetEarliestLabelWithName(check.Label.LabelName, art.Filename);
			seniorLabel ??= check.InFileSenior;
			if (seniorLabel != null)
			{
				art.Warnings[check.Slot] = new ParserMediator.ParserWarning(
					string.Format(trerror.FuncIsAlreadyDefined.Text, check.Label.LabelName,
						seniorLabel.Position!.Value.Filename, seniorLabel.Position!.Value.LineNo.ToString()),
					check.Label.Position, 1, null!);
			}
			else
				deadSlots.Add(check.Slot);
		}
		foreach (var check in art.PendingOnlys)
		{
			if (isOnlyEvent.Contains(check.LabelName))
			{
				art.Warnings[check.Slot] = new ParserMediator.ParserWarning(
					string.Format(trerror.AlreadyDeclaredOnly.Text, check.LabelName), check.Position, 1, null!);
			}
			else
				deadSlots.Add(check.Slot);
		}
		deadSlots.Sort();
		for (int i = deadSlots.Count - 1; i >= 0; i--)
			art.Warnings.RemoveAt(deadSlots[i]);
	}

	private void ReplayConsoleOps(ErbFileArtifact art)
	{
		var ops = art.ConsoleOps;
		for (int i = 0; i < ops.Count; i++)
			ops[i](output);
		ops.Clear();
	}

	/// <summary>displayReport 逐文件进度行（原两处内联打印收敛；DEBUG 构建带累计耗时）。</summary>
	private void PrintLoadingProgress(bool displayReport, DateTime starttime, string filename)
	{
		if (!displayReport)
			return;
#if DEBUG
		output.PrintSystemLine(string.Format(trsl.ElapsedTimeLoad.Text, (DateTime.Now - starttime).TotalMilliseconds, filename));
#else
		output.PrintSystemLine(string.Format(trsl.LoadingFile.Text, filename));
#endif
	}

	/// <summary>
	/// 串行调度（erb-loader-parallel 03 fallback）：逐文件“生成 → 按序合并”，与 01 完成时的行为
	/// 完全一致——进度行在生成前打印、跨文件检查生成期即时、异常即回放再抛。
	/// </summary>
	private async Task LoadPendingSerial(List<(string Filename, string File)> pending, List<string> isOnlyEvent,
		bool displayReport, DateTime starttime)
	{
		foreach (var (filename, file) in pending)
		{
			PrintLoadingProgress(displayReport, starttime, filename);
			var art = await Task.Run(() => GenerateErbFile(file, filename, isOnlyEvent, deferCrossFileChecks: false));
			MergeErbFile(art, isOnlyEvent);
		}
	}

	#region erb-bounded-lifecycle：有界滑动窗口调度（handoff §3）

	#region 测试接缝（handoff §8.1：夹具可注入失败点/延迟/observer；生产路径恒 null，零开销）
	/// <summary>worker 开始生成前按文件名 await 的 gate（窗口/收束测试用）。hook 收到 worker 的取消令牌。</summary>
	internal Func<string, CancellationToken, Task>? TestGenerateGateHook;
	/// <summary>生成完成后按文件名决定注入的异常（返回 null 不注入）；artifact 保留以复现「有部分产物 + 异常」的按序回放路径。</summary>
	internal Func<string, Exception?>? TestGenerateFaultHook;
	/// <summary>worker 生命周期 observer：(phase, filename)，phase ∈ start/end/cancel；窗口上界与尾任务收束断言用。</summary>
	internal Action<string, string>? TestWorkerObserver;
	/// <summary>窗口宽度覆盖（null = Min(ProcessorCount, MaxParallelErbFiles)）。</summary>
	internal int? TestWindowOverride;
	#endregion

	/// <summary>单个文件的生成结果：worker 永不向调用方抛异常，一切结果（含取消）显式承载。
	/// Exception 非空且 Artifact 同非空 = 生成中途抛出但已有部分产物（主循环按序回放一次后抛原异常）。</summary>
	private sealed record GenerationResult(int Index, string Filename, ErbFileArtifact? Artifact, Exception? Exception, bool Canceled);

	/// <summary>
	/// 并行调度（erb-bounded-lifecycle，重写自 erb-loader-parallel 03 的全量 Task 数组版）：
	/// 按索引推进的有界滑动窗口——运行中 worker 数与「已生成未合并产物数」共用同一上限
	/// <see cref="MaxParallelErbFiles"/>（窗口宽度 = clamp(TestWindowOverride ?? Min(ProcessorCount, MaxParallelErbFiles), 1, pending.Count)），
	/// 不再为全部 pending 创建 Task/结果数组；合并一个文件后才放行下一个，artifact 驻留量有显式上界
	/// （旧实现首文件慢时未合并产物可驻留接近整个游戏——Android NativeAOT 上表现为 OOM 闪退/长 GC 卡死）。
	/// <para>
	/// 生命周期不变式：按序处理到某索引发现异常时，先回放该文件已有的控制台输出/警告一次，
	/// 再保留原异常类型与堆栈抛出；linked CTS 立即取消未启动与运行中 worker；finally 必须 await
	/// 全部已启动任务收束后才离开——旧实现 using SemaphoreSlim 在尾任务仍运行时 Dispose，
	/// 尾任务在已释放 gate 上 Release/WaitAsync 抛出无人观察的异常。取消产生的次生
	/// OperationCanceledException 只记录，不覆盖原始加载异常。
	/// </para>
	/// 生成期共享写仍集中在 <see cref="MergeErbFile"/>（主 async 流按序执行）；worker 经 per-task
	/// AsyncLocal（警告缓冲/ScanLineMirror）与 deferCrossFileChecks 空视图做到无共享写。进度行随合并按序补发。
	/// </summary>
	// issues/17 genmerge 逐文件分账（「IR 桩 / 无函数 / 回落读源」）。并行 worker 写入，故用 Interlocked。
	// 保留为常驻观测而非一次性探针：它是 A′ 的体检指标——fallbackSrc 若回升到与文件数同量级，
	// 说明"在全集内却无 IR 条目"的判据失效（如全集构建异常），无需重跑取证即可发现。
	// 取证期的临时探针（三段 Stopwatch 计时 + 落源文件名样本）已随 issue 17 结论落定拆除。
	private int _irStubFiles;
	/// <summary>判为"在命中轮全集内、但无 IR 条目"（= 确认该文件无函数）而免读源的文件数（issues/17 A′ 的收益口径）。</summary>
	private int _irNoFuncFiles;
	private int _sourceFallbackFiles;
	/// <summary>其中"IR 已 Available 却仍回落"的条数——应恒为 0。</summary>
	private int _sourceFallbackOnIrHit;

	private async Task LoadPendingParallel(List<(string Filename, string File)> pending, List<string> isOnlyEvent,
		bool displayReport, DateTime starttime, CancellationToken externalCt)
	{
		_irStubFiles = 0;
		_irNoFuncFiles = 0;
		_sourceFallbackFiles = 0;
		_sourceFallbackOnIrHit = 0;
		if (pending.Count == 0)
			return;
		int window = Math.Clamp(TestWindowOverride ?? ResolveMaxParallelErbFiles(Config.Config.OverrideMaxParallelErbFiles, Environment.ProcessorCount), 1, pending.Count);
		// 06-K1 自证：knob= 壳层请求词表值，window= 生效值（auto=与无旋钮逐位一致）——取证以此对账
		EmueraLog.Info("ErbLoader", $"LoadPendingParallel: files={pending.Count}, window={window} knob={MaxParallelErbFilesCodec.ToConfigString(Config.Config.OverrideMaxParallelErbFiles)}");
		// 外部取消（预留 reload/会话退出）+ 内部取消（本轮首错收束）链接；worker 只见合成令牌
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
		// 运行表只持有窗口内任务（旧实现：tasks[pending.Count] 全量数组长期持有全部任务引用）
		var running = new Dictionary<int, Task<GenerationResult>>(window);
		int nextToStart = 0;
		int merged = 0;
		int peakActive = 0;
		int canceledCount = 0;
		var phaseSw = Stopwatch.StartNew();
		var gcLast = GcSnapshot.Take();

		try
		{
			while (merged < pending.Count)
			{
				// 填窗：合并掉一个才放行下一个——active worker 与未合并产物共用同一上限
				while (nextToStart < pending.Count && running.Count < window)
				{
					int idx = nextToStart++; // 闭包陷阱：捕获本迭代独立副本
					running[idx] = Task.Run(() => RunGenerateWorkerAsync(idx, pending[idx].Filename, pending[idx].File, cts.Token));
				}
				peakActive = Math.Max(peakActive, running.Count);

				var result = await running[merged].ConfigureAwait(false);
				running.Remove(merged);
				merged++;
				if (result.Exception != null)
				{
					// 首错按序处理：回放该文件已有的控制台输出/警告一次，保留原异常类型与堆栈抛出
					EmueraLog.Error("ErbLoader",
						$"LoadPendingParallel: fail idx={result.Index} file={result.Filename} stage={(result.Canceled ? "cancel" : "generate")} ex={result.Exception.GetType().Name}");
					if (result.Artifact != null)
					{
						ReplayConsoleOps(result.Artifact);
						ParserMediator.EmitWarningBuffer(result.Artifact.Warnings);
					}
					throw result.Exception;
				}
				PrintLoadingProgress(displayReport, starttime, result.Filename);
				MergeErbFile(result.Artifact!, isOnlyEvent);
				// 判别日志（handoff §6.3）：每 32 文件输出窗口占用 + GC 增量，卡住阶段可由 logcat 直接判别
				if ((merged & 0x1F) == 0)
				{
					EmueraLog.Info("ErbLoader", $"LoadPendingParallel: merged={merged}/{pending.Count} active={running.Count} window={window} ms={phaseSw.ElapsedMilliseconds} {GcSnapshot.Diff(gcLast)}");
					gcLast = GcSnapshot.Take();
				}
			}
		}
		finally
		{
			// 收束：无论正常完成/首错/取消——不再启动新 worker，但已启动的必须全部等到结束才离开本方法。
			// 此前旧实现离开即 Dispose 信号量，尾任务在已释放 gate 上炸出无人观察的异常。
			cts.Cancel();
			foreach (var kv in running)
			{
				try
				{
					var tail = await kv.Value.ConfigureAwait(false);
					if (tail.Canceled)
						Interlocked.Increment(ref canceledCount);
				}
				catch (Exception ex)
				{
					// 次生异常（含取消 OCE）：只记录，不覆盖正在传播的首错
					EmueraLog.Error("ErbLoader", $"LoadPendingParallel: tail idx={kv.Key} ended with {ex.GetType().Name}: {ex.Message}");
				}
			}
			EmueraLog.Info("ErbLoader",
				$"LoadPendingParallel: window={window} started={nextToStart} merged={merged} peakActive={peakActive} canceled={Volatile.Read(ref canceledCount)} ms={phaseSw.ElapsedMilliseconds}");
			// issues/17 取证：本轮 genmerge 的「IR 桩 / 回落读源」分账。
			// H1 判据：irAvailable=True 时 fallbackOnIrHit 应恒为 0；非 0 即"IR 命中轮白读全库源文件"。
			// issues/17 观测行（A′ 体检）：命中轮 fallbackSrc 应为 0、irNoFunc 应为"0 函数文件数"；
			// fallbackOnIrHit 恒 0 否则判据有漏。冷枚举轮（IR 不可用）fallbackSrc=n、irStub=0 属常态。
			EmueraLog.Info("ErbLoader",
				$"LoadPendingParallel(issues/17): irStub={Volatile.Read(ref _irStubFiles)} irNoFunc={Volatile.Read(ref _irNoFuncFiles)} fallbackSrc={Volatile.Read(ref _sourceFallbackFiles)} fallbackOnIrHit={Volatile.Read(ref _sourceFallbackOnIrHit)}");
		}
	}

	/// <summary>worker 主体：gate hook → 取消检查 → 生成 → fault 注入点。永不抛出——一切结果进 <see cref="GenerationResult"/>；
	/// 测试观察点 start/end/cancel 经 <see cref="TestWorkerObserver"/> 上报（含失败与取消路径，收束断言依据）。</summary>
	private async Task<GenerationResult> RunGenerateWorkerAsync(int idx, string filename, string filepath, CancellationToken ct)
	{
		TestWorkerObserver?.Invoke("start", filename);
		try
		{
			if (TestGenerateGateHook != null)
				await TestGenerateGateHook(filename, ct).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			var art = GenerateErbFile(filepath, filename, [], deferCrossFileChecks: true, replayOnFailure: false);
			// fault 注入点在生成完成后：artifact 保留 + 异常显式承载，复现「部分产物 + 异常」的按序回放路径
			var fault = TestGenerateFaultHook?.Invoke(filename);
			return fault != null
				? new GenerationResult(idx, filename, art, fault, Canceled: false)
				: new GenerationResult(idx, filename, art, null, false);
		}
		catch (OperationCanceledException)
		{
			TestWorkerObserver?.Invoke("cancel", filename);
			return new GenerationResult(idx, filename, null, new OperationCanceledException(), true);
		}
		catch (Exception ex)
		{
			// GenerateErbFile(replayOnFailure:false) 已清缓冲并抑制回放；worker 线程不写控制台/全局警告
			return new GenerationResult(idx, filename, null, ex, false);
		}
		finally
		{
			TestWorkerObserver?.Invoke("end", filename);
		}
	}

	#endregion

	private static LogicalLine addLine(ErbFileArtifact art, LogicalLine nextLine, LogicalLine lastLine)
	{
		if (nextLine == null)
			return null!;
		art.EnabledLineCount++;
		lastLine.NextLine = nextLine;
		return nextLine;
	}

	private void setLabelsArg()
	{
		List<FunctionLabelLine> labelList = labelDic.GetAllLabels(false);
		foreach (FunctionLabelLine label in labelList)
		{
			// b2-irdiskcache：IR 命中桩无 @ wc（最小桩），parseLabel 需 PopRowArgs 会 NPE——Arg/Def 延后到物化期
			// 从 blob 重建（MaterializeLabel → ReparseFunctionFromIr）。SortLabels 照常跑（含 resizeLocalVars，
			// 物化后按实际 ArgLength 扩容）。
			if (label.IrBacked)
				continue;
			try
			{
				if (label.Arg != null)
					continue;
				setScanLine(label);
				parseLabel(label);
			}
			catch (Exception exc)
			{
	#if !HEADLESS
			System.Media.SystemSounds.Hand.Play();
#endif
				string errmes = exc.Message;
				if (!(exc is EmueraException))
					errmes = exc.GetType().ToString() + ":" + errmes;
				ParserMediator.Warn(string.Format(trerror.FuncArgError.Text, label.LabelName, errmes), label, 2, true, false);
				label.ErrMes = trerror.CalledFailedFunc.Text;
				label.IsError = true;
			}
			finally
			{
				setScanLine(null!);
			}
		}
		labelDic.SortLabels();
	}

	private void parseLabel(FunctionLabelLine label)
	{
		WordCollection wc = label.PopRowArgs();
		string errMes;
		SingleTerm[] subNames;
		VariableTerm[] args = [];
		SingleTerm[] defs = [];
		int maxArg = -1;
		int maxArgs = -1;
		//1807 非イベント関数のシステム関数については警告レベル低下＆エラー解除＆引数を設定するように。
		if (label.IsEvent)
		{
			if (!wc.EOL)
				ParserMediator.Warn(string.Format(trerror.EventFuncHasArg.Text, label.LabelName), label, 2, true, false);
			//label.SubNames = subNames;
			label.Arg = args;
			label.Def = defs;
			label.ArgLength = -1;
			label.ArgsLength = -1;
			return;
		}

		if (!wc.EOL)
		{
			if (label.IsSystem)
				ParserMediator.Warn(string.Format(trerror.SystemFuncHasArg.Text, label.LabelName), label, 2, true, false);
			SymbolWord symbol = (wc.Current as SymbolWord)!;
			wc.ShiftNext();
			if (symbol == null)
			{ errMes = trerror.WrongArgFormat.Text; goto err; }
			if (symbol.Type == '[')//TODO:subNames 結局実装しないかも
			{
				var subNamesRow = ExpressionParser.ReduceArguments(wc, ArgsEndWith.RightBracket, false);
				if (subNamesRow.Count == 0)
				{ errMes = trerror.CanNotEmptyFuncSBrackets.Text; goto err; }
				subNames = new SingleTerm[subNamesRow.Count];
				for (int i = 0; i < subNamesRow.Count; i++)
				{
					if (subNamesRow[i] == null)
					{ errMes = trerror.CanNotOmitFuncDefineArg.Text; goto err; }
					AExpression term = subNamesRow[i].Restructure(exm);
					subNames[i] = (term as SingleTerm)!;
					if (subNames[i] == null)
					{ errMes = trerror.FuncDefineArgOnlyConst.Text; goto err; }
				}
				symbol = (wc.Current as SymbolWord)!;
				if (!wc.EOL && symbol == null)
				{ errMes = trerror.WrongArgFormat.Text; goto err; }
				wc.ShiftNext();
			}
			if (!wc.EOL)
			{
				List<AExpression> argsRow;
				if (symbol!.Type == ',')
					argsRow = ExpressionParser.ReduceArguments(wc, ArgsEndWith.EoL, true);
				else if (symbol.Type == '(')
					argsRow = ExpressionParser.ReduceArguments(wc, ArgsEndWith.RightParenthesis, true);
				else
				{ errMes = trerror.WrongArgFormat.Text; goto err; }
				int length = argsRow.Count / 2;
				args = new VariableTerm[length];
				defs = new SingleTerm[length];
				for (int i = 0; i < length; i++)
				{
					SingleTerm def = null!;
					AExpression term = argsRow[i * 2];
					//引数読み取り時点で判別されないといけない
					//if (term == null)
					//{ errMes = "関数定義の引数は省略できません"; goto err; }
					if (!(term.Restructure(exm) is VariableTerm vTerm) || vTerm.Identifier.IsConst)
					{ errMes = trerror.ArgCanOnlyAssignableVar.Text; goto err; }
					else if (!vTerm.Identifier.IsReference)//参照型なら添え字不要
					{
						if (vTerm is VariableNoArgTerm)
						{ errMes = string.Format(trerror.ArgHasNotSubscript.Text, vTerm.Identifier.Name); goto err; }
						if (!vTerm.isAllConst)
						{ errMes = trerror.ArgSubscriptOnlyConst.Text; goto err; }
					}
					for (int j = 0; j < i; j++)
					{
						if (vTerm.checkSameTerm(args[j]))
							ParserMediator.Warn(string.Format(trerror.DuplicateArg.Text, i + 1, vTerm.GetFullString(), j + 1), label, 1, false, false);
					}
					if (vTerm.Identifier.Code == VariableCode.ARG)
					{
						if (maxArg < vTerm.getEl1forArg + 1)
							maxArg = vTerm.getEl1forArg + 1;
					}
					else if (vTerm.Identifier.Code == VariableCode.ARGS)
					{
						if (maxArgs < vTerm.getEl1forArg + 1)
							maxArgs = vTerm.getEl1forArg + 1;
					}
					bool canDef = vTerm.Identifier.Code == VariableCode.ARG || vTerm.Identifier.Code == VariableCode.ARGS || vTerm.Identifier.IsPrivate;
					term = argsRow[i * 2 + 1];
					if (term is NullTerm)
					{
						if (canDef)// && label.ArgOptional)
						{
							if (vTerm.GetOperandType() == typeof(long))
								def = new SingleLongTerm(0);
							else
								def = new SingleStrTerm("");
						}
					}
					else
					{
						def = (term.Restructure(exm) as SingleTerm)!;
						if (def == null)
						{ errMes = trerror.ArgCanOnlyConst.Text; goto err; }
						if (!canDef)
						{ errMes = trerror.ArgCanOnlyPrivVar.Text; goto err; }
						else if (vTerm.Identifier.IsReference)
						{ errMes = trerror.RefArgCanNotInitialize.Text; goto err; }
						if (vTerm.GetOperandType() != def.GetOperandType())
						{ errMes = trerror.NotMatchTypeArgAndInitialValue.Text; goto err; }
					}
					args[i] = vTerm;
					defs[i] = def!;
				}

			}
		}
		if (!wc.EOL)
		{ errMes = trerror.WrongArgFormat.Text; goto err; }

		//label.SubNames = subNames;
		label.Arg = args;
		label.Def = defs;
		label.ArgLength = maxArg;
		label.ArgsLength = maxArgs;
		return;
	err:
		ParserMediator.Warn(string.Format(trerror.FuncArgError.Text, label.LabelName, errMes), label, 2, true, false);
		return;
	}


	public bool useCallForm;

	// ===== erb-loader-parallel 08：ParseScript 未调用遍历并行化（spec-phase2 D4/D5，ADR-0024）=====

	/// <summary>
	/// D5#6：未调用遍并行期间的 per-线程当前解析行（写点见 <see cref="ScanLine"/>）。
	/// ThreadLocal 而非 AsyncLocal：写点 per-line 高频（eraTW 1.7M 行 × 3 步），AsyncLocal 每次 set 的
	/// copy-on-write 开销不可接受；sweep body 是纯同步代码（无 await / 上下文流动），ThreadLocal 语义足够。
	/// 读者经 <see cref="LogicalLineParser.GetLoadScaningLine"/> 统一入口。
	/// </summary>
	internal static readonly ThreadLocal<LogicalLine?> SweepScanLine = new();

	/// <summary>
	/// D5#2：未调用遍并行期间的 per-label 产物落点——printFunctionNotFoundWarning 的 FNF 事件拦截用
	///（ONCE 去重 / 计数必须回放段串行重走）。label 级设置（每 label 2 次写），AsyncLocal 开销可忽略，
	/// 但与 <see cref="SweepScanLine"/> 同机制更简单。
	/// </summary>
	private static readonly ThreadLocal<UncalledSweepArt?> SweepArtCurrent = new();

	/// <summary>sweep 期间为 true（实例字段，主线程在 Parallel.ForEach 前后设置）：<see cref="ScanLine"/> 写点切换与 nestCheck 集合局部化的 fast path。</summary>
	private bool _sweepParallelActive;

	/// <summary>
	/// sweep 期间为 true（静态 volatile）：读者 fast path——<see cref="LogicalLineParser.GetLoadScaningLine"/>
	/// 常态（非 sweep，含 runtime 热路径）直接短路 null 回落全局，免去 AsyncLocal/ThreadLocal 读
	///（eraTW 复测实测该读点在变量解析热路径上被调数百万次）。
	/// </summary>
	internal static volatile bool SweepActive;

	/// <summary>D5#2：FNF 事件缓冲（回放段重走 printFunctionNotFoundWarning 的 ONCE 决策）。</summary>
	private readonly record struct FnfEvent(string Str, LogicalLine Line, int Level, bool IsError);

	/// <summary>
	/// 未调用遍 per-label 产物。警告（含 FNF 占位）按原串行发射序进 <see cref="Warnings"/>，
	/// 回放段按序消费（<see cref="ReplaySweepArt"/>）——普通警告直发、FNF 占位重走原决策，
	/// 交错序与串行一致（FNF 与普通警告在 setJumpTo 行序上天然交错）。
	/// </summary>
	private sealed class UncalledSweepArt
	{
		public FunctionLabelLine Label = null!;
		public readonly List<ParserMediator.ParserWarning> Warnings = [];
		public readonly List<FnfEvent> FnfEvents = [];
		/// <summary>FNF 占位标记 level（普通警告 level 0-3，-1 安全保留）。</summary>
		public const int FnfPlaceholderLevel = -1;
		/// <summary>
		/// D5#3：本 label 解析过程中是否用到 callform 系。并行段由 <c>MarkUseCallForm</c> 写入本字段，
		/// 回放段 <c>ReplaySweepArt</c> 在主线程合并回 <c>useCallForm</c>——并行期间不碰实例字段。
		/// </summary>
		public bool UseCallForm;
		public void AddFnfEvent(string str, LogicalLine line, int level, bool isError)
		{
			Warnings.Add(new ParserMediator.ParserWarning("", null!, FnfPlaceholderLevel, null!));
			FnfEvents.Add(new FnfEvent(str, line, level, isError));
		}
	}

	/// <summary>
	/// D5#6：解析行写点。串行路径直写全局 sink（行为不变）；未调用遍并行期间写 per-线程
	/// <see cref="SweepScanLine"/>（全局 sink 无并发写；sweep 收尾 setScanLine(null!) 对齐串行终态——
	/// 串行下每个 ParseFunctionWithCatch 的 finally 都写 null，中间行值仅解析期读者可见，已由
	/// GetLoadScaningLine 重定向覆盖）。
	/// </summary>
	private void ScanLine(LogicalLine line)
	{
		if (_sweepParallelActive)
			SweepScanLine.Value = line;
		else
			setScanLine(line);
	}

	/// <summary>
	/// ParseScript 诊断累计（erb-loader-parallel 05）：仅 DisplayReport 开启时创建，默认路径零开销。
	/// 三步分段（setArgument/nestCheck/setJumpTo）按"主循环 / 未调用遍历"两桶累计 Tick，
	/// 供 D4 策略预注册判据（spec-phase2 R2-Q2）取数。
	/// </summary>
	private ParseScriptDiag? _parseDiag;

	/// <summary>
	/// NativeAOT Android 止血门：并行未调用清扫在 Android 上暂时统一回退串行。
	/// 该策略只改变平台安全门，不改变桌面配置或 AnalysisMode 的既有语义。
	/// <para>
	/// ADR-0026 诊断旋钮扩维：<paramref name="policy"/> 为壳层内存覆盖（默认 Auto 与无旋钮逐位等价）。
	/// ForceOn 只越过 Android 平台门（AnalysisMode 门与 <paramref name="configured"/> 主开关不越——
	/// 一个旋钮一个维度）；ForceOff 恒 false，压过一切组合。本函数保持唯一决策点，语义真值表
	/// 由 ErbLoaderTests 穷举（configured × analysisMode × isAndroid × policy 全组合）。
	/// </para>
	/// </summary>
	internal static bool ShouldRunParallelSweep(bool configured, bool analysisMode, bool isAndroid, SweepGatePolicy policy)
		=> policy switch
		{
			SweepGatePolicy.ForceOn => configured && !analysisMode,
			SweepGatePolicy.ForceOff => false,
			_ => configured && !analysisMode && !isAndroid,
		};

	private sealed class ParseScriptDiag
	{
		internal readonly Stopwatch Sw = new();
		/// <summary>当前 ParseFunctionWithCatch 的三步耗时计入哪一桶（主循环=false / 未调用遍历=true）。</summary>
		internal bool InUncalled;
		internal long MainSetArgTicks, MainNestTicks, MainJumpTicks;
		internal long UncalledSetArgTicks, UncalledNestTicks, UncalledJumpTicks;
	}

	/// <summary>
	/// 事前処理したファイルをさらに解析し実行可能な状態にする
	/// </summary>
	private void ParseScript()
	{
		// b2-irdiskcache：主循环期间挂载本 loader 为 arg 解析物化源（CalledFunction 的 stub 被调就地物化，
		// 供 ConvertArg 读 callee.Arg）。Eager 无桩恒 no-op；lazy 主循环单线程，本槽仅加载中有效。
		_activeArgLoader = this;
		int usedLabelCount = 0;
		int labelDepth = -1;
		// lazy-function-load（B 线 B1）：主循环按层 BFS 保持零结构变化；only 兜底——若某层函数体尚未
		// 物化（根=事件/系统被惰性记为桩），先就地物化再跑三步，使 setJumpTo 能继续向下发现子层 Depth。
		bool lazyMode = Config.Config.LazyFunctionLoad && !env.AnalysisMode;
		List<FunctionLabelLine> labelList = labelDic.GetAllLabels(true);
		// erb-loader-parallel 05：DisplayReport 开启时采集三组诊断（层宽直方图 / 三步分段 / 主循环 vs 未调用遍切分）
		ParseScriptDiag? diag = Config.Config.DisplayReport ? new ParseScriptDiag() : null;
		_parseDiag = diag;
		long mainTicks = 0, callFormTicks = 0, uncalledTicks = 0;
		SortedDictionary<int, int>? depthHist = null;
		Stopwatch? mainSw = diag != null ? Stopwatch.StartNew() : null;
		while (true)
		{
			labelDepth++;
			int countInDepth = 0;
			foreach (FunctionLabelLine label in labelList)
			{
				if (label.Depth != labelDepth)
					continue;
				//1756beta003 なんで追加したんだろう デバグ中になんかやったのか とりあえずコメントアウトしておく
				//if (label.LabelName == "EVENTTURNEND")
				//    useCallForm = true;
				usedLabelCount++;
				countInDepth++;
				// 惰性 + 桩：就地物化（含 ReparseFunction 内建三步，逐位等价于 eager 对本标签的唯一一次三遍解析），
				// 故此处物化后不再叠加 ParseFunctionWithCatch；非桩（已物化 / eager）走原三步。
				// b2-irdiskcache：加载期 CALL arg 转换已提前物化本标签（ThreePassed）→ 跳过重复三遍（防警告重复）。
				if (lazyMode && label.IsStub)
					MaterializeLabel(label);
				else if (!label.ThreePassed)
					ParseFunctionWithCatch(label);
			}
			if (countInDepth == 0)
				break;
		}
		mainSw?.Stop();
		mainTicks = mainSw?.ElapsedTicks ?? 0;
		labelDepth = -1;
		if (diag != null)
		{
			// 层宽直方图（d-1 = 未调用标签数）：D4 判据②（p90 层宽 vs 目标并行度）取数源
			depthHist = [];
			foreach (FunctionLabelLine label in labelList)
			{
				if (depthHist.TryGetValue(label.Depth, out int count))
					depthHist[label.Depth] = count + 1;
				else
					depthHist[label.Depth] = 1;
			}
		}
		var ignoredFNCWarningFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int ignoredFNCWarningCount = 0;

		bool ignoreAll = false;
		DisplayWarningFlag notCalledWarning = Config.Config.FunctionNotCalledWarning;
		switch (notCalledWarning)
		{
			case DisplayWarningFlag.IGNORE:
			case DisplayWarningFlag.LATER:
				ignoreAll = true;
				break;
		}
		// erb-loader-parallel 08 D4-1：未调用遍历并行化（C-only）——callform / else 两分支同票同套
		//（D4-3）。门控：ParallelParseScript 且非 AnalysisMode（warningDic/tempDic 路径强制串行，D5 前提）。
		// NativeAOT Android SIGSEGV 止血：Android 暂不运行并行清扫，设置值仍保留供桌面/后续 A-B。
		// ADR-0026：policy 为壳层诊断旋钮内存覆盖（null=Auto），parsescript_policy 行尾 policy= 字段
		// 自证当时策略与生效值（追加式字段，对既有解析兼容）——取证对「哪条路径确实执行了」以此为准。
		bool parallelSweepConfigured = Config.Config.ParallelParseScript;
		bool isAndroid = OperatingSystem.IsAndroid();
		SweepGatePolicy sweepPolicy = Config.Config.OverrideSweepGatePolicy ?? SweepGatePolicy.Auto;
		bool parallelSweep = ShouldRunParallelSweep(parallelSweepConfigured, env.AnalysisMode, isAndroid, sweepPolicy);
		// 06-K1/K2 自证：sweep_deg= 未调用遍历清扫度上限（与本轮 RunUncalledSweepParallel 同值），
		// dimlock= privateDimList 锁策略。时序前提：dimlock= 在 ParseScript 时读 Config，而实际锁行为是
		// IdentifierDictionary 构造时快照——壳层 OnReloadGame 先 Apply 旋钮再建会话，两者恒一致；
		// 若未来出现先于 Apply 的 idDic 构造路径，此字段会失真，需改为由 idDic 直接上报
		int sweepDegree = ResolveMaxParallelErbFiles(Config.Config.OverrideMaxParallelErbFiles, Environment.ProcessorCount);
		DimLockPolicy dimLockPolicy = Config.Config.OverrideDimLockPolicy ?? DimLockPolicy.Auto;
		EmueraLog.Info("ErbLoader",
			$"parsescript_policy configured={parallelSweepConfigured} effective={parallelSweep} android={isAndroid} analysis={env.AnalysisMode} policy={SweepGatePolicyCodec.ToConfigString(sweepPolicy)} sweep_deg={sweepDegree} dimlock={DimLockPolicyCodec.ToConfigString(dimLockPolicy)}");
		bool sweepParallelRan = false;
		if (useCallForm)
		{//callform系が使われたら全ての関数が呼び出されたとみなす。
			if (env.AnalysisMode)
				output.PrintSystemLine(trerror.BeNotFuncCheckBecauseUseCallform.Text);
			Stopwatch? callFormSw = diag != null ? Stopwatch.StartNew() : null;
			if (diag != null)
				diag.InUncalled = true;
			// lazy-function-load（B 线 B1）：惰性下 callform 分支的未调用解析体整体跳过（未调用函数保持桩），
			// 被调用主循环已按其 Depth 物化过；callform 分支本就无 FNC 导出，故跳过零损失。
			if (!lazyMode)
			{
				if (parallelSweep)
				{
					// 并行段：解析体全部入 per-label 缓冲（警告 / FNF 事件），回放段按 labelList 序重放
					ReplayUncalledSweep(RunUncalledSweepParallel(labelList, labelDepth, sweepDegree));
					sweepParallelRan = true;
				}
				else
				{
					foreach (FunctionLabelLine label in labelList)
					{
						if (label.Depth != labelDepth)
							continue;
						ParseFunctionWithCatch(label);
					}
				}
			}
			callFormSw?.Stop();
			callFormTicks = callFormSw?.ElapsedTicks ?? 0;
		}
		else
		{
			bool ignoreUncalledFunction = Config.Config.IgnoreUncalledFunction;
			Stopwatch? uncalledSw = diag != null ? Stopwatch.StartNew() : null;
			if (diag != null)
				diag.InUncalled = true;
			// else 分支（D4-3 同票同套）：解析体（!ignoreUncalledFunction 时才有）并行化同 callform 分支；
			// FNC ONCE 去重 / FuncNeverCalled 发射 / IsError 标记是序敏感决策——全部留在下方原循环体内
			// 串行重走（回放体 = 原串行循环体的重放，解析位置替换为 ReplaySweepArt 按序回放缓冲）。
			// lazy-function-load（B 线 B1）：惰性下不跑未调用解析体（sweepArts 恒 null），
			// 只保留下方 FNC 免解析导出（Depth==-1 深差集，零解析）；IgnoreUncalledFunction 的 IsError 被 supersede。
			UncalledSweepArt[]? sweepArts = !lazyMode && parallelSweep && !ignoreUncalledFunction
				? RunUncalledSweepParallel(labelList, labelDepth, sweepDegree)
				: null;
			int artIdx = 0;
			foreach (FunctionLabelLine label in labelList)
			{
				if (label.Depth != labelDepth)
					continue;
				//解析モード時は呼ばれなかったものをここで解析
				if (env.AnalysisMode)
					ParseFunctionWithCatch(label);
				bool ignore = false;
				if (notCalledWarning == DisplayWarningFlag.ONCE)
				{
					string filename = label.Position!.Value.Filename;

					if (!string.IsNullOrEmpty(filename))
					{
						if (ignoredFNCWarningFiles.Contains(filename))
						{
							ignore = true;
						}
						else
						{
							ignore = false;
							ignoredFNCWarningFiles.Add(filename);
						}
					}
					//break;
				}
				if (ignoreAll || ignore)
					ignoredFNCWarningCount++;
				else
					ParserMediator.Warn(string.Format(trerror.FuncNeverCalled.Text, label.LabelName), label, 1, false, false);
				// 决策七：AnalysisMode 已在上方调用过 ParseFunctionWithCatch，
				// nestCheck/setJumpTo 不幂等会重复发射警告。此处跳过避免重复解析。
				// lazy-function-load：惰性下跳过未调用解析体（含 IsError supersede），桩保持未物化。
				if (!lazyMode)
				{
					if (!ignoreUncalledFunction)
					{
						if (!env.AnalysisMode)
						{
							if (sweepArts != null)
							{
								// 原 ParseFunctionWithCatch(label) 的位置：按序回放该 label 的缓冲
								ReplaySweepArt(sweepArts[artIdx]);
								artIdx++;
							}
							else
								ParseFunctionWithCatch(label);
						}
					}
					else
					{
						if (!(label.NextLine is NullLine) && !(label.NextLine is FunctionLabelLine))
						{
							if (!label.NextLine.IsError)
							{
								label.NextLine.IsError = true;
								label.NextLine.ErrMes = "呼び出されないはずの関数が呼ばれた";
							}
						}
					}
				}
			}
			uncalledSw?.Stop();
			uncalledTicks = uncalledSw?.ElapsedTicks ?? 0;
			sweepParallelRan = sweepArts != null;
		}
		if (env.AnalysisMode && (warningDic.Keys.Count > 0 || GlobalStatic.tempDic.Keys.Count > 0))
		{
			output.PrintError(trerror.UndefinedFunctions.Text);
			if (warningDic.Keys.Count > 0)
			{
				output.PrintError(trerror.GeneralFunc.Text);
				foreach (string labelName in warningDic.Keys)
				{
					output.PrintError("　　" + labelName + ": " + warningDic[labelName].ToString() + trerror.Occurrences.Text);
				}
			}
			if (GlobalStatic.tempDic.Keys.Count > 0)
			{
				output.PrintError(trerror.SentenceFunc.Text);
				foreach (string labelName in GlobalStatic.tempDic.Keys)
				{
					output.PrintError("　　" + labelName + ": " + GlobalStatic.tempDic[labelName].ToString() + trerror.Occurrences.Text);
				}
			}
		}
		else
		{
			if (ignoredFNCWarningCount > 0 && Config.Config.DisplayWarningLevel <= 1 && notCalledWarning != DisplayWarningFlag.IGNORE)
				output.PrintError(string.Format(trerror.IgnoredFuncNeverCalled.Text, ignoredFNCWarningCount));
			if (ignoredFNFWarningCount > 0 && Config.Config.DisplayWarningLevel <= 2 && notCalledWarning != DisplayWarningFlag.IGNORE)
				output.PrintError(string.Format(trerror.IgnoredUndefinedFuncCall.Text, ignoredFNFWarningCount));
		}
		ParserMediator.FlushWarningList();
		if (Config.Config.DisplayReport)
			output.PrintError(string.Format(trerror.TotalFunc.Text, enabledLineCount, labelDic.Count, usedLabelCount));
		if (diag != null && depthHist != null)
		{
			// erb-loader-parallel 05：三组诊断发射（server 模式 stderr 可见，bench 脚本解析留存）
			static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
			depthHist.TryGetValue(-1, out int uncalledLabels);
			EmueraLog.Info("ErbLoader",
				$"parsescript_diag phases main_ms={TicksToMs(mainTicks):F0} callform_ms={TicksToMs(callFormTicks):F0} uncalled_ms={TicksToMs(uncalledTicks):F0} labels_main={usedLabelCount} labels_uncalled={uncalledLabels} uncalled_parallel={sweepParallelRan}");
			EmueraLog.Info("ErbLoader",
				$"parsescript_diag steps main_setargument_ms={TicksToMs(diag.MainSetArgTicks):F0} main_nestcheck_ms={TicksToMs(diag.MainNestTicks):F0} main_setjumpto_ms={TicksToMs(diag.MainJumpTicks):F0} uncalled_setargument_ms={TicksToMs(diag.UncalledSetArgTicks):F0} uncalled_nestcheck_ms={TicksToMs(diag.UncalledNestTicks):F0} uncalled_setjumpto_ms={TicksToMs(diag.UncalledJumpTicks):F0}");
			EmueraLog.Info("ErbLoader",
				$"parsescript_diag depth_hist {string.Join(' ', depthHist.Select(static kv => $"d{kv.Key}={kv.Value}"))}");
		}
		_parseDiag = null;
		if (Config.Config.AllowFunctionOverloading && Config.Config.WarnFunctionOverloading)
		{
			List<string> overloadedList = idDic.GetOverloadedList(labelDic);
			if (overloadedList.Count > 0)
			{
				output.NewLine();
				output.PrintError(trerror.OverWriteSystemFuncWarn1.Text);
				foreach (string funcname in overloadedList)
				{
					output.PrintSystemLine(string.Format(trerror.OverWriteSystemFuncWarn2.Text, funcname));
				}
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn3.Text);
				output.NewLine();
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn4.Text);
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn5.Text);
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn6.Text);
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn7.Text);
			}
		}
		_activeArgLoader = null; // b2-irdiskcache：主循环结束卸载 arg 解析物化源（stale 亦无害，下次重设）

	}


	public Dictionary<string, long> warningDic = [];
	private void printFunctionNotFoundWarning(string str, LogicalLine line, int level, bool isError)
	{
		// erb-loader-parallel 08 D5#2：未调用遍并行期间缓冲 FNF 事件（含被忽略的——回放时决策原样重走）。
		// ONCE 去重（ignoredFNFWarningFiles）与 ignoredFNFWarningCount 必须串行序，故整个决策延后到回放段；
		// IsError/ErrMes 行内写先行（幂等无害，回放重走时再写一次 no-op）。
		var sweepArt = SweepArtCurrent.Value;
		if (sweepArt != null)
		{
			if (isError)
			{
				line.IsError = true;
				line.ErrMes = str;
			}
			sweepArt.AddFnfEvent(str, line, level, isError);
			return;
		}
		if (env.AnalysisMode)
		{
			if (warningDic.TryGetValue(str, out long value))
				warningDic[str] = ++value;
			else
				warningDic.Add(str, 1);
			return;
		}
		if (isError)
		{
			line.IsError = true;
			line.ErrMes = str;
		}
		if (level < Config.Config.DisplayWarningLevel)
			return;
		bool ignore = false;
		DisplayWarningFlag warnFlag = Config.Config.FunctionNotFoundWarning;
		if (warnFlag == DisplayWarningFlag.IGNORE)
			ignore = true;
		else if (warnFlag == DisplayWarningFlag.DISPLAY)
			ignore = false;
		else if (warnFlag == DisplayWarningFlag.ONCE)
		{

			string filename = line.Position!.Value.Filename;
			if (!string.IsNullOrEmpty(filename))
			{
				if (ignoredFNFWarningFiles.Contains(filename))
				{
					ignore = true;
				}
				else
				{
					ignore = false;
					ignoredFNFWarningFiles.Add(filename);
				}
			}
		}
		if (ignore && !env.AnalysisMode)
		{
			ignoredFNFWarningCount++;
			return;
		}
		ParserMediator.Warn(str, line, level, isError, false);
	}

	/// <summary>
	/// erb-loader-parallel 08：未调用遍历并行段（spec-phase2 D4-1 / 调度框架 R3-Q5）。
	/// <see cref="Parallel"/>.ForEach(Partitioner 范围分区, Min(ProcessorCount, 8))——Partitioner 均载 +
	/// 度数封顶（度数硬编码不可配，R3-Q11）；per-label 产物（警告缓冲 / FNF 事件）按 labelList 序
	/// 存入返回数组，回放段串行重放。共享状态处置对照 D5 清单：#1 警告 per-label 缓冲（Begin/EndWarningBuffer）；
	/// #2 FNF 事件拦截（printFunctionNotFoundWarning 开头）；#3 useCallForm 改返回值 + per-label
	/// 产物（原为 ref 直写实例字段，共享写已消除，见 MarkUseCallForm）；
	/// #5 nestCheck 集合局部化（nestCheck 内部）；#6 scaningLine 写点切换（ScanLine()）；
	/// #7 Depth check-then-set 同层同值良性竞争（事实 B）；#8 并行段跳过 per-label 分步计时（_parseDiag 置 null，
	/// phases 总时长计时在调用方）。异常语义（R3-Q9）：先按序回放已产生的缓冲（已解析 label 的警告不丢）
	/// 再重抛 InnerExceptions[0]，由外层 LoadErbDir 既有 catch 统一处理。
	/// </summary>
	private UncalledSweepArt[] RunUncalledSweepParallel(List<FunctionLabelLine> labelList, int labelDepth, int maxDegree)
	{
		List<FunctionLabelLine> targets = [];
		foreach (FunctionLabelLine label in labelList)
		{
			if (label.Depth != labelDepth)
				continue;
			targets.Add(label);
		}
		var arts = new UncalledSweepArt[targets.Count];
		if (arts.Length == 0)
			return arts;
		_parseDiag = null;
		_sweepParallelActive = true;
		SweepActive = true;
		// 06-K1 修订 R3-Q11：清扫度不再硬编码——与 LoadPendingParallel 窗口同源同拨（同一旋钮，
		// 由 ParseScript 算好传入）。auto 时 = min(核数, 8) 与旧硬编码逐位一致；语义仍不同
		// （此处是并行解析线程上限，窗口是同时在生成/未合并的文件数），共用同一取证旋钮
		var opts = new ParallelOptions { MaxDegreeOfParallelism = maxDegree };
		try
		{
			// 小 chunk 动态均载：eraTW 函数大小长尾，静态大 range 会把巨型函数聚在同一分区拖成长尾
			int chunk = Math.Max(1, targets.Count / (opts.MaxDegreeOfParallelism * 16));
			Parallel.ForEach(Partitioner.Create(0, targets.Count, chunk), opts, range =>
			{
				try
				{
					for (int i = range.Item1; i < range.Item2; i++)
					{
						var art = new UncalledSweepArt { Label = targets[i] };
						arts[i] = art;
						ParserMediator.BeginWarningBuffer(art.Warnings);
						SweepArtCurrent.Value = art;
						try
						{
							ParseFunctionWithCatch(targets[i]);
						}
						finally
						{
							SweepArtCurrent.Value = null;
							ParserMediator.EndWarningBuffer();
						}
					}
				}
				finally
				{
					// 线程池线程复用防残留：本 range 结束后清 per-线程 slot（否则该线程后续跑 runtime
					// 代码时 SweepScanLine.Value 非 null，读者会读到 stale 行）
					SweepScanLine.Value = null;
					SweepArtCurrent.Value = null;
				}
			});
		}
		catch (AggregateException agg)
		{
			ReplayUncalledSweep(arts);
			throw agg.InnerExceptions[0];
		}
		finally
		{
			_sweepParallelActive = false;
			SweepActive = false;
			// Parallel.ForEach 内联执行会污染调用线程的 ThreadLocal slot——显式复位；终态对齐串行
			//（串行下最后一个 ParseFunctionWithCatch 的 finally 写 null）
			SweepScanLine.Value = null;
			SweepArtCurrent.Value = null;
			setScanLine(null!);
		}
		return arts;
	}

	/// <summary>按 labelList 序回放 per-label 产物（null 元素跳过——并行段异常时部分 label 未调度）。</summary>
	private void ReplayUncalledSweep(UncalledSweepArt[] arts)
	{
		for (int i = 0; i < arts.Length; i++)
		{
			if (arts[i] != null)
				ReplaySweepArt(arts[i]);
		}
	}

	/// <summary>
	/// 回放单个 label 的缓冲（回放协议：回放体 = 原串行循环体的重放，不写镜像逻辑）：
	/// 普通警告按序直发全局列表；FNF 占位按序重走 printFunctionNotFoundWarning 原决策
	///（ONCE 去重 / ignoredFNFWarningCount / Warn 发射，警告/事件从缓冲读，决策逻辑原样重走）。
	/// </summary>
	private void ReplaySweepArt(UncalledSweepArt art)
	{
		// D5#3：并行段采集的 callform 标记在此由主线程合并（单向置位，等价于串行的 OR 语义）。
		// 必须早于下方 warnings.Count == 0 的提前返回，否则无警告的 label 会丢失该标记。
		if (art.UseCallForm)
			useCallForm = true;
		var warnings = art.Warnings;
		if (warnings.Count == 0)
			return;
		int fnfIdx = 0;
		for (int i = 0; i < warnings.Count; i++)
		{
			var w = warnings[i];
			if (w.WarningLevel == UncalledSweepArt.FnfPlaceholderLevel)
			{
				var ev = art.FnfEvents[fnfIdx++];
				printFunctionNotFoundWarning(ev.Str, ev.Line, ev.Level, ev.IsError);
			}
			else
			{
				ParserMediator.EmitWarning(w);
			}
		}
		warnings.Clear();
		art.FnfEvents.Clear();
	}

	/// <summary>b2-irdiskcache：「行文本流」抽象（design §5.3）——源回读与 blob 文本两条实现共用同一物化循环，物化产物等价性收敛为「输入流 == 源文件启用行」一件事。</summary>
	private interface IFunctionLineSource
	{
		/// <summary>取下一启用行（blob 源不回源、不 rename、不重 [ 预处理——已在产生端过滤）。行流在物化循环内驱动 ppstate。</summary>
		CharStream? ReadEnabledLine(bool disabled);
		/// <summary>最近一次返回行的行号（对齐 EraStreamReader.LineNo 语义）。</summary>
		int LineNo { get; }
		string Filename { get; }
	}

	/// <summary>源回读后端：包一层 <see cref="EraStreamReader"/>（Preload 缓存 / DirAccessor / EncodingHandler，ADR-0023 决策二回读通道）。</summary>
	private sealed class SourceLineSource : IFunctionLineSource
	{
		private readonly EraStreamReader _reader;
		public SourceLineSource(EraStreamReader reader) => _reader = reader;
		public string Filename => _reader.Filename;
		public int LineNo => _reader.LineNo;
		public CharStream? ReadEnabledLine(bool disabled) => _reader.ReadEnabledLine(disabled);
	}

	/// <summary>IR blob 后端：回放函数块内已过滤的启用行（行号 + 文本），不做 rename / SkipWhiteSpace 之外的加工。</summary>
	private sealed class BlobLineSource : IFunctionLineSource
	{
		private readonly IrLine[] _lines;
		private readonly string _filename;
		private int _index;
		private int _lineNo;
		public BlobLineSource(IrLine[] lines, string filename) { _lines = lines; _filename = filename; }
		public string Filename => _filename;
		public int LineNo => _lineNo;
		public CharStream? ReadEnabledLine(bool disabled)
		{
			if (_index >= _lines.Length)
				return null;
			var l = _lines[_index++];
			_lineNo = l.LineNo;
			var st = new CharStream(l.Text);
			LexicalAnalyzer.SkipWhiteSpace(st); // 对齐 ReadEnabledLine 产出的指向（源侧在 ReadEnabledLine 内已 Skip）
			return st;
		}
	}

	/// <summary>
	/// 02 抽取 + b2-irdiskcache 统一：单函数体重解析（物化）引擎。给定未物化标签桩 <paramref name="stub"/>
	/// 与一条启用行流 <paramref name="src"/>，定位本函数行区间，重建 label + 行链（复用 <see cref="LogicalLineParser"/>），
	/// 再跑（IR 命中桩先行）parseLabel + setArgument/nestCheck/setJumpTo 三遍。
	/// <list type="bullet">
	/// <item>B1 源后背桩（<see cref="FunctionLabelLine.IrBacked"/> = false）：签名已在惰性生成期由源 # 行处理齐（Arg/Def/私有变量/IsMethod/LocalLength 与 eager 逐位一致），重解析只重建函数体行链，行为与既有 ReparseFunction 逐位一致。</item>
	/// <item>IR 命中桩（IrBacked = true）：最小桩签名留空，此处从 blob 文本重建——@ 行重建 wc（parseLabel 待用）、# 行重跑 ParseSharpLine（私有变量/IsMethod/LocalLength）、随后 parseLabel + resizeLocalVars。</item>
	/// </list>
	/// 返回标签（in-place 于桩上重建，不修改引用无重路由）；未到达目标函数 / 流打不开 → null（三态占位）。幂等。
	/// </summary>
	private FunctionLabelLine? MaterializeFunction(FunctionLabelLine stub, IFunctionLineSource src)
	{
		if (stub.Position == null)
			return null;
		if (stub is InvalidLabelLine)
		{
			// 非法标签桩：无 body 可重建，保持占位（不应物化）。
			return null;
		}
		// 签名是否还需重建：IR 桩（或签名已由 EnsureCalleeSignature 预计算则 Arg!=null 跳过），源后背桩 Arg 已就位。
		bool needSignature = stub.Arg == null;
		// 以「@标签名」定位本函数（不依赖行号——重开户下 LineNo 语义漂移；函数名在单文件内唯一）。
		StringComparer nameEq = Config.Config.StrComper;
		// 直接以「桩」作为重解析目标（不新建瞬态标签）：body 绑定桩的私有变量 token，与参数赋值（写桩私有变量）
		// 一致（Issue 06：此前新建标签导致 ReduceArgumentOnLoad=NO 运行期解析读空、YES 解析期绑错 token）。
		FunctionLabelLine? label = null;
		LogicalLine tail = null!;
		// [ 预处理评估：源路径重演 [SKIPSTART][SKIPEND] 等保「故意不平衡」块不被误解析；blob 源无 [ 行则 no-op。
		var ppstate = new PPState(env, idDic);
		CharStream? line;
		while ((line = src.ReadEnabledLine(ppstate.Disabled)) != null)
		{
			CharStream st = line;
			var position = new ScriptPosition(src.Filename, src.LineNo);
			// [ 预处理指令（与主加载同款，先于 @/$ 判定）
			if (st.Current == '[' && st.Next != '[')
			{
				st.ShiftNext();
				string token = LexicalAnalyzer.ReadSingleIdentifier(st);
				LexicalAnalyzer.SkipWhiteSpace(st);
				string token2 = LexicalAnalyzer.ReadSingleIdentifier(st);
				if (string.IsNullOrEmpty(token) || st.Current != ']')
					ParserMediator.Warn(trerror.InvalidSBrackets.Text, position, 1);
				ppstate.AddKeyWord(token, token2, position);
				st.ShiftNext();
				if (!st.EOS)
					ParserMediator.Warn(string.Format(trerror.IgnoreAfterPreprosessor.Text, token), position, 1);
				continue;
			}
			if (ppstate.Disabled)
				continue;
			if (st.Current == '@')
			{
				if (label == null)
				{
					// 解析 @ 行仅用于匹配标签名；body 直接解析进桩（见上方说明）。
					if (LogicalLineParser.ParseLabelLine(st, position, output, null) is FunctionLabelLine lbl
						&& nameEq.Equals(lbl.LabelName, stub.LabelName))
					{
						label = stub;
						// IR 命中桩：从 @ 行重建 wc（否则 parseLabel 的 PopRowArgs 得空）。B1 桩 wc 生成期已有。
						if (needSignature)
							stub.SetRowArgs(lbl.PopRowArgs());
						// @ 行 ParentLabelLine 须自指（生成期由 addLine 置位）：scaningLine=本函数时
						// GetVariableToken 经 line.ParentLabelLine.GetPrivateVariable 解析私有变量/参数，
						// 否则对 IR 桩扫描线命中后 ParentLabelLine=null → 私有变量落 privateDimList → 误报。
						label.ParentLabelLine = label;
						tail = stub;
					}
					continue;
				}
				break; // 下一个函数 = 本函数体边界
			}
			if (label == null)
				continue; // 尚未到达目标函数
			if (st.Current == '$')
			{
				if (LogicalLineParser.ParseLabelLine(st, position, output, null) is GotoLabelLine g)
				{
					g.ParentLabelLine = label;
					// 惰性下桩跳过 $ 标签、加载期不注册，物化必须补注册（否则 GOTO SetJumpTo 经 GetLabelDollar 解析失败）。
					labelDic.AddLabelDollar(g);
					tail.NextLine = g;
					tail = g;
				}
				continue;
			}
			if (st.Current == '#')
			{
				// B1 桩：签名已在生成期处理齐（#DIM/#LOCALSIZE/…），重解析只重建函数体；重复 AddPrivateVariable 会误报。
				// IR 命中桩：签名留空（或未经 EnsureCalleeSignature 预计算），须重跑 ParseSharpLine 重建（私有变量/IsMethod/LocalLength/…）。
				if (needSignature)
				{
					List<string> a = [], b = [];
					LogicalLineParser.ParseSharpLine(stub, st, position, a, b);
				}
				continue;
			}
			var parsed = LogicalLineParser.ParseLine(st, position, output, label);
			if (parsed == null)
				continue;
			if (parsed is InvalidLine)
				parsed.IsError = true;
			else if (JSONConfig.Data.UseNewRandom && parsed is InstructionLine instruction)
			{
				// 与 GenerateErbFileCore 同款 RANDOMIZE/DUMPRAND/INITRAND 抑制警告（结构无关，仅对齐现有发射）
				switch (instruction.FunctionCode)
				{
					case FunctionCode.RANDOMIZE:
						ParserMediator.Warn(trerror.IgnoreRandomize.Text, position, 0);
						break;
					case FunctionCode.DUMPRAND:
						ParserMediator.Warn(trerror.CanNotUseDumprand.Text, position, 0);
						break;
					case FunctionCode.INITRAND:
						ParserMediator.Warn(trerror.CanNotUseInitrand.Text, position, 0);
						break;
				}
			}
			parsed.ParentLabelLine = label;
			tail.NextLine = parsed;
			tail = parsed;
		}
		if (label == null)
			return null;
		tail.NextLine = new NullLine();
		// B1 桩不重跑 parseLabel（生成期 setLabelsArg 已解析 Arg/Def）；IR 桩在这里补 Arg/Def + 扩 ARG/ARGS 槽。
		// setScanLine(label) 提供 scan-line 上下文（对齐 eager setLabelsArg），供三步/parseLabel 内私有变量解析。
		setScanLine(label);
		// Issue 06：对齐 eager ParseFunctionWithCatch 的 finally 收尾——nestCheck 对终止 NullLine 也会 ScanLine，
		// 不重置则运行期 scaningLine 残留尾部 NullLine → 误报「この関数中では定義されていません」。三步/异常一并复位。
		try
		{
			if (needSignature)
			{
				parseLabel(label);
				// 对齐 SortLabels 的 resizeLocalVars：IR 桩 gen 期按 ArgLength=0 建槽（SortLabels 对非事件把槽建为 0），
				// 物化后按实际值扩容，否则运行期 ARGx 绑定到 size-0 槽。
				if (!label.IsEvent)
				{
					if (label.ArgLength > 0)
						idDic.resizeLocalVars("ARG", label.LabelName, label.ArgLength);
					if (label.ArgsLength > 0)
						idDic.resizeLocalVars("ARGS", label.LabelName, label.ArgsLength);
				}
			}
			setArgument(label);
			nestCheck(label);
			setJumpTo(label);
		}
		finally
		{
			ScanLine(null!);
		}
		label.ThreePassed = true; // b2-irdiskcache：三遍完成，供主循环跳过重复处理（加载期 CALL arg 转换已提前物化本标签时）
		return label;
	}

	/// <summary>
	/// b2-irdiskcache：非递归「签名物化」——只重建桩的 Arg/Def/私有变量/IsMethod（@ + # 行 → parseLabel），
	/// <b>不解析函数体、不跑 setJumpTo</b>。供加载期 CALL/式中函数 arg 转换（ConvertArg 读 callee.Arg）用，
	/// 避免「物化 A → A.setJumpTo → CALL B → 物化 B → …」的无界递归栈溢出。幂等（Arg != null 即已就位）。
	/// </summary>
	private void ComputeSignature(FunctionLabelLine stub)
	{
		if (stub.Arg != null || !stub.IsStub)
			return;
		string logical = stub.Position?.Filename ?? "";
		if (logical.Length == 0)
			return;
		// IR 优先（blob 后台）；blob 读失败 / 未命中回落到源。
		if (_ir is { Available: true } ir && ir.Index.TryGetValue((logical, stub.LabelName), out var entry))
		{
			try
			{
				var blob = ReadBlobMeasured(ir, entry);
				ComputeSignatureLines(stub, new BlobLineSource(blob.Lines, stub.Position!.Value.Filename));
				return;
			}
			catch
			{
				// blob 损坏 → 回源签名（不在此抛，避免 arg 转换 NRE）。
			}
		}
		if (_filePathByLogical.TryGetValue(logical, out string? physical) && !string.IsNullOrEmpty(physical))
		{
			using var eReader = new EraStreamReader(Config.Config.UseRenameFile && ParserMediator.RenameDic != null);
			if (eReader.OpenOnCache(physical, logical))
				ComputeSignatureLines(stub, new SourceLineSource(eReader));
		}
	}

	private void ComputeSignatureLines(FunctionLabelLine stub, IFunctionLineSource src)
	{
		CharStream? line;
		while ((line = src.ReadEnabledLine(false)) != null)
		{
			CharStream st = line;
			var position = new ScriptPosition(src.Filename, src.LineNo);
			// 签名只需 @ + # 行；body（指令/$/[[…]]）忽略；[ 预处理行忽略（签名行不受其禁用，生成期已过滤）。
			if (st.Current == '@')
			{
				if (LogicalLineParser.ParseLabelLine(st, position, output, null) is FunctionLabelLine lbl
					&& Config.Config.StrComper.Equals(lbl.LabelName, stub.LabelName))
				{
					stub.SetRowArgs(lbl.PopRowArgs());
					stub.ParentLabelLine = stub;
					EnsureEventSystemFlags(stub);
				}
				continue;
			}
			if (st.Current == '#')
			{
				List<string> a = [], b = [];
				LogicalLineParser.ParseSharpLine(stub, st, position, a, b);
				continue;
			}
			// 其余行（函数体）跳过——签名物化不解析 body。
		}
		if (stub.Arg == null)
		{
			// b2-irdiskcache：签名物化是嵌套在「调用方 arg / 式中函数解析」内的辅助——必须保留并恢复调用方
			// scaningLine。若像全量物化那样 setScanLine(stub) 后 finally ScanLine(null!)，会把调用方
			// Process.scaningLine 清空：其正在解析的表达式（如 `&& !YANUSHI` / `THING(LOCAL)` / `ARG`）
			// 续读时 scaningLine=null → 私有变量/LOCAL/ARG 全部误报「未在此函数中定义/不存在执行中的函数」。
			// round1(B1) 无此问题：源后背桩已有 Arg → EnsureCalleeSignature 提前 no-op，不进入本分支。
			LogicalLine? prevScan = GlobalStatic.Process?.GetScaningLine();
			setScanLine(stub);
			try
			{
				parseLabel(stub);
				if (!stub.IsEvent)
				{
					if (stub.ArgLength > 0)
						idDic.resizeLocalVars("ARG", stub.LabelName, stub.ArgLength);
					if (stub.ArgsLength > 0)
						idDic.resizeLocalVars("ARGS", stub.LabelName, stub.ArgsLength);
				}
			}
			finally
			{
				ScanLine(prevScan!);
			}
			// 注意：不动 IsStub（仍为桩，函数体留待主循环物化）。
		}
	}

	/// <summary>按标签名补全事件/系统标志与 Depth（对齐 ParseLabelLine 的判定）。</summary>
	private static void EnsureEventSystemFlags(FunctionLabelLine stub)
	{
		if (IdentifierDictionary.IsEventLabelName(stub.LabelName))
		{
			stub.IsEvent = true;
			stub.IsSystem = true;
			if (stub.Depth != 0)
				stub.Depth = 0;
		}
		else if (IdentifierDictionary.IsSystemLabelName(stub.LabelName))
		{
			stub.IsSystem = true;
			if (stub.Depth != 0)
				stub.Depth = 0;
		}
	}

	/// <summary>载入函数重解析（源后背，B1 兼容入口）：重开源文件，以桩为目标的单函数重解析。源不可读/桩行不在 → null。</summary>
	internal FunctionLabelLine? ReparseFunction(FunctionLabelLine stub, string filepath)
	{
		var pos = stub.Position;
		if (pos == null)
			return null;

		using var eReader = new EraStreamReader(Config.Config.UseRenameFile && ParserMediator.RenameDic != null);
		if (!eReader.OpenOnCache(filepath, pos.Value.Filename))
			return null; // 三态：源文件打不开（被移动/删除）→ 占位，不抛
		return MaterializeFunction(stub, new SourceLineSource(eReader));
	}

	/// <summary>
	/// b2-irdiskcache：IR 后背物化——以 blob 文本流作为 <see cref="MaterializeFunction"/> 输入（design §5.3 / 对账项 C）。
	/// 读 blob 失败抛异常（调用方转回退/占位）。单函数 parse 上复用既有循环，与源后背产物逐一等价。
	/// </summary>
	private FunctionLabelLine? ReparseFunctionFromIr(FunctionLabelLine stub, in IrIndexEntry entry)
	{
		if (_ir is not { Available: true } ir)
			return null;
		var blob = ReadBlobMeasured(ir, entry);
		string filename = stub.Position?.Filename ?? "";
		return MaterializeFunction(stub, new BlobLineSource(blob.Lines, filename));
	}

	/// <summary>
	/// lazy-function-load（B 线 B1）：就地物化——把惰性桩 <paramref name="stub"/> 的行链替换为
	/// <see cref="ReparseFunction"/> 产出的真实函数体（in-place 更新桩对象本身，labelDic / JumpTo 等引用
	/// 自动一致，无需重路由字典）。幂等：已物化（非桩）直接返回。
	/// 失败路径三态（源打不开 / 桩行已不在文件中 → ReparseFunction 返回 null）降级为占位报错：
	/// 标记 IsError+ErrMes、清桩标记（停止重试），不抛新异常——该函数按「已定义但解析不可得」处理。
	/// </summary>
	private void MaterializeLabel(FunctionLabelLine stub)
	{
		if (!stub.IsStub)
			return; // 幂等
		// b2-irdiskcache：IR 命中桩 → blob 优先物化，blob 失败回退源（design §5.3，User Story 8「静默回退」）。
		if (stub.IrBacked)
		{
			MaterializeLabelFromIr(stub);
			return;
		}
		// 以下为源后背（B1）原路径：桩为源生成（含完整签名）。
		string logical = stub.Position?.Filename ?? "";
		if (logical.Length == 0
			|| !_filePathByLogical.TryGetValue(logical, out string? physical) || string.IsNullOrEmpty(physical))
		{
			DegradeToPlaceholderError(stub);
			return; // 三态①：源路径不可得 → 占位报错
		}
		FunctionLabelLine? reparse;
		try
		{
			reparse = ReparseFunction(stub, physical);
		}
		catch (Exception exc)
		{
			// 物化期单函数重解析不应向调用方抛新异常（「首调用无新异常」门禁）——等价于 eager
			// ParseFunctionWithCatch 的 catch：告警 + 降级为占位报错（三态③）。
			string errmes = exc is EmueraException ? exc.Message : exc.GetType().ToString() + ":" + exc.Message;
			ParserMediator.Warn(string.Format(trerror.FuncAnalysisError.Text, stub.LabelName, errmes), stub, 2, true, false, exc.StackTrace!);
			DegradeToPlaceholderError(stub);
			return;
		}
		if (reparse == null)
		{
			DegradeToPlaceholderError(stub);
			return; // 三态②：桩行已不在文件中 → 占位报错
		}
		// ReparseFunction 直接在桩上重建行链（ParentLabelLine 已指向桩自身、$ 标签已注册、body 变量引用
		// 绑定桩的私有变量 token），此处只需复位桩标记；标签头（Position/Arg/Def/IsMethod/私有变量）在惰性
		// 生成期已与 eager 逐位一致，LocalLength/LocalsLength 亦已由生成期 #LOCALSIZE / SortLabels 置位。
		stub.NextLine = reparse.NextLine;
		stub.IsStub = false;
	}

	/// <summary>b2-irdiskcache：IR 命中桩物化——blob 优先，blob 校验失败/缺失回源（ReparseFunction 源后背亦会重建签名）。三态降级占位不抛新异常。</summary>
	private void MaterializeLabelFromIr(FunctionLabelLine stub)
	{
		string logical = stub.Position?.Filename ?? "";
		string? physical = null;
		if (logical.Length > 0)
			_filePathByLogical.TryGetValue(logical, out physical);

		// 1) blob 优先
		if (_ir is { Available: true } ir && logical.Length > 0
			&& ir.Index.TryGetValue((logical, stub.LabelName), out var entry))
		{
			FunctionLabelLine? viaIr = null;
			try
			{
				viaIr = ReparseFunctionFromIr(stub, entry);
			}
			catch (Exception exc)
			{
				EmueraLog.Warn("ErbIrCache", $"IR blob corrupt, fallback to source: {stub.LabelName} ({exc.GetType().Name})");
				viaIr = null;
			}
			if (viaIr != null)
			{
				stub.NextLine = viaIr.NextLine;
				stub.IsStub = false;
				stub.IrBacked = false;
				return;
			}
		}

		// 2) 回退源路径（blob 损坏/缺失 → 源后背高可信，回源比占位更符合 User Story 8）
		if (!string.IsNullOrEmpty(physical))
		{
			FunctionLabelLine? viaSource = null;
			try
			{
				viaSource = ReparseFunction(stub, physical);
			}
			catch (Exception exc)
			{
				string errmes = exc is EmueraException ? exc.Message : exc.GetType().ToString() + ":" + exc.Message;
				ParserMediator.Warn(string.Format(trerror.FuncAnalysisError.Text, stub.LabelName, errmes), stub, 2, true, false, exc.StackTrace!);
				viaSource = null;
			}
			if (viaSource != null)
			{
				// 源回退已重建签名（ReparseFunction 对 IrBacked 桩走 rebuildSignature），仅复位标记。
				stub.NextLine = viaSource.NextLine;
				stub.IsStub = false;
				stub.IrBacked = false;
				return;
			}
		}
		DegradeToPlaceholderError(stub);
	}

	/// <summary>物化失败降级为占位报错：清桩标记（停止重试）+ 一次性标 IsError/ErrMes，不抛新异常（ADR-0023 决策二三态先例）。</summary>
	private static void DegradeToPlaceholderError(FunctionLabelLine stub)
	{
		stub.IsStub = false;
		if (!stub.IsError)
		{
			stub.IsError = true;
			stub.ErrMes = trerror.CalledFailedFunc.Text;
		}
	}

	/// <summary>
	/// lazy-function-load：运行时物化接缝入口（<see cref="CalledFunction"/> 命中桩时调用）。
	/// 复用会话主加载保留的 <see cref="GlobalStatic.Process.RuntimeLoader"/>（含物理路径映射 / env / scanLine）。
	/// eager（无桩）下恒 no-op，运行期路径零侵入。
	/// 注：仅运行时（RuntimeLoader 非空）走全量物化；加载期由 CALL_Instruction.SetJumpTo 显式调
	/// <see cref="EnsureCalleeSignature"/>（签名级、非递归），不可在此全量物化 —— 否则 A 物化→setJumpTo→CALL B→物化 B→
	/// setJumpTo→CALL C→… 无界递归栈溢出（eraTW 递归调用链）。
	/// </summary>
	internal static void MaterializeOnRuntime(FunctionLabelLine label)
	{
		if (label == null || !label.IsStub)
			return;
		GlobalStatic.Process?.RuntimeLoader?.MaterializeLabel(label);
	}

	/// <summary>
	/// b2-irdiskcache：加载期 CALL/式中函数 arg 转换前，保证被调桩的「签名」可解析（Arg/Def/私有变量/IsMethod）。
	/// 只算签名、不动函数体、不跑 setJumpTo → 非递归；ComputeSignature 读取 blob（或源）@/# 行后 parseLabel。
	/// 已解析（Arg != null）→ no-op（幂等）。加载中 RuntimeLoader=null，故用 <see cref="_activeArgLoader"/>。
	/// </summary>
	internal static void EnsureCalleeSignature(FunctionLabelLine stub)
	{
		if (stub == null || !stub.IsStub || stub.Arg != null)
			return;
		var loader = _activeArgLoader ?? GlobalStatic.Process?.RuntimeLoader;
		if (loader == null)
			return;
		try
		{
			loader.ComputeSignature(stub);
		}
		catch
		{
			// 签名计算失败不在此抛（后续物化三态降级），确保 arg 转换路径对损坏 blob 不崩新异常。
		}
	}

	/// <summary>加载期（ParseScript 主循环）arg 解析物化用的当前 loader；运行时恒经 RuntimeLoader，本槽仅加载中回退。</summary>
	private static ErbLoader? _activeArgLoader;

	private void ParseFunctionWithCatch(FunctionLabelLine label)
	{//ここでエラーを捕まえることは本来はないはず。ExeEE相当。
		try
		{
			if (_parseDiag is { } diag)
			{
				// erb-loader-parallel 05：三步分段计时（按 InUncalled 分桶累计）
				var sw = diag.Sw;
				sw.Restart();
				setArgument(label);
				long setArg = sw.ElapsedTicks;
				sw.Restart();
				nestCheck(label);
				long nest = sw.ElapsedTicks;
				sw.Restart();
				setJumpTo(label);
				long jump = sw.ElapsedTicks;
				if (diag.InUncalled)
				{
					diag.UncalledSetArgTicks += setArg;
					diag.UncalledNestTicks += nest;
					diag.UncalledJumpTicks += jump;
				}
				else
				{
					diag.MainSetArgTicks += setArg;
					diag.MainNestTicks += nest;
					diag.MainJumpTicks += jump;
				}
			}
			else
			{
				setArgument(label);
				nestCheck(label);
				setJumpTo(label);
			}
		}
		catch (Exception exc)
		{
#if !HEADLESS
			System.Media.SystemSounds.Hand.Play();
#endif
			//1756beta2+v6.1 修正の効率化のために何かパース関係でハンドリングできてないエラーが出た場合はスタックトレースを投げるようにした
			string errmes = exc is EmueraException ? exc.Message : exc.GetType().ToString() + ":" + exc.Message;
			ParserMediator.Warn(string.Format(trerror.FuncAnalysisError.Text, label.LabelName, errmes), label, 2, true, false, (exc is not EmueraException ? exc.StackTrace : null)!);
			label.ErrMes = trerror.CalledFailedFunc.Text;
#if HEADLESS
			// 无头模式下无需处理 UI 消息队列
#else
			System.Windows.Forms.Application.DoEvents();
#endif
		}
		finally
		{
			ScanLine(null!);
		}
		label.ThreePassed = true; // b2-irdiskcache：三遍跑完（含失败已标错）——主循环按此跳过重复三遍。

	}

	private void setArgument(FunctionLabelLine label)
	{
		//1周目/3周
		//引数の解析とか
		LogicalLine nextLine = label;
		bool inMethod = label.IsMethod;
		while (true)
		{
			nextLine = nextLine.NextLine;
			ScanLine(nextLine);
			if (nextLine is not InstructionLine func)
			{
				if (nextLine is NullLine or FunctionLabelLine)
					break;
				continue;
			}
			if (inMethod)
			{
				if (!func.Function.IsMethodSafe())
				{
					ParserMediator.Warn(string.Format(trerror.CanNotUseInUserFunc.Text, func.Function.Name), nextLine, 2, true, false);
					continue;
				}
			}
			if (Config.Config.NeedReduceArgumentOnLoad || env.AnalysisMode || func.Function.IsForceSetArg())
				ArgumentParser.SetArgumentTo(func);
		}
	}

	private void nestCheck(FunctionLabelLine label)
	{
		//2周目/3周
		//IF-ELSEIF-ENDIF、REPEAT-RENDの対応チェックなど
		//PRINTDATA系もここでチェック
		// 决策三-a：复用 ErbLoader 实例字段，避免每函数分配 3 个容器（1000 函数 = 3000+ 次分配）
		// erb-loader-parallel 08 D5#5：未调用遍并行期间实例字段复用会互踩——改局部实例
		//（eraTW 63k label × 小集合，分配成本可忽略）；串行路径保持字段复用。
		LogicalLine nextLine = label;
		List<InstructionLine> tempLineList;
		Stack<InstructionLine> nestStack;
		Stack<InstructionLine> SelectcaseStack;
		if (_sweepParallelActive)
		{
			tempLineList = [];
			nestStack = new();
			SelectcaseStack = new();
		}
		else
		{
			tempLineList = _tempLineListReuse;
			nestStack = _nestStackReuse;
			SelectcaseStack = _selectcaseStackReuse;
			tempLineList.Clear();
			nestStack.Clear();
			SelectcaseStack.Clear();
		}
		InstructionLine pairLine = null!;
		while (true)
		{
			nextLine = nextLine.NextLine;
			ScanLine(nextLine);
			if (nextLine is NullLine or FunctionLabelLine)
				break;
			if (nextLine is not InstructionLine)
			{
				if (nextLine is GotoLabelLine)
				{
					InstructionLine currentBaseFunc = (nestStack.Count == 0 ? null : nestStack.Peek())!;
					if (currentBaseFunc != null)
					{
						if (currentBaseFunc.FunctionCode == FunctionCode.PRINTDATA
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAL
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAW
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAD
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATADL
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATADW
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAK
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAKL
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAKW
							|| currentBaseFunc.FunctionCode == FunctionCode.STRDATA
							|| currentBaseFunc.FunctionCode == FunctionCode.DATALIST
							|| currentBaseFunc.FunctionCode == FunctionCode.TRYCALLLIST
							|| currentBaseFunc.FunctionCode == FunctionCode.TRYJUMPLIST
							|| currentBaseFunc.FunctionCode == FunctionCode.TRYGOTOLIST)
						//|| (currentBaseFunc.FunctionCode == FunctionCode.SELECTCASE))
						{
							ParserMediator.Warn(string.Format(trerror.CanNotLabelDefineInSyntax.Text, currentBaseFunc.Function.Name), nextLine, 2, true, false);
						}
					}
				}
				continue;
			}
			var func = nextLine as InstructionLine;
			var baseFunc = nestStack.Count == 0 ? null : nestStack.Peek();
			if (baseFunc != null)
			{
				if (baseFunc!.Function!.IsPrintData() || baseFunc.FunctionCode == FunctionCode.STRDATA)
				{
					if (func!.FunctionCode != FunctionCode.DATA && func.FunctionCode != FunctionCode.DATAFORM && func.FunctionCode != FunctionCode.DATALIST
						&& func.FunctionCode != FunctionCode.ENDLIST && func.FunctionCode != FunctionCode.ENDDATA)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidInstructionInSyntax.Text, baseFunc.Function.Name, func.Function.Name), func, 2, true, false);
						continue;
					}
				}
				else if (baseFunc.FunctionCode == FunctionCode.DATALIST)
				{
					if (func!.FunctionCode != FunctionCode.DATA && func.FunctionCode != FunctionCode.DATAFORM && func.FunctionCode != FunctionCode.ENDLIST)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidInstructionInSyntax.Text, "DATALIST", func.Function.Name), func, 2, true, false);
						continue;
					}
				}
				else if (baseFunc.FunctionCode == FunctionCode.TRYCALLLIST || baseFunc.FunctionCode == FunctionCode.TRYJUMPLIST || baseFunc.FunctionCode == FunctionCode.TRYGOTOLIST)
				{
					if (func!.FunctionCode != FunctionCode.FUNC && func.FunctionCode != FunctionCode.ENDFUNC)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidInstructionInSyntax.Text, baseFunc.Function.Name, func.Function.Name), func, 2, true, false);
						continue;
					}
				}
				else if (baseFunc.FunctionCode == FunctionCode.SELECTCASE)
				{
					if (baseFunc.IfCaseList!.Count == 0 && func!.FunctionCode != FunctionCode.CASE && func!.FunctionCode != FunctionCode.CASEELSE && func!.FunctionCode != FunctionCode.ENDSELECT)
					{
						ParserMediator.Warn(string.Format(trerror.OutsideSelectcase.Text, func.Function.Name), func, 2, true, false);
						continue;
					}
				}
			}
			switch (func!.FunctionCode)
			{
				case FunctionCode.REPEAT:
					foreach (InstructionLine iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.REPEAT)
						{
							ParserMediator.Warn(trerror.NestedRepeat.Text, func, 1, false, false);
						}
						else if (iLine.FunctionCode == FunctionCode.FOR)
						{
							VariableTerm cnt = (iLine.Argument as SpForNextArgment)!.Cnt;
							if (cnt.Identifier.Name == "COUNT" && cnt.isAllConst && cnt.getEl1forArg == 0)
							{
								ParserMediator.Warn(string.Format(trerror.RepeatInsideFor.Text, "0"), func, 1, false, false);
							}
						}
					}
					if (func.IsError)
						break;
					nestStack.Push(func);
					break;
				case FunctionCode.IF:
					nestStack.Push(func);
					func.IfCaseList = [];
					func.IfCaseList.AddFirst(func);
					break;
				case FunctionCode.SELECTCASE:
					nestStack.Push(func);
					func.IfCaseList = [];
					SelectcaseStack.Push(func);
					break;
				case FunctionCode.FOR:
					//ネストエラーチェックのためにコストはかかるが、ここでチェックする
					if (func.Argument == null)
						ArgumentParser.SetArgumentTo(func);
					//上で引数解析がなされていることは保証されているので、
					//それでこれがfalseになるのは、引数解析でエラーが起きた場合のみ
					if (func.Argument != null)
					{
						VariableTerm Cnt = (func.Argument as SpForNextArgment)!.Cnt;
						if (Cnt.Identifier.Name == "COUNT")
						{
							foreach (InstructionLine iLine in nestStack)
							{
								if (iLine.FunctionCode == FunctionCode.REPEAT && Cnt.isAllConst && Cnt.getEl1forArg == 0)
								{
									ParserMediator.Warn(string.Format(trerror.RepeatInsideFor.Text, "0"), func, 1, false, false);
								}
								else if (iLine.FunctionCode == FunctionCode.FOR)
								{
									VariableTerm destCnt = (iLine.Argument as SpForNextArgment)!.Cnt;
									if (destCnt.Identifier.Name == "COUNT" && Cnt.isAllConst && destCnt.isAllConst && destCnt.getEl1forArg == Cnt.getEl1forArg)
									{
										ParserMediator.Warn(string.Format(trerror.RepeatInsideFor.Text, Cnt.getEl1forArg.ToString()), func, 1, false, false);
									}
								}
							}
						}
					}
					if (func.IsError)
						break;
					nestStack.Push(func);
					break;
				case FunctionCode.WHILE:
				case FunctionCode.TRYCGOTO:
				case FunctionCode.TRYCJUMP:
				case FunctionCode.TRYCCALL:
				case FunctionCode.TRYCGOTOFORM:
				case FunctionCode.TRYCJUMPFORM:
				case FunctionCode.TRYCCALLFORM:
				case FunctionCode.DO:
					nestStack.Push(func);
					break;
				case FunctionCode.BREAK:
				case FunctionCode.CONTINUE:
					// 决策三-b：直接遍历 Stack（LIFO，从栈顶到栈底，与 [.. nestStack] 顺序一致），消除 InstructionLine[] 分配
					pairLine = null!;
					foreach (var iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.REPEAT
							|| iLine.FunctionCode == FunctionCode.FOR
							|| iLine.FunctionCode == FunctionCode.WHILE
							|| iLine.FunctionCode == FunctionCode.DO)
						{ pairLine = iLine; break; }
					}
					if (pairLine == null)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidLoopInstruction.Text, func.Function.Name), func, 2, true, false);
						break;
					}
					func.JumpTo = pairLine;
					break;

				case FunctionCode.ELSEIF:
				case FunctionCode.ELSE:
					{
						//1.725 Stack<T>.Last.Value()はStackが空の時はnullを返す仕様だと思いこんでおりました。
						InstructionLine? ifLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (ifLine == null || ifLine.FunctionCode != FunctionCode.IF)
						{
							ParserMediator.Warn(string.Format(trerror.InvalidElse.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						if (ifLine!.IfCaseList!.Last!.Value!.FunctionCode == FunctionCode.ELSE)
							ParserMediator.Warn(string.Format(trerror.InvalidElseAfterElse.Text, func!.Function.Name), func, 1, false, false);
						ifLine.IfCaseList.AddLast(func);
					}
					break;
				case FunctionCode.ENDIF:
					{
						InstructionLine? ifLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (ifLine == null || ifLine.FunctionCode != FunctionCode.IF)
						{
							ParserMediator.Warn(trerror.UnexpectedEndif.Text, func, 2, true, false);
							break;
						}
						foreach (var ifelseifLine in ifLine.IfCaseList!)
						{
							ifelseifLine.JumpTo = func;
						}
						nestStack.Pop();
					}
					break;
				case FunctionCode.CASE:
				case FunctionCode.CASEELSE:
					{
						InstructionLine? selectLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (selectLine == null || selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count == 0)
						{
							ParserMediator.Warn(string.Format(trerror.OutsideSelectcase.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						else if (selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count > 0)
						{
							do
							{
								ParserMediator.Warn(string.Format(trerror.InstructionNotClosed.Text, selectLine.Function.Name, FunctionIdentifier.getMatchFunction(selectLine.FunctionCode), func.Function.Name), func, 2, true, false);
								//これを跨いでIF等が閉じられることがないようにする。
								nestStack.Pop();
								//if (nestStack.Count > 0)　//空になってるかは下で判定できるので、これを見る必要がない
								selectLine = nestStack.Count == 0 ? null : nestStack.Peek(); //ちなみにnullになることはない（SELECTCASEがない場合は上で弾けるから）
							} while (selectLine != null && selectLine.FunctionCode != FunctionCode.SELECTCASE);
							break;
						}
						if (selectLine.IfCaseList.Count > 0 && selectLine.IfCaseList.Last!.Value.FunctionCode == FunctionCode.CASEELSE)
							ParserMediator.Warn(string.Format(trerror.InvalidCaseAfterCaseelse.Text, func.Function.Name), func, 1, false, false);
						selectLine.IfCaseList.AddLast(func);
					}
					break;
				case FunctionCode.ENDSELECT:
					{
						InstructionLine? selectLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (selectLine == null || selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count == 0)
						{
							ParserMediator.Warn(trerror.UnexpectedEndselect.Text, func, 2, true, false);
							break;
						}
						else if (selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count > 0)
						{
							do
							{
								ParserMediator.Warn(string.Format(trerror.InstructionNotClosed.Text, selectLine.Function.Name, FunctionIdentifier.getMatchFunction(selectLine.FunctionCode), func.Function.Name), func, 2, true, false);
								//これを跨いでIF等が閉じられることがないようにする。
								nestStack.Pop();
								//if (nestStack.Count > 0)　//空になってるかは下で判定できるので、これを見る必要がない
								selectLine = nestStack.Count == 0 ? null : nestStack.Peek(); //ちなみにnullになることはない（SELECTCASEがない場合は上で弾けるから）
							} while (selectLine != null && selectLine.FunctionCode != FunctionCode.SELECTCASE);
							//とりあえず、対応するSELECTCASE跨ぎは閉じる
							SelectcaseStack.Pop();
							//こっちでも抜かないとSELECTCASEが2つのENDSELECTに対応してしまう
							nestStack.Pop();
							break;
						}
						nestStack.Pop();
						SelectcaseStack.Pop();
						selectLine.JumpTo = func;
						if (selectLine.IsError)
							break;
						var term = (selectLine.Argument as ExpressionArgument)!.Term;
						if (term == null)
						{
							ParserMediator.Warn(trerror.MissingArg.Text, selectLine, 2, true, false);
							break;
						}
						foreach (var caseLine in selectLine.IfCaseList)
						{
							caseLine.JumpTo = func;
							if (caseLine.IsError)
								continue;
							if (caseLine.FunctionCode == FunctionCode.CASEELSE)
								continue;
							var caseExps = (caseLine.Argument as CaseArgument)!.CaseExps;
							if (caseExps.Length == 0)
								ParserMediator.Warn(trerror.MissingArg.Text, caseLine, 2, true, false);

							foreach (var exp in caseExps)
							{
								if (exp.GetOperandType() != term.GetOperandType())
									ParserMediator.Warn(trerror.NotMatchCaseTypeAndSelectcaseType.Text, caseLine, 2, true, false);
							}

						}
						//コンパイル時ジャンプテーブル構築（条件を満たさない場合は null → 実行時に線形スキャンへフォールバック）。
						//eager と lazy 物化（ReparseFunction）は同一の nestCheck 経路を共有するため、物化関数は自動で建表される。
						selectLine.SelectCaseJumpTable = SelectCaseJumpTable.TryBuild(selectLine, exm);
					}
					break;
				case FunctionCode.REND:
				case FunctionCode.NEXT:
				case FunctionCode.WEND:
				case FunctionCode.LOOP:
					FunctionCode parentFunc = FunctionIdentifier.getParentFunc(func.FunctionCode);
					//if (parentFunc == FunctionCode.__NULL__)
					//    throw new ExeEE("何か変？");
					if (nestStack.Count == 0
						|| nestStack.Peek().FunctionCode != parentFunc)
					{
						ParserMediator.Warn(string.Format(trerror.MissingCorresponding.Text, parentFunc.ToString(), func.Function.Name), func, 2, true, false);
						break;
					}
					pairLine = nestStack.Pop();//REPEAT
					func.JumpTo = pairLine;
					pairLine.JumpTo = func;
					break;
				case FunctionCode.CATCH:
					pairLine = (nestStack.Count == 0 ? null : nestStack.Peek())!;
					if (pairLine == null
						|| pairLine.FunctionCode != FunctionCode.TRYCGOTO
						&& pairLine.FunctionCode != FunctionCode.TRYCCALL
						&& pairLine.FunctionCode != FunctionCode.TRYCJUMP
						&& pairLine.FunctionCode != FunctionCode.TRYCGOTOFORM
						&& pairLine.FunctionCode != FunctionCode.TRYCCALLFORM
						&& pairLine.FunctionCode != FunctionCode.TRYCJUMPFORM)
					{
						ParserMediator.Warn(trerror.MissingTryc.Text, func, 2, true, false);
						break;
					}
					pairLine = nestStack.Pop();//TRYC
					pairLine.JumpToEndCatch = func;//TRYCにCATCHの位置を教える
					nestStack.Push(func);
					break;
				case FunctionCode.ENDCATCH:
					if (nestStack.Count == 0
						|| nestStack.Peek().FunctionCode != FunctionCode.CATCH)
					{
						ParserMediator.Warn(trerror.UnexpectedEndcatch.Text, func, 2, true, false);
						break;
					}
					pairLine = nestStack.Pop();//CATCH
					pairLine.JumpToEndCatch = func;//CATCHにENDCATCHの位置を教える
					break;
				case FunctionCode.PRINTDATA:
				case FunctionCode.PRINTDATAL:
				case FunctionCode.PRINTDATAW:
				case FunctionCode.PRINTDATAD:
				case FunctionCode.PRINTDATADL:
				case FunctionCode.PRINTDATADW:
				case FunctionCode.PRINTDATAK:
				case FunctionCode.PRINTDATAKL:
				case FunctionCode.PRINTDATAKW:
					{
						foreach (var iLine in nestStack)
						{
							if (iLine.Function.IsPrintData())
							{
								ParserMediator.Warn(trerror.NestedPrintdata.Text, func, 2, true, false);
								break;
							}
							if (iLine.FunctionCode == FunctionCode.STRDATA)
							{
								ParserMediator.Warn(trerror.StrdataInsidePrintdata.Text, func, 2, true, false);
								break;
							}
						}
						if (func.IsError)
							break;
						func.dataList = [];
						nestStack.Push(func);
						break;
					}
				case FunctionCode.STRDATA:
					{
						foreach (var iLine in nestStack)
						{
							if (iLine.FunctionCode == FunctionCode.STRDATA)
							{
								ParserMediator.Warn(trerror.NestedStrdata.Text, func, 2, true, false);
								break;
							}
							if (iLine.Function.IsPrintData())
							{
								ParserMediator.Warn(trerror.PrintdataInsideStrdata.Text, func, 2, true, false);
								break;
							}
						}
						if (func.IsError)
							break;
						func.dataList = [];
						nestStack.Push(func);
						break;
					}
				case FunctionCode.DATALIST:
					{
						var pline = nestStack.Count == 0 ? null : nestStack.Peek();
						if (pline == null || !pline.Function.IsPrintData() && pline.FunctionCode != FunctionCode.STRDATA)
						{
							ParserMediator.Warn(trerror.UnexpectedDatalist.Text, func, 2, true, false);
							break;
						}
						tempLineList = [];
						nestStack.Push(func);

						break;
					}
				case FunctionCode.ENDLIST:
					{
						if (nestStack.Count == 0 || nestStack.Peek().FunctionCode != FunctionCode.DATALIST)
						{
							ParserMediator.Warn(trerror.UnexpectedEndlist.Text, func, 2, true, false);
							break;
						}
						if (tempLineList.Count == 0)
							ParserMediator.Warn(trerror.DatalistDataIsMissing.Text, func, 1, false, false);
						nestStack.Pop();
						nestStack.Peek().dataList.Add(tempLineList);
						break;
					}
				case FunctionCode.DATA:
				case FunctionCode.DATAFORM:
					{
						InstructionLine pdata = (nestStack.Count == 0 ? null : nestStack.Peek())!;
						if (pdata == null || !pdata.Function.IsPrintData() && pdata.FunctionCode != FunctionCode.DATALIST && pdata.FunctionCode != FunctionCode.STRDATA)
						{
							ParserMediator.Warn(string.Format(trerror.MissingPrintdata.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						List<InstructionLine> iList = [];
						if (pdata.FunctionCode != FunctionCode.DATALIST)
						{
							iList.Add(func);
							pdata.dataList.Add(iList);
						}
						else
							tempLineList.Add(func);
						break;
					}
				case FunctionCode.ENDDATA:
					{
						InstructionLine pline = (nestStack.Count == 0 ? null : nestStack.Peek())!;
						if (pline == null || !pline.Function.IsPrintData() && pline.FunctionCode != FunctionCode.STRDATA)
						{
							ParserMediator.Warn(string.Format(trerror.MissingPrintdataStrdata.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						if (pline.FunctionCode == FunctionCode.DATALIST)
							ParserMediator.Warn(trerror.DatalistNotClosed.Text, func, 2, true, false);
						if (pline.dataList.Count == 0)
							ParserMediator.Warn(string.Format(trerror.InstructionDataIsMissing.Text, pline.Function.Name), func, 1, false, false);
						pline.JumpTo = func;
						nestStack.Pop();
						break;
					}
				case FunctionCode.TRYCALLLIST:
				case FunctionCode.TRYJUMPLIST:
				case FunctionCode.TRYGOTOLIST:
					foreach (InstructionLine iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.TRYCALLLIST || iLine.FunctionCode == FunctionCode.TRYJUMPLIST || iLine.FunctionCode == FunctionCode.TRYGOTOLIST)
						{
							ParserMediator.Warn(trerror.NestedTrycalllist.Text, func, 2, true, false);
							break;
						}
					}
					if (func.IsError)
						break;
					func.callList = [];
					nestStack.Push(func);
					break;
				case FunctionCode.FUNC:
					{
						InstructionLine pFunc = (nestStack.Count == 0 ? null : nestStack.Peek())!;
						if (pFunc == null ||
							pFunc.FunctionCode != FunctionCode.TRYCALLLIST && pFunc.FunctionCode != FunctionCode.TRYJUMPLIST && pFunc.FunctionCode != FunctionCode.TRYGOTOLIST)
						{
							ParserMediator.Warn(string.Format(trerror.MissingTrycalllist.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						if (func.Argument == null)
						{
							ParserMediator.Warn(string.Format(trerror.InvalidInstructionInTrycalllist.Text, func.Function.Name), pFunc, 2, true, false);
							break;
						}
						if (pFunc.FunctionCode == FunctionCode.TRYGOTOLIST)
						{
							var spCallArg = func.Argument as SpCallArgment;
							if (spCallArg!.SubNames.Count != 0)
							{
								ParserMediator.Warn(trerror.TrygotolistToSBrackets.Text, func, 2, true, false);
								break;
							}
							if (spCallArg.RowArgs.Count != 0)
							{
								ParserMediator.Warn(trerror.TrygotolistTargetHasArg.Text, func, 2, true, false);
								break;
							}
						}
						pFunc.callList.Add(func);
						break;
					}
				case FunctionCode.ENDFUNC:
					var pf = nestStack.Count == 0 ? null : nestStack.Peek();
					if (pf == null ||
						pf.FunctionCode != FunctionCode.TRYCALLLIST && pf.FunctionCode != FunctionCode.TRYJUMPLIST && pf.FunctionCode != FunctionCode.TRYGOTOLIST)
					{
						ParserMediator.Warn(string.Format(trerror.MissingTrycalllist.Text, func.Function.Name), func, 2, true, false);
						break;
					}
					pf.JumpTo = func;
					nestStack.Pop();
					break;
				case FunctionCode.NOSKIP:
					foreach (var iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.NOSKIP)
						{
							ParserMediator.Warn(trerror.NestedNoskip.Text, func, 2, true, false);
							break;
						}
					}
					if (func.IsError)
						break;
					nestStack.Push(func);
					break;
				case FunctionCode.ENDNOSKIP:
					var pfunc = nestStack.Count == 0 ? null : nestStack.Peek();
					if (pfunc == null ||
						pfunc.FunctionCode != FunctionCode.NOSKIP)
					{
						ParserMediator.Warn(string.Format(trerror.MissingNoskip.Text, func.Function.Name), func, 2, true, false);
						break;
					}
					//エラーハンドリング用
					pfunc.JumpTo = func;
					func.JumpTo = pfunc;
					nestStack.Pop();
					break;
			}

		}

		while (nestStack.Count != 0)
		{
			var func = nestStack.Pop();
			string funcName = func.Function.Name;
			string funcMatch = FunctionIdentifier.getMatchFunction(func.FunctionCode);
			if (func != null)
				ParserMediator.Warn(string.Format(trerror.MissingCorresponding.Text, funcMatch, funcName), func, 2, true, false);
			else
				ParserMediator.Warn(trerror.DefaultError.Text, func!, 2, true, false);
		}
		//使ったスタックをクリア
		SelectcaseStack.Clear();
	}

	private void setJumpTo(FunctionLabelLine label)
	{
		//3周目/3周
		//フロー制御命令のジャンプ先を設定
		LogicalLine nextLine = label;
		int depth = label.Depth;
		if (depth < 0)
			depth = -2;
		while (true)
		{
			nextLine = nextLine.NextLine;
			if (!(nextLine is InstructionLine func))
			{
				if (nextLine is NullLine || nextLine is FunctionLabelLine)
					break;
				continue;
			}
			if (func.IsError)
				continue;
			ScanLine(func);

			if (func.Function.Instruction != null)
			{
				string FunctionNotFoundName = null!;
				try
				{
					// D5#3：callform 标记改为返回值，由 MarkUseCallForm 决定落点——
					// 并行未调用清扫期间写入 per-label 产物，不再直写本实例字段。
					if (func.Function.Instruction.SetJumpTo(func, depth, ref FunctionNotFoundName))
						MarkUseCallForm();
				}
				catch (CodeEE e)
				{
					ParserMediator.Warn(e.Message, func, 2, true, false);
					continue;
				}
				if (FunctionNotFoundName != null)
				{
					if (!env.AnalysisMode)
						printFunctionNotFoundWarning(string.Format(trerror.NotDefinedFunc.Text, FunctionNotFoundName), func, 2, true);
					else
						printFunctionNotFoundWarning(FunctionNotFoundName, func, 2, true);
				}
				continue;
			}
			if (func.FunctionCode == FunctionCode.TRYCALLLIST || func.FunctionCode == FunctionCode.TRYJUMPLIST)
				MarkUseCallForm();
		}
	}

	/// <summary>
	/// erb-loader-parallel 08 D5#3：记录「用到 callform 系」，按上下文分流落点而非直写实例字段。
	/// 主循环（串行）下 <see cref="SweepArtCurrent"/> 为 null → 直写 <see cref="useCallForm"/>，
	/// 与改动前完全一致；未调用并行清扫期间写入 per-label 产物
	/// <see cref="UncalledSweepArt.UseCallForm"/>，由回放段在主线程合并。
	/// 并行区因此不再有对本实例字段的写入（原为 ref 直写，构成共享写）。
	/// </summary>
	private void MarkUseCallForm()
	{
		UncalledSweepArt? art = SweepArtCurrent.Value;
		if (art != null)
			art.UseCallForm = true;
		else
			useCallForm = true;
	}

	// erb-loader-parallel 03：原 GetErbFilesFromCache 孪生实现收敛至 Preload.GetFilesFromCache（扩展名 + 日志标签参数化）。
}
