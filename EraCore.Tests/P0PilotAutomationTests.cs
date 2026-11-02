using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 自动移植试点（tools/dialect-inventory/classify.py 的 A/B 类样本）：
/// TRYJUMPSTR（A）、STRFORMCHECK（A）、MATCHALL / MATCHALLEX（B）。
///
/// 每键至少一条解析 fixture + 一条行为 fixture；行为断言直调 FunctionMethod，
/// 避免依赖完整游戏循环。MATCHALLEX 与 MATCHALL 共用同一实现类，
/// 行为差异仅在「首参是变量名还是变量引用」，故用错误路径覆盖其独立分支。
/// </summary>
[Collection("LoaderTests")]
public sealed class P0PilotAutomationTests
{
	private const string Game = """
@ENTRY
TRYJUMPSTR "TARGET(1)"
PRINTL STRFORMCHECK("abc")
RETURN

@TARGET(X)
#DIM X
RETURN
""";

	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static SingleStrTerm S(string v) => new(v);
	private static SingleLongTerm I(long v) => new(v);

	/// <summary>只读整数一维数组的 VariableToken 桩：供 MATCHALL 直接取值与取长度。</summary>
	private sealed class IntArrayToken(long[] values, VariableCode code = VariableCode.__INTEGER__ | VariableCode.__ARRAY_1D__)
		: VariableToken(code, null!)
	{
		private readonly long[] _values = values;
		public override object GetArray() => _values;
		public override int GetLength(int dimension) => _values.Length;
		public override long GetIntValue(ExpressionMediator exm, long[] arguments) => _values[arguments[0]];
		public override string GetStrValue(ExpressionMediator exm, long[] arguments) => _values[arguments[0]].ToString();
	}

	private static VariableTerm IntArrayVar(long[] values)
		=> new(new IntArrayToken(values), Array.Empty<AExpression>());

	private static VariableTerm CharDataIntArrayVar(long[] values)
		=> new(new IntArrayToken(values,
			VariableCode.__INTEGER__ | VariableCode.__ARRAY_1D__ | VariableCode.__CHARACTER_DATA__),
			Array.Empty<AExpression>());

	private static ErbLoader NewLoader(LoaderTestHarness h)
		=> new(h.Console, h.Exm, h.IdDic, h.Env, line => h.Process.scaningLine = line);

	private static DialectProfile SnakeProfile()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());
		return DialectRegistry.Resolve(DialectRegistry.SnakeId);
	}

	private static async Task WithEntryAsync(Func<LoaderTestHarness, List<InstructionLine>, Task> body)
	{
		var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		using var _ = harness;
		await harness.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		harness.WriteErb("PILOT.ERB", Game);
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

	// ---------- TRYJUMPSTR ----------

	[Fact]
	public Task TryJumpStr_parses_without_error()
		=> WithEntryAsync((harness, lines) =>
		{
			// STRFORMCHECK 是式中函数，不是独立指令行；其解析可用性由行为 fixture 覆盖。
			Assert.False(Line(lines, "TRYJUMPSTR").IsError);
			Assert.False(Line(lines, "PRINTL").IsError);
			return Task.CompletedTask;
		});

	[Fact]
	public Task TryJumpStr_success_enters_target_and_marks_jump()
		=> WithEntryAsync((harness, lines) =>
		{
			InstructionLine line = Line(lines, "TRYJUMPSTR");
			var state = new ProcessState(ProcessTrace.For(harness.Console));
			line.Function.Instruction.DoInstruction(harness.Exm, line, state);

			Assert.Equal(1, state.functionCount);
			Assert.Equal("TARGET", state.CurrentCalled.CurrentLabel.LabelName);
			Assert.True(state.CurrentCalled.IsJump);
			return Task.CompletedTask;
		});

	[Fact]
	public Task TryJumpStr_failure_does_not_throw()
		=> WithEntryAsync((harness, lines) =>
		{
			// 同族 TRYCALLSTR 的失败语义：TRY* 不抛错，仅走容错路径。
			var state = new ProcessState(ProcessTrace.For(harness.Console));
			Exception? ex = null;
			try
			{
				Line(lines, "TRYJUMPSTR").Function.Instruction.DoInstruction(harness.Exm, Line(lines, "TRYJUMPSTR"), state);
			}
			catch (Exception e) { ex = e; }
			Assert.Null(ex);
			return Task.CompletedTask;
		});

	// ---------- STRFORMCHECK ----------

	[Fact]
	public async Task StrFormCheck_returns_one_for_plain_text_and_zero_for_broken_form()
	{
		using var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		await harness.InitializeAsync();
		var exm = harness.Exm;

		// 纯文本：无可展开占位符，解析成功。
		Assert.Equal(1, Method("STRFORMCHECK").GetIntValue(exm, Args(S("abc"))));
		// 合法 %…% 形式：引用的标准变量存在 → 1。
		Assert.Equal(1, Method("STRFORMCHECK").GetIntValue(exm, Args(S("%RESULTS%"))));
		// 未闭合的 %：格式化解析阶段抛错 → 0。
		Assert.Equal(0, Method("STRFORMCHECK").GetIntValue(exm, Args(S("%"))));
		// %…% 引用不存在的变量：求值抛错 → 0。
		Assert.Equal(0, Method("STRFORMCHECK").GetIntValue(exm, Args(S("%NO_SUCH_VAR_ZZZ%"))));
	}

	// ---------- MATCHALL / MATCHALLEX ----------

	[Fact]
	public async Task MatchAll_counts_matching_int_values_in_range()
	{
		using var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		await harness.InitializeAsync();
		var exm = harness.Exm;

		long[] data = [1, 5, 5, 7, 5];
		var variable = IntArrayVar(data);

		Assert.Equal(3, Method("MATCHALL").GetIntValue(exm, Args(variable, I(5))));
		// 区间 [1, 4)：索引 1,2,3 中只有两个 5。
		Assert.Equal(2, Method("MATCHALL").GetIntValue(exm, Args(variable, I(5), I(1), I(4))));
		// end 超过数组长度 → 截断到 len，结果与全区间一致。
		Assert.Equal(3, Method("MATCHALL").GetIntValue(exm, Args(variable, I(5), I(0), I(999))));
		// 无匹配。
		Assert.Equal(0, Method("MATCHALL").GetIntValue(exm, Args(variable, I(99))));
	}

	[Fact]
	public async Task MatchAll_rejects_negative_or_reversed_range()
	{
		using var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		await harness.InitializeAsync();
		var exm = harness.Exm;

		var variable = IntArrayVar([1, 5, 5, 7, 5]);

		var negative = Assert.Throws<CodeEE>(() =>
		{
			Method("MATCHALL").GetIntValue(exm, Args(variable, I(5), I(-1)));
		});
		Assert.Equal("検索範囲に負の値が渡されました", negative.Message);

		var reversed = Assert.Throws<CodeEE>(() =>
		{
			Method("MATCHALL").GetIntValue(exm, Args(variable, I(5), I(3), I(1)));
		});
		Assert.Equal("検索範囲の指定が不正です", reversed.Message);
	}

	[Fact]
	public async Task MatchAll_uses_chara_num_for_character_data_length()
	{
		using var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		await harness.InitializeAsync();
		var exm = harness.Exm;

		// 字符变量长度取 CHARANUM 而非底层数组长度。空会话 CHARANUM=0：
		// 若实现误用 GetLength(0)，[5,5,5] 会返回 3；取 CHARANUM 则区间为空 → 0。
		Assert.Equal(0, exm.VEvaluator.CHARANUM);
		Assert.Equal(0, Method("MATCHALL").GetIntValue(exm, Args(CharDataIntArrayVar([5, 5, 5]), I(5))));
	}

	[Fact]
	public async Task MatchAllEx_throws_for_unknown_variable_name()
	{
		using var harness = new LoaderTestHarness(dialectProfile: SnakeProfile());
		await harness.InitializeAsync();
		var exm = harness.Exm;

		var error = Assert.Throws<CodeEE>(() =>
			Method("MATCHALLEX").GetIntValue(exm, Args(S("NO_SUCH_VAR_ZZZ"), I(1))));
		Assert.Contains("NO_SUCH_VAR_ZZZ", error.Message);
	}

	// ---------- 参数规则（自定义 CheckArgumentType） ----------

	[Fact]
	public void MatchAll_rejects_invalid_arity_and_first_argument()
	{
		Assert.NotNull(Method("MATCHALL").CheckArgumentType("MATCHALL", Args(I(1))));
		Assert.NotNull(Method("MATCHALLEX").CheckArgumentType("MATCHALLEX", Args(I(1), I(2))));
	}

	// ---------- profile 可见性 ----------

	[Fact]
	public void Pilot_keys_are_visible_only_when_snake_module_is_selected()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		var methods = FunctionMethodCreator.GetMethodList();
		var instructions = FunctionIdentifier.GetInstructionNameDic();

		var v24Methods = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snakeMethods = DialectProjection.Filter(methods, DialectRegistry.Resolve(DialectRegistry.SnakeId));
		var v24Instructions = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.V24PureId));
		var snakeInstructions = DialectProjection.Filter(instructions, DialectRegistry.Resolve(DialectRegistry.SnakeId));

		foreach (string name in new[] { "STRFORMCHECK", "MATCHALL", "MATCHALLEX" })
		{
			Assert.False(v24Methods.ContainsKey(name), name);
			Assert.True(snakeMethods.ContainsKey(name), name);
		}
		Assert.False(v24Instructions.ContainsKey("TRYJUMPSTR"));
		Assert.True(snakeInstructions.ContainsKey("TRYJUMPSTR"));
	}
}

/// <summary>TRYJUMPSTR 的 flag 与其它五个 CALLSTR 族键对齐（上游 addFunction 仅多一条注册）。</summary>
public sealed class P0TryJumpStrFlagTests : IDisposable
{
	private readonly IDisposable _scope = GlobalStatic.OpenScope(new ConfigData());

	public void Dispose() => _scope.Dispose();

	[Fact]
	public void Try_jump_str_flags_match_same_family()
	{
		FunctionIdentifier fi = FunctionIdentifier.GetInstructionNameDic()["TRYJUMPSTR"];

		Assert.True(fi.IsFlowContorol());
		Assert.True(fi.IsForceSetArg());
		Assert.True(fi.IsExtended());
		Assert.True(fi.IsJump());
		Assert.True(fi.IsTry());
		Assert.False(fi.IsTryC());
		Assert.False(fi.IsPartial());
		Assert.NotNull(fi.Instruction);
	}
}
