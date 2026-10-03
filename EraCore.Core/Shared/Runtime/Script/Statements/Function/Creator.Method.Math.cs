using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using System.Linq;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.GameData.Function;

internal static partial class FunctionMethodCreator
{
	private sealed class MoneyStrMethod : FunctionMethod
	{
		public MoneyStrMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.String}, OmitStart = 1 }
				];
			CanRestructure = true;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long money = arguments[0].GetIntValue(exm);
			if ((arguments.Count < 2) || (arguments[1] == null))
				return Config.MoneyFirst ? Config.MoneyLabel + money.ToString() : money.ToString() + Config.MoneyLabel;
			string format = arguments[1].GetStrValue(exm);
			string ret;
			try
			{
				ret = money.ToString(format);
			}
			catch (FormatException)
			{
				throw new CodeEE(string.Format(trerror.InvalidFormat.Text, Name, 2));
			}
			return Config.MoneyFirst ? Config.MoneyLabel + ret : ret + Config.MoneyLabel;
		}
	}

	private sealed class GettimeMethod : FunctionMethod
	{
		public GettimeMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long date = DateTime.Now.Year;
			date = date * 100 + DateTime.Now.Month;
			date = date * 100 + DateTime.Now.Day;
			date = date * 100 + DateTime.Now.Hour;
			date = date * 100 + DateTime.Now.Minute;
			date = date * 100 + DateTime.Now.Second;
			date = date * 1000 + DateTime.Now.Millisecond;
			return date;//17桁。2京くらい。
		}
	}

	private sealed class GettimesMethod : FunctionMethod
	{
		public GettimesMethod()
		{
			ReturnType = typeof(string);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override string GetStrValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			return DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss");
		}
	}

	private sealed class GetmsMethod : FunctionMethod
	{
		public GetmsMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			//西暦0001年1月1日からの経過時間をミリ秒で。
			return DateTime.Now.Ticks / 10000;
		}
	}

	private sealed class GetSecondMethod : FunctionMethod
	{
		public GetSecondMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList()];
			CanRestructure = false;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			//西暦0001年1月1日からの経過時間を秒で。
			//Ticksは100ナノ秒単位であるが実際にはそんな精度はないので無駄。
			return DateTime.Now.Ticks / 10000000;
		}
	}

	private sealed class RandMethod : FunctionMethod
	{
		public RandMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.Int}, OmitStart = 1 }
				];
			CanRestructure = false;
		}

		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long min = 0;
			long max;
			if (arguments.Count == 1)
				max = arguments[0].GetIntValue(exm);
			else
			{
				if (arguments[0] != null)
					min = arguments[0].GetIntValue(exm);
				max = arguments[1].GetIntValue(exm);
			}
			if (max <= min)
			{
				if (min == 0)
					throw new CodeEE(string.Format(trerror.NegativeMaximum.Text, Name, max));
				else
					throw new CodeEE(string.Format(trerror.MaximumLowerThanMinimum.Text, Name, max));
			}
			return exm.VEvaluator.GetNextRand(max - min) + min;
		}
	}

	private sealed class MaxMethod : FunctionMethod
	{
		readonly bool isMax;
		public MaxMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.VariadicInt}, OmitStart = 1 }
				];
			isMax = true;
			CanRestructure = true;
		}
		public MaxMethod(bool max)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [
					new ArgTypeList{ ArgTypes = { ArgType.Int, ArgType.VariadicInt}, OmitStart = 1 }
				];
			isMax = max;
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = arguments[0].GetIntValue(exm);

			for (int i = 1; i < arguments.Count; i++)
			{
				long newRet = arguments[i].GetIntValue(exm);
				if (isMax)
				{
					if (ret < newRet)
						ret = newRet;
				}
				else
				{
					if (ret > newRet)
						ret = newRet;
				}
			}
			return ret;
		}
	}

	private sealed class AbsMethod : FunctionMethod
	{
		public AbsMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = arguments[0].GetIntValue(exm);
			//普通は使わない値なので例外として投げてしまう方向性で
			if (ret == long.MinValue)
				throw new CodeEE(string.Format(trerror.MinInt64CanNotApplyABS.Text, Name, long.MinValue));
			return Math.Abs(ret);
		}
	}

	private sealed class PowerMethod : FunctionMethod
	{
		public PowerMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long x = arguments[0].GetIntValue(exm);
			long y = arguments[1].GetIntValue(exm);
			double pow = Math.Pow(x, y);
			if (double.IsNaN(pow))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			else if (double.IsInfinity(pow))
				throw new CodeEE(string.Format(trerror.ResultIsInfinity.Text, Name));
			else if ((pow >= long.MaxValue) || (pow <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, pow));
			return (long)pow;
		}
	}

	private sealed class SqrtMethod : FunctionMethod
	{
		public SqrtMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = arguments[0].GetIntValue(exm);
			if (ret < 0)
				throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, Name, 1, ret));
			return (long)Math.Sqrt(ret);
		}
	}

	private sealed class CbrtMethod : FunctionMethod
	{
		public CbrtMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = arguments[0].GetIntValue(exm);
			if (ret < 0)
				throw new CodeEE(string.Format(trerror.ArgIsNegative.Text, Name, 1, ret));
			return (long)Math.Pow(ret, 1.0 / 3.0);
		}
	}

	private sealed class LogMethod : FunctionMethod
	{
		readonly double Base;
		public LogMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			Base = Math.E;
			CanRestructure = true;
		}
		public LogMethod(double b)
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			Base = b;
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = arguments[0].GetIntValue(exm);
			if (ret <= 0)
				throw new CodeEE(string.Format(trerror.ArgIsNotMoreThan0.Text, Name, 1, ret));
			//　今の段階は発生しない
			double dret = ret;
			if (Base == Math.E)
				dret = Math.Log(dret);
			else
				dret = Math.Log10(dret);
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			else if (double.IsInfinity(dret))
				throw new CodeEE(string.Format(trerror.ResultIsInfinity.Text, Name));
			else if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));
			return (long)dret;
		}
	}

	private sealed class ExpMethod : FunctionMethod
	{
		public ExpMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = arguments[0].GetIntValue(exm);
			double dret = Math.Exp(ret);
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			else if (double.IsInfinity(dret))
				throw new CodeEE(string.Format(trerror.ResultIsInfinity.Text, Name));
			else if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));

			return (long)dret;
		}
	}

	private sealed class SignMethod : FunctionMethod
	{

		public SignMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long ret = arguments[0].GetIntValue(exm);
			return Math.Sign(ret);
		}
	}

	private sealed class GetLimitMethod : FunctionMethod
	{
		public GetLimitMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList{ ArgTypes = { ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid, ArgType.Int | ArgType.DisallowVoid } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			long value = arguments[0].GetIntValue(exm);
			long min = arguments[1].GetIntValue(exm);
			long max = arguments[2].GetIntValue(exm);
			long ret;
			if (value < min)
				ret = min;
			else if (value > max)
				ret = max;
			else
				ret = value;
			return ret;
		}
	}

	// ======== Skia 方言 P0：三角函数 / 取整 / 无检查算术（实现票 18，spec §5） ========
	// 语义逐项对齐 Skia 参照（Creator.Method.cs）；era-core 无 Float 类型，
	// 故只保留 Integer 分支（Skia 的 ToDouble 对整型实参即 GetIntValue）。

	private sealed class SinMethod : FunctionMethod
	{
		public SinMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double dret = Math.Sin(arguments[0].GetIntValue(exm));
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			if (double.IsInfinity(dret))
				throw new CodeEE(string.Format(trerror.ResultIsInfinity.Text, Name));
			if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));
			return (long)dret;
		}
	}

	private sealed class CosMethod : FunctionMethod
	{
		public CosMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double dret = Math.Cos(arguments[0].GetIntValue(exm));
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			if (double.IsInfinity(dret))
				throw new CodeEE(string.Format(trerror.ResultIsInfinity.Text, Name));
			if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));
			return (long)dret;
		}
	}

	private sealed class TanMethod : FunctionMethod
	{
		public TanMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double dret = Math.Tan(arguments[0].GetIntValue(exm));
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			if (double.IsInfinity(dret))
				throw new CodeEE(string.Format(trerror.ResultIsInfinity.Text, Name));
			if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));
			return (long)dret;
		}
	}

	private sealed class AsinMethod : FunctionMethod
	{
		public AsinMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double raw = arguments[0].GetIntValue(exm);
			if (raw < -1.0 || raw > 1.0)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 1, raw, -1, 1));
			double dret = Math.Asin(raw);
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			return (long)dret;
		}
	}

	private sealed class AcosMethod : FunctionMethod
	{
		public AcosMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double raw = arguments[0].GetIntValue(exm);
			if (raw < -1.0 || raw > 1.0)
				throw new CodeEE(string.Format(trerror.ArgIsOutOfRange.Text, Name, 1, raw, -1, 1));
			double dret = Math.Acos(raw);
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			return (long)dret;
		}
	}

	private sealed class AtanMethod : FunctionMethod
	{
		public AtanMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double dret = Math.Atan(arguments[0].GetIntValue(exm));
			if (double.IsNaN(dret))
				throw new CodeEE(string.Format(trerror.ResultIsNaN.Text, Name));
			return (long)dret;
		}
	}

	private sealed class FloorMethod : FunctionMethod
	{
		public FloorMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double dret = Math.Floor((double)arguments[0].GetIntValue(exm));
			if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));
			return (long)dret;
		}
	}

	private sealed class CeilMethod : FunctionMethod
	{
		public CeilMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double dret = Math.Ceiling((double)arguments[0].GetIntValue(exm));
			if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));
			return (long)dret;
		}
	}

	private sealed class RoundMethod : FunctionMethod
	{
		public RoundMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Any } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
		{
			double dret = Math.Round((double)arguments[0].GetIntValue(exm), MidpointRounding.AwayFromZero);
			if ((dret >= long.MaxValue) || (dret <= long.MinValue))
				throw new CodeEE(string.Format(trerror.ResultIsOutOfTheRangeOfInt64.Text, Name, dret));
			return (long)dret;
		}
	}

	private sealed class UncheckedAddMethod : FunctionMethod
	{
		public UncheckedAddMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int, ArgType.Int } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> unchecked(arguments[0].GetIntValue(exm) + arguments[1].GetIntValue(exm));
	}

	private sealed class UncheckedSubtractMethod : FunctionMethod
	{
		public UncheckedSubtractMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int, ArgType.Int } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> unchecked(arguments[0].GetIntValue(exm) - arguments[1].GetIntValue(exm));
	}

	private sealed class UncheckedMultiplyMethod : FunctionMethod
	{
		public UncheckedMultiplyMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int, ArgType.Int } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> unchecked(arguments[0].GetIntValue(exm) * arguments[1].GetIntValue(exm));
	}

	private sealed class UncheckedNegateMethod : FunctionMethod
	{
		public UncheckedNegateMethod()
		{
			ReturnType = typeof(long);
			argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Int } }];
			CanRestructure = true;
		}
		public override long GetIntValue(ExpressionMediator exm, List<AExpression> arguments)
			=> unchecked(-arguments[0].GetIntValue(exm));
	}
}
