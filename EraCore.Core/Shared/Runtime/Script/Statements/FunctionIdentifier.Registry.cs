using MinorShift.Emuera.Runtime.Script.Statements;
using System.Collections.Generic;

namespace MinorShift.Emuera.GameProc.Function;

internal sealed partial class FunctionIdentifier
{
	/// <summary>
	/// ★ 新增方言指令的唯一声明点：在这里加一条 <see cref="InstructionDescriptor"/> 即可。
	/// funcDic / methodList / IdentifierDictionary 名称表随之生成，无需改其它文件。
	///
	/// 处理体工厂可构造本类私有嵌套的 Instruction 类型；新增非结构化指令保持默认
	/// <c>Code = FunctionCode.__NULL__</c>（执行经 <c>AInstruction.DoInstruction</c>，不需要改枚举）。
	/// </summary>
	internal static IReadOnlyList<InstructionDescriptor> BuiltInInstructionDescriptors { get; } =
	[
		// 例：
		// new InstructionDescriptor
		// {
		//     Name = "TINPUTNF",
		//     CreateInstruction = () => new TINPUT_Instruction(false),
		//     Dialect = Dialect.Snake,
		// },
	];
}