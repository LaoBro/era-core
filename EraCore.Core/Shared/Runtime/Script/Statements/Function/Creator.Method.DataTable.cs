using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameView;

using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class DataTableManagementMethod : FunctionMethod
	{
		public enum Operation { Create, Check, Release, Clear, Case };
		public DataTableManagementMethod(Operation type)
		{
			ReturnType = typeof(long);
			if (type == Operation.Case)
				argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			else
				argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			bool contains = dict.ContainsKey(key);
			switch (op)
			{
				case Operation.Clear:
					{
						if (contains)
						{
							dict[key].Clear();
							return 1;
						}
						return -1;
					}
				case Operation.Case:
					{
						if (contains)
						{
							dict[key].CaseSensitive = arguments[1].GetIntValue(exm) == 0;
							return 1;
						}
						return -1;
					}
				case Operation.Check: { return contains ? 1 : 0; }
				case Operation.Release: { if (contains) dict.Remove(key); return 1; }
			}
			if (contains) return 0;
			var dt = new DataTable(key)
			{
				CaseSensitive = true
			};
			var c = dt.Columns.Add("id", typeof(long));
			c.AllowDBNull = false;
			c.Unique = true;
			dict[key] = dt;
			dt.PrimaryKey = [c];
			return 1;
		}
	}

	private sealed class DataTableColumnManagementMethod : FunctionMethod
	{
		public enum Operation { Create, Check, Remove, Names };
		public DataTableColumnManagementMethod(Operation type)
		{
			ReturnType = typeof(long);
			if (type == Operation.Create)
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.Any, ArgType.Int }, OmitStart = 2 },
					];
			else if (type == Operation.Names)
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString1D }, OmitStart = 1 },
					];
			else
				argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		// NativeAOT 豁免（已登记：nativeaot-verify-report.md §5.2）：列类型解析经
		// Utils.DataTable.NameToType/IntToType 返回内置基础类型（string/long 等）后传入
		// DataColumnCollection.Add(string, Type)（要求 PublicFields|PublicProperties）。
		// 返回值来自静态数组/字典，DAM 无法注解（Dictionary<string,Type> 元素不支持），
		// 故在唯一调用点豁免；基础类型元数据天然保留，运行时无裁剪风险。
		[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "列类型解析返回内置基础类型（NameToType/IntToType），DAM 无法注解字典值")]
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return -1;
			if (op == Operation.Names)
			{
				string[] output;
				if (arguments.Count > 1 && arguments[1] is VariableTerm v) output = (v.Identifier.GetArray() as string[])!;
				else output = exm.VEvaluator.RESULTS_ARRAY;
				for (int i = 0; i < dt.Columns.Count; i++) output[i] = dt.Columns[i].ColumnName;
				return dt.Columns.Count;
			}
			string cName = arguments[1].GetStrValue(exm);
			bool contains = dt.Columns.Contains(cName);
			switch (op)
			{
				case Operation.Check: { return contains ? Utils.DataTable.TypeToInt(dt.Columns[cName]!.DataType) : 0; }
				case Operation.Remove:
					{
						if (contains && !string.Equals(cName, "id", StringComparison.OrdinalIgnoreCase))
						{
							dt.Columns.Remove(cName);
							return 1;
						}
						return 0;
					}
			}
			if (contains) return 0;
			Type t = null!;
			if (arguments.Count >= 3)
			{
				if (arguments[2].GetOperandType() == typeof(string)) t = Utils.DataTable.NameToType(arguments[2].GetStrValue(exm));
				else t = Utils.DataTable.IntToType(arguments[2].GetIntValue(exm));
				if (t == null)
				{
					throw new CodeEE(string.Format(trerror.UnsupportedType.Text, Name));
				}
			}
			bool nullable = arguments.Count == 4 ? arguments[3].GetIntValue(exm) != 0 : true;
			DataColumn dc;
			if (t != null) dc = dt.Columns.Add(cName, t);
			else dc = dt.Columns.Add(cName);
			dc.AllowDBNull = nullable;
			return 1;
		}
	}

	private sealed class DataTableRowSetMethod : FunctionMethod
	{
		public enum Operation { Add, Set };
		public DataTableRowSetMethod(Operation type)
		{
			ReturnType = typeof(long);
			if (type == Operation.Add)
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.VariadicString, ArgType.VariadicAny }, MatchVariadicGroup = true, OmitStart = 1 },
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString1D, ArgType.RefAny1D, ArgType.Int } },
					];
			else
				argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.VariadicString, ArgType.VariadicAny }, MatchVariadicGroup = true },
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.RefString1D, ArgType.RefAny1D, ArgType.Int } },
					];
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		void CheckName(DataTable dt, string name, string key)
		{
			if (name == "id")
				throw new CodeEE(string.Format(trerror.DTCanNotEditIdColumn.Text, Name, key));
			if (!dt.Columns.Contains(name))
				throw new CodeEE(string.Format(trerror.DTLackOfNamedColumn.Text, Name, key, name));
		}
		void SetValue(DataRow row, DataTable dt, string name, string key, ExpressionMediator exm, AExpression v)
		{
			CheckName(dt, name, key);
			if (v == null)
			{
				row[name] = DBNull.Value;
				return;
			}
			bool isString = dt.Columns[name]!.DataType == typeof(string);
			if (v.GetOperandType() != (isString ? typeof(string) : typeof(long)))
				throw new CodeEE(string.Format(trerror.DTInvalidDataType.Text, Name, key, name));

			if (isString)
				row[name] = v.GetStrValue(exm);
			else
				row[name] = Utils.DataTable.ConvertInt(v.GetIntValue(exm), dt.Columns[name]!.DataType);
		}
		void SetValue(DataRow row, DataTable dt, string name, string key, string str)
		{
			CheckName(dt, name, key);
			if (dt.Columns[name]!.DataType != typeof(string))
				throw new CodeEE(string.Format(trerror.DTInvalidDataType.Text, Name, key, name));
			row[name] = str;
		}
		void SetValue(DataRow row, DataTable dt, string name, string key, long v)
		{
			CheckName(dt, name, key);
			if (dt.Columns[name]!.DataType == typeof(string))
				throw new CodeEE(string.Format(trerror.DTInvalidDataType.Text, Name, key, name));
			row[name] = Utils.DataTable.ConvertInt(v, dt.Columns[name]!.DataType);
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var b = op == Operation.Add ? 0 : 1;
			string key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return -1;
			var cCount = 0L;
			DataRow row;
			if (op == Operation.Set)
			{
				var idx = arguments[1].GetIntValue(exm);
				if (dt.Rows.Find(idx) is DataRow r)
					row = r;
				else return -2;
			}
			else
			{
				row = dt.NewRow();
				row[0] = Utils.TimePoint();
			}
			if (arguments.Count == b + 4)
			{
				var names = ((arguments[b + 1] as VariableTerm)!.Identifier.GetArray() as string[])!;
				var count = Math.Min(names.Length, arguments[b + 3].GetIntValue(exm));
				if (arguments[b + 2].GetOperandType() == typeof(string))
				{
					var vals = ((arguments[b + 2] as VariableTerm)!.Identifier.GetArray() as string[])!;
					count = Math.Min(vals.Length, count);
					for (int i = 0; i < count; i++)
						SetValue(row, dt, names[i], key, vals[i]);
					cCount += count;
				}
				else
				{
					var vals = ((arguments[b + 2] as VariableTerm)!.Identifier.GetArray() as long[])!;
					count = Math.Min(vals.Length, count);
					for (int i = 0; i < count; i++)
						SetValue(row, dt, names[i], key, vals[i]);
					cCount += count;
				}
			}
			else
			{
				var pos = b + 1;
				while (pos < arguments.Count)
				{
					var name = arguments[pos].GetStrValue(exm);
					SetValue(row, dt, name, key, exm, arguments[pos + 1]);
					pos += 2;
					cCount++;
				}
			}
			if (op == Operation.Add)
			{
				dt.Rows.Add(row);
				return (long)row[0];
			}
			return cCount;
		}
	}

	private sealed class DataTableLengthMethod : FunctionMethod
	{
		public enum Operation { Row, Column };
		public DataTableLengthMethod(Operation type)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.ContainsKey(key)) return -1;
			return op == Operation.Row ? dict[key].Rows.Count : dict[key].Columns.Count;
		}
	}

	private sealed class DataTableRowRemoveMethod : FunctionMethod
	{
		public DataTableRowRemoveMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int } },
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefInt1D, ArgType.Int } },
					];
			CanRestructure = false;
		}
		// NativeAOT 豁免（已登记：nativeaot-verify-report.md §5.2）：DataTable.Select 表达式
		// 触发 IL2026；tests/test_datatable_aot.py 13/13 实测可用，豁免附证据。
		[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "DataTable.Select 表达式实测 NativeAOT 可用（test_datatable_aot.py 13/13）")]
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			string key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return -1;
			DataRow[] rows;
			if (arguments.Count == 3)
			{
				StringBuilder sb = new();
				var array = ((arguments[1] as VariableTerm)!.Identifier.GetArray() as long[])!;
				var count = Math.Min((int)arguments[2].GetIntValue(exm), array.Length);
				if (count <= 0) return 0;
				sb.Append('(');
				for (int i = 0; i < count; i++)
					sb.Append(i == 0 ? array[i].ToString() : "," + array[i]);
				sb.Append(')');
				rows = dt.Select("id IN " + sb.ToString());
				if (rows == null) return 0;
			}
			else if (dt.Rows.Find(arguments[1].GetIntValue(exm)) is DataRow row)
				rows = [row];
			else return 0;
			foreach (var row in rows) dt.Rows.Remove(row);
			return rows.Length;
		}
	}

	private sealed class DataTableCellGetMethod : FunctionMethod
	{
		public enum Operation { Get, IsNull, Gets };
		public DataTableCellGetMethod(Operation type)
		{
			ReturnType = type == Operation.Gets ? typeof(string) : typeof(long);
			argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.String, ArgType.Int }, OmitStart = 3 },
					];
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return op == Operation.IsNull ? -1 : 0;
			bool asId = arguments.Count == 4 ? arguments[3].GetIntValue(exm) != 0 : false;
			var idx = arguments[1].GetIntValue(exm);
			var name = arguments[2].GetStrValue(exm);
			if (asId)
			{
				if (dt.Rows.Find(idx) is DataRow row && dt.Columns.Contains(name))
				{
					var v = row[name];
					return op == Operation.Get ? v == DBNull.Value ? 0 : Convert.ToInt64(v) : (v == DBNull.Value ? 1 : 0);
				}
			}
			else
			{
				if (0 <= idx && idx < dt.Rows.Count && dt.Columns.Contains(name))
				{
					var v = dt.Rows[(int)idx][name];
					return op == Operation.Get ? v == DBNull.Value ? 0 : Convert.ToInt64(v) : (v == DBNull.Value ? 1 : 0);
				}
			}
			return op == Operation.IsNull ? -2 : 0;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return string.Empty;
			bool asId = arguments.Count == 4 ? arguments[3].GetIntValue(exm) != 0 : false;
			var idx = arguments[1].GetIntValue(exm);
			var name = arguments[2].GetStrValue(exm);
			if (asId)
			{
				if (dt.Rows.Find(idx) is DataRow row && dt.Columns.Contains(name))
				{
					var v = row[name];
					if (v != DBNull.Value) return (string)v;
				}
			}
			else
			{
				if (0 <= idx && idx < dt.Rows.Count && dt.Columns.Contains(name))
				{
					var v = dt.Rows[(int)idx][name];
					if (v != DBNull.Value) return v.ToString()!;
				}
			}
			return string.Empty;
		}
	}

	// 票 36（Skia 对齐）：DT_CELL_GET 的浮点分支，只做 Get。
	private sealed class DataTableCellGetFloatMethod : FunctionMethod
	{
		public DataTableCellGetFloatMethod()
		{
			ReturnType = typeof(double);
			argumentTypeArrayEx = [
						new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.String, ArgType.Int }, OmitStart = 3 },
					];
			CanRestructure = false;
		}

		public override double GetFloatValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return 0.0;
			bool asId = arguments.Count == 4 ? arguments[3].GetIntValue(exm) != 0 : false;
			var idx = arguments[1].GetIntValue(exm);
			var name = arguments[2].GetStrValue(exm);
			if (asId)
			{
				if (dt.Rows.Find(idx) is DataRow row && dt.Columns.Contains(name))
				{
					var v = row[name];
					return v == DBNull.Value ? 0.0 : Convert.ToDouble(v);
				}
			}
			else
			{
				if (0 <= idx && idx < dt.Rows.Count && dt.Columns.Contains(name))
				{
					var v = dt.Rows[(int)idx][name];
					return v == DBNull.Value ? 0.0 : Convert.ToDouble(v);
				}
			}
			return 0.0;
		}
	}

	private sealed class DataTableCellSetMethod : FunctionMethod
	{
		public DataTableCellSetMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int, ArgType.String, ArgType.Any, ArgType.Int }, OmitStart = 3 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return -1;
			bool asId = arguments.Count == 5 ? arguments[4].GetIntValue(exm) != 0 : false;
			var idx = arguments[1].GetIntValue(exm);
			var name = arguments[2].GetStrValue(exm);
			if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)) return 0;
			var v = arguments.Count > 3 ? arguments[3] : null;
			DataRow row = null!;
			if (asId) row = dt.Rows.Find(idx)!;
			else if (idx >= 0 && idx < dt.Rows.Count) row = dt.Rows[(int)idx];
			if (row != null && dt.Columns.Contains(name))
			{
				if (v == null) row[name] = DBNull.Value;
				else
				{
					bool isString = dt.Columns[name]!.DataType == typeof(string);
					if (v.GetOperandType() != (isString ? typeof(string) : typeof(long))) return -2;

					if (isString)
						row[name] = v.GetStrValue(exm);
					else
						row[name] = Utils.DataTable.ConvertInt(v.GetIntValue(exm), dt.Columns[name]!.DataType);
				}
				return 1;
			}
			return -3;
		}
	}

	private sealed class DataTableSelectMethod : FunctionMethod
	{
		public DataTableSelectMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.RefInt1D }, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		// NativeAOT 豁免（已登记：nativeaot-verify-report.md §5.2）：DataTable.Select 表达式
		// 触发 IL2026；tests/test_datatable_aot.py 13/13 实测可用，豁免附证据。
		[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "DataTable.Select 表达式实测 NativeAOT 可用（test_datatable_aot.py 13/13）")]
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return -1;
			string filter = (arguments.Count > 1 ? (arguments[1] != null ? arguments[1].GetStrValue(exm) : null) : null)!;
			string sort = (arguments.Count > 2 ? (arguments[2] != null ? arguments[2].GetStrValue(exm) : null) : null)!;
			DataRow[] res;
			if (sort != null) res = dt.Select(filter, sort);
			else if (filter != null) res = dt.Select(filter);
			else res = dt.Select();
			bool toResult = arguments.Count != 4;
			long[] output = toResult ? GlobalStatic.VEvaluator.RESULT_ARRAY : ((arguments[3] as VariableTerm)!.Identifier.GetArray() as long[])!;
			if (res != null)
			{
				int count = Math.Min(res.Length, toResult ? output.Length - 1 : output.Length);
				for (int i = 0; i < count; i++)
					output[toResult ? i + 1 : i] = (long)res[i][0];
				if (toResult) output[0] = res.Length;
				return res.Length;
			}
			if (toResult) output[0] = 0;
			return 0;
		}
	}

	private sealed class DataTableToXmlMethod : FunctionMethod
	{
		public DataTableToXmlMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString }, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		// NativeAOT 豁免（已登记：nativeaot-verify-report.md §5.2）：DataTable.WriteXmlSchema/WriteXml
		// 触发 IL2026/IL3050；tests/test_datatable_aot.py 13/13 实测可用，豁免附证据。
		[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "DataTable XML 序列化实测 NativeAOT 可用（test_datatable_aot.py 13/13）")]
		[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "DataTable XML 序列化实测 NativeAOT 可用（test_datatable_aot.py 13/13）")]
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			if (!dict.TryGetValue(key, out var dt)) return string.Empty;
			var output = arguments.Count > 1 ? ((arguments[1] as VariableTerm)!.Identifier.GetArray() as string[])! : GlobalStatic.VEvaluator.RESULTS_ARRAY;
			var idx = arguments.Count > 1 ? 0 : 1;

			var sb = new StringBuilder();
			using (var sw = new StringWriter(sb))
			{
				dt.WriteXmlSchema(sw);
				output[idx] = sb.ToString();
				sb.Clear();
				dt.WriteXml(sw);
				return sb.ToString();
			}
		}
	}

	private sealed class DataTableFromXmlMethod : FunctionMethod
	{
		public DataTableFromXmlMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		// NativeAOT 豁免（已登记：nativeaot-verify-report.md §5.2）：DataTable.ReadXmlSchema/ReadXml
		// 触发 IL2026/IL3050；tests/test_datatable_aot.py 13/13 实测可用，豁免附证据。
		[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "DataTable XML 反序列化实测 NativeAOT 可用（test_datatable_aot.py 13/13）")]
		[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "DataTable XML 反序列化实测 NativeAOT 可用（test_datatable_aot.py 13/13）")]
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var key = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataDataTables;
			DataTable dt;
			try
			{
				dt = new DataTable(key);
				using (var reader = new StringReader(arguments[1].GetStrValue(exm)))
				{
					dt.ReadXmlSchema(reader);
				}
				using (var reader = new StringReader(arguments[2].GetStrValue(exm)))
				{
					dt.ReadXml(reader);
				}
			}
			catch (Exception e)
			{
				if (e is CodeEE)
					throw;
				EmueraLog.Warn(Name, e.Message);
				return 0;
			}
			dict[key] = dt;
			return 1;
		}
	}
}
