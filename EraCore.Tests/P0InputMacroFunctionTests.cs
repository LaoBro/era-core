using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 42（Tier 4b，Skia 对齐）：输入宏开关 + SEQUENCEINPUT 序列输入（3 键）。
/// 宏解析本身复用既有 <c>ConsoleInputHandler.PressEnterKey</c> 管线；本票只补门控与序列队列。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0InputMacroFunctionTests
{
	private const string ErbFileName = "INPUTMACRO.ERB";

	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static LoaderTestHarness NewHarness(string erb)
	{
		var harness = new LoaderTestHarness(dialectProfileId: DialectRegistry.SnakeId);
		harness.WriteErb(ErbFileName, erb);
		return harness;
	}

	/// <summary>跑一次 <c>@SYSTEM_TITLE</c>；与 <c>TimedWaitInputSwallowedTests</c> 同款装配。</summary>
	private static void Start(LoaderTestHarness harness)
	{
		harness.Console._state.process = harness.Process;
		harness.Console.RunEmueraProgram("");
	}

	private static string ScreenText(EmueraConsole console)
		=> string.Join("\n", console.DisplayLineList.Select(line => line?.ToString() ?? ""));

	[Theory]
	[InlineData("SEQUENCEINPUT")]
	[InlineData("DISABLE_INPUT_MACRO")]
	[InlineData("ENABLE_INPUT_MACRO")]
	public void Input_macro_functions_are_snake_only_and_not_restructured(string name)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method(name);
		Assert.Equal(typeof(long), method.ReturnType);
		Assert.False(method.CanRestructure);
		Assert.Contains(name, DialectRegistry.Snake.DeclaredKeys);
		Assert.DoesNotContain(name, DialectRegistry.Baseline.DeclaredKeys);
	}

	[Fact]
	public async Task Sequence_input_is_consumed_by_next_input()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			SEQUENCEINPUT "1"
			INPUT
			PRINTFORML RESULT={RESULT}
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		Assert.Contains("RESULT=1", ScreenText(harness.Console));
	}

	[Fact]
	public async Task Last_sequence_input_wins()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			SEQUENCEINPUT "1"
			SEQUENCEINPUT "2"
			INPUT
			PRINTFORML RESULT={RESULT}
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		Assert.Contains("RESULT=2", ScreenText(harness.Console));
	}

	[Fact]
	public async Task Sequence_input_is_consumed_only_once()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			SEQUENCEINPUT "7"
			INPUT
			PRINTFORML FIRST={RESULT}
			INPUT
			PRINTFORML SECOND={RESULT}
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		// 第二次 INPUT 没有预置输入 ⇒ 停在等待宿主输入态，SECOND 不会被打印。
		string screen = ScreenText(harness.Console);
		Assert.Contains("FIRST=7", screen);
		Assert.DoesNotContain("SECOND", screen);
		Assert.Equal(ConsoleState.WaitInput, harness.Console.State);
	}

	[Fact]
	public async Task Macro_expansion_and_line_split_apply_by_default()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			SEQUENCEINPUT "(A)*2\nB"
			INPUTS
			PRINTFORML R1=%RESULTS:0%
			INPUTS
			PRINTFORML R2=%RESULTS:0%
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		Assert.Contains("R1=AA", ScreenText(harness.Console));
		Assert.Contains("R2=B", ScreenText(harness.Console));
	}

	[Fact]
	public async Task Disable_input_macro_feeds_input_literally()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			DISABLE_INPUT_MACRO
			SEQUENCEINPUT "(A)*2\nB"
			INPUTS
			PRINTFORML R1=%RESULTS:0%
			INPUTS
			PRINTFORML R2=%RESULTS:0%
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		// 不展开 ()、不按 \n 拆分：整段字面量一次喂入。
		Assert.Equal("(A)*2\nB", harness.VEvaluator.RESULTS);
		Assert.DoesNotContain("R2", ScreenText(harness.Console));
		Assert.Equal(ConsoleState.WaitInput, harness.Console.State);
	}

	[Fact]
	public async Task Enable_input_macro_restores_expansion()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			DISABLE_INPUT_MACRO
			ENABLE_INPUT_MACRO
			SEQUENCEINPUT "(A)*2\nB"
			INPUTS
			PRINTFORML R1=%RESULTS:0%
			INPUTS
			PRINTFORML R2=%RESULTS:0%
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		Assert.Contains("R1=AA", ScreenText(harness.Console));
		Assert.Contains("R2=B", ScreenText(harness.Console));
	}

	[Fact]
	public async Task Escaped_e_is_preserved_and_triggers_mes_skip()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			SEQUENCEINPUT "\e"
			INPUTS
			PRINTFORML R=[%RESULTS:0%]
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		// \e 在字面量中保留为 2 字符 ⇒ 去掉后得到空输入；若被折叠成 "e" 会打印 R=[e]。
		Assert.Contains("R=[]", ScreenText(harness.Console));
		Assert.DoesNotContain("R=[e]", ScreenText(harness.Console));
	}

	[Fact]
	public async Task Disable_input_macro_keeps_escaped_e_literal()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			DISABLE_INPUT_MACRO
			SEQUENCEINPUT "\e"
			INPUTS
			PRINTFORML R=[%RESULTS:0%]
			QUIT
			""");
		await harness.InitializeAsync();
		Start(harness);

		Assert.Equal("\\e", harness.VEvaluator.RESULTS);
	}

	[Fact]
	public async Task Toggle_functions_return_zero()
	{
		using var harness = NewHarness("""
			@SYSTEM_TITLE
			QUIT
			""");
		await harness.InitializeAsync();

		Assert.Equal(0, Method("DISABLE_INPUT_MACRO").GetIntValue(harness.Exm, Args()));
		Assert.False(harness.Process.executionState.inputMacroEnabled);
		Assert.Equal(0, Method("ENABLE_INPUT_MACRO").GetIntValue(harness.Exm, Args()));
		Assert.True(harness.Process.executionState.inputMacroEnabled);
		Assert.Equal(0, Method("SEQUENCEINPUT").GetIntValue(harness.Exm, Args(new SingleStrTerm("abc"))));
		Assert.True(harness.Process.executionState.hasSequenceInput);
		Assert.Equal("abc", harness.Process.executionState.sequenceInputValue);
	}
}
