using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言 P0：图像层指令 SETIMAGELAYER / SETIMAGELAYERL / CLEARIMAGELAYER / CLEARIMAGELAYER_ALL
/// （实现票 22，spec §5 / 决策票 06）。
///
/// 证据：
/// - 解析 fixture：四条指令与 eraTW <c>Flan_UI.ERB</c> 片段（含 @"flan_frame_{L_FRAME_GID}"、
///   <c>(0 - L_FRAME_PAD_X - L_SHAPE_SHIFT)</c> 表达式）均可解析、行级无错；
/// - 参数解析 fixture：depth 用 long（-1 与 0 并存）、x/y/w/h/opacity 为 int、L 变体 followScroll 恒定；
/// - 执行 fixture：执行体只做语义接受（no-op）——不触碰 era-core 背景图管线；
/// - flag 反射与 profile 可见性对齐 asset / §4.3。
///
/// ColorMatrix 的 5×5 校验走 <see cref="ColorMatrixReader"/>（SETIMAGELAYER 的参数校验），
/// 由 <see cref="ColorMatrixReaderTests"/> 覆盖。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0ImageLayerInstructionTests
{
	private const string Game = """
@ENTRY
SETIMAGELAYER "spr", 0
SETIMAGELAYER "spr", -1, 10, 20, 30, 40, 128
SETIMAGELAYERL "spr", 1, -5, 6, 0, 0, 255
CLEARIMAGELAYER 3
CLEARIMAGELAYER_ALL
RETURN

@TITLEFRAG
SETIMAGELAYERL @"flan_frame_{1}", 1, (0 - 3 - 1), (0 - 2), 100, 50, 255
RETURN

@PARSECM
#DIM CM55, 5, 5
SETIMAGELAYER "spr", 0, 0, 0, 0, 0, 255, CM55, 1
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
		harness.WriteErb("IMAGELAYER.ERB", Game);
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
		var state = new ProcessState(h.Console);
		line.Function.Instruction.DoInstruction(h.Exm, line, state);
		return line;
	}

	// ---------- 解析 fixture ----------

	[Fact]
	public Task Four_image_layer_instructions_parse_without_error()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			foreach (string name in new[] { "SETIMAGELAYER", "SETIMAGELAYERL", "CLEARIMAGELAYER", "CLEARIMAGELAYER_ALL" })
				Assert.False(Line(lines, name).IsError, name);
			return Task.CompletedTask;
		});

	[Fact]
	public Task EraTW_flan_ui_fragment_parses_without_error()
		=> WithFunctionAsync("TITLEFRAG", (h, lines) =>
		{
			// Flan_UI.ERB：SETIMAGELAYERL spriteName, depth, xpos, ypos, width, height, opacity
			Assert.False(Line(lines, "SETIMAGELAYERL").IsError);
			return Task.CompletedTask;
		});

	[Fact]
	public Task SetImageLayer_parses_array_argument_without_error()
		=> WithFunctionAsync("PARSECM", (h, lines) =>
		{
			// 第 8 实参为 2D 整数数组（ColorMatrix）：解析期以 typeof(void) 放行，运行期再校验 5×5。
			Assert.False(Line(lines, "SETIMAGELAYER").IsError);
			return Task.CompletedTask;
		});

	// ---------- 参数解析 fixture ----------

	[Fact]
	public Task SetImageLayer_parses_all_optional_arguments()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			InstructionLine line = Run(h, Line(lines, "SETIMAGELAYER"));
			var arg = (SpSetImageLayerArgument)line.Argument;

			// 2 参形态：spriteName + depth=0，其余省略。
			Assert.Equal("spr", arg.SpriteName.GetStrValue(h.Exm));
			Assert.Equal(0, arg.Depth.GetIntValue(h.Exm));
			Assert.Null(arg.X);
			Assert.Null(arg.Y);
			Assert.Null(arg.Opacity);
			Assert.Null(arg.CMArray);
			Assert.Null(arg.FollowScroll);
			return Task.CompletedTask;
		});

	[Fact]
	public Task SetImageLayerL_keeps_depth_long_and_optional_positionals()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			InstructionLine line = Run(h, Line(lines, "SETIMAGELAYERL"));
			var arg = (SpSetImageLayerArgument)line.Argument;

			// depth 用 long 且允许负值；xpos/ypos 为 int；opacity=255；L 变体不接受 followScroll。
			Assert.Equal(1, arg.Depth.GetIntValue(h.Exm));
			Assert.Equal(-5, arg.X!.GetIntValue(h.Exm));
			Assert.Equal(6, arg.Y!.GetIntValue(h.Exm));
			Assert.Equal(0, arg.Width!.GetIntValue(h.Exm));
			Assert.Equal(0, arg.Height!.GetIntValue(h.Exm));
			Assert.Equal(255, arg.Opacity!.GetIntValue(h.Exm));
			Assert.Null(arg.FollowScroll);
			return Task.CompletedTask;
		});

	// ---------- 执行 fixture：语义接受 no-op ----------

	[Fact]
	public Task Executing_image_layer_instructions_does_not_touch_background_pipeline()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			// era-core 无 ImageLayer 抽象：这些指令既不抛错，也不影响既有背景图（CBG/背景管线解耦）。
			h.Console.SetBgImage("bg", 0, 1f);
			int before = h.Console.BgImages.Count;

			foreach (string name in new[] { "SETIMAGELAYER", "SETIMAGELAYERL" })
				Run(h, Line(lines, name));
			Run(h, Line(lines, "CLEARIMAGELAYER"));
			Run(h, Line(lines, "CLEARIMAGELAYER_ALL"));

			Assert.Equal(before, h.Console.BgImages.Count);
			return Task.CompletedTask;
		});

	[Fact]
	public Task ClearImageLayer_parses_depth_expression()
		=> WithFunctionAsync("ENTRY", (h, lines) =>
		{
			InstructionLine line = Run(h, Line(lines, "CLEARIMAGELAYER"));
			var arg = (ExpressionArgument)line.Argument;
			Assert.Equal(3, arg.Term.GetIntValue(h.Exm));
			return Task.CompletedTask;
		});
}

/// <summary>
/// ColorMatrix 读取 / 5×5 校验的深模块用例（实现票 22）：SETIMAGELAYER 的 ColorMatrix 参数校验即本模块。
/// 2D/3D 整数数组按 GDI+ 惯例解释（元素 ÷256）；越界 / 不足 5×5 / 非数组均给明确错误消息。
/// </summary>
[Collection("LoaderTests")]
public sealed class ColorMatrixReaderTests : IDisposable
{
	private readonly LoaderTestHarness _harness = new();

	public ColorMatrixReaderTests() => _harness.InitializeAsync().GetAwaiter().GetResult();

	public void Dispose() => _harness.Dispose();

	private ExpressionMediator Exm => _harness.Exm;

	private sealed class StubArrayVariableToken(VariableCode code, object array) : VariableToken(code, null!)
	{
		private readonly object _array = array;
		public override object GetArray() => _array;
	}

	private static VariableTerm Array2D(long[,] array)
		=> new(new StubArrayVariableToken(VariableCode.__INTEGER__ | VariableCode.__ARRAY_2D__, array),
			[new SingleLongTerm(0), new SingleLongTerm(0)]);

	private static VariableTerm Array3D(long[,,] array)
		=> new(new StubArrayVariableToken(VariableCode.__INTEGER__ | VariableCode.__ARRAY_3D__, array),
			[new SingleLongTerm(0), new SingleLongTerm(0), new SingleLongTerm(0)]);

	private static VariableTerm Array1D(long[] array)
		=> new(new StubArrayVariableToken(VariableCode.__INTEGER__ | VariableCode.__ARRAY_1D__, array),
			[new SingleLongTerm(0)]);

	[Fact]
	public void Reads_5x5_2d_integer_array_with_256_scale()
	{
		var array = new long[5, 5];
		array[0, 0] = 512;   // 2.0
		array[4, 4] = 256;   // 1.0

		float[][] cm = ColorMatrixReader.Read("SETIMAGELAYER", 8, Array2D(array), Exm);

		Assert.Equal(5, cm.Length);
		Assert.All(cm, row => Assert.Equal(5, row.Length));
		Assert.Equal(2f, cm[0][0]);
		Assert.Equal(1f, cm[4][4]);
	}

	[Fact]
	public void Reads_3d_integer_array()
	{
		var array = new long[1, 5, 5];
		float[][] cm = ColorMatrixReader.Read("SETIMAGELAYER", 8, Array3D(array), Exm);

		Assert.Equal(5, cm.Length);
		Assert.All(cm, row => Assert.Equal(5, row.Length));
	}

	[Fact]
	public void Rejects_2d_array_smaller_than_5x5_with_clear_message()
	{
		var ex = Assert.Throws<CodeEE>(() =>
			ColorMatrixReader.Read("SETIMAGELAYER", 8, Array2D(new long[3, 3]), Exm));

		// 消息含函数名与 5x5 提示（跨语言稳定子串）。
		Assert.Contains("SETIMAGELAYER", ex.Message);
		Assert.Contains("5x5", ex.Message);
		Assert.Contains("ColorMatrix", ex.Message);
	}

	[Fact]
	public void Rejects_3d_array_smaller_than_5x5_with_clear_message()
	{
		var ex = Assert.Throws<CodeEE>(() =>
			ColorMatrixReader.Read("SETIMAGELAYERL", 8, Array3D(new long[1, 4, 5]), Exm));

		Assert.Contains("SETIMAGELAYERL", ex.Message);
		Assert.Contains("5x5", ex.Message);
	}

	[Fact]
	public void Rejects_non_nd_array_with_clear_message()
	{
		var ex = Assert.Throws<CodeEE>(() =>
			ColorMatrixReader.Read("SETIMAGELAYER", 8, Array1D(new long[25]), Exm));

		Assert.Contains("SETIMAGELAYER", ex.Message);
	}
}

/// <summary>
/// Skia 方言 P0：图像层指令的 flag 与 profile 可见性（实现票 22）。只需注册表 + Config scope。
/// </summary>
public sealed class P0ImageLayerInstructionRegistrationTests : IDisposable
{
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	private static FunctionIdentifier Instruction(string name)
		=> FunctionIdentifier.GetInstructionNameDic()[name];

	[Theory]
	[InlineData("SETIMAGELAYER")]
	[InlineData("SETIMAGELAYERL")]
	[InlineData("CLEARIMAGELAYER")]
	[InlineData("CLEARIMAGELAYER_ALL")]
	public void Registered_flags_match_asset(string name)
	{
		FunctionIdentifier fi = Instruction(name);

		// Skia 上游 flag = METHOD_SAFE | EXTENDED；非打印/输入/跳转。
		Assert.True(fi.IsExtended());
		Assert.False(fi.IsPrint());
		Assert.False(fi.IsInput());
		Assert.False(fi.IsJump());
		Assert.False(fi.IsTry());
		Assert.NotNull(fi.Instruction);
	}

	[Fact]
	public void Image_layer_instructions_are_visible_only_when_snake_module_is_selected()
	{
		var instructions = FunctionIdentifier.GetInstructionNameDic();

		var v24 = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snake = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in new[] { "SETIMAGELAYER", "SETIMAGELAYERL", "CLEARIMAGELAYER", "CLEARIMAGELAYER_ALL" })
		{
			Assert.False(v24.ContainsKey(name), name);
			Assert.True(snake.ContainsKey(name), name);
		}
		// 既有基线背景指令在 v24pure 下不受影响。
		Assert.True(v24.ContainsKey("SETBGIMAGE"));
		Assert.True(v24.ContainsKey("CLEARBGIMAGE"));
	}
}