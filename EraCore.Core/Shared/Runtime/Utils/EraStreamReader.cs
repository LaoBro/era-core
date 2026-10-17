using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Sub;

internal sealed partial class EraStreamReader : IDisposable
{
	public EraStreamReader(bool useRename)
	{
		this.useRename = useRename;
	}

	string filepath = null!;
	string filename = null!;
	readonly bool useRename;
	int curNo;
	int nextNo = 1;
	string[] _fileLines = null!;
	public bool Open(string path)
	{
		return Open(path, SafPath.GetLogicalFileName(path));
	}

	public bool Open(string path, string name)
	{
		return OpenOnCache(path, name);
	}
	public bool OpenOnCache(string path)
	{

		return OpenOnCache(path, SafPath.GetLogicalFileName(path));
	}

	public bool OpenOnCache(string path, string name)
	{
		filepath = path.ToString();
		filename = name.ToString();
		curNo = 0;
		nextNo = 0;
		var cached = Preload.TryGetFileLines(path);
		if (cached != null)
		{
			_fileLines = cached;
			return true;
		}
		// issues/10 10-B：CSV/ERH 内容缓存轮内快照（命中轮重水化，重水化前经逐目录元数据校验）。
		// 本类是 CSV（GAMEBASE/固定名/CHARA/VarExt/.als）与 ERH 读取的唯一咽喉——在 Preload miss 后
		// 查快照即全覆盖；miss（未重水化 / 条目未缓存）照旧走 DirAccessor 懒读。
		var srcCached = SrcContentCache.TryGetLines(path);
		if (srcCached != null)
		{
			_fileLines = srcCached;
			return true;
		}
		// ADR-0037：原来在 accessor 读空之后还有一段「直连文件系统最后兜底」（File.ReadAllBytes），
		// 现一并删除 —— 它是 EraCore.Core 里最后一条可编译、可达的直连读缝。
		// 触发条件本就不成立：GamePaths.Current.DirAccessor 在 Program 静态初始化后恒非 null；
		// 而 ReadAllBytes 返回 null 意味着 SAF/文件系统都说文件不存在，此时本地同名 File.* 要么
		// 同样读不到，要么拿到 URI 路径直接抛。失败因此显性化，不再绕开 F-1。
		var dirAccessor = GamePaths.Current?.DirAccessor;
		if (dirAccessor == null)
		{
			EmueraLog.Warn("EraStreamReader", $"DirAccessor unavailable: {path}");
			Dispose();
			return false;
		}
		var bytes = dirAccessor.ReadAllBytes(path);
		if (bytes == null)
		{
			EmueraLog.Warn("EraStreamReader", $"read FAILED: {path}");
			Dispose();
			return false;
		}
		_fileLines = EncodingHandler.ReadAllLinesFromBytes(bytes);
		return true;
	}


	public string ReadLine()
	{
		string ret = null!;
		curNo = nextNo;
		if (_fileLines.Length > curNo)
		{
			ret = _fileLines[curNo];
			nextNo++;
		}
		return ret;
	}

	[GeneratedRegex(@"\[\[.*?\]\]")]
	private static partial Regex regexRenameIdentifer();

	/// <summary>
	/// 次の有効な行を読む。LexicalAnalyzer経由でConfigを参照するのでConfig完成までつかわないこと。
	/// </summary>
	public CharStream ReadEnabledLine(bool disabled = false)
	{
		string line;
		CharStream st;
		while (true)
		{
			line = ReadLine();
			if (line == null)
				return null!;
			if (line.Length == 0)
				continue;

			//Ordinal消して大丈夫なのかわからないのでコメントアウト
			//if (useRename && (line.IndexOf("[[", StringComparison.Ordinal) >= 0) && (line.IndexOf("]]", StringComparison.Ordinal) >= 0))
			// 决策一：恢复 early-out — .NET 5+ string.Contains(string) 默认 Ordinal，与原意一致。
			//   不含 [[ 或 ]] 的行完全跳过 Regex.Match 调用链（零风险：等价于"匹配不到任何内容"分支）。
			// 决策一-a：多 [[...]] 标记行改用 StringBuilder，避免每次 Replace 重新分配整行。
			if (useRename && line.Contains("[[") && line.Contains("]]"))
			{
				var match = regexRenameIdentifer().Match(line);
				if (match.Success)
				{
					var sb = new StringBuilder(line);
					do
					{
						//この段階でマッチしないパターンもある
						if (ParserMediator.RenameDic.TryGetValue(match.Value, out var targetStr))
							sb.Replace(match.Value, targetStr);
					} while ((match = match.NextMatch()).Success);
					line = sb.ToString();
				}
			}
			st = new CharStream(line);
			LexicalAnalyzer.SkipWhiteSpace(st);
			if (st.EOS)
				continue;
			//[SKIPSTART]～[SKIPEND]中にここが誤爆するので無効化
			if (!disabled)
			{
				if (st.Current == '}')
					throw new CodeEE(trerror.UnexpectedContinuationEnd.Text, new ScriptPosition(filename, curNo));
				if (st.Current == '{')
				{
					if (line.Trim() != "{")
						throw new CodeEE(trerror.CharacterAfterContinuation.Text, new ScriptPosition(filename, curNo));
					break;
				}
			}
			return st;
		}
		//curNoはこの後加算しない(始端記号の行を行番号とする)
		StringBuilder b = new();
		while (true)
		{
			line = ReadLine();
			if (line == null)
			{
				throw new CodeEE(trerror.NotCloseLineContinuation.Text, new ScriptPosition(filename, curNo));
			}

			//if (useRename && (line.IndexOf("[[", StringComparison.Ordinal) >= 0) && (line.IndexOf("]]", StringComparison.Ordinal) >= 0))
			// 决策一：续行路径同样恢复 early-out，与主循环对称。
			// 决策一-a：多标记行改用 StringBuilder。
			if (useRename && line.Contains("[[") && line.Contains("]]"))
			{
				//この段階でマッチしないパターンもある
				var match = regexRenameIdentifer().Match(line);
				if (match.Success)
				{
					var sb = new StringBuilder(line);
					do
					{
						if (ParserMediator.RenameDic.TryGetValue(match.Value, out var targetStr))
							sb.Replace(match.Value, targetStr);
					} while ((match = match.NextMatch()).Success);
					line = sb.ToString();
				}
			}
			var test = line.AsSpan().TrimStart();
			if (test.Length > 0)
			{
				if (test[0] == '}')
				{
					if (!test.TrimEnd().SequenceEqual("}"))
						throw new CodeEE(trerror.CharacterAfterContinuationEnd.Text, new ScriptPosition(filename, curNo));
					break;
				}
				//行連結文字なら1字でないとおかしい、というか、こうしないとFORMの数値変数処理が誤爆する。
				//{
				//A}
				//みたいなどうしようもないコードは知ったこっちゃない
				if (test.SequenceEqual("{"))
					throw new CodeEE(trerror.UnexpectedContinuation.Text, new ScriptPosition(filename, curNo));
			}
			b.Append(line);
			b.Append(Config.ReplaceContinuationBR.Replace("\"", ""));
		}
		st = new CharStream(b.ToString());
		LexicalAnalyzer.SkipWhiteSpace(st);
		return st;
	}

	/// <summary>
	/// 直前に読んだ行の行番号
	/// </summary>
	public int LineNo
	{ get { return curNo; } }
	public string Filename
	{
		get
		{
			return filename;
		}
	}
	//public string Filepath
	//{
	//    get
	//    {
	//        return filepath;
	//    }
	//}

	public void Close() { Dispose(); }
	bool disposed;
	#region IDisposable メンバ

	public void Dispose()
	{
		if (disposed)
			return;
		filepath = null!;
		filename = null!;
		disposed = true;
		_fileLines = null!;
	}

	#endregion
}
