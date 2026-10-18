using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Sub;
using System;
using System.Collections.Generic;
using System.Threading;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// 并行生成期的跨文件重名检查待办（erb-loader-parallel 03）。并行阶段 labelDic 不接受并发读，
/// 该检查延后到合并期（<see cref="ErbFileAssembler.Merge"/>）执行；为保证警告仍出现在
/// 原 @ 行的行序位置，生成期先在 <see cref="ErbFileArtifact.Warnings"/> 放占位槽，
/// 合并期替换为真实警告或删除。
/// </summary>
internal readonly record struct PendingOverloadCheck(int Slot, FunctionLabelLine Label, FunctionLabelLine? InFileSenior);

/// <summary>并行生成期的跨文件 #ONLY 重複宣言检查待办（同上，占位槽机制）。</summary>
internal readonly record struct PendingOnlyCheck(int Slot, string LabelName, ScriptPosition Position);

/// <summary>
/// 单文件解析产物（erb-loader-parallel Issue 01）。
/// 生成阶段（<see cref="ErbFileAssembler.Generate"/>）只写本对象：行链挂在各标签线上、警告经
/// ParserMediator 重定向进 <see cref="Warnings"/>、控制台输出（AnalysisMode PrintC/间隔行、
/// 开文件失败 PrintError）缓冲为 <see cref="ConsoleOps"/>——生成期间无任何 LabelDictionary
/// 写入 / 控制台直写 / 共享计数写。合并阶段（<see cref="ErbFileAssembler.Merge"/>）按原文件顺序执行
/// 全部共享状态写入与警告发射，03 并行化以本对象为执行单元。
/// </summary>
internal sealed class ErbFileArtifact
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
	/// <summary>b2-irdiskcache：本文件的函数 IR blob（按 @ 出现序），仅 IR 生成期（_irBuildEnabled）非空。</summary>
	public readonly List<IrBlob> IrBlobs = [];
}

/// <summary>
/// ADR-0034：单文件「生成 → 合并」的深模块（原 <c>ErbLoader</c> B 组，566-1056 + 1254-1261）。
/// <para>
/// <b>唯一职责</b>：把一个 ERB 文件翻译成 <see cref="ErbFileArtifact"/>（生成），再把产物按原文件
/// 顺序并入共享状态（合并）。<c>ErbLoader</c> 退化为装配/调度点，不再持有"单文件怎么解析"的知识。
/// </para>
/// <para>
/// <b>接缝</b>（ADR-0034 决策三，档 3）：宿主依赖经 <see cref="Context"/> 显式穿参；可变共享对象
/// （<c>labelDic</c> / <c>isOnlyEvent</c> / <see cref="Stats"/>）传引用；<b>标量</b>聚合
/// （<c>enabledLineCount</c> / <c>noError</c>）由 <see cref="MergeOutcome"/> 回传、宿主应用。
/// 「这个模块会改哪些共享状态」从此是本类<b>签名可见的事实</b>，而非方法体内部细节。
/// </para>
/// <para>
/// <b>不做</b>：调度（串行/并行、窗口、测试钩子）属 C 组，仍在 <c>ErbLoader</c>；<c>PPState</c>
/// 是与惰性物化共用的共享件，已外移为顶层类型（决策四）。
/// </para>
/// </summary>
internal static class ErbFileAssembler
{
	/// <summary>
	/// 宿主依赖的只读快照 + 合并期可变对象的引用。
	/// 每次调用前现算（<c>labelDic</c> / <c>_ir</c> / <c>_irPipeline</c> 在一次装载内会被重赋值），
	/// 故不缓存为模块静态——缓存会引入"快照陈旧"这类新失效模式。
	/// </summary>
	internal readonly struct Context
	{
		internal Context(LoaderEnv env, IdentifierDictionary idDic, EmueraConsole output,
			Action<LogicalLine> setScanLine, LabelDictionary labelDic, IrState? ir, ErbIrLoadPipeline? irPipeline,
			bool useRenameFile, bool hasRenameDic, StringComparer strComper, bool lazyFunctionLoad,
			bool warnNormalFunctionOverloading, int printCPerLine, bool useNewRandom)
		{
			Env = env;
			IdDic = idDic;
			Output = output;
			SetScanLine = setScanLine;
			LabelDic = labelDic;
			Ir = ir;
			IrPipeline = irPipeline;
			UseRenameFile = useRenameFile;
			HasRenameDic = hasRenameDic;
			StrComper = strComper;
			LazyFunctionLoad = lazyFunctionLoad;
			WarnNormalFunctionOverloading = warnNormalFunctionOverloading;
			PrintCPerLine = printCPerLine;
			UseNewRandom = useNewRandom;
		}

		public readonly LoaderEnv Env;
		public readonly IdentifierDictionary IdDic;
		public readonly EmueraConsole Output;
		public readonly Action<LogicalLine> SetScanLine;
		/// <summary>共享标签字典——合并期的<b>唯一</b>写入点落在 <see cref="Merge"/>。</summary>
		public readonly LabelDictionary LabelDic;
		public readonly IrState? Ir;
		public readonly ErbIrLoadPipeline? IrPipeline;

		// ── ADR-0035：原生的 ambient 配置读，全部在<b>装载入口一次快照</b>（决策一）─────────────
		// 逐字段对应原来的 Config.Config.* / ParserMediator.RenameDic / JSONConfig.Data.* 直读，
		// 不做折叠（决策二）：保留"这个决定由哪个 ambient 得出"的可追溯性。
		/// <summary>原 <c>Config.Config.UseRenameFile</c>。</summary>
		public readonly bool UseRenameFile;
		/// <summary>原 <c>ParserMediator.RenameDic != null</c>。</summary>
		public readonly bool HasRenameDic;
		/// <summary>原 <c>Config.Config.StrComper</c>。</summary>
		public readonly StringComparer StrComper;
		/// <summary>原 <c>Config.Config.LazyFunctionLoad</c>。</summary>
		public readonly bool LazyFunctionLoad;
		/// <summary>原 <c>Config.Config.WarnNormalFunctionOverloading</c>。</summary>
		public readonly bool WarnNormalFunctionOverloading;
		/// <summary>原 <c>Config.Config.PrintCPerLine</c>。</summary>
		public readonly int PrintCPerLine;
		/// <summary>原 <c>JSONConfig.Data.UseNewRandom</c>。</summary>
		public readonly bool UseNewRandom;
	}

	/// <summary>
	/// issues/17 genmerge 逐文件分账（「IR 桩 / 无函数 / 回落读源」）。并行 worker 写入，故用 Interlocked。
	/// 保留为常驻观测而非一次性探针：它是 A′ 的体检指标——fallbackSrc 若回升到与文件数同量级，
	/// 说明"在全集内却无 IR 条目"的判据失效（如全集构建异常），无需重跑取证即可发现。
	/// 取证期的临时探针（三段 Stopwatch 计时 + 落源文件名样本）已随 issue 17 结论落定拆除。
	/// <para>宿主持有并复用（并行轮起始清零、收尾读），本模块只自增——故传引用而非回传。</para>
	/// </summary>
	internal sealed class Stats
	{
		public int IrStubFiles;
		/// <summary>判为"在命中轮全集内、但无 IR 条目"（= 确认该文件无函数）而免读源的文件数（issues/17 A′ 的收益口径）。</summary>
		public int IrNoFuncFiles;
		public int SourceFallbackFiles;
		/// <summary>其中"IR 已 Available 却仍回落"的条数——应恒为 0。</summary>
		public int SourceFallbackOnIrHit;

		public void Reset()
		{
			IrStubFiles = 0;
			IrNoFuncFiles = 0;
			SourceFallbackFiles = 0;
			SourceFallbackOnIrHit = 0;
		}
	}

	/// <summary>合并回传的<b>标量</b>聚合（档 3）：宿主侧 <c>enabledLineCount += EnabledLines</c>、<c>if (HasError) noError = false</c>。</summary>
	internal readonly record struct MergeOutcome(int EnabledLines, bool HasError);

	/// <summary>
	/// ファイル一つを読む（生成阶段）：产出全部收进文件产物，不触碰 LabelDictionary / 控制台 / 共享计数。
	/// 串行路径下紧随 <see cref="Merge"/>；生成期对共享状态只读（labelDic 重载检查的合并序
	/// 快照、isOnlyEvent 已合并宣言、IdentifierDictionary 宏表）。
	/// </summary>
	/// <param name="deferCrossFileChecks">erb-loader-parallel 03：并行调度下为 true——跨文件重名与
	/// #ONLY 重複检查延后到合并期（占位槽保行序），生成期不读 labelDic / isOnlyEvent 共享快照。</param>
	/// <param name="replayOnFailure">串行调度为 true（异常时本方法内回放控制台输出与警告再抛，与旧路径
	/// 一致）；并行调度为 false（缓冲清理照常，回放由主线程按窗口序统一执行，避免并发控制台写）。</param>
	internal static ErbFileArtifact Generate(string filepath, string filename, List<string> isOnlyEvent,
		bool deferCrossFileChecks, bool replayOnFailure, in Context ctx, Stats stats)
	{
		var art = new ErbFileArtifact { Filename = filename };
		ParserMediator.BeginWarningBuffer(art.Warnings);
		LogicalLineParser.ScanLineMirror.Value = line => art.ScaningLine = line;
		try
		{
			GenerateErbFileCore(filepath, filename, isOnlyEvent, art, deferCrossFileChecks, ctx, stats);
		}
		catch
		{
			// 异常路径输出与旧路径一致：已产生的控制台输出与警告照常发射（旧路径为直写/直入全局列表）
			if (replayOnFailure)
			{
				ReplayConsoleOps(art, ctx);
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

	private static void GenerateErbFileCore(string filepath, string filename, List<string> isOnlyEvent, ErbFileArtifact art,
		bool deferCrossFileChecks, in Context ctx, Stats stats)
	{
		// b2-irdiskcache：IR 命中 → 本文件由索引直建桩，免读源文件（design §5.2）——第二次启动的大头收益所在。
		// issues/17 A′：命中判定分三段（旧实现只有 (1)(3)，把 (2) 误并进 (3)）——
		//   (1) ByFile 命中且有条目 → IR 桩。真机 2363/2388 走此路，SAF 下总耗仅 38ms，本就无问题；
		//   (2) ByFile 未命中、但文件在命中轮全集内 → **该文件确实没有函数**（IR 索引只记有函数的文件），
		//       直接建空产物 return。旧实现落到 (3)：SAF 下每个白付 ~25ms 读源解析（25 个文件 ≈ 620ms），
		//       且主循环按序 await 使这 25 次阻塞串行放大到整轮 genmerge 的 100%；
		//   (3) 不在全集内（异常/陈旧路径）→ 回落源路径，保持既有安全语义。
		if (ctx.Ir is { Available: true } irHit)
		{
			if (irHit.ByFile.TryGetValue(filename, out var irEntries) && irEntries.Count > 0)
			{
				Interlocked.Increment(ref stats.IrStubFiles); // issues/17 观测
				TryGenerateFromIr(filename, irEntries, art);
				return;
			}
			if (irHit.AllFiles?.Contains(filename) == true)
			{
				Interlocked.Increment(ref stats.IrNoFuncFiles); // issues/17 取证：A′ 新省下的文件数
				return; // 空产物——IR 已证明此文件无函数，无需读源
			}
		}

		// issues/17：走到此处即回落读源。IR 可用时本不该到达（判据漏了 → fallbackOnIrHit 非 0 可即时
		// 发现）；IR 不可用（冷枚举轮）则是常态——SAF 下逐文件走跨进程读，正是修复前 620ms 的来源。
		Interlocked.Increment(ref stats.SourceFallbackFiles);
		if (ctx.Ir is { Available: true })
			Interlocked.Increment(ref stats.SourceFallbackOnIrHit);

		using var eReader = new EraStreamReader(ctx.UseRenameFile && ctx.HasRenameDic);

		if (!eReader.OpenOnCache(filepath, filename))
		{
			art.ConsoleOps.Add(c => c.PrintError(string.Format(trerror.FailedOpenFile.Text, eReader.Filename)));
			return; // ADR-0019：文件打不开直接跳过，避免空 eReader 进入解析循环
		}
		// ADR-0039：警告目的地显式注入（本文件产物列表）——Begin/End 缓冲协议仍保留，
		// 因为 LogicalLineParser 等被调方还有 ~54 处经 AsyncLocal 落进同一个列表（见 ADR 事实 4）。
		var ppstate = new PPState(ctx.Env, ctx.IdDic, art.Warnings);
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
		var firstValidLabelByName = new Dictionary<string, FunctionLabelLine>(ctx.StrComper);
		var firstGotoByParent = new Dictionary<FunctionLabelLine, Dictionary<string, GotoLabelLine>>();
		// lazy-function-load（B 线 B1）：惰性模式 = 开 LazyFunctionLoad 且非 AnalysisMode（AnalysisMode 强制 eager）。
		// 惰性下函数体行链不解析——labelDic 只注册全量标签桩（IsStub），仅 # 签名行处理（保 Argument/Def/IsMethod）。
		bool lazy = ctx.LazyFunctionLoad && !ctx.Env.AnalysisMode;
		// b2-irdiskcache：收集本文件函数 IR blob（仅 IR 生成期，即增量写器非空）。
		bool collectIr = ctx.IrPipeline is { CollectIr: true };
		FunctionLabelLine? irCurLabel = null;
		List<IrLine>? irCurLines = null;
		// 结束当前函数的 blob（@ 边界 / EOF 时调用）。
		void FinishIrBlob()
		{
			if (!collectIr || irCurLabel == null)
			{
				irCurLabel = null;
				irCurLines = null;
				return;
			}
			if (irCurLabel is InvalidLabelLine bad)
				art.IrBlobs.Add(IrBlob.InvalidFunc(bad.LabelName, bad.Position!.Value.LineNo, bad.ErrMes));
			else
				art.IrBlobs.Add(IrBlob.Valid(irCurLabel.LabelName, irCurLabel.Position!.Value.LineNo, irCurLines!));
			irCurLabel = null;
			irCurLines = null;
		}
		if (ctx.Env.AnalysisMode)
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
					art.Warnings.Warn(trerror.InvalidSBrackets.Text, position, 1);
				ppstate.AddKeyWord(token, token2, position);
				st.ShiftNext();
				if (!st.EOS)
					art.Warnings.Warn(string.Format(trerror.IgnoreAfterPreprosessor.Text, token), position, 1);
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
					art.Warnings.Warn(trerror.InvalidSharp.Text, position, 1);
					continue;
				}
				// 占位锚：本 # 行首个警告的槽位（跨文件 ONLY 检查占位须插到这里——
				// 串行路径该警告在 ParseSharpLine 的 Contains 检查点发射，先于同行的 BeIgnore* 警告）
				int lineWarnStart = art.Warnings.Count;
				int onlyCountBefore = art.OnlyDeclarations.Count;
				// b2-irdiskcache：收集 # 签名行文本（物化时重跑 ParseSharpLine 重建签名）。
				if (collectIr && irCurLabel != null)
					irCurLines?.Add(new IrLine(position!.Value.LineNo, st.RowString));
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
				if (!isFunction && lazy)
				{
					// b2-irdiskcache：$ goto 属函数体，物化时随 ReparseFunction 重建——blob 收集进本函数行集。
					if (collectIr && irCurLabel != null)
						irCurLines?.Add(new IrLine(eReader.LineNo, st.RowString));
					continue; // 惰性：$ goto 属函数体，物化时随 ReparseFunction 重建，此处跳过
				}
				nextLine = LogicalLineParser.ParseLabelLine(st, position, ctx.Output, art.ConsoleOps);
				if (isFunction)
				{
					var label = nextLine as FunctionLabelLine;
					lastLabelLine = label!;
					// b2-irdiskcache：@ 边界——结束上一函数 blob、开启本函数 blob（@ 行文本作首行，物化重建 wc）。
					FinishIrBlob();
					if (collectIr)
					{
						irCurLabel = label;
						irCurLines = new() { new IrLine(eReader.LineNo, st.RowString) };
					}
					// 惰性：注册标签桩——保签名头、函数体行链留空；函数体在首调用/主循环时物化。
					if (lazy)
						label!.IsStub = true;
					art.FunctionLabels.Add(label!);
					if (label is InvalidLabelLine)
					{
						art.HasError = true;

						art.Warnings.Warn(nextLine.ErrMes, position, 2);
					}
					else// if (label is FunctionLabelLine)
					{
						var inFileSenior = firstValidLabelByName.GetValueOrDefault(label!.LabelName);
						if (!label!.IsEvent && (ctx.WarnNormalFunctionOverloading || ctx.Env.AnalysisMode))
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
								FunctionLabelLine? seniorLabel = ctx.LabelDic.GetEarliestLabelWithName(label.LabelName, filename);
								seniorLabel ??= inFileSenior;
								if (seniorLabel != null)
								{
									//output.NewLine();
									art.Warnings.Warn(string.Format(trerror.FuncIsAlreadyDefined.Text, label.LabelName, seniorLabel.Position!.Value.Filename, seniorLabel.Position!.Value.LineNo.ToString()), position, 1);
									funcCount = -1;
								}
							}
						}
						funcCount++;
						firstValidLabelByName.TryAdd(label.LabelName, label);
						if (ctx.Env.AnalysisMode && ctx.PrintCPerLine > 0 && funcCount % ctx.PrintCPerLine == 0)
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
								byName = new Dictionary<string, GotoLabelLine>(ctx.StrComper);
								firstGotoByParent.Add(lastLabelLine, byName);
							}
							if (byName.TryGetValue(gotoLabel.LabelName, out GotoLabelLine? firstGoto))
							{
								ScriptPosition pos = firstGoto.Position!.Value;
								art.Warnings.Warn(string.Format(trerror.LabelIsAlreadyDefined.Text, gotoLabel.LabelName, pos.Filename, pos.LineNo.ToString()), position, 2);
							}
							else
								byName.Add(gotoLabel.LabelName, gotoLabel);
						}
					}
				}
				if (nextLine is InvalidLine)
				{
					art.HasError = true;
					art.Warnings.Warn(nextLine.ErrMes, position, 2);
				}
			}
			else
			{
				// b2-irdiskcache：函数体指令行文本收集（blob 物化输入）。
				if (collectIr && irCurLabel != null)
					irCurLines?.Add(new IrLine(eReader.LineNo, st.RowString));
				// 惰性：函数体指令行不解析（物化时随 ReparseFunction 重建），直接跳过
				if (lazy)
					continue;
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
					art.Warnings.Warn(trerror.LineBeforeFunc.Text, position, 1);
				nextLine = LogicalLineParser.ParseLine(st, position, ctx.Output, lastLabelLine!);


				if (nextLine == null)
					continue;
				if (nextLine is InvalidLine)
				{
					art.HasError = true;
					art.Warnings.Warn(nextLine.ErrMes, position, 2);
				}
				else if (ctx.UseNewRandom &&
										nextLine is InstructionLine instruction)
				{
					switch (instruction.FunctionCode)
					{
						case FunctionCode.RANDOMIZE:
							art.Warnings.Warn(trerror.IgnoreRandomize.Text, position, 0);
							break;
						case FunctionCode.DUMPRAND:
							art.Warnings.Warn(trerror.CanNotUseDumprand.Text, position, 0);
							break;
						case FunctionCode.INITRAND:
							art.Warnings.Warn(trerror.CanNotUseInitrand.Text, position, 0);
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
		FinishIrBlob(); // b2-irdiskcache：EOF 收尾最后函数 blob
		position = new ScriptPosition(eReader.Filename, -1);
		ppstate.FileEnd(position);
		return;
	}

	/// <summary>
	/// b2-irdiskcache：IR 命中路径的最小桩直建（design §5.2，实现时对账项 B）。免读源文件、免全文件词法扫描。
	/// 桩只带 LabelName + Position + 最小签名（IsEvent/IsSystem/Depth 由标签名经 IdentifierDictionary 判同节点，
	/// 与 ParseLabelLine 一致）；Arg/Def/私有变量/IsMethod/LocalLength 留空 → setLabelsArg 跳过、物化时从 blob
	/// 重建（@ 行重建 wc → # 行重跑 ParseSharpLine → parseLabel → resizeLocalVars）。警告由物化期补发，生成期跳过
	/// 源 ParsSharpLine/重载检查（指纹保证 IR 与源的等价性不变，改动源 → 指纹变 → 全量重建 → 警告照常）。
	/// </summary>
	private static void TryGenerateFromIr(string filename, List<IrIndexEntry> entries, ErbFileArtifact art)
	{
		foreach (var e in entries)
		{
			var pos = new ScriptPosition(e.File, e.LineNo - 1);
			if (e.Invalid)
			{
				art.FunctionLabels.Add(new InvalidLabelLine(pos, e.Label, e.ErrMes ?? ""));
				art.HasError = true;
				continue;
			}
			var label = new FunctionLabelLine(pos, e.Label, null!);
			label.IsStub = true;
			label.IrBacked = true;
			if (IdentifierDictionary.IsEventLabelName(e.Label))
			{
				label.IsEvent = true;
				label.IsSystem = true;
				label.Depth = 0;
			}
			else if (IdentifierDictionary.IsSystemLabelName(e.Label))
			{
				label.IsSystem = true;
				label.Depth = 0;
			}
			art.FunctionLabels.Add(label);
		}
	}

	/// <summary>
	/// 合并阶段（erb-loader-parallel Issue 01）：按原文件顺序把文件产物并入共享状态——
	/// 缓冲警告按序发射、全部 LabelDictionary 写入（IfFileLoadClearLabelWithPath / AddLabel /
	/// AddInvalidLabel / AddLabelDollar）、#ONLY 宣言入共享列表、控制台输出回放、计数聚合。
	/// Index / FileIndex 分配序与警告顺序由“逐文件生成后立即按序合并”保证与旧串行路径一致。
	/// 03：并行产物先经 <see cref="ResolvePendingCrossFileChecks"/> 补齐跨文件检查（占位槽替换）。
	/// <para>
	/// <b>顺序敏感</b>：<see cref="ResolvePendingCrossFileChecks"/> 必须<b>先于</b>
	/// <c>labelDic.IfFileLoadClearLabelWithPath</c> 与 <c>isOnlyEvent.AddRange</c>——此时
	/// labelDic / isOnlyEvent 恰含“更早合并文件”的全部内容，与串行路径生成期快照的可见性一致。
	/// 这段顺序是串行/并行等价性的凭据，改动前请先读 ADR-0034 决策三。
	/// </para>
	/// </summary>
	/// <returns>标量聚合（启用行数 / 是否有错）；宿主负责 <c>enabledLineCount +=</c> 与 <c>noError</c> 落定。</returns>
	internal static MergeOutcome Merge(ErbFileArtifact art, List<string> isOnlyEvent, in Context ctx)
	{
		ResolvePendingCrossFileChecks(art, isOnlyEvent, ctx);
		// 警告最先入列：等价于旧路径“解析期即入全局列表”（共享写入若异常也不丢已产出警告）
		ParserMediator.EmitWarningBuffer(art.Warnings);
		ctx.LabelDic.IfFileLoadClearLabelWithPath(art.Filename);
		ReplayConsoleOps(art, ctx);
		var labels = art.FunctionLabels;
		for (int i = 0; i < labels.Count; i++)
		{
			if (labels[i] is InvalidLabelLine)
				ctx.LabelDic.AddInvalidLabel(labels[i]);
			else
				ctx.LabelDic.AddLabel(labels[i]);
		}
		var gotoLabels = art.GotoLabels;
		for (int i = 0; i < gotoLabels.Count; i++)
			ctx.LabelDic.AddLabelDollar(gotoLabels[i]);
		if (art.OnlyDeclarations.Count > 0)
			isOnlyEvent.AddRange(art.OnlyDeclarations);
		if (art.ScaningLine != null)
			ctx.SetScanLine(art.ScaningLine);
		// 原就地写 enabledLineCount / noError 改为由 <see cref="MergeOutcome"/> 回传（ADR-0034 档 3）。
		// b2-irdiskcache：逐文件批量落盘到 IR（本文件 image 已并入共享状态，blob 写完即清空释放——内存 O(单文件)）。
		if (ctx.IrPipeline is { CollectIr: true } irPipeline && art.IrBlobs.Count > 0)
		{
			irPipeline.AppendFile(art.Filename, art.IrBlobs);
			art.IrBlobs.Clear();
		}
		return new MergeOutcome(art.EnabledLineCount, art.HasError);
	}

	/// <summary>并行产物的跨文件检查占位（合并期由 <see cref="ResolvePendingCrossFileChecks"/> 替换或删除）。</summary>
	private static ParserMediator.ParserWarning PlaceholderWarning() => new("", null, 1, null!);

	/// <summary>
	/// erb-loader-parallel 03：合并期补检——并行生成期延后的跨文件重名（FuncIsAlreadyDefined）与
	/// #ONLY 重複宣言（AlreadyDeclaredOnly）检查。此时 labelDic / isOnlyEvent 恰含“更早合并文件”
	/// 的全部内容（本文件的 IfFileLoadClearLabelWithPath 尚未执行、宣言尚未 AddRange），与串行路径
	/// 生成期快照的可见性一致，判定结果与警告序（占位槽位置）均与串行一致。串行产物无待办，no-op。
	/// </summary>
	private static void ResolvePendingCrossFileChecks(ErbFileArtifact art, List<string> isOnlyEvent, in Context ctx)
	{
		if (art.PendingOverloads.Count == 0 && art.PendingOnlys.Count == 0)
			return;
		List<int> deadSlots = [];
		foreach (var check in art.PendingOverloads)
		{
			// senior 判定序与串行即时检查一致：跨文件最早（排除本文件名，重载场景旧标签
			// 由本文件稍后的 IfFileLoadClearLabelWithPath 清理）?? 文件内首现
			FunctionLabelLine? seniorLabel = ctx.LabelDic.GetEarliestLabelWithName(check.Label.LabelName, art.Filename);
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

	internal static void ReplayConsoleOps(ErbFileArtifact art, in Context ctx)
	{
		var ops = art.ConsoleOps;
		for (int i = 0; i < ops.Count; i++)
			ops[i](ctx.Output);
		ops.Clear();
	}

	private static LogicalLine addLine(ErbFileArtifact art, LogicalLine nextLine, LogicalLine lastLine)
	{
		if (nextLine == null)
			return null!;
		art.EnabledLineCount++;
		lastLine.NextLine = nextLine;
		return nextLine;
	}
}
