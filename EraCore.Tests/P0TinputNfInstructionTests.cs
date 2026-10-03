using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言 P0：NF 输入指令 TINPUTNF / TINPUTSNF / TONEINPUTNF / TONEINPUTSNF（实现票 21，spec §5）。
///
/// 证据：
/// - 解析 fixture：eraTW TITLE.ERB 片段（<c>TINPUTNF 20000,2,0,"开始播放DEMO"</c>）与四形态均可解析、行级无错；
/// - 执行 fixture：DoInstruction 产出的 <see cref="InputRequest"/> 覆盖超时/输入两路径所需的全部字段
///   （timelimit / 默认值 / 超时文案 / OneInput / NeedValue）；
/// - 反射差异：flag 与 asset §4.1.1 一致（IS_PRINT | IS_INPUT | EXTENDED）。
///
/// 说明：经 HTTP 服务器跑超时/输入全链路的 e2e（tests/test_tinput_timeout.py 手法）依赖
/// game→profile 的产品装配（实现票 25），届时由一致性套件补齐；此处先固定指令级行为。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0TinputNfInstructionTests
{
	private const string Game = """
@ENTRY
TINPUTNF 300, 7, 0, "TIME UP", 0
TINPUTSNF 300, "def", 0, "TIME UP", 0
TONEINPUTNF 300, 7, 0, "TIME UP", 0
TONEINPUTSNF 300, "def", 0, "TIME UP", 0
RETURN

@TITLEFRAG
TINPUTNF 20000,2,0,"开始播放DEMO"
RETURN
""";

	private static ErbLoader NewLoader(LoaderTestHarness h)
		=> new(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);

	/// <summary>见实现票 20 同名说明：session scope 不跨 await 上浮，故断言在本方法内回调执行。</summary>
	private static DialectProfile SnakeProfile()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		return DialectRegistry.Resolve(DialectRegistry.SnakeId);
	}

	private static async Task WithFunctionAsync(string label, Func<LoaderTestHarness, List<InstructionLine>, Task> body)
	{
		var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		using var _ = harness;
		await harness.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		harness.WriteErb("TINPUTNF.ERB", Game);
		await harness.PreloadAsync();
		await NewLoader(harness).LoadErbDir(harness.ErbDir, displayReport: false, harness.LabelDic);

		FunctionLabelLine entry = harness.LabelDic.GetNonEventLabel(label);
		Assert.NotNull(entry);
		List<InstructionLine> lines = [];
		for (LogicalLine? line = entry.NextLine; line != null && line is not FunctionLabelLine; line = line.NextLine)
			if (line is InstructionLine instruction)
				lines.Add(instruction);

		await body(harness, lines);
	}

	private static InstructionLine Line(List<InstructionLine> lines, string name)
		=> lines.FirstOrDefault(l => l.Function?.Name == name)
			?? throw new Xunit.Sdk.XunitException(
				$"未找到 {name}；指令行 = [{string.Join(", ", lines.Select(l => l.Function?.Name ?? "<null>"))}]");

	private static InputRequest Run(LoaderTestHarness h, InstructionLine line)
	{
		// 非 FORCE_SETARG 指令的实参在加载期不解析，运行期才由 ArgumentParser 补齐（复刻 ScriptProc 路径）。
		Assert.True(ArgumentParser.SetArgumentTo(line));
		var state = new ProcessState(h.Console);
		line.Function.Instruction.DoInstruction(h.Exm, line, state);
		return h.Console.CurrentRequest!;
	}

	// ---------- 解析 fixture ----------

	[Fact]
	public Task Four_nf_forms_parse_without_error()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			foreach (string name in new[] { "TINPUTNF", "TINPUTSNF", "TONEINPUTNF", "TONEINPUTSNF" })
				Assert.False(Line(lines, name).IsError, name);
			return Task.CompletedTask;
		});

	[Fact]
	public Task EraTW_title_fragment_parses_without_error()
		=> WithFunctionAsync("TITLEFRAG", (h, lines) =>
		{
			Assert.False(Line(lines, "TINPUTNF").IsError);
			return Task.CompletedTask;
		});

	// ---------- 执行 fixture：超时 / 输入两路径 ----------

	[Fact]
	public Task TinputNf_yields_int_request_carrying_timeout_and_default()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			InputRequest req = Run(h, Line(lines, "TINPUTNF"));

			// 输入路径：等待整数、需要取值、非 OneInput。
			Assert.Equal(InputType.IntValue, req.InputType);
			Assert.True(req.NeedValue);
			Assert.False(req.OneInput);
			// 超时路径：限时、默认值（超时以默认值推进）、超时文案、disp=0 不显示倒计时。
			Assert.Equal(300, req.Timelimit);
			Assert.True(req.HasDefValue);
			Assert.Equal(7, req.DefIntValue);
			Assert.Equal("TIME UP", req.TimeUpMes);
			Assert.False(req.DisplayTime);
			return Task.CompletedTask;
		});

	[Fact]
	public Task TinputSnf_yields_string_request_carrying_timeout_and_default()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			InputRequest req = Run(h, Line(lines, "TINPUTSNF"));

			Assert.Equal(InputType.StrValue, req.InputType);
			Assert.True(req.NeedValue);
			Assert.False(req.OneInput);
			Assert.Equal(300, req.Timelimit);
			Assert.True(req.HasDefValue);
			Assert.Equal("def", req.DefStrValue);
			Assert.Equal("TIME UP", req.TimeUpMes);
			return Task.CompletedTask;
		});

	[Fact]
	public Task OneInputNf_sets_single_input_flag()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			Assert.True(Run(h, Line(lines, "TONEINPUTNF")).OneInput);
			Assert.True(Run(h, Line(lines, "TONEINPUTSNF")).OneInput);
			return Task.CompletedTask;
		});
}

/// <summary>
/// Skia 方言 P0：NF 输入指令的 flag 与 profile 可见性（实现票 21）。只需注册表 + Config scope。
/// </summary>
public sealed class P0TinputNfInstructionRegistrationTests : IDisposable
{
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	private static FunctionIdentifier Instruction(string name)
		=> FunctionIdentifier.GetInstructionNameDic()[name];

	[Theory]
	[InlineData("TINPUTNF")]
	[InlineData("TINPUTSNF")]
	[InlineData("TONEINPUTNF")]
	[InlineData("TONEINPUTSNF")]
	public void Registered_flags_match_asset(string name)
	{
		FunctionIdentifier fi = Instruction(name);

		// asset §4.1.1：TINPUTNF 族 = IS_PRINT | IS_INPUT | EXTENDED（无 EXTENDED 之外的附加 flag）。
		Assert.True(fi.IsPrint());
		Assert.True(fi.IsInput());
		Assert.True(fi.IsExtended());
		Assert.False(fi.IsJump());
		Assert.False(fi.IsTry());
		Assert.False(fi.IsTryC());
		Assert.False(fi.IsPartial());
		Assert.NotNull(fi.Instruction);
	}

	[Fact]
	public void Nf_input_instructions_are_visible_only_when_snake_module_is_selected()
	{
		var instructions = FunctionIdentifier.GetInstructionNameDic();

		var v24 = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snake = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in new[] { "TINPUTNF", "TINPUTSNF", "TONEINPUTNF", "TONEINPUTSNF" })
		{
			Assert.False(v24.ContainsKey(name), name);
			Assert.True(snake.ContainsKey(name), name);
		}
		// 既有基线输入指令在 v24pure 下不受影响。
		Assert.True(v24.ContainsKey("TINPUT"));
		Assert.True(v24.ContainsKey("TONEINPUTS"));
	}
}