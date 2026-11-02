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
		new() { Name = "SIN", CreateMethod = () => new SinMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "COS", CreateMethod = () => new CosMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "TAN", CreateMethod = () => new TanMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "ASIN", CreateMethod = () => new AsinMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "ACOS", CreateMethod = () => new AcosMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "ATAN", CreateMethod = () => new AtanMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "FLOOR", CreateMethod = () => new FloorMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "CEIL", CreateMethod = () => new CeilMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "ROUND", CreateMethod = () => new RoundMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "UNCHECKED_ADD", CreateMethod = () => new UncheckedAddMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "UNCHECKED_SUB", CreateMethod = () => new UncheckedSubtractMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "UNCHECKED_MUL", CreateMethod = () => new UncheckedMultiplyMethod(), Dialect = Dialect.Snake, Family = "数学" },
		new() { Name = "UNCHECKED_NEG", CreateMethod = () => new UncheckedNegateMethod(), Dialect = Dialect.Snake, Family = "数学" },
		// Skia 方言 P0：数组位运算（实现票 18）。
		new() { Name = "BITSET", CreateMethod = () => new BitSetMethod(), Dialect = Dialect.Snake, Family = "位运算" },
		new() { Name = "BITGET", CreateMethod = () => new BitGetMethod(), Dialect = Dialect.Snake, Family = "位运算" },
		new() { Name = "BITTOGGLE", CreateMethod = () => new BitToggleMethod(), Dialect = Dialect.Snake, Family = "位运算" },
		new() { Name = "BITINDEXOFFIRST", CreateMethod = () => new BitIndexOfFirstMethod(), Dialect = Dialect.Snake, Family = "位运算" },
		// Skia 方言 P0：字符串动态求值（实现票 18）。
		new() { Name = "EVAL", CreateMethod = () => new EvalMethod(), Dialect = Dialect.Snake, Family = "字符串求值" },
		new() { Name = "EVALS", CreateMethod = () => new EvalSMethod(), Dialect = Dialect.Snake, Family = "字符串求值" },
		// 试点（自动分档 A 类）：STRFORMCHECK 复用 era-core 既有格式化字符串解析 API。
		new() { Name = "STRFORMCHECK", CreateMethod = () => new StrFormCheckMethod(), Dialect = Dialect.Snake, Family = "字符串求值" },
		// 试点（自动分档 B 类）：MATCHALL/MATCHALLEX 在变量区间内统计匹配值，参数规则需自定义校验。
		new() { Name = "MATCHALL", CreateMethod = () => new MatchAllMethod(false), Dialect = Dialect.Snake, Family = "数组搜索" },
		new() { Name = "MATCHALLEX", CreateMethod = () => new MatchAllMethod(true), Dialect = Dialect.Snake, Family = "数组搜索" },
		// Skia 方言 P0：集合 MAP_*（实现票 19）。复用既有 DataStringMaps 数据模型。
		new() { Name = "MAP_VALUES", CreateMethod = () => new MapValuesMethod(), Dialect = Dialect.Snake, Family = "MAP" },
		new() { Name = "MAP_MERGE", CreateMethod = () => new MapMergeMethod(), Dialect = Dialect.Snake, Family = "MAP" },
		new() { Name = "MAP_REMOVEIF", CreateMethod = () => new MapRemoveIfMethod(), Dialect = Dialect.Snake, Family = "MAP" },
		new() { Name = "MAP_FINDKEY", CreateMethod = () => new MapFindKeyMethod(), Dialect = Dialect.Snake, Family = "MAP" },
		new() { Name = "MAP_TOSTRING", CreateMethod = () => new MapToStringMethod(), Dialect = Dialect.Snake, Family = "MAP" },
		new() { Name = "MAP_FROMSTRING", CreateMethod = () => new MapFromStringMethod(), Dialect = Dialect.Snake, Family = "MAP" },
		// Skia 方言 P0：平台 / 动画定时器（实现票 28，spec §5 追加 2）。
		new() { Name = "GETPLATFORM", CreateMethod = () => new GetPlatformMethod(), Dialect = Dialect.Snake, Family = "平台定时器" },
		new() { Name = "GETANIMETIMER", CreateMethod = () => new GetAnimeTimerMethod(), Dialect = Dialect.Snake, Family = "平台定时器" },
		// Skia 方言 P0：SQL_* 函数族（实现票 28）。22 个非 Float 键；3 个 Float 变体待票 27。
		new() { Name = "SQL_CONNECTION_OPEN", CreateMethod = () => new SqlConnectionOpenMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_CONNECT", CreateMethod = () => new SqlConnectMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_DISCONNECT", CreateMethod = () => new SqlDisconnectMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_ESCAPE", CreateMethod = () => new SqlEscapeMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_EXECUTE_NONQUERY", CreateMethod = () => new SqlExecuteNonQueryMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_EXECUTE_READER", CreateMethod = () => new SqlExecuteReaderMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_EXECUTE_SCALAR_LONG", CreateMethod = () => new SqlExecuteScalarLongMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_EXECUTE_SCALAR_STRING", CreateMethod = () => new SqlExecuteScalarStringMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_P_EXECUTE_NONQUERY", CreateMethod = () => new SqlPExecuteNonQueryMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_P_EXECUTE_READER", CreateMethod = () => new SqlPExecuteReaderMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_P_EXECUTE_SCALAR_LONG", CreateMethod = () => new SqlPExecuteScalarLongMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_P_EXECUTE_SCALAR_STRING", CreateMethod = () => new SqlPExecuteScalarStringMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_READER_READ", CreateMethod = () => new SqlReaderReadMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_READER_GET_LONG", CreateMethod = () => new SqlReaderGetLongMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_READER_GET_STRING", CreateMethod = () => new SqlReaderGetStringMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_READER_ISNULL", CreateMethod = () => new SqlReaderIsNullMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_READER_CLOSE", CreateMethod = () => new SqlReaderCloseMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_IMPORT_MAP_XML", CreateMethod = () => new SqlImportMapXmlMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_IMPORT_DT_XML", CreateMethod = () => new SqlImportDtXmlMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_EXPORT_MAP_XML", CreateMethod = () => new SqlExportMapXmlMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_EXPORT_DT_XML", CreateMethod = () => new SqlExportDtXmlMethod(), Dialect = Dialect.Snake, Family = "SQL" },
		new() { Name = "SQL_IMPORT_XML_CUSTOM", CreateMethod = () => new SqlImportXmlCustomMethod(), Dialect = Dialect.Snake, Family = "SQL" },
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