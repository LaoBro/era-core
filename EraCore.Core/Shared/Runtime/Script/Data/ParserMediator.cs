using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace MinorShift.Emuera;

//1756 新設。ParserやLexicalAnalyzerなどが知りたい情報をまとめる
//本当は引数として渡すべきなのかもしれないが全てのParserの引数を書きなおすのが面倒なのでstatic
internal partial class ParserMediator
{
	static ParserMediator()
	{
		RenameDic = [];
	}
	/// <summary>
	/// emuera.config等で発生した警告
	/// Initializeより前に発生する
	/// </summary>
	/// <param name="str"></param>
	/// <param name="?"></param>
	public static void ConfigWarn(string str, ScriptPosition? pos, int level, string stack)
	{
		if (level <Config.DisplayWarningLevel && !Program.AnalysisMode)
			return;
		warningList.Add(new ParserWarning(str, pos, level, stack));
	}

	static EmueraConsole console = null!;
	public static void Initialize(EmueraConsole console)
	{
		ParserMediator.console = console;
	}

	#region Rename
	public static Dictionary<string, string> RenameDic { get; private set; }
	//1756 Process.Load.csより移動
	public static void LoadEraExRenameFile(string filepath)
	{
		if (!SafCompat.FileExists(filepath))
		{
			return;
		}
		if (RenameDic.Count > 0)
			RenameDic.Clear();

		var fileLine = SafCompat.ReadAllLines(filepath);
		ScriptPosition? pos = null;
		Regex regex = unEscapedCommaRegex();
		try
		{
			var lineNo = 0;
			foreach (var line in fileLine)
			{
				pos = new ScriptPosition(filepath, lineNo);
				if (line.StartsWith(';'))
					continue;
				var tokens = regex.Split(line);
				if (tokens.Length == 2)
				{
					//右がERB中の表記、左が変換先になる。
					string key = $"[[{tokens[1].Trim()}]]";
					string value = tokens[0].Trim();
					RenameDic[key] = value;
				}
				lineNo++;
			}
		}
		catch (Exception e)
		{
			throw new CodeEE(e.Message, pos);
		}
	}
	#endregion

	static object warningListLock = new();

	// erb-loader-parallel 01：per-file 警告缓冲。生成阶段（ErbLoader.GenerateErbFile）经
	// BeginWarningBuffer 把 Warn 重定向到文件产物自己的列表，合并阶段 EmitWarningBuffer
	// 按原文件顺序搬回全局 warningList——警告内容与顺序不变，仅落点延后。
	// 契约：单个缓冲同一时刻只有一个写入者（串行逐文件 / 03 并行时每文件各自一缓冲）。
	static readonly AsyncLocal<List<ParserWarning>?> warningBuffer = new();

	internal static void BeginWarningBuffer(List<ParserWarning> buffer)
	{
		warningBuffer.Value = buffer;
	}

	internal static void EndWarningBuffer()
	{
		warningBuffer.Value = null;
	}

	/// <summary>把 per-file 缓冲的警告按原顺序并入全局警告列表（合并阶段调用）。</summary>
	internal static void EmitWarningBuffer(List<ParserWarning> buffer)
	{
		if (buffer.Count == 0)
			return;
		lock (warningListLock)
		{
			warningList.AddRange(buffer);
		}
		buffer.Clear();
	}

	/// <summary>写入一条警告：缓冲激活时进 per-file 缓冲，否则锁加进全局列表。</summary>
	static void AddWarning(ParserWarning warning)
	{
		var buffer = warningBuffer.Value;
		if (buffer != null)
		{
			buffer.Add(warning);
			return;
		}
		lock (warningListLock)
		{
			warningList.Add(warning);
		}
	}

	public static void Warn(string str, ScriptPosition? pos, int level)
	{
		Warn(str, pos, level, null!);
	}

	public static void Warn(string str, ScriptPosition? pos, int level, string stack)
	{
		if (level < Config.DisplayWarningLevel && !Program.AnalysisMode)
			return;
		if (console != null && !console.RunERBFromMemory)
			AddWarning(new ParserWarning(str, pos, level, stack));
	}

	/// <summary>
	/// Parser中での警告出力
	/// </summary>
	/// <param name="str"></param>
	/// <param name="line"></param>
	/// <param name="level">警告レベル.0:軽微なミス.1:無視できる行.2:行が実行されなければ無害.3:致命的</param>
	public static void Warn(string str, LogicalLine line, int level, bool isError, bool isBackComp)
	{
		Warn(str, line, level, isError, isBackComp, null!);
	}

	public static void Warn(string str, LogicalLine line, int level, bool isError, bool isBackComp, string stack)
	{
		if (isError)
		{
			line.IsError = true;
			line.ErrMes = str;
		}
		if (level < Config.DisplayWarningLevel && !Program.AnalysisMode)
			return;
		if (isBackComp && !Config.WarnBackCompatibility)
			return;
		if (console != null && !console.RunERBFromMemory)
		{
			// 决策七：无缓冲时加锁写入 — 与 Warn(string, ScriptPosition?, ...) 重载一致，为决策六并行化铺路。
			AddWarning(new ParserWarning(str, line.Position, level, stack));
		}
		//				console.PrintWarning(str, line.Position, level);
	}

	private static List<ParserWarning> warningList = [];

	public static bool HasWarning { get { return warningList.Count > 0; } }
	public static void ClearWarningList()
	{
		warningList.Clear();
	}

	public static void FlushWarningList()
	{
		for (int i = 0; i < warningList.Count; i++)
		{
			ParserWarning warning = warningList[i];
			console.PrintWarning(warning.WarningMes, warning.WarningPos, warning.WarningLevel);
			if (warning.StackTrace != null)
			{
				string[] stacks = warning.StackTrace.Split('\n');
				for (int j = 0; j < stacks.Length; j++)
				{
					console.PrintSystemLine(stacks[j]);
				}
			}
		}
		warningList.Clear();
	}

	internal sealed class ParserWarning
	{
		public ParserWarning(string mes, ScriptPosition? pos, int level, string stackTrace)
		{
			WarningMes = mes;
			WarningPos = pos;
			WarningLevel = level;
			StackTrace = stackTrace;
		}
		public string WarningMes;
		public ScriptPosition? WarningPos;
		public int WarningLevel;
		public string StackTrace;
	}

	[GeneratedRegex(@"(?<!\\),")]
	private static partial Regex unEscapedCommaRegex();

	public static void SingleLine(string str)
	{
		console.PrintSingleLine(str);
	}

}
