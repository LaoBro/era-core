using System;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// ADR-0026 诊断旋钮一：并行未调用遍历平台门策略（内存覆盖型，不进 emuera.config、不落盘）。
/// <list type="bullet">
/// <item><see cref="Auto"/>（默认）：跟随平台门——<c>configured &amp;&amp; !analysisMode &amp;&amp; !isAndroid</c>，与 ADR-0024 止血门逐位等价。</item>
/// <item><see cref="ForceOn"/>：只越过 Android 平台门（<c>configured &amp;&amp; !analysisMode</c>）——AnalysisMode 门与主开关不越，一个旋钮一个维度。</item>
/// <item><see cref="ForceOff"/>：恒为 false——紧急杀开关，压过游戏配置与设置页的一切组合。</item>
/// </list>
/// 由 app 壳层（MAUI 设置页「诊断」分组经 Preferences）写入 <see cref="ConfigData.OverrideSweepGatePolicy"/>；
/// 游戏随包分发的 emuera.config 无法设置（无对应 ConfigCode，loadConfig 对未知键静默跳过）。
/// </summary>
public enum SweepGatePolicy
{
	Auto = 0,
	ForceOn,
	ForceOff,
}

/// <summary>
/// <see cref="SweepGatePolicy"/> ↔ 协议字符串（"auto"/"forceon"/"forceoff"）的唯一映射点——
/// MAUI 壳层解析 Preferences 值与 <c>parsescript_policy</c> 日志的 <c>policy=</c> 字段共用，
/// 保证「设置页写入的值」与「日志自证的值」永远同一套词表。字符串 switch，NativeAOT 下无反射。
/// </summary>
internal static class SweepGatePolicyCodec
{
	/// <summary>协议字符串 → 策略。"auto" / null / 空 / 未知值一律回退 <see cref="SweepGatePolicy.Auto"/>（容错与既有三态解析一致）。</summary>
	internal static SweepGatePolicy Parse(string? value) => value switch
	{
		"forceon" => SweepGatePolicy.ForceOn,
		"forceoff" => SweepGatePolicy.ForceOff,
		_ => SweepGatePolicy.Auto,
	};

	/// <summary>策略 → 协议字符串（Preferences 存储格式 / parsescript_policy 日志 policy= 字段）。</summary>
	internal static string ToConfigString(SweepGatePolicy policy) => policy switch
	{
		SweepGatePolicy.ForceOn => "forceon",
		SweepGatePolicy.ForceOff => "forceoff",
		_ => "auto",
	};
}
