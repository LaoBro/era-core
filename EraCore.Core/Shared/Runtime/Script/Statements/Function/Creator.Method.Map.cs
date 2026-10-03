using MinorShift.Emuera.GameData.Variable;
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
using System.Linq;
using System.Text;
using System.Xml;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class MapManagementMethod : FunctionMethod
	{
		public enum Operation { Create, Check, Release };
		public MapManagementMethod(Operation type)
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
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			bool contains = dict.ContainsKey(key);
			switch (op)
			{
				case Operation.Check: { return contains ? 1 : 0; }
				case Operation.Release: { if (contains) dict.Remove(key); return 1; }
			}
			if (contains) return 0;
			dict[key] = [];
			return 1;
		}
	}

	private sealed class MapDataOperationMethod : FunctionMethod
	{
		public enum Operation { Set, Has, Remove, Clear, Size };
		public MapDataOperationMethod(Operation type)
		{
			ReturnType = typeof(long);
			switch (type)
			{
				case Operation.Set:
					argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid } }]; break;
				case Operation.Has:
				case Operation.Remove:
					argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid } }]; break;
				default:
					argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }]; break;
			}
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var map = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			if (!dict.TryGetValue(map, out var sMap)) return -1;
			if (op == Operation.Clear) sMap.Clear();
			else if (op == Operation.Size) return sMap.Count;
			else
			{
				var key = arguments[1].GetStrValue(exm);
				bool contains = sMap.ContainsKey(key);
				if (op == Operation.Has) return contains ? 1 : 0;
				if (op == Operation.Remove)
					sMap.Remove(key);
				else
					sMap[key] = arguments[2].GetStrValue(exm);
			}
			return 1;
		}
	}

	private sealed class MapGetStrMethod : FunctionMethod
	{
		public enum Operation { Get, ToXml, GetKeys };
		public MapGetStrMethod(Operation type)
		{
			ReturnType = typeof(string);
			switch (type)
			{
				case Operation.Get:
					argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid } }]; break;
				case Operation.ToXml:
					argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid } }]; break;
				case Operation.GetKeys:
					argumentTypeArrayEx = [
							new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int }, OmitStart = 1 },
							new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString1D, ArgType.Int } },
						]; break;
			}
			CanRestructure = false;
			op = type;
		}
		private Operation op;
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var map = arguments[0].GetStrValue(exm);
			if (!dict.TryGetValue(map, out var sMap)) return "";
			if (op == Operation.Get)
			{
				var key = arguments[1].GetStrValue(exm);
				if (sMap.TryGetValue(key, out var val)) return val;
				return "";
			}
			else if (op == Operation.GetKeys && arguments.Count > 1)
			{
				int count = 0;
				string[] array;
				if (arguments.Count == 3) // to array
				{
					var Term = (arguments[1] as VariableTerm)!;
					if (arguments[2].GetIntValue(exm) == 0) return "";
					array = (Term.Identifier.GetArray() as string[])!;
				}
				else if (arguments.Count == 2) // to RESULTS array
				{
					if (arguments[1].GetIntValue(exm) == 0) return "";
					array = exm.VEvaluator.RESULTS_ARRAY;
				}
				else return "";
				foreach (var k in sMap.Keys)
				{
					if (count >= array.Length) break;
					array[count] = k;
					count++;
				}
				exm.VEvaluator.RESULT = sMap.Keys.Count;
				return arguments.Count == 2 ? exm.VEvaluator.RESULTS : "";
			}
			StringBuilder sb = new();
			if (op == Operation.GetKeys)
			{
				bool isNotEmpty = false;
				foreach (var k in sMap.Keys)
				{
					if (isNotEmpty) sb.Append(',').Append(k);
					else
					{
						isNotEmpty = true;
						sb.Append(k);
					}
				}
			}
			else
			{
				sb.Append("<map>");
				foreach (var p in sMap)
					sb.Append(string.Format("<p><k>{0}</k><v>{1}</v></p>", p.Key, p.Value));
				sb.Append("</map>");
			}
			return sb.ToString();
		}
	}

	private sealed class MapFromXmlMethod : FunctionMethod
	{
		public MapFromXmlMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String | ArgType.DisallowVoid, ArgType.String | ArgType.DisallowVoid } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var map = arguments[0].GetStrValue(exm);
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			if (!dict.TryGetValue(map, out var sMap)) return 0;
			var xml = arguments[1].GetStrValue(exm);
			XmlDocument doc = new();
			XmlNodeList nodes;
			try
			{
				doc.LoadXml(xml);
				nodes = doc.SelectNodes("/map/p")!;
			}
			catch (XmlException e)
			{
				throw new CodeEE(string.Format(trerror.XmlParseError.Text, Name, xml, e.Message));
			}
			for (int i = 0; i < nodes.Count; i++)
			{
				XmlNodeList key, val;
				var node = nodes[i];
				key = node!.SelectNodes("./k")!;
				val = node!.SelectNodes("./v")!;
				if (key.Count != 1 || val.Count != 1) continue;
				sMap[key[0]!.InnerText] = val[0]!.InnerXml;
			}
			return 1;
		}
	}

	// Skia 方言 P0：MAP_VALUES（实现票 19）。无参形态返回逗号连接的值；带参形态写入变量数组
	// （三参）或 RESULTS（两参），并置 RESULT 为条目数。对齐 asset §4.1.2。
	private sealed class MapValuesMethod : FunctionMethod
	{
		public MapValuesMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.Int }, OmitStart = 1 },
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.RefString1D, ArgType.Int } },
				];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var map = arguments[0].GetStrValue(exm);
			if (!dict.TryGetValue(map, out var sMap)) return "";
			if (arguments.Count > 1)
			{
				int count = 0;
				if (arguments.Count == 3)
				{
					var Term = (arguments[1] as VariableTerm)!;
					if (arguments[2].GetIntValue(exm) == 0) return "";
					var array = (Term.Identifier.GetArray() as string[])!;
					foreach (var v in sMap.Values)
					{
						if (count >= array.Length) break;
						array[count] = v;
						count++;
					}
				}
				else if (arguments.Count == 2)
				{
					if (arguments[1].GetIntValue(exm) == 0) return "";
					var array = exm.VEvaluator.RESULTS_ARRAY;
					foreach (var v in sMap.Values)
					{
						if (count >= array.Length) break;
						array[count] = v;
						count++;
					}
				}
				else return "";
				exm.VEvaluator.RESULT = sMap.Values.Count;
				return arguments.Count == 2 ? exm.VEvaluator.RESULTS : "";
			}
			StringBuilder sb = new();
			bool isNotEmpty = false;
			foreach (var v in sMap.Values)
			{
				if (isNotEmpty) sb.Append(',').Append(v);
				else
				{
					isNotEmpty = true;
					sb.Append(v);
				}
			}
			return sb.ToString();
		}
	}

	// Skia 方言 P0：MAP_MERGE（实现票 19）。把 src 的全部键值覆盖写入 dest。两个 map 任一不存在返回 0。
	private sealed class MapMergeMethod : FunctionMethod
	{
		public MapMergeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var destMap = arguments[0].GetStrValue(exm);
			var srcMap = arguments[1].GetStrValue(exm);
			if (!dict.TryGetValue(destMap, out var dest) || !dict.TryGetValue(srcMap, out var src)) return 0;
			foreach (var kvp in src)
				dest[kvp.Key] = kvp.Value;
			return 1;
		}
	}

	// Skia 方言 P0：MAP_REMOVEIF（实现票 19）。按 mode 匹配键或值批量删除，返回删除条数；未知 mode 返回 -1。
	private sealed class MapRemoveIfMethod : FunctionMethod
	{
		public MapRemoveIfMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var map = arguments[0].GetStrValue(exm);
			if (!dict.TryGetValue(map, out var sMap)) return 0;
			var matchValue = arguments[1].GetStrValue(exm);
			var mode = arguments[2].GetStrValue(exm);
			List<string> toRemove = [];
			switch (mode)
			{
				case "KEY_CONTAINS":
					foreach (var k in sMap.Keys)
						if (k.Contains(matchValue)) toRemove.Add(k);
					break;
				case "KEY_PREFIX":
					foreach (var k in sMap.Keys)
						if (k.StartsWith(matchValue)) toRemove.Add(k);
					break;
				case "KEY_SUFFIX":
					foreach (var k in sMap.Keys)
						if (k.EndsWith(matchValue)) toRemove.Add(k);
					break;
				case "VAL_CONTAINS":
					foreach (var kvp in sMap)
						if (kvp.Value.Contains(matchValue)) toRemove.Add(kvp.Key);
					break;
				case "VAL_EQ":
					foreach (var kvp in sMap)
						if (kvp.Value == matchValue) toRemove.Add(kvp.Key);
					break;
				case "VAL_NE":
					foreach (var kvp in sMap)
						if (kvp.Value != matchValue) toRemove.Add(kvp.Key);
					break;
				default:
					return -1;
			}
			foreach (var k in toRemove)
				sMap.Remove(k);
			return toRemove.Count;
		}
	}

	// Skia 方言 P0：MAP_FINDKEY（实现票 19）。按 mode 返回逗号连接的匹配键，并置 RESULT 为匹配数。
	private sealed class MapFindKeyMethod : FunctionMethod
	{
		public MapFindKeyMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String } }];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var map = arguments[0].GetStrValue(exm);
			if (!dict.TryGetValue(map, out var sMap)) return "";
			var matchValue = arguments[1].GetStrValue(exm);
			var mode = arguments[2].GetStrValue(exm);
			StringBuilder sb = new();
			bool isNotEmpty = false;
			switch (mode)
			{
				case "KEY_CONTAINS":
					foreach (var k in sMap.Keys)
					{
						if (!k.Contains(matchValue)) continue;
						if (isNotEmpty) sb.Append(',');
						isNotEmpty = true;
						sb.Append(k);
					}
					break;
				case "KEY_PREFIX":
					foreach (var k in sMap.Keys)
					{
						if (!k.StartsWith(matchValue)) continue;
						if (isNotEmpty) sb.Append(',');
						isNotEmpty = true;
						sb.Append(k);
					}
					break;
				case "KEY_SUFFIX":
					foreach (var k in sMap.Keys)
					{
						if (!k.EndsWith(matchValue)) continue;
						if (isNotEmpty) sb.Append(',');
						isNotEmpty = true;
						sb.Append(k);
					}
					break;
				case "VAL_CONTAINS":
					foreach (var kvp in sMap)
					{
						if (!kvp.Value.Contains(matchValue)) continue;
						if (isNotEmpty) sb.Append(',');
						isNotEmpty = true;
						sb.Append(kvp.Key);
					}
					break;
				case "VAL_EQ":
					foreach (var kvp in sMap)
					{
						if (kvp.Value != matchValue) continue;
						if (isNotEmpty) sb.Append(',');
						isNotEmpty = true;
						sb.Append(kvp.Key);
					}
					break;
			}
			exm.VEvaluator.RESULT = sb.Length > 0 ? sb.ToString().Split(',').Length : 0;
			return sb.ToString();
		}
	}

	// Skia 方言 P0：MAP_TOSTRING（实现票 19）。缺省分隔符 "," 与键值分隔符 "="。
	private sealed class MapToStringMethod : FunctionMethod
	{
		public MapToStringMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String }, OmitStart = 1 },
				];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var map = arguments[0].GetStrValue(exm);
			if (!dict.TryGetValue(map, out var sMap)) return "";
			var sep = arguments.Count > 1 ? arguments[1].GetStrValue(exm) : ",";
			var kvSep = arguments.Count > 2 ? arguments[2].GetStrValue(exm) : "=";
			StringBuilder sb = new();
			bool isNotEmpty = false;
			foreach (var kvp in sMap)
			{
				if (isNotEmpty) sb.Append(sep);
				isNotEmpty = true;
				sb.Append(kvp.Key).Append(kvSep).Append(kvp.Value);
			}
			return sb.ToString();
		}
	}

	// Skia 方言 P0：MAP_FROMSTRING（实现票 19）。MAP_TOSTRING 的逆操作，返回成功解析的条数。
	private sealed class MapFromStringMethod : FunctionMethod
	{
		public MapFromStringMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.String, ArgType.String, ArgType.String, ArgType.String }, OmitStart = 2 },
				];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			var dict = exm.VEvaluator.VariableData.DataStringMaps;
			var map = arguments[0].GetStrValue(exm);
			if (!dict.TryGetValue(map, out var sMap)) return 0;
			var data = arguments[1].GetStrValue(exm);
			var sep = arguments.Count > 2 ? arguments[2].GetStrValue(exm) : ",";
			var kvSep = arguments.Count > 3 ? arguments[3].GetStrValue(exm) : "=";
			if (string.IsNullOrEmpty(data)) return 0;
			var entries = data.Split(new[] { sep }, StringSplitOptions.None);
			int count = 0;
			foreach (var entry in entries)
			{
				if (string.IsNullOrEmpty(entry)) continue;
				var idx = entry.IndexOf(kvSep);
				if (idx < 0) continue;
				var key = entry.Substring(0, idx);
				var val = entry.Substring(idx + kvSep.Length);
				sMap[key] = val;
				count++;
			}
			return count;
		}
	}
}
