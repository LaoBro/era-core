using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// ADR-0044（D 组第三刀 · C3b）：单标签**三遍解析**与「函数未找到」决策。
/// <para>
/// 搬来的是 <c>ParseFunctionWithCatch</c>（入口）+ <c>SetArgument</c> / <c>NestCheck</c> / <c>SetJumpTo</c>
/// 三步 + <c>MarkUseCallForm</c> + <c>PrintFunctionNotFoundWarning</c>（ONCE 去重与计数）。
/// </para>
/// <para>
/// <b>接缝</b>（<see cref="Context"/>）：<c>analysisMode</c>（AnalysisMode 走计数而非告警）、
/// <c>exm</c>（SelectCaseJumpTable 构建）、<c>scanLine</c>（解析行写点：串行直写全局 sink、
/// 并行写 per-线程发布槽）、<c>warningDic</c>（AnalysisMode 计数表，宿主出报告时读）、
/// <c>onUseCallForm</c>（宿主标量 <c>useCallForm</c> 的置位回调）。本类<b>不持有</b>宿主字段。
/// </para>
/// </summary>
internal sealed class ErbThreePassParser
{
	/// <summary>宿主依赖快照（与 <c>ErbFileAssembler.Context</c> 同款：标量按值、可变对象传引用、可变标量走回调）。</summary>
	internal readonly struct Context
	{
		internal readonly bool AnalysisMode;
		internal readonly ExpressionMediator Exm;
		internal readonly Action<LogicalLine, ParseContext> ScanLine;
		internal readonly Dictionary<string, long> WarningDic;
		internal readonly Action OnUseCallForm;

		internal Context(bool analysisMode, ExpressionMediator exm,
			Action<LogicalLine, ParseContext> scanLine, Dictionary<string, long> warningDic, Action onUseCallForm)
		{
			AnalysisMode = analysisMode;
			Exm = exm;
			ScanLine = scanLine;
			WarningDic = warningDic;
			OnUseCallForm = onUseCallForm;
		}
	}

	readonly bool analysisMode;
	readonly ExpressionMediator exm;
	readonly Action<LogicalLine, ParseContext> scanLine;
	readonly Dictionary<string, long> warningDic;
	readonly Action onUseCallForm;

	internal ErbThreePassParser(in Context ctx)
	{
		analysisMode = ctx.AnalysisMode;
		exm = ctx.Exm;
		scanLine = ctx.ScanLine;
		warningDic = ctx.WarningDic;
		onUseCallForm = ctx.OnUseCallForm;
	}

	/// <summary>FNF ONCE 去重集合与计数（原 ErbLoader 字段，随决策一并搬入）。</summary>
	readonly HashSet<string> ignoredFNFWarningFiles = new(StringComparer.OrdinalIgnoreCase);
	int ignoredFNFWarningCount;

	/// <summary>被 ONCE 去重掉的 FNF 条数（宿主在装载收尾读它出汇总行）。</summary>
	internal int IgnoredFnfCount => ignoredFNFWarningCount;

	/// <summary>三步耗时桶（主循环期间由宿主挂上；未调用遍历期 InUncalled=true）。</summary>
	internal ParseScriptDiag? ParseDiag;

	// 解析期的三块复用缓冲（原 ErbLoader 实例字段；只被三遍解析使用，随代码一并搬入）
	readonly Stack<InstructionLine> nestStackReuse = new();
	readonly Stack<InstructionLine> selectcaseStackReuse = new();
	readonly List<InstructionLine> tempLineListReuse = new();
	/// <summary>
	/// ADR-0042：回放段的 FNF 重走入口。FNF 决策（ONCE 去重 / count / Warn）仍是宿主的私有逻辑，
	/// 因为它绑着 <see cref="ParseContext"/>（宿主私有嵌套类型）——C3 搬 ParseContext 时一并处理。
	/// </summary>
	internal void ReplayFnf(string str, LogicalLine line, int level, bool isError)
		=> PrintFunctionNotFoundWarning(str, line, level, isError, ParseContext.Serial);

	internal void PrintFunctionNotFoundWarning(string str, LogicalLine line, int level, bool isError, ParseContext ctx)
	{
		// erb-loader-parallel 08 D5#2：未调用遍并行期间缓冲 FNF 事件（含被忽略的——回放时决策原样重走）。
		// ONCE 去重（ignoredFNFWarningFiles）与 ignoredFNFWarningCount 必须串行序，故整个决策延后到回放段；
		// IsError/ErrMes 行内写先行（幂等无害，回放重走时再写一次 no-op）。
		// ADR-0027 决策二.2：落点由显式 ctx 决定（原读 SweepArtCurrent ThreadLocal）——并行 ctx 的
		// Art 非 null → 拦截入缓冲；串行/回放 ctx 的 Art 为 null → 下方原决策逐位重走。
		var sweepArt = ctx.Art;
		if (sweepArt != null)
		{
			if (isError)
			{
				line.IsError = true;
				line.ErrMes = str;
			}
			sweepArt.AddFnfEvent(str, line, level, isError);
			return;
		}
		if (analysisMode)
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
		if (ignore && !analysisMode)
		{
			ignoredFNFWarningCount++;
			return;
		}
		ParserMediator.Warn(str, line, level, isError, false);
	}

	/// <summary>
	/// 单标签三遍解析（setArgument / nestCheck / setJumpTo）。<paramref name="ctx"/> 为**显式**解析上下文
	/// （ADR-0027 决策二.2）：串行主循环与回放传 <see cref="ParseContext.Serial"/>，并行 worker 传其
	/// per-label ctx——本方法及其三步不再读写任何 ambient 槽。
	/// </summary>
	internal void ParseFunctionWithCatch(FunctionLabelLine label, ParseContext ctx)
	{//ここでエラーを捕まえることは本来はないはず。ExeEE相当。
		try
		{
			if (ParseDiag is { } diag)
			{
				// erb-loader-parallel 05：三步分段计时（按 InUncalled 分桶累计）
				var sw = diag.Sw;
				sw.Restart();
				SetArgument(label, ctx);
				long setArg = sw.ElapsedTicks;
				sw.Restart();
				NestCheck(label, ctx);
				long nest = sw.ElapsedTicks;
				sw.Restart();
				SetJumpTo(label, ctx);
				long jump = sw.ElapsedTicks;
				if (diag.InUncalled)
				{
					diag.UncalledSetArgTicks += setArg;
					diag.UncalledNestTicks += nest;
					diag.UncalledJumpTicks += jump;
				}
				else
				{
					diag.MainSetArgTicks += setArg;
					diag.MainNestTicks += nest;
					diag.MainJumpTicks += jump;
				}
			}
			else
			{
				SetArgument(label, ctx);
				NestCheck(label, ctx);
				SetJumpTo(label, ctx);
			}
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
			scanLine(null!, ctx);
		}
		label.ThreePassed = true; // b2-irdiskcache：三遍跑完（含失败已标错）——主循环按此跳过重复三遍。

	}

	internal void SetArgument(FunctionLabelLine label, ParseContext ctx)
	{
		//1周目/3周
		//引数の解析とか
		LogicalLine nextLine = label;
		bool inMethod = label.IsMethod;
		while (true)
		{
			nextLine = nextLine.NextLine;
			scanLine(nextLine, ctx);
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
			if (Config.Config.NeedReduceArgumentOnLoad || analysisMode || func.Function.IsForceSetArg())
				ArgumentParser.SetArgumentTo(func);
		}
	}

	internal void NestCheck(FunctionLabelLine label, ParseContext ctx)
	{
		//2周目/3周
		//IF-ELSEIF-ENDIF、REPEAT-RENDの対応チェックなど
		//PRINTDATA系もここでチェック
		// 决策三-a：复用 ErbLoader 实例字段，避免每函数分配 3 个容器（1000 函数 = 3000+ 次分配）
		// erb-loader-parallel 08 D5#5：未调用遍并行期间实例字段复用会互踩——改局部实例
		//（eraTW 63k label × 小集合，分配成本可忽略）；串行路径保持字段复用。
		LogicalLine nextLine = label;
		List<InstructionLine> tempLineList;
		Stack<InstructionLine> nestStack;
		Stack<InstructionLine> SelectcaseStack;
		if (ctx.IsParallel)
		{
			tempLineList = [];
			nestStack = new();
			SelectcaseStack = new();
		}
		else
		{
			tempLineList = tempLineListReuse;
			nestStack = nestStackReuse;
			SelectcaseStack = selectcaseStackReuse;
			tempLineList.Clear();
			nestStack.Clear();
			SelectcaseStack.Clear();
		}
		InstructionLine pairLine = null!;
		while (true)
		{
			nextLine = nextLine.NextLine;
			scanLine(nextLine, ctx);
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
			// 方言结构化 TRYC 指令（FunctionCode.__NULL__，如 TRYCJUMPSTR/TRYCCALLSTR）按 flag 参与
			// CATCH 嵌套配对（实现票 20）。原生 TRYC* 有真实 FunctionCode，仍由下面的 switch 处理。
			if (func!.FunctionCode == FunctionCode.__NULL__ && func.Function.IsTryC())
			{
				nestStack.Push(func);
				continue;
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
						//コンパイル時ジャンプテーブル構築（条件を満たさない場合は null → 実行時に線形スキャンへフォールバック）。
						//eager と lazy 物化（ReparseFunction）は同一の nestCheck 経路を共有するため、物化関数は自動で建表される。
						selectLine.SelectCaseJumpTable = SelectCaseJumpTable.TryBuild(selectLine, exm);
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
					// 按 flag 判定（实现票 20）：同时覆盖原生 TRYC* 与方言 __NULL__ TRYC 指令（TRYCJUMPSTR/TRYCCALLSTR）。
					if (pairLine == null
						|| !pairLine.Function.IsTryC())
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

	internal void SetJumpTo(FunctionLabelLine label, ParseContext ctx)
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
			scanLine(func, ctx);

			if (func.Function.Instruction != null)
			{
				string FunctionNotFoundName = null!;
				try
				{
					// D5#3：callform 标记改为返回值，由 MarkUseCallForm 决定落点——
					// 并行未调用清扫期间写入 per-label 产物，不再直写本实例字段。
					if (func.Function.Instruction.SetJumpTo(func, depth, ref FunctionNotFoundName))
						MarkUseCallForm(ctx);
				}
				catch (CodeEE e)
				{
					ParserMediator.Warn(e.Message, func, 2, true, false);
					continue;
				}
				if (FunctionNotFoundName != null)
				{
					if (!analysisMode)
						PrintFunctionNotFoundWarning(string.Format(trerror.NotDefinedFunc.Text, FunctionNotFoundName), func, 2, true, ctx);
					else
						PrintFunctionNotFoundWarning(FunctionNotFoundName, func, 2, true, ctx);
				}
				continue;
			}
			if (func.FunctionCode == FunctionCode.TRYCALLLIST || func.FunctionCode == FunctionCode.TRYJUMPLIST)
				MarkUseCallForm(ctx);
		}
	}

	/// <summary>
	/// erb-loader-parallel 08 D5#3：记录「用到 callform 系」，按显式 ctx 落点而非直写实例字段
	/// （ADR-0027 第二刀：落点由 <paramref name="ctx"/> 决定，原读 <c>SweepArtCurrent</c> ThreadLocal）。
	/// 主循环（串行/回放）下 <see cref="ParseContext.Art"/> 为 null → 直写 <see cref="useCallForm"/>，
	/// 与改动前完全一致；未调用并行清扫期间写入 per-label 产物
	/// <see cref="UncalledSweepArt.UseCallForm"/>，由回放段在主线程合并。
	/// 并行区因此不再有对本实例字段的写入（原为 ref 直写，构成共享写）。
	/// </summary>
	internal void MarkUseCallForm(ParseContext ctx)
	{
		UncalledSweepArt? art = ctx.Art;
		if (art != null)
			art.UseCallForm = true;
		else
			onUseCallForm();
	}
}

internal sealed class ParseScriptDiag
{
	internal readonly Stopwatch Sw = new();
	/// <summary>当前 ParseFunctionWithCatch 的三步耗时计入哪一桶（主循环=false / 未调用遍历=true）。</summary>
	internal bool InUncalled;
	internal long MainSetArgTicks, MainNestTicks, MainJumpTicks;
	internal long UncalledSetArgTicks, UncalledNestTicks, UncalledJumpTicks;
}
