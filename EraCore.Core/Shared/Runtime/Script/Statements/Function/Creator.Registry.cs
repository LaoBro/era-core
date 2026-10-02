using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using System.Collections.Generic;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	/// <summary>
	/// ★ 新增式中函数的唯一声明点：在这里加一条 <see cref="FunctionMethodDescriptor"/> 即可。
	/// methodList 与（经 FunctionIdentifier 合并的）funcDic / 名称表随之生成，无需改其它文件。
	/// 参数规则与返回类型写在工厂产出的 <see cref="FunctionMethod"/> 里（成员为私有嵌套类型）。
	/// </summary>
	internal static IReadOnlyList<FunctionMethodDescriptor> BuiltInMethodDescriptors { get; } =
	[
		// 例：
		// new FunctionMethodDescriptor
		// {
		//     Name = "EVAL",
		//     CreateMethod = () => new EvalMethod(),
		//     Dialect = Dialect.Snake,
		// },
	];

	/// <summary>
	/// descriptor → methodList 注册项（实现票 15）。静态构造器与测试共用同一路径。
	/// 需在 <c>SetMethodName</c> 回填前调用，使新增项的名称与既有项一致。
	/// </summary>
	internal static void AddMethodDescriptors(Dictionary<string, FunctionMethod> methodList, IReadOnlyList<FunctionMethodDescriptor> descriptors)
	{
		foreach (FunctionMethodDescriptor descriptor in descriptors)
			methodList.Add(descriptor.Name, descriptor.CreateMethod());
	}
}