using MinorShift.Emuera.Runtime.Script.Statements;
using System.Threading;

namespace MinorShift.Emuera.Runtime.Script.Loader;

// ADR-0043（D 组第三刀 · C3a）：本类型原为 ErbLoader 的私有嵌套类，外移为顶层 internal——
// 它是「未调用遍并行」的显式解析上下文（ADR-0027 决策二.2 / 三），也是 C3b/C3c 的穿参类型。
/// <summary>
/// D5#2/#6 的收纳处：未调用遍并行期的**显式解析上下文**（ADR-0027 决策二.2 / 三）。
/// <para>
/// 它取代原先散落的三个 ambient 槽（<c>SweepScanLine</c> ThreadLocal / <c>SweepArtCurrent</c> ThreadLocal /
/// <c>SweepActive</c> static volatile）与实例分流开关 <c>_sweepParallelActive</c>：并行 worker
/// <see cref="Enter"/> 后把本对象**显式穿参**给解析链（<see cref="ParseFunctionWithCatch"/> 及其三步与
/// 全部写点），写点不再直写任何 ThreadLocal / 实例字段，而是写本对象的 <see cref="Art"/>。
/// </para>
/// <para>
/// 仍有一处 per-线程发布槽 <see cref="LoadScanLine"/>：运行期读者（VariableToken / LexicalAnalyzer /
/// ExpressionParser 等）在**同一个 worker 线程**上按线程读"当前解析行"，无法接收参数，故只能按线程发布。
/// 它与 <see cref="Active"/> 同为本类型的私有细节——外部仅经
/// <see cref="ErbLoader.TryGetLoadScanLine"/> 这一个 internal static 入口读（决策三：静态字段退化为
/// ParseContext 管理的实现细节，审计面从全库 grep 收敛到一个类型）。
/// </para>
/// <para>
/// <see cref="CurrentOrSerial"/> 供**运行期接缝可达**的两个方法取用（<see cref="MaterializeFunction"/> /
/// <see cref="ComputeSignatureLines"/>——其调用链经 <c>MaterializeOnRuntime</c> /
/// <c>EnsureCalleeSignature</c>，按决策一签名冻结，无法穿参）：本线程已发布并行 ctx 时取它，否则取串行 ctx，
/// 语义与改动前的 <c>_sweepParallelActive</c> 分流逐位一致。
/// </para>
/// </summary>
internal sealed class ParseContext
{
	/// <summary>D5#2：本 label 的产物落点（串行 ctx 为 null → 写点落全局 sink / 实例字段，行为逐位不变）。</summary>
	internal readonly UncalledSweepArt? Art;

	/// <summary>串行与回放 ctx：<see cref="ScanLine"/> 写全局 sink，FNF / useCallForm 落实例字段。</summary>
	internal static readonly ParseContext Serial = new(null);

	private ParseContext(UncalledSweepArt? art) => Art = art;

	/// <summary>并行 worker 的 per-label 上下文。</summary>
	internal static ParseContext ForLabel(UncalledSweepArt art) => new(art);

	/// <summary>本 ctx 是否并行——原 <c>_sweepParallelActive</c> 的替代：落点由 ctx 决定，不再是分流开关。</summary>
	internal bool IsParallel => Art != null;

	/// <summary>D5#6：并行期间的 per-线程当前解析行（运行期读者按线程读，见类文档）。</summary>
	private static readonly ThreadLocal<LogicalLine?> LoadScanLine = new();

	/// <summary>本线程当前所处 ctx（供 <see cref="CurrentOrSerial"/> 与 <see cref="ExitLabel"/> 用）。</summary>
	private static readonly ThreadLocal<ParseContext?> CurrentLocal = new();

	/// <summary>
	/// sweep 期间为 true（volatile）：读者 fast path——<see cref="ErbLoader.TryGetLoadScanLine"/>
	/// 常态（非 sweep，含 runtime 热路径）直接短路 null 回落全局，免去 ThreadLocal 读
	///（eraTW 复测实测该读点在变量解析热路径上被调数百万次）。
	/// </summary>
	internal static volatile bool Active;

	/// <summary>运行期读者唯一读点（经 <see cref="ErbLoader.TryGetLoadScanLine"/> 暴露）。</summary>
	internal static LogicalLine? CurrentLoadScanLine => Active ? LoadScanLine.Value : null;

	/// <summary>接缝可达方法的写点取用：本线程的并行 ctx（若有），否则串行 ctx。</summary>
	internal static ParseContext CurrentOrSerial => CurrentLocal.Value ?? Serial;

	/// <summary>worker 进入 per-label 上下文：发布 per-thread 面（配 <see cref="ExitLabel"/>）。</summary>
	internal void Enter()
	{
		CurrentLocal.Value = this;
		LoadScanLine.Value = null;
	}

	/// <summary>label 解析完毕：撤销本线程的 ctx 发布（解析行槽按既有语义留到 sweep 收尾/range 收尾清）。</summary>
	internal static void ExitLabel() => CurrentLocal.Value = null;

	/// <summary>清本线程两个发布槽（线程池线程复用防残留；原 sweep 收尾的两行）。</summary>
	internal static void ClearThreadSlots()
	{
		LoadScanLine.Value = null;
		CurrentLocal.Value = null;
	}

	/// <summary>并行 worker 的解析行写点：写 per-线程发布槽（静态——槽按线程而非按 ctx 实例，见类文档）。</summary>
	internal static void PublishScanLine(LogicalLine line) => LoadScanLine.Value = line;
}
