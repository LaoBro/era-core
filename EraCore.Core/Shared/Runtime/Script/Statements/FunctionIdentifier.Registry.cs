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
		// Skia 方言 P0：NF 输入变体（实现票 21）。参数与 TINPUT/TINPUTS 同形，NoFocus 语义接缝见指令类。
		new InstructionDescriptor
		{
			Name = "TINPUTNF",
			CreateInstruction = () => new TINPUT_Instruction(false, noFocus: true),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "TINPUTSNF",
			CreateInstruction = () => new TINPUTS_Instruction(false, noFocus: true),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "TONEINPUTNF",
			CreateInstruction = () => new TINPUT_Instruction(true, noFocus: true),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "TONEINPUTSNF",
			CreateInstruction = () => new TINPUTS_Instruction(true, noFocus: true),
			Dialect = Dialect.Snake,
		},
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
		// Skia 方言 P0：图像层（实现票 22）。参数完整解析 + ColorMatrix 5×5 校验；执行体走 EmueraConsole 语义接缝（no-op，实测 eraTW 引用见 Flan_UI.ERB）。
		new InstructionDescriptor
		{
			Name = "SETIMAGELAYER",
			CreateInstruction = () => new SETIMAGELAYER_Instruction(false),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "SETIMAGELAYERL",
			CreateInstruction = () => new SETIMAGELAYER_Instruction(true),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "CLEARIMAGELAYER",
			CreateInstruction = () => new CLEARIMAGELAYER_Instruction(),
			Dialect = Dialect.Snake,
		},
		new InstructionDescriptor
		{
			Name = "CLEARIMAGELAYER_ALL",
			CreateInstruction = () => new CLEARIMAGELAYER_ALL_Instruction(),
			Dialect = Dialect.Snake,
		},
	];
}