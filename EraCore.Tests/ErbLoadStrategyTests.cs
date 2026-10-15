using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Loader;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0027 决策二.1（第一刀）：并行性策略对象 <see cref="ErbLoadStrategy"/> 的语义真值表。
/// <para>
/// 本类承接两处**既有**真值表（原宿主侧静态入口已随第一刀降为策略对象的私有实现）：
/// <list type="bullet">
/// <item><c>ErbLoaderTests.ShouldRunParallelSweep_truth_table_exhaustive</c>（24 组合）——测试名与
/// InlineData 逐组保留，仅把被测对象由静态谓词改为策略实例，回归保护一分不减。</item>
/// <item><c>MaxParallelErbFilesTests.ResolveMaxParallelErbFiles_truth_table</c>（8 组）——度数解析随
/// 两个消费点（生成窗口 / 清扫度）一并归入策略，故断言**两个出口同源同值**。</item>
/// </list>
/// 另新增 genmerge 路径的判据真值表：该判据原先是方法体内裸表达式（判断行 + 日志行各写一遍），
/// 第一刀后由 <see cref="ErbLoadStrategy.ParallelGenmerge"/> 单点归属。
/// </para>
/// <para>
/// 全部用例经**显式入参构造**（不读 ambient、不起引擎）——这是策略对象的形态收益：语义可在无引擎
/// 条件下穷举。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public class ErbLoadStrategyTests
{
	/// <summary>未调用遍历主开关 / AnalysisMode / Android / 平台门策略 的显式组合（度数取 auto）。</summary>
	private static ErbLoadStrategy Sweep(bool configured, bool analysisMode, bool isAndroid, SweepGatePolicy policy)
		=> new(parallelErbLoad: false, parallelSweepConfigured: configured, analysisMode: analysisMode,
			isAndroid: isAndroid, sweepPolicy: policy, degreeRequested: null, processorCount: 4);

	/// <summary>
	/// ADR-0026 诊断旋钮：未调用遍历并行门语义真值表——
	/// 主开关(configured) × AnalysisMode × Android × policy(auto/forceon/forceoff) 全 24 组合穷举。
	/// 语义边界（回归保护）：Auto 与 ADR-0024 止血门逐位等价；ForceOn 只越过 Android 平台门，
	/// 不越过 AnalysisMode 门与主开关；ForceOff 压过一切组合恒串行。
	/// (true,false,true,Auto) 行即探针分支 probe-android-sweep 曾临时改 Short 并 Skip 的用例——
	/// 随 policy 维度参数化恢复：Auto 下 Android 仍串行，越门改用 ForceOn 显式表达。
	/// </summary>
	[Theory]
	// ---- Auto：configured && !analysisMode && !isAndroid（与改动前逐位一致）----
	[InlineData(true, false, false, SweepGatePolicy.Auto, true)]
	[InlineData(false, false, false, SweepGatePolicy.Auto, false)]
	[InlineData(true, true, false, SweepGatePolicy.Auto, false)]
	[InlineData(true, false, true, SweepGatePolicy.Auto, false)]
	[InlineData(false, true, false, SweepGatePolicy.Auto, false)]
	[InlineData(false, false, true, SweepGatePolicy.Auto, false)]
	[InlineData(true, true, true, SweepGatePolicy.Auto, false)]
	[InlineData(false, true, true, SweepGatePolicy.Auto, false)]
	// ---- ForceOn：configured && !analysisMode（Android 平台门被越过；其余门不动）----
	[InlineData(true, false, false, SweepGatePolicy.ForceOn, true)]
	[InlineData(true, false, true, SweepGatePolicy.ForceOn, true)]
	[InlineData(false, false, false, SweepGatePolicy.ForceOn, false)]
	[InlineData(false, false, true, SweepGatePolicy.ForceOn, false)]
	[InlineData(true, true, false, SweepGatePolicy.ForceOn, false)]
	[InlineData(true, true, true, SweepGatePolicy.ForceOn, false)]
	[InlineData(false, true, false, SweepGatePolicy.ForceOn, false)]
	[InlineData(false, true, true, SweepGatePolicy.ForceOn, false)]
	// ---- ForceOff：恒 false（紧急杀开关，压过游戏配置与一切组合）----
	[InlineData(true, false, false, SweepGatePolicy.ForceOff, false)]
	[InlineData(false, false, false, SweepGatePolicy.ForceOff, false)]
	[InlineData(true, true, false, SweepGatePolicy.ForceOff, false)]
	[InlineData(true, false, true, SweepGatePolicy.ForceOff, false)]
	[InlineData(false, true, false, SweepGatePolicy.ForceOff, false)]
	[InlineData(false, false, true, SweepGatePolicy.ForceOff, false)]
	[InlineData(true, true, true, SweepGatePolicy.ForceOff, false)]
	[InlineData(false, true, true, SweepGatePolicy.ForceOff, false)]
	public void ShouldRunParallelSweep_truth_table_exhaustive(
		bool configured, bool analysisMode, bool isAndroid, SweepGatePolicy policy, bool expected)
	{
		Assert.Equal(expected, Sweep(configured, analysisMode, isAndroid, policy).ParallelSweep);
	}

	/// <summary>
	/// genmerge（文件级）路径判据真值表：<c>ParallelErbLoad &amp;&amp; !AnalysisMode</c>。
	/// 第一刀前该表达式在判断行与 <c>phase=genmerge parallel=</c> 日志行各写一遍，现归策略对象单点。
	/// </summary>
	[Theory]
	[InlineData(true, false, true)]
	[InlineData(false, false, false)]
	[InlineData(true, true, false)]  // AnalysisMode 压倒主开关（与 sweep 门的 AnalysisMode 门槛同源）
	[InlineData(false, true, false)]
	public void ParallelGenmerge_truth_table(bool parallelErbLoad, bool analysisMode, bool expected)
	{
		var strategy = new ErbLoadStrategy(parallelErbLoad: parallelErbLoad, parallelSweepConfigured: false,
			analysisMode: analysisMode, isAndroid: false, sweepPolicy: SweepGatePolicy.Auto,
			degreeRequested: null, processorCount: 4);
		Assert.Equal(expected, strategy.ParallelGenmerge);
	}

	/// <summary>
	/// 度数决策点真值表：auto = min(核数, 8) 与现状逐位一致；显式档直接生效不与核数取 min。
	/// 两个出口（生成窗口 <see cref="ErbLoadStrategy.FileWindow"/> / 清扫度
	/// <see cref="ErbLoadStrategy.SweepDegree"/>）同源同值——原两处调用点各自读一次配置，现由策略快照。
	/// </summary>
	[Theory]
	[InlineData(null, 4, 4)] // auto：核数先触顶
	[InlineData(null, 8, 8)]
	[InlineData(null, 12, 8)] // auto：8 封顶触顶
	[InlineData(null, 16, 8)]
	[InlineData(4, 12, 4)]
	[InlineData(8, 4, 8)] // 显式档高于核数也原样生效（取证档与核数解耦）
	[InlineData(16, 8, 16)] // 8 核真机上的超订阅观测点
	[InlineData(16, 12, 16)]
	public void Degree_truth_table_shared_by_both_consumers(int? requested, int processorCount, int expected)
	{
		var strategy = new ErbLoadStrategy(parallelErbLoad: false, parallelSweepConfigured: false,
			analysisMode: false, isAndroid: false, sweepPolicy: SweepGatePolicy.Auto,
			degreeRequested: requested, processorCount: processorCount);
		Assert.Equal(expected, strategy.FileWindow);
		Assert.Equal(expected, strategy.SweepDegree);
	}

	/// <summary>
	/// 观测字段词表：<c>parsescript_policy</c> 的 <c>policy=</c> 与 <c>LoadPendingParallel</c> 的
	/// <c>knob=</c> 由策略对象提供，且与各自 codec 同一词表（设置页写入值与日志自证值永不分叉）。
	/// </summary>
	[Fact]
	public void Observability_strings_follow_the_codecs()
	{
		var auto = Sweep(true, false, false, SweepGatePolicy.Auto);
		Assert.Equal("auto", auto.SweepPolicyString);
		Assert.Equal("auto", auto.DegreeKnobString);
		Assert.True(auto.SweepConfigured);
		Assert.False(auto.IsAndroid);
		Assert.False(auto.AnalysisMode);

		Assert.Equal("forceon", Sweep(true, false, false, SweepGatePolicy.ForceOn).SweepPolicyString);
		Assert.Equal("forceoff", Sweep(true, false, false, SweepGatePolicy.ForceOff).SweepPolicyString);
		Assert.Equal("16", new ErbLoadStrategy(parallelErbLoad: false, parallelSweepConfigured: false,
			analysisMode: false, isAndroid: false, sweepPolicy: SweepGatePolicy.Auto,
			degreeRequested: 16, processorCount: 8).DegreeKnobString);
	}
}
