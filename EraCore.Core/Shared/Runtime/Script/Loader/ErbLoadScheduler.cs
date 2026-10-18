using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// erb-loader-parallel 06：WP0 局部性实验用 GC 快照（累计分配量 / 各代收集次数）。
/// <see cref="Diff"/> 输出段内增量 + 当前堆大小，挂在 phase=genmerge / phase=parsescript
/// 日志行尾——判别"ParseScript 回归来自 GC 压力还是堆布局交错"（两段 GC 增量相近而耗时
/// 持续偏高 ⇒ GC 压力说排除）。调用均为廉价计数器读取，常态开销可忽略。
/// <para>ADR-0040：原为 <c>ErbLoader</c> 的私有嵌套 struct，因调度器外移且 <c>ErbLoader</c>
/// 自身仍有 3 处使用（phase 日志），提升为顶层 <c>internal</c>（纯移动，无状态依赖）。</para>
/// </summary>
internal readonly struct GcSnapshot
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

/// <summary>单个文件的生成结果：worker 永不向调用方抛异常，一切结果（含取消）显式承载。
/// Exception 非空且 Artifact 同非空 = 生成中途抛出但已有部分产物（主循环按序回放一次后抛原异常）。</summary>
internal sealed record GenerationResult(int Index, string Filename, ErbFileArtifact? Artifact, Exception? Exception, bool Canceled);

/// <summary>
/// ADR-0040：装载调度（C 组）——串行逐文件、有界滑动窗口并行，以及生成 worker 与合并驱动。
/// <para>
/// <b>接缝</b>：本模块<b>看不见</b>宿主字段。它只拿到三样东西——<see cref="ErbFileAssembler.Stats"/>
/// 的引用（worker 自增）、一个 <see cref="ErbFileAssembler.MergeOutcome"/> 回调（宿主据此累加
/// <c>enabledLineCount</c> / 落定 <c>noError</c>）、以及每次调用的进度回调（由宿主闭包捕获
/// <c>displayReport</c> / <c>starttime</c>）。这是 07 票「档 3」的延伸：标量与显示都留在宿主侧。
/// </para>
/// <para>
/// <b>不做</b>：装载编排（IR 复位、<c>AssembleContext</c> 时机、<c>LoadErbDir/LoadErbList</c> 的 façade）
/// 仍在 <c>ErbLoader</c>；装配仍是 <see cref="ErbFileAssembler"/>。本类只负责"给定 pending 列表，
/// 以何种并发形态生成并按序合并"。
/// </para>
/// </summary>
internal sealed class ErbLoadScheduler
{
	readonly ErbFileAssembler.Stats stats;
	readonly Action<ErbFileAssembler.MergeOutcome> applyOutcome;

	internal ErbLoadScheduler(ErbFileAssembler.Stats stats, Action<ErbFileAssembler.MergeOutcome> applyOutcome)
	{
		this.stats = stats;
		this.applyOutcome = applyOutcome;
	}

	// ── 测试接缝（handoff §8.1：夹具可注入失败点/延迟/observer；生产路径恒 null，零开销）──
	/// <summary>worker 开始生成前按文件名 await 的 gate（窗口/收束测试用）。hook 收到 worker 的取消令牌。</summary>
	internal Func<string, CancellationToken, Task>? TestGenerateGateHook;
	/// <summary>生成完成后按文件名决定注入的异常（返回 null 不注入）；artifact 保留以复现「有部分产物 + 异常」的按序回放路径。</summary>
	internal Func<string, Exception?>? TestGenerateFaultHook;
	/// <summary>worker 生命周期 observer：(phase, filename)，phase ∈ start/end/cancel；窗口上界与尾任务收束断言用。</summary>
	internal Action<string, string>? TestWorkerObserver;
	/// <summary>窗口宽度覆盖（null = <see cref="ErbLoadStrategy.FileWindow"/>，即 Min(核数, 8) 或旋钮档）。</summary>
	internal int? TestWindowOverride;

	/// <summary>
	/// 串行调度（erb-loader-parallel 03 fallback）：逐文件“生成 → 按序合并”，与 01 完成时的行为
	/// 完全一致——进度行在生成前打印、跨文件检查生成期即时、异常即回放再抛。
	/// </summary>
	internal async Task LoadSerial(List<(string Filename, string File)> pending, List<string> isOnlyEvent,
		ErbFileAssembler.Context ctx, Action<string> reportProgress)
	{
		foreach (var (filename, file) in pending)
		{
			reportProgress(filename);
			var art = await Task.Run(() => ErbFileAssembler.Generate(file, filename, isOnlyEvent,
				deferCrossFileChecks: false, replayOnFailure: true, ctx, stats));
			applyOutcome(ErbFileAssembler.Merge(art, isOnlyEvent, ctx));
		}
	}

	/// <summary>
	/// 并行调度（erb-bounded-lifecycle，重写自 erb-loader-parallel 03 的全量 Task 数组版）：
	/// 按索引推进的有界滑动窗口——运行中 worker 数与「已生成未合并产物数」共用同一上限
	/// <see cref="ErbLoadStrategy.MaxParallelErbFiles"/>（窗口宽度 = clamp(TestWindowOverride ?? Min(ProcessorCount, MaxParallelErbFiles), 1, pending.Count)），
	/// 不再为全部 pending 创建 Task/结果数组；合并一个文件后才放行下一个，artifact 驻留量有显式上界
	/// （旧实现首文件慢时未合并产物可驻留接近整个游戏——Android NativeAOT 上表现为 OOM 闪退/长 GC 卡死）。
	/// <para>
	/// 生命周期不变式：按序处理到某索引发现异常时，先回放该文件已有的控制台输出/警告一次，
	/// 再保留原异常类型与堆栈抛出；linked CTS 立即取消未启动与运行中 worker；finally 必须 await
	/// 全部已启动任务收束后才离开——旧实现 using SemaphoreSlim 在尾任务仍运行时 Dispose，
	/// 尾任务在已释放 gate 上 Release/WaitAsync 抛出无人观察的异常。取消产生的次生
	/// OperationCanceledException 只记录，不覆盖原始加载异常。
	/// </para>
	/// 生成期共享写仍集中在 <see cref="ErbFileAssembler.Merge"/>（主 async 流按序执行）；worker 经 per-task
	/// AsyncLocal（警告缓冲/ScanLineMirror）与 deferCrossFileChecks 空视图做到无共享写。进度行随合并按序补发。
	/// </summary>
	internal async Task LoadParallel(List<(string Filename, string File)> pending, List<string> isOnlyEvent,
		ErbLoadStrategy strategy, ErbFileAssembler.Context ctx, Action<string> reportProgress,
		CancellationToken externalCt)
	{
		stats.Reset();
		if (pending.Count == 0)
			return;
		int window = Math.Clamp(TestWindowOverride ?? strategy.FileWindow, 1, pending.Count);
		// 06-K1 自证：knob= 壳层请求词表值，window= 生效值（auto=与无旋钮逐位一致）——取证以此对账
		EmueraLog.Info("ErbLoader", $"LoadPendingParallel: files={pending.Count}, window={window} knob={strategy.DegreeKnobString}");
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
		// ADR-0035：ctx 由装载入口（LoadErbDir/LoadErbList）传入——worker 线程不回读宿主字段
		//（生成期 deferCrossFileChecks=true 本就不碰 labelDic，此处连字段读也一并免掉）。

		try
		{
			while (merged < pending.Count)
			{
				// 填窗：合并掉一个才放行下一个——active worker 与未合并产物共用同一上限
				while (nextToStart < pending.Count && running.Count < window)
				{
					int idx = nextToStart++; // 闭包陷阱：捕获本迭代独立副本
					running[idx] = Task.Run(() => RunGenerateWorkerAsync(idx, pending[idx].Filename, pending[idx].File, ctx, cts.Token));
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
						ErbFileAssembler.ReplayConsoleOps(result.Artifact, ctx);
						ParserMediator.EmitWarningBuffer(result.Artifact.Warnings);
					}
					throw result.Exception;
				}
				reportProgress(result.Filename);
				applyOutcome(ErbFileAssembler.Merge(result.Artifact!, isOnlyEvent, ctx));
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
				$"LoadPendingParallel(issues/17): irStub={Volatile.Read(ref stats.IrStubFiles)} irNoFunc={Volatile.Read(ref stats.IrNoFuncFiles)} fallbackSrc={Volatile.Read(ref stats.SourceFallbackFiles)} fallbackOnIrHit={Volatile.Read(ref stats.SourceFallbackOnIrHit)}");
		}
	}

	/// <summary>worker 主体：gate hook → 取消检查 → 生成 → fault 注入点。永不抛出——一切结果进 <see cref="GenerationResult"/>；
	/// 测试观察点 start/end/cancel 经 <see cref="TestWorkerObserver"/> 上报（含失败与取消路径，收束断言依据）。</summary>
	// 注：本方法是 async，故 <see cref="ErbFileAssembler.Context"/> 走按值传参（CS1988 禁止
	// 异步方法带 in/ref/out 参数）。Context 是 7 个引用的 readonly struct，拷贝成本可忽略。
	async Task<GenerationResult> RunGenerateWorkerAsync(int idx, string filename, string filepath,
		ErbFileAssembler.Context ctx, CancellationToken ct)
	{
		TestWorkerObserver?.Invoke("start", filename);
		try
		{
			if (TestGenerateGateHook != null)
				await TestGenerateGateHook(filename, ct).ConfigureAwait(false);
			ct.ThrowIfCancellationRequested();
			var art = ErbFileAssembler.Generate(filepath, filename, [], deferCrossFileChecks: true,
				replayOnFailure: false, ctx, stats);
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
}
