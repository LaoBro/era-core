using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Utils;

internal enum EraDataState
{
	OK = 0,//ロード可能
	FILENOTFOUND = 1,//ファイルが存在せず
	GAME_ERROR = 2,//ゲームが違う
	VIRSION_ERROR = 3,//バージョンが違う
	ETC_ERROR = 4,//その他のエラー

}

internal sealed class EraDataResult
{
	public EraDataState State = EraDataState.OK;
	public string DataMes = "";
	// 票 45（Skia 12.1.0）：CHKDATA 通过 RESULT:1 暴露存档版本；仅 OK / VIRSION_ERROR 有值。
	public long Version;
}

/// <summary>
/// セーブデータ読み取り
/// </summary>
internal sealed class EraDataReader : IDisposable
{
	//public EraDataReader(string filepath)
	//{
	//    file = new FileStream(filepath, FileMode.Open, FileAccess.Read);
	//    reader = new StreamReader(file, Config.Encode);
	//}
	public EraDataReader(Stream file)
	{
		// SAF OpenInputStream 可能不可 Seek；缓冲到 MemoryStream 再 DetectEncoding / SeekEmuStart
		if (!file.CanSeek)
		{
			var ms = new MemoryStream();
			file.CopyTo(ms);
			file.Dispose();
			ms.Position = 0;
			this.file = ms;
		}
		else
		{
			this.file = file;
			file.Seek(0, SeekOrigin.Begin);
		}
		reader = new StreamReader(this.file, EncodingHandler.DetectEncoding(this.file));
	}
	Stream file;
	StreamReader reader;
	public const string FINISHER = "__FINISHED";
	public const string EMU_1700_START = "__EMUERA_STRAT__";
	public const string EMU_1708_START = "__EMUERA_1708_STRAT__";
	public const string EMU_1729_START = "__EMUERA_1729_STRAT__";
	public const string EMU_1803_START = "__EMUERA_1803_STRAT__";
	public const string EMU_1808_START = "__EMUERA_1808_STRAT__";
	public const string EMU_FLOAT_START = "__EMUERA_FLOAT_STRAT__";
	public const string EMU_SEPARATOR = "__EMU_SEPARATOR__";

	/// <summary>27b：浮点段探测用的一行回退缓冲（仅 TryReadFloatStart 会填充）。</summary>
	string? peekedLine;

	string? ReadLineCore()
	{
		if (peekedLine != null)
		{
			string line = peekedLine;
			peekedLine = null;
			return line;
		}
		return reader.ReadLine();
	}

	/// <summary>
	/// 27b：旧存档在 6 个用户变量段后即结束；新存档若存在浮点 SAVEDATA，
	/// 会追加 EMU_FLOAT_START 标记。探测时不吞掉普通行。
	/// </summary>
	public bool TryReadFloatStart()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		string? line = ReadLineCore();
		if (line == null)
			return false;
		if (line.Equals(EMU_FLOAT_START, StringComparison.Ordinal))
			return true;
		peekedLine = line;
		return false;
	}
	#region eramaker
	public string ReadString()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		string str = reader.ReadLine()!;
		if (str == null)
			throw new FileEE(trerror.NoStrToRead.Text);
		return str;
	}

	public long ReadInt64()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		string str = reader.ReadLine()!;
		if (str == null)
			throw new FileEE(trerror.NoNumToRead.Text);
		if (!long.TryParse(str, out long ret))
			throw new FileEE(trerror.CanNotInterpretNum.Text);
		return ret;
	}


	public void ReadInt64Array(long[] array)
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int i = -1;
		string str;
		while (true)
		{
			i++;
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				break;
			if (i >= array.Length)//配列を超えて保存されていても動じないで読み飛ばす。
				continue;
			if (!long.TryParse(str, out long integer))
				throw new FileEE(trerror.InvalidArray.Text);
			array[i] = integer;
		}
		for (; i < array.Length; i++)//保存されている値が無いなら0に初期化
			array[i] = 0;
	}

	public void ReadStringArray(string[] array)
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int i = -1;
		string str;
		while (true)
		{
			i++;
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				break;
			if (i >= array.Length)//配列を超えて保存されていても動じないで読み飛ばす。
				continue;
			array[i] = str;
		}
		for (; i < array.Length; i++)//保存されている値が無いなら""に初期化
			array[i] = "";
	}
	#endregion
	#region Emuera
	int emu_version = -1;
	public int DataVersion { get { return emu_version; } }
	public bool SeekEmuStart()
	{

		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (reader.EndOfStream)
			return false;
		while (true)
		{
			string str = reader.ReadLine()!;
			if (str == null)
				return false;
			if (str.Equals(EMU_1700_START, StringComparison.Ordinal))
			{
				emu_version = 1700;
				return true;
			}
			if (str.Equals(EMU_1708_START, StringComparison.Ordinal))
			{
				emu_version = 1708;
				return true;
			}
			if (str.Equals(EMU_1729_START, StringComparison.Ordinal))
			{
				emu_version = 1729;
				return true;
			}
			if (str.Equals(EMU_1803_START, StringComparison.Ordinal))
			{
				emu_version = 1803;
				return true;
			}
			if (str.Equals(EMU_1808_START, StringComparison.Ordinal))
			{
				emu_version = 1808;
				return true;
			}
		}
	}

	public Dictionary<string, string> ReadStringExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, string> strList = [];
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			int index = str.IndexOf(':', StringComparison.Ordinal);
			if (index < 0)
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			string key = str[..index];
			string value = str.Substring(index + 1);
			strList.TryAdd(key, value);
		}
		return strList;
	}
	public Dictionary<string, long> ReadInt64Extended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, long> intList = [];
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			int index = str.IndexOf(':', StringComparison.Ordinal);
			if (index < 0)
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			string key = str[..index];
			string valueStr = str.Substring(index + 1);
			if (!long.TryParse(valueStr, out long value))
				throw new FileEE(trerror.InvalidArray.Text);
			intList.TryAdd(key, value);
		}
		return intList;
	}

	public Dictionary<string, List<long>> ReadInt64ArrayExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<long>> ret = [];
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			string key = str;
			List<long> valueList = [];
			while (true)
			{
				str = reader.ReadLine()!;
				if (str == null)
					throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
				if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
					throw new FileEE(trerror.InvalidSaveDataFormat.Text);
				if (str.Equals(FINISHER, StringComparison.Ordinal))
					break;
				if (!long.TryParse(str, out long value))
					throw new FileEE(trerror.InvalidArray.Text);
				valueList.Add(value);
			}
			ret.TryAdd(key, valueList);
		}
		return ret;
	}

	public Dictionary<string, List<string>> ReadStringArrayExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<string>> ret = [];
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			string key = str;
			List<string> valueList = [];
			while (true)
			{
				str = reader.ReadLine()!;
				if (str == null)
					throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
				if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
					throw new FileEE(trerror.InvalidSaveDataFormat.Text);
				if (str.Equals(FINISHER, StringComparison.Ordinal))
					break;
				valueList.Add(str);
			}
			ret.TryAdd(key, valueList);
		}
		return ret;
	}

	public Dictionary<string, List<long[]>> ReadInt64Array2DExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<long[]>> ret = [];
		if (emu_version < 1708)
			return ret;
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			string key = str;
			List<long[]> valueList = [];
			while (true)
			{
				str = reader.ReadLine()!;
				if (str == null)
					throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
				if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
					throw new FileEE(trerror.InvalidSaveDataFormat.Text);
				if (str.Equals(FINISHER, StringComparison.Ordinal))
					break;
				if (str.Length == 0)
				{
					valueList.Add([]);
					continue;
				}
				string[] tokens = str.Split(',');
				long[] intTokens = new long[tokens.Length];

				for (int x = 0; x < tokens.Length; x++)
					if (!long.TryParse(tokens[x], out intTokens[x]))
						throw new FileEE(string.Format(trerror.CanNotInterpretNumValue.Text, tokens[x]));
				valueList.Add(intTokens);
			}
			ret.TryAdd(key, valueList);
		}
		return ret;
	}

	public Dictionary<string, List<string[]>> ReadStringArray2DExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<string[]>> ret = [];
		if (emu_version < 1708)
			return ret;
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			throw new FileEE(trerror.NotSupportStringArray2D.Text);
		}
		return ret;
	}

	public Dictionary<string, List<List<long[]>>> ReadInt64Array3DExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<List<long[]>>> ret = [];
		if (emu_version < 1729)
			return ret;
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			string key = str;
			List<List<long[]>> valueList = [];
			while (true)
			{
				str = reader.ReadLine()!;
				if (str == null)
					throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
				if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
					throw new FileEE(trerror.InvalidSaveDataFormat.Text);
				if (str.Equals(FINISHER, StringComparison.Ordinal))
					break;
				if (str.Contains('{'))
				{
					List<long[]> tokenList = [];
					while (true)
					{
						str = reader.ReadLine()!;
						if (str == "}")
							break;
						if (str!.Length == 0)
						{
							tokenList.Add([]);
							continue;
						}
						string[] tokens = str.Split(',');
						long[] intTokens = new long[tokens.Length];

						for (int x = 0; x < tokens.Length; x++)
							if (!long.TryParse(tokens[x], out intTokens[x]))
								throw new FileEE(string.Format(trerror.CanNotInterpretNumValue.Text, tokens[x]));
						tokenList.Add(intTokens);
					}
					valueList.Add(tokenList);
				}
			}
			ret.TryAdd(key, valueList);
		}
		return ret;
	}

	public Dictionary<string, List<List<string[]>>> ReadStringArray3DExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<List<string[]>>> ret = [];
		if (emu_version < 1729)
			return ret;
		string str;
		while (true)
		{
			str = reader.ReadLine()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			throw new FileEE(trerror.NotSupportStringArray2D.Text);
		}
		return ret;
	}

	#region 27b 浮点段
	private static bool TryParseDouble(string str, out double value)
	{
		return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
			|| double.TryParse(str, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
	}

	public Dictionary<string, double> ReadDoubleExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, double> ret = [];
		string str;
		while (true)
		{
			str = ReadLineCore()!;
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			int index = str.IndexOf(':', StringComparison.Ordinal);
			if (index < 0)
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			string key = str[..index];
			if (!TryParseDouble(str.Substring(index + 1), out double value))
				throw new FileEE(trerror.InvalidArray.Text);
			ret.TryAdd(key, value);
		}
		return ret;
	}

	public Dictionary<string, List<double>> ReadDoubleArrayExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<double>> ret = [];
		string? str;
		while (true)
		{
			str = ReadLineCore();
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			string key = str;
			List<double> valueList = [];
			while (true)
			{
				str = ReadLineCore();
				if (str == null)
					throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
				if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
					throw new FileEE(trerror.InvalidSaveDataFormat.Text);
				if (str.Equals(FINISHER, StringComparison.Ordinal))
					break;
				if (!TryParseDouble(str, out double value))
					throw new FileEE(trerror.InvalidArray.Text);
				valueList.Add(value);
			}
			ret.TryAdd(key, valueList);
		}
		return ret;
	}

	public Dictionary<string, List<double[]>> ReadDoubleArray2DExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<double[]>> ret = [];
		string? str;
		while (true)
		{
			str = ReadLineCore();
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			string key = str;
			List<double[]> valueList = [];
			while (true)
			{
				str = ReadLineCore();
				if (str == null)
					throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
				if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
					throw new FileEE(trerror.InvalidSaveDataFormat.Text);
				if (str.Equals(FINISHER, StringComparison.Ordinal))
					break;
				if (str.Length == 0)
				{
					valueList.Add([]);
					continue;
				}
				string[] tokens = str.Split(',');
				double[] values = new double[tokens.Length];
				for (int x = 0; x < tokens.Length; x++)
					if (!TryParseDouble(tokens[x], out values[x]))
						throw new FileEE(string.Format(trerror.CanNotInterpretNumValue.Text, tokens[x]));
				valueList.Add(values);
			}
			ret.TryAdd(key, valueList);
		}
		return ret;
	}

	public Dictionary<string, List<List<double[]>>> ReadDoubleArray3DExtended()
	{
		if (reader == null)
			throw new FileEE(trerror.InvalidStream.Text);
		Dictionary<string, List<List<double[]>>> ret = [];
		string? str;
		while (true)
		{
			str = ReadLineCore();
			if (str == null)
				throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
			if (str.Equals(FINISHER, StringComparison.Ordinal))
				throw new FileEE(trerror.InvalidSaveDataFormat.Text);
			if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
				break;
			string key = str;
			List<List<double[]>> valueList = [];
			while (true)
			{
				str = ReadLineCore();
				if (str == null)
					throw new FileEE(trerror.UnexpectedSaveDataEnd.Text);
				if (str.Equals(EMU_SEPARATOR, StringComparison.Ordinal))
					throw new FileEE(trerror.InvalidSaveDataFormat.Text);
				if (str.Equals(FINISHER, StringComparison.Ordinal))
					break;
				if (str.Contains('{'))
				{
					List<double[]> tokenList = [];
					while (true)
					{
						str = ReadLineCore();
						if (str == "}")
							break;
						if (str!.Length == 0)
						{
							tokenList.Add([]);
							continue;
						}
						string[] tokens = str.Split(',');
						double[] values = new double[tokens.Length];
						for (int x = 0; x < tokens.Length; x++)
							if (!TryParseDouble(tokens[x], out values[x]))
								throw new FileEE(string.Format(trerror.CanNotInterpretNumValue.Text, tokens[x]));
						tokenList.Add(values);
					}
					valueList.Add(tokenList);
				}
			}
			ret.TryAdd(key, valueList);
		}
		return ret;
	}
	#endregion

	#endregion
	#region IDisposable メンバ

	public void Dispose()
	{
		if (reader != null)
			reader.Close();
		else if (file != null)
			file.Close();
		file = null!;
		reader = null!;
	}

	#endregion
	public void Close()
	{
		Dispose();
	}

}

/// <summary>
/// セーブデータ書き込み
/// </summary>
internal sealed class EraDataWriter : IDisposable
{
	//public EraDataWriter(string filepath)
	//{
	//    FileStream file = new FileStream(filepath, FileMode.Create, FileAccess.Write);
	//    writer = new StreamWriter(file, Config.SaveEncode);
	//    //writer = new StreamWriter(filepath, false, Config.SaveEncode);
	//}
	public EraDataWriter(Stream file)
	{
		this.file = file;
		writer = new StreamWriter(file, Config.Config.SaveEncode);
	}

	public const string FINISHER = EraDataReader.FINISHER;
	public const string EMU_START = EraDataReader.EMU_1808_START;
	public const string EMU_FLOAT_START = EraDataReader.EMU_FLOAT_START;
	public const string EMU_SEPARATOR = EraDataReader.EMU_SEPARATOR;
	Stream file;
	StreamWriter writer;
	#region eramaker
	public void Write(long integer)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		writer.WriteLine(integer.ToString());
	}


	public void Write(string str)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (str == null)
			writer.WriteLine("");
		else
			writer.WriteLine(str);
	}

	public void Write(long[] array)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int count = -1;
		for (int i = 0; i < array.Length; i++)
			if (array[i] != 0)
				count = i;
		count++;
		for (int i = 0; i < count; i++)
			writer.WriteLine(array[i].ToString());
		writer.WriteLine(FINISHER);
	}
	public void Write(string[] array)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int count = -1;
		for (int i = 0; i < array.Length; i++)
			if (!string.IsNullOrEmpty(array[i]))
				count = i;
		count++;
		for (int i = 0; i < count; i++)
		{
			if (array[i] == null)
				writer.WriteLine("");
			else
				writer.WriteLine(array[i]);
		}
		writer.WriteLine(FINISHER);
	}
	#endregion
	#region Emuera

	public void EmuStart()
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		writer.WriteLine(EMU_START);
	}
	public void EmuSeparete()
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		writer.WriteLine(EMU_SEPARATOR);
	}

	/// <summary>27b：浮点段起始标记（仅当存在浮点 SAVEDATA 变量时写入）。</summary>
	public void EmuFloatStart()
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		writer.WriteLine(EMU_FLOAT_START);
	}

	private static string FormatDouble(double value)
		=> value.ToString("R", CultureInfo.InvariantCulture);

	public void WriteExtended(string key, long value)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (value == 0)
			return;
		writer.WriteLine(string.Format("{0}:{1}", key, value));
	}

	public void WriteExtended(string key, double value)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (value == 0)
			return;
		writer.WriteLine(string.Format("{0}:{1}", key, FormatDouble(value)));
	}

	public void WriteExtended(string key, string value)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (string.IsNullOrEmpty(value))
			return;
		writer.WriteLine(string.Format("{0}:{1}", key, value));
	}


	public void WriteExtended(string key, long[] array)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int count = -1;
		for (int i = 0; i < array.Length; i++)
			if (array[i] != 0)
				count = i;
		count++;
		if (count == 0)
			return;
		writer.WriteLine(key);
		for (int i = 0; i < count; i++)
			writer.WriteLine(array[i].ToString());
		writer.WriteLine(FINISHER);
	}
	public void WriteExtended(string key, string[] array)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int count = -1;
		for (int i = 0; i < array.Length; i++)
			if (!string.IsNullOrEmpty(array[i]))
				count = i;
		count++;
		if (count == 0)
			return;
		writer.WriteLine(key);
		for (int i = 0; i < count; i++)
		{
			if (array[i] == null)
				writer.WriteLine("");
			else
				writer.WriteLine(array[i]);
		}
		writer.WriteLine(FINISHER);
	}

	public void WriteExtended(string key, long[,] array2D)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array2D == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int countX = 0;
		int length0 = array2D.GetLength(0);
		int length1 = array2D.GetLength(1);
		int[] countY = new int[length0];
		for (int x = 0; x < length0; x++)
		{
			for (int y = 0; y < length1; y++)
			{
				if (array2D[x, y] != 0)
				{
					countX = x + 1;
					countY[x] = y + 1;
				}
			}
		}
		if (countX == 0)
			return;
		writer.WriteLine(key);
		for (int x = 0; x < countX; x++)
		{
			if (countY[x] == 0)
			{
				writer.WriteLine("");
				continue;
			}
			StringBuilder builder = new("");
			for (int y = 0; y < countY[x]; y++)
			{
				builder.Append(array2D[x, y]);
				if (y != countY[x] - 1)
					builder.Append(',');
			}
			writer.WriteLine(builder.ToString());
		}
		writer.WriteLine(FINISHER);
	}

	public void WriteExtended(string key, string[,] array2D)
	{
		throw new NotImplementedException(trerror.NotImplement.Text);
	}

	public void WriteExtended(string key, long[,,] array3D)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array3D == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int countX = 0;
		int length0 = array3D.GetLength(0);
		int length1 = array3D.GetLength(1);
		int length2 = array3D.GetLength(2);
		int[] countY = new int[length0];
		int[,] countZ = new int[length0, length1];
		for (int x = 0; x < length0; x++)
		{
			for (int y = 0; y < length1; y++)
			{
				for (int z = 0; z < length2; z++)
				{
					if (array3D[x, y, z] != 0)
					{
						countX = x + 1;
						countY[x] = y + 1;
						countZ[x, y] = z + 1;
					}
				}
			}
		}
		if (countX == 0)
			return;
		writer.WriteLine(key);
		for (int x = 0; x < countX; x++)
		{
			writer.WriteLine(x.ToString() + "{");
			if (countY[x] == 0)
			{
				writer.WriteLine("}");
				continue;
			}
			for (int y = 0; y < countY[x]; y++)
			{
				StringBuilder builder = new("");
				if (countZ[x, y] == 0)
				{
					writer.WriteLine("");
					continue;
				}
				for (int z = 0; z < countZ[x, y]; z++)
				{
					builder.Append(array3D[x, y, z]);
					if (z != countZ[x, y] - 1)
						builder.Append(',');
				}
				writer.WriteLine(builder.ToString());
			}
			writer.WriteLine("}");
		}
		writer.WriteLine(FINISHER);
	}

	public void WriteExtended(string key, string[,,] array2D)
	{
		throw new NotImplementedException(trerror.NotImplement.Text);
	}

	#region 27b 浮点扩展
	public void WriteExtended(string key, double[] array)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int count = -1;
		for (int i = 0; i < array.Length; i++)
			if (array[i] != 0)
				count = i;
		count++;
		if (count == 0)
			return;
		writer.WriteLine(key);
		for (int i = 0; i < count; i++)
			writer.WriteLine(FormatDouble(array[i]));
		writer.WriteLine(FINISHER);
	}

	public void WriteExtended(string key, double[,] array2D)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array2D == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int countX = 0;
		int length0 = array2D.GetLength(0);
		int length1 = array2D.GetLength(1);
		int[] countY = new int[length0];
		for (int x = 0; x < length0; x++)
		{
			for (int y = 0; y < length1; y++)
			{
				if (array2D[x, y] != 0)
				{
					countX = x + 1;
					countY[x] = y + 1;
				}
			}
		}
		if (countX == 0)
			return;
		writer.WriteLine(key);
		for (int x = 0; x < countX; x++)
		{
			if (countY[x] == 0)
			{
				writer.WriteLine("");
				continue;
			}
			StringBuilder builder = new("");
			for (int y = 0; y < countY[x]; y++)
			{
				builder.Append(FormatDouble(array2D[x, y]));
				if (y != countY[x] - 1)
					builder.Append(',');
			}
			writer.WriteLine(builder.ToString());
		}
		writer.WriteLine(FINISHER);
	}

	public void WriteExtended(string key, double[,,] array3D)
	{
		if (writer == null)
			throw new FileEE(trerror.InvalidStream.Text);
		if (array3D == null)
			throw new FileEE(trerror.InvalidArray.Text);
		int countX = 0;
		int length0 = array3D.GetLength(0);
		int length1 = array3D.GetLength(1);
		int length2 = array3D.GetLength(2);
		int[] countY = new int[length0];
		int[,] countZ = new int[length0, length1];
		for (int x = 0; x < length0; x++)
		{
			for (int y = 0; y < length1; y++)
			{
				for (int z = 0; z < length2; z++)
				{
					if (array3D[x, y, z] != 0)
					{
						countX = x + 1;
						countY[x] = y + 1;
						countZ[x, y] = z + 1;
					}
				}
			}
		}
		if (countX == 0)
			return;
		writer.WriteLine(key);
		for (int x = 0; x < countX; x++)
		{
			writer.WriteLine(x.ToString() + "{");
			if (countY[x] == 0)
			{
				writer.WriteLine("}");
				continue;
			}
			for (int y = 0; y < countY[x]; y++)
			{
				StringBuilder builder = new("");
				if (countZ[x, y] == 0)
				{
					writer.WriteLine("");
					continue;
				}
				for (int z = 0; z < countZ[x, y]; z++)
				{
					builder.Append(FormatDouble(array3D[x, y, z]));
					if (z != countZ[x, y] - 1)
						builder.Append(',');
				}
				writer.WriteLine(builder.ToString());
			}
			writer.WriteLine("}");
		}
		writer.WriteLine(FINISHER);
	}
	#endregion
	#endregion

	#region IDisposable メンバ

	public void Dispose()
	{
		if (writer != null)
			writer.Close();
		else if (file != null)
			file.Close();
		writer = null!;
		file = null!;
	}

	#endregion
	public void Close()
	{
		Dispose();
	}
}
