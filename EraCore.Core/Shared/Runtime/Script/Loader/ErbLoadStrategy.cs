using System;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// ADR-0027 决策二.1（第一刀）：本轮装载的**并行性策略**——ERB 装载管线里全部「串行 vs 并行」
/// 路径选择的单一归属，以及两处并行度数解析与相关观测词表的唯一来源。
/// <list type="bullet">
/// <item><b>genmerge（文件级）</b>：<see cref="ParallelGenmerge"/> = <c>ParallelErbLoad &amp;&amp; !AnalysisMode</c>。</item>
/// <item><b>未调用遍历清扫（标签级）</b>：<see cref="ParallelSweep"/> = ADR-0024 止血门 + ADR-0026 平台门策略
///（auto/forceon/forceoff 三态；语义真值表穷举于 <c>ErbLoadStrategyTests</c>）。</item>
/// <item><b>度数</b>：<see cref="FileWindow"/>（生成窗口，文件数语义）与 <see cref="SweepDegree"/>
///（清扫度，线程数语义）**同源同值**，由 <c>OverrideMaxParallelErbFiles</c> 旋钮经 06-K1 决策点解析
///（auto = min(核数, 8)）。</item>
/// </list>
/// <para>
/// <b>本刀范围</b>：纯抽取——把原先散落在方法体里的路径选择与输入装配收进单一对象。**ambient 状态与
/// ThreadLocal 一律不动**（那是第二刀 ParseContext 的靶子）。<see cref="FromCurrent"/> 是唯一读取
/// ambient（<c>Config.Config.*</c> / 平台 API / 核数）的入口，每轮装载只调一次；此后全部消费方只问
/// 本对象，不再各自读配置——原先被写了两遍的 genmerge 判据表达式（判断行 + 日志行）由此归一。
/// </para>
/// <para>
/// <b>不在范围内</b>：<c>OverrideDimLockPolicy</c>（私有维锁策略，非路径选择；其真实生效点是
/// <c>IdentifierDictionary</c> 构造期快照，见 <c>parsescript_policy</c> 注释）。
/// </para>
/// </summary>
internal sealed class ErbLoadStrategy
{
	/// <summary>erb-loader-parallel 03：并行度的常量上限——窗口内产物同时驻留 = 上限文件数的解析产物
	///（spec 实测 eraTW 规模 12 核理想并行度 ~8-10x，取 8）。默认度 = 核数，见 <see cref="ResolveDegree"/>。</summary>
	internal const int MaxParallelErbFiles = 8;

	/// <summary>genmerge 阶段是否并行（文件级滑动窗口）。</summary>
	internal bool ParallelGenmerge { get; }

	/// <summary>未调用遍历清扫是否并行（标签级）。</summary>
	internal bool ParallelSweep { get; }

	/// <summary>并行生成窗口宽度（文件数语义；消费方仍按 <c>pending.Count</c> 夹取）。</summary>
	internal int FileWindow { get; }

	/// <summary>未调用遍历清扫度（线程数语义）。与 <see cref="FileWindow"/> 同源同值。</summary>
	internal int SweepDegree { get; }

	/// <summary>壳层请求的度数档位（null = auto）——<c>LoadPendingParallel</c> 的 <c>knob=</c> 字段。</summary>
	internal int? DegreeRequested { get; }

	/// <summary>未调用遍历主开关（游戏配置的 <c>ParallelParseScript</c>）——<c>configured=</c> 字段。</summary>
	internal bool SweepConfigured { get; }

	/// <summary>是否 Android（平台门输入）——<c>android=</c> 字段。</summary>
	internal bool IsAndroid { get; }

	/// <summary>AnalysisMode（两条路径选择的共同门槛）——<c>analysis=</c> 字段。</summary>
	internal bool AnalysisMode { get; }

	/// <summary>生效的平台门策略——<c>policy=</c> 字段。</summary>
	internal SweepGatePolicy SweepPolicy { get; }

	/// <summary>
	/// 本轮装载的 ambient 快照。**唯一**读取点：每轮装载调用一次，两条装载入口各自调用；
	/// 装载中途的配置变化不再被观察到（今天 genmerge 与 ParseScript 各读一次，同一轮内取值相同——
	/// 壳层在进游戏前 <c>Apply</c> 旋钮，见 <c>parsescript_policy</c> 的时序前提注释）。
	/// </summary>
	internal static ErbLoadStrategy FromCurrent(LoaderEnv env) => new(
		parallelErbLoad: Config.Config.ParallelErbLoad,
		parallelSweepConfigured: Config.Config.ParallelParseScript,
		analysisMode: env.AnalysisMode,
		isAndroid: OperatingSystem.IsAndroid(),
		sweepPolicy: Config.Config.OverrideSweepGatePolicy ?? SweepGatePolicy.Auto,
		degreeRequested: Config.Config.OverrideMaxParallelErbFiles,
		processorCount: Environment.ProcessorCount);

	/// <summary>显式入参构造：真值表穷举用，不读 ambient、不需引擎。</summary>
	internal ErbLoadStrategy(bool parallelErbLoad, bool parallelSweepConfigured, bool analysisMode,
		bool isAndroid, SweepGatePolicy sweepPolicy, int? degreeRequested, int processorCount)
	{
		SweepConfigured = parallelSweepConfigured;
		IsAndroid = isAndroid;
		AnalysisMode = analysisMode;
		SweepPolicy = sweepPolicy;
		DegreeRequested = degreeRequested;
		ParallelGenmerge = parallelErbLoad && !analysisMode;
		ParallelSweep = ShouldRunParallelSweep(parallelSweepConfigured, analysisMode, isAndroid, sweepPolicy);
		FileWindow = SweepDegree = ResolveDegree(degreeRequested, processorCount);
	}

	/// <summary><c>knob=</c> 观测字段（与 <see cref="MaxParallelErbFilesCodec"/> 同一词表）。</summary>
	internal string DegreeKnobString => MaxParallelErbFilesCodec.ToConfigString(DegreeRequested);

	/// <summary><c>policy=</c> 观测字段（与 <see cref="SweepGatePolicyCodec"/> 同一词表）。</summary>
	internal string SweepPolicyString => SweepGatePolicyCodec.ToConfigString(SweepPolicy);

	/// <summary>
	/// 未调用遍历并行门（ADR-0024 止血门 + ADR-0026 平台门策略，语义逐位不变）：
	/// Auto 与止血门逐位等价 <c>configured &amp;&amp; !analysisMode &amp;&amp; !isAndroid</c>；
	/// ForceOn 只越过 Android 平台门（AnalysisMode 门与主开关不越——一个旋钮一个维度）；
	/// ForceOff 恒 false，压过一切组合。24 组合全穷举于 <c>ErbLoadStrategyTests</c>。
	/// </summary>
	private static bool ShouldRunParallelSweep(bool configured, bool analysisMode, bool isAndroid, SweepGatePolicy policy)
		=> policy switch
		{
			SweepGatePolicy.ForceOn => configured && !analysisMode,
			SweepGatePolicy.ForceOff => false,
			_ => configured && !analysisMode && !isAndroid,
		};

	/// <summary>
	/// 06-K1 度数决策点（真值表穷举于 <c>ErbLoadStrategyTests</c>）：auto（null）= min(核数, 8)
	/// 与现状逐位一致；显式 4/8/16 = 直接生效（与核数解耦的取证档——16 档的价值正在于超订阅观测）。
	/// </summary>
	private static int ResolveDegree(int? requested, int processorCount)
		=> requested ?? Math.Min(processorCount, MaxParallelErbFiles);
}
