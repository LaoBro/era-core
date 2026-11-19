using System;
using System.Collections.Generic;
using System.Linq;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 票 27：浮点能力（浮点字面量 / 值类型 / 运算符 / 函数动态返回）。
/// 端到端夹具见 <c>test_game_float</c>（tests/games.json）。
/// </summary>
public sealed class FloatCapabilityTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static List<Word> Lex(string text)
		=> [.. LexicalAnalyzer.Analyse(new CharStream(text), LexEndWith.EoL, LexAnalyzeFlag.None).Collection];

	// ---------- 能力位 ----------

	[Fact]
	public void Float_capability_is_on_for_snake_and_off_for_v24pure()
	{
		using (GlobalStatic.OpenScope(new ConfigData(), "v24pure"))
			Assert.False(GlobalStatic.LanguageCapabilities.HasFlag(LanguageCapability.Float));

		using (GlobalStatic.OpenScope(new ConfigData(), "snake"))
			Assert.True(GlobalStatic.LanguageCapabilities.HasFlag(LanguageCapability.Float));
	}

	// ---------- 字面量判定（对齐 Skia：数字后紧邻 '.'） ----------

	[Theory]
	[InlineData("1.0", 1.0)]
	[InlineData("1.2", 1.2)]
	[InlineData("0.01", 0.01)]
	[InlineData("1.", 1.0)]
	[InlineData("1.0e3", 1000.0)]
	public void Float_literal_is_recognized_when_digit_is_followed_by_dot(string text, double expected)
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), "snake");
		var word = Assert.IsType<LiteralFloatWord>(Lex(text)[0]);
		Assert.Equal(expected, word.Float, 10);
	}

	[Fact]
	public void Exponent_without_dot_stays_integer()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), "snake");
		var word = Assert.IsType<LiteralIntegerWord>(Lex("1e4")[0]);
		Assert.Equal(10000L, word.Int);
	}

	[Fact]
	public void Leading_dot_is_not_a_float_literal()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), "snake");
		var symbol = Assert.IsType<SymbolWord>(Lex(".5")[0]);
		Assert.Equal('.', symbol.Type);
	}

	[Fact]
	public void Float_literal_is_not_recognized_without_capability()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), "v24pure");
		var words = Lex("1.0");
		Assert.IsType<LiteralIntegerWord>(words[0]);
		Assert.IsType<SymbolWord>(words[1]);
		Assert.IsType<LiteralIntegerWord>(words[2]);
	}

	// ---------- 运算符 ----------

	[Fact]
	public void Mixed_arithmetic_widens_to_float()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), "snake");
		var term = OperatorMethodManager.ReduceBinaryTerm(OperatorCode.Mult, new SingleFloatTerm(1.5), new SingleLongTerm(2));
		Assert.Equal(typeof(double), term.GetOperandType());
		Assert.Equal(3.0, term.GetFloatValue(null!));
	}

	[Fact]
	public void Float_division_is_float_division()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), "snake");
		var term = OperatorMethodManager.ReduceBinaryTerm(OperatorCode.Div, new SingleFloatTerm(3.0), new SingleLongTerm(2));
		Assert.Equal(1.5, term.GetFloatValue(null!));
	}

	[Fact]
	public void Comparison_returns_integer()
	{
		using var scope = GlobalStatic.OpenScope(new ConfigData(), "snake");
		var term = OperatorMethodManager.ReduceBinaryTerm(OperatorCode.Greater, new SingleFloatTerm(1.2), new SingleLongTerm(1));
		Assert.Equal(typeof(long), term.GetOperandType());
		Assert.Equal(1L, term.GetIntValue(null!));
	}

	[Fact]
	public void Ternary_accepts_int_condition_and_float_branches()
	{
		using var scope = GlobalScopeWithProfile("snake");
		var term = OperatorMethodManager.ReduceTernaryTerm(
			new SingleLongTerm(1), new SingleFloatTerm(2.5), new SingleFloatTerm(3.5));
		Assert.Equal(typeof(double), term.GetOperandType());
		Assert.Equal(2.5, term.GetFloatValue(null!));
	}

	// ---------- 函数 ----------

	[Fact]
	public void Float_capable_function_returns_float_when_any_argument_is_float()
	{
		using var scope = GlobalScopeWithProfile("snake");
		var term = new FunctionMethodTerm(Method("POWER"), Args(new SingleFloatTerm(1.4), new SingleLongTerm(2)));
		Assert.Equal(typeof(double), term.GetOperandType());
		Assert.Equal(Math.Pow(1.4, 2), term.GetFloatValue(null!), 10);
	}

	[Fact]
	public void Float_capable_function_keeps_integer_return_for_integer_arguments()
	{
		using var scope = GlobalScopeWithProfile("snake");
		var term = new FunctionMethodTerm(Method("POWER"), Args(new SingleLongTerm(2), new SingleLongTerm(3)));
		Assert.Equal(typeof(long), term.GetOperandType());
		Assert.Equal(8L, term.GetIntValue(null!));
	}

	[Theory]
	[InlineData("ABS", -2.5, 2.5)]
	[InlineData("SQRT", 2.25, 1.5)]
	[InlineData("FLOOR", 2.9, 2.0)]
	[InlineData("CEIL", 2.1, 3.0)]
	[InlineData("ROUND", 2.5, 3.0)]
	public void Float_math_functions_return_double(string name, double input, double expected)
	{
		using var scope = GlobalScopeWithProfile("snake");
		var value = Method(name).GetFloatValue(null!, Args(new SingleFloatTerm(input)));
		Assert.Equal(expected, value, 10);
	}

	[Fact]
	public void ToInt_truncates_float_toward_zero()
	{
		using var scope = GlobalScopeWithProfile("snake");
		var toInt = Method("TOINT");
		Assert.Equal(1L, toInt.GetIntValue(null!, Args(new SingleFloatTerm(1.9))));
		Assert.Equal(-1L, toInt.GetIntValue(null!, Args(new SingleFloatTerm(-1.9))));
	}

	[Fact]
	public void ToInt_argument_rule_follows_capability()
	{
		using (GlobalStatic.OpenScope(new ConfigData(), "v24pure"))
		{
			var error = Method("TOINT").CheckArgumentType("TOINT", Args(new SingleFloatTerm(1.0)));
			Assert.NotNull(error);
		}
		using (GlobalStatic.OpenScope(new ConfigData(), "snake"))
		{
			Assert.Null(Method("TOINT").CheckArgumentType("TOINT", Args(new SingleFloatTerm(1.0))));
		}
	}

	[Fact]
	public void Float_argument_to_integer_only_function_reports_conversion_error()
	{
		using var scope = GlobalScopeWithProfile("snake");
		var error = Method("MAX").CheckArgumentType("MAX", Args(new SingleFloatTerm(1.5), new SingleLongTerm(2)));
		Assert.NotNull(error);
		Assert.Contains("MAX", error);
	}

	private static IDisposable GlobalScopeWithProfile(string profileId)
		=> GlobalStatic.OpenScope(new ConfigData(), profileId);
}
