using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0034/0035 收尾：<see cref="ErbFileAssembler"/> 的<b>模块边界</b>等价性锚点。
/// <para>
/// <b>与 <c>ErbLoaderTests.LoadErbDir_serial_fallback_equivalent_to_parallel</c> 的分工</b>：
/// 那个在<b>整轮装载</b>层比——经 <c>LoadErbDir</c>，调度器（窗口/worker/取消）也在被测范围内，
/// 变红时无法区分"调度器坏了"还是"装配器坏了"。本测试直接驱动 <c>Generate</c>/<c>Merge</c>，
/// 调度器不在路径上，失败即定位到装配器。
/// </para>
/// <para>
/// <b>本测试补齐的维度</b>：<c>isOnlyEvent</c>、<c>art.OnlyDeclarations</c>、<c>enabledLineCount</c>（档 3
/// 回传的标量），以及<b>占位槽机制本身</b>（见 <see cref="Deferred_mode_seeds_placeholder_slots"/>）——
/// 后者在任何其他测试里都没有覆盖。
/// </para>
/// <para>
/// <b>陷阱（本次实测踩到，已修正）</b>：<c>art.Warnings</c> 必须在 <c>Merge</c> <b>之前</b>读，
/// 但延后模式下此刻拿到的是<b>未解析的占位槽</b>——真正的警告要等 <c>Merge</c> 内部
/// <c>ResolvePendingCrossFileChecks</c> 替换后才存在，而 <c>EmitWarningBuffer</c> 末尾会
/// <c>buffer.Clear()</c>。故两条路径只能对称地在<b>发出之后</b>比警告。
/// </para>
/// </summary>
[Collection("LoaderTests")]
public sealed class ErbFileAssemblerTests
{
	// 覆盖面（沿用 ErbLoaderTests 已验证可解析的语料形态）：跨文件 @ 重名 / 文件内 $ 重複 /
	// #ONLY 跨文件重複 / 带参标签。ASCII 标签名避免触碰 LangManager 编码坑。
	private static readonly (string Name, string Content)[] Corpus =
	[
		("A.ERB", "@FOO\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n"),
		("B.ERB", "@FOO\n$LAB\n$LAB\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n@BFOO(X, Y)\nRETURN\n"),
		("C.ERB", "@EVENTSHOP\nRETURN\n"),
	];

	/// <summary>
	/// 串行（<c>deferCrossFileChecks=false</c>，生成期即时跨文件检查）与
	/// 延后（<c>true</c>，合并期经占位槽补检）两条路径产出<b>逐位一致</b>。
	/// 两条路径的<b>唯一</b>差别就在 defer 与"全部生成后再按序合并"这一节奏
	///（与 <c>LoadPendingSerial</c> / <c>LoadPendingParallel</c> 的差别同构）。
	/// </summary>
	[Fact]
	public async Task Deferred_cross_file_checks_are_equivalent_to_immediate()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		// FuncIsAlreadyDefined 默认不警告（WarnNormalFunctionOverloading 默认 false），显式开启才有跨文件重名可比。
		ConfigData.Current!.GetConfigItem(ConfigCode.WarnNormalFunctionOverloading).SetValue(true);
		// 语料写在 Initialize 之后：否则 LoadHeadersAndScripts 会把这些文件全量装载掉，
		// 测的就不再是 Generate+Merge 了。且刻意**不** PreloadAsync——让 EraStreamReader 走真实读盘回退
		//（唯一临时路径下 Preload / SrcContentCache 两级都 miss）。
		var files = new List<(string Path, string Name)>();
		foreach (var (name, content) in Corpus)
			files.Add((h.WriteErb(name, content), name));

		var immediate = Run(h, files, deferCrossFileChecks: false);
		var deferred = Run(h, files, deferCrossFileChecks: true);

		Assert.Equal(immediate.Labels, deferred.Labels);
		Assert.Equal(immediate.OnlyDeclarations, deferred.OnlyDeclarations);
		Assert.Equal(immediate.IsOnlyEvent, deferred.IsOnlyEvent);
		Assert.Equal(immediate.EnabledLines, deferred.EnabledLines);
		Assert.Equal(immediate.EmittedWarnings, deferred.EmittedWarnings);
		// 自检：语料确实触发了跨文件检查，否则本测试是空转（等价性在空集上平凡成立）
		Assert.Contains(immediate.EmittedWarnings, s => s.Contains("@FOO"));
		Assert.Contains(immediate.EmittedWarnings, s => s.Contains("#ONLY"));
	}

	/// <summary>
	/// 占位槽机制本身（延后模式独有，其他测试零覆盖）：
	/// 生成期不能读 labelDic（并行下不可并发读），故在 <c>art.Warnings</c> 里按<b>行序位置</b>
	/// 插空槽，并把待办记进 <c>PendingOverloads</c> / <c>PendingOnlys</c>；合并期再替换或删除。
	/// 本测试钉住两件事：**槽数 == 待办数**，且**槽位是空消息**（未解析的形态）。
	/// </summary>
	[Fact]
	public async Task Deferred_mode_seeds_placeholder_slots()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.WarnNormalFunctionOverloading).SetValue(true);
		var path = h.WriteErb("B.ERB", "@FOO\n$LAB\n$LAB\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n");
		h.WriteErb("A.ERB", "@FOO\nRETURN\n@EVENTTRAIN\n#ONLY\nRETURN\n");

		var ctx = NewContext(h, new LabelDictionary());
		var art = ErbFileAssembler.Generate(path, "B.ERB", [], deferCrossFileChecks: true,
			replayOnFailure: true, ctx, new ErbFileAssembler.Stats());

		// B 相对 A：@FOO 跨文件重名 1 处 + #ONLY 跨文件重複 1 处
		Assert.Single(art.PendingOverloads);
		Assert.Single(art.PendingOnlys);
		// 槽位 = 空消息（占位符），且数量与待办一致
		Assert.Equal(2, art.Warnings.Count(w => w.WarningMes.Length == 0));
		// 非占位的警告（$ 重複）仍在，说明占位是"插入"而非"清空"
		Assert.Contains(art.Warnings, w => w.WarningMes.Length > 0);
	}

	/// <summary>
	/// ADR-0039：<b>两层断言</b>——27 处显式写入（本文件 12 + <c>PPState</c> 15）与
	/// <c>LogicalLineParser</c> 等约 54 处经 <c>AsyncLocal</c> 缓冲的间接告警，必须
	/// <b>按行序交错</b>落在同一个产物列表里，并在 <c>Merge</c> 后保持同一顺序进全局列表。
	/// <para>
	/// 语料刻意造成"装配器 → PPState → 装配器"的三行交错：<c>#X</c>（InvalidSharp，装配器直写）、
	/// <c>[SKIPSTART] extra</c>（HasTooManyArg，来自 PPState）、<c>#Y</c>（再次 InvalidSharp）。
	/// 任何一处绕过目的地直投全局，中间那条就会提前进全局、<c>Merge</c> 后顺序变成 [2,1,3]——
	/// 只剩第一层的断言抓不到，必须两层一起比。
	/// </para>
	/// </summary>
	[Fact]
	public async Task Warnings_from_both_sources_are_interleaved_in_source_order()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		ParserMediator.ClearWarningList();
		var path = h.WriteErb("W.ERB", "#X\n[IF]\nRETURN\n@F\nRETURN\n");

		var ctx = NewContext(h, new LabelDictionary());
		var art = ErbFileAssembler.Generate(path, "W.ERB", [], deferCrossFileChecks: false,
			replayOnFailure: true, ctx, new ErbFileAssembler.Stats());

		// 层 1：产物层——两类来源按行序交错（位置是判别器，与本地化文案无关）
		var lineNos = art.Warnings.Select(w => w.WarningPos!.Value.LineNo).ToList();
		Assert.Equal([1, 2, 3], lineNos.Take(3)); // 前三条即 #X(装配器) → [IF](PPState) → RETURN(装配器)
		var messages = art.Warnings.Select(w => w.WarningMes).ToList();
		Assert.Equal(3, messages.Take(3).Distinct().Count()); // 三条文案互不相同，顺序断言才有意义

		// 层 2：发射层——Merge 后这三条在全局序列里的相对顺序必须与产物层一致
		//（用子序列判定而非逐位相等：产物里可能另有无害告警，但交错的三条不能被拆散/重排）
		_ = ErbFileAssembler.Merge(art, [], ctx);
		var emitted = EmittedWarnings(h);
		var positions = new List<int>();
		foreach (var message in messages.Take(3))
		{
			var at = emitted.FindIndex(line => line.Contains(message));
			Assert.True(at >= 0, $"该警告未发射到全局序列：{message}");
			positions.Add(at);
		}
		Assert.True(positions[0] < positions[1] && positions[1] < positions[2],
			$"两条来源的警告序被打乱：产物层 1→2→3，全局层落在 {string.Join(",", positions)}");
	}

	// ===== 夹具 =====

	/// <summary>一次完整装配：按 <paramref name="defer"/> 决定跨文件检查即时还是延后。</summary>
	private static Outcome Run(LoaderTestHarness h, List<(string Path, string Name)> files, bool deferCrossFileChecks)
	{
		var labelDic = new LabelDictionary();
		var isOnlyEvent = new List<string>();
		var stats = new ErbFileAssembler.Stats();
		var ctx = NewContext(h, labelDic);
		ParserMediator.ClearWarningList();

		var onlyDeclarations = new List<string>();
		int enabledLines = 0;

		// 延后路径 = 先全部生成、再按序合并（并行调度语义）；即时路径 = 生成即合并（串行调度语义）。
		var pending = new List<ErbFileArtifact>();
		foreach (var (path, name) in files)
		{
			var art = ErbFileAssembler.Generate(path, name, isOnlyEvent, deferCrossFileChecks,
				replayOnFailure: true, ctx, stats);
			onlyDeclarations.AddRange(art.OnlyDeclarations.Select(d => $"{name}:{d}"));
			if (deferCrossFileChecks)
			{
				pending.Add(art);
				continue;
			}
			enabledLines += ErbFileAssembler.Merge(art, isOnlyEvent, ctx).EnabledLines;
		}
		foreach (var art in pending)
			enabledLines += ErbFileAssembler.Merge(art, isOnlyEvent, ctx).EnabledLines;

		return new Outcome(Signature(labelDic), onlyDeclarations, isOnlyEvent, enabledLines, EmittedWarnings(h));
	}

	private static ErbFileAssembler.Context NewContext(LoaderTestHarness h, LabelDictionary labelDic)
		// 取值口径照抄 ErbLoader.AssembleContext（IR 两路传 null：本刀只覆盖源路径）。
		=> new(h.Env, h.IdDic, h.Console, line => h.Process.scaningLine = line, labelDic, null, null,
			Config.UseRenameFile, ParserMediator.RenameDic != null, Config.StrComper,
			Config.LazyFunctionLoad, Config.WarnNormalFunctionOverloading,
			Config.PrintCPerLine, JSONConfig.Data.UseNewRandom);

	/// <summary>
	/// 发出后的警告（逐字）。两条路径都经 <c>EmitWarningBuffer</c> 进全局列表，
	/// 只有 flushed 之后才能对称比较；控制台是唯一公开观察通道。
	/// </summary>
	private static List<string> EmittedWarnings(LoaderTestHarness h)
	{
		h.Console.DisplayLineList.Clear();
		ParserMediator.FlushWarningList();
		return h.Console.DisplayLineList.Select(l => l?.ToString() ?? "").ToList();
	}

	/// <summary>标签签名：名 + Index + FileIndex（与 ErbLoaderTests 的等价性口径同源，便于交叉判读）。</summary>
	private static List<string> Signature(LabelDictionary dic) =>
		dic.GetAllLabels(false).OrderBy(l => l.Index)
			.Select(l => $"{l.LabelName}:{l.Index}:{l.FileIndex}").ToList();

	private sealed record Outcome(
		List<string> Labels,
		List<string> OnlyDeclarations,
		List<string> IsOnlyEvent,
		int EnabledLines,
		List<string> EmittedWarnings);
}
