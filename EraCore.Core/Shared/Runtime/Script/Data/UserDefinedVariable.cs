using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Data;

internal sealed class UserDefinedVariableData
{
	public string Name = null!;
	public bool TypeIsStr;
	public bool TypeIsFloat;
	public bool Reference;
	public int Dimension = 1;
	public int[] Lengths = null!;
	public long[] DefaultInt = null!;
	public string[] DefaultStr = null!;
	public double[] DefaultFloat = null!;
	public bool Global;
	public bool Save;
	public bool Static = true;
	public bool Private;
	public bool CharaData;
	public bool Const;
	// 票 47（Skia 18.0.0）：CONSTTABLE 常数表。
	public bool ConstTable;
	public string? ConstTablePath;
	public Dictionary<string, int>[]? ConstTableNames;
	public List<string>? ConstTableWarnings;
	public List<string>? ConstTableConsumed;

	/// <summary>
	/// 27c（Skia 对齐）：`#REFF`——浮点标量引用（Dimension = 0，不能带尺寸/关键字）。
	/// </summary>
	public static UserDefinedVariableData CreateRefScalar(WordCollection wc, bool isStr, bool isFloat, bool isPrivate, ScriptPosition? sc)
	{
		UserDefinedVariableData ret = new()
		{
			TypeIsStr = isStr,
			TypeIsFloat = isFloat,
			Reference = true,
			Static = false,
			Private = isPrivate,
			Dimension = 0,
			Lengths = [1]
		};
		string keyword = isStr ? "#REFS" : isFloat ? "#REFF" : "#REF";
		if (wc.EOL)
			throw new CodeEE(string.Format(trerror.NotVarAfterKeyword.Text, keyword), sc);
		if (wc.Current is not IdentifierWord idw)
			throw new CodeEE(string.Format(trerror.NotVarAfterKeyword.Text, keyword), sc);
		wc.ShiftNext();
		ret.Name = idw.Code;
		if (!wc.EOL)
			throw new CodeEE(trerror.CanNotSizedRef.Text, sc);
		string errMes = "";
		int errLevel = -1;
		if (isPrivate)
			GlobalStatic.IdentifierDictionary.CheckUserPrivateVarName(ref errMes, ref errLevel, ret.Name);
		else
			GlobalStatic.IdentifierDictionary.CheckUserVarName(ref errMes, ref errLevel, ret.Name);
		if (errLevel >= 0)
		{
			if (errLevel >= 2)
				throw new CodeEE(errMes, sc);
			ParserMediator.Warn(errMes, sc, errLevel);
		}
		return ret;
	}

	//1822 Privateの方もDIMだけ遅延させようとしたけどちょっと課題がおおいのでやめとく
	public static UserDefinedVariableData Create(DimLineWC dimline)
	{
		return Create(dimline.WC, dimline.Dims, dimline.Dimf, dimline.IsPrivate, dimline.SC);
	}

	public static UserDefinedVariableData Create(WordCollection wc, bool dims, bool dimf, bool isPrivate, ScriptPosition? sc)
	{
		string dimtype = dimf ? "#DIMF" : (dims ? "#DIMS" : "#DIM");
		UserDefinedVariableData ret = new()
		{
			TypeIsStr = dims,
			TypeIsFloat = dimf
		};

		IdentifierWord? idw;
		bool staticDefined = false;
		ret.Const = false;
		string keyword = dimtype;
		//List<string> keywords;
		while (!wc.EOL && (idw = wc.Current as IdentifierWord) != null)
		{
			wc.ShiftNext();
			keyword = idw.Code;
			var cmp = Config.Config.StringComparison;
			//TODO ifの数があたまわるい なんとかしたい
			switch (keyword)
			{
				case var s when s.Equals("CONSTTABLE", cmp) || s.Equals("常数表", cmp) || s.Equals("常数数组", cmp):
					// 名称下标在 ErhLoader 注册；函数内声明没有对应注册路径。
					if (isPrivate)
						throw new CodeEE(keyword + " 只能在 ERH 中声明，不能在函数内使用", sc);
					if (ret.CharaData)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CHARADATA"), sc);
					if (ret.Global)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "GLOBAL"), sc);
					if (ret.Save)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "SAVEDATA"), sc);
					if (ret.Reference)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "REF"), sc);
					if (staticDefined && !ret.Static)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "DYNAMIC"), sc);
					if (ret.Const)
						throw new CodeEE(string.Format(trerror.DuplicateKeyword.Text, keyword), sc);
					ret.ConstTable = true;
					ret.Const = true;
					ret.Static = true;
					break;
				case var s when s.Equals("CONST", cmp):
					if (ret.ConstTable)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CONSTTABLE"), sc);
					if (ret.CharaData)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CHARADATA"), sc);
					if (ret.Global)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "GLOBAL"), sc);
					if (ret.Save)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "SAVEDATA"), sc);
					if (ret.Reference)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "REF"), sc);
					if (!ret.Static)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "DYNAMIC"), sc);
					if (ret.Const)
						throw new CodeEE(string.Format(trerror.DuplicateKeyword.Text, keyword), sc);
					ret.Const = true;
					break;
				case var s when s.Equals("REF", cmp):
					//throw new CodeEE("未実装の機能です", sc);
					//if (!isPrivate)
					//	throw new CodeEE("広域変数の宣言に" + keyword + "キーワードは指定できません", sc);
					if (staticDefined && ret.Static)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "STATIC"), sc);
					if (ret.CharaData)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CHARADATA"), sc);
					if (ret.Global)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "GLOBAL"), sc);
					if (ret.Save)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "SAVEDATA"), sc);
					if (ret.Const)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CONST"), sc);
					if (ret.Reference)
						throw new CodeEE(string.Format(trerror.DuplicateKeyword.Text, keyword), sc);
					ret.Reference = true;
					ret.Static = false;
					break;
				case var s when s.Equals("DYNAMIC", cmp):
					if (!isPrivate)
						throw new CodeEE(string.Format(trerror.CanNotUseKeywordGlobalVar.Text, keyword), sc);
					if (ret.CharaData)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CHARADATA"), sc);
					if (ret.Const)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CONST"), sc);
					if (staticDefined)
						if (ret.Static)
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "STATIC", "DYNAMIC"), sc);
						else
							throw new CodeEE(string.Format(trerror.DuplicateKeyword.Text, keyword), sc);
					staticDefined = true;
					ret.Static = false;
					break;
				case var s when s.Equals("STATIC", cmp):
					if (!isPrivate)
						throw new CodeEE(string.Format(trerror.CanNotUseKeywordGlobalVar.Text, keyword), sc);
					if (ret.CharaData)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CHARADATA"), sc);
					if (staticDefined)
						if (!ret.Static)
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "STATIC", "DYNAMIC"), sc);
						else
							throw new CodeEE(string.Format(trerror.DuplicateKeyword.Text, keyword), sc);
					if (ret.Reference)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "REF"), sc);
					staticDefined = true;
					ret.Static = true;
					break;
				case var s when s.Equals("GLOBAL", cmp):
					if (isPrivate)
						throw new CodeEE(string.Format(trerror.CanNotUseKeywordLocalVar.Text, keyword), sc);
					if (ret.CharaData)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CHARADATA"), sc);
					if (ret.Reference)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "REF"), sc);
					if (ret.Const)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CONST"), sc);
					if (staticDefined)
						if (ret.Static)
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "STATIC", "GLOBAL"), sc);
						else
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "DYNAMIC", "GLOBAL"), sc);
					ret.Global = true;
					break;
				case var s when s.Equals("SAVEDATA", cmp):
					if (isPrivate)
						throw new CodeEE(string.Format(trerror.CanNotUseKeywordLocalVar.Text, keyword), sc);
					if (staticDefined)
						if (ret.Static)
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "STATIC", "SAVEDATA"), sc);
						else
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "DYNAMIC", "SAVEDATA"), sc);
					if (ret.Reference)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "REF"), sc);
					if (ret.Const)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CONST"), sc);
					if (ret.Save)
						throw new CodeEE(string.Format(trerror.DuplicateKeyword.Text, keyword), sc);
					ret.Save = true;
					break;
				case var s when s.Equals("CHARADATA", cmp):
					if (isPrivate)
						throw new CodeEE(string.Format(trerror.CanNotUseKeywordLocalVar.Text, keyword), sc);
					if (ret.Reference)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "REF"), sc);
					if (ret.Const)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "CONST"), sc);
					if (staticDefined)
						if (ret.Static)
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "STATIC"), sc);
						else
							throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "DYNAMIC"), sc);
					if (ret.Global)
						throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, keyword, "GLOBAL"), sc);
					if (ret.CharaData)
						throw new CodeEE(string.Format(trerror.DuplicateKeyword.Text, keyword), sc);
					ret.CharaData = true;
					break;
				default:
					ret.Name = keyword;
					goto whilebreak;
			}
		}
	whilebreak:
		if (ret.Name == null)
			throw new CodeEE(string.Format(trerror.NotVarAfterKeyword.Text, keyword), sc);
		// 票 27（Skia 对齐）：浮点用户变量只实现 1D；CHARADATA / REF 明确报错。
		// SAVEDATA 自 27b 起支持 1D 浮点持久化（文本 + 二进制）。
		if (dimf && ret.CharaData)
			throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "#DIMF", "CHARADATA"), sc);
		if (dimf && ret.Reference)
			throw new CodeEE(string.Format(trerror.CanNotSpecifiedWith.Text, "#DIMF", "REF"), sc);
		if (Config.Config.UseERD && Config.Config.CheckDuplicateIdentifier)
			GlobalStatic.ConstantData.isDefinedErd(ret.Name, sc);
		string errMes = "";
		int errLevel = -1;
		if (isPrivate)
			GlobalStatic.IdentifierDictionary.CheckUserPrivateVarName(ref errMes, ref errLevel, ret.Name);
		else
			GlobalStatic.IdentifierDictionary.CheckUserVarName(ref errMes, ref errLevel, ret.Name);
		if (errLevel >= 0)
		{
			if (errLevel >= 2)
				throw new CodeEE(errMes, sc);
			ParserMediator.Warn(errMes, sc, errLevel);
		}


		List<int> sizeNum = [];
		if (wc.EOL)//サイズ省略
		{
			// 常数表必须写明尺寸（由下方统一报错），不走 CONST 的“缺初值”与默认 1。
			if (ret.Const && !ret.ConstTable)
				throw new CodeEE(trerror.ConstHasNotInitialValue.Text);
			if (!ret.ConstTable)
				sizeNum.Add(1);
		}
		else if (wc.Current.Type == ',')//サイズ指定
		{
			while (!wc.EOL)
			{
				if (wc.Current.Type == '=')//サイズ指定解読完了＆初期値指定
					break;
				if (wc.Current.Type != ',')
					throw new CodeEE(trerror.WrongFormat.Text, sc);
				wc.ShiftNext();
				if (ret.Reference)//参照型の場合は要素数不要
				{
					sizeNum.Add(0);
					if (wc.EOL)
						break;
					if (wc.Current.Type == ',')
						continue;
				}
				if (wc.EOL)
					throw new CodeEE(trerror.HasNotExpressionAfterComma.Text, sc);
				AExpression arg = ExpressionParser.ReduceIntegerTerm(wc, TermEndWith.Comma_Assignment);
				if (arg.Restructure(null!) is not SingleLongTerm sizeTerm)
					throw new CodeEE(trerror.HasNotExpressionAfterComma.Text, sc);
				if (ret.Reference)//参照型には要素数指定不可(0にするか書かないかどっちか
				{
					if (sizeTerm.Int != 0)
						throw new CodeEE(trerror.CanNotSizedRef.Text, sc);

					continue;
				}
				else if (sizeTerm.Int <= 0 || sizeTerm.Int > 1000000)
					throw new CodeEE(trerror.OoRDefinable.Text, sc);
				sizeNum.Add((int)sizeTerm.Int);
			}
		}


		if (wc.Current.Type != '=')//初期値指定なし
		{
			// 常数表的值来自同名表，不走 CONST 的内联初值要求。
			if (ret.Const && !ret.ConstTable)
				throw new CodeEE(trerror.ConstHasNotInitialValue.Text);
		}
		else//初期値指定あり
		{
			// 常数表的值只来自同名表；允许内联初始值会造成「写了却不生效」的静默覆盖。
			if (ret.ConstTable)
				throw new CodeEE("常数表的值来自同名表，不能写内联初始值：" + ret.Name, sc);
			if (((OperatorWord)wc.Current).Code != OperatorCode.Assignment)
				throw new CodeEE(trerror.UnexpectedOp.Text);
			if (ret.Reference)
				throw new CodeEE(string.Format(trerror.CanNotSetInitialValue.Text, trerror.RefType.Text));
			if (sizeNum.Count >= 2)
				throw new CodeEE(string.Format(trerror.CanNotSetInitialValue.Text, trerror.MultidimType.Text));
			if (ret.CharaData)
				throw new CodeEE(string.Format(trerror.CanNotSetInitialValue.Text, trerror.CharaType.Text));
			int size = 0;
			if (sizeNum.Count == 1)
				size = sizeNum[0];
			wc.ShiftNext();
			var terms = ExpressionParser.ReduceArguments(wc, ArgsEndWith.EoL, false);
			if (terms.Count == 0)
				throw new CodeEE(trerror.ArrayVarCanNotOmitInitialValue.Text);
			if (size > 0)
			{
				if (terms.Count > size)
					throw new CodeEE(trerror.InitialValueMoreThanArraySize.Text);
				if (ret.Const && terms.Count != size)
					throw new CodeEE(trerror.ConstInitialValueDifferentArraySize.Text);
			}
			if (dimf)
				ret.DefaultFloat = new double[terms.Count];
			else if (dims)
				ret.DefaultStr = new string[terms.Count];
			else
				ret.DefaultInt = new long[terms.Count];

			for (int i = 0; i < terms.Count; i++)
			{
				if (terms[i] == null)
					throw new CodeEE(trerror.ArrayVarCanNotOmitInitialValue.Text);
				terms[i] = terms[i].Restructure(GlobalStatic.EMediator);
				if (terms[i] is not SingleTerm sTerm)
					throw new CodeEE(trerror.InitialValueOnlyConst.Text);
				if (dimf)
				{
					if (sTerm is SingleFloatTerm floatTerm)
						ret.DefaultFloat[i] = floatTerm.Float;
					else if (sTerm is SingleLongTerm longTerm)
						ret.DefaultFloat[i] = longTerm.Int;
					else
						throw new CodeEE(trerror.NotMatchVarTypeAndInitialValue.Text);
				}
				else if (dims != sTerm.IsString)
					throw new CodeEE(trerror.NotMatchVarTypeAndInitialValue.Text);
				else if (dims)
					ret.DefaultStr[i] = ((SingleStrTerm)sTerm).Str;
				else
					ret.DefaultInt[i] = ((SingleLongTerm)sTerm).Int;
			}
			if (sizeNum.Count == 0)
				sizeNum.Add(terms.Count);
		}
		if (!wc.EOL)
			throw new CodeEE(trerror.WrongFormat.Text, sc);

		if (sizeNum.Count == 0)
		{
			if (ret.ConstTable)
				throw new CodeEE("常数表声明必须写明尺寸：" + ret.Name, sc);
			sizeNum.Add(1);
		}

		ret.Private = isPrivate;
		ret.Dimension = sizeNum.Count;
		// 票 48（Skia 18.0.0）：常数表支持一维（三列表）与二维（@1/@2 布局）。
		if (ret.ConstTable && ret.Dimension > 2)
			throw new CodeEE("常数表只支持一维与二维数组", sc);
		// 票 46（Skia 18.0.0）：#DIMF 与 #DIM/#DIMS 一样支持一至三维。
		if (ret.Const && !ret.ConstTable && ret.Dimension > 1)
			throw new CodeEE(trerror.CanNotDeclareConstArray.Text);
		if (ret.CharaData && ret.Dimension > 2)
			throw new CodeEE(trerror.CharaVarCanNotDeclareMoreThan3D.Text, sc);
		if (ret.Dimension > 3)
			throw new CodeEE(trerror.VarCanNotDeclareMoreThan4D.Text, sc);
		ret.Lengths = new int[sizeNum.Count];
		if (ret.Reference)
			return ret;
		long totalBytes = 1;
		for (int i = 0; i < sizeNum.Count; i++)
		{
			ret.Lengths[i] = sizeNum[i];
			totalBytes *= ret.Lengths[i];
		}
		if (totalBytes <= 0 || totalBytes > 1000000)
			throw new CodeEE(trerror.OoRDefinable.Text, sc);
		if (ret.ConstTable)
		{
			int valueKind = dimf ? ConstArrayTable.KindFloat : dims ? ConstArrayTable.KindStr : ConstArrayTable.KindInt;
			ConstArrayTable.Result? table = ConstArrayTable.Load(ret.Name, ret.Lengths, valueKind, out string tableError);
			if (table == null)
				throw new CodeEE("常数表声明 " + ret.Name + " 不可用：" + tableError, sc);
			ret.ConstTablePath = table.Path;
			ret.ConstTableNames = table.Names;
			ret.ConstTableWarnings = table.Warnings;
			ret.ConstTableConsumed = table.ConsumedPaths;
			if (table.Values is long[] intValues)
				ret.DefaultInt = intValues;
			else if (table.Values is string[] strValues)
				ret.DefaultStr = strValues;
			else
				ret.DefaultFloat = (double[])table.Values;
		}
		if (!isPrivate && ret.Save && !Config.Config.SystemSaveInBinary)
		{
			if (dims && ret.Dimension > 1)
				throw new CodeEE(trerror.StrVarrRequiredBinaryOption.Text, sc);
			else if (ret.CharaData)
				throw new CodeEE(trerror.CharaStrRequiredBinaryOption.Text, sc);
		}
		return ret;
	}
}
internal sealed class DimLineWC
{
	public WordCollection WC;
	public bool Dims;
	public bool Dimf;
	public bool IsPrivate;
	public ScriptPosition? SC;
	public DimLineWC(WordCollection wc, bool isString, bool isFloat, bool isPrivate, ScriptPosition? position)
	{
		WC = wc;
		Dims = isString;
		Dimf = isFloat;
		IsPrivate = isPrivate;
		SC = position;
	}
}
