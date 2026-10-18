using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言 P0：HTML/文本指令 HTML_PRINTC / HTML_PRINTLC / TEXT_BGC_ON / TEXT_BGC_OFF（实现票 23，spec §5 / 决策票 06）。
///
/// 证据：
/// - 解析 fixture：四条指令与 eraTW 片段（<c>HTML_PRINTC L_BTN_HTML</c>）均可解析、行级无错；
/// - 复用管线 fixture：HTML_PRINTC/LC 走既有 HTML 管线，执行后打印缓冲非空（真语义，非 no-op）；
/// - TEXT_BGC 语义开关：TEXT_BGC_ON 按 RGB + 透明度(%) 落地状态，TEXT_BGC_OFF 清除（页面渲染未消费 → 已知差异）；
/// - flag 反射与 profile 可见性。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0HtmlTextInstructionTests
{
	private const string Game = """
@ENTRY
HTML_PRINTC "hello"
HTML_PRINTLC "world", 200
TEXT_BGC_ON 0xFF0000, 50
TEXT_BGC_OFF
RETURN

@PRINTCFRAG
#DIMS L_BTN_HTML
HTML_PRINTC L_BTN_HTML
RETURN

@BGCNONCONST
TEXT_BGC_ON FLAG:0, 100
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
		harness.WriteErb("HTMLTEXT.ERB", Game);
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

	private static InstructionLine Run(LoaderTestHarness h, InstructionLine line)
	{
		// 非 FORCE_SETARG 指令的实参在加载期不解析，运行期才由 ArgumentParser 补齐（复刻 ScriptProc 路径）。
		Assert.True(ArgumentParser.SetArgumentTo(line));
		var state = new ProcessState(ProcessTrace.For(h.Console));
		line.Function.Instruction.DoInstruction(h.Exm, line, state);
		return line;
	}

	// ---------- 解析 fixture ----------

	[Fact]
	public Task Four_html_text_instructions_parse_without_error()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			foreach (string name in new[] { "HTML_PRINTC", "HTML_PRINTLC", "TEXT_BGC_ON", "TEXT_BGC_OFF" })
				Assert.False(Line(lines, name).IsError, name);
			return Task.CompletedTask;
		});

	[Fact]
	public Task EraTW_printc_variable_fragment_parses_without_error()
		=> WithFunctionAsync("PRINTCFRAG", (h, lines) =>
		{
			// eraTW HTML_PRINT_Components.ERB / QOL_USERCOM.ERB：HTML_PRINTC L_BTN_HTML
			Assert.False(Line(lines, "HTML_PRINTC").IsError);
			return Task.CompletedTask;
		});

	// ---------- 参数解析 fixture ----------

	[Fact]
	public Task HtmlPrintC_parses_optional_cell_width()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			InstructionLine printc = Line(lines, "HTML_PRINTC");
			InstructionLine printlc = Line(lines, "HTML_PRINTLC");

			InstructionLine ran1 = Run(h, printc);
			var arg1 = (SpHtmlPrintC)ran1.Argument;
			Assert.Equal("hello", arg1.Str.GetStrValue(h.Exm));
			Assert.Null(arg1.CellWidth);

			InstructionLine ran2 = Run(h, printlc);
			var arg2 = (SpHtmlPrintC)ran2.Argument;
			Assert.Equal("world", arg2.Str.GetStrValue(h.Exm));
			Assert.Equal(200, arg2.CellWidth!.GetIntValue(h.Exm));
			return Task.CompletedTask;
		});

	[Fact]
	public Task TextBgcOn_parses_rgb_and_alpha()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			var arg = (SpColorAlphaArgument)Run(h, Line(lines, "TEXT_BGC_ON")).Argument;
			Assert.Equal(0xFF0000, arg.RGB.GetIntValue(h.Exm));
			Assert.Equal(50, arg.Alpha.GetIntValue(h.Exm));
			return Task.CompletedTask;
		});

	// ---------- 复用 HTML 管线：真语义 ----------

	[Fact]
	public Task HtmlPrintC_and_PrintLc_route_html_into_print_pipeline()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			Assert.True(h.Console.printBuffer.IsEmpty);
			Run(h, Line(lines, "HTML_PRINTC"));
			Assert.False(h.Console.printBuffer.IsEmpty);
			return Task.CompletedTask;
		});

	// ---------- TEXT_BGC 语义开关 ----------

	[Fact]
	public Task TextBgcOn_sets_color_with_alpha_scaled_to_255()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			Assert.Null(h.Console.TextBackgroundColor);

			Run(h, Line(lines, "TEXT_BGC_ON"));

			// 常量路径：RGB=0xFF0000，alpha=50% → 50*255/100=127。
			Assert.Equal(EmuColor.FromArgb(127, 255, 0, 0), h.Console.TextBackgroundColor);
			return Task.CompletedTask;
		});

	[Fact]
	public Task TextBgcOn_uses_runtime_values_when_not_const()
		=> WithFunctionAsync("BGCNONCONST", (h, lines) =>
		{
			Run(h, Line(lines, "TEXT_BGC_ON"));

			// 变量路径：RGB=FLAG:0(=0)，alpha=100% → 255。
			Assert.Equal(EmuColor.FromArgb(255, 0, 0, 0), h.Console.TextBackgroundColor);
			return Task.CompletedTask;
		});

	[Fact]
	public Task TextBgcOff_clears_color()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			Run(h, Line(lines, "TEXT_BGC_ON"));
			Assert.NotNull(h.Console.TextBackgroundColor);

			Run(h, Line(lines, "TEXT_BGC_OFF"));
			Assert.Null(h.Console.TextBackgroundColor);
			return Task.CompletedTask;
		});
}

/// <summary>
/// Skia 方言 P0：HTML/文本指令的 flag 与 profile 可见性（实现票 23）。只需注册表 + Config scope。
/// </summary>
public sealed class P0HtmlTextInstructionRegistrationTests : IDisposable
{
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	private static FunctionIdentifier Instruction(string name)
		=> FunctionIdentifier.GetInstructionNameDic()[name];

	[Fact]
	public void HtmlPrintC_and_PrintLc_carry_print_flag()
	{
		foreach (string name in new[] { "HTML_PRINTC", "HTML_PRINTLC" })
		{
			FunctionIdentifier fi = Instruction(name);
			// Skia 上游：EXTENDED | METHOD_SAFE + IS_PRINT。
			Assert.True(fi.IsExtended());
			Assert.True(fi.IsPrint());
			Assert.False(fi.IsInput());
			Assert.False(fi.IsJump());
			Assert.NotNull(fi.Instruction);
		}
	}

	[Fact]
	public void TextBgc_instructions_are_extended_without_print_flag()
	{
		foreach (string name in new[] { "TEXT_BGC_ON", "TEXT_BGC_OFF" })
		{
			FunctionIdentifier fi = Instruction(name);
			Assert.True(fi.IsExtended());
			Assert.False(fi.IsPrint());
			Assert.False(fi.IsInput());
			Assert.NotNull(fi.Instruction);
		}
	}

	[Fact]
	public void Html_text_instructions_are_visible_only_when_snake_module_is_selected()
	{
		var instructions = FunctionIdentifier.GetInstructionNameDic();

		var v24 = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snake = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in new[] { "HTML_PRINTC", "HTML_PRINTLC", "TEXT_BGC_ON", "TEXT_BGC_OFF" })
		{
			Assert.False(v24.ContainsKey(name), name);
			Assert.True(snake.ContainsKey(name), name);
		}
		// 既有基线 HTML_PRINT 在 v24pure 下不受影响。
		Assert.True(v24.ContainsKey("HTML_PRINT"));
	}
}