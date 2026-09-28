using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameProc.Function;
using MinorShift.Emuera.Compat;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using static MinorShift.Emuera.Runtime.Utils.EvilMask.Utils;

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem
{
	public class PluginManager
	{

		public static PluginManager GetInstance()
		{
			if (instance == null)
			{
				instance = new PluginManager();
			}

			return instance;
		}

		private PluginManager()
		{

		}

		static private PluginManager instance = null!;

		/// <summary>
		/// Unsafe rudimentary method to execute ERB line of code from Plugin.
		/// Instead of using this method better implement direct API call that works directly with game state, bypassing ERB interpreter
		/// </summary>
		/// <param name="line">Line of code to execute</param>
		public void ExecuteLine(string line)
		{
			var logicalLine = LogicalLineParser.ParseLine(line, null!);
			InstructionLine func = (InstructionLine)logicalLine;
			ArgumentParser.SetArgumentTo(func);
			func.Function.Instruction.DoInstruction(expressionMediator, func, processState);
		}

		public void Print(string text)
		{
			expressionMediator.Console.Print(text);
		}
		public void PrintError(string text)
		{
			expressionMediator.Console.PrintError(text);
		}
		public void PrintC(string text, bool aligmentRight = false)
		{
			expressionMediator.Console.PrintC(text, aligmentRight);
		}
		public void PrintPlain(string text)
		{
			expressionMediator.Console.PrintPlain(text);
		}
		public void PrintPlainWithSingleLine(string text)
		{
			expressionMediator.Console.PrintPlainWithSingleLineFix(text);
		}
		public void PrintSingleLine(string text)
		{
			expressionMediator.Console.PrintSingleLine(text);
		}
		public void PrintSystemLine(string text)
		{
			expressionMediator.Console.PrintSystemLine(text);
		}
		public void PrintTemporaryLine(string text)
		{
			expressionMediator.Console.PrintTemporaryLine(text);
		}
		public void PrintButton(string text, long id)
		{
			expressionMediator.Console.PrintButton(text, id);
		}
		public void PrintButtonC(string text, long id, bool aligmentRight = false)
		{
			expressionMediator.Console.PrintButtonC(text, id, aligmentRight);
		}
		public void PrintBar(string text = "", bool isConst = true)
		{
			if (text == "")
			{
				expressionMediator.Console.PrintBar();
			}
			else
			{
				expressionMediator.Console.printCustomBar(text, isConst);
			}
		}
		public void PrintHtml(string htmlText, bool toBuffer = false)
		{
			expressionMediator.Console.PrintHtml(htmlText, toBuffer);
		}
		public void PrintImage(string resourceName, int width, int height, int y, string buttonResourceName = null!, string mapResourceName = null!)
		{
			MixedNum widthNum = new()
			{
				isPx = true,
				num = width
			};
			MixedNum heightNum = new()
			{
				isPx = true,
				num = height
			};
			MixedNum yNum = new()
			{
				isPx = true,
				num = y
			};
			expressionMediator.Console.PrintImg(resourceName, buttonResourceName, mapResourceName, heightNum, widthNum, yNum);
		}
		public void PrintNewLine()
		{
			expressionMediator.Console.NewLine();
		}
		public void FlushConsole(bool force = false)
		{
			expressionMediator.Console.PrintFlush(force);
		}
		public void DebugPrint(string text)
		{
			expressionMediator.Console.DebugPrint(text);
		}

		public void ClearDisplay()
		{
			expressionMediator.Console.ClearDisplay();
		}
		public void SetBgColor(EmuColor color)
		{
			expressionMediator.Console.SetBgColor(color);
		}
		public void SetFont(string fontName)
		{
			expressionMediator.Console.SetFont(fontName);
		}
		public EmuPoint GetMousePosition()
		{
			return expressionMediator.Console.GetMousePosition();
		}
		public void WaitInput(bool oneInput = true, int timelimit = -1)
		{
			InputRequest request = new()
			{
				OneInput = oneInput,
				Timelimit = timelimit
			};
			expressionMediator.Console.WaitInput(request);
		}
		public void ReadAnyKey()
		{
			expressionMediator.Console.ReadAnyKey();

		}
		public void Await(int time)
		{
			expressionMediator.Console.Await(time);
		}
		public void ForceStopTimer()
		{
			expressionMediator.Console.forceStopTimer();
		}
		public void Quit(bool force = false)
		{
			if (force)
			{
				expressionMediator.Console.ForceQuit();
			}
			else
			{
				expressionMediator.Console.Quit();
			}
		}

		public long[] GetCharacterIDs()
		{
			return expressionMediator.VEvaluator.VariableData.CharacterList.Select(v => v.NO).ToArray();
		}
		public long GetIntVar(string name, int index = 0)
		{
			return expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name].GetIntValue(expressionMediator, [index]);
		}
		public string GetStrVar(string name, int index = 0)
		{
			return expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name].GetStrValue(expressionMediator, [index]);
		}
		public void SetIntVar(string name, long val, int index = 0)
		{
			expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name].SetValue(val, [index]);
		}
		public void SetStrVar(string name, string val, int index = 0)
		{
			expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name].SetValue(val, [index]);
		}

		public void SetCharVar(string name, long charId, string key, long value)
		{
			var variable = expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name];
			var errPos = "";
			var dict = expressionMediator.VEvaluator.Constant.GetKeywordDictionary(out errPos, VariableCode.CFLAG, 1, key);
			variable.SetValue(value, [charId, dict[key]]);
		}
		public void SetCharVar(string name, long charId, long key, long value)
		{
			var variable = expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name];
			variable.SetValue(value, [charId, key]);
		}
		public long GetCharVar(string name, long charId, long key)
		{
			var variable = expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name];
			return variable.GetIntValue(expressionMediator, [charId, key]);
		}
		public long GetCharVar(string name, long charId, string key)
		{
			var variable = expressionMediator.VEvaluator.VariableData.GetVarTokenDic()[name];
			var errPos = "";
			var dict = expressionMediator.VEvaluator.Constant.GetKeywordDictionary(out errPos, VariableCode.CFLAG, 1, key);
			return variable.GetIntValue(expressionMediator, [charId, dict[key]]);
		}

		public System.Data.DataTable GetDataTable(string name)
		{
			return expressionMediator.VEvaluator.VariableData.DataDataTables[name];
		}

		public static PluginAPICharContext CreateCharContext(long charId)
		{
			return new PluginAPICharContext(charId);
		}


		/// <summary>
		/// Returns call stacktrace from emuera/ERB side
		/// </summary>
		public IEnumerable<String> GetStackTrace(){
			LogicalLine parent;
			List<String> stackTraceCollecter = [];
			int depth = 0;

			// add initial caller
			stackTraceCollecter.Add(this.processState.CurrentLine.Position!.Value.Filename + ":" + this.processState.CurrentLine.Position!.Value.LineNo.ToString() + "@" +  this.processState.CurrentLine.ParentLabelLine.LabelName);
			// loop call stack
			while ((parent = this.processState.GetReturnAddressSequensial(depth++)) != null)
			{
				if (parent.Position != null)
				{
					stackTraceCollecter.Add(parent.Position.Value.Filename + ":" + parent.Position.Value.LineNo.ToString() + "@" + parent.ParentLabelLine.LabelName);
				}
			}
			return stackTraceCollecter;
		}

		/// <summary>
		/// Load all DLL plugins from Plugins directory of the game
		/// </summary>
		// NativeAOT 豁免（已登记：nativeaot-verify-report.md §5.2）：插件加载依赖
		// Assembly.LoadFrom/GetTypes/Activator.CreateInstance（IL2026/IL2072）。
		// 仅「真实路径 + 支持动态代码」的形态走本方法；纯 AOT（含 NativeAOT）与 SAF content-URI
		// 由 LegacyPluginCompat.CanLoadPluginsHere 拦截并抛 ExeEE（见该类型注释）。
		[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "插件加载仅真实路径 + JIT 形态；纯 AOT 由 CanLoadPluginsHere 拦截")]
		[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "插件加载仅真实路径 + JIT 形态；纯 AOT 由 CanLoadPluginsHere 拦截")]
		public void LoadPlugins()
		{
			var accessor = GamePaths.Current.DirAccessor;
			// 大小写不敏感定位插件目录：eraTW 用小写 plugins/，Android/Linux 真实路径大小写敏感；
			// 目录枚举统一走 accessor（与 SAF 语义一致，对齐 CLAUDE.md 通配约定）。
			var pluginDir = ResolvePluginsDir(accessor);

			if (!LegacyPluginCompat.CanLoadPluginsHere())
			{
				if (!accessor.DirectoryExists(pluginDir))
					return;
				if (accessor.GetFiles(pluginDir, "*.dll", SearchOption.TopDirectoryOnly).Length > 0)
					throw new ExeEE(LegacyPluginCompat.UnsupportedReason);
				return;
			}

			if (!accessor.DirectoryExists(pluginDir))
			{
				//フォルダを作らないようにする
				return;
				//Directory.CreateDirectory("Plugins");
			}

			string[] plugins = accessor.GetFiles(pluginDir, "*.dll", SearchOption.TopDirectoryOnly);
			bool pluginsAware = File.Exists(Program.ExeDir + "pluginsAware.txt");
			// legacy-plugin-compat：解析插件自带的兄弟依赖（.NET Core LoadFrom 不探测自身目录）。
			LegacyPluginCompat.Prepare(pluginDir);
			ClearMethods();
			foreach (var pluginPath in plugins)
			{
				Assembly DLL = Assembly.LoadFrom(pluginPath);
				var manifestType = DLL.GetTypes().Where((v) => v.Name == "PluginManifest").FirstOrDefault();
				if (manifestType == null)
				{
					//TODO: throw warning
					continue;
				}
				if (LegacyPluginCompat.IsAwareGateEnforced && !pluginsAware)
				{
					throw new ExeEE("This game comes prepackaged with plugins. This is a security check to make sure you're aware of that: Never run your EE under Administrator rights, Always get your games and Plugins from verified sources and If you're maintainer of the build and it should NOT come with plugins, Investigate immediately. If everything is okay, create file pluginsAware.txt at the root of the game distributive and restart");
				}

#pragma warning disable CS8600
				PluginManifestAbstract? manifest = (PluginManifestAbstract)Activator.CreateInstance(manifestType);
#pragma warning restore CS8600
				if (manifest == null)
				{
					//TODO: throw warning
					continue;
				}

				var methods = manifest.GetPluginMethods();
				foreach (var method in methods)
				{
					AddMethod(method);
				}
			}
		}

		/// <summary>
		/// 大小写不敏感解析插件目录，返回磁盘真实路径。优先枚举 ExeDir 子目录按 "Plugins" 通配匹配
		/// （托管通配大小写不敏感，Windows/Android 真实路径语义一致）；枚举不可用/未命中时回退按名拼接。
		/// </summary>
		private static string ResolvePluginsDir(IGameDirAccessor accessor)
		{
			try
			{
				var dirs = accessor.GetDirectories(Program.ExeDir, "Plugins", SearchOption.TopDirectoryOnly);
				if (dirs.Length > 0)
					return dirs[0];
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
			{
				// 目录不可达/不可枚举：回退按名解析（随后由 DirectoryExists 决定去留）
			}
			return SafCompat.ResolveSubPath(Program.ExeDir, "Plugins");
		}

		public IPluginMethod GetMethod(string name)
		{
			var key = name;
			if (Config.Config.IgnoreCase)
			{
				key = key.ToUpper();
			}
			return methods[key];
		}

		public bool HasMethod(string name)
		{
			var key = name;
			if (Config.Config.IgnoreCase)
			{
				key = key.ToUpper();
			}
			return methods.ContainsKey(key);
		}

		internal void SetParent(Process process, ProcessState processState, ExpressionMediator expressionMediator)
		{
			this.process = process;
			this.processState = processState;
			this.expressionMediator = expressionMediator;
			PluginAPICharContext.CacheVariables(expressionMediator);
			CacheVariables();
		}

		internal void CacheVariables()
		{
			DAY = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["DAY"], expressionMediator, VariableCode.DAY);
			MONEY = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["MONEY"], expressionMediator, VariableCode.MONEY);
			ITEM = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["ITEM"], expressionMediator, VariableCode.ITEM);
			FLAG = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["FLAG"], expressionMediator, VariableCode.FLAG);
			TFLAG = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TFLAG"], expressionMediator, VariableCode.TFLAG);
			UP = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["UP"], expressionMediator, VariableCode.UP);
			PALAMLV = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["PALAMLV"], expressionMediator, VariableCode.PALAMLV);
			EXPLV = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["EXPLV"], expressionMediator, VariableCode.EXPLV);
			EJAC = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["EJAC"], expressionMediator, VariableCode.EJAC);
			DOWN = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["DOWN"], expressionMediator, VariableCode.DOWN);
			RESULT = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["RESULT"], expressionMediator, VariableCode.RESULT);
			COUNT = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["COUNT"], expressionMediator, VariableCode.COUNT);
			TARGET = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TARGET"], expressionMediator, VariableCode.TARGET);
			ASSI = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["ASSI"], expressionMediator, VariableCode.ASSI);
			MASTER = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["MASTER"], expressionMediator, VariableCode.MASTER);
			NOITEM = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["NOITEM"], expressionMediator, VariableCode.NOITEM);
			LOSEBASE = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["LOSEBASE"], expressionMediator, VariableCode.LOSEBASE);
			SELECTCOM = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["SELECTCOM"], expressionMediator, VariableCode.SELECTCOM);
			ASSIPLAY = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["ASSIPLAY"], expressionMediator, VariableCode.ASSIPLAY);
			PREVCOM = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["PREVCOM"], expressionMediator, VariableCode.PREVCOM);
			TIME = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TIME"], expressionMediator, VariableCode.TIME);
			ITEMSALES = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["ITEMSALES"], expressionMediator, VariableCode.ITEMSALES);
			PLAYER = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["PLAYER"], expressionMediator, VariableCode.PLAYER);
			NEXTCOM = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["NEXTCOM"], expressionMediator, VariableCode.NEXTCOM);
			PBAND = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["PBAND"], expressionMediator, VariableCode.PBAND);
			BOUGHT = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["BOUGHT"], expressionMediator, VariableCode.BOUGHT);

			GLOBAL = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["GLOBAL"], expressionMediator, VariableCode.GLOBAL);
			RANDDATA = new GlobalInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["RANDDATA"], expressionMediator, VariableCode.RANDDATA);

			SAVESTR = new GlobalString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["SAVESTR"], expressionMediator, VariableCode.SAVESTR);
			TSTR = new GlobalString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TSTR"], expressionMediator, VariableCode.TSTR);
			STR = new GlobalString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["STR"], expressionMediator, VariableCode.STR);
			RESULTS = new GlobalString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["RESULTS"], expressionMediator, VariableCode.RESULTS);
			GLOBALS = new GlobalString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["GLOBALS"], expressionMediator, VariableCode.GLOBALS);

			NO = new GlobalConstInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["NO"], expressionMediator, VariableCode.NO);
			ITEMPRICE = new GlobalConstInt1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["ITEMPRICE"], expressionMediator, VariableCode.ITEMPRICE);

			ABLNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["ABLNAME"], expressionMediator, VariableCode.ABLNAME);
			TALENTNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TALENTNAME"], expressionMediator, VariableCode.TALENTNAME);
			EXPNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["EXPNAME"], expressionMediator, VariableCode.EXPNAME);
			MARKNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["MARKNAME"], expressionMediator, VariableCode.MARKNAME);
			PALAMNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["PALAMNAME"], expressionMediator, VariableCode.PALAMNAME);
			ITEMNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["ITEMNAME"], expressionMediator, VariableCode.ITEMNAME);
			TRAINNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TRAINNAME"], expressionMediator, VariableCode.TRAINNAME);
			BASENAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["BASENAME"], expressionMediator, VariableCode.BASENAME);
			SOURCENAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["SOURCENAME"], expressionMediator, VariableCode.SOURCENAME);
			EXNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["EXNAME"], expressionMediator, VariableCode.EXNAME);
			EQUIPNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["EQUIPNAME"], expressionMediator, VariableCode.EQUIPNAME);
			TEQUIPNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TEQUIPNAME"], expressionMediator, VariableCode.TEQUIPNAME);
			FLAGNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["FLAGNAME"], expressionMediator, VariableCode.FLAGNAME);
			TFLAGNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TFLAGNAME"], expressionMediator, VariableCode.TFLAGNAME);
			CFLAGNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["CFLAGNAME"], expressionMediator, VariableCode.CFLAGNAME);
			TCVARNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TCVARNAME"], expressionMediator, VariableCode.TCVARNAME);
			CSTRNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["CSTRNAME"], expressionMediator, VariableCode.CSTRNAME);
			STAINNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["STAINNAME"], expressionMediator, VariableCode.STAINNAME);

			CDFLAGNAME1 = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["CDFLAGNAME1"], expressionMediator, VariableCode.CDFLAGNAME1);
			CDFLAGNAME2 = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["CDFLAGNAME2"], expressionMediator, VariableCode.CDFLAGNAME2);
			STRNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["STRNAME"], expressionMediator, VariableCode.STRNAME);
			TSTRNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["TSTRNAME"], expressionMediator, VariableCode.TSTRNAME);
			SAVESTRNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["SAVESTRNAME"], expressionMediator, VariableCode.SAVESTRNAME);
			GLOBALNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["GLOBALNAME"], expressionMediator, VariableCode.GLOBALNAME);
			GLOBALSNAME = new GlobalConstString1dWrapper(expressionMediator.VEvaluator.VariableData.GetVarTokenDic()["GLOBALSNAME"], expressionMediator, VariableCode.GLOBALSNAME);
		}

		private void ClearMethods()
		{
			methods.Clear();
		}

		private void AddMethod(IPluginMethod method)
		{
			var key = method.Name;
			if (Config.Config.IgnoreCase)
			{
				key = key.ToUpper();
			}
			methods.Add(key, method);
		}

		private Dictionary<string, IPluginMethod> methods = [];
		private Process process = null!;
		private ProcessState processState = null!;
		private ExpressionMediator expressionMediator = null!;

		public GlobalInt1dWrapper DAY = null!;
		public GlobalInt1dWrapper MONEY = null!;
		public GlobalInt1dWrapper ITEM = null!;
		public GlobalInt1dWrapper FLAG = null!;
		public GlobalInt1dWrapper TFLAG = null!;
		public GlobalInt1dWrapper UP = null!;
		public GlobalInt1dWrapper PALAMLV = null!;
		public GlobalInt1dWrapper EXPLV = null!;
		public GlobalInt1dWrapper EJAC = null!;
		public GlobalInt1dWrapper DOWN = null!;
		public GlobalInt1dWrapper RESULT = null!;
		public GlobalInt1dWrapper COUNT = null!;
		public GlobalInt1dWrapper TARGET = null!;
		public GlobalInt1dWrapper ASSI = null!;
		public GlobalInt1dWrapper MASTER = null!;
		public GlobalInt1dWrapper NOITEM = null!;
		public GlobalInt1dWrapper LOSEBASE = null!;
		public GlobalInt1dWrapper SELECTCOM = null!;
		public GlobalInt1dWrapper ASSIPLAY = null!;
		public GlobalInt1dWrapper PREVCOM = null!;
		public GlobalInt1dWrapper TIME = null!;
		public GlobalInt1dWrapper ITEMSALES = null!;
		public GlobalInt1dWrapper PLAYER = null!;
		public GlobalInt1dWrapper NEXTCOM = null!;
		public GlobalInt1dWrapper PBAND = null!;
		public GlobalInt1dWrapper BOUGHT = null!;

		public GlobalInt1dWrapper GLOBAL = null!;
		public GlobalInt1dWrapper RANDDATA = null!;

		public GlobalString1dWrapper SAVESTR = null!;
		public GlobalString1dWrapper TSTR = null!;
		public GlobalString1dWrapper STR = null!;
		public GlobalString1dWrapper RESULTS = null!;
		public GlobalString1dWrapper GLOBALS = null!;

		public GlobalConstInt1dWrapper NO = null!;
		public GlobalConstInt1dWrapper ITEMPRICE = null!;

		public GlobalConstString1dWrapper ABLNAME = null!;
		public GlobalConstString1dWrapper TALENTNAME = null!;
		public GlobalConstString1dWrapper EXPNAME = null!;
		public GlobalConstString1dWrapper MARKNAME = null!;
		public GlobalConstString1dWrapper PALAMNAME = null!;
		public GlobalConstString1dWrapper ITEMNAME = null!;
		public GlobalConstString1dWrapper TRAINNAME = null!;
		public GlobalConstString1dWrapper BASENAME = null!;
		public GlobalConstString1dWrapper SOURCENAME = null!;
		public GlobalConstString1dWrapper EXNAME = null!;
		public GlobalConstString1dWrapper EQUIPNAME = null!;
		public GlobalConstString1dWrapper TEQUIPNAME = null!;
		public GlobalConstString1dWrapper FLAGNAME = null!;
		public GlobalConstString1dWrapper TFLAGNAME = null!;
		public GlobalConstString1dWrapper CFLAGNAME = null!;
		public GlobalConstString1dWrapper TCVARNAME = null!;
		public GlobalConstString1dWrapper CSTRNAME = null!;
		public GlobalConstString1dWrapper STAINNAME = null!;

		public GlobalConstString1dWrapper CDFLAGNAME1 = null!;
		public GlobalConstString1dWrapper CDFLAGNAME2 = null!;
		public GlobalConstString1dWrapper STRNAME = null!;
		public GlobalConstString1dWrapper TSTRNAME = null!;
		public GlobalConstString1dWrapper SAVESTRNAME = null!;
		public GlobalConstString1dWrapper GLOBALNAME = null!;
		public GlobalConstString1dWrapper GLOBALSNAME = null!;

		/// <summary>
		/// 是否已加载到插件方法（<see cref="LoadPlugins"/> 之后判定）。
		/// mem-leak-fix：无插件游戏据此在启动时回收插件系统的运行时静态持有。
		/// </summary>
		internal bool HasPlugins => methods.Count > 0;

		/// <summary>
		/// mem-leak-fix：重置单例。释放单例对游戏运行时（Process / ProcessState /
		/// ExpressionMediator / VariableData wrapper）的静态持有，让游戏对象图可被 GC 回收。
		/// 调用时机：游戏会话结束（GlobalStatic.Scope.Dispose）；或无插件游戏启动时
		/// （Process.Initialize 检测 HasPlugins == false）。
		/// 下次游戏加载时 Process.Initialize 会经 <see cref="GetInstance"/> 重建单例并
		/// 重新 SetParent + LoadPlugins，无需手动恢复。
		/// </summary>
		internal static void ResetInstance()
		{
			instance = null!;
		}
	}
}
