using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameData.Function;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Script.Statements.Function;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 实跑回填票 30：`EXISTVAR` 签名增量（Skia/gEmuera 扩为 `(String, Int=0)`，`mode≠0` → 把字符串当表达式解析）。
///
/// 归属：**向后兼容并入 baseline**（非 dialect 键）——1 参调用逐位不变；键可见面不变，故面快照无 diff
/// （由 <see cref="DialectSurfaceSnapshotTests"/> 一并把关）。值级语义走 <see cref="LoaderTestHarness"/>。
/// </summary>
[Collection("LoaderTests")]
public sealed class ExistVarSignatureTests
{
	private static FunctionMethod Method(string name) => FunctionMethodCreator.GetMethodList()[name];

	private static List<AExpression> Args(params AExpression[] args) => [.. args];

	private static long ExistVar(LoaderTestHarness h, params AExpression[] args)
		=> Method("EXISTVAR").GetIntValue(h.Exm, Args(args));

	// ---------- 反射 ----------

	private static readonly FieldInfo ArgRulesField =
		typeof(FunctionMethod).GetField("argumentTypeArrayEx", BindingFlags.Instance | BindingFlags.NonPublic)!;

	[Fact]
	public void Reflection_matches_two_arg_optional_signature()
	{
		// 注册表静态构造读 ambient Config（ConfigData.Current）——必须先开 scope，否则类型初始化失败并污染整进程。
		using var scope = GlobalStatic.OpenScope(new ConfigData());

		FunctionMethod method = Method("EXISTVAR");

		Assert.Equal(typeof(long), method.ReturnType);
		Assert.True(method.CanRestructure);

		var lists = (Array)ArgRulesField.GetValue(method)!;
		Assert.Single(lists);
		object rule = lists.GetValue(0)!;
		Type ruleType = rule.GetType();
		var argTypes = (IEnumerable)ruleType.GetProperty("ArgTypes")!.GetValue(rule)!;
		List<int> types = [];
		foreach (object argType in argTypes)
			types.Add(Convert.ToInt32(argType.GetType().GetField("type")!.GetValue(argType)));
		Assert.Equal(new[] { 4, 2 }, types); // String=4, Int=2
		Assert.Equal(1, (int)ruleType.GetProperty("OmitStart")!.GetValue(rule)!);
	}

	// ---------- mode == 0：沿用位掩码，1 参调用语义不变 ----------

	[Fact]
	public async Task Default_mode_keeps_bitmask_semantics()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		// 整数变量 → 置 Int 位(1)；字符串变量 → 置 Str 位(2)。
		Assert.Equal(1, ExistVar(h, new SingleStrTerm("FLAG")) & 1);
		Assert.Equal(2, ExistVar(h, new SingleStrTerm("SAVESTR")) & 2);
		// 未知名 → 0。
		Assert.Equal(0, ExistVar(h, new SingleStrTerm("NO_SUCH_VAR_XYZ")));
		// 显式 mode=0 与缺省一致。
		Assert.Equal(ExistVar(h, new SingleStrTerm("FLAG")), ExistVar(h, new SingleStrTerm("FLAG"), new SingleLongTerm(0)));
	}

	// ---------- mode != 0：表达式解析校验 ----------

	[Fact]
	public async Task Nonzero_mode_validates_expression_syntax()
	{
		using var h = new LoaderTestHarness();
		await h.InitializeAsync();

		// 已有变量引用 → 1。
		Assert.Equal(1, ExistVar(h, new SingleStrTerm("FLAG"), new SingleLongTerm(1)));
		// 合法表达式 → 1。
		Assert.Equal(1, ExistVar(h, new SingleStrTerm("1+2"), new SingleLongTerm(1)));
		// 未定义标识符 → 0（eraTW 正是靠此判断运行时拼出的变量名是否存在）。
		Assert.Equal(0, ExistVar(h, new SingleStrTerm("NO_SUCH_VAR_XYZ"), new SingleLongTerm(1)));
		// 非法语法 → 0。
		Assert.Equal(0, ExistVar(h, new SingleStrTerm("@@@"), new SingleLongTerm(1)));
		// 空串 → 0。
		Assert.Equal(0, ExistVar(h, new SingleStrTerm(""), new SingleLongTerm(1)));
	}
}
