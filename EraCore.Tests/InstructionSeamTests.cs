using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 候选 4 · 第 3 刀：指令层 seam 验收。
///
/// 此前 <c>DoInstruction</c> 的第三参是具体 <c>ProcessState</c>（118 个 override 直连实现类），
/// 指令层因此看得见 ProcessState 的全部 27 个 public 成员。本刀把它换成 <see cref="IProcessState"/>
/// 后，指令层的可见面收窄到接口的 18 个成员，并且——这是关键——<b>可以用 fake 驱动</b>。
///
/// 本测试即该推论的兑现：用一个记录型 fake 驱动 BEGIN 指令，断言它只经接缝说话。
/// </summary>
[Collection("LoaderTests")]
public sealed class InstructionSeamTests
{
	private const string Game = """
@ENTRY
BEGIN TRAIN
""";

	[Fact]
	public async Task BEGIN_instruction_is_drivable_through_a_fake_IProcessState()
	{
		var harness = new LoaderTestHarness();
		using var _ = harness;
		await harness.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		harness.WriteErb("BEGINSEAM.ERB", Game);
		await harness.PreloadAsync();
		var loader = new ErbLoader(harness.Console, harness.Exm, harness.IdDic, harness.Env,
			line => harness.Process.scaningLine = line);
		await loader.LoadErbDir(harness.ErbDir, displayReport: false, harness.LabelDic);

		var entry = harness.LabelDic.GetNonEventLabel("ENTRY");
		Assert.NotNull(entry);
		var line = entry.NextLine as InstructionLine;
		Assert.NotNull(line);
		// 非 FORCE_SETARG 指令的实参在运行期才补齐（复刻 ScriptProc 路径）
		Assert.True(ArgumentParser.SetArgumentTo(line));

		var spy = new SpyProcessState();
		line.Function.Instruction.DoInstruction(harness.Exm, line, spy);

		// BEGIN TRAIN 只经接缝说话：SetBegin 是第 3 刀为指令层补进接口的三个成员之一。
		Assert.Equal(new[] { "SetBegin:TRAIN:True", "Return:0" }, spy.Calls);
	}

	/// <summary>记录型 fake：不持有 Process / Console / Config，只记录接缝上的每一次调用。</summary>
	sealed class SpyProcessState : IProcessState
	{
		public List<string> Calls { get; } = [];

		public SystemStateCode SystemState { get; set; }
		public LogicalLine CurrentLine { get; set; } = null!;
		public bool ScriptEnd { get; set; }
		public bool IsFunctionMethod { get; set; }
		public bool isBegun { get; set; }
		public bool calledWhenNormal { get; set; }
		public CalledFunction CurrentCalled { get; set; } = null!;
		public int lineCount { get; set; }
		public string Scope { get; set; } = "";

		public void ShiftNextLine() => Calls.Add(nameof(ShiftNextLine));
		public void JumpTo(LogicalLine line) => Calls.Add(nameof(JumpTo));
		public void Return(long ret) => Calls.Add($"{nameof(Return)}:{ret}");
		public void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm)
			=> Calls.Add(nameof(IntoFunction));
		public void Begin() => Calls.Add(nameof(Begin));
		public void ClearFunctionList() => Calls.Add(nameof(ClearFunctionList));
		public void SetBegin(string keyword, bool force) => Calls.Add($"{nameof(SetBegin)}:{keyword}:{force}");
		public void ReturnF(SingleTerm ret) => Calls.Add(nameof(ReturnF));
		public IProcessState Clone() => this;
	}
}
