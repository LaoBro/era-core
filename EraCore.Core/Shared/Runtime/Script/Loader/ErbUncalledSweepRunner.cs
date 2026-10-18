using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// ADR-0045（D 组第三刀 · C3c）：未调用遍历的**生成侧**并行段（回放侧在
/// <see cref="ErbUncalledSweepReplay"/>）。
/// <para>
/// <b>接缝</b>：解析动作走 <see cref="ErbThreePassParser"/>（它同时提供 `ParseDiag` 的挂载点）、
/// 收尾的 scan-line 复位走 <c>setScanLine</c> 委托、callform 标记走 <c>onUseCallForm</c> 回调。
/// 本类不持有宿主字段。
/// </para>
/// </summary>
internal static class ErbUncalledSweepRunner
{	/// <summary>
	/// erb-loader-parallel 08：未调用遍历并行段（spec-phase2 D4-1 / 调度框架 R3-Q5）。
	/// <see cref="Parallel"/>.ForEach(Partitioner 范围分区, Min(ProcessorCount, 8))——Partitioner 均载 +
	/// 度数封顶（度数硬编码不可配，R3-Q11）；per-label 产物（警告缓冲 / FNF 事件）按 labelList 序
	/// 存入返回数组，回放段串行重放。共享状态处置对照 D5 清单：#1 警告 per-label 缓冲（Begin/EndWarningBuffer）；
	/// #2 FNF 事件拦截 + #3 useCallForm（第二刀后**同经 <see cref="ParseContext"/> 的 per-label 产物落点**，
	/// 原 ThreadLocal <c>SweepArtCurrent</c> 已删除，见 MarkUseCallForm / printFunctionNotFoundWarning）；
	/// #5 nestCheck 集合局部化（nestCheck 内部，按 <see cref="ParseContext.IsParallel"/> 判）；
	/// #6 scaningLine 写点（<see cref="ScanLine"/>：第二刀后写 <see cref="ParseContext"/> 的 per-线程发布槽，
	/// 原 <c>SweepScanLine</c> 静态已删除）；
	/// #7 Depth check-then-set 同层同值良性竞争（事实 B）；#8 并行段跳过 per-label 分步计时（_parseDiag 置 null，
	/// phases 总时长计时在调用方）。异常语义（R3-Q9）：先按序回放已产生的缓冲（已解析 label 的警告不丢）
	/// 再重抛 InnerExceptions[0]，由外层 LoadErbDir 既有 catch 统一处理。
	/// </summary>
	internal static UncalledSweepArt[] RunParallel(List<FunctionLabelLine> labelList, int labelDepth, int maxDegree,
		ErbThreePassParser threePass, Action<LogicalLine> setScanLine, Action onUseCallForm)
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
		threePass.ParseDiag = null;
		ParseContext.Active = true;
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
						// ADR-0027 决策二.2：per-label 解析上下文**显式建立并穿参**（取代原 SweepArtCurrent
						// ThreadLocal —— worker 不再直写任何 ambient 槽）。
						var ctx = ParseContext.ForLabel(art);
						ctx.Enter();
						try
						{
							threePass.ParseFunctionWithCatch(targets[i], ctx);
						}
						finally
						{
							ParseContext.ExitLabel();
							ParserMediator.EndWarningBuffer();
						}
					}
				}
				finally
				{
					// 线程池线程复用防残留：本 range 结束后清 per-线程 slot（否则该线程后续跑 runtime
					// 代码时发布槽非 null，读者会读到 stale 行）
					ParseContext.ClearThreadSlots();
				}
			});
		}
		catch (AggregateException agg)
		{
			if (ErbUncalledSweepReplay.ReplayAll(arts, threePass.ReplayFnf))
				onUseCallForm();
			throw agg.InnerExceptions[0];
		}
		finally
		{
			ParseContext.Active = false;
			// Parallel.ForEach 内联执行会污染调用线程的 ThreadLocal slot——显式复位；终态对齐串行
			//（串行下最后一个 ParseFunctionWithCatch 的 finally 写 null）
			ParseContext.ClearThreadSlots();
			setScanLine(null!);
		}
		return arts;
	}
}
