using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements;
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>D5#2：FNF 事件缓冲（回放段重走 printFunctionNotFoundWarning 的 ONCE 决策）。</summary>
internal readonly record struct FnfEvent(string Str, LogicalLine Line, int Level, bool IsError);

/// <summary>
/// 未调用遍 per-label 产物。警告（含 FNF 占位）按原串行发射序进 <see cref="Warnings"/>，
/// 回放段按序消费（<see cref="ErbUncalledSweepReplay.Replay"/>）——普通警告直发、FNF 占位重走原决策，
/// 交错序与串行一致（FNF 与普通警告在 setJumpTo 行序上天然交错）。
/// </summary>
internal sealed class UncalledSweepArt
{
	public FunctionLabelLine Label = null!;
	public readonly List<ParserMediator.ParserWarning> Warnings = [];
	public readonly List<FnfEvent> FnfEvents = [];
	/// <summary>FNF 占位标记 level（普通警告 level 0-3，-1 安全保留）。</summary>
	public const int FnfPlaceholderLevel = -1;
	/// <summary>
	/// D5#3：本 label 解析过程中是否用到 callform 系。并行段由 <c>MarkUseCallForm</c> 写入本字段，
	/// 回放段（<see cref="ErbUncalledSweepReplay"/>）返回值给主线程合并回 <c>useCallForm</c>——并行期间不碰实例字段。
	/// </summary>
	public bool UseCallForm;
	public void AddFnfEvent(string str, LogicalLine line, int level, bool isError)
	{
		Warnings.Add(new ParserMediator.ParserWarning("", null!, FnfPlaceholderLevel, null!));
		FnfEvents.Add(new FnfEvent(str, line, level, isError));
	}
}

/// <summary>
/// ADR-0042（D 组第二刀 · C2）：未调用遍历的**回放**——把 per-label 产物按 labelList 序回放到
/// 全局警告序列，并交出「本批是否用到 callform」。
/// <para>
/// <b>接缝</b>：两件事不能由本模块自己完成，故显式外置——① FNF 占位要重走宿主
/// <c>printFunctionNotFoundWarning</c> 的原决策（它绑着 <c>ParseContext</c>，仍是宿主私有嵌套类型），
/// 经 <c>replayFnf</c> 回调；② <c>useCallForm</c> 是宿主标量，故以返回值交出（调用方 OR 回去）。
/// </para>
/// <para>
/// <b>不变式</b>：callform 标记必须<b>早于</b>「无警告则提前返回」被采集，否则无警告的 label 会丢标记。
/// </para>
/// </summary>
internal static class ErbUncalledSweepReplay
{
	/// <summary>按 labelList 序回放 per-label 产物（null 元素跳过——并行段异常时部分 label 未调度）。</summary>
	internal static bool ReplayAll(UncalledSweepArt[] arts, Action<string, LogicalLine, int, bool> replayFnf)
	{
		bool usedCallForm = false;
		for (int i = 0; i < arts.Length; i++)
		{
			if (arts[i] != null)
				usedCallForm |= Replay(arts[i], replayFnf);
		}
		return usedCallForm;
	}

	/// <summary>
	/// 回放单个 label 的缓冲（回放协议：回放体 = 原串行循环体的重放，不写镜像逻辑）：
	/// 普通警告按序直发全局列表；FNF 占位按序重走宿主 <paramref name="replayFnf"/> 的原决策
	///（ONCE 去重 / ignoredFNFWarningCount / Warn 发射，警告/事件从缓冲读，决策逻辑原样重走）。
	/// </summary>
	/// <returns>该 label 是否用到 callform 系（由调用方 OR 回宿主字段）。</returns>
	internal static bool Replay(UncalledSweepArt art, Action<string, LogicalLine, int, bool> replayFnf)
	{
		// D5#3：并行段采集的 callform 标记在此由主线程合并（单向置位，等价于串行的 OR 语义）。
		// 必须早于下方 warnings.Count == 0 的提前返回，否则无警告的 label 会丢失该标记。
		bool usedCallForm = art.UseCallForm;
		var warnings = art.Warnings;
		if (warnings.Count == 0)
			return usedCallForm;
		int fnfIdx = 0;
		for (int i = 0; i < warnings.Count; i++)
		{
			var w = warnings[i];
			if (w.WarningLevel == UncalledSweepArt.FnfPlaceholderLevel)
			{
				var ev = art.FnfEvents[fnfIdx++];
				replayFnf(ev.Str, ev.Line, ev.Level, ev.IsError);
			}
			else
			{
				ParserMediator.EmitWarning(w);
			}
		}
		warnings.Clear();
		art.FnfEvents.Clear();
		return usedCallForm;
	}
}
