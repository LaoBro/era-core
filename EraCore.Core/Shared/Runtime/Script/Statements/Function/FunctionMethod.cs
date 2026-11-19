using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.Text;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Statements.Function;

internal abstract class FunctionMethod
{
	// 唯一参数规则源为 argumentTypeArrayEx；旧 argumentTypeArray 已废弃。
	public Type ReturnType { get; protected set; } = null!;

	/// <summary>
	/// 票 27：函数可按实参类型动态返回 Integer 或 Float。为 true 时
	/// <see cref="FunctionMethodTerm"/> 按「任一实参为 Float」决定表达式的操作数类型，
	/// <see cref="GetReturnValue"/> 相应返回 <see cref="SingleFloatTerm"/> 或 <see cref="SingleLongTerm"/>。
	/// </summary>
	public bool CanReturnFloat { get; protected set; }
	protected string Name { get; private set; } = null!;
	#region EM_私家版_Emuera多言語化改造
	protected enum ArgType
	{
		Invalid = 0,

		Any = 1,
		Int = 1 << 1,
		String = 1 << 2,
		Ref = 1 << 3,
		Array = 1 << 4,
		Array1D = 1 << 5,
		Array2D = 1 << 6,
		Array3D = 1 << 7,
		Variadic = 1 << 8,
		SameAsFirst = 1 << 9,
		CharacterData = Ref | 1 << 10,
		AllowConstRef = 1 << 11,
		DisallowVoid = 1 << 12,
		Float = 1 << 13,

		IntOrFloat = Int | Float,

		RefInt = Ref | Int,
		RefAny = Ref | Any,
		RefString = Ref | String,
		RefAnyArray = RefAny | Array,
		RefIntArray = RefInt | Array,
		RefStringArray = RefString | Array,
		RefAny1D = RefAny | Array1D,
		RefInt1D = RefInt | Array1D,
		RefString1D = RefString | Array1D,
		RefAny2D = RefAny | Array2D,
		RefInt2D = RefInt | Array2D,
		RefString2D = RefString | Array2D,
		RefAny3D = RefAny | Array3D,
		RefInt3D = RefInt | Array3D,
		RefString3D = RefString | Array3D,

		VariadicAny = Variadic | Any,
		VariadicInt = Variadic | Int,
		VariadicString = Variadic | String,
		VariadicSameAsFirst = Variadic | SameAsFirst,
	}
	protected sealed class _ArgType
	{
		public _ArgType(ArgType t)
		{
			type = t;
		}
		public Type Type
		{
			get
			{
				if (Int) return typeof(long);
				if (Float) return typeof(double);
				if (String) return typeof(string);
				throw new InvalidOperationException("_ArgType.Type is only valid for Int, Float or String rules.");
			}
		}
		public ArgType type = ArgType.Invalid;
		public bool AllowConstRef { get { return (type & ArgType.AllowConstRef) != 0; } }
		public bool DisallowVoid { get { return (type & ArgType.DisallowVoid) != 0; } }
		public bool Ref { get { return (type & ArgType.Ref) != 0; } }
		public bool Any { get { return (type & ArgType.Any) != 0; } }
		public bool Int { get { return (type & ArgType.Int) != 0; } }
		public bool Float { get { return (type & ArgType.Float) != 0; } }
		public bool Array { get { return (type & ArgType.Array) != 0; } }
		public bool Array1D { get { return (type & ArgType.Array1D) != 0; } }
		public bool Array2D { get { return (type & ArgType.Array2D) != 0; } }
		public bool Array3D { get { return (type & ArgType.Array3D) != 0; } }
		public bool String { get { return (type & ArgType.String) != 0; } }
		public bool Variadic { get { return (type & ArgType.Variadic) != 0; } }
		public bool SameAsFirst { get { return (type & ArgType.SameAsFirst) != 0; } }
		public bool CharacterData { get { return ((int)type & 1 << 10) != 0; } }

		/// <summary>票 27：规则是否接受该操作数类型（Int/Float 可同时开放）。</summary>
		public bool Accepts(Type t)
		{
			if (Int && t == typeof(long)) return true;
			if (Float && t == typeof(double)) return true;
			if (String && t == typeof(string)) return true;
			return false;
		}
		public int ArrayDims
		{
			get
			{
				return Array ? -1
					: Array1D ? 1
					: Array2D ? 2
					: Array3D ? 3 : 0;
			}
		}

		public static implicit operator _ArgType(ArgType value)
		{
			return new _ArgType(value);
		}
	}
	protected sealed class ArgTypeList
	{
		public List<_ArgType> ArgTypes { get; set; } = [];
		public int OmitStart { get; set; } = -1;
		public bool MatchVariadicGroup { get; set; }

		public _ArgType[]? LastVariadics
		{
			get
			{
				int count = 0;
				for (int i = ArgTypes.Count - 1; i >= 0; i--)
				{
					if (!ArgTypes[i].Variadic) break;
					count++;
				}
				if (count == 0) return null;
				var ret = new _ArgType[count];
				for (int i = 0; i < count; i++)
				{
					ret[i] = ArgTypes[ArgTypes.Count - count + i];
				}
				return ret;
			}
		}
	}
	// 唯一参数规则源为 argumentTypeArrayEx；旧 argumentTypeArray 已废弃并删除。
	protected ArgTypeList[] argumentTypeArrayEx = null!;

	//引数の数・型が一致するかどうかのテスト
	//正しくない場合はエラーメッセージを返す。
	//引数の数が不定である場合や引数の省略を許す場合にはoverrideすること。
	private string CheckArgumentTypeEx(string name, List<AExpression> arguments)
	{
		string[] errMsg = new string[argumentTypeArrayEx.Length];
		for (int idx = 0; idx < argumentTypeArrayEx.Length; idx++)
		{
			var list = argumentTypeArrayEx[idx];
			var vs = list.LastVariadics;
			bool variadic = vs != null;
			bool argsNotMoreThanRule = variadic ? true : arguments.Count <= list.ArgTypes.Count;
			bool argsNotLessThanRule = list.OmitStart > -1 ? arguments.Count >= list.OmitStart : arguments.Count >= list.ArgTypes.Count;
			if (argsNotMoreThanRule && argsNotLessThanRule)
			{
				if (list.MatchVariadicGroup && vs != null)
				{
					var variadicGroupStart = list.ArgTypes.Count - vs.Length;
					if (arguments.Count > variadicGroupStart && (arguments.Count - variadicGroupStart) % vs.Length != 0)
					{
						errMsg[idx] = string.Format(trerror.ArgsNotFitExpr.Text, name, arguments.Count, variadicGroupStart, vs.Length);
						continue;
					}
				}
				// 引数の数が有効
				for (int i = 0; i < (variadic ? arguments.Count : Math.Min(arguments.Count, list.ArgTypes.Count)); i++)
				{
					var rule = variadic && i >= list.ArgTypes.Count
						? vs![(i - list.ArgTypes.Count) % vs.Length]
						: list.ArgTypes[i];
					if (arguments[i] is null)
					{
						// OmitStart < 0 表示没有可省略参数；OmitStart 之前的参数也不可省略。
						// 可省略区间内的 null 仅在规则显式 DisallowVoid 时拒绝。
						if (list.OmitStart < 0 || i < list.OmitStart || i >= list.OmitStart && rule.DisallowVoid)
						{
							errMsg[idx] = string.Format(trerror.ArgCanNotBeNull.Text, name, i + 1);
							break;
						}
						else continue;
					}
					bool typeNotMatch = rule.SameAsFirst
						? arguments[0].GetOperandType() != arguments[i].GetOperandType()
						: !rule.Any && !rule.Accepts(arguments[i].GetOperandType());
					if (rule.Ref)
					{
						if (rule.CharacterData && (!(arguments[i] is VariableTerm cvarTerm) || !cvarTerm.Identifier.IsCharacterData))
						{
							// キャラ変数ではない
							errMsg[idx] = string.Format(trerror.ArgIsNotCharacterVar.Text, name, i + 1);
							break;
						}
						// 引数の型が違う
						bool error = false;
						string errText;
						var dims = rule.ArrayDims;
						switch (dims)
						{
							case 0:
								{
									// 普通の場合
									var err = rule.String ? trerror.ArgIsNotStrVar
										: rule.Int ? trerror.ArgIsNotIntVar : trerror.ArgIsNotVar;
									errText = string.Format(err.Text, name, i + 1);
									break;
								}
							case -1:
								{
									// 任意配列の場合
									var err = rule.String ? trerror.ArgIsNotStrArray
										: rule.Int ? trerror.ArgIsNotIntArray : trerror.ArgIsNotArray;
									errText = string.Format(err.Text, name, i + 1);
									break;
								}
							default:
								{
									// 1-3次元配列の場合
									var err = rule.String ? trerror.ArgIsNotNDStrArray
										: rule.Int ? trerror.ArgIsNotNDIntArray : trerror.ArgIsNotNDArray;
									errText = string.Format(err.Text, name, i + 1, dims);
									break;
								}
						}
						// 引数が引用系
						if (arguments[i] is VariableTerm varTerm && !(varTerm.Identifier.IsCalc || !rule.AllowConstRef && varTerm.Identifier.IsConst))
						{
							// 変数の場合
							switch (dims)
							{
								case 0: error = typeNotMatch; break;
								case -1: error = !varTerm.Identifier.IsArray1D && !varTerm.Identifier.IsArray2D && !varTerm.Identifier.IsArray3D || typeNotMatch; break;
								case 1: error = !varTerm.Identifier.IsArray1D || typeNotMatch; break;
								case 2: error = !varTerm.Identifier.IsArray2D || typeNotMatch; break;
								case 3: error = !varTerm.Identifier.IsArray3D || typeNotMatch; break;
							}
						}
						else error = true; // 変数ではない
						if (error)
						{
							errMsg[idx] = errText;
							break;
						}
					}
					else if (typeNotMatch)
					{
						var operandType = rule.SameAsFirst ? arguments[0].GetOperandType() : rule.Type;
						// 票 27：Float 实参不能隐式转 Int（对齐 Skia 的显式转换口径）。
						if (operandType == typeof(long) && arguments[i].GetOperandType() == typeof(double))
							errMsg[idx] = string.Format(trerror.CanNotConvertFloatToInt.Text, name, i + 1);
						else
							// 引数の型が違う
							errMsg[idx] = operandType == typeof(string) ? string.Format(trerror.ArgIsNotStr.Text, name, i + 1)
								: operandType == typeof(double) ? string.Format(trerror.ArgIsNotFloat.Text, name, i + 1)
								: string.Format(trerror.ArgIsNotInt.Text, name, i + 1);
						break;
					}
				}
				if (errMsg[idx] == null) return null!;
			}
			else if (list.OmitStart == -1 && list.ArgTypes.Count == 0 && !variadic)
			{
				// 0 参数规则：不接受任何实参（保持旧 argumentTypeArray = [] 的错误消息）
				errMsg[idx] = string.Format(trerror.ArgsNotNeeded.Text, name);
				continue;
			}
			else if (list.OmitStart == -1 && list.ArgTypes.Count > 0 && !variadic)
			{
				// 数固定の引数が必要
				errMsg[idx] = string.Format(trerror.ArgsCountNotMatches.Text, name, list.ArgTypes.Count, arguments.Count);
				continue;
			}
			// 可変長引数
			else if (!argsNotMoreThanRule)
			{
				// 引数が多すぎる
				errMsg[idx] = string.Format(trerror.TooManyFuncArgs.Text, name);
				continue;
			}
			else
			{
				// 引数が足りない
				errMsg[idx] = string.Format(trerror.NotEnoughArgs.Text, name, list.OmitStart < 0 ? list.ArgTypes.Count : list.OmitStart);
				continue;
			}
		}
		if (argumentTypeArrayEx.Length == 1) return errMsg[0];

		StringBuilder sb = new();
		for (int i = 0; i < errMsg.Length; i++)
		{
			sb.Append(string.Format(trerror.NotValidArgsReason.Text, i + 1, errMsg[i]));
			if (i + 1 < errMsg.Length) sb.Append(" | ");
		}
		return string.Format(trerror.NotValidArgs.Text, name, sb.ToString());
	}
	public virtual string CheckArgumentType(string name, List<AExpression> arguments)
	{
		// 唯一参数规则源为 argumentTypeArrayEx；未设置规则时保持旧的“不校验”行为。
		return argumentTypeArrayEx != null ? CheckArgumentTypeEx(name, arguments) : null!;
	}
	#endregion

	//Argumentが全て定数の時にMethodを解体してよいかどうか。RANDやCharaを参照するものなどは不可
	public bool CanRestructure { get; protected set; }

	//FunctionMethodが固有のRestructure()を持つかどうか
	public bool HasUniqueRestructure { get; protected set; }

	//実際の計算。
	public virtual long GetIntValue(ExpressionMediator exm, List<AExpression> arguments) { throw new ExeEE(trerror.ReturnTypeDifferentOrNotImpelemnt.Text); }
	public virtual string GetStrValue(ExpressionMediator exm, List<AExpression> arguments) { throw new ExeEE(trerror.ReturnTypeDifferentOrNotImpelemnt.Text); }
	public virtual double GetFloatValue(ExpressionMediator exm, List<AExpression> arguments) { throw new ExeEE(trerror.ReturnTypeDifferentOrNotImpelemnt.Text); }
	public virtual SingleTerm GetReturnValue(ExpressionMediator exm, List<AExpression> arguments)
	{
		if (CanReturnFloat)
			return HasFloatArg(arguments)
				? new SingleFloatTerm(GetFloatValue(exm, arguments))
				: new SingleLongTerm(GetIntValue(exm, arguments));
		if (ReturnType == typeof(long))
			return new SingleLongTerm(GetIntValue(exm, arguments));
		if (ReturnType == typeof(double))
			return new SingleFloatTerm(GetFloatValue(exm, arguments));
		return new SingleStrTerm(GetStrValue(exm, arguments));
	}

	/// <summary>票 27：任一实参为 Float（含 null 占位跳过）。</summary>
	protected static bool HasFloatArg(List<AExpression> arguments)
	{
		for (int i = 0; i < arguments.Count; i++)
		{
			if (arguments[i] != null && arguments[i].GetOperandType() == typeof(double))
				return true;
		}
		return false;
	}

	/// <summary>票 27：按操作数类型取 double（Int 隐式拓宽）。</summary>
	protected static double ToDouble(AExpression arg, ExpressionMediator exm)
	{
		return arg.GetOperandType() == typeof(double) ? arg.GetFloatValue(exm) : arg.GetIntValue(exm);
	}

	/// <summary>
	/// 戻り値は全体をRestructureできるかどうか
	/// </summary>
	/// <param name="exm"></param>
	/// <param name="arguments"></param>
	/// <returns></returns>
	public virtual bool UniqueRestructure(ExpressionMediator exm, List<AExpression> arguments)
	{ throw new ExeEE(trerror.NotImplement.Text); }


	internal void SetMethodName(string name)
	{
		Name = name;
	}
}
