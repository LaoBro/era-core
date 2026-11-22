using MinorShift.Emuera.Files;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MinorShift.Emuera.Runtime.Script.Data;

/// <summary>
/// 票 47（Skia 18.0.0）：CONSTTABLE 常数表的一维子集。
///
/// 一维值表每行形如 [注册数, 注册名, 值]：
/// - 空行与以 ';' 开头的注释行忽略；全空字段行（`,,`）按视觉分隔忽略、不占有效行号；
/// - 注册数与注册名都留空 → 顺延上一格索引；只写注册数 → 按编号；有注册名却没写注册数 → 坏行；
/// - 留空的单元格保持默认值（0 / 0.0 / ""），非空但解析不了才算坏格；
/// - 字段切分走 CSV 规则（RFC4180 读取侧）：以双引号开头的字段按引号字段解析，`""` 为字面引号；
/// - 超出声明长度的索引不读；同一索引两个名字 → 主表报错 / 补充表警告并保留先者。
///
/// 同名文件的合并与预设变量 ERD 扩展一致：CSV/&lt;名称&gt;.csv 是主表，
/// CSV/&lt;名称&gt;.erd 与 ERB/**/&lt;名称&gt;.ERD 是补充来源；冲突一律 Lv1 以主表为准。
/// 二维布局（@1/@2）在票 48 实现；本类遇到布局标记会按「只能用于二维」拒绝。
/// </summary>
internal static class ConstArrayTable
{
	internal const int KindInt = 0;
	internal const int KindStr = 1;
	internal const int KindFloat = 2;

	private const string Marker1 = "@1";
	private const string Marker2 = "@2";

	/// <summary>加载结果。Names 为一维 name→index，Values 为按索引展平的数组。</summary>
	internal sealed class Result
	{
		public string Path = null!;
		public Dictionary<string, int>[] Names = null!;
		public object Values = null!;
		/// <summary>补充表冲突、补充表坏格等 Lv1 提示。</summary>
		public List<string> Warnings = [];
		/// <summary>已被常数表消费的文件，调用方不要再交给 EE_ERD 解析。</summary>
		public List<string> ConsumedPaths = [];
	}

	private sealed class Parsed
	{
		public object Values = null!;
		public int ValueKind;
		/// <summary>该格是否在文件里被显式写过；用于区分「写了 0」与「没写」。</summary>
		public bool[] Written = null!;
		public Dictionary<string, int> NameIndex = null!;
		/// <summary>index→name（该索引的第一个名字），用于冲突判定。</summary>
		public string[] NameAt = null!;
	}

	private sealed class Invalid
	{
		public string Path = null!;
		public string Reason = null!;
	}

	private sealed class Candidate
	{
		public string Path = null!;
		public List<string[]> Rows = null!;
	}

	/// <summary>
	/// 按声明尺寸加载同名常数表：先选主表，再把其余同名表作为补充合并进来。
	/// 返回 null 表示没有任何候选可用，error 说明原因。
	/// </summary>
	public static Result Load(string varName, int[] lengths, int valueKind, out string error)
	{
		error = null!;
		List<Candidate> candidates = FindCandidates(varName, out List<Invalid> invalids, out string discoveryError);
		if (candidates.Count == 0)
		{
			error = discoveryError;
			return null!;
		}

		var reasons = new List<string>();
		// 主表严格校验失败的候选不再参与宽松合并：内容写坏的表整份作废。
		var rejected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var unfitCandidates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		Parsed parsedMain = null!;
		string mainPath = null!;
		int mainIndex = -1;
		List<string> mainWarnings = null!;
		for (int i = 0; i < candidates.Count; i++)
		{
			Candidate candidate = candidates[i];
			var trialMainWarnings = new List<string>();
			if (TryParse(candidate.Path, candidate.Rows, lengths, valueKind, true, trialMainWarnings,
				out Parsed parsed, out string reason, out bool unfit))
			{
				mainPath = candidate.Path;
				parsedMain = parsed;
				mainIndex = i;
				mainWarnings = trialMainWarnings;
				break;
			}
			reasons.Add(candidate.Path + " — " + reason);
			rejected[candidate.Path] = reason;
			unfitCandidates[candidate.Path] = unfit;
		}
		if (parsedMain == null)
		{
			error = "同名常数表均不适用于该声明：" + string.Join("；", reasons);
			return null!;
		}

		var result = new Result
		{
			Path = mainPath,
			Names = [parsedMain.NameIndex],
			Values = parsedMain.Values
		};
		if (mainWarnings != null)
			result.Warnings.AddRange(mainWarnings);
		var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainPath };
		result.ConsumedPaths.Add(mainPath);
		// 别名表只为「真正参与合并」的文件加载：被作废的候选其 .als 指向的索引已无意义。
		var aliasSources = new List<string> { mainPath };

		foreach (KeyValuePair<string, string> pair in rejected)
		{
			if (!unfitCandidates.TryGetValue(pair.Key, out bool unfit) || !unfit)
				result.Warnings.Add("同名常数表候选 " + pair.Key + " 未通过严格校验（" + pair.Value + "），已整份跳过");
			if (consumed.Add(pair.Key))
				result.ConsumedPaths.Add(pair.Key);
		}

		for (int i = 0; i < candidates.Count; i++)
		{
			if (i == mainIndex)
				continue;
			Candidate candidate = candidates[i];
			if (rejected.ContainsKey(candidate.Path))
				continue;
			var trialWarnings = new List<string>();
			if (!TryParse(candidate.Path, candidate.Rows, lengths, valueKind, false, trialWarnings,
				out Parsed extension, out _, out _))
				continue;
			result.Warnings.AddRange(trialWarnings);
			MergeValueTable(parsedMain, extension, lengths[0], candidate.Path, result.Warnings);
			aliasSources.Add(candidate.Path);
			if (consumed.Add(candidate.Path))
				result.ConsumedPaths.Add(candidate.Path);
		}

		// EE_ERD 名称表：<名称>.ERD 与 <名称>@1.ERD，只补名称。
		var layoutMarked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (Candidate candidate in candidates)
			if (HasLayoutMarkers(candidate.Rows))
				layoutMarked.Add(candidate.Path);
		foreach (string suffix in new[] { "", "@1" })
		{
			foreach (string path in FindFiles(varName + suffix + ".erd", true))
			{
				if (consumed.Contains(path))
					continue;
				if (layoutMarked.Contains(path))
				{
					if (consumed.Add(path))
						result.ConsumedPaths.Add(path);
					continue;
				}
				if (!MergeNameFile(path, lengths[0], parsedMain, result.Warnings,
					out int skippedRows, out string firstBadNote))
					continue;
				aliasSources.Add(path);
				if (consumed.Add(path))
					result.ConsumedPaths.Add(path);
				if (skippedRows > 0)
					result.Warnings.Add(path + " 有 " + skippedRows + " 个有效行不是「编号,名称」格式（首个在 "
						+ firstBadNote + "），已跳过；名称里含逗号时要用双引号把名称包住");
			}
		}
		// 既没当成值表、也没当成名称表的同名文件，才真的是「不是可用的常数表」。
		foreach (Invalid invalid in invalids)
		{
			if (consumed.Contains(invalid.Path))
				continue;
			result.Warnings.Add("同名文件 " + invalid.Path + " 不是可用的常数表（" + invalid.Reason + "），已跳过");
			if (consumed.Add(invalid.Path))
				result.ConsumedPaths.Add(invalid.Path);
		}
		// 同名 .als 别名表：正式名优先，别名不覆盖已有名字。
		LoadAliasFiles(aliasSources, lengths[0], parsedMain, result.Warnings);
		return result;
	}

	#region 候选与索引

	// CSV / ERB 目录按文件名建索引，避免每个常数表声明都递归枚举一次 ERB。
	// 重新解析 ERH 时由 ErhLoader.LoadHeaderFiles 入口清空（常数表声明只在 ERH 里读）。
	private static Dictionary<string, List<string>>? csvFileIndex;
	private static Dictionary<string, List<string>>? erbFileIndex;

	public static void ClearFileIndex()
	{
		csvFileIndex = null;
		erbFileIndex = null;
	}

	private static List<string> FindFiles(string fileName, bool fromErb)
	{
		Dictionary<string, List<string>> index;
		if (fromErb)
		{
			erbFileIndex ??= BuildErbIndex();
			index = erbFileIndex;
		}
		else
		{
			csvFileIndex ??= BuildCsvIndex();
			index = csvFileIndex;
		}
		return index.TryGetValue(fileName, out List<string>? list) ? list : [];
	}

	private static Dictionary<string, List<string>> BuildCsvIndex()
	{
		var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		string? dir = Program.CsvDir;
		IGameDirAccessor accessor = Program.DirAccessor;
		if (string.IsNullOrEmpty(dir) || !accessor.DirectoryExists(dir))
			return index;
		try
		{
			foreach (string path in accessor.GetFiles(dir, "*", SearchOption.TopDirectoryOnly))
			{
				string name = SafPath.GetLogicalFileName(path);
				if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
					&& !name.EndsWith(".erd", StringComparison.OrdinalIgnoreCase))
					continue;
				Add(index, name, path);
			}
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		SortIndex(index);
		return index;
	}

	private static Dictionary<string, List<string>> BuildErbIndex()
	{
		var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
		string? dir = Program.ErbDir;
		IGameDirAccessor accessor = Program.DirAccessor;
		if (string.IsNullOrEmpty(dir) || !accessor.DirectoryExists(dir))
			return index;
		try
		{
			foreach (KeyValuePair<string, string> pair in Preload.EnumerateFilesFromTree(dir, accessor, ".erd"))
				Add(index, SafPath.GetLogicalFileName(pair.Value), pair.Value);
		}
		catch (IOException) { }
		catch (UnauthorizedAccessException) { }
		SortIndex(index);
		return index;
	}

	private static void Add(Dictionary<string, List<string>> index, string name, string path)
	{
		if (!index.TryGetValue(name, out List<string>? list))
		{
			list = [];
			index.Add(name, list);
		}
		list.Add(path);
	}

	private static void SortIndex(Dictionary<string, List<string>> index)
	{
		foreach (List<string> list in index.Values)
			list.Sort(StringComparer.OrdinalIgnoreCase);
	}

	private static List<Candidate> FindCandidates(string varName, out List<Invalid> invalids, out string error)
	{
		var ret = new List<Candidate>();
		invalids = [];
		error = null!;
		if (string.IsNullOrEmpty(varName))
		{
			error = "常数表名称为空";
			return ret;
		}
		var candidates = new List<string>();
		candidates.AddRange(FindFiles(varName + ".csv", false));
		candidates.AddRange(FindFiles(varName + ".erd", false));
		candidates.AddRange(FindFiles(varName + ".erd", true));
		foreach (string candidatePath in candidates)
		{
			if (TryReadTable(candidatePath, out List<string[]> tableRows, out string reason))
				ret.Add(new Candidate { Path = candidatePath, Rows = tableRows });
			else
				invalids.Add(new Invalid { Path = candidatePath, Reason = reason });
		}
		if (ret.Count > 0)
			return ret;
		if (invalids.Count > 0)
		{
			var sb = new StringBuilder("常数表的格式不合规：");
			for (int i = 0; i < invalids.Count; i++)
			{
				if (i > 0)
					sb.Append('；');
				sb.Append(invalids[i].Path).Append(" — ").Append(invalids[i].Reason);
			}
			error = sb.ToString();
		}
		else
			error = "常数表声明找不到同名文件（CSV/" + varName + ".csv、CSV/" + varName + ".erd，或 ERB 下的 "
				+ varName + ".ERD）";
		return ret;
	}

	#endregion

	#region 解析

	private static bool TryParse(string path, List<string[]> rows, int[] lengths, int valueKind,
		bool strict, List<string> warnings, out Parsed parsed, out string error, out bool unfit)
	{
		parsed = null!;
		error = null!;
		// unfit：维度/布局这类「不是给这个声明用的」失败，回退下一份即可；
		// 其余是内容写错，要报给用户，不能当补充表宽松合并。
		unfit = true;
		int size = lengths.Length == 1 ? lengths[0] : 0;
		if (size <= 0 || size > 1000000)
		{
			error = "常数表对应的数组尺寸非法";
			return false;
		}

		var table = new Parsed
		{
			Values = valueKind == KindStr ? new string[size] : valueKind == KindFloat ? new double[size] : new long[size],
			ValueKind = valueKind,
			Written = new bool[size],
			NameIndex = [],
			NameAt = new string[size],
		};
		if (valueKind == KindStr)
		{
			string[] strValues = (string[])table.Values;
			for (int i = 0; i < strValues.Length; i++)
				strValues[i] = "";
		}

		if (HasMarkers(rows) && HasLayoutMarkers(rows))
		{
			error = "含 @1 / @2 的常数表只能用于二维数组（二维布局由后续票实现）";
			return false;
		}
		unfit = false;
		if (!Fill1D(rows, lengths[0], valueKind, table, strict, warnings, path, out error))
			return false;
		parsed = table;
		return true;
	}

	private static bool Fill1D(List<string[]> rows, int length, int valueKind, Parsed table,
		bool strict, List<string> warnings, string path, out string error)
	{
		error = null!;
		int lastIndex = -1;
		bool warnedOverflow = false;
		for (int r = 0; r < rows.Count; r++)
		{
			string[] row = rows[r];
			int line = r + 1;
			if (row.Length < 3)
			{
				if (strict)
				{
					error = "第 " + line + " 个有效行不足 3 列";
					return false;
				}
				warnings.Add(ExtensionNote(path) + "第 " + line + " 个有效行不足 3 列；跳过该行");
				continue;
			}
			// 一维表固定三列，多出来只可能是值里的逗号没加引号被切开了。
			if (row.Length > 3)
			{
				string msg = "第 " + line + " 个有效行有 " + row.Length + " 列，多于一维表固定的 3 列："
					+ "值里含逗号时要用双引号把整个值包住（如 \"a,b\"）";
				if (strict)
				{
					error = msg;
					return false;
				}
				warnings.Add(ExtensionNote(path) + msg + "；多出的列被忽略");
			}
			int index;
			if (row[0].Length > 0)
			{
				if (!int.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0)
				{
					if (strict)
					{
						error = "第 " + line + " 个有效行的注册数不是非负整数：" + row[0];
						return false;
					}
					warnings.Add(ExtensionNote(path) + "第 " + line + " 个有效行的注册数不是非负整数："
						+ row[0] + "；跳过该行");
					continue;
				}
				index = n;
				lastIndex = n;
			}
			else
			{
				// 有注册名就必须写明注册数；两者都空才按前一行顺延。
				if (row[1].Length > 0)
				{
					string msg = "第 " + line + " 个有效行写了注册名「" + row[1] + "」却没有注册数";
					if (strict)
					{
						error = msg + "；有注册名就必须写注册数";
						return false;
					}
					warnings.Add(ExtensionNote(path) + msg + "；跳过该行");
					continue;
				}
				index = ++lastIndex;
			}
			if (index < 0 || index >= length)
			{
				if (!warnedOverflow && row[2].Length > 0)
				{
					warnedOverflow = true;
					warnings.Add(TableNote(path, strict) + "第 " + line + " 个有效行的索引 " + index
						+ " 超出声明长度（" + length + "），该行已忽略；值里含逗号时要用双引号把整个值包住（如 \"a,b\"）");
				}
				continue;
			}
			if (row[1].Length > 0)
			{
				string? conflict = TryAddName(table, index, row[1]);
				if (conflict != null)
				{
					if (strict)
					{
						error = "第 " + line + " 个有效行：" + conflict;
						return false;
					}
					warnings.Add(ExtensionNote(path) + "第 " + line + " 个有效行：" + conflict + "；跳过该名字");
				}
			}
			if (!TryConvert(row[2], valueKind, out object? value, out string convertError))
			{
				if (strict)
				{
					error = "第 " + line + " 个有效行的值" + convertError + "：" + row[2];
					return false;
				}
				warnings.Add(ExtensionNote(path) + "第 " + line + " 个有效行的值" + convertError
					+ "：" + row[2] + "；跳过该格");
				continue;
			}
			if (value != null)
			{
				SetValue(table.Values, index, valueKind, value);
				table.Written[index] = true;
			}
		}
		return true;
	}

	private static void MergeValueTable(Parsed main, Parsed extension, int length, string extensionPath,
		List<string> warnings)
	{
		MergeNames(main, extension.NameIndex, length, extensionPath, warnings);

		for (int i = 0; i < main.Written.Length; i++)
		{
			if (!extension.Written[i])
				continue;
			object value = GetValue(extension.Values, i, main.ValueKind);
			if (!main.Written[i])
			{
				SetValue(main.Values, i, main.ValueKind, value);
				main.Written[i] = true;
				continue;
			}
			object current = GetValue(main.Values, i, main.ValueKind);
			if (!SameValue(current, value, main.ValueKind))
				warnings.Add(ExtensionNote(extensionPath) + "索引 " + i + " 的值与主表不同（主表="
					+ FormatValue(current, main.ValueKind) + ", 补充=" + FormatValue(value, main.ValueKind)
					+ "）；以主表为准");
		}
	}

	private static void MergeNames(Parsed main, Dictionary<string, int> extensionIndex, int length,
		string extensionPath, List<string> warnings)
	{
		foreach (KeyValuePair<string, int> pair in extensionIndex)
			MergeOneName(main, length, pair.Value, pair.Key, extensionPath, warnings);
	}

	private static void MergeOneName(Parsed main, int length, int index, string name,
		string extensionPath, List<string> warnings)
	{
		if (main.NameIndex.TryGetValue(name, out int exists))
		{
			if (exists != index)
				warnings.Add(ExtensionNote(extensionPath) + "索引 " + index + " 的名字\"" + name
					+ "\"已存在于索引 " + exists + "；跳过");
			return;
		}
		if (index < 0 || index >= length)
			return;
		string? current = main.NameAt[index];
		if (current != null)
		{
			if (!string.Equals(current, name, StringComparison.Ordinal))
				warnings.Add(ExtensionNote(extensionPath) + "索引 " + index + " 的名字与主表不同（主表=\""
					+ current + "\", 补充=\"" + name + "\"）；以主表为准");
			return;
		}
		main.NameIndex.Add(name, index);
		main.NameAt[index] = name;
	}

	/// <summary>
	/// 读取 EE_ERD 风格的维度名称表（&lt;名称&gt;.ERD / &lt;名称&gt;@1.ERD）。
	/// 每行为 [编号, 名称]，切分与值表同一套规则；只补名称。
	/// 返回 true 表示这份文件确实是名称表。
	/// </summary>
	private static bool MergeNameFile(string path, int length, Parsed main, List<string> warnings,
		out int skippedRows, out string firstBadNote)
	{
		bool sawRow = false;
		skippedRows = 0;
		firstBadNote = null!;
		int validLine = 0;
		try
		{
			foreach (string raw in ReadAllLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == ';')
					continue;
				if (!TrySplitQuoted(line, out string[] cells, out _, out string why))
				{
					if (why.StartsWith('的'))
						why = why[1..];
					validLine++;
					if (skippedRows++ == 0)
						firstBadNote = "第 " + validLine + " 个有效行（" + why + "）";
					continue;
				}
				if (IsAllEmpty(cells))
					continue;
				validLine++;
				string? badWhy = null;
				int index = 0;
				if (cells.Length < 2)
					badWhy = "不足「编号,名称」两列";
				else if (!int.TryParse(cells[0].Trim().Trim('\uFEFF').Trim(), NumberStyles.Integer,
					CultureInfo.InvariantCulture, out index))
					badWhy = "首列不是编号：" + cells[0].Trim().Trim('\uFEFF').Trim();
				if (badWhy != null)
				{
					if (skippedRows++ == 0)
						firstBadNote = "第 " + validLine + " 个有效行（" + badWhy + "）";
					continue;
				}
				sawRow = true;
				string name = cells[1].Trim().Trim('\uFEFF').Trim();
				if (name.Length == 0)
					continue;
				MergeOneName(main, length, index, name, path, warnings);
			}
		}
		catch (Exception e)
		{
			warnings.Add(path + " 读取失败（" + e.Message + "），该名称表未生效");
			return true;
		}
		return sawRow;
	}

	private static void LoadAliasFiles(IEnumerable<string> consumedPaths, int length, Parsed main,
		List<string> warnings)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string source in consumedPaths)
		{
			string stem = SafPath.GetLogicalFileNameWithoutExtension(source);
			IGameDirAccessor accessor = Program.DirAccessor;
			string aliasPath = accessor.CombinePath(accessor.GetParentPath(source), stem + ".als");
			if (!seen.Add(aliasPath) || !SafCompat.FileExists(aliasPath))
				continue;
			MergeAliasFile(aliasPath, length, main, warnings);
		}
	}

	private static void MergeAliasFile(string path, int length, Parsed main, List<string> warnings)
	{
		int skipped = 0;
		string? firstBadNote = null;
		int validLine = 0;
		try
		{
			foreach (string raw in ReadAllLines(path))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == ';')
					continue;
				if (!TrySplitQuoted(line, out string[] cells, out _, out string why))
				{
					if (why.StartsWith('的'))
						why = why[1..];
					validLine++;
					if (skipped++ == 0)
						firstBadNote = "第 " + validLine + " 个有效行（" + why + "）";
					continue;
				}
				if (IsAllEmpty(cells))
					continue;
				validLine++;
				string? badWhy = null;
				int index = 0;
				if (cells.Length < 2)
					badWhy = "不足「编号,别名」两列";
				else if (!int.TryParse(cells[0].Trim().Trim('\uFEFF').Trim(), NumberStyles.Integer,
					CultureInfo.InvariantCulture, out index))
					badWhy = "首列不是编号：" + cells[0].Trim().Trim('\uFEFF').Trim();
				if (badWhy != null)
				{
					if (skipped++ == 0)
						firstBadNote = "第 " + validLine + " 个有效行（" + badWhy + "）";
					continue;
				}
				string name = cells[1].Trim().Trim('\uFEFF').Trim();
				if (name.Length == 0)
					continue;
				if (index < 0 || index >= length)
					continue;
				if (main.NameIndex.ContainsKey(name))
					continue;
				main.NameIndex.Add(name, index);
				main.NameAt[index] ??= name;
			}
		}
		catch (Exception e)
		{
			warnings.Add(path + " 读取失败（" + e.Message + "），该别名表未生效");
			return;
		}
		if (skipped > 0)
			warnings.Add(path + " 有 " + skipped + " 个有效行不是「编号,别名」格式（首个在 " + firstBadNote
				+ "），已跳过；别名里含逗号时要用双引号把别名包住");
	}

	#endregion

	#region 工具

	private static bool IsAllEmpty(string[] cells)
	{
		foreach (string cell in cells)
			if (cell.Trim().Length > 0)
				return false;
		return true;
	}

	/// <summary>转换非空值；空数值返回 value=null，表示保持默认值。非空但非法时返回 false。</summary>
	private static bool TryConvert(string cell, int valueKind, out object? value, out string error)
	{
		value = null;
		error = null!;
		if (cell.Length == 0)
			return true;
		if (valueKind == KindStr)
		{
			value = cell;
			return true;
		}
		if (valueKind == KindFloat)
		{
			if (!double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
			{
				error = "不是有效的浮点数";
				return false;
			}
			value = f;
			return true;
		}
		if (!long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
		{
			error = "不是有效的整数";
			return false;
		}
		value = i;
		return true;
	}

	/// <summary>
	/// 登记一条「索引→名字」。同名已在别处登记时保留先出现者（返回 null，不算冲突）；
	/// 该索引已被别的名字占用时返回冲突说明。
	/// </summary>
	private static string? TryAddName(Parsed table, int index, string name)
	{
		if (table.NameIndex.ContainsKey(name))
			return null;
		string? current = table.NameAt[index];
		if (current != null)
			return "索引 " + index + " 已有名字「" + current + "」，不能再取名「" + name + "」";
		table.NameIndex.Add(name, index);
		table.NameAt[index] = name;
		return null;
	}

	private static void SetValue(object values, int index, int valueKind, object value)
	{
		if (valueKind == KindStr)
			((string[])values)[index] = (string)value;
		else if (valueKind == KindFloat)
			((double[])values)[index] = (double)value;
		else
			((long[])values)[index] = (long)value;
	}

	private static object GetValue(object values, int index, int valueKind)
	{
		if (valueKind == KindStr)
			return ((string[])values)[index];
		if (valueKind == KindFloat)
			return ((double[])values)[index];
		return ((long[])values)[index];
	}

	private static bool SameValue(object a, object b, int valueKind)
	{
		if (valueKind == KindStr)
			return string.Equals((string)a, (string)b, StringComparison.Ordinal);
		if (valueKind == KindFloat)
		{
			double x = (double)a, y = (double)b;
			return x == y || (double.IsNaN(x) && double.IsNaN(y));
		}
		return (long)a == (long)b;
	}

	private static string FormatValue(object value, int valueKind)
	{
		if (valueKind == KindStr)
			return "\"" + value + "\"";
		return Convert.ToString(value, CultureInfo.InvariantCulture)!;
	}

	private static string ExtensionNote(string path)
	{
		return "常数表补充表(" + path + ")中，";
	}

	private static string TableNote(string path, bool strict)
	{
		return strict ? "常数表(" + path + ")中，" : ExtensionNote(path);
	}

	/// <summary>
	/// 读取表格文本。切分规则与 CSV 一致：以双引号开头的字段按引号字段解析，
	/// 引号内的逗号属于值，`""` 表示一个字面引号；其余字段取到下一个逗号。
	/// 「第 N 个有效行」的口径与 rows 一致：全空字段的行（`,,`）不占行号。
	/// </summary>
	private static List<string[]> ReadGrid(string path, out string error)
	{
		error = null!;
		var lines = new List<string>();
		foreach (string raw in ReadAllLines(path))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line[0] == ';')
				continue;
			lines.Add(line);
		}

		var rows = new List<string[]>();
		for (int n = 0; n < lines.Count; n++)
		{
			if (!TrySplitQuoted(lines[n], out string[] cells, out int badField, out string why))
			{
				error = "第 " + (rows.Count + 1) + " 个有效行的第 " + badField + " 个字段" + why;
				return null!;
			}
			bool any = false;
			for (int i = 0; i < cells.Length; i++)
			{
				cells[i] = cells[i].Trim().Trim('\uFEFF').Trim();
				if (cells[i].Length > 0)
					any = true;
			}
			if (any)
				rows.Add(cells);
		}
		return rows;
	}

	/// <summary>CSV 切分（RFC 4180 读取侧）。引号没闭合、或结束引号后还有内容 → 格式错误。</summary>
	private static bool TrySplitQuoted(string line, out string[] cells, out int badField, out string why)
	{
		var list = new List<string>();
		var sb = new StringBuilder();
		badField = 0;
		why = null!;
		cells = null!;
		int i = 0;
		while (true)
		{
			while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
				i++;
			int fieldStart = i;
			sb.Clear();
			if (i < line.Length && line[i] == '"')
			{
				i++;
				bool closed = false;
				while (i < line.Length)
				{
					char ch = line[i];
					if (ch == '"')
					{
						if (i + 1 < line.Length && line[i + 1] == '"')
						{
							sb.Append('"');
							i += 2;
							continue;
						}
						i++;
						closed = true;
						break;
					}
					sb.Append(ch);
					i++;
				}
				if (!closed)
				{
					badField = list.Count + 1;
					why = "的双引号没有闭合（值里的引号要写成两个双引号，整段值也要用一对双引号包住）";
					return false;
				}
				while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
					i++;
				if (i < line.Length && line[i] != ',')
				{
					int end = line.IndexOf(',', i);
					string raw = end < 0 ? line[fieldStart..] : line[fieldStart..end];
					badField = list.Count + 1;
					why = "在结束引号后还有内容（值里的引号要写成两个双引号）；按这个规则整段值要写成 "
						+ "\"" + raw.Replace("\"", "\"\"") + "\"";
					return false;
				}
			}
			else
			{
				int start = i;
				while (i < line.Length && line[i] != ',')
					i++;
				sb.Append(line, start, i - start);
			}
			list.Add(sb.ToString());
			if (i >= line.Length)
				break;
			i++;
			if (i >= line.Length)
			{
				list.Add("");
				break;
			}
		}
		cells = [.. list];
		return true;
	}

	private static bool TryReadTable(string path, out List<string[]> rows, out string reason)
	{
		reason = null!;
		try
		{
			rows = ReadGrid(path, out reason);
			if (rows == null)
				return false;
		}
		catch (Exception e)
		{
			rows = null!;
			reason = "读取失败（" + e.Message + "）";
			return false;
		}
		return CheckTable(rows, out reason);
	}

	/// <summary>判定内容是否为本表格式。含 @1/@2 的按二维表候选；否则要求每行三列。</summary>
	private static bool CheckTable(List<string[]> rows, out string reason)
	{
		reason = null!;
		if (rows.Count == 0)
		{
			reason = "空文件，或只有空行与注释行";
			return false;
		}
		if (HasMarkers(rows))
			return true;
		for (int i = 0; i < rows.Count; i++)
		{
			if (rows[i].Length < 3)
			{
				reason = "第 " + (i + 1) + " 个有效行不足 3 列（一维表需要 [注册数,注册名,值]）";
				return false;
			}
			if (rows[i][0].Length > 0
				&& !int.TryParse(rows[i][0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
			{
				reason = "第 " + (i + 1) + " 个有效行的首列不是编号";
				return false;
			}
		}
		return true;
	}

	private static bool HasLayoutMarkers(List<string[]> rows)
	{
		bool vertical = rows.Count > 1 && rows[1].Length > 0 && IsMarker(rows[1][0]);
		bool horizontal = rows.Count > 0 && rows[0].Length > 1 && IsMarker(rows[0][1]);
		return vertical && horizontal;
	}

	private static bool IsMarker(string cell)
	{
		return cell == Marker1 || cell == Marker2;
	}

	private static bool HasMarkers(List<string[]> rows)
	{
		foreach (string[] row in rows)
			foreach (string cell in row)
				if (cell == Marker1 || cell == Marker2)
					return true;
		return false;
	}

	private static List<string> ReadAllLines(string path)
	{
		var lines = new List<string>();
		Stream? stream = SafCompat.OpenRead(path);
		if (stream == null)
			throw new FileNotFoundException(path);
		using (stream)
		using (var reader = new StreamReader(stream, EncodingHandler.DetectEncoding(stream)))
		{
			string? line;
			while ((line = reader.ReadLine()) != null)
				lines.Add(line);
		}
		return lines;
	}

	#endregion
}
