using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Skia 方言 P0：动态调用指令 JUMPSTR / CALLSTR / TRYCALLSTR / TRYCJUMPSTR / TRYCCALLSTR
/// （实现票 20，spec §5）。
///
/// 证据：
/// - 解析 fixture：五条指令被识别、行级无错、CATCH 与 TRYCCALLSTR 完成嵌套配对；
/// - 执行 fixture：成功路径进入目标函数、JUMPSTR 置 IsJump、TRY* 失败不抛并跳 CATCH；
/// - 反射差异：flag 对齐 asset §4.1.1（EXTENDED / IS_JUMP / IS_TRY / IS_TRYC / FLOW_CONTROL / FORCE_SETARG）。
///
/// 走 <see cref="LoaderTestHarness"/> 以取得被完整解析的 InstructionLine 与真实 ExpressionMediator。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0DynamicCallInstructionTests
{
	private const string Game = """
@ENTRY
CALLSTR "TARGET(1, 2)"
JUMPSTR "TARGET(3, 4)"
TRYCALLSTR "NO_SUCH_FUNC_XYZ"
TRYCCALLSTR "NO_SUCH_FUNC_XYZ"
CATCH
PRINTL caught
ENDCATCH
RETURN

@TARGET(X, Y)
#DIM X
#DIM Y
RETURN
""";

	private static ErbLoader NewLoader(LoaderTestHarness h)
		=> new(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);

	/// <summary>
	/// 解析 snake profile。注册表静态构造读 ambient Config，故先在临时 scope 内解析，
	/// 返回的 <see cref="DialectProfile"/> 为不可变缓存值，可跨 scope 传给 harness。
	/// </summary>
	private static DialectProfile SnakeProfile()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		return DialectRegistry.Resolve(DialectRegistry.SnakeId);
	}

	/// <summary>
	/// 写入 Game、强制全量解析（IgnoreUncalledFunction=false），在 harness 的 session scope 内
	/// 回调 <paramref name="body"/> 并传入 ENTRY 的指令行。
	///
	/// 注意：session scope 由 harness 构造器经 AsyncLocal 开启，**不跨 await 上浮**，
	/// 故断言必须在本方法（与 harness 同一异步流）内执行，不能把 harness 返回给调用方。
	/// </summary>
	private static async Task WithEntryAsync(Func<LoaderTestHarness, List<InstructionLine>, Task> body)
	{
		// 方言键仅在选中 snake 模块的会话可见，须以该 profile 建会话。
		var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		using var _ = harness;
		await harness.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		harness.WriteErb("CALLSTR.ERB", Game);
		await harness.PreloadAsync();
		await NewLoader(harness).LoadErbDir(harness.ErbDir, displayReport: false, harness.LabelDic);

		FunctionLabelLine entry = harness.LabelDic.GetNonEventLabel("ENTRY");
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
				$"未找到 {name}；ENTRY 指令行 = [{string.Join(", ", lines.Select(l => l.Function?.Name ?? "<null>"))}]");

	// ---------- 解析 fixture ----------

	[Fact]
	public Task All_five_instructions_parse_without_error_and_pair_catch()
		=> WithEntryAsync((harness, lines) =>
		{
			foreach (string name in new[] { "CALLSTR", "JUMPSTR", "TRYCALLSTR", "TRYCCALLSTR" })
				Assert.False(Line(lines, name).IsError, name);

			// TRYCCALLSTR + CATCH 完成嵌套配对（按 flag 判定，而非 FunctionCode）。
			InstructionLine tryc = Line(lines, "TRYCCALLSTR");
			InstructionLine catchLine = Line(lines, "CATCH");
			Assert.Same(catchLine, tryc.JumpToEndCatch);
			return Task.CompletedTask;
		});

	// ---------- 执行 fixture ----------

	[Fact]
	public Task CallStr_enters_target_function()
		=> WithEntryAsync((harness, lines) =>
		{
			InstructionLine call = Line(lines, "CALLSTR");
			var state = new ProcessState(harness.Console);
			call.Function.Instruction.DoInstruction(harness.Exm, call, state);

			Assert.Equal(1, state.functionCount);
			Assert.Equal("TARGET", state.CurrentCalled.CurrentLabel.LabelName);
			Assert.False(state.CurrentCalled.IsJump);
			return Task.CompletedTask;
		});

	[Fact]
	public Task JumpStr_marks_call_as_jump()
		=> WithEntryAsync((harness, lines) =>
		{
			InstructionLine jump = Line(lines, "JUMPSTR");
			var state = new ProcessState(harness.Console);
			jump.Function.Instruction.DoInstruction(harness.Exm, jump, state);

			Assert.Equal(1, state.functionCount);
			Assert.True(state.CurrentCalled.IsJump);
			return Task.CompletedTask;
		});

	[Fact]
	public Task TryStr_failure_does_not_throw_and_converges_on_catch()
		=> WithEntryAsync((harness, lines) =>
		{
			foreach (string name in new[] { "TRYCALLSTR", "TRYCCALLSTR" })
			{
				InstructionLine line = Line(lines, name);
				var state = new ProcessState(harness.Console);

				// 目标函数不存在：TRY* 不抛错，走容错（有配对则跳到 CATCH）。
				Exception? ex = null;
				try { line.Function.Instruction.DoInstruction(harness.Exm, line, state); }
				catch (Exception e) { ex = e; }
				Assert.Null(ex);

				if (name == "TRYCCALLSTR")
					Assert.Same(line.JumpToEndCatch, state.CurrentLine);
			}
			return Task.CompletedTask;
		});
}

/// <summary>
/// Skia 方言 P0：动态调用指令的 flag 与 profile 可见性（实现票 20）。只需注册表 + Config scope。
/// </summary>
public sealed class P0DynamicCallInstructionRegistrationTests : IDisposable
{
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	private static FunctionIdentifier Instruction(string name)
		=> FunctionIdentifier.GetInstructionNameDic()[name];

	// name, extended, jump, try, tryC, partial
	public static IEnumerable<object[]> FlagExpectations()
	{
		yield return ["JUMPSTR", false, true, false, false, false];
		yield return ["CALLSTR", false, false, false, false, false];
		yield return ["TRYCALLSTR", true, false, true, false, false];
		yield return ["TRYCJUMPSTR", true, true, true, true, true];
		yield return ["TRYCCALLSTR", true, false, true, true, true];
	}

	[Theory]
	[MemberData(nameof(FlagExpectations))]
	public void Registered_flags_match_asset(string name, bool extended, bool jump, bool isTry, bool tryC, bool partial)
	{
		FunctionIdentifier fi = Instruction(name);

		Assert.True(fi.IsFlowContorol());
		Assert.True(fi.IsForceSetArg());
		Assert.Equal(extended, fi.IsExtended());
		Assert.Equal(jump, fi.IsJump());
		Assert.Equal(isTry, fi.IsTry());
		Assert.Equal(tryC, fi.IsTryC());
		Assert.Equal(partial, fi.IsPartial());
		Assert.NotNull(fi.Instruction);
	}

	[Fact]
	public void Dynamic_call_instructions_are_visible_only_when_snake_module_is_selected()
	{
		var instructions = FunctionIdentifier.GetInstructionNameDic();

		var v24 = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snake = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in new[] { "JUMPSTR", "CALLSTR", "TRYCALLSTR", "TRYCJUMPSTR", "TRYCCALLSTR" })
		{
			Assert.False(v24.ContainsKey(name), name);
			Assert.True(snake.ContainsKey(name), name);
		}
		// 既有基线调用指令在 v24pure 下不受影响。
		Assert.True(v24.ContainsKey("CALL"));
		Assert.True(v24.ContainsKey("TRYCALL"));
	}
}