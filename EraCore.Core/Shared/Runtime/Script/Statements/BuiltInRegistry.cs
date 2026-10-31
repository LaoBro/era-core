using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>
/// 方言标签：descriptor 声明项归属的可见面模块（spec §4.2）。
/// <see cref="BuiltInRegistry"/> 汇总的声明按此标签供会话级 profile 投影（实现票 16）。
/// </summary>
internal enum Dialect
{
	/// <summary>era-core 移植前冻结面（默认 profile v24pure 仅此模块）。</summary>
	Baseline,
	/// <summary>Skia 方言语义键（P0 指令/函数）。</summary>
	Snake,
	/// <summary>游戏适配（兜底函数、宽容度策略）。</summary>
	Eratw,
}

/// <summary>
/// 已登记方言键的语义完成度（ADR-0053）。只描述**已登记**的键——未实现的键没有 descriptor，
/// 其决策属于 fork JSON 的 <c>status</c>（pending / excluded / out-of-scope），两层词汇互不越界。
/// </summary>
internal enum PortStatus
{
	/// <summary>真语义：行为与上游一致（默认值）。</summary>
	Semantic,
	/// <summary>语义接受：参数完整解析校验，执行体暂为空（no-op）。</summary>
	AcceptedNoOp,
	/// <summary>仅状态：保存状态，消费线未接。</summary>
	StateOnly,
}

/// <summary>
/// 声明式登记接缝（实现票 15）：一条指令 = 一条 descriptor。
/// 注册层只声明 name / 处理体工厂 / 附加 flag / <see cref="FunctionCode"/> / 方言标签；
/// funcDic、methodList 与 IdentifierDictionary 名称表由这些声明生成。
/// <see cref="FunctionCode"/> 枚举与运行期分派保留（NativeAOT 友好），descriptor 不改执行路径。
/// </summary>
internal sealed class InstructionDescriptor
{
	/// <summary>ERB 中出现的指令名（大写）。</summary>
	public required string Name { get; init; }

	/// <summary>处理体工厂；成员为私有嵌套类型的既有指令经各自 partial 声明文件构造。</summary>
	public required Func<AInstruction> CreateInstruction { get; init; }

	/// <summary>在 <see cref="AInstruction.Flag"/> 之上追加的 flag（与既有 addFunction 的 additionalFlag 同义）。</summary>
	public int AdditionalFlag { get; init; }

	/// <summary>
	/// 运行期分派码。新增方言指令保持 <see cref="FunctionCode.__NULL__"/>（走 AInstruction.DoInstruction，
	/// 无需改枚举）；仅迁移既有结构化指令时才填真实码。
	/// </summary>
	public FunctionCode Code { get; init; } = FunctionCode.__NULL__;

	/// <summary>归属方言模块。</summary>
	public Dialect Dialect { get; init; } = Dialect.Baseline;

	/// <summary>
	/// 检索分组（canonical 集合由测试锁定）；<c>null</c> = 未归类。baseline 键没有 descriptor，天然不设。
	/// </summary>
	public string? Family { get; init; }

	/// <summary>移植状态；默认 <see cref="PortStatus.Semantic"/>。</summary>
	public PortStatus Status { get; init; } = PortStatus.Semantic;

	/// <summary>
	/// 一句话差异/理由：中文 ≤ 60 字无换行（评审纪律），不复制参数表、不写测试链接。
	/// <see cref="PortStatus.Semantic"/> 之外的键与 <see cref="Dialect.Eratw"/> 键必填（测试锁定）。
	/// </summary>
	public string? Note { get; init; }
}

/// <summary>
/// 声明式登记接缝（实现票 15）：一个式中函数 = 一条 descriptor。
/// 参数规则（argumentTypeArrayEx）与返回类型由工厂产出的 <see cref="FunctionMethod"/> 自带，
/// descriptor 不重复声明，避免两处漂移。
/// </summary>
internal sealed class FunctionMethodDescriptor
{
	/// <summary>ERB 中出现的函数名（大写）。</summary>
	public required string Name { get; init; }

	/// <summary>实例工厂（每 name 一个单例，与既有 methodList 语义一致）。</summary>
	public required Func<FunctionMethod> CreateMethod { get; init; }

	/// <summary>归属方言模块。</summary>
	public Dialect Dialect { get; init; } = Dialect.Baseline;

	/// <summary>
	/// 检索分组（canonical 集合由测试锁定）；<c>null</c> = 未归类。baseline 键没有 descriptor，天然不设。
	/// </summary>
	public string? Family { get; init; }

	/// <summary>移植状态；默认 <see cref="PortStatus.Semantic"/>。</summary>
	public PortStatus Status { get; init; } = PortStatus.Semantic;

	/// <summary>
	/// 一句话差异/理由：中文 ≤ 60 字无换行（评审纪律），不复制参数表、不写测试链接。
	/// <see cref="PortStatus.Semantic"/> 之外的键与 <see cref="Dialect.Eratw"/> 键必填（测试锁定）。
	/// </summary>
	public string? Note { get; init; }
}

/// <summary>
/// 声明式登记接缝的单一访问面（实现票 15）：汇总所有 descriptor 声明的内置项，
/// 供 profile 投影（票 16）与面快照（票 17）消费。
///
/// 声明点因 C# 私有嵌套类型可见性约束分别落在
/// <c>FunctionIdentifier.Registry.cs</c> 与 <c>Creator.Registry.cs</c>，本类只做聚合，
/// 使「新增一条指令/函数只改一处」成立。
/// </summary>
internal static class BuiltInRegistry
{
	/// <summary>descriptor 声明的指令集合。</summary>
	internal static IReadOnlyList<InstructionDescriptor> Instructions => FunctionIdentifier.BuiltInInstructionDescriptors;

	/// <summary>descriptor 声明的式中函数集合。</summary>
	internal static IReadOnlyList<FunctionMethodDescriptor> Methods => FunctionMethodCreator.BuiltInMethodDescriptors;
}