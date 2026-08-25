using MinorShift.Emuera.GameData.Variable;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Utils.PluginSystem
{
	//Wrapper for easier character data access
	public class PluginAPICharContext
	{
		internal static void CacheVariables(ExpressionMediator exm)
		{
			_BASE = exm.VEvaluator.VariableData.GetVarTokenDic()["BASE"];
			_MAXBASE = exm.VEvaluator.VariableData.GetVarTokenDic()["MAXBASE"];
			_ABL = exm.VEvaluator.VariableData.GetVarTokenDic()["ABL"];
			_TALENT = exm.VEvaluator.VariableData.GetVarTokenDic()["TALENT"];
			_EXP = exm.VEvaluator.VariableData.GetVarTokenDic()["EXP"];
			_MARK = exm.VEvaluator.VariableData.GetVarTokenDic()["MARK"];
			_PALAM = exm.VEvaluator.VariableData.GetVarTokenDic()["PALAM"];
			_SOURCE = exm.VEvaluator.VariableData.GetVarTokenDic()["SOURCE"];
			_EX = exm.VEvaluator.VariableData.GetVarTokenDic()["EX"];
			_CFLAG = exm.VEvaluator.VariableData.GetVarTokenDic()["CFLAG"];
			_JUEL = exm.VEvaluator.VariableData.GetVarTokenDic()["JUEL"];
			_RELATION = exm.VEvaluator.VariableData.GetVarTokenDic()["RELATION"];
			_EQUIP = exm.VEvaluator.VariableData.GetVarTokenDic()["EQUIP"];
			_TEQUIP = exm.VEvaluator.VariableData.GetVarTokenDic()["TEQUIP"];
			_STAIN = exm.VEvaluator.VariableData.GetVarTokenDic()["STAIN"];
			_GOTJUEL = exm.VEvaluator.VariableData.GetVarTokenDic()["GOTJUEL"];
			_NOWEX = exm.VEvaluator.VariableData.GetVarTokenDic()["NOWEX"];
			_DOWNBASE = exm.VEvaluator.VariableData.GetVarTokenDic()["DOWNBASE"];
			_CUP = exm.VEvaluator.VariableData.GetVarTokenDic()["CUP"];
			_CDOWN = exm.VEvaluator.VariableData.GetVarTokenDic()["CDOWN"];
			_TCVAR = exm.VEvaluator.VariableData.GetVarTokenDic()["TCVAR"];
			_NAME = exm.VEvaluator.VariableData.GetVarTokenDic()["NAME"];
			_CALLNAME = exm.VEvaluator.VariableData.GetVarTokenDic()["CALLNAME"];
			_NICKNAME = exm.VEvaluator.VariableData.GetVarTokenDic()["NICKNAME"];
			_MASTERNAME = exm.VEvaluator.VariableData.GetVarTokenDic()["MASTERNAME"];
			_CSTR = exm.VEvaluator.VariableData.GetVarTokenDic()["CSTR"];
			_CDFLAG = exm.VEvaluator.VariableData.GetVarTokenDic()["CDFLAG"];

			_UserDefinedVars.Clear();
			var userCharaVars = exm.VEvaluator.VariableData.UserDefinedCharaVarList;
			foreach (var userCharVar in userCharaVars)
			{
				var key = userCharVar.Name;
				_UserDefinedVars.Add(key, userCharVar);
			}

			PluginAPICharContext.exm = exm;
		}

		/// <summary>
		/// mem-leak-fix：清空静态缓存。游戏会话结束（GlobalStatic.Scope.Dispose）时调用，
		/// 释放对 VariableData token / ExpressionMediator / 用户自定义变量的静态持有，
		/// 配合 <see cref="PluginManager.ResetInstance"/> 让游戏对象图可被 GC 回收。
		/// 下次游戏加载时 <see cref="CacheVariables"/> 会重新填充。
		/// </summary>
		internal static void ClearStaticCache()
		{
			exm = null!;
			_BASE = null!; _MAXBASE = null!; _ABL = null!; _TALENT = null!;
			_EXP = null!; _MARK = null!; _PALAM = null!; _SOURCE = null!;
			_EX = null!; _CFLAG = null!; _JUEL = null!; _RELATION = null!;
			_EQUIP = null!; _TEQUIP = null!; _STAIN = null!; _GOTJUEL = null!;
			_NOWEX = null!; _DOWNBASE = null!; _CUP = null!; _CDOWN = null!;
			_TCVAR = null!; _NAME = null!; _CALLNAME = null!; _NICKNAME = null!;
			_MASTERNAME = null!; _CSTR = null!; _CDFLAG = null!;
			_UserDefinedVars.Clear();
		}

		//Cached Int[] variables
		static VariableToken _BASE = null!;
		static VariableToken _MAXBASE = null!;
		static VariableToken _ABL = null!;
		static VariableToken _TALENT = null!;
		static VariableToken _EXP = null!;
		static VariableToken _MARK = null!;
		static VariableToken _PALAM = null!;
		static VariableToken _SOURCE = null!;
		static VariableToken _EX = null!;
		static VariableToken _CFLAG = null!;
		static VariableToken _JUEL = null!;
		static VariableToken _RELATION = null!;
		static VariableToken _EQUIP = null!;
		static VariableToken _TEQUIP = null!;
		static VariableToken _STAIN = null!;
		static VariableToken _GOTJUEL = null!;
		static VariableToken _NOWEX = null!;
		static VariableToken _DOWNBASE = null!;
		static VariableToken _CUP = null!;
		static VariableToken _CDOWN = null!;
		static VariableToken _TCVAR = null!;

		//Cached string variables
		static VariableToken _NAME = null!;
		static VariableToken _CALLNAME = null!;
		static VariableToken _NICKNAME = null!;
		static VariableToken _MASTERNAME = null!;

		//Cached string[] variables
		static VariableToken _CSTR = null!;

		//Cached int[][] variables
		static VariableToken _CDFLAG = null!;

		static Dictionary<string, VariableToken> _UserDefinedVars = [];

		static ExpressionMediator exm = null!;
		internal PluginAPICharContext(long charId)
		{
			this.charId = charId;
			BASE = new CharInt1dWrapper(charId, _BASE, exm, VariableCode.BASE);
			MAXBASE = new CharInt1dWrapper(charId, _MAXBASE, exm, VariableCode.MAXBASE);
			ABL = new CharInt1dWrapper(charId, _ABL, exm, VariableCode.ABL);
			TALENT = new CharInt1dWrapper(charId, _TALENT, exm, VariableCode.TALENT);
			EXP = new CharInt1dWrapper(charId, _EXP, exm, VariableCode.EXP);
			MARK = new CharInt1dWrapper(charId, _MARK, exm, VariableCode.MARK);
			PALAM = new CharInt1dWrapper(charId, _PALAM, exm, VariableCode.PALAM);
			SOURCE = new CharInt1dWrapper(charId, _SOURCE, exm, VariableCode.SOURCE);
			EX = new CharInt1dWrapper(charId, _EX, exm, VariableCode.EX);
			CFLAG = new CharInt1dWrapper(charId, _CFLAG, exm, VariableCode.CFLAG);
			JUEL = new CharInt1dWrapper(charId, _JUEL, exm, VariableCode.JUEL);
			RELATION = new CharInt1dWrapper(charId, _RELATION, exm, VariableCode.RELATION);
			EQUIP = new CharInt1dWrapper(charId, _EQUIP, exm, VariableCode.EQUIP);
			TEQUIP = new CharInt1dWrapper(charId, _TEQUIP, exm, VariableCode.TEQUIP);
			STAIN = new CharInt1dWrapper(charId, _STAIN, exm, VariableCode.STAIN);
			GOTJUEL = new CharInt1dWrapper(charId, _GOTJUEL, exm, VariableCode.GOTJUEL);
			NOWEX = new CharInt1dWrapper(charId, _NOWEX, exm, VariableCode.NOWEX);
			DOWNBASE = new CharInt1dWrapper(charId, _DOWNBASE, exm, VariableCode.DOWNBASE);
			CUP = new CharInt1dWrapper(charId, _CUP, exm, VariableCode.CUP);
			CDOWN = new CharInt1dWrapper(charId, _CDOWN, exm, VariableCode.CDOWN);
			TCVAR = new CharInt1dWrapper(charId, _TCVAR, exm, VariableCode.TCVAR);

			NAME = new CharStringWrapper(charId, _NAME, exm, VariableCode.NAME);
			CALLNAME = new CharStringWrapper(charId, _CALLNAME, exm, VariableCode.CALLNAME);
			NICKNAME = new CharStringWrapper(charId, _NICKNAME, exm, VariableCode.NICKNAME);
			MASTERNAME = new CharStringWrapper(charId, _MASTERNAME, exm, VariableCode.MASTERNAME);

			CSTR = new CharString1dWrapper(charId, _CSTR, exm, VariableCode.CSTR);
			CDFLAG = new CharInt2dWrapper(charId, _CDFLAG, exm, VariableCode.CDFLAG);

			UserDefined = new CharUserdefinedInt1dWrapper(charId, _UserDefinedVars, exm, VariableCode.VAR);
		}

		public CharUserdefinedInt1dWrapper UserDefined;

		public CharInt1dWrapper BASE;
		public CharInt1dWrapper MAXBASE;
		public CharInt1dWrapper ABL;
		public CharInt1dWrapper TALENT;
		public CharInt1dWrapper EXP;
		public CharInt1dWrapper MARK;
		public CharInt1dWrapper PALAM;
		public CharInt1dWrapper SOURCE;
		public CharInt1dWrapper EX;
		public CharInt1dWrapper CFLAG;
		public CharInt1dWrapper JUEL;
		public CharInt1dWrapper RELATION;
		public CharInt1dWrapper EQUIP;
		public CharInt1dWrapper TEQUIP;
		public CharInt1dWrapper STAIN;
		public CharInt1dWrapper GOTJUEL;
		public CharInt1dWrapper NOWEX;
		public CharInt1dWrapper DOWNBASE;
		public CharInt1dWrapper CUP;
		public CharInt1dWrapper CDOWN;
		public CharInt1dWrapper TCVAR;

		public CharStringWrapper NAME;
		public CharStringWrapper CALLNAME;
		public CharStringWrapper NICKNAME;
		public CharStringWrapper MASTERNAME;

		public CharString1dWrapper CSTR;

		public CharInt2dWrapper CDFLAG;

		long charId;
	}
}
