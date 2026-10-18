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

	// 决策三-a：nestCheck 栈/列表复用 — 1000 函数避免 3000+ 次堆分配。
	// loadErb/ParseScript 为单线程调用，字段在 nestCheck 入口 Clear() 即可安全复用。

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
	/// <summary>
	/// ADR-0033：本轮装载的 IR 流水线实例（装载期创建，<see cref="ResetIrState"/> 或收尾后释放）。
	/// 它接替原 <c>_irWriter</c> 字段的槽位——并行 worker 侧仅以
	/// <see cref="ErbIrLoadPipeline.CollectIr"/> 这一个**只读布尔**读它，写器本体与其生命周期
	/// 完全内化于流水线（决策一）。
	/// </summary>
	private ErbIrLoadPipeline? _irPipeline;

	// issues/17 A′ 的「命中轮待载文件全集」已随第四刀（ADR-0027 决策二.4）收进 IrState.AllFiles——
	// 它只对应当前 _ir，作为宿主独立字段存活需两道人工同步（赋值 + ResetIrState 置空），
	// 属"清理滞后"异味；收进对象后随 _ir 一起生灭。

	// ===== S0-M2 取证探针（2026-09-05，diagnostic-knobs issues/11）：加载窗 blob I/O 分账 =====
	// parsescript 段内所有 blob 读取都经 ReadBlobMeasured（ComputeSignature / ReparseFunctionFromIr
	// 两处咽喉）累计；LoadErbDir 起始清零，phase=parsescript 日志行输出 blobReads/blobBytes/blobMs。
	// 运行期（加载完成后）懒物化同样吃累计，但该日志只在加载窗打一次，不影响「blob I/O vs 物化」口径。
	private long _blobReadTicks;
	private long _blobReadBytes;
	private int _blobReadCount;

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
			long t1 = Stopwatch.GetTimestamp();
			_blobReadTicks += t1 - t0;
			_blobReadBytes += entry.BlobLength;
			_blobReadCount++;
			// issue 07 P1a：段内 blob 读（签名预算触发的那些）单独记一笔——blob 总量减去它才是「段外预读」
			// （ReparseFunctionFromIr 在进入 MaterializeFunction 之前读的那一次），对账 main_wall 时不能重复扣。
			if (_materializeDiag is { } md && ParserProbe.Scope != ParserProbeScope.None)
				md.BlobInSegTicks += t1 - t0;
		}
	}

	/// <summary>
	/// b2-irdiskcache：清空本会话 IR 状态并显式关闭常驻读句柄（issues/05 句柄常驻的关闭点——
	/// reload / 加载失败 / 部分重载的既有 <c>_ir = null</c> 置空处统一走这里）。流水线实例一并回收，
	/// 其生成期写器经 <see cref="ErbIrLoadPipeline.Abort"/> 清 tmp（异常路径不留下半截缓存）。
	/// <para>
	/// ADR-0033 决策二：<c>_ir</c>（含 <see cref="IrState.AllFiles"/>）是宿主持有的跨装载状态、写器是流水线内部状态——
	/// 故本方法不迁入流水线，而是两者的**统一失效点**（流水线不反向触碰宿主字段）。
	/// </para>
	/// </summary>
	private void ResetIrState()
	{
		_ir?.CloseHandle();
		_ir = null;
		_irPipeline?.Abort();
		_irPipeline = null;
		// issues/17 A′ 的「命中轮全集」现为 IrState.AllFiles——随 _ir 置空自动失效，无需单独清理。
	}

	// erb-loader-parallel 03 / 06-K1：并行度常量上限与其决策点已随第一刀（ADR-0027 决策二.1）迁入
	// ErbLoadStrategy.MaxParallelErbFiles / FileWindow·SweepDegree（真值表穷举于 ErbLoadStrategyTests）——
	// 原两处消费点（LoadPendingParallel 窗口 + RunUncalledSweepParallel 清扫度）改由策略快照单点提供。

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
		// <see cref="ErbIrLoadPipeline.ShouldUseIrCache"/>；content-URI 游戏经方案 B 镜像到 app 私有存储，读放大止血已解除，
		// 见 ErbIrCache 的 GetIrCachePath 注释）。启动进入文件读取前一次性判定：命中 IR（Available=true，
		// 索引驻留）→ 本轮免源扫描直建桩；未命中/失效 → B1 源路径跑本轮，收尾重建 IR。
		// 指纹基于 pending 文件集合 + firstDir 清单 + 解析相关配置（design §6）。
		// ADR-0027 第四刀（issue 05）：IR 路径的 ambient（开关 / 读侧旋钮 / 指纹配置 / Rename 表 / 缓存路径基座）
		// 在此**一次快照**为显式上下文，此后本方法与流水线内各处只问对象（形状同第一刀的 ErbLoadStrategy）。
		var irCtx = IrLoadContext.FromCurrent(env);
		string irPath = irCtx.IrCachePath();
		bool irEnabled = irCtx.ShouldUseIrCache;
		// issues/12 M4：命中轮清单构建前先读 IR 头存 firstDir 清单（读侧受回退旋钮门控，FALSE = 恒扫描；
		// 无 v2 IR / 损坏 → null → 照旧扫描）——与 IsIrHitRound 预判定同源同拨。
		List<string>? irFirstDirs =
			irEnabled && irCtx.FirstDirListEnabled && ErbIrCache.TryReadFirstDirs(irPath, out var loadFd) ? loadFd : null;
		// issues/13 M5：sidecar 清单（与 IsIrHitRound 同源同拨；陈旧自愈见下方 discard-rescan 分支）。
		// issues/14 B：legacy 标记供命中轮收尾自愈重写（CSV 尾段缺失 → CSV 冷枚举一次后升级格式）。
		List<(string Logical, string RelToRoot)>? irFileList = null;
		bool irSidecarLegacy = false;
		if (irEnabled && irCtx.FileListCacheEnabled && SafPath.IsContentUri(env.ErbDir)
			&& ErbIrCache.TryReadFileListFull(irPath, out var loadFl, out _, out _, out var loadLegacy))
		{
			irFileList = loadFl;
			irSidecarLegacy = loadLegacy;
		}
		// issue 09：待载清单构建唯一路径（缓存命中轮与启动 IR 预判定共用；命中轮无缓存 →
		// Preload 语义枚举，见 <see cref="ErbIrLoadPipeline.BuildPendingList"/>）。
		List<(string Filename, string File)> pending = ErbIrLoadPipeline.BuildPendingList(env, irCtx, irFirstDirs, out List<string> firstDirs, out int erbFileCount, irFileList);
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
			// ADR-0033：装载期判定与建库（命中建状态 / miss 自愈重扫 / miss 开写器）整体内化到流水线。
			// 结果对象带回**最终** pending/firstDirs/erbFileCount（miss 自愈兜底可能重建过清单），宿主只并入。
			_irPipeline = new ErbIrLoadPipeline(env, irCtx);
			var irPrep = _irPipeline.Prepare(irEnabled, irPath, pending, firstDirs, erbFileCount, irFirstDirs, irFileList);
			pending = irPrep.Pending;
			firstDirs = irPrep.FirstDirs;
			erbFileCount = irPrep.ErbFileCount;
			_ir = irPrep.State;
			// ReduceArgumentOnLoad=ONCE 的"上次启动以来源有没有变"判据寄生到 IR 指纹：
			// 命中 = 指纹一致 = 源未变（本地路径下指纹含逐文件 mtime）→ 跳过全量参数解析；
			// 未命中（首次运行／源变更／IR 未启用）→ 强制全量参数解析。必须在任何参数解析之前
			// （生成期与 ParseScript 三遍都在此后），故此处是唯一正确的位置。
			ConfigData.Current!.ApplyReduceArgumentOnLoad(
				Config.Config.ReduceArgumentOnLoad,
				irHit: irPrep.Enabled && irPrep.Hit);
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
			// ADR-0027 决策二.1（第一刀）：本轮装载的并行性策略——genmerge（文件级）与未调用遍历清扫
			//（标签级）两处路径选择、两处度数（生成窗口 / 清扫度）与观测词表全部由它单点归属；
			// ambient（配置 / 平台 / 核数）只在此读一次，此后各处只问对象。
			var strategy = ErbLoadStrategy.FromCurrent(env);
			// ADR-0035：生成期的全部 ambient（依赖 + 配置）在此一次快照，位置须在 _ir/_irPipeline
			// 赋值（215/220）之后、genmerge 之前——与紧邻的 IrLoadContext.FromCurrent(env) 同一节拍。
			var ctx = AssembleContext();
			var phaseSw = Stopwatch.StartNew();
			var gcBeforeGenmerge = GcSnapshot.Take();
			// ADR-0040：进度行由宿主闭包捕获 displayReport / starttime，调度器只见文件名
			Action<string> reportProgress = filename => PrintLoadingProgress(displayReport, starttime, filename);
			if (strategy.ParallelGenmerge)
				await Scheduler.LoadParallel(pending, isOnlyEvent, strategy, ctx, reportProgress, externalCt);
			else
				await Scheduler.LoadSerial(pending, isOnlyEvent, ctx, reportProgress);
			phaseSw.Stop();
			EmueraLog.Info("ErbLoader", $"phase=genmerge parallel={strategy.ParallelGenmerge} files={pending.Count} ms={phaseSw.ElapsedMilliseconds} {GcSnapshot.Diff(gcBeforeGenmerge)}");
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
			await Task.Run(() => ParseScript(strategy), CancellationToken.None);
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

			// b2-irdiskcache：本轮生成期收集完毕 → 流水线收尾（Complete 写器 + sidecar 写 + legacy 自愈）。
			// issues/12 M4：firstDirs = 本轮实际清单——兜底重扫后 firstDirs 已是新鲜值。
			// 生成失败静默（进程继续走 B1 路径，下次再试；design §5.1 异常策略），成功即下次启动命中。
			// ADR-0033 决策三：显式调用——本行必须位于 ParseScript 之后，禁用 using/Dispose 收尾。
			_irPipeline?.Complete(irPath, irPrep.Fingerprint, firstDirs, pending, irPrep.Hit, irSidecarLegacy);

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
		// 单文件增量失效不在 v1 范围（design §6「不做」），本路不生成 IR 写器。
		ResetIrState();
		// ADR-0035：装载入口一次快照（须在 ResetIrState 之后——部分重载不走 IR，_ir 恒 null）。
		var ctx = AssembleContext();

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
				var art = ErbFileAssembler.Generate(fpath, fname, isOnlyEvent, deferCrossFileChecks: false,
					replayOnFailure: true, ctx, _genStats);
				ApplyMergeOutcome(ErbFileAssembler.Merge(art, isOnlyEvent, ctx));
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
		await Task.Run(() => ParseScript(ErbLoadStrategy.FromCurrent(env)));
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

	#region erb-bounded-lifecycle：有界滑动窗口调度（handoff §3）——实现已外移 ADR-0040

	/// <summary>
	/// ADR-0040：装载调度器（串行 / 有界窗口 / worker）。裸移动：主体代码逐字未改，
	/// 仅把 <c>_genStats</c> / <c>applyOutcome</c> / 进度行改为经构造与调用参数传入。
	/// <para>延迟创建：测试在 <c>new ErbLoader(...)</c> 之后就设钩子，那时才实例化。</para>
	/// </summary>
	private ErbLoadScheduler? _scheduler;
	private ErbLoadScheduler Scheduler => _scheduler ??= new ErbLoadScheduler(_genStats, ApplyMergeOutcome);

	// ── 四个测试钩子的转发（测试写法零改动；生产路径恒 null，零新增分支）──
	/// <summary>worker 开始生成前按文件名 await 的 gate（窗口/收束测试用）。hook 收到 worker 的取消令牌。</summary>
	internal Func<string, CancellationToken, Task>? TestGenerateGateHook
	{
		get => Scheduler.TestGenerateGateHook;
		set => Scheduler.TestGenerateGateHook = value;
	}
	/// <summary>生成完成后按文件名决定注入的异常（返回 null 不注入）；artifact 保留以复现「有部分产物 + 异常」的按序回放路径。</summary>
	internal Func<string, Exception?>? TestGenerateFaultHook
	{
		get => Scheduler.TestGenerateFaultHook;
		set => Scheduler.TestGenerateFaultHook = value;
	}
	/// <summary>worker 生命周期 observer：(phase, filename)，phase ∈ start/end/cancel；窗口上界与尾任务收束断言用。</summary>
	internal Action<string, string>? TestWorkerObserver
	{
		get => Scheduler.TestWorkerObserver;
		set => Scheduler.TestWorkerObserver = value;
	}
	/// <summary>窗口宽度覆盖（null = <see cref="ErbLoadStrategy.FileWindow"/>，即 Min(核数, 8) 或旋钮档）。</summary>
	internal int? TestWindowOverride
	{
		get => Scheduler.TestWindowOverride;
		set => Scheduler.TestWindowOverride = value;
	}



	// issues/17 genmerge 逐文件分账（「IR 桩 / 无函数 / 回落读源」）。并行 worker 写入，故用 Interlocked。
	// 保留为常驻观测而非一次性探针：它是 A′ 的体检指标——fallbackSrc 若回升到与文件数同量级，
	// 说明"在全集内却无 IR 条目"的判据失效（如全集构建异常），无需重跑取证即可发现。
	// 取证期的临时探针（三段 Stopwatch 计时 + 落源文件名样本）已随 issue 17 结论落定拆除。
	// ADR-0034：四个计数随「生成」一并外置为 <see cref="ErbFileAssembler.Stats"/>——worker 线程
	// 自增它、宿主持有并读取（传引用而非回传，避免改动"并发自增"这一语义）。
	private readonly ErbFileAssembler.Stats _genStats = new();

	/// <summary>
	/// ADR-0034：<see cref="ErbFileAssembler"/> 的依赖快照。
	/// <para>
	/// <b>ADR-0035 决策一/四：改为在装载入口调用一次</b>（<see cref="LoadErbDir"/> /
	/// <see cref="LoadErbList"/>），此后整轮装载共用同一份——包含配置在内全部冻结
	/// （与 <see cref="IrLoadContext.FromCurrent"/> 同款"装载入口一次快照"）。
	/// </para>
	/// <b>调用时机有约束</b>：必须在 <c>_ir</c> / <c>_irPipeline</c> 赋值之后
	/// （<see cref="LoadErbDir"/> 内为 215/220 行之后），否则快照里的 IR 会是上一轮的残留。
	/// </summary>
	private ErbFileAssembler.Context AssembleContext()
		=> new(env, idDic, output, setScanLine, labelDic, _ir, _irPipeline,
			Config.Config.UseRenameFile, ParserMediator.RenameDic != null, Config.Config.StrComper,
			Config.Config.LazyFunctionLoad, Config.Config.WarnNormalFunctionOverloading,
			Config.Config.PrintCPerLine, JSONConfig.Data.UseNewRandom);

	/// <summary>应用 <see cref="ErbFileAssembler.MergeOutcome"/>——档 3 的宿主侧残留义务（每处 3 行）。</summary>
	private void ApplyMergeOutcome(ErbFileAssembler.MergeOutcome outcome)
	{
		enabledLineCount += outcome.EnabledLines;
		if (outcome.HasError)
			noError = false;
	}

	#endregion

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
	/// 运行期读缝（ADR-0027 决策三）：原先分散的两个静态（<c>SweepActive</c> 短路 + <c>SweepScanLine</c> 读）
	/// 收敛成的**单一** internal static 解析入口——"运行期读加载期状态"这一事实从此只在本方法可见，
	/// 槽本体是 <see cref="ParseContext"/> 的私有细节。运行期唯一外部读者
	/// （<see cref="LogicalLineParser.GetLoadScaningLine"/>）转调本方法。
	/// </summary>
	internal static LogicalLine? TryGetLoadScanLine() => ParseContext.CurrentLoadScanLine;


	/// <summary>
	/// D5#6：解析行写点。落点由**显式传入的 ctx** 决定（ADR-0027 决策二.2：不再直写 ThreadLocal /
	/// 实例字段）：串行/回放 ctx 直写全局 sink（行为不变）；并行 ctx 写其 per-线程发布槽
	///（全局 sink 无并发写；sweep 收尾 setScanLine(null!) 对齐串行终态——串行下每个
	/// ParseFunctionWithCatch 的 finally 都写 null，中间行值仅解析期读者可见，已由
	/// <see cref="TryGetLoadScanLine"/> 重定向覆盖）。
	/// </summary>
	private void ScanLine(LogicalLine line, ParseContext ctx)
	{
		if (ctx.IsParallel)
			ParseContext.PublishScanLine(line);
		else
			setScanLine(line);
	}

	/// <summary>
	/// ParseScript 诊断累计（erb-loader-parallel 05）：仅 DisplayReport 开启时创建，默认路径零开销。
	/// 三步分段（setArgument/nestCheck/setJumpTo）按"主循环 / 未调用遍历"两桶累计 Tick，
	/// 供 D4 策略预注册判据（spec-phase2 R2-Q2）取数。
	/// </summary>

	// NativeAOT Android 止血门 + ADR-0026 平台门策略（Auto/ForceOn/ForceOff）的判定已随第一刀
	// （ADR-0027 决策二.1）迁入 ErbLoadStrategy.ParallelSweep（私有实现 + 24 组合真值表穷举于
	// ErbLoadStrategyTests）——原先由本类静态谓词承担，第二处选择点（genmerge）自有判据，现已归一。



	/// <summary>当前物化分账桶（仅 ParseScript 主循环期间非 null；运行期物化不采集）。</summary>
	private MaterializeDiag? _materializeDiag;

	/// <summary>
	/// 事前処理したファイルをさらに解析し実行可能な状態にする。
	/// <paramref name="strategy"/> 由装载入口（LoadErbDir / LoadErbList）构造下传——
	/// ADR-0027 决策二.1：并行性判定不在本方法体内装配（第一刀），也不经实例字段读取（显式依赖）。
	/// </summary>
	private void ParseScript(ErbLoadStrategy strategy)
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
		ThreePass.ParseDiag = diag;
		// issue 07 P1a/P1b：物化分账（无条件采集与发射——lazy 命中轮不走 ParseFunctionWithCatch，
		// 现有 parsescript_diag 的 steps 桶恒空，故这条线独立于 DisplayReport）。
		MaterializeDiag matDiag = new();
		_materializeDiag = matDiag;
		ParserProbe.Activate();
		long mainTicks = 0, callFormTicks = 0, uncalledTicks = 0;
		SortedDictionary<int, int>? depthHist = null;
		// issue 07 P1a：主循环墙钟改为无条件测量——materialize_diag 的 main_wall_ms 对账需要它，
		// 不再只在 DisplayReport 下才有值（唯一 Stopwatch 分配，量级可忽略）。
		Stopwatch? mainSw = Stopwatch.StartNew();
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
					Materializer.MaterializeLabel(label);
				else if (!label.ThreePassed)
					ThreePass.ParseFunctionWithCatch(label, ParseContext.Serial);
			}
			if (countInDepth == 0)
				break;
		}
		mainSw?.Stop();
		mainTicks = mainSw?.ElapsedTicks ?? 0;
		labelDepth = -1;
		// 关窗（累计值保留）后立即发射：作用域严格 = 主循环物化波，不含随后的未调用遍历 / 警告回放。
		ParserProbe.Deactivate();
		_materializeDiag = null;
		// ADR-0041：诊断行与 FNF 决策外移到 ErbParseDiagnostics；blob 读账按值传入（模块不持有本类字段）
		ErbParseDiagnostics.EmitMaterializeDiag(matDiag, mainTicks, _blobReadTicks, _blobReadBytes, _blobReadCount);
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
		// ADR-0027 决策二.1（第一刀）：门的输入装配与判定已归 ErbLoadStrategy——本方法体内不再有
		// 路径选择的散落状态，只问对象。字段名与顺序逐字不变（parsescript_policy 是取证接口）。
		// 06-K1/K2 自证：sweep_deg= 未调用遍历清扫度上限（与本轮 RunUncalledSweepParallel 同值，
		// 由策略的 FileWindow/SweepDegree 同源提供），dimlock= privateDimList 锁策略（非路径选择，
		// 故不归策略对象）。时序前提：dimlock= 在 ParseScript 时读 Config，而实际锁行为是
		// IdentifierDictionary 构造时快照——壳层 OnReloadGame 先 Apply 旋钮再建会话，两者恒一致；
		// 若未来出现先于 Apply 的 idDic 构造路径，此字段会失真，需改为由 idDic 直接上报
		DimLockPolicy dimLockPolicy = Config.Config.OverrideDimLockPolicy ?? DimLockPolicy.Auto;
		EmueraLog.Info("ErbLoader",
			$"parsescript_policy configured={strategy.SweepConfigured} effective={strategy.ParallelSweep} android={strategy.IsAndroid} analysis={strategy.AnalysisMode} policy={strategy.SweepPolicyString} sweep_deg={strategy.SweepDegree} dimlock={DimLockPolicyCodec.ToConfigString(dimLockPolicy)}");
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
				if (strategy.ParallelSweep)
				{
					// 并行段：解析体全部入 per-label 缓冲（警告 / FNF 事件），回放段按 labelList 序重放
					// ADR-0042：回放外移；FNF 重走经回调（它绑着 ParseContext，仍在宿主），callform 标记由返回值交回
					if (ErbUncalledSweepReplay.ReplayAll(
						ErbUncalledSweepRunner.RunParallel(labelList, labelDepth, strategy.SweepDegree, ThreePass, setScanLine, () => useCallForm = true),
					ThreePass.ReplayFnf))
						useCallForm = true;
					sweepParallelRan = true;
				}
				else
				{
					foreach (FunctionLabelLine label in labelList)
					{
						if (label.Depth != labelDepth)
							continue;
						ThreePass.ParseFunctionWithCatch(label, ParseContext.Serial);
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
			UncalledSweepArt[]? sweepArts = !lazyMode && strategy.ParallelSweep && !ignoreUncalledFunction
				? ErbUncalledSweepRunner.RunParallel(labelList, labelDepth, strategy.SweepDegree, ThreePass, setScanLine, () => useCallForm = true)
				: null;
			int artIdx = 0;
			foreach (FunctionLabelLine label in labelList)
			{
				if (label.Depth != labelDepth)
					continue;
				//解析モード時は呼ばれなかったものをここで解析
				if (env.AnalysisMode)
					ThreePass.ParseFunctionWithCatch(label, ParseContext.Serial);
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
								// 原 ParseFunctionWithCatch(label) 的位置：按序回放该 label 的缓冲（ADR-0042）
								if (ErbUncalledSweepReplay.Replay(sweepArts[artIdx], ThreePass.ReplayFnf))
									useCallForm = true;
								artIdx++;
							}
							else
								ThreePass.ParseFunctionWithCatch(label, ParseContext.Serial);
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
			if (ThreePass.IgnoredFnfCount > 0 && Config.Config.DisplayWarningLevel <= 2 && notCalledWarning != DisplayWarningFlag.IGNORE)
				output.PrintError(string.Format(trerror.IgnoredUndefinedFuncCall.Text, ThreePass.IgnoredFnfCount));
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
		ThreePass.ParseDiag = null;
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



	/// <summary>AnalysisMode 下的解析告警计数（宿主持有：主循环结束时由 ParseScript 读它出报告）。</summary>
	public Dictionary<string, long> warningDic = [];

	/// <summary>
	/// ADR-0044（D 组第三刀 · C3b）：单标签三遍解析 + FNF 决策。延迟创建——Context 里两个引用
	/// （<c>warningDic</c> 公开字段、<c>setScanLine</c> 委托）都是本类的实例成员。
	/// </summary>
	private ErbThreePassParser? _threePass;
	private ErbThreePassParser ThreePass => _threePass ??= new ErbThreePassParser(new ErbThreePassParser.Context(
		env.AnalysisMode, exm, ScanLine, warningDic, () => useCallForm = true));

	/// <summary>
	/// ADR-0047（E 组 · C-E2b）：惰性物化引擎。延迟创建——Context 里 <c>labelDic</c> / <c>_filePathByLogical</c> /
	/// <c>_ir</c> 都是本类的实例状态，且 <c>_materializeDiag</c> 每次装载会被重挂（故以委托实时读，不缓存快照）。
	/// </summary>
	private ErbMaterializer? _materializer;
	private ErbMaterializer Materializer => _materializer ??= new ErbMaterializer(new ErbMaterializer.Context(
		env, output, setScanLine, idDic, labelDic, _filePathByLogical,
		(string logical, string labelName, int lineNo, out IrBlob blob) =>
		{
			if (_ir is { Available: true } ir && ir.Index.TryGetValue((logical, labelName, lineNo), out var entry))
			{
				blob = ReadBlobMeasured(ir, entry);
				return true;
			}
			blob = default;
			return false;
		},
		() => _materializeDiag, parseLabel, setLabelsArg, ScanLine, ThreePass));

	/// <summary>
	/// 外部签名保持：<see cref="ReparseFunction"/> 随物化引擎搬入 <see cref="ErbMaterializer"/>，
	/// 这里保留同名转发（测试与 IVT 调用方零改动，13 票验收项「外部签名零变更」）。
	/// </summary>
	internal FunctionLabelLine? ReparseFunction(FunctionLabelLine stub, string filepath)
		=> Materializer.ReparseFunction(stub, filepath);








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
		GlobalStatic.Process?.RuntimeLoader?.Materializer.MaterializeLabel(label);
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
			loader.Materializer.ComputeSignature(stub);
		}
		catch
		{
			// 签名计算失败不在此抛（后续物化三态降级），确保 arg 转换路径对损坏 blob 不崩新异常。
		}
	}

	/// <summary>加载期（ParseScript 主循环）arg 解析物化用的当前 loader；运行时恒经 RuntimeLoader，本槽仅加载中回退。</summary>
	private static ErbLoader? _activeArgLoader;


	// erb-loader-parallel 03：原 GetErbFilesFromCache 孪生实现收敛至 Preload.GetFilesFromCache（扩展名 + 日志标签参数化）。
}
