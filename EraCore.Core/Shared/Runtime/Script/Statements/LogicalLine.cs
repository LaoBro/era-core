using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Statements;

/// <summary>
/// 命令文1行に相当する抽象クラス
/// </summary>
internal abstract class LogicalLine
{
	protected ScriptPosition? scriptPosition;

	//LogicalLine prevLine;
	LogicalLine nextLine = null!;
	public ScriptPosition? Position
	{
		get { return scriptPosition; }
	}

	public FunctionLabelLine ParentLabelLine { get; set; } = null!;
	public LogicalLine NextLine
	{
		get { return nextLine; }
		set { nextLine = value; }
	}
	public override string ToString()
	{
		if (scriptPosition == null)
			return base.ToString()!;
		return string.Format("{0}:{1}:{2}", scriptPosition.Value.Filename, scriptPosition.Value.LineNo, Process.getRawTextFormFilewithLine(scriptPosition)!);
	}

	protected bool isError;
	protected string errMes = "";

	public virtual string ErrMes
	{
		get { return errMes; }
		set { errMes = value; }
	}
	public virtual bool IsError
	{
		get { return isError; }
		set { isError = value; }
	}
}

///// <summary>
///// コメント行。
///// </summary>
//internal sealed class CommentLine : LogicalLine
//{
//    public CommentLine(ScriptPosition? thePosition, string str)
//    {
//        base.position = thePosition;
//        //comment = str;
//    }
//    //string comment;
//    public override bool IsError
//    {
//        get { return false; }
//    }
//}

/// <summary>
/// 無効な行。
/// </summary>
internal sealed class InvalidLine : LogicalLine
{
	public InvalidLine(ScriptPosition? thePosition, string err)
	{
		scriptPosition = thePosition;
		errMes = err;
	}
	public override bool IsError
	{
		get { return true; }
	}
}

/// <summary>
/// 命令文
/// </summary>
internal sealed class InstructionLine : LogicalLine
{
	public InstructionLine(ScriptPosition? thePosition, FunctionIdentifier theFunc, CharStream theArgPrimitive)
	{
		scriptPosition = thePosition;
		func = theFunc;
		argprimitive = theArgPrimitive;
	}

	public InstructionLine(ScriptPosition? thePosition, FunctionIdentifier functionIdentifier, OperatorCode assignOP, WordCollection dest, CharStream theArgPrimitive)
	{
		scriptPosition = thePosition;
		func = functionIdentifier;
		AssignOperator = assignOP;
		assigndest = dest;
		argprimitive = theArgPrimitive;
	}
	readonly FunctionIdentifier func;
	CharStream argprimitive;

	WordCollection assigndest = null!;
	public OperatorCode AssignOperator { get; private set; }
	long subData;
	public FunctionCode FunctionCode
	{
		get { return func.Code; }
	}
	public FunctionIdentifier Function
	{
		get { return func; }
	}
	public Argument Argument { get; set; } = null!;
	public CharStream PopArgumentPrimitive()
	{
		CharStream ret = argprimitive;
		argprimitive = null!;
		return ret;
	}
	public WordCollection PopAssignmentDestStr()
	{
		WordCollection ret = assigndest;
		assigndest = null!;
		return ret;
	}

	/// <summary>
	/// 繰り返しの終了を記憶する
	/// </summary>
	public long LoopEnd
	{
		get { return subData; }
		set { subData = value; }
	}

	VariableTerm cnt = null!;
	/// <summary>
	/// 繰り返しにつかう変数を記憶する
	/// </summary>
	public VariableTerm LoopCounter
	{
		get { return cnt; }
		set { cnt = value; }
	}

	long step;
	/// <summary>
	/// 繰り返しのたびに増加する値を記憶する
	/// </summary>
	public long LoopStep
	{
		get { return step; }
		set { step = value; }
	}

	private LogicalLine jumpto = null!;
	private LogicalLine jumptoendcatch = null!;
	//IF文とSELECT文のみが使う。
	public LinkedList<InstructionLine> IfCaseList = null!;
	//SELECTCASEのみが使う。コンパイル時ジャンプテーブル（条件を満たさない場合は null＝線形スキャン）。
	public SelectCaseJumpTable? SelectCaseJumpTable { get; set; }
	//PRINTDATA文のみが使う。
	public List<List<InstructionLine>> dataList = null!;
	//TRYCALLLIST系が使う
	public List<InstructionLine> callList = null!;

	public LogicalLine JumpTo
	{
		get { return jumpto; }
		set { jumpto = value; }
	}

	public LogicalLine JumpToEndCatch
	{
		get { return jumptoendcatch; }
		set { jumptoendcatch = value; }
	}

}

/// <summary>
/// ファイルの始端と終端
/// </summary>
internal sealed class NullLine : LogicalLine { }

/// <summary>
/// ラベルがエラーになっている関数行専用のクラス
/// </summary>
internal sealed class InvalidLabelLine : FunctionLabelLine
{
	public InvalidLabelLine(ScriptPosition? thePosition, string labelname, string err)
	{
		scriptPosition = thePosition;
		LabelName = labelname;
		errMes = err;
		IsSingle = false;
		Index = -1;
		Depth = -1;
		IsMethod = false;
		MethodType = typeof(void);
	}
	public override bool IsError
	{
		get { return true; }
	}
}

/// <summary>
/// @で始まるラベル行
/// </summary>
internal class FunctionLabelLine : LogicalLine, IComparable<FunctionLabelLine>
{
	protected FunctionLabelLine() { }
	public FunctionLabelLine(ScriptPosition? thePosition, string labelname, WordCollection wc)
	{
		scriptPosition = thePosition;
		LabelName = labelname;
		IsSingle = false;
		hasPrivDynamicVar = false;
		Index = -1;
		Depth = -1;
		LocalLength = 0;
		LocalsLength = 0;
		ArgLength = 0;
		ArgsLength = 0;
		IsMethod = false;
		MethodType = typeof(void);
		this.wc = wc;

		//ArgOptional = true;
		//ArgAutoConvert = true;
	}
	WordCollection wc = null!;
	public WordCollection PopRowArgs()
	{
		WordCollection ret = wc;
		wc = null!;
		return ret;
	}
	/// <summary>b2-irdiskcache：IR 命中桩由索引构建时无 wc（物化时从 blob 的 @ 行重建并走 parseLabel），此 setter 供重建用。</summary>
	internal void SetRowArgs(WordCollection value) => wc = value;

	public string LabelName { get; protected set; } = null!;
	public bool IsEvent { get; set; }
	public bool IsSystem { get; set; }
	public bool IsSingle { get; set; }
	public bool IsPri { get; set; }
	public bool IsLater { get; set; }
	public bool IsOnly { get; set; }
	public bool hasPrivDynamicVar { get; set; }
	public int LocalLength { get; set; }
	public int LocalsLength { get; set; }
	public int ArgLength { get; set; }
	public int ArgsLength { get; set; }

	//public bool ArgOptional { get; set; }
	//public bool ArgAutoConvert { get; set; }

	public bool IsMethod { get; set; }
	public Type MethodType { get; set; } = null!;
	public VariableTerm[] Arg { get; set; } = null!;
	public SingleTerm[] Def { get; set; } = null!;
	//public SingleTerm[] SubNames { get; set; }
	public int Depth { get; set; }
	/// <summary>lazy-function-load：true = 惰性桩（只有标签头、函数体行链未物化）；首次被调用时经物化接缝在输入端替换 NextLine 并复位。eager 一律 false。</summary>
	public bool IsStub { get; set; }
	/// <summary>b2-irdiskcache：true = 本桩由 IR 索引构建（无 wc/签名，来自最小值桩）；物化时以 blob 重建签名。源后背桩恒 false。</summary>
	public bool IrBacked { get; set; }

	#region IComparable<FunctionLabelLine> メンバ
	//ソート用情報
	public int Index { get; set; }
	public int FileIndex { get; set; }
	public int CompareTo(FunctionLabelLine? other)
	{
		// IComparable<T> 契约：null 视为最小（排在非 null 之前）
		if (other is null) return 1;
		if (FileIndex != other.FileIndex)
			return FileIndex.CompareTo(other.FileIndex);
		//position == nullであるLine(デバッグコマンドなど)をSortすることはないはず
		if (Position!.Value.LineNo != other.Position!.Value.LineNo)
			return Position!.Value.LineNo.CompareTo(other.Position!.Value.LineNo);
		return Index.CompareTo(other.Index);
	}
	#endregion
	#region private変数
	readonly Dictionary<string, UserDefinedVariableToken> privateVar = new(Config.Config.StrComper);
	internal bool AddPrivateVariable(UserDefinedVariableData data)
	{
		if (privateVar.ContainsKey(data.Name))
			return false;
		UserDefinedVariableToken var = GlobalStatic.VariableData.CreatePrivateVariable(data);
		privateVar.Add(data.Name, var);
		//静的な変数のみの場合は関数呼び出し時に何もする必要がない
		if (!data.Static)
			hasPrivDynamicVar = true;
		return true;
	}
	internal UserDefinedVariableToken GetPrivateVariable(string key)
	{
		privateVar.TryGetValue(key, out UserDefinedVariableToken? var);
		return var!;
	}

	/// <summary>
	/// 引数の値の確定後、引数の代入より前に呼ぶこと
	/// </summary>
	internal void ScopeIn()
	{
#if DEBUG
		GlobalStatic.StackList.Add(this);
#endif
		foreach (UserDefinedVariableToken var in privateVar.Values)
			if (!var.IsStatic)
				var.ScopeIn();
	}
	internal void ScopeOut()
	{
#if DEBUG
		GlobalStatic.StackList.Remove(this);
#endif
		foreach (UserDefinedVariableToken var in privateVar.Values)
			if (!var.IsStatic)
				var.ScopeOut();
	}
	#endregion

}

/// <summary>
/// $で始まるラベル行
/// </summary>
internal sealed class GotoLabelLine : LogicalLine, IEqualityComparer<GotoLabelLine>
{
	public GotoLabelLine(ScriptPosition? thePosition, string labelname)
	{
		scriptPosition = thePosition;
		this.labelname = labelname;
	}
	readonly string labelname = "";
	public string LabelName
	{
		get { return labelname; }
	}

	#region IEqualityComparer<GotoLabelLine> メンバ

	public bool Equals(GotoLabelLine? x, GotoLabelLine? y)
	{
		if (x == null || y == null)
			return false;
		return x.ParentLabelLine == y.ParentLabelLine && x.labelname == y.labelname;
	}

	public int GetHashCode(GotoLabelLine obj)
	{
		return labelname.GetHashCode() ^ ParentLabelLine.GetHashCode();
	}

	#endregion
}
