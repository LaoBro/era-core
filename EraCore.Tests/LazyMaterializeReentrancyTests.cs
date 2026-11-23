using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// lazy-function-load / b2-irdiskcache 回归：运行期物化接缝必须能处理任意合法调用图。
/// 互递归（A⇄B）与自递归（A→A）的桩在「另一个桩物化的 setJumpTo 里」被首次触发时，
/// 旧实现会 A 物化→CALL B→物化 B→CALL A→… 无界递归压栈（实测在线程栈 guard page 上 SIGSEGV）。
/// 修复后嵌套触发只入队，栈深恒为 1。两条输入流各覆盖一次：源后背（round1）与 IR blob 后背（round2）。
/// </summary>
[Collection("LoaderTests")]
public class LazyMaterializeReentrancyTests
{
	// 互递归对 + 自递归：三个函数都无人调用，加载后必须仍是桩，才能在运行期触发「桩内 CALL 桩」。
	private const string Corpus = """
@EVENTFIRST
	RETURN

@TFC_TICK
	X = 1
	CALL TFC_WATCH
	RETURN

@TFC_WATCH
	CALL TFC_TICK
	RETURN

@SELFREC
	Y = 2
	CALL SELFREC
	RETURN
""";

	private static string FullIrPath(string tempRoot) => Path.Combine(tempRoot, "_IRCache", "era_ir.dat");

	private static string SummarizeChain(LogicalLine header)
	{
		var list = new List<string>();
		var n = header.NextLine;
		while (n != null)
		{
			if (n is NullLine || n is FunctionLabelLine)
				break;
			if (n is GotoLabelLine g)
				list.Add($"G@{n.Position?.LineNo ?? -1}");
			else if (n is InstructionLine ins)
				list.Add($"{ins.FunctionCode}@{n.Position?.LineNo ?? -1}");
			n = n.NextLine;
		}
		return string.Join(";", list);
	}

	private static FunctionLabelLine Find(LabelDictionary labelDic, string name) => labelDic.GetNonEventLabel(name)!;

	private static void EnableLazy(LoaderTestHarness h, bool irDiskCache)
	{
		ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(true);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelErbLoad).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.ParallelParseScript).SetValue(false);
		ConfigData.Current!.GetConfigItem(ConfigCode.IrDiskCacheLoad).SetValue(irDiskCache);
	}

	private static void AssertMaterializedWithoutRecursion(LoaderTestHarness h, LabelDictionary labelDic)
	{
		var tick = Find(labelDic, "TFC_TICK");
		var watch = Find(labelDic, "TFC_WATCH");
		var self = Find(labelDic, "SELFREC");
		Assert.True(tick.IsStub, "TFC_TICK must stay a stub after lazy load");
		Assert.True(watch.IsStub, "TFC_WATCH must stay a stub after lazy load");
		Assert.True(self.IsStub, "SELFREC must stay a stub after lazy load");

		// 旧实现：这里在 setJumpTo 的 CALL 里递归回本函数 → 栈溢出中止。
		ErbLoader.MaterializeOnRuntime(tick);
		Assert.False(tick.IsStub, "TFC_TICK must materialize; err=" + tick.ErrMes);
		Assert.False(tick.IsError, "TFC_TICK must not degrade: " + tick.ErrMes);
		// 互递归的另一半由队列在同一次顶层调用内完成（返回即 body 可用）。
		Assert.False(watch.IsStub, "TFC_WATCH must be materialized by the worklist; err=" + watch.ErrMes);
		Assert.False(watch.IsError, "TFC_WATCH must not degrade: " + watch.ErrMes);
		Assert.NotEqual("", SummarizeChain(tick));
		Assert.NotEqual("", SummarizeChain(watch));

		ErbLoader.MaterializeOnRuntime(self);
		Assert.False(self.IsStub, "SELFREC must materialize; err=" + self.ErrMes);
		Assert.False(self.IsError, "SELFREC must not degrade: " + self.ErrMes);
		Assert.NotEqual("", SummarizeChain(self));

		// 幂等：再次触发不得重建行链。
		var chainTick = SummarizeChain(tick);
		ErbLoader.MaterializeOnRuntime(tick);
		Assert.Equal(chainTick, SummarizeChain(tick));

		var falsePositives = h.Console.DisplayLineList
			.Select(l => l?.ToString() ?? "")
			.Where(s => s.Contains("未在此函数中定义") || s.Contains("不存在执行中的函数") || s.Contains("定義されていません"))
			.ToList();
		Assert.True(falsePositives.Count == 0, "falsePositives:\n" + string.Join("\n", falsePositives));
	}

	/// <summary>源后背（B1）：首轮加载后未调用的互递归/自递归桩在运行期首调物化。</summary>
	[Fact]
	public async Task Source_backed_mutual_and_self_recursive_stubs_materialize()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h, irDiskCache: false);
		await h.PreloadAsync();
		h.WriteErb("REENTRANT_SRC.ERB", Corpus);

		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok, "lazy load failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		AssertMaterializedWithoutRecursion(h, h.LabelDic);
	}

	/// <summary>IR 后备（B2）：第二轮命中磁盘 IR 后，blob 桩的互递归/自递归运行期首调物化。</summary>
	[Fact]
	public async Task Ir_backed_mutual_and_self_recursive_stubs_materialize()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		EnableLazy(h, irDiskCache: true);
		await h.PreloadAsync();
		h.WriteErb("REENTRANT_IR.ERB", Corpus);

		// round1：建 IR（源后背）
		var loader1 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok1 = await loader1.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok1, "round1 failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		Assert.True(File.Exists(FullIrPath(h.TempRoot)), "round1 should build IR cache");

		// round2：命中 IR，桩为 IrBacked；此处不得在 round1 里先物化，否则 round2 无桩可测。
		var loader2 = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);
		var ok2 = await loader2.LoadErbDir(h.ErbDir, displayReport: false, h.LabelDic);
		Assert.True(ok2, "round2 failed\n" + string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));
		Assert.True(Find(h.LabelDic, "TFC_TICK").IrBacked, "round2 stubs must be IR-backed");

		AssertMaterializedWithoutRecursion(h, h.LabelDic);
	}
}
