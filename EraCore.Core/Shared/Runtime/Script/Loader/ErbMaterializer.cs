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
using System.Diagnostics;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Loader;


// ADR-0047（E 组 · C-E2a）：物化的两条输入流从 ErbLoader 外移。放在本文件而不是单独文件，
// 是因为它们是 ErbMaterializer 的输入抽象，下一步引擎搬入时同处一处。
/// <summary>b2-irdiskcache：「行文本流」抽象（design §5.3）——源回读与 blob 文本两条实现共用同一物化循环，物化产物等价性收敛为「输入流 == 源文件启用行」一件事。</summary>
internal interface IFunctionLineSource
{
	/// <summary>取下一启用行（blob 源不回源、不 rename、不重 [ 预处理——已在产生端过滤）。行流在物化循环内驱动 ppstate。</summary>
	CharStream? ReadEnabledLine(bool disabled);
	/// <summary>最近一次返回行的行号（对齐 EraStreamReader.LineNo 语义）。</summary>
	int LineNo { get; }
	string Filename { get; }
	/// <summary>
	/// 该流是否只含单个函数（blob 源 = 单函数块）。true 时「@名」在本流内唯一，定位无需行号消歧
	/// （IR 索引行的行号与 blob 首行行号可能不一致，见实现票 31）。源文件流 = false，需按行号区分同名多定义。
	/// </summary>
	bool SingleFunction { get; }
}

/// <summary>源回读后端：包一层 <see cref="EraStreamReader"/>（Preload 缓存 / DirAccessor / EncodingHandler，ADR-0023 决策二回读通道）。</summary>
internal sealed class SourceLineSource : IFunctionLineSource
{
	private readonly EraStreamReader _reader;
	public SourceLineSource(EraStreamReader reader) => _reader = reader;
	public string Filename => _reader.Filename;
	public int LineNo => _reader.LineNo;
	public bool SingleFunction => false;
	public CharStream? ReadEnabledLine(bool disabled) => _reader.ReadEnabledLine(disabled);
}

/// <summary>IR blob 后端：回放函数块内已过滤的启用行（行号 + 文本），不做 rename / SkipWhiteSpace 之外的加工。</summary>
internal sealed class BlobLineSource : IFunctionLineSource
{
	private readonly IrLine[] _lines;
	private readonly string _filename;
	private int _index;
	private int _lineNo;
	public BlobLineSource(IrLine[] lines, string filename) { _lines = lines; _filename = filename; }
	public string Filename => _filename;
	public int LineNo => _lineNo;
	public bool SingleFunction => true;
	public CharStream? ReadEnabledLine(bool disabled)
	{
		if (_index >= _lines.Length)
			return null;
		var l = _lines[_index++];
		_lineNo = l.LineNo;
		var st = new CharStream(l.Text);
		LexicalAnalyzer.SkipWhiteSpace(st); // 对齐 ReadEnabledLine 产出的指向（源侧在 ReadEnabledLine 内已 Skip）
		return st;
	}
}

/// <summary>ADR-0047：IR blob 读取回调——命中返回 true 并给 blob，未命中/不可用返回 false（宿主侧做 <c>_ir</c> 索引查找 + 读账）。</summary>
internal delegate bool TryReadBlobHandler(string logical, string labelName, int lineNo, out IrBlob blob);

/// <summary>
/// ADR-0047（E 组 · C-E2b）：惰性物化引擎——「按需把一个标签桩重建成完整函数体」。
/// <para>
/// 三态语义（原样保留，<b>不可压成 bool</b>）：<see cref="MaterializeFunction"/> 返回 null 既可能是
/// 「未到达目标函数」也可能是「流打不开」；<see cref="MaterializeLabel"/> 另有「源路径不可得 → 占位报错」一态。
/// 幂等：已物化（非桩）直接返回；<c>IsStub=false</c> 是停止重试的标记。
/// </para>
/// <para>
/// <b>接缝</b>：字段名刻意与宿主原标识符同名（<c>env</c> / <c>idDic</c> / <c>labelDic</c> / <c>parseLabel</c> /
/// <c>setLabelsArg</c> / <c>scanLine</c>），使方法体除三处 IR 站点外<b>零改动</b>；三处 IR 站点改为
/// <c>tryReadBlob(...)</c> 回调（宿主持有 <c>_ir</c> 与 <c>ReadBlobMeasured</c> 的读账）。
/// </para>
/// </summary>
internal sealed class ErbMaterializer
{
	internal readonly struct Context
	{
		internal readonly LoaderEnv Env;
		internal readonly EmueraConsole Output;
		internal readonly Action<LogicalLine> SetScanLine;
		internal readonly IdentifierDictionary IdDic;
		internal readonly LabelDictionary LabelDic;
		internal readonly IReadOnlyDictionary<string, string> FilePathByLogical;
		internal readonly TryReadBlobHandler TryReadBlob;
		internal readonly Func<MaterializeDiag?> MaterializeDiag;
		internal readonly Action<FunctionLabelLine> ParseLabel;
		internal readonly Action SetLabelsArg;
		internal readonly Action<LogicalLine, ParseContext> ScanLine;
		internal readonly ErbThreePassParser ThreePass;

		internal Context(LoaderEnv env, EmueraConsole output, Action<LogicalLine> setScanLine, IdentifierDictionary idDic, LabelDictionary labelDic,
			IReadOnlyDictionary<string, string> filePathByLogical, TryReadBlobHandler tryReadBlob,
			Func<MaterializeDiag?> materializeDiag, Action<FunctionLabelLine> parseLabel,
			Action setLabelsArg, Action<LogicalLine, ParseContext> scanLine, ErbThreePassParser threePass)
		{
			Env = env;
			Output = output;
			SetScanLine = setScanLine;
			IdDic = idDic;
			LabelDic = labelDic;
			FilePathByLogical = filePathByLogical;
			TryReadBlob = tryReadBlob;
			MaterializeDiag = materializeDiag;
			ParseLabel = parseLabel;
			SetLabelsArg = setLabelsArg;
			ScanLine = scanLine;
			ThreePass = threePass;
		}
	}

	readonly LoaderEnv env;
	readonly EmueraConsole output;
	readonly Action<LogicalLine> setScanLine;
	readonly IdentifierDictionary idDic;
	readonly LabelDictionary labelDic;
	readonly IReadOnlyDictionary<string, string> filePathByLogical;
	readonly TryReadBlobHandler tryReadBlob;
	readonly Func<MaterializeDiag?> materializeDiag;
	readonly Action<FunctionLabelLine> parseLabel;
	readonly Action setLabelsArg;
	readonly Action<LogicalLine, ParseContext> scanLine;
	readonly ErbThreePassParser threePass;

	internal ErbMaterializer(in Context ctx)
	{
		env = ctx.Env;
		output = ctx.Output;
		setScanLine = ctx.SetScanLine;
		idDic = ctx.IdDic;
		labelDic = ctx.LabelDic;
		filePathByLogical = ctx.FilePathByLogical;
		tryReadBlob = ctx.TryReadBlob;
		materializeDiag = ctx.MaterializeDiag;
		parseLabel = ctx.ParseLabel;
		setLabelsArg = ctx.SetLabelsArg;
		scanLine = ctx.ScanLine;
		threePass = ctx.ThreePass;
	}
	/// <summary>
	/// 02 抽取 + b2-irdiskcache 统一：单函数体重解析（物化）引擎。给定未物化标签桩 <paramref name="stub"/>
	/// 与一条启用行流 <paramref name="src"/>，定位本函数行区间，重建 label + 行链（复用 <see cref="LogicalLineParser"/>），
	/// 再跑（IR 命中桩先行）parseLabel + setArgument/nestCheck/setJumpTo 三遍。
	/// <list type="bullet">
	/// <item>B1 源后背桩（<see cref="FunctionLabelLine.IrBacked"/> = false）：签名已在惰性生成期由源 # 行处理齐（Arg/Def/私有变量/IsMethod/LocalLength 与 eager 逐位一致），重解析只重建函数体行链，行为与既有 ReparseFunction 逐位一致。</item>
	/// <item>IR 命中桩（IrBacked = true）：最小桩签名留空，此处从 blob 文本重建——@ 行重建 wc（parseLabel 待用）、# 行重跑 ParseSharpLine（私有变量/IsMethod/LocalLength）、随后 parseLabel + resizeLocalVars。</item>
	/// </list>
	/// 返回标签（in-place 于桩上重建，不修改引用无重路由）；未到达目标函数 / 流打不开 → null（三态占位）。幂等。
	/// </summary>
	internal FunctionLabelLine? MaterializeFunction(FunctionLabelLine stub, IFunctionLineSource src)
	{
		if (stub.Position == null)
			return null;
		if (stub is InvalidLabelLine)
		{
			// 非法标签桩：无 body 可重建，保持占位（不应物化）。
			return null;
		}
		// 签名是否还需重建：IR 桩（或签名已由 EnsureCalleeSignature 预计算则 Arg!=null 跳过），源后背桩 Arg 已就位。
		bool needSignature = stub.Arg == null;
		// 定位本函数：**源文件流**按「@标签名 + 行号」双匹配——名字在单文件内可能重复（eraTW 在同一文件
		// 二次定义 @EVENTLOAD，是事件函数的合法叠加写法），只按名字会命中首个同名定义的 body → 该桩因物化期
		// 跳过 `#` 行而缺私有变量 → `FOR 索引` 等语句级解析报「この関数中では定義されていません」（实现票 31）。
		// **blob 流**（SingleFunction）只含单函数、@ 名唯一，按名即可（IR 索引行的行号与 blob 首行行号可能不一致，
		// 不能拿来做行号消歧）；源文件流唯一名时行号恒等于旧行为，故对既有路径逐位无影响。
		StringComparer nameEq = Config.Config.StrComper;
		// 直接以「桩」作为重解析目标（不新建瞬态标签）：body 绑定桩的私有变量 token，与参数赋值（写桩私有变量）
		// 一致（Issue 06：此前新建标签导致 ReduceArgumentOnLoad=NO 运行期解析读空、YES 解析期绑错 token）。
		FunctionLabelLine? label = null;
		LogicalLine tail = null!;
		// [ 预处理评估：源路径重演 [SKIPSTART][SKIPEND] 等保「故意不平衡」块不被误解析；blob 源无 [ 行则 no-op。
		// ADR-0039：惰性物化路径没有 per-file 缓冲，显式指定全局目标（与改动前"立即进全局"逐位一致）。
		var ppstate = new PPState(env, idDic, ParserMediator.GlobalWarningTarget);
		MaterializeDiag? mdiag = materializeDiag();
		// issue 07 P1b：词法归属切换（mdiag == null 时完全不碰静态字段——运行期物化路径零侵入）。
		ParserProbeScope EnterScope(ParserProbeScope sc)
		{
			if (mdiag == null)
				return ParserProbeScope.None;
			ParserProbeScope prev = ParserProbe.Scope;
			ParserProbe.Scope = sc;
			return prev;
		}
		void ExitScope(ParserProbeScope prev)
		{
			if (mdiag != null)
				ParserProbe.Scope = prev;
		}
		// issue 07 P1a′：段内分配账读取（与 GcSnapshot 同 API；mdiag == null 时零调用）。
		long A() => mdiag != null ? GC.GetTotalAllocatedBytes(precise: false) : 0L;
		CharStream? line;
		while (true)
		{
			long aRead = A();
			long tRead = mdiag != null ? Stopwatch.GetTimestamp() : 0L;
			line = src.ReadEnabledLine(ppstate.Disabled);
			if (line == null)
				break;
			if (mdiag != null)
			{
				mdiag.ReadTicks += Stopwatch.GetTimestamp() - tRead;
				mdiag.ReadAlloc += A() - aRead;
				mdiag.Lines++;
			}
			CharStream st = line;
			var position = new ScriptPosition(src.Filename, src.LineNo);
			// [ 预处理指令（与主加载同款，先于 @/$ 判定）
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
			if (ppstate.Disabled)
				continue;
			if (st.Current == '@')
			{
				if (label == null)
				{
					// 解析 @ 行仅用于匹配标签名；body 直接解析进桩（见上方说明）。
					long aTree = A();
					long tTree = mdiag != null ? Stopwatch.GetTimestamp() : 0L;
					ParserProbeScope ps = EnterScope(ParserProbeScope.Tree);
					LogicalLine? atLine = LogicalLineParser.ParseLabelLine(st, position, output, null);
					ExitScope(ps);
					if (mdiag != null)
					{
						mdiag.TreeTicks += Stopwatch.GetTimestamp() - tTree;
						mdiag.TreeAlloc += A() - aTree;
						mdiag.LabelLines++;
					}
					if (atLine is FunctionLabelLine lbl
						&& nameEq.Equals(lbl.LabelName, stub.LabelName)
						&& (src.SingleFunction
							|| (lbl.Position != null && lbl.Position.Value.LineNo == stub.Position.Value.LineNo)))
					{
						label = stub;
						// IR 命中桩：从 @ 行重建 wc（否则 parseLabel 的 PopRowArgs 得空）。B1 桩 wc 生成期已有。
						if (needSignature)
							stub.SetRowArgs(lbl.PopRowArgs());
						// @ 行 ParentLabelLine 须自指（生成期由 addLine 置位）：scaningLine=本函数时
						// GetVariableToken 经 line.ParentLabelLine.GetPrivateVariable 解析私有变量/参数，
						// 否则对 IR 桩扫描线命中后 ParentLabelLine=null → 私有变量落 privateDimList → 误报。
						label.ParentLabelLine = label;
						tail = stub;
					}
					continue;
				}
				break; // 下一个函数 = 本函数体边界
			}
			if (label == null)
				continue; // 尚未到达目标函数
			if (st.Current == '$')
			{
				long aTree = A();
				long tTree = mdiag != null ? Stopwatch.GetTimestamp() : 0L;
				ParserProbeScope ps = EnterScope(ParserProbeScope.Tree);
				LogicalLine? dollarLine = LogicalLineParser.ParseLabelLine(st, position, output, null);
				ExitScope(ps);
				if (mdiag != null)
				{
					mdiag.TreeTicks += Stopwatch.GetTimestamp() - tTree;
					mdiag.TreeAlloc += A() - aTree;
					mdiag.DollarLines++;
				}
				if (dollarLine is GotoLabelLine g)
				{
					g.ParentLabelLine = label;
					// 惰性下桩跳过 $ 标签、加载期不注册，物化必须补注册（否则 GOTO SetJumpTo 经 GetLabelDollar 解析失败）。
					labelDic.AddLabelDollar(g);
					tail.NextLine = g;
					tail = g;
				}
				continue;
			}
			if (st.Current == '#')
			{
				// B1 桩：签名已在生成期处理齐（#DIM/#LOCALSIZE/…），重解析只重建函数体；重复 AddPrivateVariable 会误报。
				// IR 命中桩：签名留空（或未经 EnsureCalleeSignature 预计算），须重跑 ParseSharpLine 重建（私有变量/IsMethod/LocalLength/…）。
				if (needSignature)
				{
					long aTree = A();
					long tTree = mdiag != null ? Stopwatch.GetTimestamp() : 0L;
					ParserProbeScope ps = EnterScope(ParserProbeScope.Tree);
					List<string> a = [], b = [];
					LogicalLineParser.ParseSharpLine(stub, st, position, a, b);
					ExitScope(ps);
					if (mdiag != null)
					{
						mdiag.TreeTicks += Stopwatch.GetTimestamp() - tTree;
						mdiag.TreeAlloc += A() - aTree;
					}
				}
				if (mdiag != null)
					mdiag.SharpLines++;
				continue;
			}
			long aBody = A();
			long tBody = mdiag != null ? Stopwatch.GetTimestamp() : 0L;
			ParserProbeScope psBody = EnterScope(ParserProbeScope.Tree);
			var parsed = LogicalLineParser.ParseLine(st, position, output, label);
			ExitScope(psBody);
			if (mdiag != null)
			{
				mdiag.TreeTicks += Stopwatch.GetTimestamp() - tBody;
				mdiag.TreeAlloc += A() - aBody;
			}
			if (parsed == null)
				continue;
			if (parsed is InvalidLine)
				parsed.IsError = true;
			else if (JSONConfig.Data.UseNewRandom && parsed is InstructionLine instruction)
			{
				// 与 GenerateErbFileCore 同款 RANDOMIZE/DUMPRAND/INITRAND 抑制警告（结构无关，仅对齐现有发射）
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
				}
			}
			parsed.ParentLabelLine = label;
			tail.NextLine = parsed;
			tail = parsed;
		}
		if (label == null)
			return null;
		if (mdiag != null)
			mdiag.Labels++;
		tail.NextLine = new NullLine();
		// B1 桩不重跑 parseLabel（生成期 setLabelsArg 已解析 Arg/Def）；IR 桩在这里补 Arg/Def + 扩 ARG/ARGS 槽。
		// setScanLine(label) 提供 scan-line 上下文（对齐 eager setLabelsArg），供三步/parseLabel 内私有变量解析。
		setScanLine(label);
		// Issue 06：对齐 eager ParseFunctionWithCatch 的 finally 收尾——nestCheck 对终止 NullLine 也会 ScanLine，
		// 不重置则运行期 scaningLine 残留尾部 NullLine → 误报「この関数中では定義されていません」。三步/异常一并复位。
		try
		{
			if (needSignature)
			{
				long aLabel = A();
				long tLabel = mdiag != null ? Stopwatch.GetTimestamp() : 0L;
				ParserProbeScope psLabel = EnterScope(ParserProbeScope.Label);
				parseLabel(label);
				ExitScope(psLabel);
				// 对齐 SortLabels 的 resizeLocalVars：IR 桩 gen 期按 ArgLength=0 建槽（SortLabels 对非事件把槽建为 0），
				// 物化后按实际值扩容，否则运行期 ARGx 绑定到 size-0 槽。
				if (!label.IsEvent)
				{
					if (label.ArgLength > 0)
						idDic.resizeLocalVars("ARG", label.LabelName, label.ArgLength);
					if (label.ArgsLength > 0)
						idDic.resizeLocalVars("ARGS", label.LabelName, label.ArgsLength);
				}
				if (mdiag != null)
				{
					mdiag.LabelTicks += Stopwatch.GetTimestamp() - tLabel;
					mdiag.LabelAlloc += A() - aLabel;
					mdiag.SignatureLabels++;
				}
			}
			// ADR-0027 决策三：本方法经运行期接缝（MaterializeOnRuntime / EnsureCalleeSignature）可达，
			// 按决策一签名冻结 ⇒ 不穿参，经 ParseContext 的单一访问器取本线程 ctx（在并行 worker 线程上
			// 取到的即该 worker 的 per-label ctx——语义与原 _sweepParallelActive 分流逐位一致）。
			ParseContext ctx = ParseContext.CurrentOrSerial;
			if (mdiag == null)
			{
				threePass.SetArgument(label, ctx);
				threePass.NestCheck(label, ctx);
				threePass.SetJumpTo(label, ctx);
			}
			else
			{
				// issue 07 P1a 段④：三步合计 + 三子项分列（对齐既有 parsescript_diag steps 口径）。
				long aThree = A();
				long tSetArg = Stopwatch.GetTimestamp();
				ParserProbeScope psThree = EnterScope(ParserProbeScope.Three);
				threePass.SetArgument(label, ctx);
				long tNest = Stopwatch.GetTimestamp();
				threePass.NestCheck(label, ctx);
				long tJump = Stopwatch.GetTimestamp();
				threePass.SetJumpTo(label, ctx);
				long tEnd = Stopwatch.GetTimestamp();
				ExitScope(psThree);
				mdiag.SetArgTicks += tNest - tSetArg;
				mdiag.NestTicks += tJump - tNest;
				mdiag.JumpTicks += tEnd - tJump;
				mdiag.ThreeTicks += tEnd - tSetArg;
				mdiag.ThreeAlloc += A() - aThree;
			}
		}
		finally
		{
			scanLine(null!, ParseContext.CurrentOrSerial);
		}
		label.ThreePassed = true; // b2-irdiskcache：三遍完成，供主循环跳过重复处理（加载期 CALL arg 转换已提前物化本标签时）
		return label;
	}

	/// <summary>
	/// b2-irdiskcache：非递归「签名物化」——只重建桩的 Arg/Def/私有变量/IsMethod（@ + # 行 → parseLabel），
	/// <b>不解析函数体、不跑 setJumpTo</b>。供加载期 CALL/式中函数 arg 转换（ConvertArg 读 callee.Arg）用，
	/// 避免「物化 A → A.setJumpTo → CALL B → 物化 B → …」的无界递归栈溢出。幂等（Arg != null 即已就位）。
	/// </summary>
	internal void ComputeSignature(FunctionLabelLine stub)
	{
		// issue 07 P1a：签名预算分账（R1 定价用）。运行期 / 未开窗 → 零开销直通，路径逐位不变。
		MaterializeDiag? md = materializeDiag();
		if (md == null)
		{
			ComputeSignatureCore(stub);
			return;
		}
		long t0 = Stopwatch.GetTimestamp();
		long a0 = GC.GetTotalAllocatedBytes(precise: false);
		ParserProbeScope prevScope = ParserProbe.Scope;
		ParserProbe.Scope = ParserProbeScope.Signature;
		try
		{
			ComputeSignatureCore(stub);
		}
		finally
		{
			ParserProbe.Scope = prevScope;
			md.SigTicks += Stopwatch.GetTimestamp() - t0;
			md.SigAlloc += GC.GetTotalAllocatedBytes(precise: false) - a0;
			md.SigCalls++;
		}
	}

	/// <summary>签名预算本体（原 <c>ComputeSignature</c> 实体，未改动一行——包装只为计时）。</summary>
	private void ComputeSignatureCore(FunctionLabelLine stub)
	{
		if (stub.Arg != null || !stub.IsStub)
			return;
		string logical = stub.Position?.Filename ?? "";
		if (logical.Length == 0)
			return;
		// IR 优先（blob 后台）；blob 读失败 / 未命中回落到源。行号入键以消歧同文件同名多定义。
		try
		{
			if (tryReadBlob(logical, stub.LabelName, stub.Position!.Value.LineNo, out var blob))
			{
				ComputeSignatureLines(stub, new BlobLineSource(blob.Lines, stub.Position!.Value.Filename));
				return;
			}
		}
		catch
		{
			// blob 损坏 → 回源签名（不在此抛，避免 arg 转换 NRE）。
		}
		if (filePathByLogical.TryGetValue(logical, out string? physical) && !string.IsNullOrEmpty(physical))
		{
			using var eReader = new EraStreamReader(Config.Config.UseRenameFile && ParserMediator.RenameDic != null);
			if (eReader.OpenOnCache(physical, logical))
				ComputeSignatureLines(stub, new SourceLineSource(eReader));
		}
	}

	private void ComputeSignatureLines(FunctionLabelLine stub, IFunctionLineSource src)
	{
		CharStream? line;
		while ((line = src.ReadEnabledLine(false)) != null)
		{
			CharStream st = line;
			var position = new ScriptPosition(src.Filename, src.LineNo);
			// 签名只需 @ + # 行；body（指令/$/[[…]]）忽略；[ 预处理行忽略（签名行不受其禁用，生成期已过滤）。
			if (st.Current == '@')
			{
				if (LogicalLineParser.ParseLabelLine(st, position, output, null) is FunctionLabelLine lbl
					&& Config.Config.StrComper.Equals(lbl.LabelName, stub.LabelName))
				{
					stub.SetRowArgs(lbl.PopRowArgs());
					stub.ParentLabelLine = stub;
					EnsureEventSystemFlags(stub);
				}
				continue;
			}
			if (st.Current == '#')
			{
				List<string> a = [], b = [];
				LogicalLineParser.ParseSharpLine(stub, st, position, a, b);
				continue;
			}
			// 其余行（函数体）跳过——签名物化不解析 body。
		}
		if (stub.Arg == null)
		{
			// b2-irdiskcache：签名物化是嵌套在「调用方 arg / 式中函数解析」内的辅助——必须保留并恢复调用方
			// scaningLine。若像全量物化那样 setScanLine(stub) 后 finally scanLine(null!)，会把调用方
			// Process.scaningLine 清空：其正在解析的表达式（如 `&& !YANUSHI` / `THING(LOCAL)` / `ARG`）
			// 续读时 scaningLine=null → 私有变量/LOCAL/ARG 全部误报「未在此函数中定义/不存在执行中的函数」。
			// round1(B1) 无此问题：源后背桩已有 Arg → EnsureCalleeSignature 提前 no-op，不进入本分支。
			LogicalLine? prevScan = GlobalStatic.Process?.GetScaningLine();
			setScanLine(stub);
			try
			{
				parseLabel(stub);
				if (!stub.IsEvent)
				{
					if (stub.ArgLength > 0)
						idDic.resizeLocalVars("ARG", stub.LabelName, stub.ArgLength);
					if (stub.ArgsLength > 0)
						idDic.resizeLocalVars("ARGS", stub.LabelName, stub.ArgsLength);
				}
			}
			finally
			{
				scanLine(prevScan!, ParseContext.CurrentOrSerial);
			}
			// 注意：不动 IsStub（仍为桩，函数体留待主循环物化）。
		}
	}

	/// <summary>按标签名补全事件/系统标志与 Depth（对齐 ParseLabelLine 的判定）。</summary>
	private static void EnsureEventSystemFlags(FunctionLabelLine stub)
	{
		if (IdentifierDictionary.IsEventLabelName(stub.LabelName))
		{
			stub.IsEvent = true;
			stub.IsSystem = true;
			if (stub.Depth != 0)
				stub.Depth = 0;
		}
		else if (IdentifierDictionary.IsSystemLabelName(stub.LabelName))
		{
			stub.IsSystem = true;
			if (stub.Depth != 0)
				stub.Depth = 0;
		}
	}

	/// <summary>载入函数重解析（源后背，B1 兼容入口）：重开源文件，以桩为目标的单函数重解析。源不可读/桩行不在 → null。</summary>
	internal FunctionLabelLine? ReparseFunction(FunctionLabelLine stub, string filepath)
	{
		var pos = stub.Position;
		if (pos == null)
			return null;

		using var eReader = new EraStreamReader(Config.Config.UseRenameFile && ParserMediator.RenameDic != null);
		if (!eReader.OpenOnCache(filepath, pos.Value.Filename))
			return null; // 三态：源文件打不开（被移动/删除）→ 占位，不抛
		return MaterializeFunction(stub, new SourceLineSource(eReader));
	}

	/// <summary>
	/// b2-irdiskcache：IR 后背物化——以 blob 文本流作为 <see cref="MaterializeFunction"/> 输入（design §5.3 / 对账项 C）。
	/// 读 blob 失败抛异常（调用方转回退/占位）。单函数 parse 上复用既有循环，与源后背产物逐一等价。
	/// </summary>
	private FunctionLabelLine? ReparseFunctionFromIr(FunctionLabelLine stub, IrBlob blob)
	{
		string filename = stub.Position?.Filename ?? "";
		return MaterializeFunction(stub, new BlobLineSource(blob.Lines, filename));
	}

	/// <summary>
	/// lazy-function-load（B 线 B1）：就地物化——把惰性桩 <paramref name="stub"/> 的行链替换为
	/// <see cref="ReparseFunction"/> 产出的真实函数体（in-place 更新桩对象本身，labelDic / JumpTo 等引用
	/// 自动一致，无需重路由字典）。幂等：已物化（非桩）直接返回。
	/// 失败路径三态（源打不开 / 桩行已不在文件中 → ReparseFunction 返回 null）降级为占位报错：
	/// 标记 IsError+ErrMes、清桩标记（停止重试），不抛新异常——该函数按「已定义但解析不可得」处理。
	/// </summary>
	internal void MaterializeLabel(FunctionLabelLine stub)
	{
		if (!stub.IsStub)
			return; // 幂等
		// b2-irdiskcache：IR 命中桩 → blob 优先物化，blob 失败回退源（design §5.3，User Story 8「静默回退」）。
		if (stub.IrBacked)
		{
			MaterializeLabelFromIr(stub);
			return;
		}
		// 以下为源后背（B1）原路径：桩为源生成（含完整签名）。
		string logical = stub.Position?.Filename ?? "";
		if (logical.Length == 0
			|| !filePathByLogical.TryGetValue(logical, out string? physical) || string.IsNullOrEmpty(physical))
		{
			DegradeToPlaceholderError(stub);
			return; // 三态①：源路径不可得 → 占位报错
		}
		FunctionLabelLine? reparse;
		try
		{
			reparse = ReparseFunction(stub, physical);
		}
		catch (Exception exc)
		{
			// 物化期单函数重解析不应向调用方抛新异常（「首调用无新异常」门禁）——等价于 eager
			// ParseFunctionWithCatch 的 catch：告警 + 降级为占位报错（三态③）。
			string errmes = exc is EmueraException ? exc.Message : exc.GetType().ToString() + ":" + exc.Message;
			ParserMediator.Warn(string.Format(trerror.FuncAnalysisError.Text, stub.LabelName, errmes), stub, 2, true, false, exc.StackTrace!);
			DegradeToPlaceholderError(stub);
			return;
		}
		if (reparse == null)
		{
			DegradeToPlaceholderError(stub);
			return; // 三态②：桩行已不在文件中 → 占位报错
		}
		// ReparseFunction 直接在桩上重建行链（ParentLabelLine 已指向桩自身、$ 标签已注册、body 变量引用
		// 绑定桩的私有变量 token），此处只需复位桩标记；标签头（Position/Arg/Def/IsMethod/私有变量）在惰性
		// 生成期已与 eager 逐位一致，LocalLength/LocalsLength 亦已由生成期 #LOCALSIZE / SortLabels 置位。
		stub.NextLine = reparse.NextLine;
		stub.IsStub = false;
	}

	/// <summary>b2-irdiskcache：IR 命中桩物化——blob 优先，blob 校验失败/缺失回源（ReparseFunction 源后背亦会重建签名）。三态降级占位不抛新异常。</summary>
	private void MaterializeLabelFromIr(FunctionLabelLine stub)
	{
		string logical = stub.Position?.Filename ?? "";
		string? physical = null;
		if (logical.Length > 0)
			filePathByLogical.TryGetValue(logical, out physical);

		// 1) blob 优先（行号入键以消歧同文件同名多定义）
		if (logical.Length > 0)
		{
			FunctionLabelLine? viaIr = null;
			try
			{
				if (tryReadBlob(logical, stub.LabelName, stub.Position!.Value.LineNo, out var blob))
					viaIr = ReparseFunctionFromIr(stub, blob);
			}
			catch (Exception exc)
			{
				EmueraLog.Warn("ErbIrCache", $"IR blob corrupt, fallback to source: {stub.LabelName} ({exc.GetType().Name})");
				viaIr = null;
			}
			if (viaIr != null)
			{
				stub.NextLine = viaIr.NextLine;
				stub.IsStub = false;
				stub.IrBacked = false;
				return;
			}
		}

		// 2) 回退源路径（blob 损坏/缺失 → 源后背高可信，回源比占位更符合 User Story 8）
		if (!string.IsNullOrEmpty(physical))
		{
			FunctionLabelLine? viaSource = null;
			try
			{
				viaSource = ReparseFunction(stub, physical);
			}
			catch (Exception exc)
			{
				string errmes = exc is EmueraException ? exc.Message : exc.GetType().ToString() + ":" + exc.Message;
				ParserMediator.Warn(string.Format(trerror.FuncAnalysisError.Text, stub.LabelName, errmes), stub, 2, true, false, exc.StackTrace!);
				viaSource = null;
			}
			if (viaSource != null)
			{
				// 源回退已重建签名（ReparseFunction 对 IrBacked 桩走 rebuildSignature），仅复位标记。
				stub.NextLine = viaSource.NextLine;
				stub.IsStub = false;
				stub.IrBacked = false;
				return;
			}
		}
		DegradeToPlaceholderError(stub);
	}

	/// <summary>物化失败降级为占位报错：清桩标记（停止重试）+ 一次性标 IsError/ErrMes，不抛新异常（ADR-0023 决策二三态先例）。</summary>
	private static void DegradeToPlaceholderError(FunctionLabelLine stub)
	{
		stub.IsStub = false;
		if (!stub.IsError)
		{
			stub.IsError = true;
			stub.ErrMes = trerror.CalledFailedFunc.Text;
		}
	}
}
