namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// ADR-0026 诊断旋钮四（06-K2）：privateDimList 生成期全局锁策略（内存覆盖型，不进 emuera.config、不落盘）。
/// <list type="bullet">
/// <item><see cref="Auto"/>（默认）：恒加锁——与 771708b 方案 B 止血逐位一致
/// （并行生成期 #DIM/#DIMS 经 Add/Contains 并发触达普通 List，无锁可损坏内部数组）。</item>
/// <item><see cref="NoLock"/>：纯取证档——关锁重开它防的并发损坏竞态（issue 04-H1 候选判别：
/// 损坏型假警告是否消失），可致状态损坏；UI 显著危险标注，仅真机单变量取证用。</item>
/// </list>
/// 由 app 壳层（MAUI 设置页「诊断」分组经 Preferences）写入 <see cref="ConfigData.OverrideDimLockPolicy"/>；
/// 游戏随包分发的 emuera.config 无法设置（无对应 ConfigCode，loadConfig 对未知键静默跳过）。
/// </summary>
public enum DimLockPolicy
{
	Auto = 0,
	NoLock,
}

/// <summary>
/// <see cref="DimLockPolicy"/> ↔ 协议字符串（"auto"/"nolock"）的唯一映射点——
/// MAUI 壳层解析 Preferences 值与 parsescript_policy 日志的 <c>dimlock=</c> 字段共用，
/// 保证「设置页写入的值」与「日志自证的值」永远同一套词表。字符串 switch，NativeAOT 下无反射。
/// </summary>
internal static class DimLockPolicyCodec
{
	/// <summary>协议字符串 → 策略。"auto" / null / 空 / 未知值一律回退 <see cref="DimLockPolicy.Auto"/>（恒加锁）。</summary>
	internal static DimLockPolicy Parse(string? value) => value switch
	{
		"nolock" => DimLockPolicy.NoLock,
		_ => DimLockPolicy.Auto,
	};

	/// <summary>策略 → 协议字符串（Preferences 存储格式 / parsescript_policy 日志 dimlock= 字段）。</summary>
	internal static string ToConfigString(DimLockPolicy policy) => policy switch
	{
		DimLockPolicy.NoLock => "nolock",
		_ => "auto",
	};

	/// <summary>锁启用判定（唯一决策点，真值表穷举于 DimLockPolicyTests）：仅 NoLock 关锁，其余恒加锁。</summary>
	internal static bool IsDimLockEnabled(DimLockPolicy? policy) => policy != DimLockPolicy.NoLock;
}
