using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.GameData.Function;

/// <summary>
/// Skia 方言 P0：SQL_* 函数族包装层（实现票 28，spec §5）。
///
/// <para><b>浅 adapter</b>：只做参数取值 + <see cref="SqlManagerException"/> → <see cref="CodeEE"/> 映射；
/// 全部 SQL 语义在 <see cref="SqlManager"/> 深模块内。包装层**不引用 SQLite 类型**
/// （依赖收口约束，见 <c>SqlManager</c> 头注）。</para>
///
/// <para><b>命令形</b>：这些键作为式中函数登记后，<c>FunctionIdentifier</c> 会把 methodList 每项并入
/// funcDic（<c>methodInstruction</c>）——故 <c>SQL_CONNECT "db", "cs"</c> 这类无括号语句行同样可解析。</para>
/// </summary>
internal static partial class FunctionMethodCreator
{
	/// <summary>薄包装层统一把深模块的 <see cref="SqlManagerException"/> 转成 ERB 的 <see cref="CodeEE"/>。</summary>
	private static long SqlGuard(string name, Func<long> op)
	{
		try
		{
			return op();
		}
		catch (SqlManagerException ex)
		{
			throw new CodeEE($"{name}: {ex.Message}");
		}
	}

	private static string SqlGuardStr(string name, Func<string> op)
	{
		try
		{
			return op();
		}
		catch (SqlManagerException ex)
		{
			throw new CodeEE($"{name}: {ex.Message}");
		}
	}

	/// <summary>收集 <c>SQL_P_*</c> 的第 3 参起变参（全部按 String 取），无参返回 null。</summary>
	private static string[]? CollectSqlParams(ExpressionMediator exm, List<AExpression> arguments)
	{
		if (arguments.Count <= 2)
			return null;
		string[] values = new string[arguments.Count - 2];
		for (int i = 2; i < arguments.Count; i++)
			values[i - 2] = arguments[i].GetStrValue(exm);
		return values;
	}

	// ===== 连接 =====

	private sealed class SqlConnectionOpenMethod : FunctionMethod
	{
		public SqlConnectionOpenMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ConnectionOpen(arguments[0].GetStrValue(exm)));
	}

	private sealed class SqlConnectMethod : FunctionMethod
	{
		public SqlConnectMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String }, OmitStart = 1 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string dbName = arguments[0].GetStrValue(exm);
			string? connectionString = arguments.Count > 1 ? arguments[1].GetStrValue(exm) : null;
			return SqlGuard(Name, () => SqlManager.Connect(dbName, connectionString, replaceExisting: false));
		}
	}

	private sealed class SqlDisconnectMethod : FunctionMethod
	{
		public SqlDisconnectMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.Disconnect(arguments[0].GetStrValue(exm)));
	}

	// ===== 语句 / reader =====

	private sealed class SqlExecuteNonQueryMethod : FunctionMethod
	{
		public SqlExecuteNonQueryMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ExecuteNonQuery(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm)));
	}

	private sealed class SqlExecuteReaderMethod : FunctionMethod
	{
		public SqlExecuteReaderMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ExecuteReader(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm)));
	}

	private sealed class SqlReaderReadMethod : FunctionMethod
	{
		public SqlReaderReadMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ReaderRead(arguments[0].GetIntValue(exm)));
	}

	private sealed class SqlReaderGetLongMethod : FunctionMethod
	{
		public SqlReaderGetLongMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int, ArgType.Int } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ReaderGetLong(arguments[0].GetIntValue(exm), (int)arguments[1].GetIntValue(exm)));
	}

	private sealed class SqlReaderGetStringMethod : FunctionMethod
	{
		public SqlReaderGetStringMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int, ArgType.Int } }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuardStr(Name, () => SqlManager.ReaderGetString(arguments[0].GetIntValue(exm), (int)arguments[1].GetIntValue(exm)));
	}

	private sealed class SqlReaderIsNullMethod : FunctionMethod
	{
		public SqlReaderIsNullMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int, ArgType.Int } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ReaderIsNull(arguments[0].GetIntValue(exm), (int)arguments[1].GetIntValue(exm)));
	}

	private sealed class SqlReaderCloseMethod : FunctionMethod
	{
		public SqlReaderCloseMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ReaderClose(arguments[0].GetIntValue(exm)));
	}

	// ===== 标量 =====

	private sealed class SqlExecuteScalarLongMethod : FunctionMethod
	{
		public SqlExecuteScalarLongMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ExecuteScalarLong(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm)));
	}

	private sealed class SqlExecuteScalarStringMethod : FunctionMethod
	{
		public SqlExecuteScalarStringMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuardStr(Name, () => SqlManager.ExecuteScalarString(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm)));
	}

	// ===== 转义 =====

	private sealed class SqlEscapeMethod : FunctionMethod
	{
		public SqlEscapeMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String } }];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlManager.Escape(arguments[0].GetStrValue(exm));
	}

	// ===== XML 导入 / 导出 =====

	private sealed class SqlImportMapXmlMethod : FunctionMethod
	{
		public SqlImportMapXmlMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ImportMapXml(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm), arguments[2].GetStrValue(exm)));
	}

	private sealed class SqlImportDtXmlMethod : FunctionMethod
	{
		public SqlImportDtXmlMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ImportDtXml(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm), arguments[2].GetStrValue(exm), arguments[3].GetStrValue(exm)));
	}

	private sealed class SqlExportMapXmlMethod : FunctionMethod
	{
		public SqlExportMapXmlMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ExportMapXml(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm), arguments[2].GetStrValue(exm)));
	}

	private sealed class SqlExportDtXmlMethod : FunctionMethod
	{
		public SqlExportDtXmlMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ExportDtXml(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm), arguments[2].GetStrValue(exm), arguments[3].GetStrValue(exm)));
	}

	private sealed class SqlImportXmlCustomMethod : FunctionMethod
	{
		public SqlImportXmlCustomMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> SqlGuard(Name, () => SqlManager.ImportXmlCustom(arguments[0].GetStrValue(exm), arguments[1].GetStrValue(exm), arguments[2].GetStrValue(exm), arguments[3].GetStrValue(exm), arguments[4].GetStrValue(exm)));
	}

	// ===== 参数化（@0..@n 变参） =====

	private sealed class SqlPExecuteNonQueryMethod : FunctionMethod
	{
		public SqlPExecuteNonQueryMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.VariadicString }, OmitStart = 2 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string dbName = arguments[0].GetStrValue(exm);
			string sql = arguments[1].GetStrValue(exm);
			return SqlGuard(Name, () => SqlManager.ExecuteNonQuery(dbName, sql, CollectSqlParams(exm, arguments)));
		}
	}

	private sealed class SqlPExecuteReaderMethod : FunctionMethod
	{
		public SqlPExecuteReaderMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.VariadicString }, OmitStart = 2 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string dbName = arguments[0].GetStrValue(exm);
			string sql = arguments[1].GetStrValue(exm);
			return SqlGuard(Name, () => SqlManager.ExecuteReader(dbName, sql, CollectSqlParams(exm, arguments)));
		}
	}

	private sealed class SqlPExecuteScalarLongMethod : FunctionMethod
	{
		public SqlPExecuteScalarLongMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.VariadicString }, OmitStart = 2 }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string dbName = arguments[0].GetStrValue(exm);
			string sql = arguments[1].GetStrValue(exm);
			return SqlGuard(Name, () => SqlManager.ExecuteScalarLong(dbName, sql, CollectSqlParams(exm, arguments)));
		}
	}

	private sealed class SqlPExecuteScalarStringMethod : FunctionMethod
	{
		public SqlPExecuteScalarStringMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.String, ArgType.String, ArgType.VariadicString }, OmitStart = 2 }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string dbName = arguments[0].GetStrValue(exm);
			string sql = arguments[1].GetStrValue(exm);
			return SqlGuardStr(Name, () => SqlManager.ExecuteScalarString(dbName, sql, CollectSqlParams(exm, arguments)));
		}
	}
}
