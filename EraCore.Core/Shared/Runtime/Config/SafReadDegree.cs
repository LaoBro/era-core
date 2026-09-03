using System;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// ADR-0026 诊断旋钮二：SAF content-URI 读取并发度（内存覆盖型，不进 emuera.config、不落盘）。
/// <list type="bullet">
/// <item><see cref="Auto"/>（默认）：每树 1 并发——与 7c10328 止血值逐位一致（双树 WhenAll 下总计 ≤2）。</item>
/// <item><see cref="One"/>/<see cref="Two"/>/<see cref="Four"/>：每树显式并发度——degree 表征实验用
/// （degree=2 是否既比 1 快又比 4 安全），仅绑 content-URI 读取分支，桌面文件系统路径不受限。</item>
/// </list>
/// 由 app 壳层（MAUI 设置页「诊断」分组经 Preferences）写入 <see cref="ConfigData.OverrideSafReadDegree"/>；
/// 游戏随包分发的 emuera.config 无法设置（无对应 ConfigCode，loadConfig 对未知键静默跳过）。
/// </summary>
public enum SafReadDegree
{
	Auto = 0,
	One = 1,
	Two = 2,
	Four = 4,
}

/// <summary>
/// <see cref="SafReadDegree"/> ↔ 协议字符串（"auto"/"1"/"2"/"4"）的唯一映射点——
/// MAUI 壳层解析 Preferences 值与 <c>preload_policy</c> 日志的 <c>requested=</c> 字段共用，
/// 保证「设置页写入的值」与「日志自证的值」永远同一套词表。字符串 switch，NativeAOT 下无反射。
/// </summary>
internal static class SafReadDegreeCodec
{
	/// <summary>协议字符串 → 并发度。"1"/"2"/"4" 为显式档位；"auto" / null / 空 / 未知值一律回退 <see cref="SafReadDegree.Auto"/>（容错与旋钮一三态解析一致）。</summary>
	internal static SafReadDegree Parse(string? value) => value switch
	{
		"1" => SafReadDegree.One,
		"2" => SafReadDegree.Two,
		"4" => SafReadDegree.Four,
		_ => SafReadDegree.Auto,
	};

	/// <summary>并发度 → 协议字符串（Preferences 存储格式 / preload_policy 日志 requested= 字段）。</summary>
	internal static string ToConfigString(SafReadDegree degree) => degree switch
	{
		SafReadDegree.One => "1",
		SafReadDegree.Two => "2",
		SafReadDegree.Four => "4",
		_ => "auto",
	};
}
