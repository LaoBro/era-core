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
		// Skia 方言 P0：数学 / 取整 / 无检查算术（实现票 18）。
		new() { Name = "SIN", CreateMethod = () => new SinMethod(), Dialect = Dialect.Snake },
		new() { Name = "COS", CreateMethod = () => new CosMethod(), Dialect = Dialect.Snake },
		new() { Name = "TAN", CreateMethod = () => new TanMethod(), Dialect = Dialect.Snake },
		new() { Name = "ASIN", CreateMethod = () => new AsinMethod(), Dialect = Dialect.Snake },
		new() { Name = "ACOS", CreateMethod = () => new AcosMethod(), Dialect = Dialect.Snake },
		new() { Name = "ATAN", CreateMethod = () => new AtanMethod(), Dialect = Dialect.Snake },
		new() { Name = "FLOOR", CreateMethod = () => new FloorMethod(), Dialect = Dialect.Snake },
		new() { Name = "CEIL", CreateMethod = () => new CeilMethod(), Dialect = Dialect.Snake },
		new() { Name = "ROUND", CreateMethod = () => new RoundMethod(), Dialect = Dialect.Snake },
		new() { Name = "UNCHECKED_ADD", CreateMethod = () => new UncheckedAddMethod(), Dialect = Dialect.Snake },
		new() { Name = "UNCHECKED_SUB", CreateMethod = () => new UncheckedSubtractMethod(), Dialect = Dialect.Snake },
		new() { Name = "UNCHECKED_MUL", CreateMethod = () => new UncheckedMultiplyMethod(), Dialect = Dialect.Snake },
		new() { Name = "UNCHECKED_NEG", CreateMethod = () => new UncheckedNegateMethod(), Dialect = Dialect.Snake },
		// Skia 方言 P0：数组位运算（实现票 18）。
		new() { Name = "BITSET", CreateMethod = () => new BitSetMethod(), Dialect = Dialect.Snake },
		new() { Name = "BITGET", CreateMethod = () => new BitGetMethod(), Dialect = Dialect.Snake },
		new() { Name = "BITTOGGLE", CreateMethod = () => new BitToggleMethod(), Dialect = Dialect.Snake },
		new() { Name = "BITINDEXOFFIRST", CreateMethod = () => new BitIndexOfFirstMethod(), Dialect = Dialect.Snake },
		// Skia 方言 P0：字符串动态求值（实现票 18）。
		new() { Name = "EVAL", CreateMethod = () => new EvalMethod(), Dialect = Dialect.Snake },
		new() { Name = "EVALS", CreateMethod = () => new EvalSMethod(), Dialect = Dialect.Snake },
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