using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.UI.Game.Image;
using System;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trsl = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.SystemLine;

namespace MinorShift.Emuera.GameProc;

internal sealed class SystemProc
{
	readonly EmueraConsole console;
	readonly IVariableEvaluator vEvaluator;
	readonly GameBase gamebase;
	readonly string[] trainName;
	readonly LabelDictionary labelDic;
	readonly ExecutionState executionState;
	IProcessState state => executionState.CurrentState;
	delegate void SystemProcess();
	Dictionary<SystemStateCode, SystemProcess> systemProcessDictionary = [];
	internal bool NeedWaitToEventComEnd;
	bool needCheck = true;
	int[] comAble = null!;
	int lastCalledComable = -1;
	int lastAddCom = -1;
	int printComCount;
	bool[] dataIsAvailable = new bool[21];
	bool isFirstTime = true;
	const int AutoSaveIndex = 99;
	int page;
	int saveTarget = -1;

	internal SystemProc(EmueraConsole console, IVariableEvaluator vEvaluator, GameBase gamebase, string[] trainName, ExecutionState executionState, LabelDictionary labelDic)
	{
		this.console = console;
		this.vEvaluator = vEvaluator;
		this.gamebase = gamebase;
		this.trainName = trainName;
		this.executionState = executionState;
		this.labelDic = labelDic;
	}

	internal void Init()
	{
		comAble = new int[trainName.Length];
		systemProcessDictionary.Add(SystemStateCode.Title_Begin, new SystemProcess(beginTitle));
		systemProcessDictionary.Add(SystemStateCode.Openning, new SystemProcess(endOpenning));

		systemProcessDictionary.Add(SystemStateCode.Train_Begin, new SystemProcess(beginTrain));
		systemProcessDictionary.Add(SystemStateCode.Train_CallEventTrain, new SystemProcess(endCallEventTrain));
		systemProcessDictionary.Add(SystemStateCode.Train_CallShowStatus, new SystemProcess(endCallShowStatus));
		systemProcessDictionary.Add(SystemStateCode.Train_CallComAbleXX, new SystemProcess(endCallComAbleXX));
		systemProcessDictionary.Add(SystemStateCode.Train_CallShowUserCom, new SystemProcess(endCallShowUserCom));
		systemProcessDictionary.Add(SystemStateCode.Train_WaitInput, new SystemProcess(trainWaitInput));
		systemProcessDictionary.Add(SystemStateCode.Train_CallEventCom, new SystemProcess(endEventCom));
		systemProcessDictionary.Add(SystemStateCode.Train_CallComXX, new SystemProcess(endCallComXX));
		systemProcessDictionary.Add(SystemStateCode.Train_CallSourceCheck, new SystemProcess(endCallSourceCheck));
		systemProcessDictionary.Add(SystemStateCode.Train_CallEventComEnd, new SystemProcess(endCallEventComEnd)); ;
		systemProcessDictionary.Add(SystemStateCode.Train_DoTrain, new SystemProcess(doTrain));

		systemProcessDictionary.Add(SystemStateCode.AfterTrain_Begin, new SystemProcess(beginAfterTrain));

		systemProcessDictionary.Add(SystemStateCode.Ablup_Begin, new SystemProcess(beginAblup));
		systemProcessDictionary.Add(SystemStateCode.Ablup_CallShowJuel, new SystemProcess(endCallShowJuel));
		systemProcessDictionary.Add(SystemStateCode.Ablup_CallShowAblupSelect, new SystemProcess(endCallShowAblupSelect));
		systemProcessDictionary.Add(SystemStateCode.Ablup_WaitInput, new SystemProcess(ablupWaitInput));
		systemProcessDictionary.Add(SystemStateCode.Ablup_CallAblupXX, new SystemProcess(endCallAblupXX));

		systemProcessDictionary.Add(SystemStateCode.Turnend_Begin, new SystemProcess(beginTurnend));

		systemProcessDictionary.Add(SystemStateCode.Shop_Begin, new SystemProcess(beginShop));
		systemProcessDictionary.Add(SystemStateCode.Shop_CallEventShop, new SystemProcess(endCallEventShop));
		systemProcessDictionary.Add(SystemStateCode.Shop_CallShowShop, new SystemProcess(endCallShowShop));
		systemProcessDictionary.Add(SystemStateCode.Shop_WaitInput, new SystemProcess(shopWaitInput));
		systemProcessDictionary.Add(SystemStateCode.Shop_CallEventBuy, new SystemProcess(endCallEventBuy));

		systemProcessDictionary.Add(SystemStateCode.SaveGame_Begin, new SystemProcess(beginSaveGame));
		systemProcessDictionary.Add(SystemStateCode.SaveGame_WaitInput, new SystemProcess(saveGameWaitInput));
		systemProcessDictionary.Add(SystemStateCode.SaveGame_WaitInputOverwrite, new SystemProcess(saveGameWaitInputOverwrite));
		systemProcessDictionary.Add(SystemStateCode.SaveGame_CallSaveInfo, new SystemProcess(endCallSaveInfo));
		systemProcessDictionary.Add(SystemStateCode.LoadGame_Begin, new SystemProcess(beginLoadGame));
		systemProcessDictionary.Add(SystemStateCode.LoadGame_WaitInput, new SystemProcess(loadGameWaitInput));
		systemProcessDictionary.Add(SystemStateCode.LoadGameOpenning_Begin, new SystemProcess(beginLoadGameOpening));
		systemProcessDictionary.Add(SystemStateCode.LoadGameOpenning_WaitInput, new SystemProcess(loadGameWaitInput));

		systemProcessDictionary.Add(SystemStateCode.AutoSave_CallSaveInfo, new SystemProcess(endAutoSaveCallSaveInfo));
		systemProcessDictionary.Add(SystemStateCode.AutoSave_CallUniqueAutosave, new SystemProcess(endAutoSave));

		systemProcessDictionary.Add(SystemStateCode.LoadData_DataLoaded, new SystemProcess(beginDataLoaded));
		systemProcessDictionary.Add(SystemStateCode.LoadData_CallSystemLoad, new SystemProcess(endSystemLoad));
		systemProcessDictionary.Add(SystemStateCode.LoadData_CallEventLoad, new SystemProcess(endEventLoad));

		systemProcessDictionary.Add(SystemStateCode.Openning_TitleLoadgame, new SystemProcess(endTitleLoadgame));

		systemProcessDictionary.Add(SystemStateCode.System_Reloaderb, new SystemProcess(endReloaderb));
		systemProcessDictionary.Add(SystemStateCode.First_Begin, new SystemProcess(beginFirst));


		systemProcessDictionary.Add(SystemStateCode.Normal, new SystemProcess(endNormal));
		return;
	}

	public void Run()
	{
		systemProcessDictionary[state.SystemState]();
	}

	internal bool ClearCommands()
	{
		executionState.coms.Clear();
		executionState.count = 0;
		executionState.isCTrain = false;
		executionState.skipPrint = true;
		return CallFunction("CALLTRAINEND", false, false);
	}

	internal bool CallFunction(string functionName, bool force, bool isEvent)
	{
		CalledFunction call;
		if (isEvent)
			call = CalledFunction.CallEventFunction(labelDic, functionName, null!);
		else
			call = CalledFunction.CallFunction(labelDic, functionName, null!);
		if (call == null)
			if (!force)
				return false;
			else
				throw new CodeEE(string.Format(trerror.FuncIsNotFound.Text, functionName));
		state.IntoFunction(call, null!, null!);
		return true;
	}

	void setWait()
	{
		console.ReadAnyKey();
	}

	void setWaitInput()
	{
		InputRequest req = new();
		if (executionState.flowinput)
		{
			req.HasDefValue = true;
			req.DefIntValue = executionState.flowinputDef;
			req.MouseInput = executionState.flowinput;
			req.DefStrValue = executionState.flowinputDefString;
		}
		if (executionState.flowinputString)
			req.InputType = InputType.StrValue;
		else
			req.InputType = InputType.IntValue;
		req.IsSystemInput = true;
		if (executionState.flowinputForceSkip)
		{
			executionState.systemResult = req.DefIntValue;
			if (executionState.flowinputString)
				vEvaluator.RESULTS = req.DefStrValue;
		}
		else if (executionState.flowinputCanSkip && console.MesSkip)
		{
			executionState.systemResult = req.DefIntValue;
			if (executionState.flowinputString)
				vEvaluator.RESULTS = req.DefStrValue;
		}
		console.WaitInput(req);
	}

	void beginTitle()
	{
		if (executionState.isCTrain)
			if (ClearCommands())
				return;
		executionState.skipPrint = false;
		console.ResetStyle();
		executionState.DeleteAllPrevState();
			if (Program.AnalysisMode)
			{
				console.PrintSystemLine(trsl.AnalysisCompleted.Text);
				console.OutputSystemLog("Analysis.log");
				console.noOutputLog = true;
				console.PrintSystemLine(trsl.PressEnterOrClick.Text);
#if !HEADLESS
				System.Media.SystemSounds.Asterisk.Play();
#endif
				console.ThrowTitleError(false);
				return;
			}
			if ((!executionState.noError) && (!Config.CompatiErrorLine))
			{
				console.PrintErrorButton(trsl.ExitBecauseCanNotInterpreted1.Text, null, 3);
				console.PrintSystemLine(string.Format(trsl.ExitBecauseCanNotInterpreted2.Text, Config.GetConfigName(ConfigCode.CompatiErrorLine)));
				console.PrintSystemLine(trsl.ExitBecauseCanNotInterpreted3.Text);
				console.OutputSystemLog("emuera.log");
				console.noOutputLog = true;
				console.PrintSystemLine(trsl.PressEnterOrClick.Text);
#if !HEADLESS
				System.Media.SystemSounds.Asterisk.Play();
#endif
				console.ThrowTitleError(true);
				return;
			}
			//SYSTEM_TITLE中にSAVEGAME/LOADGAME/BEGINを使えるよう、実行前にNormalへ遷移させる
			state.SystemState = SystemStateCode.Normal;
			if (CallFunction("SYSTEM_TITLE", false, false))
			{
				return;
			}
			console.PrintBar();
			console.NewLine();
			console.Alignment = DisplayLineAlignment.CENTER;
			console.PrintSingleLine(gamebase.ScriptTitle);
			if (gamebase.ScriptVersion != 0)
				console.PrintSingleLine(gamebase.ScriptVersionText);
			console.PrintSingleLine(gamebase.ScriptAutherName);
			console.PrintSingleLine("(" + gamebase.ScriptYear + ")");
			console.NewLine();
			console.PrintSingleLine(gamebase.ScriptDetail);
			console.Alignment = DisplayLineAlignment.LEFT;

			console.PrintBar();
			console.NewLine();
			console.PrintSingleLine("[0] " + Config.TitleMenuString0);
			console.PrintSingleLine("[1] " + Config.TitleMenuString1);
			openingInput();
			return;
		}

		void openingInput()
		{
			setWaitInput();
			state.SystemState = SystemStateCode.Openning;
			return;
		}

		void endOpenning()
		{
			if (executionState.systemResult == 0)
			{
				vEvaluator.ResetData();
				vEvaluator.AddCharacterFromCsvNo(0);
				if (gamebase.DefaultCharacter > 0)
					vEvaluator.AddCharacterFromCsvNo(gamebase.DefaultCharacter);
				console.PrintBar();
				console.NewLine();
				beginFirst();
			}
			else if (executionState.systemResult == 1)
			{
				if (CallFunction("TITLE_LOADGAME", false, false))
				{
					state.SystemState = SystemStateCode.Openning_TitleLoadgame;
				}
				else
				{
					beginLoadGameOpening();
				}
			}
			else
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				openingInput();
			}

		}

		void beginFirst()
		{
			state.SystemState = SystemStateCode.Normal;
			if (executionState.isCTrain)
				if (ClearCommands())
					return;
			executionState.skipPrint = false;
			CallFunction("EVENTFIRST", true, true);
		}

		void endTitleLoadgame()
		{
			beginTitle();
		}

		void beginTrain()
		{
			vEvaluator.UpdateInBeginTrain();
			state.SystemState = SystemStateCode.Train_CallEventTrain;
			if (!CallFunction("EVENTTRAIN", false, true))
			{
				endCallEventTrain();
			}
		}

		void endCallEventTrain()
		{
			if (vEvaluator.NEXTCOM >= 0)
			{
				state.SystemState = SystemStateCode.Train_CallEventCom;
				vEvaluator.SELECTCOM = vEvaluator.NEXTCOM;
				vEvaluator.NEXTCOM = 0;
				callEventCom();
				return;
			}
			else
			{
				if (executionState.isCTrain)
					executionState.skipPrint = true;
				CallFunction("SHOW_STATUS", true, false);
				state.SystemState = SystemStateCode.Train_CallShowStatus;
			}
		}

		void endCallShowStatus()
		{
			state.SystemState = SystemStateCode.Train_CallComAbleXX;
			lastCalledComable = -1;
			lastAddCom = -1;
			printComCount = 0;
			for (int i = 0; i < comAble.Length; i++)
				comAble[i] = -1;
			endCallComAbleXX();
		}

		string getTrainComString(int trainCode, int comNo)
		{
			string name = trainName[trainCode];
			return string.Format("{0}[{1,3}]", name, comNo);
		}

		void endCallComAbleXX()
		{
			if ((lastCalledComable >= 0) && (trainName[lastCalledComable] != null))
			{
				lastAddCom++;
				if (vEvaluator.RESULT != 0)
				{
					comAble[lastAddCom] = lastCalledComable;
					if (!executionState.isCTrain)
					{
						console.PrintC(getTrainComString(lastCalledComable, lastAddCom), true);
						printComCount++;
						if ((Config.PrintCPerLine > 0) && (printComCount % Config.PrintCPerLine == 0))
							console.PrintFlush(false);
					}
					console.RefreshStrings(false);
				}
			}
			while (++lastCalledComable < trainName.Length)
			{
				if (trainName[lastCalledComable] == null)
					continue;
				string comName = string.Format("COM_ABLE{0}", lastCalledComable);
				if (!CallFunction(comName, false, false))
				{
					lastAddCom++;
					if (Config.ComAbleDefault == 0)
						continue;
					comAble[lastAddCom] = lastCalledComable;
					if (!executionState.isCTrain)
					{
						console.PrintC(getTrainComString(lastCalledComable, lastAddCom), true);
						printComCount++;
						if ((Config.PrintCPerLine > 0) && (printComCount % Config.PrintCPerLine == 0))
							console.PrintFlush(false);
					}
					continue;
				}
				console.RefreshStrings(false);
				return;
			}
			if (lastCalledComable >= trainName.Length)
			{
				state.SystemState = SystemStateCode.Train_CallShowUserCom;
				console.PrintFlush(false);
				console.RefreshStrings(false);
				CallFunction("SHOW_USERCOM", true, false);
			}
		}

		void endCallShowUserCom()
		{
			if (executionState.skipPrint)
				executionState.skipPrint = false;
			vEvaluator.UpdateAfterShowUsercom();
			if (!executionState.isCTrain)
			{
				setWaitInput();

				state.SystemState = SystemStateCode.Train_WaitInput;
			}
			else
			{
				if (executionState.count < executionState.coms.Count)
				{
					executionState.systemResult = executionState.coms[executionState.count];
					executionState.count++;
					trainWaitInput();
				}
			}
		}

		void trainWaitInput()
		{
			int selectCom = -1;
			if (!executionState.isCTrain)
			{
				if ((executionState.systemResult >= 0) && (executionState.systemResult < comAble.Length))
					selectCom = comAble[executionState.systemResult];
			}
			else
			{
				for (int i = 0; i < comAble.Length; i++)
				{
					if (comAble[i] == executionState.systemResult)
						selectCom = (int)executionState.systemResult;
				}
				console.PrintSingleLine(string.Format(trerror.ExecutedCom.Text, executionState.count, executionState.coms.Count));
			}
			if (selectCom >= 0)
			{
				vEvaluator.SELECTCOM = selectCom;
				callEventCom();
			}
			else
			{
				if (executionState.isCTrain)
					console.PrintSingleLine(trerror.CouldNotExecuteCom.Text);
				vEvaluator.RESULT = executionState.systemResult;
				state.SystemState = SystemStateCode.Train_CallEventComEnd;
				CallFunction("USERCOM", true, false);
			}
		}

		void doTrain()
		{
			vEvaluator.UpdateAfterShowUsercom();
			vEvaluator.SELECTCOM = executionState.doTrainSelectCom;
			callEventCom();
		}

		void callEventCom()
		{
			vEvaluator.UpdateAfterInputCom();
			state.SystemState = SystemStateCode.Train_CallEventCom;
			if (!CallFunction("EVENTCOM", false, true))
				endEventCom();
			return;
		}

		void endEventCom()
		{
			long selectCom = vEvaluator.SELECTCOM;
			string comName = string.Format("COM{0}", selectCom);
			state.SystemState = SystemStateCode.Train_CallComXX;
			CallFunction(comName, true, false);
		}

		void endCallComXX()
		{
			if (vEvaluator.RESULT == 0)
			{
				endCallEventComEnd();
			}
			else
			{
				state.SystemState = SystemStateCode.Train_CallSourceCheck;
				CallFunction("SOURCE_CHECK", true, false);
			}
		}

		void endCallSourceCheck()
		{
			vEvaluator.UpdateAfterSourceCheck();
			state.SystemState = SystemStateCode.Train_CallEventComEnd;
			NeedWaitToEventComEnd = true;
			if (!CallFunction("EVENTCOMEND", false, true))
			{
				endCallEventComEnd();
			}
		}

		void endCallEventComEnd()
		{
			if (console.LastLineIsTemporary && !executionState.isCTrain && needCheck)
			{
				if (console.LastLineIsEmpty)
				{
					console.deleteLine(2);
					console.PrintTemporaryLine(trerror.InvalidValue.Text);
				}
				console.updatedGeneration = true;
				endCallShowUserCom();
			}
			else
			{
				if (executionState.isCTrain && executionState.count == executionState.coms.Count)
				{
					executionState.isCTrain = false;
					executionState.skipPrint = false;
					executionState.coms.Clear();
					executionState.count = 0;
					if (CallFunction("CALLTRAINEND", false, false))
					{
						needCheck = false;
						return;
					}
				}
				needCheck = true;
				if (NeedWaitToEventComEnd)
					setWait();
				NeedWaitToEventComEnd = false;
				endCallEventTrain();
			}
		}

		void beginAfterTrain()
		{
			if (executionState.isCTrain)
				if (ClearCommands())
					return;
			executionState.skipPrint = false;
			state.SystemState = SystemStateCode.Normal;
			CallFunction("EVENTEND", true, true);
		}

		void beginAblup()
		{
			if (executionState.isCTrain)
				if (ClearCommands())
					return;
			executionState.skipPrint = false;
			state.SystemState = SystemStateCode.Ablup_CallShowJuel;
			CallFunction("SHOW_JUEL", true, false);
		}

		void endCallShowJuel()
		{
			state.SystemState = SystemStateCode.Ablup_CallShowAblupSelect;
			CallFunction("SHOW_ABLUP_SELECT", true, false);
		}

		void endCallShowAblupSelect()
		{
			setWaitInput();
			state.SystemState = SystemStateCode.Ablup_WaitInput;
		}

		void ablupWaitInput()
		{
			if ((executionState.systemResult >= 0) && (executionState.systemResult < 100))
			{
				state.SystemState = SystemStateCode.Ablup_CallAblupXX;
				string ablName = string.Format("ABLUP{0}", executionState.systemResult);
				if (!CallFunction(ablName, false, false))
				{
					console.deleteLine(1);
					console.PrintTemporaryLine(trerror.InvalidValue.Text);
					console.updatedGeneration = true;
					endCallShowAblupSelect();
				}
			}
			else
			{
				vEvaluator.RESULT = executionState.systemResult;
				state.SystemState = SystemStateCode.Ablup_CallAblupXX;
				CallFunction("USERABLUP", true, false);
			}
		}

		void endCallAblupXX()
		{
			if (console.LastLineIsTemporary)
			{
				if (console.LastLineIsEmpty)
				{
					console.deleteLine(2);
					console.PrintTemporaryLine("無効な値です");
				}
				console.updatedGeneration = true;
				endCallShowAblupSelect();
			}
			else
				beginAblup();
		}

		void beginTurnend()
		{
			if (executionState.isCTrain)
				if (ClearCommands())
					return;
			executionState.skipPrint = false;
			CallFunction("EVENTTURNEND", true, true);
			state.SystemState = SystemStateCode.Normal;
		}

		void beginShop()
		{
			if (executionState.isCTrain)
				if (ClearCommands())
					return;
			executionState.skipPrint = false;
			state.SystemState = SystemStateCode.Shop_CallEventShop;
			if (!CallFunction("EVENTSHOP", false, true))
			{
				endCallEventShop();
			}
		}

		void endCallEventShop()
		{
			saveTarget = -1;
			if (Config.AutoSave && state.calledWhenNormal)
				beginAutoSave();
			else
			{
				state.SystemState = SystemStateCode.AutoSave_Skipped;
				endAutoSaveCallSaveInfo();
			}
		}

		void beginAutoSave()
		{
			if (CallFunction("SYSTEM_AUTOSAVE", false, false))
			{
				state.SystemState = SystemStateCode.AutoSave_CallUniqueAutosave;
				return;
			}
			saveTarget = AutoSaveIndex;
			vEvaluator.SAVEDATA_TEXT = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + " ";
			state.SystemState = SystemStateCode.AutoSave_CallSaveInfo;
			if (!CallFunction("SAVEINFO", false, false))
				endAutoSaveCallSaveInfo();
		}

		void endAutoSaveCallSaveInfo()
		{
			if (saveTarget == AutoSaveIndex)
			{
				if (!vEvaluator.SaveTo(saveTarget, vEvaluator.SAVEDATA_TEXT))
				{
					console.PrintError(trerror.AutoSaveError1.Text);
					console.PrintError(trerror.AutoSaveError2.Text);
					console.ReadAnyKey();
				}
			}
			endAutoSave();
		}

		void endAutoSave()
		{
			if (state.isBegun)
			{
				state.Begin();
				return;
			}
			state.SystemState = SystemStateCode.Shop_CallShowShop;
			CallFunction("SHOW_SHOP", true, false);
		}

		void endCallShowShop()
		{
			setWaitInput();
			state.SystemState = SystemStateCode.Shop_WaitInput;
		}

		void shopWaitInput()
		{
			if ((executionState.systemResult >= 0) && (executionState.systemResult < Config.MaxShopItem))
			{
				if (vEvaluator.ItemSales(executionState.systemResult))
				{
					if (vEvaluator.BuyItem(executionState.systemResult))
					{
						state.SystemState = SystemStateCode.Shop_CallEventBuy;
						if (!CallFunction("EVENTBUY", false, true))
							endCallEventBuy();
						return;
					}
					else
					{
						console.deleteLine(1);
						console.PrintTemporaryLine(trerror.NotEnoughMoney.Text);
					}
				}
				else
				{
					console.deleteLine(1);
					console.PrintTemporaryLine(trerror.OutOfStock.Text);
				}
				endCallShowShop();
				return;
			}
			else
			{
				vEvaluator.RESULT = executionState.systemResult;

				CallFunction("USERSHOP", true, false);
				state.SystemState = SystemStateCode.Shop_CallEventBuy;
				return;
			}
		}

		void endCallEventBuy()
		{
			if (console.LastLineIsTemporary)
			{
				if (console.LastLineIsEmpty)
				{
					console.deleteLine(2);
					console.PrintTemporaryLine(trerror.InvalidValue.Text);
				}
				console.updatedGeneration = true;
				endCallShowShop();
			}
			else
			{
				endAutoSave();
			}
		}


		void beginDataLoaded()
		{
			state.SystemState = SystemStateCode.LoadData_CallSystemLoad;

			if (!CallFunction("SYSTEM_LOADEND", false, false))
				endSystemLoad();
		}
		void endSystemLoad()
		{
			AppContents.UnloadTempLoadedConstImageNames();
			AppContents.UnloadTempLoadedGraphicsImageNames();
			state.SystemState = SystemStateCode.LoadData_CallEventLoad;
			if (!CallFunction("EVENTLOAD", false, true))
			{
				endAutoSave();
			}
		}

		void endEventLoad()
		{
			endAutoSave();
		}

		void beginSaveGame()
		{
			console.PrintSingleLine(trsl.SaveQuestion.Text);
			state.SystemState = SystemStateCode.SaveGame_Begin;
			printSaveDataText();
		}

		void beginLoadGame()
		{
			console.PrintSingleLine(trsl.LoadQuestion.Text);
			state.SystemState = SystemStateCode.LoadGame_Begin;
			printSaveDataText();
		}

		void beginLoadGameOpening()
		{
			console.PrintSingleLine(trsl.LoadQuestion.Text);
			state.SystemState = SystemStateCode.LoadGameOpenning_Begin;
			printSaveDataText();
		}

		internal void LoadSilent()
		{
			state.SystemState = SystemStateCode.LoadGameOpenning_Begin;

			if (isFirstTime)
			{
				isFirstTime = false;
				dataIsAvailable = new bool[Config.SaveDataNos + 1];
			}
			int dataNo;
			for (int i = 0; i < page; i++)
			{
			}
			for (int i = 0; i < 20; i++)
			{
				dataNo = page * 20 + i;
				if (dataNo == dataIsAvailable.Length - 1)
					break;
				dataIsAvailable[dataNo] = false;
				if (!writeSavedataTextFrom_Silent(dataNo))
					continue;
				dataIsAvailable[dataNo] = true;
			}
			for (int i = page; i < ((dataIsAvailable.Length - 2) / 20); i++)
			{
			}
			dataIsAvailable[^1] = false;
			if (state.SystemState != SystemStateCode.SaveGame_Begin)
			{
				dataNo = AutoSaveIndex;
				if (writeSavedataTextFrom_Silent(dataNo))
					dataIsAvailable[^1] = true;
			}
			setWaitInput();
			if (state.SystemState == SystemStateCode.SaveGame_Begin)
				state.SystemState = SystemStateCode.SaveGame_WaitInput;
			else if (state.SystemState == SystemStateCode.LoadGame_Begin)
				state.SystemState = SystemStateCode.LoadGame_WaitInput;
			else
				state.SystemState = SystemStateCode.LoadGameOpenning_WaitInput;
		}

		void printSaveDataText()
		{
			if (isFirstTime)
			{
				isFirstTime = false;
				dataIsAvailable = new bool[Config.SaveDataNos + 1];
			}
			int dataNo;
			for (int i = 0; i < page; i++)
			{
				console.PrintFlush(false);
				console.Print(string.Format(trsl.DisplaySaveSlot.Text, i * 20, i * 20 + 19));
			}
			for (int i = 0; i < 20; i++)
			{
				dataNo = page * 20 + i;
				if (dataNo == dataIsAvailable.Length - 1)
					break;
				dataIsAvailable[dataNo] = false;
				console.PrintFlush(false);
				console.Print(string.Format("[{0, 2}] ", dataNo));
				if (!writeSavedataTextFrom(dataNo))
					continue;
				dataIsAvailable[dataNo] = true;
			}
			for (int i = page; i < ((dataIsAvailable.Length - 2) / 20); i++)
			{
				console.PrintFlush(false);
				console.Print(string.Format(trsl.DisplaySaveSlot.Text, (i + 1) * 20, (i + 1) * 20 + 19));
			}
			dataIsAvailable[^1] = false;
			if (state.SystemState != SystemStateCode.SaveGame_Begin)
			{
				dataNo = AutoSaveIndex;
				console.PrintFlush(false);
				console.Print(string.Format("[{0, 2}] ", dataNo));
				if (writeSavedataTextFrom(dataNo))
					dataIsAvailable[^1] = true;
			}
			console.RefreshStrings(false);
			console.PrintSingleLine("[100] 戻る");
			setWaitInput();
			if (state.SystemState == SystemStateCode.SaveGame_Begin)
				state.SystemState = SystemStateCode.SaveGame_WaitInput;
			else if (state.SystemState == SystemStateCode.LoadGame_Begin)
				state.SystemState = SystemStateCode.LoadGame_WaitInput;
			else
				state.SystemState = SystemStateCode.LoadGameOpenning_WaitInput;
		}

		void saveGameWaitInput()
		{
			if (executionState.systemResult == 100)
			{
				executionState.LoadPrevState();
				return;
			}
			else if (((int)executionState.systemResult / 20) != page && executionState.systemResult != AutoSaveIndex && executionState.systemResult >= 0 && executionState.systemResult < dataIsAvailable.Length - 1)
			{
				page = (int)executionState.systemResult / 20;
				state.SystemState = SystemStateCode.SaveGame_Begin;
				printSaveDataText();
				return;
			}
			bool available;
			if ((executionState.systemResult >= 0) && (executionState.systemResult < dataIsAvailable.Length - 1))
				available = dataIsAvailable[executionState.systemResult];
			else
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			saveTarget = (int)executionState.systemResult;

			GlobalStatic.ctrlZ.OnSavePrepare(saveTarget);

			if (available)
			{
				console.PrintSingleLine(trsl.DoYouOverwrite.Text);
				console.PrintC(trsl.Yes.Text, false);
				console.PrintC(trsl.No.Text, false);
				setWaitInput();
				state.SystemState = SystemStateCode.SaveGame_WaitInputOverwrite;
				return;
			}
			executionState.systemResult = 0;
			saveGameWaitInputOverwrite();
		}

		void saveGameWaitInputOverwrite()
		{
			if (executionState.systemResult == 1)
			{
				beginSaveGame();
				return;
			}
			else if (executionState.systemResult != 0)
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			vEvaluator.SAVEDATA_TEXT = DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss") + " ";
			state.SystemState = SystemStateCode.SaveGame_CallSaveInfo;
			if (!CallFunction("SAVEINFO", false, false))
				endCallSaveInfo();
		}

		void endCallSaveInfo()
		{
			if (!vEvaluator.SaveTo(saveTarget, vEvaluator.SAVEDATA_TEXT))
			{
				console.PrintError(trerror.UnexpectedSaveError.Text);
				console.ReadAnyKey();
			}

			GlobalStatic.ctrlZ.OnSave();

			executionState.LoadPrevState();
		}

		void loadGameWaitInput()
		{
			if (executionState.systemResult == 100)
			{
				if (state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
				{
					beginTitle();
					return;
				}
				executionState.LoadPrevState();
				return;
			}
			else if (((int)executionState.systemResult / 20) != page && executionState.systemResult != AutoSaveIndex && executionState.systemResult >= 0 && executionState.systemResult < dataIsAvailable.Length - 1)
			{
				page = (int)executionState.systemResult / 20;
				if (state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
					state.SystemState = SystemStateCode.LoadGameOpenning_Begin;
				else
					state.SystemState = SystemStateCode.LoadGame_Begin;
				printSaveDataText();
				return;
			}
			bool available;
			if ((executionState.systemResult >= 0) && (executionState.systemResult < dataIsAvailable.Length - 1))
				available = dataIsAvailable[executionState.systemResult];
			else if (executionState.systemResult == AutoSaveIndex)
				available = dataIsAvailable[^1];
			else
			{
				console.deleteLine(1);
				console.PrintTemporaryLine(trerror.InvalidValue.Text);
				console.updatedGeneration = true;
				setWaitInput();
				return;
			}
			if (!available)
			{
				console.PrintSingleLine(executionState.systemResult.ToString());
				console.PrintError(trerror.NoData.Text);
				if (state.SystemState == SystemStateCode.LoadGameOpenning_WaitInput)
				{
					beginLoadGameOpening();
					return;
				}
				beginLoadGame();
				return;
			}

			GlobalStatic.ctrlZ.OnLoad((int)executionState.systemResult);

			if (!vEvaluator.LoadFrom((int)executionState.systemResult))
				throw new ExeEE(trerror.UnexpectedErrorInLoaddata.Text);
			executionState.DeletePrevState();
			beginDataLoaded();
		}


		void endNormal()
		{
			throw new CodeEE(trerror.UnexpectedScriptEnd.Text);
		}

		void endReloaderb()
		{
			executionState.LoadPrevState();
			console.ReloadErbFinished();
		}

		bool writeSavedataTextFrom(int saveIndex)
		{
			EraDataResult result = vEvaluator.CheckData(saveIndex, EraSaveFileType.Normal);
			console.Print(result.DataMes);
			console.NewLine();
			return result.State == EraDataState.OK;
		}

		bool writeSavedataTextFrom_Silent(int saveIndex)
		{
			EraDataResult result = vEvaluator.CheckData(saveIndex, EraSaveFileType.Normal);
			return result.State == EraDataState.OK;
		}

}
