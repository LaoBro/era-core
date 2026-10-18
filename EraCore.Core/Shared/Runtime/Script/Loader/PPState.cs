using MinorShift.Emuera.Runtime.Utils;
using System.Collections.Generic;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// 单文件生成的预处理状态机（<c>[SKIPSTART]/[IF]/[ELSEIF]/[ELSE]/[ENDIF]…</c>）。
/// <para>
/// <b>ADR-0034 决策四</b>：本类原为 <c>ErbLoader</c> 的嵌套私有类，被「单文件生成」（B 组，
/// 现已抽为 <see cref="ErbFileAssembler"/>）与「惰性物化」（E 组，仍留在 <c>ErbLoader</c>）
/// <b>双方消费</b>。若留在 ErbLoader 内，新模块引用它会形成双向依赖（ADR-0033 把
/// 「与宿主之间无双向依赖」列为设计属性），故外移为顶层 <c>internal</c> 共享件。
/// </para>
/// <para>
/// 自包含：经构造注入 <c>env</c> / <c>idDic</c>（ADR-0012）与警告目的地 <c>warnings</c>（ADR-0039，
/// 原走 <see cref="ParserMediator"/> 静态）。
/// 因与两处消费方同命名空间，外移后调用点无需改动。
/// </para>
/// </summary>
internal sealed class PPState
{
	// ADR-0012：原走 Program.DebugMode / GlobalStatic.IdentifierDictionary（static），
	// 现经构造注入从外层 ErbLoader 传入。
	readonly LoaderEnv env;
	readonly IdentifierDictionary idDic;
	// ADR-0039：警告目的地显式注入——装配期传文件产物列表，惰性物化传全局目标。
	readonly List<ParserMediator.ParserWarning> warnings;
	internal PPState(LoaderEnv env, IdentifierDictionary idDic, List<ParserMediator.ParserWarning> warnings)
	{
		this.env = env;
		this.idDic = idDic;
		this.warnings = warnings;
	}

	bool skip;
	bool done;
	public bool Disabled;
	readonly Stack<bool> disabledStack = new();
	readonly Stack<bool> doneStack = new();
	readonly Stack<string> ppMatch = new();

	internal void AddKeyWord(string token, string token2, ScriptPosition? position)
	{
		//bool token2enabled = string.IsNullOrEmpty(token2);
		switch (token)
		{
			case "SKIPSTART":
				if (!string.IsNullOrEmpty(token2))
				{
					warnings.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
					break;
				}
				if (skip)
				{
					warnings.Warn(trerror.DuplicateSkipstart.Text, position, 1);
					break;
				}
				ppMatch.Push("SKIPEND");
				disabledStack.Push(Disabled);
				doneStack.Push(done);
				skip = true;
				Disabled = true;
				done = false;
				break;
			case "IF_DEBUG":
				if (!string.IsNullOrEmpty(token2))
				{
					warnings.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
					break;
				}
				ppMatch.Push("ELSEIF");
				disabledStack.Push(Disabled);
				doneStack.Push(done);
				Disabled = !env.DebugMode;
				done = !Disabled;
				break;
			case "IF_NDEBUG":
				if (!string.IsNullOrEmpty(token2))
				{
					warnings.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
					break;
				}
				ppMatch.Push("ELSEIF");
				disabledStack.Push(Disabled);
				doneStack.Push(done);
				Disabled = env.DebugMode;
				done = !Disabled;
				break;
			case "IF":
				if (string.IsNullOrEmpty(token2))
				{
					warnings.Warn(string.Format(trerror.MissingArguments.Text, token), position, 1);
					break;
				}
				ppMatch.Push("ELSEIF");
				disabledStack.Push(Disabled);
				doneStack.Push(done);
				Disabled = idDic.GetMacro(token2) == null;
				done = !Disabled;
				break;
			case "ELSEIF":
				if (string.IsNullOrEmpty(token2))
				{
					warnings.Warn(string.Format(trerror.MissingArguments.Text, token), position, 1);
					break;
				}
				if (ppMatch.Count == 0 || ppMatch.Pop() != "ELSEIF")
				{
					warnings.Warn(string.Format(trerror.IsInvalid.Text, "[ELSEIF]"), position, 1);
					break;
				}
				ppMatch.Push("ELSEIF");
				Disabled = done || idDic.GetMacro(token2) == null;
				done |= !Disabled;
				break;
			case "ELSE":
				if (!string.IsNullOrEmpty(token2))
				{
					warnings.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
					break;
				}
				if (ppMatch.Count == 0 || ppMatch.Pop() != "ELSEIF")
				{
					warnings.Warn(string.Format(trerror.IsInvalid.Text, "[ELSE]"), position, 1);
					break;
				}
				ppMatch.Push("ENDIF");
				Disabled = done;
				done = true;
				break;

			case "SKIPEND":
				{
					if (!string.IsNullOrEmpty(token2))
					{
						warnings.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					string match = ppMatch.Count == 0 ? "" : ppMatch.Pop();
					if (match != "SKIPEND")
					{
						warnings.Warn(trerror.UnexpectedSkipend.Text, position, 1);
						break;
					}
					skip = false;
					Disabled = disabledStack.Pop();
					done = doneStack.Pop();
				}
				break;
			case "ENDIF":
				{
					if (!string.IsNullOrEmpty(token2))
					{
						warnings.Warn(string.Format(trerror.HasTooManyArg.Text, token), position, 1);
						break;
					}
					string match = ppMatch.Count == 0 ? "" : ppMatch.Pop();
					if (match != "ENDIF" && match != "ELSEIF")
					{
						warnings.Warn(trerror.UnexpectedMacroEndif.Text, position, 1);
						break;
					}
					Disabled = disabledStack.Pop();
					done = doneStack.Pop();
				}
				break;
			default:
				warnings.Warn(trerror.UnrecognizedPreprosessor.Text, position, 1);
				break;
		}
		if (skip)
			Disabled = true;
	}

	internal void FileEnd(ScriptPosition? position)
	{
		if (ppMatch.Count != 0)
		{
			string match = ppMatch.Pop();
			if (match == "ELSEIF")
				match = "ENDIF";
			warnings.Warn(string.Format(trerror.TheresNo.Text, match), position, 1);
		}
	}
}
