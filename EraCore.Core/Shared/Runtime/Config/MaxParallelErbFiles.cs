namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// ADR-0026 诊断旋钮三（06-K1）：并行生成度上限（内存覆盖型，不进 emuera.config、不落盘）。
/// <list type="bullet">
/// <item><c>null</c>（=auto，默认）：min(核数, 8)——与 <c>ErbLoadStrategy.MaxParallelErbFiles</c> 常量逐位一致
///（第一刀后由 <c>ErbLoadStrategy.FileWindow</c> / <c>SweepDegree</c> 单点解析，见 ADR-0027 决策二.1）。</item>
/// <item>显式 4/8/16：直接生效（不再与核数取 min）——真机取证档，与核数解耦；16 档的价值正在于
/// 观察超订阅/更多未合并产物驻留下的表现。ERB 生成窗口（同时在生成/未合并的文件数）与未调用
/// 遍历清扫度（并行解析线程上限）同源同拨——语义仍不同（文件数 vs 线程数），共用同一取证旋钮
/// 修订原 R3-Q11「度数硬编码不可配」。</item>
/// </list>
/// 由 app 壳层（MAUI 设置页「诊断」分组经 Preferences）写入 <see cref="ConfigData.OverrideMaxParallelErbFiles"/>；
/// 游戏随包分发的 emuera.config 无法设置（无对应 ConfigCode，loadConfig 对未知键静默跳过）。
/// </summary>

/// <summary>
/// 并行生成度上限 ↔ 协议字符串（"auto"/"4"/"8"/"16"）的唯一映射点——
/// MAUI 壳层解析 Preferences 值与 LoadPendingParallel/parsescript_policy 日志的 <c>knob=</c> 字段共用，
/// 保证「设置页写入的值」与「日志自证的值」永远同一套词表。闭集词表字符串精确匹配
/// （"08" 不算 8），词表外值一律回退 null(=auto)。字符串 switch，NativeAOT 下无反射。
/// </summary>
internal static class MaxParallelErbFilesCodec
{
	/// <summary>协议字符串 → 上限值。"auto" / null / 空 / 词表外值回退 null（=auto min(核数,8)）。</summary>
	internal static int? Parse(string? value) => value switch
	{
		"4" => 4,
		"8" => 8,
		"16" => 16,
		_ => null,
	};

	/// <summary>上限值 → 协议字符串（Preferences 存储格式 / 日志 knob= 字段）。null 或词表外值 → "auto"。</summary>
	internal static string ToConfigString(int? degree) => degree switch
	{
		4 => "4",
		8 => "8",
		16 => "16",
		_ => "auto",
	};
}
