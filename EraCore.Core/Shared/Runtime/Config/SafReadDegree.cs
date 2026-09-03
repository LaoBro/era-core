using System;
using System.Globalization;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// ADR-0026 诊断旋钮二：SAF content-URI 读取并发度（内存覆盖型，不进 emuera.config、不落盘）。
/// <list type="bullet">
/// <item><c>null</c>（=auto，默认）：每树 1 并发——与 7c10328 止血值逐位一致（双树 WhenAll 下总计 ≤2）。</item>
/// <item>显式数值：每树任意并发度（钳制 <see cref="SafReadDegreeCodec.MinExplicitDegree"/>–<see cref="SafReadDegreeCodec.MaxExplicitDegree"/>）——
/// 真机崩溃阈值二分取证用（"多少并发才崩"），仅绑 content-URI 读取分支，桌面文件系统路径不受限。</item>
/// </list>
/// 由 app 壳层（MAUI 设置页「诊断」分组经 Preferences）写入 <see cref="ConfigData.OverrideSafReadDegree"/>；
/// 游戏随包分发的 emuera.config 无法设置（无对应 ConfigCode，loadConfig 对未知键静默跳过）。
/// </summary>

/// <summary>
/// SAF 读取并发度 ↔ 协议字符串（"auto" / 十进制数值字符串）的唯一映射点——
/// MAUI 壳层解析 Preferences 值与 <c>preload_policy</c> 日志的 <c>requested=</c> 字段共用，
/// 保证「设置页写入的值」与「日志自证的值」永远同一套词表。数值域钳制 [1,64]：
/// 下界防 0/负数（ParallelOptions 语义不同），上界防手滑巨值导致瞬时内存爆炸（真机 ContentResolver
/// 实际崩点远低于 64，二分取证足够）。字符串解析，NativeAOT 下无反射。
/// </summary>
internal static class SafReadDegreeCodec
{
	/// <summary>显式档位下界。</summary>
	internal const int MinExplicitDegree = 1;
	/// <summary>显式档位上界（取证二分足够覆盖任何实际崩点）。</summary>
	internal const int MaxExplicitDegree = 64;

	/// <summary>协议字符串 → 并发度。"auto" / null / 空 / 非数值回退 null（=auto 每树 1）；数值钳制 [1,64]。</summary>
	internal static int? Parse(string? value)
	{
		if (string.IsNullOrEmpty(value) || value == "auto")
			return null;
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
			? Math.Clamp(n, MinExplicitDegree, MaxExplicitDegree)
			: null;
	}

	/// <summary>并发度 → 协议字符串（Preferences 存储格式 / preload_policy 日志 requested= 字段）。null → "auto"；数值规范化钳制输出。</summary>
	internal static string ToConfigString(int? degree)
		=> degree is { } n ? Math.Clamp(n, MinExplicitDegree, MaxExplicitDegree).ToString(CultureInfo.InvariantCulture) : "auto";
}
