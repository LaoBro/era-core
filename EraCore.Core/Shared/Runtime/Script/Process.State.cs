using MinorShift.Emuera.GameData.Variable;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game.Image;

namespace MinorShift.Emuera.Runtime.Script;

//1756 インナークラス解除して一般に開放

internal enum SystemStateCode
{
	__CAN_SAVE__ = 0x10000,//セーブロード画面を呼び出し可能か？
	__CAN_BEGIN__ = 0x20000,//BEGIN命令を呼び出し可能か？
	Title_Begin = 0,//初期状態
	Openning = 1,//最初の入力待ち
	Train_Begin = 0x10,//BEGIN TRAINから。
	Train_CallEventTrain = 0x11,//@EVENTTRAINの呼び出し中。スキップ可能
	Train_CallShowStatus = 0x12,//@SHOW_STATUSの呼び出し中
	Train_CallComAbleXX = 0x13,//@COM_ABLExxの呼び出し中。スキップの場合、RETURN 1とする。
	Train_CallShowUserCom = 0x14,//@SHOW_USERCOMの呼び出し中
	Train_WaitInput = 0x15,//入力待ち状態。選択が実行可能ならEVENTCOMからCOMxx、そうでなければ@USERCOMにRESULTを渡す
	Train_CallEventCom = 0x16 | __CAN_BEGIN__,//@EVENTCOMの呼び出し中

	Train_CallComXX = 0x17 | __CAN_BEGIN__,//@COMxxの呼び出し中
	Train_CallSourceCheck = 0x18 | __CAN_BEGIN__,//@SOURCE_CHECKの呼び出し中
	Train_CallEventComEnd = 0x19 | __CAN_BEGIN__,//@EVENTCOMENDの呼び出し中。スキップ可能。Train_CallEventTrainへ帰る。@USERCOMの呼び出し中もここ

	Train_DoTrain = 0x1A,

	AfterTrain_Begin = 0x20 | __CAN_BEGIN__,//BEGIN AFTERTRAINから。@EVENTENDを呼び出してNormalへ。

	Ablup_Begin = 0x30,//BEGIN ABLUPから。
	Ablup_CallShowJuel = 0x31,//@SHOW_JUEL
	Ablup_CallShowAblupSelect = 0x32,//@SHOW_ABLUP_SELECT
	Ablup_WaitInput = 0x33,//
	Ablup_CallAblupXX = 0x34 | __CAN_BEGIN__,//@ABLUPxxがない場合は、@USERABLUPにRESULTを渡す。Ablup_CallShowJuelへ戻る。

	Turnend_Begin = 0x40 | __CAN_BEGIN__,//BEGIN TURNENDから。@EVENTTURNENDを呼び出してNormalへ。

	Shop_Begin = 0x50 | __CAN_SAVE__,//BEGIN SHOPから
	Shop_CallEventShop = 0x51 | __CAN_BEGIN__ | __CAN_SAVE__,//@EVENTSHOPの呼び出し中。スキップ可能
	Shop_CallShowShop = 0x52 | __CAN_SAVE__,//@SHOW_SHOPの呼び出し中
	Shop_WaitInput = 0x53 | __CAN_SAVE__,//入力待ち状態。アイテムが存在するならEVENTBUYにBOUGHT、そうでなければ@USERSHOPにRESULTを渡す
	Shop_CallEventBuy = 0x54 | __CAN_BEGIN__ | __CAN_SAVE__,//@USERSHOPまた@EVENTBUYはの呼び出し中

	SaveGame_Begin = 0x100,//SAVEGAMEから
	SaveGame_WaitInput = 0x101,//入力待ち
	SaveGame_WaitInputOverwrite = 0x102,//上書きの許可待ち
	SaveGame_CallSaveInfo = 0x103,//@SAVEINFO呼び出し中。20回。
	LoadGame_Begin = 0x110,//LOADGAMEから
	LoadGame_WaitInput = 0x111,//入力待ち
	LoadGameOpenning_Begin = 0x120,//最初に[1]を選択したとき。
	LoadGameOpenning_WaitInput = 0x121,//入力待ち


	//AutoSave_Begin = 0x200,
	AutoSave_CallSaveInfo = 0x201,
	AutoSave_CallUniqueAutosave = 0x202,
	AutoSave_Skipped = 0x203,

	LoadData_DataLoaded = 0x210,//データロード直後
	LoadData_CallSystemLoad = 0x211 | __CAN_BEGIN__,//データロード直後
	LoadData_CallEventLoad = 0x212 | __CAN_BEGIN__,//@EVENTLOADの呼び出し中。スキップ可能

	Openning_TitleLoadgame = 0x220,

	System_Reloaderb = 0x230,
	First_Begin = 0x240,

	Normal = 0xFFFF | __CAN_BEGIN__ | __CAN_SAVE__,//特に何でもないとき。ScriptEndに達したらエラー
}

internal enum BeginType
{
	NULL = 0,
	SHOP = 2,
	TRAIN = 3,
	AFTERTRAIN = 4,
	ABLUP = 5,
	TURNEND = 6,
	FIRST = 7,
	TITLE = 8,
}

internal sealed class ProcessState : IProcessState
{
	/// <summary>
	/// 候选 4 · 第 1 刀：调试 trace 改为注入通道（<see cref="ProcessTrace"/>），本类不再依赖
	/// EmueraConsole / Program.DebugMode / GlobalStatic.Process。
	/// traceClearGate 替代原先的 GlobalStatic.Process.MethodStack() == 0 判定
	///（ADR-0011 决策三要求子模块不持 Process 反向引用；注入委托属于显式依赖，非 ambient 反向引用）。
	/// </summary>
	public ProcessState(ProcessTrace trace, Func<bool>? traceClearGate = null)
	{
		this.trace = trace;
		this.traceClearGate = traceClearGate;
		callStack = new CallStack(trace);
	}
	readonly ProcessTrace trace;
	readonly Func<bool>? traceClearGate;
	/// <summary>
	/// 候选 4 · 第 2 刀：执行游标与函数调用栈已劈出为 <see cref="CallStack"/>。
	/// 本类退为协调者（会话状态 + BEGIN 状态机），下列成员仅作转发 ——
	/// 以守住 ADR-0011 决策四 G1：指令层与 Process 的调用语法零改动。
	/// </summary>
	readonly CallStack callStack;

	//private LogicalLine nextLine;

	public int lineCount { get => callStack.LineCount; set => callStack.LineCount = value; }

	public int currentMin { get => callStack.CurrentMin; set => callStack.CurrentMin = value; }

	public bool ScriptEnd => callStack.ScriptEnd;

	public int functionCount => callStack.FunctionCount;

	SystemStateCode sysStateCode = SystemStateCode.Title_Begin;
	BeginType begintype = BeginType.NULL;
	public bool isBegun { get { return begintype != BeginType.NULL; } }

	public LogicalLine CurrentLine { get { return callStack.CurrentLine; } set { callStack.CurrentLine = value; } }
	public LogicalLine ErrorLine
	{
		get
		{
			//if (RunningLine != null)
			//	return RunningLine;
			return callStack.CurrentLine;
		}
	}

	//IF文中でELSEIF文の中身をチェックするなどCurrentLineと作業中のLineが違う時にセットする
	//public LogicalLine RunningLine { get; set; }
	//1755a 呼び出し元消滅
	//public bool Sequential { get { return sequential; } }
	public CalledFunction CurrentCalled => callStack.CurrentCalled;
	public SystemStateCode SystemState
	{
		get { return sysStateCode; }
		set { sysStateCode = value; }
	}

	public void ShiftNextLine() => callStack.ShiftNextLine();

	/// <summary>
	/// 関数内の移動。JUMPではなくGOTOやIF文など
	/// </summary>
	/// <param name="line"></param>
	public void JumpTo(LogicalLine line) => callStack.JumpTo(line);
	#region EE_FORCE_QUIT系
	// public void SetBegin(string keyword)
	public void SetBegin(string keyword, bool force)
	{//TrimとToUpper済みのはず
		switch (keyword)
		{
			case "SHOP":
				AppContents.UnloadTempLoadedConstImageNames();
				AppContents.UnloadTempLoadedGraphicsImageNames();
				SetBegin(BeginType.SHOP, force); return;
			case "TRAIN":
				SetBegin(BeginType.TRAIN, force); return;
			case "AFTERTRAIN":
				SetBegin(BeginType.AFTERTRAIN, force); return;
			case "ABLUP":
				SetBegin(BeginType.ABLUP, force); return;
			case "TURNEND":
				SetBegin(BeginType.TURNEND, force); return;
			case "FIRST":
				AppContents.UnloadTempLoadedConstImageNames();
				AppContents.UnloadTempLoadedGraphicsImageNames();
				SetBegin(BeginType.FIRST, force); return;
			case "TITLE":
				SetBegin(BeginType.TITLE, force); return;
		}
		throw new CodeEE(string.Format(trerror.InvalidBeginArg.Text, keyword));
	}

	//public void SetBegin(BeginType type)
	public void SetBegin(BeginType type, bool force)
	{
		string errmes;
		switch (type)
		{
			case BeginType.SHOP:
			case BeginType.TRAIN:
			case BeginType.AFTERTRAIN:
			case BeginType.ABLUP:
			case BeginType.TURNEND:
			case BeginType.FIRST:
				if (force == true) break;
				if ((sysStateCode & SystemStateCode.__CAN_BEGIN__) != SystemStateCode.__CAN_BEGIN__)
				{
					errmes = "BEGIN";
					goto err;
				}
				break;
			//1.729 BEGIN TITLEはどこでも使えるように
			case BeginType.TITLE:
				break;
				//BEGINの処理中でチェック済み
				//default:
				//    throw new ExeEE("不適当なBEGIN呼び出し");
		}
		begintype = type;
		return;
	err:
		string funcName = callStack.BottomFunctionName;
		throw new CodeEE(string.Format(trerror.CanNotUseInstruction.Text, funcName, errmes));
	}
	#endregion

	public void SaveLoadData(bool saveData)
	{

		if (saveData)
			sysStateCode = SystemStateCode.SaveGame_Begin;
		else
			sysStateCode = SystemStateCode.LoadGame_Begin;
		//ClearFunctionList();
		return;
	}

	public void ClearFunctionList()
	{
		if (!isClone)
			trace.Clear(traceClearGate);
		callStack.Clear();
		begintype = BeginType.NULL;
	}

	public bool calledWhenNormal { get; set; } = true;
	/// <summary>
	/// BEGIN命令によるプログラム状態の変化
	/// </summary>
	/// <param name="key"></param>
	/// <returns></returns>
	public void Begin()
	{
		//@EVENTSHOPからの呼び出しは一旦破棄
		if (sysStateCode == SystemStateCode.Shop_CallEventShop)
			return;

		switch (begintype)
		{
			case BeginType.SHOP:
				if (sysStateCode == SystemStateCode.Normal)
					calledWhenNormal = true;
				else
					calledWhenNormal = false;
				sysStateCode = SystemStateCode.Shop_Begin;
				break;
			case BeginType.TRAIN:
				sysStateCode = SystemStateCode.Train_Begin;
				break;
			case BeginType.AFTERTRAIN:
				sysStateCode = SystemStateCode.AfterTrain_Begin;
				break;
			case BeginType.ABLUP:
				sysStateCode = SystemStateCode.Ablup_Begin;
				break;
			case BeginType.TURNEND:
				sysStateCode = SystemStateCode.Turnend_Begin;
				break;
			case BeginType.FIRST:
				sysStateCode = SystemStateCode.First_Begin;
				break;
			case BeginType.TITLE:
				sysStateCode = SystemStateCode.Title_Begin;
				break;
				//セット時に判定してるので、ここには来ないはず
				//default:
				//    throw new ExeEE("不適当なBEGIN呼び出し");
		}
		if (trace.Enabled)
		{
			trace.Clear(null);
			trace.Add("BEGIN:" + begintype.ToString());
		}
		callStack.Clear();
		begintype = BeginType.NULL;
		return;
	}

	/// <summary>
	/// システムによる強制的なBEGIN
	/// </summary>
	/// <param name="type"></param>
	public void Begin(BeginType type)
	{
		begintype = type;
		sysStateCode = SystemStateCode.Title_Begin;
		Begin();
	}

	public LogicalLine GetCurrentReturnAddress => callStack.GetCurrentReturnAddress;

	public LogicalLine GetReturnAddressSequensial(int curerntDepth) => callStack.GetReturnAddressSequensial(curerntDepth);

	public string Scope => callStack.Scope;

	/// <summary>
	/// 通常関数の RETURN。清栈语义见 <see cref="CallStack.Return(long)"/>：栈清空且无处可回时
	/// 由 CallStack 返回 true，此处据此触发挂起的 BEGIN —— 这是 BEGIN 状态机与调用栈之间
	/// 唯一的耦合点，劈分后保持单向（调用栈不反向依赖 BEGIN）。
	/// </summary>
	public void Return(long ret)
	{
		if (callStack.Return(ret) && begintype != BeginType.NULL)
			Begin();
	}

	public void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm)
		=> callStack.IntoFunction(call, srcArgs, exm);

	#region userdifinedmethod
	public bool IsFunctionMethod => callStack.IsFunctionMethod;

	public SingleTerm MethodReturnValue { get => callStack.MethodReturnValue; set => callStack.MethodReturnValue = value; }

	public void ReturnF(SingleTerm ret) => callStack.ReturnF(ret);

	#endregion

	bool isClone;
	public bool IsClone { get { return isClone; } set { isClone = value; } }

	// functionListのコピーを必要とする呼び出し元が無かったのでコピーしないことにする。
	public ProcessState Clone()
	{
		// 候选 4 · 第 2 刀：调用栈历来不随 Clone 复制（functionList / MethodReturnValue /
		// currentMin / lineCount 全部不复制），只带 PC —— 新 CallStack 为空栈，与原先一致。
		ProcessState ret = new(trace, traceClearGate)
		{
			isClone = true,
			CurrentLine = callStack.CurrentLine,
			sysStateCode = sysStateCode,
			begintype = begintype
		};
		return ret;
	}
	IProcessState IProcessState.Clone() => Clone();
	//public ProcessState CloneForFunctionMethod()
	//{
	//    ProcessState ret = new ProcessState(console);
	//    ret.isClone = true;

	//    //どうせ消すからコピー不要
	//    //foreach (CalledFunction func in functionList)
	//    //	ret.functionList.Add(func.Clone());
	//    ret.currentLine = this.currentLine;
	//    ret.nextLine = this.nextLine;
	//    //ret.sequential = this.sequential;
	//    ret.sysStateCode = this.sysStateCode;
	//    ret.begintype = this.begintype;
	//    //ret.MethodReturnValue = this.MethodReturnValue;
	//    return ret;
	//}
}
