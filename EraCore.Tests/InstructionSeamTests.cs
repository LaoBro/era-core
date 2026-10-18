using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc;
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
/// 两例分工：
/// ① 直接调 <c>DoInstruction</c>，验证指令经接缝说话（守指令行为）；
/// ② 经 <c>ScriptProc.Run()</c> 派发，验证<b>派发路径没有下转型</b>（守 seam 完整性）。
/// ② 是 ADR-0052 记录过的覆盖缺口：此前把下转型加回去，没有任何测试转红。
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
		using var harness = new LoaderTestHarness();
		var instruction = await LoadFirstInstructionAsync(harness, "BEGINSEAM.ERB");

		// 非 FORCE_SETARG 指令的实参在运行期才补齐（复刻 ScriptProc 路径）
		Assert.True(ArgumentParser.SetArgumentTo(instruction));

		var spy = new SpyProcessState();
		instruction.Function.Instruction.DoInstruction(harness.Exm, instruction, spy);

		// BEGIN TRAIN 只经接缝说话：SetBegin 是第 3 刀为指令层补进接口的三个成员之一。
		Assert.Equal(new[] { "SetBegin:TRAIN:True", "Return:0" }, spy.Calls);
	}

	[Fact]
	public async Task ScriptProc_dispatch_hands_the_seam_state_to_the_instruction_without_downcasting()
	{
		using var harness = new LoaderTestHarness();
		var instruction = await LoadFirstInstructionAsync(harness, "DISPATCHSEAM.ERB");

		// 行链：head → 指令行。Run() 首步 ShiftNextLine 恰好落到指令行上。
		var head = new HeadLine();
		head.NextLine = instruction;

		var spy = new SpyProcessState { CurrentLine = head };
		var es = new ExecutionState { CurrentState = spy };
		// vEvaluator / gameBase / trainName 在本路径上不被触及：行链首行是指令行，
		// 不进 NullLine / FunctionLabelLine 分支。ScriptProc 构造接受 null 注入（既有先例）。
		var sp = new ScriptProc(harness.Console, null!, harness.Exm, harness.IdDic, es, null!, null!, harness.LabelDic);

		Assert.True(harness.Console.IsRunning, "console 未处于可运行态，Run() 会在首个检查点直接返回");

		sp.Run();

		// 关键：Run() 必须把 IProcessState 原样交给指令层。若 ScriptProc 里存在
		// (ProcessState)executionState.CurrentState 下转型，此处会抛 InvalidCastException
		// 而根本走不到断言 —— 这正是本例要守的不变量。
		Assert.Equal(new[] { "ShiftNextLine", "SetBegin:TRAIN:True", "Return:0" }, spy.Calls);
	}

	// ---------- fixture ----------

	/// <summary>
	/// harness 必须由<b>测试方法自己</b>创建并 using，不能挪进被调用的 async 方法：
	/// GlobalStatic 的 Config/Process scope 走 AsyncLocal，在被调方法里设置的值
	/// 不会向上传播回调用者（表现为 GlobalStatic.EMediator 为 null）。
	/// </summary>
	static async Task<InstructionLine> LoadFirstInstructionAsync(LoaderTestHarness harness, string erbName)
	{
		await harness.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		harness.WriteErb(erbName, Game);
		await harness.PreloadAsync();
		var loader = new ErbLoader(harness.Console, harness.Exm, harness.IdDic, harness.Env,
			line => harness.Process.scaningLine = line);
		await loader.LoadErbDir(harness.ErbDir, displayReport: false, harness.LabelDic);

		var entry = harness.LabelDic.GetNonEventLabel("ENTRY");
		Assert.NotNull(entry);
		var instruction = entry.NextLine as InstructionLine;
		Assert.NotNull(instruction);
		return instruction;
	}

	/// <summary>LogicalLine 无抽象成员；此行仅用于承载 NextLine 链的起点。</summary>
	sealed class HeadLine : LogicalLine { }

	/// <summary>记录型 fake：不持有 Process / Console / Config，只记录接缝上的每一次调用。</summary>
	sealed class SpyProcessState : IProcessState
	{
		public List<string> Calls { get; } = [];

		public SystemStateCode SystemState { get; set; }
		public LogicalLine CurrentLine { get; set; } = null!;
		public bool IsFunctionMethod { get; set; }
		public bool isBegun { get; set; }
		public bool calledWhenNormal { get; set; }
		public CalledFunction CurrentCalled { get; set; } = null!;
		public int lineCount { get; set; }
		public string Scope { get; set; } = "";

		public bool ScriptEnd { get; set; }

		/// <summary>真实推进 PC —— 否则 Run() 的循环不会前进。</summary>
		public void ShiftNextLine()
		{
			Calls.Add(nameof(ShiftNextLine));
			CurrentLine = CurrentLine.NextLine;
		}

		/// <summary>出栈即视为本轮执行结束，让 Run() 的循环在下一个检查点收住。</summary>
		public void Return(long ret)
		{
			Calls.Add($"{nameof(Return)}:{ret}");
			ScriptEnd = true;
		}

		public void JumpTo(LogicalLine line) => Calls.Add(nameof(JumpTo));
		public void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm)
			=> Calls.Add(nameof(IntoFunction));
		public void Begin() => Calls.Add(nameof(Begin));
		public void ClearFunctionList() => Calls.Add(nameof(ClearFunctionList));
		public void SetBegin(string keyword, bool force) => Calls.Add($"{nameof(SetBegin)}:{keyword}:{force}");
		public void ReturnF(SingleTerm ret) => Calls.Add(nameof(ReturnF));
		public IProcessState Clone() => this;
	}
}
