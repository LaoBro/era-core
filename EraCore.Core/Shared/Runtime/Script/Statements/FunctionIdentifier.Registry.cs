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
		// Skia 方言 P0：动态调用（实现票 20）。CALLS_Instruction 运行时解析字符串调用串。
		new InstructionDescriptor
		{
			Name = "JUMPSTR",
			CreateInstruction = () => new CALLS_Instruction(true, false, false),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "CALLSTR",
			CreateInstruction = () => new CALLS_Instruction(false, false, false),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "TRYCALLSTR",
			CreateInstruction = () => new CALLS_Instruction(false, true, false),
			AdditionalFlag = EXTENDED,
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "TRYCJUMPSTR",
			CreateInstruction = () => new CALLS_Instruction(true, true, true),
			AdditionalFlag = EXTENDED,
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "TRYCCALLSTR",
			CreateInstruction = () => new CALLS_Instruction(false, true, true),
			AdditionalFlag = EXTENDED,
			Dialect = Dialect.Snake,
		},
	];
}