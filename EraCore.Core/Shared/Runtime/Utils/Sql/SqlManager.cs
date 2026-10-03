using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using Microsoft.Data.Sqlite;

namespace MinorShift.Emuera.Runtime.Utils;

/// <summary>
/// SQL 语义深模块（实现票 28，spec §5）：连接表 + reader 表 + 路径归一 + 标识符加固 + 异常格式化
/// 全部收敛于此，ERB 包装层只做参数取值与 <see cref="SqlManagerException"/> → <c>CodeEE</c> 映射。
///
/// <para><b>依赖收口约束</b>：本文件是 Core 内唯一 <c>using Microsoft.Data.Sqlite</c> 的位置。
/// 包装器 / 注册层不得直接引用 SQLite 类型——这正是「单一 deep 收口点」的可验证边界。</para>
///
/// <para><b>深模块接口</b>：对外只暴露成对的 SQL 语义方法；失败一律抛 <see cref="SqlManagerException"/>
/// （不带 ERB 异常类型），由薄包装层转成 <c>CodeEE</c>。SQLite 类型不出现在公开签名里。</para>
///
/// <para><b>与 Skia 原版的差异</b>（吸收 gEmuera 加固，spec §5）：
/// ① 相对 <c>Data Source=</c> 按 <see cref="MinorShift.Emuera.Program.ExeDir"/> 归一（headless 的 CWD ≠ 游戏目录）；
/// ② 列取值走 <c>Convert</c> 宽容转换（eraTW 的 .db 列型未知）；
/// ③ 表名/列名经 <see cref="QuoteIdentifier"/> 加固（原版直接插值 = 注入面）；
/// ④ <see cref="Disconnect"/> 连带关闭该库 reader（原版悬空）。</para>
///
/// <para><b>平台</b>：先只交付 Windows 桌面闭环；Android/MAUI/NativeAOT 的原生库定位形态单开后续 issue（spec §9）。</para>
/// </summary>
internal static class SqlManager
{
	private sealed class ReaderContext
	{
		public required SqliteCommand Command { get; init; }
		public required SqliteDataReader Reader { get; init; }
		public required string DbName { get; init; }
	}

	private static readonly Dictionary<string, SqliteConnection> connections = new(StringComparer.OrdinalIgnoreCase);
	private static readonly Dictionary<long, ReaderContext> readers = [];
	private static long nextReaderId = 1;

	// ===== 连接生命周期 =====

	/// <summary>在 <c>SavDir/sql/&lt;name&gt;.db</c> 上打开（或重建）一个命名连接。</summary>
	public static long ConnectionOpen(string name)
	{
		if (string.IsNullOrWhiteSpace(name)
			|| name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
			|| name.Contains("..", StringComparison.Ordinal))
		{
			throw new SqlManagerException($"SQL_CONNECTION_OPEN: invalid database name '{name}'");
		}

		string dir = SafCompat.ResolveSubPath(global::MinorShift.Emuera.Runtime.Config.Config.SavDir, "sql");
		if (!SafCompat.DirectoryExists(dir))
			SafCompat.CreateDirectory(dir);
		string dbPath = SafCompat.CombinePath(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), name + ".db");
		return Connect(name, new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString, replaceExisting: true);
	}

	/// <summary>按连接串连接；<paramref name="replaceExisting"/> 为 false 且已连接时 no-op（对齐 gEmuera）。</summary>
	public static long Connect(string dbName, string? connectionString, bool replaceExisting)
	{
		if (string.IsNullOrWhiteSpace(dbName))
			throw new SqlManagerException("SQL_CONNECT: database name is empty");

		if (connections.ContainsKey(dbName))
		{
			if (!replaceExisting)
				return 1;
			Disconnect(dbName);
		}

		string normalized = NormalizeConnectionString(connectionString, Program.ExeDir);
		SqliteConnection? conn = null;
		try
		{
			conn = new SqliteConnection(normalized);
			conn.Open();
			using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
				cmd.ExecuteNonQuery();
			}
			connections[dbName] = conn;
			return 1;
		}
		catch (Exception ex)
		{
			conn?.Dispose();
			throw new SqlManagerException("SQL_CONNECT failed: " + ex.Message, ex);
		}
	}

	/// <summary>断开命名连接，并连带关闭其未关闭的 reader。</summary>
	public static long Disconnect(string dbName)
	{
		if (string.IsNullOrWhiteSpace(dbName))
			return 1;

		CloseReadersFor(dbName);
		if (connections.TryGetValue(dbName, out SqliteConnection? conn))
		{
			conn.Dispose();
			connections.Remove(dbName);
		}
		return 1;
	}

	// ===== 语句执行 =====

	public static long ExecuteNonQuery(string dbName, string sql, string[]? parameters = null)
	{
		using SqliteCommand cmd = CreateCommand(dbName, sql, parameters);
		try
		{
			return cmd.ExecuteNonQuery();
		}
		catch (Exception ex)
		{
			throw new SqlManagerException($"SQL execute error: {ex.Message}\nSQL: {sql}", ex);
		}
	}

	public static long ExecuteReader(string dbName, string sql, string[]? parameters = null)
	{
		SqliteCommand cmd = CreateCommand(dbName, sql, parameters);
		try
		{
			SqliteDataReader reader = cmd.ExecuteReader();
			long id = nextReaderId++;
			readers[id] = new ReaderContext { Command = cmd, Reader = reader, DbName = dbName };
			return id;
		}
		catch (Exception ex)
		{
			cmd.Dispose();
			throw new SqlManagerException($"SQL query error: {ex.Message}\nSQL: {sql}", ex);
		}
	}

	// ===== Reader =====

	public static long ReaderRead(long readerId)
	{
		try
		{
			return readers.TryGetValue(readerId, out ReaderContext? ctx) && ctx.Reader.Read() ? 1 : 0;
		}
		catch (Exception ex)
		{
			throw new SqlManagerException($"SQL reader read error (id={readerId}): {ex.Message}", ex);
		}
	}

	public static long ReaderGetLong(long readerId, int columnIndex)
		=> ReaderColumn(readerId, columnIndex, r => r.IsDBNull(columnIndex) ? 0L : Convert.ToInt64(r.GetValue(columnIndex), CultureInfo.InvariantCulture));

	public static string ReaderGetString(long readerId, int columnIndex)
		=> ReaderColumn(readerId, columnIndex, r => r.IsDBNull(columnIndex) ? "" : Convert.ToString(r.GetValue(columnIndex), CultureInfo.InvariantCulture) ?? "");

	public static long ReaderIsNull(long readerId, int columnIndex)
		=> ReaderColumn(readerId, columnIndex, r => r.IsDBNull(columnIndex) ? 1L : 0L);

	public static long ReaderClose(long readerId)
	{
		if (readers.TryGetValue(readerId, out ReaderContext? ctx))
		{
			ctx.Reader.Dispose();
			ctx.Command.Dispose();
			readers.Remove(readerId);
		}
		return 1;
	}

	// ===== 标量 =====

	public static long ExecuteScalarLong(string dbName, string sql, string[]? parameters = null)
	{
		object? value = ExecuteScalar(dbName, sql, parameters);
		return value == null || value == DBNull.Value ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
	}

	public static string ExecuteScalarString(string dbName, string sql, string[]? parameters = null)
	{
		object? value = ExecuteScalar(dbName, sql, parameters);
		return value == null || value == DBNull.Value ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
	}

	// ===== 字符串转义 =====

	/// <summary>SQL 单引号字面量转义（<c>'</c> → <c>''</c>）。</summary>
	public static string Escape(string? value) => (value ?? "").Replace("'", "''", StringComparison.Ordinal);

	// ===== XML 导入 / 导出 =====

	public static long ImportMapXml(string dbName, string tableName, string filePath)
	{
		if (!connections.TryGetValue(dbName ?? "", out SqliteConnection? conn))
			return 0;

		string resolvedPath = ResolveReadPath(filePath, "SQL_IMPORT_MAP_XML");
		string table = QuoteIdentifier(tableName, "SQL_IMPORT_MAP_XML table name");
		try
		{
			XmlDocument doc = new();
			using (var stream = new MemoryStream(SafCompat.ReadAllBytes(resolvedPath) ?? []))
				doc.Load(stream);

			using var trans = conn.BeginTransaction();
			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = trans;
				cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {table} (k TEXT PRIMARY KEY, v TEXT);";
				cmd.ExecuteNonQuery();
			}

			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = trans;
				cmd.CommandText = $"INSERT OR REPLACE INTO {table} (k, v) VALUES (@k, @v);";
				SqliteParameter keyParam = cmd.Parameters.Add("@k", SqliteType.Text);
				SqliteParameter valueParam = cmd.Parameters.Add("@v", SqliteType.Text);

				XmlNodeList? nodes = doc.SelectNodes("/map/p");
				if (nodes != null)
				{
					foreach (XmlNode node in nodes)
					{
						XmlNode? keyNode = node.SelectSingleNode("./k");
						XmlNode? valueNode = node.SelectSingleNode("./v");
						if (keyNode == null || valueNode == null)
							continue;
						keyParam.Value = keyNode.InnerText;
						valueParam.Value = valueNode.InnerXml;
						cmd.ExecuteNonQuery();
					}
				}
			}
			trans.Commit();
			return 1;
		}
		catch (SqlManagerException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new SqlManagerException("SQL_IMPORT_MAP_XML failed: " + ex.Message, ex);
		}
	}

	public static long ImportDtXml(string dbName, string tableName, string schemaPath, string dataPath)
	{
		if (!connections.TryGetValue(dbName ?? "", out SqliteConnection? conn))
			return 0;

		string resolvedSchema = ResolveReadPath(schemaPath, "SQL_IMPORT_DT_XML schema");
		string resolvedData = ResolveReadPath(dataPath, "SQL_IMPORT_DT_XML data");
		string table = QuoteIdentifier(tableName, "SQL_IMPORT_DT_XML table name");
		try
		{
			DataTable schemaTable = new(tableName);
			using (var schemaStream = new MemoryStream(SafCompat.ReadAllBytes(resolvedSchema) ?? []))
				schemaTable.ReadXmlSchema(schemaStream);

			using var trans = conn.BeginTransaction();
			List<string> columns = [];
			foreach (DataColumn col in schemaTable.Columns)
			{
				string type = col.DataType == typeof(long) || col.DataType == typeof(int) ? "INTEGER" : "TEXT";
				string pk = col.ColumnName.Equals("id", StringComparison.OrdinalIgnoreCase) ? " PRIMARY KEY" : "";
				columns.Add($"{QuoteIdentifier(col.ColumnName, "SQL_IMPORT_DT_XML column name")} {type}{pk}");
			}
			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = trans;
				cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {table} ({string.Join(", ", columns)});";
				cmd.ExecuteNonQuery();
			}

			string colNames = string.Join(", ", schemaTable.Columns.Cast<DataColumn>().Select(c => QuoteIdentifier(c.ColumnName, "SQL_IMPORT_DT_XML column name")));
			string paramNames = string.Join(", ", schemaTable.Columns.Cast<DataColumn>().Select(c => "@" + c.ColumnName));

			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = trans;
				cmd.CommandText = $"INSERT OR REPLACE INTO {table} ({colNames}) VALUES ({paramNames});";
				foreach (DataColumn col in schemaTable.Columns)
					cmd.Parameters.Add("@" + col.ColumnName, col.DataType == typeof(long) || col.DataType == typeof(int) ? SqliteType.Integer : SqliteType.Text);

				using XmlReader reader = XmlReader.Create(new MemoryStream(SafCompat.ReadAllBytes(resolvedData) ?? []));
				while (reader.Read())
				{
					if (reader.NodeType != XmlNodeType.Element || reader.Name != tableName)
						continue;
					using XmlReader rowReader = reader.ReadSubtree();
					foreach (SqliteParameter p in cmd.Parameters)
						p.Value = DBNull.Value;

					while (rowReader.Read())
					{
						if (rowReader.NodeType != XmlNodeType.Element || rowReader.Name == tableName)
							continue;
						string colName = rowReader.Name;
						if (cmd.Parameters.Contains("@" + colName))
							cmd.Parameters["@" + colName].Value = rowReader.ReadElementContentAsString();
					}
					cmd.ExecuteNonQuery();
				}
			}
			trans.Commit();
			return 1;
		}
		catch (SqlManagerException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new SqlManagerException("SQL_IMPORT_DT_XML failed: " + ex.Message, ex);
		}
	}

	public static long ExportMapXml(string dbName, string tableName, string filePath)
	{
		if (!connections.TryGetValue(dbName ?? "", out SqliteConnection? conn))
			return 0;

		try
		{
			string fullPath = ResolveWritePath(filePath);
			string table = QuoteIdentifier(tableName, "SQL_EXPORT_MAP_XML table name");
			XmlWriterSettings settings = new() { Indent = true };
			using Stream stream = SafCompat.OpenWrite(fullPath);
			using XmlWriter writer = XmlWriter.Create(stream, settings);
			writer.WriteStartElement("map");
			using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = $"SELECT k, v FROM {table}";
				using SqliteDataReader reader = cmd.ExecuteReader();
				while (reader.Read())
				{
					writer.WriteStartElement("p");
					writer.WriteElementString("k", Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "");
					writer.WriteStartElement("v");
					writer.WriteRaw(reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "");
					writer.WriteEndElement();
					writer.WriteEndElement();
				}
			}
			writer.WriteEndElement();
			return 1;
		}
		catch (SqlManagerException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new SqlManagerException("SQL_EXPORT_MAP_XML failed: " + ex.Message, ex);
		}
	}

	public static long ExportDtXml(string dbName, string tableName, string schemaPath, string dataPath)
	{
		if (!connections.TryGetValue(dbName ?? "", out SqliteConnection? conn))
			return 0;

		try
		{
			string fullSchema = ResolveWritePath(schemaPath);
			string fullData = ResolveWritePath(dataPath);
			string table = QuoteIdentifier(tableName, "SQL_EXPORT_DT_XML table name");

			// 用 DataTable 从空结果集取结构，再写 schema / 数据（对齐 gEmuera，保持与 IMPORT_DT_XML 对称）。
			DataTable dt = new(tableName);
			using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = $"SELECT * FROM {table} LIMIT 0";
				using SqliteDataReader reader = cmd.ExecuteReader();
				dt.Load(reader);
			}
			using (Stream schemaStream = SafCompat.OpenWrite(fullSchema))
				dt.WriteXmlSchema(schemaStream);

			XmlWriterSettings settings = new() { Indent = true };
			using Stream dataStream = SafCompat.OpenWrite(fullData);
			using XmlWriter writer = XmlWriter.Create(dataStream, settings);
			writer.WriteStartElement("DocumentElement");
			using (var cmd = conn.CreateCommand())
			{
				cmd.CommandText = $"SELECT * FROM {table}";
				using SqliteDataReader reader = cmd.ExecuteReader();
				while (reader.Read())
				{
					writer.WriteStartElement(tableName);
					for (int i = 0; i < reader.FieldCount; i++)
					{
						if (!reader.IsDBNull(i))
							writer.WriteElementString(reader.GetName(i), Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? "");
					}
					writer.WriteEndElement();
				}
			}
			writer.WriteEndElement();
			return 1;
		}
		catch (SqlManagerException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new SqlManagerException("SQL_EXPORT_DT_XML failed: " + ex.Message, ex);
		}
	}

	/// <summary>按 <c>columnMappings</c>（<c>Col=@attr,Col2=node,Col3=node(xml)</c>）流式导入非结构化 XML。</summary>
	public static long ImportXmlCustom(string dbName, string tableName, string filePath, string rowXPath, string columnMappings)
	{
		if (!connections.TryGetValue(dbName ?? "", out SqliteConnection? conn))
			return 0;

		string resolvedPath = ResolveReadPath(filePath, "SQL_IMPORT_XML_CUSTOM");
		string table = QuoteIdentifier(tableName, "SQL_IMPORT_XML_CUSTOM table name");
		try
		{
			Dictionary<string, string> mapping = columnMappings
				.Split(',')
				.Select(s => s.Split('='))
				.ToDictionary(a => a[0].Trim(), a => a.Length > 1 ? a[1].Trim() : "");

			using var trans = conn.BeginTransaction();
			string colDef = string.Join(", ", mapping.Keys.Select(k => QuoteIdentifier(k, "SQL_IMPORT_XML_CUSTOM column name") + " TEXT"));
			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = trans;
				cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {table} ({colDef});";
				cmd.ExecuteNonQuery();
			}

			using (var cmd = conn.CreateCommand())
			{
				cmd.Transaction = trans;
				string colNames = string.Join(", ", mapping.Keys.Select(k => QuoteIdentifier(k, "SQL_IMPORT_XML_CUSTOM column name")));
				string paramNames = string.Join(", ", mapping.Keys.Select(k => "@" + k));
				cmd.CommandText = $"INSERT INTO {table} ({colNames}) VALUES ({paramNames});";
				foreach (string col in mapping.Keys)
					cmd.Parameters.Add("@" + col, SqliteType.Text);

				string targetNodeName = rowXPath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();

				using XmlReader reader = XmlReader.Create(new MemoryStream(SafCompat.ReadAllBytes(resolvedPath) ?? []));
				while (reader.Read())
				{
					if (reader.NodeType != XmlNodeType.Element || reader.Name != targetNodeName)
						continue;
					using XmlReader subReader = reader.ReadSubtree();
					XmlDocument rowDoc = new();
					rowDoc.Load(subReader);
					XmlNode? row = rowDoc.DocumentElement;
					if (row == null)
						continue;

					foreach (KeyValuePair<string, string> map in mapping)
					{
						string expr = map.Value;
						string? value;
						if (expr.StartsWith('@'))
							value = row.Attributes?[expr[1..]]?.Value;
						else if (expr.EndsWith("(xml)", StringComparison.Ordinal))
							value = row.SelectSingleNode(expr[..^5])?.InnerXml;
						else
							value = row.SelectSingleNode(expr)?.InnerText;
						cmd.Parameters["@" + map.Key].Value = (object?)value ?? DBNull.Value;
					}
					cmd.ExecuteNonQuery();
				}
			}
			trans.Commit();
			return 1;
		}
		catch (SqlManagerException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new SqlManagerException("SQL_IMPORT_XML_CUSTOM failed: " + ex.Message, ex);
		}
	}

	/// <summary>会话结束清理（挂 <c>GlobalStatic.Scope.Dispose</c> 清理链，防跨会话泄漏）。</summary>
	public static void CloseAll()
	{
		foreach (ReaderContext ctx in readers.Values)
		{
			ctx.Reader.Dispose();
			ctx.Command.Dispose();
		}
		readers.Clear();

		foreach (SqliteConnection conn in connections.Values)
			conn.Dispose();
		connections.Clear();
		nextReaderId = 1;
	}

	// ===== 内部 =====

	private static object? ExecuteScalar(string dbName, string sql, string[]? parameters)
	{
		using SqliteCommand cmd = CreateCommand(dbName, sql, parameters);
		try
		{
			return cmd.ExecuteScalar();
		}
		catch (Exception ex)
		{
			throw new SqlManagerException($"SQL scalar error: {ex.Message}\nSQL: {sql}", ex);
		}
	}

	private static SqliteCommand CreateCommand(string dbName, string sql, string[]? parameters)
	{
		if (!connections.TryGetValue(dbName ?? "", out SqliteConnection? conn))
			throw new SqlManagerException("SQL database is not connected: " + (dbName ?? ""));

		SqliteCommand cmd = conn.CreateCommand();
		cmd.CommandText = sql ?? "";
		if (parameters != null)
		{
			for (int i = 0; i < parameters.Length; i++)
			{
				SqliteParameter p = cmd.CreateParameter();
				p.ParameterName = "@" + i.ToString(CultureInfo.InvariantCulture);
				p.Value = (object?)parameters[i] ?? DBNull.Value;
				cmd.Parameters.Add(p);
			}
		}
		return cmd;
	}

	private static T ReaderColumn<T>(long readerId, int columnIndex, Func<SqliteDataReader, T> read)
	{
		try
		{
			if (!readers.TryGetValue(readerId, out ReaderContext? ctx))
				throw new SqlManagerException("Invalid SQL reader id: " + readerId.ToString(CultureInfo.InvariantCulture));
			return read(ctx.Reader);
		}
		catch (SqlManagerException)
		{
			throw;
		}
		catch (Exception ex)
		{
			throw new SqlManagerException($"SQL reader column error (id={readerId}, col={columnIndex}): {ex.Message}", ex);
		}
	}

	private static void CloseReadersFor(string dbName)
	{
		List<long> closeIds = [];
		foreach (KeyValuePair<long, ReaderContext> pair in readers)
		{
			if (string.Equals(pair.Value.DbName, dbName, StringComparison.OrdinalIgnoreCase))
				closeIds.Add(pair.Key);
		}
		foreach (long id in closeIds)
			ReaderClose(id);
	}

	/// <summary>相对 <c>Data Source=</c> 按 <paramref name="baseDir"/> 归一（headless 的 CWD ≠ 游戏目录）。</summary>
	private static string NormalizeConnectionString(string? connectionString, string baseDir)
	{
		string input = string.IsNullOrWhiteSpace(connectionString) ? "Data Source=:memory:" : connectionString;
		SqliteConnectionStringBuilder builder = new(input);
		string dataSource = builder.DataSource;
		if (!string.IsNullOrEmpty(dataSource)
			&& !dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
			&& !Path.IsPathRooted(dataSource)
			&& !dataSource.StartsWith("content://", StringComparison.Ordinal)
			&& !string.IsNullOrEmpty(baseDir)
			&& !baseDir.StartsWith("content://", StringComparison.Ordinal))
		{
			builder.DataSource = Path.Combine(baseDir, dataSource);
		}
		return builder.ConnectionString;
	}

	private static string ResolveReadPath(string path, string description)
	{
		foreach (string candidate in BuildPathCandidates(path))
		{
			if (SafCompat.FileExists(candidate))
				return candidate;
		}
		throw new SqlManagerException($"{description}: file not found: {path ?? ""}");
	}

	private static IEnumerable<string> BuildPathCandidates(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
			yield break;
		if (!Path.IsPathRooted(path) && !path.StartsWith("content://", StringComparison.Ordinal))
		{
			yield return SafCompat.CombinePath(Program.ExeDir, path);
			yield return SafCompat.CombinePath(Program.ContentDir, path);
		}
		yield return path;
	}

	private static string ResolveWritePath(string path)
	{
		if (Path.IsPathRooted(path) || path.StartsWith("content://", StringComparison.Ordinal))
			return path;
		return SafCompat.CombinePath(Program.ExeDir, path);
	}

	/// <summary>双引号包裹标识符 + <c>""</c> 转义 + 拒 NUL（吸收 gEmuera 的注入加固）。</summary>
	private static string QuoteIdentifier(string? identifier, string description)
	{
		if (string.IsNullOrEmpty(identifier) || identifier.Contains('\0'))
			throw new SqlManagerException($"invalid {description}: '{identifier}'");
		return "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
	}
}

/// <summary>
/// SQL 深模块的错误类型（实现票 28）：<see cref="SqlManager"/> 只抛本类型（不依赖 ERB 的 <c>CodeEE</c>），
/// 由薄包装层统一映射为 <c>CodeEE</c>——保持「Manager 不依赖 ERB 异常协议」的分层。
/// </summary>
internal sealed class SqlManagerException : Exception
{
	public SqlManagerException(string message) : base(message)
	{
	}

	public SqlManagerException(string message, Exception inner) : base(message, inner)
	{
	}
}
