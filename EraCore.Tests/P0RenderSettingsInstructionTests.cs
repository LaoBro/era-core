using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言：渲染设置指令与 getter（实现票 35）。
/// <c>SET_TEXT_DRAWING_MODE</c> 真语义（写 Config.TextDrawingMode），
/// <c>SET_SKIA_QUALITY</c> / <c>STRICT_FONT_FALLBACK</c> 为 StateOnly（保存状态，渲染线未消费）。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0RenderSettingsInstructionTests
{
	private const string Game = """
@ENTRY
SET_TEXT_DRAWING_MODE 3
SET_SKIA_QUALITY 1, 2, 0
STRICT_FONT_FALLBACK 1
RETURN

@IGNORED
SET_TEXT_DRAWING_MODE 0
SET_SKIA_QUALITY 2
STRICT_FONT_FALLBACK 0
RETURN
""";

	private static ErbLoader NewLoader(LoaderTestHarness h)
		=> new(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);

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
		harness.WriteErb("RENDERSET.ERB", Game);
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
		Assert.True(ArgumentParser.SetArgumentTo(line));
		var state = new ProcessState(ProcessTrace.For(h.Console));
		line.Function.Instruction.DoInstruction(h.Exm, line, state);
		return line;
	}

	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	[Fact]
	public Task Three_render_setting_instructions_parse_without_error()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			foreach (string name in new[] { "SET_TEXT_DRAWING_MODE", "SET_SKIA_QUALITY", "STRICT_FONT_FALLBACK" })
				Assert.False(Line(lines, name).IsError, name);
			return Task.CompletedTask;
		});

	[Fact]
	public Task SetTextDrawingMode_writes_mode_3_and_getter_reads_it_back()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			Assert.Equal(TextDrawingMode.TEXTRENDERER, Config.TextDrawingMode);

			Run(h, Line(lines, "SET_TEXT_DRAWING_MODE"));

			Assert.Equal(TextDrawingMode.SKIASHARP, Config.TextDrawingMode);
			Assert.Equal(3, Method("GET_TEXT_DRAWING_MODE").GetIntValue(h.Exm, Args()));
			return Task.CompletedTask;
		});

	[Fact]
	public Task SetTextDrawingMode_ignores_zero_and_two()
		=> WithFunctionAsync("IGNORED", (h, lines) =>
		{
			Run(h, Line(lines, "SET_TEXT_DRAWING_MODE"));
			Assert.Equal(TextDrawingMode.TEXTRENDERER, Config.TextDrawingMode);
			return Task.CompletedTask;
		});

	[Fact]
	public Task SetSkiaQuality_stores_three_slots_and_getter_reads_them()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			// Skia 默认：ImageQuality=High(3)、FontHinting=None(0)、FontEdging=SubpixelAntiAlias(2)。
			Assert.Equal(3, Method("GET_SKIA_QUALITY").GetIntValue(h.Exm, Args(new SingleLongTerm(0))));
			Assert.Equal(0, Method("GET_SKIA_QUALITY").GetIntValue(h.Exm, Args(new SingleLongTerm(1))));
			Assert.Equal(2, Method("GET_SKIA_QUALITY").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));

			Run(h, Line(lines, "SET_SKIA_QUALITY"));

			Assert.Equal(1, h.Console.ImageQuality);
			Assert.Equal(2, h.Console.FontHinting);
			Assert.Equal(0, h.Console.FontEdging);
			Assert.Equal(1, Method("GET_SKIA_QUALITY").GetIntValue(h.Exm, Args(new SingleLongTerm(0))));
			Assert.Equal(2, Method("GET_SKIA_QUALITY").GetIntValue(h.Exm, Args(new SingleLongTerm(1))));
			Assert.Equal(0, Method("GET_SKIA_QUALITY").GetIntValue(h.Exm, Args(new SingleLongTerm(2))));
			Assert.Equal(-1, Method("GET_SKIA_QUALITY").GetIntValue(h.Exm, Args(new SingleLongTerm(9))));
			return Task.CompletedTask;
		});

	[Fact]
	public Task SetSkiaQuality_with_partial_args_leaves_other_slots_untouched()
		=> WithFunctionAsync("IGNORED", (h, lines) =>
		{
			Run(h, Line(lines, "SET_SKIA_QUALITY"));
			Assert.Equal(2, h.Console.ImageQuality);
			Assert.Equal(0, h.Console.FontHinting);
			Assert.Equal(2, h.Console.FontEdging);
			return Task.CompletedTask;
		});

	[Fact]
	public Task StrictFontFallback_toggles_state()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			Assert.False(h.Console.StrictFontFallback);

			Run(h, Line(lines, "STRICT_FONT_FALLBACK"));
			Assert.True(h.Console.StrictFontFallback);
			return Task.CompletedTask;
		});

	[Fact]
	public Task StrictFontFallback_zero_clears_state()
		=> WithFunctionAsync("IGNORED", (h, lines) =>
		{
			h.Console.StrictFontFallback = true;

			Run(h, Line(lines, "STRICT_FONT_FALLBACK"));
			Assert.False(h.Console.StrictFontFallback);
			return Task.CompletedTask;
		});
}

/// <summary>实现票 35：渲染设置键的 flag 与 profile 可见性（只需注册表 + Config scope）。</summary>
public sealed class P0RenderSettingsRegistrationTests : IDisposable
{
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	private static readonly string[] InstructionNames =
		["SET_TEXT_DRAWING_MODE", "SET_SKIA_QUALITY", "STRICT_FONT_FALLBACK"];

	private static readonly string[] FunctionNames =
		["GET_TEXT_DRAWING_MODE", "GET_SKIA_QUALITY"];

	[Fact]
	public void Instructions_are_extended_without_print_or_input_flags()
	{
		var instructions = FunctionIdentifier.GetInstructionNameDic();
		foreach (string name in InstructionNames)
		{
			FunctionIdentifier fi = instructions[name];
			Assert.True(fi.IsExtended(), name);
			Assert.False(fi.IsPrint(), name);
			Assert.False(fi.IsInput(), name);
			Assert.NotNull(fi.Instruction);
		}
	}

	[Fact]
	public void Getters_return_long_and_are_not_restructurable()
	{
		foreach (string name in FunctionNames)
		{
			FunctionMethod method = FunctionMethodCreator.GetMethodList()[name];
			Assert.Equal(typeof(long), method.ReturnType);
			Assert.False(method.CanRestructure, name);
		}
	}

	[Fact]
	public void Render_setting_keys_are_visible_only_when_snake_module_is_selected()
	{
		var instructions = FunctionIdentifier.GetInstructionNameDic();
		var methods = FunctionMethodCreator.GetMethodList();

		var v24Instructions = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snakeInstructions = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.SnakeId));
		var v24Methods = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snakeMethods = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in InstructionNames)
		{
			Assert.False(v24Instructions.ContainsKey(name), name);
			Assert.True(snakeInstructions.ContainsKey(name), name);
		}
		foreach (string name in FunctionNames)
		{
			Assert.False(v24Methods.ContainsKey(name), name);
			Assert.True(snakeMethods.ContainsKey(name), name);
		}
	}
}
