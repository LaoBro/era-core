using MinorShift.Emuera.Files;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameProc;

internal sealed partial class Process(EmueraConsole view)
{
	public LogicalLine getCurrentLine { get { return state.CurrentLine; } }

	public bool SkipPrint { get { return executionState.skipPrint; } set { executionState.skipPrint = value; } }

	/// <summary>
	/// @~~と$~~を集めたもの。CALL命令などで使う
	/// 実行順序はLogicalLine自身が保持する。
	/// </summary>
	LabelDictionary labelDic = null!;
	public LabelDictionary LabelDictionary { get { return labelDic; } }

	/// <summary>lazy-function-load（B 线 B1）：主加载完成后保留的 loader，供运行时物化接缝复用（物理路径映射 / env / scanLine）。</summary>
	internal ErbLoader? RuntimeLoader { get; set; }

	/// <summary>
	/// 会话级方言 profile（实现票 16）：自 scope 捕获一次，供 <see cref="IdentifierDictionary"/>
	/// 构造期投影可见面。会话内不变。
	///
	/// 刻意**不在 <see cref="Initialize"/> 前置段**读取——那里在 <c>try</c> 保护圈之外，
	/// 而注册表构建失败必须走既有 <c>CodeEE(DoNotInstallWMP)</c> 守卫（原始故障模式见
	/// <c>IdentifierDictionary</c> 构造器的同款 try/catch）。捕获点落在 composition root
	/// <see cref="Loader.LoadEngineData"/> 内、<c>IdentifierDictionary</c> 构造之前。
	/// </summary>
	internal DialectProfile DialectProfile { get; private set; } = null!;

	/// <summary>
	/// 変数全部。スクリプト中で必要になる変数は（ユーザーが直接触れないものも含め）この中にいれる
	/// </summary>
	private VariableEvaluator vEvaluator = null!;
	public VariableEvaluator VEvaluator { get { return vEvaluator; } }
	private ExpressionMediator exm = null!;
	// ADR-0011：引擎状态收归 Process 实例，GlobalStatic 对应属性转为只读转发层。
	public ExpressionMediator EMediator { get { return exm; } }
	private GameBase gamebase = null!;
	public GameBase gameBase { get { return gamebase; } }
	// ADR-0011：6 引擎字段之一，实例持有，不再写回 GlobalStatic。
	public GameBase GameBaseData { get { return gamebase; } }
	private ConstantData constantData = null!;
	// ADR-0011：6 引擎字段之一，实例持有，不再写回 GlobalStatic。
	public ConstantData ConstantData { get { return constantData; } }
	readonly EmueraConsole console = view;
	private IdentifierDictionary idDic = null!;
	// ADR-0011：6 引擎字段之一，实例持有，不再写回 GlobalStatic。
	public IdentifierDictionary IdentifierDictionary { get { return idDic; } }
	LoaderEnv _loaderEnv = null!;
	// ADR-0011 回归修复：state 不再是独立字段，而是 executionState.CurrentState 的别名。
	// 上游（emuera.em）在 saveCurrentState 里直接 state = state.Clone()（同一引用），本仓库曾用独立字段，
	// 导致 SAVEGAME/LOADGAME 后 DoScript（用本字段）与 ScriptProc/SystemProc（用 CurrentState）读到不同
	// ProcessState，LOADGAME 后无法转入存档界面而陷入死循环。
	ProcessState state => (ProcessState)executionState.CurrentState;
	ProcessState originalState = null!;//リセットする時のために
	internal ExecutionState executionState = null!;
	internal SystemProc _systemProc = null!;
	internal ScriptProc _scriptProc = null!;
	private string[] TrainName = null!;
	//色々あって復活させてみる
	bool initialiing;
	public bool inInitializeing { get { return initialiing; } }

	public async Task<bool> Initialize(LoaderEnv env, StreamWriter? logWriter)
	{
		var stopWatch = new Stopwatch();
		stopWatch.Start();
		LexicalAnalyzer.UseMacro = false;
		originalState = new ProcessState(ProcessTrace.For(console), () => MethodStack() == 0);
		executionState = new ExecutionState();
		executionState.CurrentState = originalState;
		initialiing = true;
		_loaderEnv = env;
		try
		{
			logWriter?.WriteLine($"Proc:Init:Start {stopWatch.ElapsedMilliseconds}ms");
			logWriter?.WriteLine($"Proc:Init:Parser:Start {stopWatch.ElapsedMilliseconds}ms");
			ParserMediator.Initialize(console);
			//コンフィグファイルに関するエラーの処理（コンフィグファイルはこの関数に入る前に読込済み）
			if (ParserMediator.HasWarning)
			{
				ParserMediator.FlushWarningList();
				if (Dialog.ShowPrompt(trmb.ConfigFileError.Text, trmb.ConfigError.Text))
				{
					console.PrintSystemLine(trsl.SelectExitConfigMB.Text);
					EmueraLog.Error("Initialize", "soft-return reason=ConfigWarningExit");
					return false;
				}
			}
			logWriter?.WriteLine($"Proc:Init:Parser:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:Image:Start {stopWatch.ElapsedMilliseconds}ms");
			//リソースフォルダ読み込み
			EmueraLog.Info("Initialize", "stage=Image start");
			var err = await Task.Run(() => AppContents.LoadContents(false));
			if (err != null)
			{
				ParserMediator.FlushWarningList();
				console.PrintSystemLine(trsl.ResourceReadError.Text);
				console.Print(err.ToString());
				EmueraLog.Error("Initialize", $"soft-return reason=ResourceLoadFail err={err}");
				return false;
			}
			ParserMediator.FlushWarningList();
			EmueraLog.Info("Initialize", "stage=Image ok");
			logWriter?.WriteLine($"Proc:Init:Image:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:KeyMacro:Start {stopWatch.ElapsedMilliseconds}ms");
			//キーマクロ読み込み
			#region eee_カレントディレクトリー
			if (Config.UseKeyMacro && !env.AnalysisMode)
			{
				//if (File.Exists(Program.ExeDir + "macro.txt"))
				if (SafCompat.FileExists(env.DirAccessor.CombinePath(Program.ExeDir, "macro.txt")))
				{
					if (Config.DisplayReport)
						console.PrintSystemLine(trsl.LoadingMacro.Text);
					//KeyMacro.LoadMacroFile(Program.ExeDir + "macro.txt");
					KeyMacro.LoadMacroFile(env.DirAccessor.CombinePath(Program.ExeDir, "macro.txt"));
				}
			}
			#endregion
			logWriter?.WriteLine($"Proc:Init:KeyMacro:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:Replace:Start {stopWatch.ElapsedMilliseconds}ms");
			//_replace.csv読み込み
			if (Config.UseReplaceFile && !env.AnalysisMode)
			{
				if (SafCompat.FileExists(env.DirAccessor.CombinePath(env.CsvDir, "_Replace.csv")))
				{
					if (Config.DisplayReport)
						console.PrintSystemLine(trsl.LoadingReplace.Text);
					ConfigData.Current!.LoadReplaceFile(env.DirAccessor.CombinePath(env.CsvDir, "_Replace.csv"));
					if (ParserMediator.HasWarning)
					{
						ParserMediator.FlushWarningList();
						if (Dialog.ShowPrompt(trmb.ReplaceFileError.Text, trmb.ReplaceError.Text))
						{
							console.PrintSystemLine(trsl.SelectExitReplaceMB.Text);
							EmueraLog.Error("Initialize", "soft-return reason=ReplaceWarningExit");
							return false;
						}
					}
				}
			}
			logWriter?.WriteLine($"Proc:Init:Replace:End {stopWatch.ElapsedMilliseconds}ms");

		// 候选 2 / ADR-0009：原 Config.SetReplace(ConfigData.Instance) 已删除——
		// MoneyLabel/DrawLineString 等 replace 值现在由 Config 薄视图直接从 _data 转发（经合并索引），
		// 无需再拷入 Config 静态镜像。
		//ここでBARを設定すれば、いいことに気づいた予感
		console.setStBar(Config.DrawLineString);

			logWriter?.WriteLine($"Proc:Init:Rename:Load:Start {stopWatch.ElapsedMilliseconds}ms");
			//_rename.csv読み込み
			if (Config.UseRenameFile)
			{
				if (SafCompat.FileExists(env.DirAccessor.CombinePath(env.CsvDir, "_Rename.csv")))
				{
					if (Config.DisplayReport || env.AnalysisMode)
						console.PrintSystemLine(trsl.LoadingRename.Text);
					ParserMediator.LoadEraExRenameFile(env.DirAccessor.CombinePath(env.CsvDir, "_Rename.csv"));
				}
				else
					console.PrintError(trsl.MissingRename.Text);
			}
			logWriter?.WriteLine($"Proc:Init:Rename:Load:End {stopWatch.ElapsedMilliseconds}ms");

			// issue 09：IR 命中轮跳过内容 Preload——命中判定前移至此（RenameDic 已载，指纹与建库轮同源）。
			// 命中 → 只留文件清单枚举（LoadErbDir 内 Config.GetFiles 兜底重建），物化走 blob、
			// 源回退/CSV/ERH/ERD 走懒读（缓存空直读）；未命中 / IR 关闭 → 仍走完整 Preload
			//（建库轮 / IR 关闭轮行为不变）。time.log 的 File:Preload 标记随本段移动：
			// 命中轮 Start→End 间隔即预判定耗时（近 0），非命中轮含完整内容读取。
			logWriter?.WriteLine("File:Preload:Start");
			if (ErbIrLoadPipeline.IsIrHitRound(env))
			{
				// issues/10 10-B：CSV/ERH 内容缓存重水化（era_src.dat 清单校验 → 轮内快照，
				// EraStreamReader.OpenOnCache 咽喉消费）。必须先于 LoadBaseCsv（GAMEBASE/固定名 CSV）；
				// 校验的逐目录查询顺带预热 O1 子项缓存，IndexTree 的 CSV 树枚举由冷变热。
				SrcContentCache.TryRehydrate(env);
				// 命中轮：跳过内容读取，只枚举文件清单建路径索引（运行期报错回显按需定位源文件用）。
				Preload.IndexTree(env.ErbDir, env.DirAccessor);
				Preload.IndexTree(env.CsvDir, env.DirAccessor);
			}
			else
			{
				await Preload.LoadTreesAsync(env.ErbDir, env.CsvDir, env.DirAccessor);
				// issues/10 10-B：建库轮收尾写 CSV/ERH 内容缓存（行来自 Preload.files、元数据来自
				// 已暖的子项缓存 → 零额外 IPC；门控在方法内：旋钮 304 + SAF + IR 启用）。
				SrcContentCache.TryWriteAfterPreload(env);
			}
			logWriter?.WriteLine("File:Preload:End " + stopWatch.ElapsedMilliseconds + "ms");

			if (!Config.DisplayReport)
			{
				console.PrintSingleLine(Config.LoadLabel);
				console.RefreshStrings(true);
			}
	// ADR-0011 决策二：引擎字段在 Initialize（composition root）中创建，经方法参数注入 Loader。
	// Loader 仅处理文件 I/O，不设 Process 反向引用（F2）。
	// ADR-0012：LoaderEnv 收口 Program.* 进程级 static，Loader 不再直读 Program。
	this.gamebase = new GameBase();
	this.constantData = new ConstantData();
	this.labelDic = new LabelDictionary();

	var loader = new Loader(console);
	EmueraLog.Info("Initialize", "stage=LoadEngineData start");
	if (!await loader.LoadEngineData(this, env, logWriter, stopWatch))
	{
		EmueraLog.Error("Initialize", "soft-return reason=LoadEngineDataFalse");
		return false;
	}
	EmueraLog.Info("Initialize", "stage=LoadEngineData ok");
	logWriter?.WriteLine($"Proc:Init:ERB:End {stopWatch.ElapsedMilliseconds}ms");

		_systemProc.Init();
		initialiing = false;

			logWriter?.WriteLine($"Proc:Init:End {stopWatch.ElapsedMilliseconds}ms");
		}
		catch (Exception e)
		{
			// SAF diag: print type/message/stack so csm returned-false is not the only signal
			var msg = $"[Initialize] EXCEPTION: {e.GetType().Name}: {e.Message}\n{e.StackTrace}";
			EmueraLog.Error("Initialize", msg);
			handleException(e, null!, true);
			console.PrintSystemLine(trsl.ErhLoadingError.Text);
			return false;
		}
		if (labelDic == null)
		{
			EmueraLog.Error("Initialize", "soft-return reason=LabelDicNull");
			return false;
		}
		state.Begin(BeginType.TITLE);
		return true;
	}

	public async Task ReloadErb()
	{
		// erb-loader-parallel 03：两棵树并行预载收敛为单一入口（02 引入 WhenAll，03 收敛调用方）
		await Preload.LoadTreesAsync(_loaderEnv.ErbDir, _loaderEnv.CsvDir, _loaderEnv.DirAccessor);
		saveCurrentState(false);
		state.SystemState = SystemStateCode.System_Reloaderb;
		// ADR-0012：ErbLoader F2 构造注入，不再传 Process。复用 _loaderEnv。
		ErbLoader loader = new(console, exm, idDic, _loaderEnv, line => scaningLine = line);
		await loader.LoadErbDir(_loaderEnv.ErbDir, false, labelDic);
		console.ReadAnyKey();
	}

	public async Task ReloadPartialErb(List<string> paths)
	{
		saveCurrentState(false);
		state.SystemState = SystemStateCode.System_Reloaderb;
		await Preload.Load(paths, _loaderEnv.DirAccessor);
		var loader = new ErbLoader(console, exm, idDic, _loaderEnv, line => scaningLine = line);
		await loader.LoadErbList(paths, labelDic);
		console.ReadAnyKey();
	}

	#region EE_INPUTMOUSEKEYのボタン対応
	// public void InputResult5(int r0, int r1, int r2, int r3, int r4)
	public void InputResult5(int r0, int r1, int r2, int r3, int r4, long r5)
	{
		long[] result = vEvaluator.RESULT_ARRAY;
		result[0] = r0;
		result[1] = r1;
		result[2] = r2;
		result[3] = r3;
		result[4] = r4;
		result[5] = r5;
	}
	#endregion
	public void InputInteger(long i)
	{
		GlobalStatic.ctrlZ.Add(i.ToString());
		vEvaluator.RESULT = i;
	}
	#region EM_私家版_INPUT系機能拡張
	public void InputInteger(long idx, long i)
	{
		if (idx < vEvaluator.RESULT_ARRAY.Length)
			vEvaluator.RESULT_ARRAY[idx] = i;
	}
	public void InputString(long idx, string i)
	{
		if (idx < vEvaluator.RESULT_ARRAY.Length)
			vEvaluator.RESULTS_ARRAY[idx] = i;
	}
	#endregion
	public void InputSystemInteger(long i)
	{
		GlobalStatic.ctrlZ.Add(i.ToString());
		executionState.systemResult = i;
	}
	public void InputString(string s)
	{
		GlobalStatic.ctrlZ.Add(s);
		vEvaluator.RESULTS = s;
	}

	public void DoScript()
	{
		bool systemProcRunning = true;
		try
		{
			while (true)
			{
				methodStack = 0;
				systemProcRunning = true;
				while (state.ScriptEnd && console.IsRunning)
					_systemProc.Run();
				if (!console.IsRunning)
					break;
				systemProcRunning = false;
				_scriptProc.Run();
			}
		}
		catch (Exception ec)
		{
#if HEADLESS
			// I-11：HeadlessConsole.Close()/ExitApplication() 抛 GameExitException 绕过
			// Environment.Exit(0)。此处必须 rethrow 让异常穿透到 CoreSession/HeadlessRunner
			// 的 catch，否则 GameExitException 被当脚本错误处理 → Error 状态 → 僵尸 session。
			if (ec is GameExitException)
				throw;
#endif
			// ERB THROW / 除零等脚本级异常（CodeEE）在此处被 handleException 捕获并处理，
			// 不会传播到协议层（AgentJsonlProtocol.StepAsync / AgentCliProtocol.RunCliLoop）
			// 的 catch 块。StepAsync catch 是防御性兜底，仅捕获 Process 未预料的 C# 异常。
			LogicalLine currentLine = state.ErrorLine;
			if (currentLine != null && currentLine is NullLine)
				currentLine = null!;
			if (systemProcRunning)
				handleExceptionInSystemProc(ec, currentLine!, true);
			else
				handleException(ec, currentLine!, true);
		}
	}

	public void BeginTitle()
	{
		vEvaluator.ResetData();
		executionState.CurrentState = originalState;
		state.Begin(BeginType.TITLE);
	}

	public void saveCurrentState(bool single) => executionState.SaveCurrentState(single);

	public ProcessState getCurrentState => (ProcessState)executionState.CurrentState;

	public void DoDebugNormalFunction(InstructionLine func, bool munchkin) => _scriptProc.DoDebugNormalFunction(func, munchkin);

	public void LoadSilent() => _systemProc.LoadSilent();

	int methodStack;
	public SingleTerm GetValue(SuperUserDefinedMethodTerm udmt)
	{
		// erb-bounded-lifecycle（IR 命中 NRE 修复，真机四开关全开错误屏）：式中方法命中惰性桩时
		// 先物化再进入——CallFunction（CALL 语句）在 CalledFunction.cs 有同款接缝，而 IR 命中桩
		// 不经 Depth-BFS 主循环物化（TryGenerateFromIr 不恢复 #FUNCTION 的 Depth=0，桩 Depth=-1
		// 永不匹配层），无此接缝会在 IntoFunction 后读到空行链（CurrentLine=null → ScriptProc.Run:75 NRE）。
		// 幂等（非桩恒 no-op）；物化失败降级为占位报错（IsError），Run() 转 CodeEE 而非 NRE。
		if (udmt.Call.TopLabel.IsStub)
			ErbLoader.MaterializeOnRuntime(udmt.Call.TopLabel);
		methodStack++;
		if (methodStack > 100)
		{
			//StackOverflowExceptionはcatchできない上に再現性がないので発生前に一定数で打ち切る。
			//環境によっては100以前にStackOverflowExceptionがでるかも？
			throw new CodeEE(trerror.OverflowFuncStack.Text);
		}
		SingleTerm ret = null!;
		int temp_current = state.currentMin;
		state.currentMin = state.functionCount;
		udmt.Call.updateRetAddress(state.CurrentLine);
		try
		{
			state.IntoFunction(udmt.Call, udmt.Argument, exm);
			//do whileの中でthrow されたエラーはここではキャッチされない。
			//#functionを全て抜けてDoScriptでキャッチされる。
			_scriptProc.Run();
			ret = state.MethodReturnValue;
		}
		finally
		{
			if (udmt.Call.TopLabel.hasPrivDynamicVar)
				udmt.Call.TopLabel.ScopeOut();
			//1756beta2+v3:こいつらはここにないとデバッグコンソールで式中関数が事故った時に大事故になる
			state.currentMin = temp_current;
			methodStack--;
		}
		return ret;
	}

	public void clearMethodStack()
	{
		methodStack = 0;
	}

	public int MethodStack()
	{
		return methodStack;
	}

	public ScriptPosition? GetRunningPosition()
	{
		LogicalLine line = state.ErrorLine;
		if (line == null)
			return null!;
		return line.Position;
	}
	/*
			private readonly string scaningScope = null;
			private string GetScaningScope()
			{
				if (scaningScope != null)
					return scaningScope;
				return state.Scope;
			}
	*/
	public LogicalLine scaningLine = null!;
	internal LogicalLine GetScaningLine()
	{
		if (scaningLine != null)
			return scaningLine;
		LogicalLine line = state.ErrorLine;
		if (line == null)
			return default!;
		return line;
	}


	private void handleExceptionInSystemProc(Exception exc, LogicalLine current, bool playSound)
	{
		// T-027 Phase 4 双写：画面保留玩家可见错误（UX），门面补 Error 级诊断（类型+堆栈）供排障。
		EmueraLog.Error("script", $"系统流程异常: {exc.GetType().Name}: {exc.Message}\n{exc.StackTrace}");
		console.ThrowError(playSound);
		if (exc is CodeEE)
		{
			console.PrintError(string.Format(trerror.FuncEndError.Text, AssemblyData.EmueraVersionText));
			console.PrintError(exc.Message);
		}
		else if (exc is ExeEE)
		{
			console.PrintError(string.Format(trerror.FuncEndEmueraError.Text, AssemblyData.EmueraVersionText));
			console.PrintError(exc.Message);
		}
		else
		{
			console.PrintError(string.Format(trerror.FuncEndUnexpectedError.Text, AssemblyData.EmueraVersionText));
			console.PrintError(exc.GetType().ToString() + ":" + exc.Message);
			string[] stack = exc.StackTrace!.Split('\n');
			for (int i = 0; i < stack.Length; i++)
			{
				console.PrintError(stack[i]);
			}
		}
	}

	private void handleException(Exception exc, LogicalLine current, bool playSound)
	{
		console.ThrowError(playSound);
		ScriptPosition? position = null;
		if ((exc is EmueraException ee) && (ee.Position != null))
			position = ee.Position;
		else if ((current != null) && (current.Position != null))
			position = current.Position;
		string posString = "";
		if (position != null)
		{
			if (position.Value.LineNo >= 0)
				posString = string.Format(trerror.ErrorFileAndLine.Text, position.Value.Filename, position.Value.LineNo.ToString());
			else
				posString = string.Format(trerror.ErrorFile.Text, position.Value.Filename);

		}
		// T-027 Phase 4 双写：画面保留玩家可见错误（含错误按钮/脚本栈帧，UX 不变），门面补 Error 级诊断（类型+消息+位置+.NET 堆栈）供排障。
		EmueraLog.Error("script", $"脚本异常: {exc.GetType().Name}: {exc.Message} @ {posString}\n{exc.StackTrace}");
		if (exc is CodeEE)
		{
			if (position != default)
			{
				if (current is InstructionLine procline && procline.FunctionCode == FunctionCode.THROW)
				{
					console.PrintErrorButton(string.Format(trerror.HasThrow.Text, posString), position);
					printRawLine(position);
					console.PrintError(string.Format(trerror.ThrowMessage.Text, exc.Message));
				}
				else
				{
					console.PrintErrorButton(string.Format(trerror.HasError.Text, posString, AssemblyData.EmueraVersionText), position);
					printRawLine(position);
					console.PrintError(string.Format(trerror.ErrorMessage.Text, exc.Message));
				}
				console!.PrintError(string.Format(trerror.ErrorInFunc.Text, current!.ParentLabelLine!.LabelName, current!.ParentLabelLine!.Position!.Value.Filename, current!.ParentLabelLine!.Position!.Value.LineNo.ToString()));
				console.PrintError(trerror.FuncCallStack.Text);
				LogicalLine parent;
				int depth = 0;
				while ((parent = state.GetReturnAddressSequensial(depth++)) != null)
				{
					if (parent.Position != null)
					{
						console.PrintErrorButton(string.Format(trerror.ErrorFuncStack.Text, parent.Position!.Value.Filename, parent.Position!.Value.LineNo.ToString(), parent.ParentLabelLine.LabelName), parent.Position);
					}
				}
			}
			else
			{
				console.PrintError(string.Format(trerror.HasError.Text, posString, AssemblyData.EmueraVersionText));
				console.PrintError(exc.Message);
			}
		}
		else if (exc is ExeEE)
		{
			console.PrintError(string.Format(trerror.HasEmueraError.Text, posString, AssemblyData.EmueraVersionText));
			console.PrintError(exc.Message);
		}
		else
		{
			console.PrintError(string.Format(trerror.HasUnexpectedError.Text, posString, AssemblyData.EmueraVersionText));
			console.PrintError(exc.GetType().ToString() + ":" + exc.Message);
			string[] stack = exc.StackTrace!.Split('\n');
			for (int i = 0; i < stack.Length; i++)
			{
				console.PrintError(stack[i]);
			}
		}
	}

	public void printRawLine(ScriptPosition? position)
	{
		string str = getRawTextFormFilewithLine(position);
		if (!string.IsNullOrEmpty(str))
			console.PrintError(str);
	}

	public static string getRawTextFormFilewithLine(ScriptPosition? position)
	{
		// 决策二：错误路径改用 Preload 缓存，消除 File.ReadLines + DetectEncoding 的重复 I/O。
		// 同时修复原 .csv 分支误传 Program.ErbDir 给 DetectEncoding 的复制粘贴 bug。
		// preload-cache-lifecycle 01（ADR-0023）：加载收尾清空缓存后未命中 → 查路径索引按需回读；
		// 失败三态（文件不存在 / IO 异常 / 行号越界）返回占位文本，不得抛新异常。
		// LineNo <= 0 保持返回 ""（合成位置，如 Preload 警告的 ScriptPosition(path, 0)——
		// PrintWarning 对 LineNo >= 0 均调 printRawLine，占位会污染加载期警告输出）。
		string extents = position!.Value.Filename[^4..].ToLower();
		if (extents != ".erb" && extents != ".csv")
			return "";
		string filename = position!.Value.Filename;
		string fullPath = Program.DirAccessor.CombinePath(
			extents == ".csv" ? Program.CsvDir : Program.ErbDir,
			filename);
		string[]? lines = Preload.TryGetFileLines(fullPath);
		lines ??= ReadSourceLinesForErrorEcho(filename, fullPath);
		int lineNo = position!.Value.LineNo;
		if (lineNo <= 0)
			return "";
		if (lines != null && lineNo <= lines.Length)
			return lines[lineNo - 1];
		return string.Format(trerror.SourceLineUnavailable.Text, filename, lineNo);
	}

	/// <summary>
	/// preload-cache-lifecycle 01：报错回显的按需回读（缓存未命中时）。
	/// 查路径索引定位全路径（索引未命中退回 CombinePath 重构值），经 DirAccessor 读字节 +
	/// EncodingHandler 解码拆行。任何失败返回 null（由调用方落占位文本），不抛新异常。
	/// </summary>
	static string[]? ReadSourceLinesForErrorEcho(string filename, string fallbackFullPath)
	{
		try
		{
			var fullPath = Preload.TryGetFullPath(filename, out var indexed) ? indexed : fallbackFullPath;
			var accessor = Program.DirAccessor;
			var bytes = accessor.ReadAllBytes(fullPath);
			return bytes == null ? null : EncodingHandler.ReadAllLinesFromBytes(bytes);
		}
		catch (Exception ex)
		{
			// IO 异常等一律降级占位（三态失败不抛新异常）；留 Warn 供排障区分"文件不在"与"IO 异常"
			EmueraLog.Warn("preload", $"readback FAILED: {filename} → {ex.GetType().Name}: {ex.Message}");
			return null;
		}
	}

	/// <summary>
	/// ADR-0011 决策二 + ADR-0012：引擎数据加载子模块（F2 深模块）。
	/// 字段在 <see cref="Initialize"/>（composition root）中创建；Loader 仅处理文件 I/O，
	/// 经方法参数接收所需对象（含 <see cref="LoaderEnv"/> / scaningLine sink / noError sink），
	/// 不存储 Process 反向引用字段（F2），方法签名亦不接收 Process。
	/// PluginManager 装配已移至 <see cref="LoadEngineData"/>（composition root）。
	/// 统一入口 <see cref="LoadEngineData"/> 按字段创建顺序写回 Process 实例字段。
	/// </summary>
	internal sealed class Loader
	{
		private readonly EmueraConsole console;
		internal Loader(EmueraConsole console) { this.console = console; }

		/// <summary>统一引擎数据加载入口（composition root 委托）。</summary>
		internal async Task<bool> LoadEngineData(Process process, LoaderEnv env,
			StreamWriter? logWriter, Stopwatch stopWatch)
		{
			EmueraLog.Info("Initialize", "stage=LoadBaseCsv start");
			if (!await LoadBaseCsv(process.gamebase, process.constantData, env, logWriter, stopWatch))
			{
				EmueraLog.Error("Initialize", "soft-return reason=LoadBaseCsvFalse");
				return false;
			}
			EmueraLog.Info("Initialize", "stage=LoadBaseCsv ok");
			console.SetWindowTitle(process.gamebase.ScriptWindowTitle);
			logWriter?.WriteLine($"Proc:Init:EtcCSV:End {stopWatch.ElapsedMilliseconds}ms");

			process.TrainName = process.constantData.GetCsvNameList(VariableCode.TRAINNAME);
			process.vEvaluator = new VariableEvaluator(process.gamebase, process.constantData);
			// 方言 profile 捕获点（实现票 16）：已在 Initialize 的 try 保护圈内，且晚于注册表守卫。
			process.DialectProfile = GlobalStatic.DialectProfile;
			process.idDic = new IdentifierDictionary(process.vEvaluator.VariableData, process.DialectProfile);
			StrForm.Initialize();
			VariableParser.Initialize();
			process.exm = new ExpressionMediator(process, process.vEvaluator, console);
			process._systemProc = new SystemProc(console, process.vEvaluator, process.gamebase, process.TrainName, process.executionState, process.labelDic);
			process._scriptProc = new ScriptProc(console, process.vEvaluator, process.exm, process.idDic, process.executionState, process.gamebase, process.TrainName, process.labelDic);

			PluginManager.GetInstance().SetParent(process, process.state, process.exm);
			PluginManager.GetInstance().LoadPlugins();
			// mem-leak-fix：无插件游戏——插件系统在启动时即回收。SetParent 无条件把
			// Process/ExpressionMediator/VariableData wrapper 缓存进 PluginManager 单例与
			// PluginAPICharContext 静态字段（跨会话泄漏源）；LoadPlugins 后若未加载到任何
			// 插件方法，立即清空这些静态持有，游戏循环运行期不再保留。
			// 有插件游戏保留（运行时需要），退出时由 GlobalStatic.Scope.Dispose 统一清理。
			if (!PluginManager.GetInstance().HasPlugins)
			{
				PluginManager.ResetInstance();
				PluginAPICharContext.ClearStaticCache();
			}

			logWriter?.WriteLine($"Proc:Init:ERH:Start {stopWatch.ElapsedMilliseconds}ms");
			EmueraLog.Info("Initialize", "stage=ERH+ERB start");
			return await LoadHeadersAndScripts(process.idDic, process.exm, process.labelDic,
				process.vEvaluator, env,
				line => process.scaningLine = line,
				ok => process.executionState.noError = ok,
				logWriter, stopWatch);
		}

		/// <summary>加载 GAMEBASE.CSV 与其余全部 CSV 数据。</summary>
		private async Task<bool> LoadBaseCsv(GameBase gamebase, ConstantData constantData,
			LoaderEnv env, StreamWriter? logWriter, Stopwatch stopWatch)
		{
			logWriter?.WriteLine($"Proc:Init:MainCSV:Start {stopWatch.ElapsedMilliseconds}ms");
			var gamebasePath = env.DirAccessor.CombinePath(env.CsvDir, "GAMEBASE.CSV");
			EmueraLog.Info("Initialize", $"stage=GAMEBASE path={gamebasePath}");
			if (!await Task.Run(() => gamebase.LoadGameBaseCsv(gamebasePath, env.DirAccessor)))
			{
				ParserMediator.FlushWarningList();
				console.PrintSystemLine(trsl.GamebaseError.Text);
				EmueraLog.Error("Initialize", "soft-return reason=GameBaseCsvFail");
				return false;
			}
			logWriter?.WriteLine($"Proc:Init:MainCSV:End {stopWatch.ElapsedMilliseconds}ms");

			constantData.LoadData(env.CsvDir, env.DirAccessor, console, Config.DisplayReport);
			return true;
		}

		/// <summary>加载 ERH 头文件与 ERB 脚本文件。</summary>
		private async Task<bool> LoadHeadersAndScripts(IdentifierDictionary idDic, ExpressionMediator exm,
			LabelDictionary labelDic, VariableEvaluator vEvaluator, LoaderEnv env,
			Action<LogicalLine> setScanLine, Action<bool> setNoError,
			StreamWriter? logWriter, Stopwatch stopWatch)
		{
			ErhLoader hLoader = new(console, idDic, vEvaluator, env);

			LexicalAnalyzer.UseMacro = false;

			EmueraLog.Info("Initialize", $"stage=ERH LoadHeaderFiles start dir={env.ErbDir}");
			if (!await Task.Run(() => hLoader.LoadHeaderFiles(env.ErbDir, Config.DisplayReport)))
			{
				ParserMediator.FlushWarningList();
				console.PrintSystemLine("");
				EmueraLog.Error("Initialize", "soft-return reason=ErhLoadHeaderFilesFalse");
				return false;
			}
			EmueraLog.Info("Initialize", "stage=ERH ok");
			LexicalAnalyzer.UseMacro = idDic.UseMacro();
			logWriter?.WriteLine($"Proc:Init:ERH:End {stopWatch.ElapsedMilliseconds}ms");

			logWriter?.WriteLine($"Proc:Init:ERB:Start {stopWatch.ElapsedMilliseconds}ms");
			EmueraLog.Info("proc", $"LoadHeadersAndScripts: ERH OK, starting ERB from {env.ErbDir}");
			var erbLoader = new ErbLoader(console, exm, idDic, env, setScanLine);
			bool erbNoError;
			if (env.AnalysisMode)
				erbNoError = await erbLoader.LoadErbList(env.AnalysisFiles, labelDic);
			else
				erbNoError = await erbLoader.LoadErbDir(env.ErbDir, Config.DisplayReport, labelDic);
			setNoError(erbNoError);
			EmueraLog.Info("proc", $"LoadHeadersAndScripts: erbNoError={erbNoError}, labels={labelDic?.Count ?? 0}");
			return true;
		}
	}
}
