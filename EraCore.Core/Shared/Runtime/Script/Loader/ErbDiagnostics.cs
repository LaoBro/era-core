using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Loader;
/// <summary>
/// issue 07 P1a：物化（<see cref="MaterializeFunction"/>）四段分账。
/// <para>
/// 与 <see cref="ParseScriptDiag"/> 的区别：后者只在 <see cref="ParseFunctionWithCatch"/> 里计数，
/// 而 lazy 模式走 <see cref="MaterializeLabel"/>（:1917）→ 本方法，故命中轮的 steps 桶恒为空——
/// 这就是 issue 07 的 P1 一直拿不到数据的原因。本桶无条件发射（每次加载一行），
/// 覆盖命中轮（blob 后背）与建库轮（源后背）两条路径（两者都经 MaterializeFunction）。
/// </para>
/// </summary>
	internal sealed class MaterializeDiag
	{
		/// <summary>① 行流读取（<c>src.ReadEnabledLine</c>：blob 侧含 CharStream 构造 + SkipWhiteSpace）。</summary>
		internal long ReadTicks;
		/// <summary>② 建树（ParseLine / ParseLabelLine / ParseSharpLine；其中含词法，词法单列于 ParserProbe）。</summary>
		internal long TreeTicks;
		/// <summary>③ 签名重算（parseLabel + resizeLocalVars；仅 needSignature 桩）。</summary>
		internal long LabelTicks;
		/// <summary>④ 三步（setArgument / nestCheck / setJumpTo 合计 + 各自分列）。</summary>
		internal long ThreeTicks, SetArgTicks, NestTicks, JumpTicks;
		/// <summary>
		/// 签名预算（<see cref="ComputeSignature"/>，被段④ setArgument 内的 arg 转换触发，墙钟已含在 ThreeTicks 内）。
		/// <b>R1（签名序列化）的定价口径 = SigTicks + LabelTicks</b>——两者合起来才是「命中轮特有的签名重算」总量。
		/// </summary>
		internal long SigTicks, SigCalls;
		/// <summary>段内发生的 blob 读（签名预算那部分）——用于把 blob 总量拆成「段外预读」与「段内」。</summary>
		internal long BlobInSegTicks;
		/// <summary>计数：实际物化标签数 / 走签名重算的标签数。</summary>
		internal long Labels, SignatureLabels;
		/// <summary>计数：行流总数（含 @/$/# 与体行）/ 各类行数。</summary>
		internal long Lines, LabelLines, DollarLines, SharpLines;
		/// <summary>
		/// 各段**分配字节数**（`GC.GetTotalAllocatedBytes(precise: false)` 增量，与 <see cref="GcSnapshot"/> 同 API）。
		/// 为什么与墙钟并列：物化成本里分配/GC 主导（`dAllocMB=177`/轮），所以「序列化将来能不能赢」不仅看墙钟，
		/// 还要看反序列化路径能不能把这几百 MB 的分配换掉。Read/Tree/Label/Three 之和 ≤ 段内实际总量，
		/// 差额 = 循环簿记与未归属（`[` 预处理行、跳过行等）。
		/// </summary>
		internal long ReadAlloc, TreeAlloc, LabelAlloc, ThreeAlloc, SigAlloc;
	}

/// <summary>
/// ADR-0041（D 组第一刀 · C1）：<b>解析期诊断</b>——`materialize_diag` 行
/// （物化四段分账 + 词法/变量探针 + blob 读账）。
/// <para>
/// <b>接缝</b>：blob 读账三项由调用方按值传入；本类<b>不持有</b> `ErbLoader` 的任何字段。
/// </para>
/// <para>
/// <b>为什么 FNF 没跟着搬</b>：`printFunctionNotFoundWarning` 的签名绑着 `ErbLoader.ParseContext`
/// （<b>私有嵌套类</b>，38 处引用）；把它一起搬要么泄漏宿主类型、要么得先搬 `ParseContext`——
/// 后者属 C3（主循环 + 三遍解析）的边界，故 FNF 与 `ignoredFNF*` 暂留宿主。
/// </para>
/// </summary>
internal static class ErbParseDiagnostics
{
	/// <summary>
	/// issue 07 P1a/P1b：发射物化四段分账（单行，无条件——不依赖 DisplayReport / Agent Log）。
/// <para>
/// 口径：<c>read_ms + tree_ms + parseLabel_ms + three_ms = total_ms</c>（四段墙钟互斥）。
/// 归属子桶：<c>lexTree ⊆ tree</c>、<c>lexThree ⊆ three</c>、<c>varThree ⊆ three</c>、
/// <c>sig_ms ⊆ three</c>（签名预算是被 setArgument 触发的嵌套区）、<c>lexSig ⊆ sig</c>、<c>varSig ⊆ sig</c>、
/// <c>parseLabel_ms</c> 是段③ 内的签名重算。
/// </para>
/// <para>
/// <b>R1（签名序列化）定价口径 = sigTotal_ms = parseLabel_ms + sig_ms</b>；
/// <b>R2（07-M 词法中间态）口径 = lexTree_ms / total_ms</b>（只有段② 内的词法能被「跳词法」省掉）；
/// <b>R5 收缩范围口径 = three_ms 内 lexThree + varThree vs 余量</b>（余量 = ArgBuilder 分发 + ExpressionParser 建树 + 杂项）。
/// 分配列（<c>allocMB</c>）与墙钟并列：物化成本里分配/GC 主导，判断「反序列化能否赢」要看它能否替掉这些分配。
/// 对账：<c>loop_overhead_ms = main_wall − total − 段外 blob 读</c>（循环簿记 / 降级占位 / GC / 循环外物化）。
/// </para>
/// </summary>
	internal static void EmitMaterializeDiag(MaterializeDiag d, long mainTicks, long blobReadTicks, long blobReadBytes, int blobReadCount)
		=> EmueraLog.Info("ErbLoader", FormatMaterializeDiag(d, mainTicks, blobReadTicks, blobReadBytes, blobReadCount));

/// <summary>
/// ADR-0041：<c>materialize_diag</c> 行的<b>纯</b>格式化（无日志副作用、无实例状态）。
/// 抽出的目的是让"行的字段与顺序"可以被一条测试钉住——它是 D 组里唯一没有测试保护的
/// 对外形状，真机排障脚本按字段名 grep；搬家时若误改一个字段名会静默失配。
/// </summary>
	internal static string FormatMaterializeDiag(MaterializeDiag d, long mainTicks,
		long blobReadTicks, long blobReadBytes, int blobReadCount)
	{
		static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
		static double MB(long bytes) => bytes / 1048576.0;
		static long Lex(ParserProbeScope s) => ParserProbe.LexTicks[(int)s];
		static long LexC(ParserProbeScope s) => ParserProbe.LexCalls[(int)s];
		static long Var(ParserProbeScope s) => ParserProbe.VarTicks[(int)s];
		static long VarC(ParserProbeScope s) => ParserProbe.VarCalls[(int)s];
		long blobMs = (blobReadTicks * 1000) / Stopwatch.Frequency;
		long blobInSegMs = (d.BlobInSegTicks * 1000) / Stopwatch.Frequency;
		long insideTicks = d.ReadTicks + d.TreeTicks + d.LabelTicks + d.ThreeTicks;
		return
			$"materialize_diag labels={d.Labels} sigLabels={d.SignatureLabels}" +
			$" lines={d.Lines} labelLines={d.LabelLines} dollarLines={d.DollarLines} sharpLines={d.SharpLines}" +
			$" total_ms={Ms(insideTicks):F0} read_ms={Ms(d.ReadTicks):F0} tree_ms={Ms(d.TreeTicks):F0}" +
			$" parseLabel_ms={Ms(d.LabelTicks):F0} three_ms={Ms(d.ThreeTicks):F0}" +
			$" setarg_ms={Ms(d.SetArgTicks):F0} nest_ms={Ms(d.NestTicks):F0} jump_ms={Ms(d.JumpTicks):F0}" +
			$" sig_ms={Ms(d.SigTicks):F0} sigCalls={d.SigCalls} sigTotal_ms={Ms(d.LabelTicks + d.SigTicks):F0}" +
			$" lexTree_ms={Ms(Lex(ParserProbeScope.Tree)):F0}/{LexC(ParserProbeScope.Tree)}" +
			$" lexThree_ms={Ms(Lex(ParserProbeScope.Three)):F0}/{LexC(ParserProbeScope.Three)}" +
			$" lexSig_ms={Ms(Lex(ParserProbeScope.Signature)):F0}/{LexC(ParserProbeScope.Signature)}" +
			$" lexLabel_ms={Ms(Lex(ParserProbeScope.Label)):F0}/{LexC(ParserProbeScope.Label)}" +
			$" lexNone_ms={Ms(Lex(ParserProbeScope.None)):F0}/{LexC(ParserProbeScope.None)}" +
			$" varTree_ms={Ms(Var(ParserProbeScope.Tree)):F0}/{VarC(ParserProbeScope.Tree)}" +
			$" varThree_ms={Ms(Var(ParserProbeScope.Three)):F0}/{VarC(ParserProbeScope.Three)}" +
			$" varSig_ms={Ms(Var(ParserProbeScope.Signature)):F0}/{VarC(ParserProbeScope.Signature)}" +
			$" varLabel_ms={Ms(Var(ParserProbeScope.Label)):F0}/{VarC(ParserProbeScope.Label)}" +
			$" varNone_ms={Ms(Var(ParserProbeScope.None)):F0}/{VarC(ParserProbeScope.None)}" +
			$" allocMB read={MB(d.ReadAlloc):F1} tree={MB(d.TreeAlloc):F1} label={MB(d.LabelAlloc):F1}" +
			$" three={MB(d.ThreeAlloc):F1} sigInThree={MB(d.SigAlloc):F1}" +
			$" blob_ms={blobMs} blobInSeg_ms={blobInSegMs} blobReads={blobReadCount} blobBytes={blobReadBytes}" +
			$" main_wall_ms={Ms(mainTicks):F0} loop_overhead_ms={Ms(mainTicks - insideTicks) - (blobMs - blobInSegMs):F0}";
	}

}
