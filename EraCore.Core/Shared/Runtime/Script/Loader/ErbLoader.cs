using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Sub;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.Runtime.Script.Loader;

internal sealed class ErbLoader
{
	// ADR-0012：F2——构造注入显式依赖，不持 Process 反向引用。
	//   scaningLine 写 → Action<LogicalLine> sink
	//   进程级目录 / Analysis / Debug 标志 → LoaderEnv
	//   标识符字典 → IdentifierDictionary idDic（原走 GlobalStatic 兼容层）
	public ErbLoader(EmueraConsole main, ExpressionMediator exm, IdentifierDictionary idDic,
		LoaderEnv env, Action<LogicalLine> setScanLine)
	{
		output = main;
		this.exm = exm;
		this.idDic = idDic;
		this.env = env;
		this.setScanLine = setScanLine;
	}
	readonly ExpressionMediator exm;
	readonly IdentifierDictionary idDic;
	readonly LoaderEnv env;
	readonly Action<LogicalLine> setScanLine;
	readonly EmueraConsole output;
	readonly HashSet<string> ignoredFNFWarningFiles = new(StringComparer.OrdinalIgnoreCase);
	int ignoredFNFWarningCount;

	// 决策三-a：nestCheck 栈/列表复用 — 1000 函数避免 3000+ 次堆分配。
	// loadErb/ParseScript 为单线程调用，字段在 nestCheck 入口 Clear() 即可安全复用。
	private readonly Stack<InstructionLine> _nestStackReuse = new();
	private readonly Stack<InstructionLine> _selectcaseStackReuse = new();
	private readonly List<InstructionLine> _tempLineListReuse = new();

	int enabledLineCount;
	LabelDictionary labelDic = null!;

	/// <summary>erb-loader-parallel 03：并行生成度上限——窗口内产物同时驻留 = 上限文件数的解析产物，
	/// 封顶防内存峰值（spec 实测 eraTW 规模 12 核理想并行度 ~8-10x，取 8；默认度 = ProcessorCount）。</summary>
	internal const int MaxParallelErbFiles = 8;

	bool noError = true;
	/// <summary>
	/// 複数のファイルを読む
	/// </summary>
	/// <param name="filepath"></param>
	public async Task<bool> LoadErbDir(string erbDir, bool displayReport, LabelDictionary labelDictionary)
	{
		//1.713 labelDicをnewする位置を変更。
		//checkScript();の時点でExpressionPerserがProcess.instance.LabelDicを必要とするから。
		labelDic = labelDictionary;
		labelDic.Initialized = false;
		#region EE_ファイル読み込み順拡張
		var firstDir = env.DirAccessor.GetDirectories(erbDir, "*#*", Config.Config.SearchSubdirectory ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly) ?? [];
		// erb-loader-parallel 02：firstDir 已载文件查重 O(n²)（List 线性扫）→ 集合查找；
		// 文件系统路径大小写不敏感，与既有 SCIgnore 语义一致
		HashSet<string> loadedFiles = new(StringComparer.OrdinalIgnoreCase);
		#endregion
		var erbFiles = Preload.GetFilesFromCache(erbDir, ".ERB", "ErbLoader") ?? Config.Config.GetFiles(erbDir, "*.ERB") ?? [];
		List<string> isOnlyEvent = [];
		noError = true;
		var starttime = DateTime.Now;
		EmueraLog.Info("ErbLoader", $"LoadErbDir start: dir={erbDir}, files={erbFiles.Count}, firstDirs={firstDir.Length}");
		// erb-loader-parallel 03：统一有序待载清单（firstDir 目录序在前、主枚举去重序在后），
		// 串行 / 并行两条调度共用同一清单与合并序
		List<(string Filename, string File)> pending = [];
		try
		{
			labelDic.RemoveAll();
			#region EE_ファイル読み込み順拡張
			foreach (var dir in firstDir)
			{
				var firstErbFiles = Preload.GetFilesFromCache(dir, ".ERB", "ErbLoader") ?? Config.Config.GetFiles(dir, erbDir, "*.ERB");
				foreach (var erb in firstErbFiles ?? [])
				{
					loadedFiles.Add(erb.Value);
					pending.Add((erb.Key, erb.Value));
				};
			};
			#endregion

			foreach (var erb in erbFiles)
			{
				if (loadedFiles.Contains(erb.Value))
					continue;
				pending.Add((erb.Key, erb.Value));
			};
			// erb-loader-parallel 03：默认并行；配置串行或 AnalysisMode 时走 01 完成时的串行路径
			//（fallback = 语义偏差排查 / 单线程调试的 abort 杠杆；AnalysisMode 的 PrintC 输出与
			// funcCount 进度节奏依赖串行即时检查，一律强制串行）
			// erb-loader-parallel 03：阶段计时（genmerge = 生成+合并，与 ParseScript 分开观测）
			var phaseSw = Stopwatch.StartNew();
			if (Config.Config.ParallelErbLoad && !env.AnalysisMode)
				await LoadPendingParallel(pending, isOnlyEvent, displayReport, starttime);
			else
				await LoadPendingSerial(pending, isOnlyEvent, displayReport, starttime);
			phaseSw.Stop();
			EmueraLog.Info("ErbLoader", $"phase=genmerge parallel={Config.Config.ParallelErbLoad && !env.AnalysisMode} files={pending.Count} ms={phaseSw.ElapsedMilliseconds}");
			ParserMediator.FlushWarningList();
#if DEBUG
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, (DateTime.Now - starttime).TotalMilliseconds));
#endif
			if (displayReport)
				output.PrintSystemLine(trsl.BuildingUserFunc.Text);
			setLabelsArg();
			ParserMediator.FlushWarningList();
			labelDic.Initialized = true;
#if DEBUG
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, (DateTime.Now - starttime).TotalMilliseconds));
#endif
			if (displayReport)
				output.PrintSystemLine(trsl.CheckingSyntax.Text);

			// 决策四：ParseScript 阶段计时（Stopwatch 精度，displayReport 时可见）
			var parseSw = Stopwatch.StartNew();
			await Task.Run(() => ParseScript());
			parseSw.Stop();
			EmueraLog.Info("ErbLoader", $"phase=parsescript ms={parseSw.ElapsedMilliseconds}");
			if (displayReport)
				output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, parseSw.ElapsedMilliseconds));

			ParserMediator.FlushWarningList();

#if DEBUG
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, (DateTime.Now - starttime).TotalMilliseconds));
#endif
			if (displayReport)
				output.PrintSystemLine(trsl.LoadComplete.Text);
		}
		catch (Exception e)
		{
			ParserMediator.FlushWarningList();
#if !HEADLESS
			System.Media.SystemSounds.Hand.Play();
#endif
			var msg = $"[ErbLoader] EXCEPTION: {e.GetType().Name}: {e.Message}\n{e.StackTrace}";
			EmueraLog.Error("ErbLoader", msg);
			output.PrintError(string.Format(trerror.UnexpectedErrorFrom.Text, AssemblyData.EmueraVersionText));
			output.PrintError(e.GetType().ToString() + ":" + e.Message);
			return false;
		}
		finally
		{
			// finally exception bypasses catch above and bubbles to Process.Initialize
			try
			{
				setScanLine(null!);
			}
			catch (Exception ex)
			{
				var msg = $"[ErbLoader] finally exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
				EmueraLog.Error("ErbLoader", msg);
			}
		}
		EmueraLog.Info("ErbLoader", $"LoadErbDir done: noError={noError}, labels={labelDic?.Count ?? 0}");
		isOnlyEvent.Clear();
		return noError;
	}

	/// <summary>
	/// 指定されたファイルを読み込む
	/// </summary>
	/// <param name="filename"></param>
	public async Task<bool> LoadErbList(List<string> paths, LabelDictionary labelDictionary)
	{
		string fname;
		List<string> isOnlyEvent = [];
		noError = true;
		labelDic = labelDictionary;
		labelDic.Initialized = false;

		await Task.Run(() =>
		{
			foreach (var fpath in paths)
			{
				if (fpath.StartsWith(env.ErbDir, Config.Config.SCIgnoreCase) && !env.AnalysisMode)
					fname = SafPath.GetRelativePathFromRoot(env.ErbDir, fpath);
				else
					fname = fpath;
				if (env.AnalysisMode)
				{
					output.PrintSystemLine(string.Format(trsl.LoadingFile.Text, fname));
				}
				// erb-loader-parallel 01/03：与 LoadErbDir 串行调度同款“生成 → 按序合并”两步
				//（部分重载文件数少，不参与并行化；跨文件检查保持串行即时）
				var art = GenerateErbFile(fpath, fname, isOnlyEvent, deferCrossFileChecks: false);
				MergeErbFile(art, isOnlyEvent);
			};
		});
		if (env.AnalysisMode)
			output.NewLine();
		ParserMediator.FlushWarningList();
		setLabelsArg();
		ParserMediator.FlushWarningList();
		labelDic.Initialized = true;

		// 决策四：ParseScript 阶段计时（AnalysisMode 时可见）
		var parseSw = Stopwatch.StartNew();
		await Task.Run(() => ParseScript());
		parseSw.Stop();
		if (env.AnalysisMode)
			output.PrintSystemLine(string.Format(trsl.ElapsedTime.Text, parseSw.ElapsedMilliseconds));

		ParserMediator.FlushWarningList();
		setScanLine(null!);
		isOnlyEvent.Clear();
		return noError;
	}

	private sealed class PPState
	{
		// ADR-0012：原走 Program.DebugMode / GlobalStatic.IdentifierDictionary（static），
		// 现经构造注入从外层 ErbLoader 传入。
		readonly LoaderEnv env;
		readonly IdentifierDictionary idDic;
		internal PPState(LoaderEnv env, IdentifierDictionary idDic) { this.env = env; this.idDic = idDic; }

		bool skip;
		bool done;
		public bool Disabled;
		readonly Stack<bool> disabledStack = new();
		readonly Stack<bool> doneStack = new();
		readonly Stack<string> ppMatch = new();

		internal void AddKeyWord(string token, string token2, ScriptPosition? position)
		{
			//bool token2enabled = string.IsNullOrEmpty(token2);
			switch (token)
			{
				case "SKIPSTART":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					if (skip)
					{
						ParserMediator.Warn(trerror.DuplicateSkipstart.Text, position, 1);
						break;
					}
					ppMatch.Push("SKIPEND");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					skip = true;
					Disabled = true;
					done = false;
					break;
				case "IF_DEBUG":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					Disabled = !env.DebugMode;
					done = !Disabled;
					break;
				case "IF_NDEBUG":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					Disabled = env.DebugMode;
					done = !Disabled;
					break;
				case "IF":
					if (string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.MissingArguments.Text, token), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					disabledStack.Push(Disabled);
					doneStack.Push(done);
					Disabled = idDic.GetMacro(token2) == null;
					done = !Disabled;
					break;
				case "ELSEIF":
					if (string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.MissingArguments.Text, token), position, 1);
						break;
					}
					if (ppMatch.Count == 0 || ppMatch.Pop() != "ELSEIF")
					{
						ParserMediator.Warn(string.Format(trerror.IsInvalid.Text, "[ELSEIF]"), position, 1);
						break;
					}
					ppMatch.Push("ELSEIF");
					Disabled = done || idDic.GetMacro(token2) == null;
					done |= !Disabled;
					break;
				case "ELSE":
					if (!string.IsNullOrEmpty(token2))
					{
						ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					if (ppMatch.Count == 0 || ppMatch.Pop() != "ELSEIF")
					{
						ParserMediator.Warn(string.Format(trerror.IsInvalid.Text, "[ELSE]"), position, 1);
						break;
					}
					ppMatch.Push("ENDIF");
					Disabled = done;
					done = true;
					break;

				case "SKIPEND":
					{
						if (!string.IsNullOrEmpty(token2))
						{
							ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
							break;
						}
						string match = ppMatch.Count == 0 ? "" : ppMatch.Pop();
						if (match != "SKIPEND")
						{
							ParserMediator.Warn(trerror.UnexpectedSkipend.Text, position, 1);
							break;
						}
						skip = false;
						Disabled = disabledStack.Pop();
						done = doneStack.Pop();
					}
					break;
				case "ENDIF":
					{
						if (!string.IsNullOrEmpty(token2))
						{
							ParserMediator.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
							break;
						}
						string match = ppMatch.Count == 0 ? "" : ppMatch.Pop();
						if (match != "ENDIF" && match != "ELSEIF")
						{
							ParserMediator.Warn(trerror.UnexpectedMacroEndif.Text, position, 1);
							break;
						}
						Disabled = disabledStack.Pop();
						done = doneStack.Pop();
					}
					break;
				default:
					ParserMediator.Warn(trerror.UnrecognizedPreprosessor.Text, position, 1);
					break;
			}
			if (skip)
				Disabled = true;
		}

		internal void FileEnd(ScriptPosition? position)
		{
			if (ppMatch.Count != 0)
			{
				string match = ppMatch.Pop();
				if (match == "ELSEIF")
					match = "ENDIF";
				ParserMediator.Warn(string.Format(trerror.TheresNo.Text, match), position, 1);
			}
		}
	}

	/// <summary>
	/// 并行生成期的跨文件重名检查待办（erb-loader-parallel 03）。并行阶段 labelDic 不接受并发读，
	/// 该检查延后到合并期（<see cref="ErbLoader.MergeErbFile"/>）执行；为保证警告仍出现在
	/// 原 @ 行的行序位置，生成期先在 <see cref="ErbFileArtifact.Warnings"/> 放占位槽，
	/// 合并期替换为真实警告或删除。
	/// </summary>
	internal readonly record struct PendingOverloadCheck(int Slot, FunctionLabelLine Label, FunctionLabelLine? InFileSenior);

	/// <summary>并行生成期的跨文件 #ONLY 重複宣言检查待办（同上，占位槽机制）。</summary>
	internal readonly record struct PendingOnlyCheck(int Slot, string LabelName, ScriptPosition Position);

	/// <summary>
	/// 单文件解析产物（erb-loader-parallel Issue 01）。
	/// 生成阶段（<see cref="GenerateErbFile"/>）只写本对象：行链挂在各标签线上、警告经
	/// ParserMediator 重定向进 <see cref="Warnings"/>、控制台输出（AnalysisMode PrintC/间隔行、
	/// 开文件失败 PrintError）缓冲为 <see cref="ConsoleOps"/>——生成期间无任何 LabelDictionary
	/// 写入 / 控制台直写 / 共享计数写。合并阶段（<see cref="MergeErbFile"/>）按原文件顺序执行
	/// 全部共享状态写入与警告发射，03 并行化以本对象为执行单元。
	/// </summary>
	private sealed class ErbFileArtifact
	{
		public string Filename = null!;
		/// <summary>@ 标签（含 InvalidLabelLine），按出现序；合并阶段逐个 AddLabel / AddInvalidLabel。</summary>
		public readonly List<FunctionLabelLine> FunctionLabels = [];
		/// <summary>$ 标签（ParentLabelLine 生成期已设），按出现序；仅收录 parent 非空者。</summary>
		public readonly List<GotoLabelLine> GotoLabels = [];
		/// <summary>#ONLY 宣言名，按出现序；合并阶段 AddRange 进共享 isOnlyEvent 列表。</summary>
		public readonly List<string> OnlyDeclarations = [];
		/// <summary>逐文件缓冲的 ParserMediator 警告，合并阶段按序发射。</summary>
		public readonly List<ParserMediator.ParserWarning> Warnings = [];
		/// <summary>控制台输出回放（AnalysisMode PrintC/间隔行、开文件失败 PrintError）。</summary>
		public readonly List<Action<EmueraConsole>> ConsoleOps = [];
		/// <summary>本文件最后一次 scaningLine 写（VARI 初期值解析镜像）；null = 本文件未写。</summary>
		public LogicalLine? ScaningLine;
		/// <summary>并行生成期的跨文件重名检查待办（占位槽）；串行路径恒空。</summary>
		public readonly List<PendingOverloadCheck> PendingOverloads = [];
		/// <summary>并行生成期的跨文件 #ONLY 重複宣言检查待办（占位槽）；串行路径恒空。</summary>
		public readonly List<PendingOnlyCheck> PendingOnlys = [];
		public int EnabledLineCount;
		public bool HasError;
	}

	/// <summary>
	/// ファイル一つを読む（生成阶段）：产出全部收进文件产物，不触碰 LabelDictionary / 控制台 / 共享计数。
	/// 串行路径下紧随 <see cref="MergeErbFile"/>；生成期对共享状态只读（labelDic 重载检查的合并序
	/// 快照、isOnlyEvent 已合并宣言、IdentifierDictionary 宏表）。
	/// </summary>
	/// <param name="deferCrossFileChecks">erb-loader-parallel 03：并行调度下为 true——跨文件重名与
	/// #ONLY 重複检查延后到合并期（占位槽保行序），生成期不读 labelDic / isOnlyEvent 共享快照。</param>
	/// <param name="replayOnFailure">串行调度为 true（异常时本方法内回放控制台输出与警告再抛，与旧路径
	/// 一致）；并行调度为 false（缓冲清理照常，回放由主线程按窗口序统一执行，避免并发控制台写）。</param>
	private ErbFileArtifact GenerateErbFile(string filepath, string filename, List<string> isOnlyEvent,
		bool deferCrossFileChecks, bool replayOnFailure = true)
	{
		var art = new ErbFileArtifact { Filename = filename };
		ParserMediator.BeginWarningBuffer(art.Warnings);
		LogicalLineParser.ScanLineMirror.Value = line => art.ScaningLine = line;
		try
		{
			GenerateErbFileCore(filepath, filename, isOnlyEvent, art, deferCrossFileChecks);
		}
		catch
		{
			// 异常路径输出与旧路径一致：已产生的控制台输出与警告照常发射（旧路径为直写/直入全局列表）
			if (replayOnFailure)
			{
				ReplayConsoleOps(art);
				ParserMediator.EmitWarningBuffer(art.Warnings);
			}
			throw;
		}
		finally
		{
			LogicalLineParser.ScanLineMirror.Value = null;
			ParserMediator.EndWarningBuffer();
		}
		return art;
	}

	private void GenerateErbFileCore(string filepath, string filename, List<string> isOnlyEvent, ErbFileArtifact art,
		bool deferCrossFileChecks)
	{
		using var eReader = new EraStreamReader(Config.Config.UseRenameFile && ParserMediator.RenameDic != null);

		if (!eReader.OpenOnCache(filepath, filename))
		{
			art.ConsoleOps.Add(c => c.PrintError(string.Format(trerror.FailedOpenFile.Text, eReader.Filename)));
			return; // ADR-0019：文件打不开直接跳过，避免空 eReader 进入解析循环
		}
		var ppstate = new PPState(env, idDic);
		// 并行调度：跨文件 #ONLY 重複检查延后（共享 isOnlyEvent 是同窗口其他文件未合并的不完整快照），
		// 传独立空视图禁用即时检查；串行调度传共享快照即时检查（01 行为）。
		List<string> mergedOnlyView = deferCrossFileChecks ? [] : isOnlyEvent;
		LogicalLine nextLine = new NullLine();
		LogicalLine lastLine = new NullLine();
		FunctionLabelLine lastLabelLine = null!;
		CharStream st = null!;
		ScriptPosition? position = null;
		int funcCount = 0;
		// 生成期重名判定的文件内视图：跨文件同名经 labelDic.GetEarliestLabelWithName 按合并序读
		var firstValidLabelByName = new Dictionary<string, FunctionLabelLine>(Config.Config.StrComper);
		var firstGotoByParent = new Dictionary<FunctionLabelLine, Dictionary<string, GotoLabelLine>>();
		if (env.AnalysisMode)
			art.ConsoleOps.Add(c => c.PrintSystemLine(" "));
		while ((st = eReader.ReadEnabledLine(ppstate.Disabled)) != null)
		{
			position = new ScriptPosition(eReader.Filename, eReader.LineNo);
			//rename処理をEraStreamReaderに移管
			//変換できなかった[[～～]]についてはLexAnalyzerがエラーを投げる
			if (st.Current == '[' && st.Next != '[')
			{
				st.ShiftNext();
				string token = LexicalAnalyzer.ReadSingleIdentifier(st);
				LexicalAnalyzer.SkipWhiteSpace(st);
				string token2 = LexicalAnalyzer.ReadSingleIdentifier(st);
				if (string.IsNullOrEmpty(token) || st.Current != ']')
					ParserMediator.Warn(trerror.InvalidSBrackets.Text, position, 1);
				ppstate.AddKeyWord(token, token2, position);
				st.ShiftNext();
				if (!st.EOS)
					ParserMediator.Warn(string.Format(trerror.IgnoreAfterPreprosessor.Text, token), position, 1);
				continue;
			}
			//if ((skip) || (env.DebugMode && ifndebug) || (!env.DebugMode && ifdebug))
			//	continue;
			if (ppstate.Disabled)
				continue;
			//ここまでプリプロセッサ

			if (st.Current == '#')
			{
				if (lastLine == null || lastLine is not FunctionLabelLine funcLine)
				{
					ParserMediator.Warn(trerror.InvalidSharp.Text, position, 1);
					continue;
				}
				// 占位锚：本 # 行首个警告的槽位（跨文件 ONLY 检查占位须插到这里——
				// 串行路径该警告在 ParseSharpLine 的 Contains 检查点发射，先于同行的 BeIgnore* 警告）
				int lineWarnStart = art.Warnings.Count;
				int onlyCountBefore = art.OnlyDeclarations.Count;
				if (!LogicalLineParser.ParseSharpLine(funcLine, st, position, mergedOnlyView, art.OnlyDeclarations))
					art.HasError = true;
				if (deferCrossFileChecks && art.OnlyDeclarations.Count > onlyCountBefore)
				{
					int insertAt = lineWarnStart;
					for (int j = onlyCountBefore; j < art.OnlyDeclarations.Count; j++)
					{
						art.Warnings.Insert(insertAt, PlaceholderWarning());
						art.PendingOnlys.Add(new PendingOnlyCheck(insertAt, art.OnlyDeclarations[j], position!.Value));
						insertAt++;
					}
				}
				continue;
			}
			if (st.Current == '$' || st.Current == '@')
			{
				bool isFunction = st.Current == '@';
				nextLine = LogicalLineParser.ParseLabelLine(st, position, output, art.ConsoleOps);
				if (isFunction)
				{
					var label = nextLine as FunctionLabelLine;
					lastLabelLine = label!;
					art.FunctionLabels.Add(label!);
					if (label is InvalidLabelLine)
					{
						art.HasError = true;

						ParserMediator.Warn(nextLine.ErrMes, position, 2);
					}
					else// if (label is FunctionLabelLine)
					{
						var inFileSenior = firstValidLabelByName.GetValueOrDefault(label!.LabelName);
						if (!label!.IsEvent && (Config.Config.WarnNormalFunctionOverloading || env.AnalysisMode))
						{
							if (deferCrossFileChecks)
							{
								// 并行调度：跨文件 senior 合并期查 labelDic（此处不可并发读）；
								// 文件内首现表作 InFileSenior 一并延后——合并期 senior 判定序
								// （跨文件最早 ?? 文件内首现）与串行即时检查一致，警告仍在 @ 行行序位置
								art.PendingOverloads.Add(new PendingOverloadCheck(art.Warnings.Count, label, inFileSenior));
								art.Warnings.Add(PlaceholderWarning());
							}
							else
							{
								// 重载检查（生成期合并序只读视图 + 文件内首现表，与旧“先清同文件旧标签、
								// AddLabel 后 GetSameNameLabel 取最早同名”语义一致：
								// excludingFilename = 本文件——重载场景旧标签要到合并阶段才被清掉，须排除）
								FunctionLabelLine? seniorLabel = labelDic.GetEarliestLabelWithName(label.LabelName, filename);
								seniorLabel ??= inFileSenior;
								if (seniorLabel != null)
								{
									//output.NewLine();
									ParserMediator.Warn(string.Format(trerror.FuncIsAlreadyDefined.Text, label.LabelName, seniorLabel.Position!.Value.Filename, seniorLabel.Position!.Value.LineNo.ToString()), position, 1);
									funcCount = -1;
								}
							}
						}
						funcCount++;
						firstValidLabelByName.TryAdd(label.LabelName, label);
						if (env.AnalysisMode && Config.Config.PrintCPerLine > 0 && funcCount % Config.Config.PrintCPerLine == 0)
						{
							art.ConsoleOps.Add(c => c.NewLine());
							art.ConsoleOps.Add(c => c.PrintSystemLine(" "));
						}
					}
				}
				else
				{
					if (nextLine is GotoLabelLine gotoLabel)
					{
						gotoLabel.ParentLabelLine = lastLabelLine!;
						if (lastLabelLine != null)
						{
							art.GotoLabels.Add(gotoLabel);
							// 同関数内 $ 重複判定（AddLabelDollar 的 TryAdd 语义：同 parent+名保留首个）
							if (!firstGotoByParent.TryGetValue(lastLabelLine, out var byName))
							{
								byName = new Dictionary<string, GotoLabelLine>(Config.Config.StrComper);
								firstGotoByParent.Add(lastLabelLine, byName);
							}
							if (byName.TryGetValue(gotoLabel.LabelName, out GotoLabelLine? firstGoto))
							{
								ScriptPosition pos = firstGoto.Position!.Value;
								ParserMediator.Warn(string.Format(trerror.LabelIsAlreadyDefined.Text, gotoLabel.LabelName, pos.Filename, pos.LineNo.ToString()), position, 2);
							}
							else
								byName.Add(gotoLabel.LabelName, gotoLabel);
						}
					}
				}
				if (nextLine is InvalidLine)
				{
					art.HasError = true;
					ParserMediator.Warn(nextLine.ErrMes, position, 2);
				}
			}
			else
			{
				//1808alpha006 処理位置変更
				////全置換はここで対応
				////1756beta1+++ 最初に全置換してしまうと関数定義を_Renameでとか論外なことができてしまうので永久封印した
				//if (ParserMediator.RenameDic != null && st.CurrentEqualTo("[[") && (rowLine.TrimEnd().IndexOf("]]") == rowLine.TrimEnd().Length - 2))
				//{
				//    string replacedLine = st.Substring();
				//    foreach (KeyValuePair<string, string> pair in ParserMediator.RenameDic)
				//        replacedLine = replacedLine.Replace(pair.Key, pair.Value);
				//    st = new StringStream(replacedLine);
				//}
				if (lastLabelLine == null)
					ParserMediator.Warn(trerror.LineBeforeFunc.Text, position, 1);
				nextLine = LogicalLineParser.ParseLine(st, position, output, lastLabelLine!);


				if (nextLine == null)
					continue;
				if (nextLine is InvalidLine)
				{
					art.HasError = true;
					ParserMediator.Warn(nextLine.ErrMes, position, 2);
				}
				else if (JSONConfig.Data.UseNewRandom &&
										nextLine is InstructionLine instruction)
				{
					switch (instruction.FunctionCode)
					{
						case FunctionCode.RANDOMIZE:
							ParserMediator.Warn(trerror.IgnoreRandomize.Text, position, 0);
							break;
						case FunctionCode.DUMPRAND:
							ParserMediator.Warn(trerror.CanNotUseDumprand.Text, position, 0);
							break;
						case FunctionCode.INITRAND:
							ParserMediator.Warn(trerror.CanNotUseInitrand.Text, position, 0);
							break;
						default:
							break;
					}
				}
			}
			nextLine.ParentLabelLine = lastLabelLine!;

			lastLine = addLine(art, nextLine, lastLine!);
		}
		addLine(art, new NullLine(), lastLine!);
		position = new ScriptPosition(eReader.Filename, -1);
		ppstate.FileEnd(position);
		return;
	}

	/// <summary>
	/// 合并阶段（erb-loader-parallel Issue 01）：按原文件顺序把文件产物并入共享状态——
	/// 缓冲警告按序发射、全部 LabelDictionary 写入（IfFileLoadClearLabelWithPath / AddLabel /
	/// AddInvalidLabel / AddLabelDollar）、#ONLY 宣言入共享列表、控制台输出回放、计数聚合。
	/// Index / FileIndex 分配序与警告顺序由“逐文件生成后立即按序合并”保证与旧串行路径一致。
	/// 03：并行产物先经 <see cref="ResolvePendingCrossFileChecks"/> 补齐跨文件检查（占位槽替换）。
	/// </summary>
	private void MergeErbFile(ErbFileArtifact art, List<string> isOnlyEvent)
	{
		ResolvePendingCrossFileChecks(art, isOnlyEvent);
		// 警告最先入列：等价于旧路径“解析期即入全局列表”（共享写入若异常也不丢已产出警告）
		ParserMediator.EmitWarningBuffer(art.Warnings);
		labelDic.IfFileLoadClearLabelWithPath(art.Filename);
		ReplayConsoleOps(art);
		var labels = art.FunctionLabels;
		for (int i = 0; i < labels.Count; i++)
		{
			if (labels[i] is InvalidLabelLine)
				labelDic.AddInvalidLabel(labels[i]);
			else
				labelDic.AddLabel(labels[i]);
		}
		var gotoLabels = art.GotoLabels;
		for (int i = 0; i < gotoLabels.Count; i++)
			labelDic.AddLabelDollar(gotoLabels[i]);
		if (art.OnlyDeclarations.Count > 0)
			isOnlyEvent.AddRange(art.OnlyDeclarations);
		if (art.ScaningLine != null)
			setScanLine(art.ScaningLine);
		enabledLineCount += art.EnabledLineCount;
		if (art.HasError)
			noError = false;
	}

	/// <summary>并行产物的跨文件检查占位（合并期由 <see cref="ResolvePendingCrossFileChecks"/> 替换或删除）。</summary>
	private static ParserMediator.ParserWarning PlaceholderWarning() => new("", null, 1, null!);

	/// <summary>
	/// erb-loader-parallel 03：合并期补检——并行生成期延后的跨文件重名（FuncIsAlreadyDefined）与
	/// #ONLY 重複宣言（AlreadyDeclaredOnly）检查。此时 labelDic / isOnlyEvent 恰含“更早合并文件”
	/// 的全部内容（本文件的 IfFileLoadClearLabelWithPath 尚未执行、宣言尚未 AddRange），与串行路径
	/// 生成期快照的可见性一致，判定结果与警告序（占位槽位置）均与串行一致。串行产物无待办，no-op。
	/// </summary>
	private void ResolvePendingCrossFileChecks(ErbFileArtifact art, List<string> isOnlyEvent)
	{
		if (art.PendingOverloads.Count == 0 && art.PendingOnlys.Count == 0)
			return;
		List<int> deadSlots = [];
		foreach (var check in art.PendingOverloads)
		{
			// senior 判定序与串行即时检查一致：跨文件最早（排除本文件名，重载场景旧标签
			// 由本文件稍后的 IfFileLoadClearLabelWithPath 清理）?? 文件内首现
			FunctionLabelLine? seniorLabel = labelDic.GetEarliestLabelWithName(check.Label.LabelName, art.Filename);
			seniorLabel ??= check.InFileSenior;
			if (seniorLabel != null)
			{
				art.Warnings[check.Slot] = new ParserMediator.ParserWarning(
					string.Format(trerror.FuncIsAlreadyDefined.Text, check.Label.LabelName,
						seniorLabel.Position!.Value.Filename, seniorLabel.Position!.Value.LineNo.ToString()),
					check.Label.Position, 1, null!);
			}
			else
				deadSlots.Add(check.Slot);
		}
		foreach (var check in art.PendingOnlys)
		{
			if (isOnlyEvent.Contains(check.LabelName))
			{
				art.Warnings[check.Slot] = new ParserMediator.ParserWarning(
					string.Format(trerror.AlreadyDeclaredOnly.Text, check.LabelName), check.Position, 1, null!);
			}
			else
				deadSlots.Add(check.Slot);
		}
		deadSlots.Sort();
		for (int i = deadSlots.Count - 1; i >= 0; i--)
			art.Warnings.RemoveAt(deadSlots[i]);
	}

	private void ReplayConsoleOps(ErbFileArtifact art)
	{
		var ops = art.ConsoleOps;
		for (int i = 0; i < ops.Count; i++)
			ops[i](output);
		ops.Clear();
	}

	/// <summary>displayReport 逐文件进度行（原两处内联打印收敛；DEBUG 构建带累计耗时）。</summary>
	private void PrintLoadingProgress(bool displayReport, DateTime starttime, string filename)
	{
		if (!displayReport)
			return;
#if DEBUG
		output.PrintSystemLine(string.Format(trsl.ElapsedTimeLoad.Text, (DateTime.Now - starttime).TotalMilliseconds, filename));
#else
		output.PrintSystemLine(string.Format(trsl.LoadingFile.Text, filename));
#endif
	}

	/// <summary>
	/// 串行调度（erb-loader-parallel 03 fallback）：逐文件“生成 → 按序合并”，与 01 完成时的行为
	/// 完全一致——进度行在生成前打印、跨文件检查生成期即时、异常即回放再抛。
	/// </summary>
	private async Task LoadPendingSerial(List<(string Filename, string File)> pending, List<string> isOnlyEvent,
		bool displayReport, DateTime starttime)
	{
		foreach (var (filename, file) in pending)
		{
			PrintLoadingProgress(displayReport, starttime, filename);
			var art = await Task.Run(() => GenerateErbFile(file, filename, isOnlyEvent, deferCrossFileChecks: false));
			MergeErbFile(art, isOnlyEvent);
		}
	}

	/// <summary>
	/// 并行调度（erb-loader-parallel 03）：全部文件入池生成，经 <see cref="MaxParallelErbFiles"/> 封顶的
	/// 信号量限流（并发生成中的文件数 = degree，防内存峰值）；本方法按原文件序逐个 await 产物并合并——
	/// 无窗口 barrier，最慢文件的长尾不再阻塞后续文件的合并（流水线）。生成期经 per-task AsyncLocal
	/// （警告缓冲 / ScanLineMirror / Config scope 由 Task.Run 快照 ExecutionContext 各自独立）做到无共享写；
	/// 全部 LabelDictionary 写入与警告发射都在本方法（主线程 async 流）按序执行。进度行随合并按序补发——
	/// 输出内容与顺序与串行一致（PR 决策：按序补发而非聚合行）。异常任务不在任务内回放控制台输出，
	/// 由本方法按序单线程回放后抛首个异常（避免并发控制台写）。
	/// </summary>
	private async Task LoadPendingParallel(List<(string Filename, string File)> pending, List<string> isOnlyEvent,
		bool displayReport, DateTime starttime)
	{
		int degree = Math.Min(Environment.ProcessorCount, MaxParallelErbFiles);
		EmueraLog.Info("ErbLoader", $"LoadPendingParallel: files={pending.Count}, degree={degree}");
		var arts = new ErbFileArtifact?[pending.Count];
		var errors = new Exception?[pending.Count];
		using var gate = new SemaphoreSlim(degree, degree);
		var tasks = new Task[pending.Count];
		for (int i = 0; i < pending.Count; i++)
		{
			int idx = i; // for 循环变量闭包陷阱：任务体必须捕获本迭代的独立副本
			tasks[i] = Task.Run(async () =>
			{
				await gate.WaitAsync();
				try
				{
					arts[idx] = GenerateErbFile(pending[idx].File, pending[idx].Filename, isOnlyEvent,
						deferCrossFileChecks: true, replayOnFailure: false);
				}
				catch (Exception ex) { errors[idx] = ex; }
				finally
				{
					gate.Release();
				}
			});
		}
		for (int i = 0; i < pending.Count; i++)
		{
			await tasks[i];
			if (errors[i] != null)
			{
				if (arts[i] != null)
				{
					// 复刻串行路径 GenerateErbFile 的异常回放语义（单线程执行）
					ReplayConsoleOps(arts[i]!);
					ParserMediator.EmitWarningBuffer(arts[i]!.Warnings);
				}
				throw errors[i]!;
			}
			PrintLoadingProgress(displayReport, starttime, pending[i].Filename);
			MergeErbFile(arts[i]!, isOnlyEvent);
		}
	}

	private static LogicalLine addLine(ErbFileArtifact art, LogicalLine nextLine, LogicalLine lastLine)
	{
		if (nextLine == null)
			return null!;
		art.EnabledLineCount++;
		lastLine.NextLine = nextLine;
		return nextLine;
	}

	private void setLabelsArg()
	{
		List<FunctionLabelLine> labelList = labelDic.GetAllLabels(false);
		foreach (FunctionLabelLine label in labelList)
		{
			try
			{
				if (label.Arg != null)
					continue;
				setScanLine(label);
				parseLabel(label);
			}
			catch (Exception exc)
			{
	#if !HEADLESS
			System.Media.SystemSounds.Hand.Play();
#endif
				string errmes = exc.Message;
				if (!(exc is EmueraException))
					errmes = exc.GetType().ToString() + ":" + errmes;
				ParserMediator.Warn(string.Format(trerror.FuncArgError.Text, label.LabelName, errmes), label, 2, true, false);
				label.ErrMes = trerror.CalledFailedFunc.Text;
				label.IsError = true;
			}
			finally
			{
				setScanLine(null!);
			}
		}
		labelDic.SortLabels();
	}

	private void parseLabel(FunctionLabelLine label)
	{
		WordCollection wc = label.PopRowArgs();
		string errMes;
		SingleTerm[] subNames;
		VariableTerm[] args = [];
		SingleTerm[] defs = [];
		int maxArg = -1;
		int maxArgs = -1;
		//1807 非イベント関数のシステム関数については警告レベル低下＆エラー解除＆引数を設定するように。
		if (label.IsEvent)
		{
			if (!wc.EOL)
				ParserMediator.Warn(string.Format(trerror.EventFuncHasArg.Text, label.LabelName), label, 2, true, false);
			//label.SubNames = subNames;
			label.Arg = args;
			label.Def = defs;
			label.ArgLength = -1;
			label.ArgsLength = -1;
			return;
		}

		if (!wc.EOL)
		{
			if (label.IsSystem)
				ParserMediator.Warn(string.Format(trerror.SystemFuncHasArg.Text, label.LabelName), label, 2, true, false);
			SymbolWord symbol = (wc.Current as SymbolWord)!;
			wc.ShiftNext();
			if (symbol == null)
			{ errMes = trerror.WrongArgFormat.Text; goto err; }
			if (symbol.Type == '[')//TODO:subNames 結局実装しないかも
			{
				var subNamesRow = ExpressionParser.ReduceArguments(wc, ArgsEndWith.RightBracket, false);
				if (subNamesRow.Count == 0)
				{ errMes = trerror.CanNotEmptyFuncSBrackets.Text; goto err; }
				subNames = new SingleTerm[subNamesRow.Count];
				for (int i = 0; i < subNamesRow.Count; i++)
				{
					if (subNamesRow[i] == null)
					{ errMes = trerror.CanNotOmitFuncDefineArg.Text; goto err; }
					AExpression term = subNamesRow[i].Restructure(exm);
					subNames[i] = (term as SingleTerm)!;
					if (subNames[i] == null)
					{ errMes = trerror.FuncDefineArgOnlyConst.Text; goto err; }
				}
				symbol = (wc.Current as SymbolWord)!;
				if (!wc.EOL && symbol == null)
				{ errMes = trerror.WrongArgFormat.Text; goto err; }
				wc.ShiftNext();
			}
			if (!wc.EOL)
			{
				List<AExpression> argsRow;
				if (symbol!.Type == ',')
					argsRow = ExpressionParser.ReduceArguments(wc, ArgsEndWith.EoL, true);
				else if (symbol.Type == '(')
					argsRow = ExpressionParser.ReduceArguments(wc, ArgsEndWith.RightParenthesis, true);
				else
				{ errMes = trerror.WrongArgFormat.Text; goto err; }
				int length = argsRow.Count / 2;
				args = new VariableTerm[length];
				defs = new SingleTerm[length];
				for (int i = 0; i < length; i++)
				{
					SingleTerm def = null!;
					AExpression term = argsRow[i * 2];
					//引数読み取り時点で判別されないといけない
					//if (term == null)
					//{ errMes = "関数定義の引数は省略できません"; goto err; }
					if (!(term.Restructure(exm) is VariableTerm vTerm) || vTerm.Identifier.IsConst)
					{ errMes = trerror.ArgCanOnlyAssignableVar.Text; goto err; }
					else if (!vTerm.Identifier.IsReference)//参照型なら添え字不要
					{
						if (vTerm is VariableNoArgTerm)
						{ errMes = string.Format(trerror.ArgHasNotSubscript.Text, vTerm.Identifier.Name); goto err; }
						if (!vTerm.isAllConst)
						{ errMes = trerror.ArgSubscriptOnlyConst.Text; goto err; }
					}
					for (int j = 0; j < i; j++)
					{
						if (vTerm.checkSameTerm(args[j]))
							ParserMediator.Warn(string.Format(trerror.DuplicateArg.Text, i + 1, vTerm.GetFullString(), j + 1), label, 1, false, false);
					}
					if (vTerm.Identifier.Code == VariableCode.ARG)
					{
						if (maxArg < vTerm.getEl1forArg + 1)
							maxArg = vTerm.getEl1forArg + 1;
					}
					else if (vTerm.Identifier.Code == VariableCode.ARGS)
					{
						if (maxArgs < vTerm.getEl1forArg + 1)
							maxArgs = vTerm.getEl1forArg + 1;
					}
					bool canDef = vTerm.Identifier.Code == VariableCode.ARG || vTerm.Identifier.Code == VariableCode.ARGS || vTerm.Identifier.IsPrivate;
					term = argsRow[i * 2 + 1];
					if (term is NullTerm)
					{
						if (canDef)// && label.ArgOptional)
						{
							if (vTerm.GetOperandType() == typeof(long))
								def = new SingleLongTerm(0);
							else
								def = new SingleStrTerm("");
						}
					}
					else
					{
						def = (term.Restructure(exm) as SingleTerm)!;
						if (def == null)
						{ errMes = trerror.ArgCanOnlyConst.Text; goto err; }
						if (!canDef)
						{ errMes = trerror.ArgCanOnlyPrivVar.Text; goto err; }
						else if (vTerm.Identifier.IsReference)
						{ errMes = trerror.RefArgCanNotInitialize.Text; goto err; }
						if (vTerm.GetOperandType() != def.GetOperandType())
						{ errMes = trerror.NotMatchTypeArgAndInitialValue.Text; goto err; }
					}
					args[i] = vTerm;
					defs[i] = def!;
				}

			}
		}
		if (!wc.EOL)
		{ errMes = trerror.WrongArgFormat.Text; goto err; }

		//label.SubNames = subNames;
		label.Arg = args;
		label.Def = defs;
		label.ArgLength = maxArg;
		label.ArgsLength = maxArgs;
		return;
	err:
		ParserMediator.Warn(string.Format(trerror.FuncArgError.Text, label.LabelName, errMes), label, 2, true, false);
		return;
	}


	public bool useCallForm;

	/// <summary>
	/// 事前処理したファイルをさらに解析し実行可能な状態にする
	/// </summary>
	private void ParseScript()
	{
		int usedLabelCount = 0;
		int labelDepth = -1;
		List<FunctionLabelLine> labelList = labelDic.GetAllLabels(true);
		while (true)
		{
			labelDepth++;
			int countInDepth = 0;
			foreach (FunctionLabelLine label in labelList)
			{
				if (label.Depth != labelDepth)
					continue;
				//1756beta003 なんで追加したんだろう デバグ中になんかやったのか とりあえずコメントアウトしておく
				//if (label.LabelName == "EVENTTURNEND")
				//    useCallForm = true;
				usedLabelCount++;
				countInDepth++;
				ParseFunctionWithCatch(label);
			}
			if (countInDepth == 0)
				break;
		}
		labelDepth = -1;
		var ignoredFNCWarningFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		int ignoredFNCWarningCount = 0;

		bool ignoreAll = false;
		DisplayWarningFlag notCalledWarning = Config.Config.FunctionNotCalledWarning;
		switch (notCalledWarning)
		{
			case DisplayWarningFlag.IGNORE:
			case DisplayWarningFlag.LATER:
				ignoreAll = true;
				break;
		}
		if (useCallForm)
		{//callform系が使われたら全ての関数が呼び出されたとみなす。
			if (env.AnalysisMode)
				output.PrintSystemLine(trerror.BeNotFuncCheckBecauseUseCallform.Text);
			foreach (FunctionLabelLine label in labelList)
			{
				if (label.Depth != labelDepth)
					continue;
				ParseFunctionWithCatch(label);
			}
		}
		else
		{
			bool ignoreUncalledFunction = Config.Config.IgnoreUncalledFunction;
			foreach (FunctionLabelLine label in labelList)
			{
				if (label.Depth != labelDepth)
					continue;
				//解析モード時は呼ばれなかったものをここで解析
				if (env.AnalysisMode)
					ParseFunctionWithCatch(label);
				bool ignore = false;
				if (notCalledWarning == DisplayWarningFlag.ONCE)
				{
					string filename = label.Position!.Value.Filename;

					if (!string.IsNullOrEmpty(filename))
					{
						if (ignoredFNCWarningFiles.Contains(filename))
						{
							ignore = true;
						}
						else
						{
							ignore = false;
							ignoredFNCWarningFiles.Add(filename);
						}
					}
					//break;
				}
				if (ignoreAll || ignore)
					ignoredFNCWarningCount++;
				else
					ParserMediator.Warn(string.Format(trerror.FuncNeverCalled.Text, label.LabelName), label, 1, false, false);
				// 决策七：AnalysisMode 已在上方 L775 调用过 ParseFunctionWithCatch，
				// nestCheck/setJumpTo 不幂等会重复发射警告。此处跳过避免重复解析。
				if (!ignoreUncalledFunction)
				{
					if (!env.AnalysisMode)
						ParseFunctionWithCatch(label);
				}
				else
				{
					if (!(label.NextLine is NullLine) && !(label.NextLine is FunctionLabelLine))
					{
						if (!label.NextLine.IsError)
						{
							label.NextLine.IsError = true;
							label.NextLine.ErrMes = "呼び出されないはずの関数が呼ばれた";
						}
					}
				}
			}
		}
		if (env.AnalysisMode && (warningDic.Keys.Count > 0 || GlobalStatic.tempDic.Keys.Count > 0))
		{
			output.PrintError(trerror.UndefinedFunctions.Text);
			if (warningDic.Keys.Count > 0)
			{
				output.PrintError(trerror.GeneralFunc.Text);
				foreach (string labelName in warningDic.Keys)
				{
					output.PrintError("　　" + labelName + ": " + warningDic[labelName].ToString() + trerror.Occurrences.Text);
				}
			}
			if (GlobalStatic.tempDic.Keys.Count > 0)
			{
				output.PrintError(trerror.SentenceFunc.Text);
				foreach (string labelName in GlobalStatic.tempDic.Keys)
				{
					output.PrintError("　　" + labelName + ": " + GlobalStatic.tempDic[labelName].ToString() + trerror.Occurrences.Text);
				}
			}
		}
		else
		{
			if (ignoredFNCWarningCount > 0 && Config.Config.DisplayWarningLevel <= 1 && notCalledWarning != DisplayWarningFlag.IGNORE)
				output.PrintError(string.Format(trerror.IgnoredFuncNeverCalled.Text, ignoredFNCWarningCount));
			if (ignoredFNFWarningCount > 0 && Config.Config.DisplayWarningLevel <= 2 && notCalledWarning != DisplayWarningFlag.IGNORE)
				output.PrintError(string.Format(trerror.IgnoredUndefinedFuncCall.Text, ignoredFNFWarningCount));
		}
		ParserMediator.FlushWarningList();
		if (Config.Config.DisplayReport)
			output.PrintError(string.Format(trerror.TotalFunc.Text, enabledLineCount, labelDic.Count, usedLabelCount));
		if (Config.Config.AllowFunctionOverloading && Config.Config.WarnFunctionOverloading)
		{
			List<string> overloadedList = idDic.GetOverloadedList(labelDic);
			if (overloadedList.Count > 0)
			{
				output.NewLine();
				output.PrintError(trerror.OverWriteSystemFuncWarn1.Text);
				foreach (string funcname in overloadedList)
				{
					output.PrintSystemLine(string.Format(trerror.OverWriteSystemFuncWarn2.Text, funcname));
				}
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn3.Text);
				output.NewLine();
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn4.Text);
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn5.Text);
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn6.Text);
				output.PrintSystemLine(trerror.OverWriteSystemFuncWarn7.Text);
			}
		}

	}


	public Dictionary<string, long> warningDic = [];
	private void printFunctionNotFoundWarning(string str, LogicalLine line, int level, bool isError)
	{
		if (env.AnalysisMode)
		{
			if (warningDic.TryGetValue(str, out long value))
				warningDic[str] = ++value;
			else
				warningDic.Add(str, 1);
			return;
		}
		if (isError)
		{
			line.IsError = true;
			line.ErrMes = str;
		}
		if (level < Config.Config.DisplayWarningLevel)
			return;
		bool ignore = false;
		DisplayWarningFlag warnFlag = Config.Config.FunctionNotFoundWarning;
		if (warnFlag == DisplayWarningFlag.IGNORE)
			ignore = true;
		else if (warnFlag == DisplayWarningFlag.DISPLAY)
			ignore = false;
		else if (warnFlag == DisplayWarningFlag.ONCE)
		{

			string filename = line.Position!.Value.Filename;
			if (!string.IsNullOrEmpty(filename))
			{
				if (ignoredFNFWarningFiles.Contains(filename))
				{
					ignore = true;
				}
				else
				{
					ignore = false;
					ignoredFNFWarningFiles.Add(filename);
				}
			}
		}
		if (ignore && !env.AnalysisMode)
		{
			ignoredFNFWarningCount++;
			return;
		}
		ParserMediator.Warn(str, line, level, isError, false);
	}

	private void ParseFunctionWithCatch(FunctionLabelLine label)
	{//ここでエラーを捕まえることは本来はないはず。ExeEE相当。
		try
		{
			setArgument(label);
			nestCheck(label);
			setJumpTo(label);
		}
		catch (Exception exc)
		{
#if !HEADLESS
			System.Media.SystemSounds.Hand.Play();
#endif
			//1756beta2+v6.1 修正の効率化のために何かパース関係でハンドリングできてないエラーが出た場合はスタックトレースを投げるようにした
			string errmes = exc is EmueraException ? exc.Message : exc.GetType().ToString() + ":" + exc.Message;
			ParserMediator.Warn(string.Format(trerror.FuncAnalysisError.Text, label.LabelName, errmes), label, 2, true, false, (exc is not EmueraException ? exc.StackTrace : null)!);
			label.ErrMes = trerror.CalledFailedFunc.Text;
#if HEADLESS
			// 无头模式下无需处理 UI 消息队列
#else
			System.Windows.Forms.Application.DoEvents();
#endif
		}
		finally
		{
			setScanLine(null!);
		}

	}

	private void setArgument(FunctionLabelLine label)
	{
		//1周目/3周
		//引数の解析とか
		LogicalLine nextLine = label;
		bool inMethod = label.IsMethod;
		while (true)
		{
			nextLine = nextLine.NextLine;
			setScanLine(nextLine);
			if (nextLine is not InstructionLine func)
			{
				if (nextLine is NullLine or FunctionLabelLine)
					break;
				continue;
			}
			if (inMethod)
			{
				if (!func.Function.IsMethodSafe())
				{
					ParserMediator.Warn(string.Format(trerror.CanNotUseInUserFunc.Text, func.Function.Name), nextLine, 2, true, false);
					continue;
				}
			}
			if (Config.Config.NeedReduceArgumentOnLoad || env.AnalysisMode || func.Function.IsForceSetArg())
				ArgumentParser.SetArgumentTo(func);
		}
	}

	private void nestCheck(FunctionLabelLine label)
	{
		//2周目/3周
		//IF-ELSEIF-ENDIF、REPEAT-RENDの対応チェックなど
		//PRINTDATA系もここでチェック
		// 决策三-a：复用 ErbLoader 实例字段，避免每函数分配 3 个容器（1000 函数 = 3000+ 次分配）
		LogicalLine nextLine = label;
		var tempLineList = _tempLineListReuse;
		var nestStack = _nestStackReuse;
		var SelectcaseStack = _selectcaseStackReuse;
		tempLineList.Clear();
		nestStack.Clear();
		SelectcaseStack.Clear();
		InstructionLine pairLine = null!;
		while (true)
		{
			nextLine = nextLine.NextLine;
			setScanLine(nextLine);
			if (nextLine is NullLine or FunctionLabelLine)
				break;
			if (nextLine is not InstructionLine)
			{
				if (nextLine is GotoLabelLine)
				{
					InstructionLine currentBaseFunc = (nestStack.Count == 0 ? null : nestStack.Peek())!;
					if (currentBaseFunc != null)
					{
						if (currentBaseFunc.FunctionCode == FunctionCode.PRINTDATA
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAL
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAW
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAD
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATADL
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATADW
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAK
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAKL
							|| currentBaseFunc.FunctionCode == FunctionCode.PRINTDATAKW
							|| currentBaseFunc.FunctionCode == FunctionCode.STRDATA
							|| currentBaseFunc.FunctionCode == FunctionCode.DATALIST
							|| currentBaseFunc.FunctionCode == FunctionCode.TRYCALLLIST
							|| currentBaseFunc.FunctionCode == FunctionCode.TRYJUMPLIST
							|| currentBaseFunc.FunctionCode == FunctionCode.TRYGOTOLIST)
						//|| (currentBaseFunc.FunctionCode == FunctionCode.SELECTCASE))
						{
							ParserMediator.Warn(string.Format(trerror.CanNotLabelDefineInSyntax.Text, currentBaseFunc.Function.Name), nextLine, 2, true, false);
						}
					}
				}
				continue;
			}
			var func = nextLine as InstructionLine;
			var baseFunc = nestStack.Count == 0 ? null : nestStack.Peek();
			if (baseFunc != null)
			{
				if (baseFunc!.Function!.IsPrintData() || baseFunc.FunctionCode == FunctionCode.STRDATA)
				{
					if (func!.FunctionCode != FunctionCode.DATA && func.FunctionCode != FunctionCode.DATAFORM && func.FunctionCode != FunctionCode.DATALIST
						&& func.FunctionCode != FunctionCode.ENDLIST && func.FunctionCode != FunctionCode.ENDDATA)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidInstructionInSyntax.Text, baseFunc.Function.Name, func.Function.Name), func, 2, true, false);
						continue;
					}
				}
				else if (baseFunc.FunctionCode == FunctionCode.DATALIST)
				{
					if (func!.FunctionCode != FunctionCode.DATA && func.FunctionCode != FunctionCode.DATAFORM && func.FunctionCode != FunctionCode.ENDLIST)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidInstructionInSyntax.Text, "DATALIST", func.Function.Name), func, 2, true, false);
						continue;
					}
				}
				else if (baseFunc.FunctionCode == FunctionCode.TRYCALLLIST || baseFunc.FunctionCode == FunctionCode.TRYJUMPLIST || baseFunc.FunctionCode == FunctionCode.TRYGOTOLIST)
				{
					if (func!.FunctionCode != FunctionCode.FUNC && func.FunctionCode != FunctionCode.ENDFUNC)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidInstructionInSyntax.Text, baseFunc.Function.Name, func.Function.Name), func, 2, true, false);
						continue;
					}
				}
				else if (baseFunc.FunctionCode == FunctionCode.SELECTCASE)
				{
					if (baseFunc.IfCaseList!.Count == 0 && func!.FunctionCode != FunctionCode.CASE && func!.FunctionCode != FunctionCode.CASEELSE && func!.FunctionCode != FunctionCode.ENDSELECT)
					{
						ParserMediator.Warn(string.Format(trerror.OutsideSelectcase.Text, func.Function.Name), func, 2, true, false);
						continue;
					}
				}
			}
			switch (func!.FunctionCode)
			{
				case FunctionCode.REPEAT:
					foreach (InstructionLine iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.REPEAT)
						{
							ParserMediator.Warn(trerror.NestedRepeat.Text, func, 1, false, false);
						}
						else if (iLine.FunctionCode == FunctionCode.FOR)
						{
							VariableTerm cnt = (iLine.Argument as SpForNextArgment)!.Cnt;
							if (cnt.Identifier.Name == "COUNT" && cnt.isAllConst && cnt.getEl1forArg == 0)
							{
								ParserMediator.Warn(string.Format(trerror.RepeatInsideFor.Text, "0"), func, 1, false, false);
							}
						}
					}
					if (func.IsError)
						break;
					nestStack.Push(func);
					break;
				case FunctionCode.IF:
					nestStack.Push(func);
					func.IfCaseList = [];
					func.IfCaseList.AddFirst(func);
					break;
				case FunctionCode.SELECTCASE:
					nestStack.Push(func);
					func.IfCaseList = [];
					SelectcaseStack.Push(func);
					break;
				case FunctionCode.FOR:
					//ネストエラーチェックのためにコストはかかるが、ここでチェックする
					if (func.Argument == null)
						ArgumentParser.SetArgumentTo(func);
					//上で引数解析がなされていることは保証されているので、
					//それでこれがfalseになるのは、引数解析でエラーが起きた場合のみ
					if (func.Argument != null)
					{
						VariableTerm Cnt = (func.Argument as SpForNextArgment)!.Cnt;
						if (Cnt.Identifier.Name == "COUNT")
						{
							foreach (InstructionLine iLine in nestStack)
							{
								if (iLine.FunctionCode == FunctionCode.REPEAT && Cnt.isAllConst && Cnt.getEl1forArg == 0)
								{
									ParserMediator.Warn(string.Format(trerror.RepeatInsideFor.Text, "0"), func, 1, false, false);
								}
								else if (iLine.FunctionCode == FunctionCode.FOR)
								{
									VariableTerm destCnt = (iLine.Argument as SpForNextArgment)!.Cnt;
									if (destCnt.Identifier.Name == "COUNT" && Cnt.isAllConst && destCnt.isAllConst && destCnt.getEl1forArg == Cnt.getEl1forArg)
									{
										ParserMediator.Warn(string.Format(trerror.RepeatInsideFor.Text, Cnt.getEl1forArg.ToString()), func, 1, false, false);
									}
								}
							}
						}
					}
					if (func.IsError)
						break;
					nestStack.Push(func);
					break;
				case FunctionCode.WHILE:
				case FunctionCode.TRYCGOTO:
				case FunctionCode.TRYCJUMP:
				case FunctionCode.TRYCCALL:
				case FunctionCode.TRYCGOTOFORM:
				case FunctionCode.TRYCJUMPFORM:
				case FunctionCode.TRYCCALLFORM:
				case FunctionCode.DO:
					nestStack.Push(func);
					break;
				case FunctionCode.BREAK:
				case FunctionCode.CONTINUE:
					// 决策三-b：直接遍历 Stack（LIFO，从栈顶到栈底，与 [.. nestStack] 顺序一致），消除 InstructionLine[] 分配
					pairLine = null!;
					foreach (var iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.REPEAT
							|| iLine.FunctionCode == FunctionCode.FOR
							|| iLine.FunctionCode == FunctionCode.WHILE
							|| iLine.FunctionCode == FunctionCode.DO)
						{ pairLine = iLine; break; }
					}
					if (pairLine == null)
					{
						ParserMediator.Warn(string.Format(trerror.InvalidLoopInstruction.Text, func.Function.Name), func, 2, true, false);
						break;
					}
					func.JumpTo = pairLine;
					break;

				case FunctionCode.ELSEIF:
				case FunctionCode.ELSE:
					{
						//1.725 Stack<T>.Last.Value()はStackが空の時はnullを返す仕様だと思いこんでおりました。
						InstructionLine? ifLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (ifLine == null || ifLine.FunctionCode != FunctionCode.IF)
						{
							ParserMediator.Warn(string.Format(trerror.InvalidElse.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						if (ifLine!.IfCaseList!.Last!.Value!.FunctionCode == FunctionCode.ELSE)
							ParserMediator.Warn(string.Format(trerror.InvalidElseAfterElse.Text, func!.Function.Name), func, 1, false, false);
						ifLine.IfCaseList.AddLast(func);
					}
					break;
				case FunctionCode.ENDIF:
					{
						InstructionLine? ifLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (ifLine == null || ifLine.FunctionCode != FunctionCode.IF)
						{
							ParserMediator.Warn(trerror.UnexpectedEndif.Text, func, 2, true, false);
							break;
						}
						foreach (var ifelseifLine in ifLine.IfCaseList!)
						{
							ifelseifLine.JumpTo = func;
						}
						nestStack.Pop();
					}
					break;
				case FunctionCode.CASE:
				case FunctionCode.CASEELSE:
					{
						InstructionLine? selectLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (selectLine == null || selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count == 0)
						{
							ParserMediator.Warn(string.Format(trerror.OutsideSelectcase.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						else if (selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count > 0)
						{
							do
							{
								ParserMediator.Warn(string.Format(trerror.InstructionNotClosed.Text, selectLine.Function.Name, FunctionIdentifier.getMatchFunction(selectLine.FunctionCode), func.Function.Name), func, 2, true, false);
								//これを跨いでIF等が閉じられることがないようにする。
								nestStack.Pop();
								//if (nestStack.Count > 0)　//空になってるかは下で判定できるので、これを見る必要がない
								selectLine = nestStack.Count == 0 ? null : nestStack.Peek(); //ちなみにnullになることはない（SELECTCASEがない場合は上で弾けるから）
							} while (selectLine != null && selectLine.FunctionCode != FunctionCode.SELECTCASE);
							break;
						}
						if (selectLine.IfCaseList.Count > 0 && selectLine.IfCaseList.Last!.Value.FunctionCode == FunctionCode.CASEELSE)
							ParserMediator.Warn(string.Format(trerror.InvalidCaseAfterCaseelse.Text, func.Function.Name), func, 1, false, false);
						selectLine.IfCaseList.AddLast(func);
					}
					break;
				case FunctionCode.ENDSELECT:
					{
						InstructionLine? selectLine = nestStack.Count == 0 ? null : nestStack.Peek()!;
						if (selectLine == null || selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count == 0)
						{
							ParserMediator.Warn(trerror.UnexpectedEndselect.Text, func, 2, true, false);
							break;
						}
						else if (selectLine.FunctionCode != FunctionCode.SELECTCASE && SelectcaseStack.Count > 0)
						{
							do
							{
								ParserMediator.Warn(string.Format(trerror.InstructionNotClosed.Text, selectLine.Function.Name, FunctionIdentifier.getMatchFunction(selectLine.FunctionCode), func.Function.Name), func, 2, true, false);
								//これを跨いでIF等が閉じられることがないようにする。
								nestStack.Pop();
								//if (nestStack.Count > 0)　//空になってるかは下で判定できるので、これを見る必要がない
								selectLine = nestStack.Count == 0 ? null : nestStack.Peek(); //ちなみにnullになることはない（SELECTCASEがない場合は上で弾けるから）
							} while (selectLine != null && selectLine.FunctionCode != FunctionCode.SELECTCASE);
							//とりあえず、対応するSELECTCASE跨ぎは閉じる
							SelectcaseStack.Pop();
							//こっちでも抜かないとSELECTCASEが2つのENDSELECTに対応してしまう
							nestStack.Pop();
							break;
						}
						nestStack.Pop();
						SelectcaseStack.Pop();
						selectLine.JumpTo = func;
						if (selectLine.IsError)
							break;
						var term = (selectLine.Argument as ExpressionArgument)!.Term;
						if (term == null)
						{
							ParserMediator.Warn(trerror.MissingArg.Text, selectLine, 2, true, false);
							break;
						}
						foreach (var caseLine in selectLine.IfCaseList)
						{
							caseLine.JumpTo = func;
							if (caseLine.IsError)
								continue;
							if (caseLine.FunctionCode == FunctionCode.CASEELSE)
								continue;
							var caseExps = (caseLine.Argument as CaseArgument)!.CaseExps;
							if (caseExps.Length == 0)
								ParserMediator.Warn(trerror.MissingArg.Text, caseLine, 2, true, false);

							foreach (var exp in caseExps)
							{
								if (exp.GetOperandType() != term.GetOperandType())
									ParserMediator.Warn(trerror.NotMatchCaseTypeAndSelectcaseType.Text, caseLine, 2, true, false);
							}

						}
					}
					break;
				case FunctionCode.REND:
				case FunctionCode.NEXT:
				case FunctionCode.WEND:
				case FunctionCode.LOOP:
					FunctionCode parentFunc = FunctionIdentifier.getParentFunc(func.FunctionCode);
					//if (parentFunc == FunctionCode.__NULL__)
					//    throw new ExeEE("何か変？");
					if (nestStack.Count == 0
						|| nestStack.Peek().FunctionCode != parentFunc)
					{
						ParserMediator.Warn(string.Format(trerror.MissingCorresponding.Text, parentFunc.ToString(), func.Function.Name), func, 2, true, false);
						break;
					}
					pairLine = nestStack.Pop();//REPEAT
					func.JumpTo = pairLine;
					pairLine.JumpTo = func;
					break;
				case FunctionCode.CATCH:
					pairLine = (nestStack.Count == 0 ? null : nestStack.Peek())!;
					if (pairLine == null
						|| pairLine.FunctionCode != FunctionCode.TRYCGOTO
						&& pairLine.FunctionCode != FunctionCode.TRYCCALL
						&& pairLine.FunctionCode != FunctionCode.TRYCJUMP
						&& pairLine.FunctionCode != FunctionCode.TRYCGOTOFORM
						&& pairLine.FunctionCode != FunctionCode.TRYCCALLFORM
						&& pairLine.FunctionCode != FunctionCode.TRYCJUMPFORM)
					{
						ParserMediator.Warn(trerror.MissingTryc.Text, func, 2, true, false);
						break;
					}
					pairLine = nestStack.Pop();//TRYC
					pairLine.JumpToEndCatch = func;//TRYCにCATCHの位置を教える
					nestStack.Push(func);
					break;
				case FunctionCode.ENDCATCH:
					if (nestStack.Count == 0
						|| nestStack.Peek().FunctionCode != FunctionCode.CATCH)
					{
						ParserMediator.Warn(trerror.UnexpectedEndcatch.Text, func, 2, true, false);
						break;
					}
					pairLine = nestStack.Pop();//CATCH
					pairLine.JumpToEndCatch = func;//CATCHにENDCATCHの位置を教える
					break;
				case FunctionCode.PRINTDATA:
				case FunctionCode.PRINTDATAL:
				case FunctionCode.PRINTDATAW:
				case FunctionCode.PRINTDATAD:
				case FunctionCode.PRINTDATADL:
				case FunctionCode.PRINTDATADW:
				case FunctionCode.PRINTDATAK:
				case FunctionCode.PRINTDATAKL:
				case FunctionCode.PRINTDATAKW:
					{
						foreach (var iLine in nestStack)
						{
							if (iLine.Function.IsPrintData())
							{
								ParserMediator.Warn(trerror.NestedPrintdata.Text, func, 2, true, false);
								break;
							}
							if (iLine.FunctionCode == FunctionCode.STRDATA)
							{
								ParserMediator.Warn(trerror.StrdataInsidePrintdata.Text, func, 2, true, false);
								break;
							}
						}
						if (func.IsError)
							break;
						func.dataList = [];
						nestStack.Push(func);
						break;
					}
				case FunctionCode.STRDATA:
					{
						foreach (var iLine in nestStack)
						{
							if (iLine.FunctionCode == FunctionCode.STRDATA)
							{
								ParserMediator.Warn(trerror.NestedStrdata.Text, func, 2, true, false);
								break;
							}
							if (iLine.Function.IsPrintData())
							{
								ParserMediator.Warn(trerror.PrintdataInsideStrdata.Text, func, 2, true, false);
								break;
							}
						}
						if (func.IsError)
							break;
						func.dataList = [];
						nestStack.Push(func);
						break;
					}
				case FunctionCode.DATALIST:
					{
						var pline = nestStack.Count == 0 ? null : nestStack.Peek();
						if (pline == null || !pline.Function.IsPrintData() && pline.FunctionCode != FunctionCode.STRDATA)
						{
							ParserMediator.Warn(trerror.UnexpectedDatalist.Text, func, 2, true, false);
							break;
						}
						tempLineList = [];
						nestStack.Push(func);

						break;
					}
				case FunctionCode.ENDLIST:
					{
						if (nestStack.Count == 0 || nestStack.Peek().FunctionCode != FunctionCode.DATALIST)
						{
							ParserMediator.Warn(trerror.UnexpectedEndlist.Text, func, 2, true, false);
							break;
						}
						if (tempLineList.Count == 0)
							ParserMediator.Warn(trerror.DatalistDataIsMissing.Text, func, 1, false, false);
						nestStack.Pop();
						nestStack.Peek().dataList.Add(tempLineList);
						break;
					}
				case FunctionCode.DATA:
				case FunctionCode.DATAFORM:
					{
						InstructionLine pdata = (nestStack.Count == 0 ? null : nestStack.Peek())!;
						if (pdata == null || !pdata.Function.IsPrintData() && pdata.FunctionCode != FunctionCode.DATALIST && pdata.FunctionCode != FunctionCode.STRDATA)
						{
							ParserMediator.Warn(string.Format(trerror.MissingPrintdata.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						List<InstructionLine> iList = [];
						if (pdata.FunctionCode != FunctionCode.DATALIST)
						{
							iList.Add(func);
							pdata.dataList.Add(iList);
						}
						else
							tempLineList.Add(func);
						break;
					}
				case FunctionCode.ENDDATA:
					{
						InstructionLine pline = (nestStack.Count == 0 ? null : nestStack.Peek())!;
						if (pline == null || !pline.Function.IsPrintData() && pline.FunctionCode != FunctionCode.STRDATA)
						{
							ParserMediator.Warn(string.Format(trerror.MissingPrintdataStrdata.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						if (pline.FunctionCode == FunctionCode.DATALIST)
							ParserMediator.Warn(trerror.DatalistNotClosed.Text, func, 2, true, false);
						if (pline.dataList.Count == 0)
							ParserMediator.Warn(string.Format(trerror.InstructionDataIsMissing.Text, pline.Function.Name), func, 1, false, false);
						pline.JumpTo = func;
						nestStack.Pop();
						break;
					}
				case FunctionCode.TRYCALLLIST:
				case FunctionCode.TRYJUMPLIST:
				case FunctionCode.TRYGOTOLIST:
					foreach (InstructionLine iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.TRYCALLLIST || iLine.FunctionCode == FunctionCode.TRYJUMPLIST || iLine.FunctionCode == FunctionCode.TRYGOTOLIST)
						{
							ParserMediator.Warn(trerror.NestedTrycalllist.Text, func, 2, true, false);
							break;
						}
					}
					if (func.IsError)
						break;
					func.callList = [];
					nestStack.Push(func);
					break;
				case FunctionCode.FUNC:
					{
						InstructionLine pFunc = (nestStack.Count == 0 ? null : nestStack.Peek())!;
						if (pFunc == null ||
							pFunc.FunctionCode != FunctionCode.TRYCALLLIST && pFunc.FunctionCode != FunctionCode.TRYJUMPLIST && pFunc.FunctionCode != FunctionCode.TRYGOTOLIST)
						{
							ParserMediator.Warn(string.Format(trerror.MissingTrycalllist.Text, func.Function.Name), func, 2, true, false);
							break;
						}
						if (func.Argument == null)
						{
							ParserMediator.Warn(string.Format(trerror.InvalidInstructionInTrycalllist.Text, func.Function.Name), pFunc, 2, true, false);
							break;
						}
						if (pFunc.FunctionCode == FunctionCode.TRYGOTOLIST)
						{
							var spCallArg = func.Argument as SpCallArgment;
							if (spCallArg!.SubNames.Count != 0)
							{
								ParserMediator.Warn(trerror.TrygotolistToSBrackets.Text, func, 2, true, false);
								break;
							}
							if (spCallArg.RowArgs.Count != 0)
							{
								ParserMediator.Warn(trerror.TrygotolistTargetHasArg.Text, func, 2, true, false);
								break;
							}
						}
						pFunc.callList.Add(func);
						break;
					}
				case FunctionCode.ENDFUNC:
					var pf = nestStack.Count == 0 ? null : nestStack.Peek();
					if (pf == null ||
						pf.FunctionCode != FunctionCode.TRYCALLLIST && pf.FunctionCode != FunctionCode.TRYJUMPLIST && pf.FunctionCode != FunctionCode.TRYGOTOLIST)
					{
						ParserMediator.Warn(string.Format(trerror.MissingTrycalllist.Text, func.Function.Name), func, 2, true, false);
						break;
					}
					pf.JumpTo = func;
					nestStack.Pop();
					break;
				case FunctionCode.NOSKIP:
					foreach (var iLine in nestStack)
					{
						if (iLine.FunctionCode == FunctionCode.NOSKIP)
						{
							ParserMediator.Warn(trerror.NestedNoskip.Text, func, 2, true, false);
							break;
						}
					}
					if (func.IsError)
						break;
					nestStack.Push(func);
					break;
				case FunctionCode.ENDNOSKIP:
					var pfunc = nestStack.Count == 0 ? null : nestStack.Peek();
					if (pfunc == null ||
						pfunc.FunctionCode != FunctionCode.NOSKIP)
					{
						ParserMediator.Warn(string.Format(trerror.MissingNoskip.Text, func.Function.Name), func, 2, true, false);
						break;
					}
					//エラーハンドリング用
					pfunc.JumpTo = func;
					func.JumpTo = pfunc;
					nestStack.Pop();
					break;
			}

		}

		while (nestStack.Count != 0)
		{
			var func = nestStack.Pop();
			string funcName = func.Function.Name;
			string funcMatch = FunctionIdentifier.getMatchFunction(func.FunctionCode);
			if (func != null)
				ParserMediator.Warn(string.Format(trerror.MissingCorresponding.Text, funcMatch, funcName), func, 2, true, false);
			else
				ParserMediator.Warn(trerror.DefaultError.Text, func!, 2, true, false);
		}
		//使ったスタックをクリア
		SelectcaseStack.Clear();
	}

	private void setJumpTo(FunctionLabelLine label)
	{
		//3周目/3周
		//フロー制御命令のジャンプ先を設定
		LogicalLine nextLine = label;
		int depth = label.Depth;
		if (depth < 0)
			depth = -2;
		while (true)
		{
			nextLine = nextLine.NextLine;
			if (!(nextLine is InstructionLine func))
			{
				if (nextLine is NullLine || nextLine is FunctionLabelLine)
					break;
				continue;
			}
			if (func.IsError)
				continue;
			setScanLine(func);

			if (func.Function.Instruction != null)
			{
				string FunctionNotFoundName = null!;
				try
				{
					func.Function.Instruction.SetJumpTo(ref useCallForm, func, depth, ref FunctionNotFoundName);
				}
				catch (CodeEE e)
				{
					ParserMediator.Warn(e.Message, func, 2, true, false);
					continue;
				}
				if (FunctionNotFoundName != null)
				{
					if (!env.AnalysisMode)
						printFunctionNotFoundWarning(string.Format(trerror.NotDefinedFunc.Text, FunctionNotFoundName), func, 2, true);
					else
						printFunctionNotFoundWarning(FunctionNotFoundName, func, 2, true);
				}
				continue;
			}
			if (func.FunctionCode == FunctionCode.TRYCALLLIST || func.FunctionCode == FunctionCode.TRYJUMPLIST)
				useCallForm = true;
		}
	}

	// erb-loader-parallel 03：原 GetErbFilesFromCache 孪生实现收敛至 Preload.GetFilesFromCache（扩展名 + 日志标签参数化）。
}
