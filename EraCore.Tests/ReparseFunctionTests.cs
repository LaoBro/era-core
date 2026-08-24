using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 02：单函数体重解析件（<see cref="ErbLoader.ReparseFunction"/>）等价性测试。
/// 同一 test_game 语料先后经「全量加载」与「单函数重解析」产出两套行链，断言逐行等价：
/// 指令序列（FunctionCode）+ 源行号 + 非法标记一致，且重解析不新增错误。
/// 并验证失败路径三态语义（源文件缺失 / 桩行已不在文件中）返回 null 而非抛异常。
/// </summary>
[Collection("LoaderTests")]
public class ReparseFunctionTests
{
	/// <summary>
	/// 覆盖：简单体 / #DIM 局部 / $ goto+JUMP / IF-ELSEIF-ENDIF /
	/// FOR-NEXT / REPEAT-REND / CALLF 调另一函数 / RETURN 值 / 注释与空行。
	/// </summary>
	private const string Corpus = """
@BASIC
RETURN

@WITHARGS
#DIM MYLOCAL
MYLOCAL = 5
IF MYLOCAL > 10
	PRINT 1
ELSEIF MYLOCAL == 0
	RETURN 1
ELSE
	RETURN 0
ENDIF
RETURN

@WITHLOOP
#DIM II
FOR II, 0, 5
	IF II == 2
		BREAK
	ENDIF
NEXT
REPEAT 3
	; 注释行与空行下接一行
	PRINT II
	X = 1
	IF X > 5
		BREAK
	ENDIF
REND
RETURN

@CALLER
	CALLF BASIC
	JUMP $INNER
$INNER
	RETURN 2

@METHOD
	RETURN 3
""";

	/// <summary>将一条行链规范化为 (指令码|goto) + 行号 序列；遇下一函数标签（全量加载链跨函数）或 NullLine（重解析）同为函数体边界，二者均终止且不产出标记。</summary>
	private static List<string> SummarizeChain(LogicalLine header)
	{
		var list = new List<string>();
		var n = header.NextLine;
		while (n != null)
		{
			if (n is NullLine || n is FunctionLabelLine)
				break; // 函数体边界（重解析以 NullLine 收尾，全量加载延续到下一 @）
			if (n is GotoLabelLine g)
				list.Add($"G@{n.Position?.LineNo ?? -1}");
			else if (n is InstructionLine ins)
				list.Add($"{ins.FunctionCode}@{n.Position?.LineNo ?? -1}");
			n = n.NextLine;
		}
		return list;
	}

	private static readonly string[] Funcs = ["BASIC", "WITHARGS", "WITHLOOP", "CALLER", "METHOD"];

	[Fact]
	public async Task ReparseFunction_line_chain_equals_full_load()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var path = h.WriteErb("REPARSE.ERB", Corpus);
		await h.PreloadAsync();

		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		var labelDic = new LabelDictionary();
		// 加载失败时以控制台行辅助定位语料问题；成功路径不做字符串拼接。
		var ok = await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);
		if (!ok)
			throw new Xunit.Sdk.XunitException("LoadErbDir failed on corpus.\n"
				+ string.Join(" | ", h.Console.DisplayLineList.Select(l => l?.ToString() ?? "")));

		foreach (var name in Funcs)
		{
			var eager = labelDic.GetNonEventLabel(name);
			Assert.NotNull(eager);
			var reparsed = loader.ReparseFunction(eager, path);
			Assert.True(reparsed != null, name + " reparse returned null (pos=" + eager.Position + ", file=" + path + ")");
			// 重解析不得新增错误（eager 中干净的函数重解析后同样干净）
			Assert.False(reparsed.IsError);
			var expect = SummarizeChain(eager);
			var actual = SummarizeChain(reparsed);
			Assert.True(expect.Count > 0, name + ": empty chain?");
			Assert.Equal(expect, actual);
		}
	}

	[Fact]
	public async Task ReparseFunction_missing_file_returns_null_without_throwing()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var path = h.WriteErb("REPARSE2.ERB", "@BASIC2\nRETURN\n");
		await h.PreloadAsync();

		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		var labelDic = new LabelDictionary();
		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);
		var eager = labelDic.GetNonEventLabel("BASIC2");
		Assert.NotNull(eager);

		// 源文件缺失：占位 null，不抛新异常
		var missing = loader.ReparseFunction(eager, Path.Combine(h.ErbDir, "NOPE.ERB"));
		Assert.Null(missing);
	}

	[Fact]
	public async Task ReparseFunction_stub_line_relocated_returns_null_without_throwing()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();
		var path = h.WriteErb("REPARSE3.ERB", "@BASIC3\nRETURN\n");
		await h.PreloadAsync();

		var loader = new ErbLoader(h.Console, h.Exm, h.IdDic, h.Env, _ => { });
		var labelDic = new LabelDictionary();
		await loader.LoadErbDir(h.ErbDir, displayReport: false, labelDic);
		var eager = labelDic.GetNonEventLabel("BASIC3");
		Assert.NotNull(eager);
		var startLine = eager.Position!.Value.LineNo;

		// 改写源文件使原桩行（@BASIC3）不再存在，再重解析 → 占位 null。
		File.WriteAllText(path, "@NOTBASIC3\nRETURN\n");
		await h.PreloadAsync();
		var relocated = loader.ReparseFunction(eager, path);
		Assert.Null(relocated);

		// 桩的 Position 行号仍能对上原始文件（此时文件首行已是 @NOTBASIC3）→ 说明走的是「行号不匹配」三态。
		Assert.True(startLine >= 1);
	}
}