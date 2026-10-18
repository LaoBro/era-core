using System.Threading.Tasks;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Loader;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 候选 4 · 第 2 刀遗留项：CallStack 的<b>事件函数</b>（<c>IsEvent</c>）返回分支。
///
/// <see cref="CallStackTests"/> 覆盖的是非事件路径 —— 因为 <c>CalledFunction.IsEvent</c> 是
/// <c>{ get; private set; }</c>，只能经 <c>CallEventFunction(LabelDictionary, …)</c> 构造，
/// 故本类改用真实标签字典（<c>LoaderTestHarness</c> 加载 ERB 后 SortLabels 的结果）。
///
/// 事件分组（<c>LabelDictionary.SortLabels</c>）：<c>eventLabels[0]=#ONLY</c>、
/// <c>[1]=#PRI</c>、<c>[2]=普通</c>、<c>[3]=#LATER</c>；<c>ShiftNext</c> 在组内推进 counter、
/// 组耗尽则进下一组、越过第 4 组即事件链结束（<c>CurrentLabel = null</c>）。
///
/// 三条分支的驱动点都落在 <c>FunctionLabelLine</c> 的可写属性上
/// （<c>IsOnly</c> / <c>IsSingle</c>），因此无需 mock，改标签状态即可直达分支。
/// </summary>
[Collection("LoaderTests")]
public sealed class CallStackEventTests
{
	private const string OneEvent = """
@EVENTFIRST
RETURN
""";

	private const string TwoSameEvents = """
@EVENTFIRST
RETURN

@EVENTFIRST
RETURN
""";

	[Fact]
	public async Task Event_return_empties_the_stack_when_the_group_chain_is_exhausted()
	{
		using var harness = new LoaderTestHarness();
		var stack = new CallStack(ProcessTrace.Disabled);
		var called = await NewEventCallAsync(harness, "EVENTONE.ERB", OneEvent);

		Assert.True(called.IsEvent);

		stack.IntoFunction(called, null!, null!);
		Assert.False(stack.ScriptEnd);

		bool emptied = stack.Return(0);

		// 组内没有下一个同名函数 → 越过第 4 组 → 事件链结束 → 栈清空
		Assert.True(emptied);
		Assert.True(stack.ScriptEnd);
	}

	[Theory]
	[InlineData(1, true)]  // #SINGLE 且 RETURN 1 → ShiftNextGroup：跳过本组剩余
	[InlineData(0, false)] // 否则 → ShiftNext：走到本组下一个同名函数
	public async Task Single_flag_and_return_value_decide_whether_the_group_is_skipped(long ret, bool expectEmptied)
	{
		using var harness = new LoaderTestHarness();
		var stack = new CallStack(ProcessTrace.Disabled);
		var called = await NewEventCallAsync(harness, "EVENTDUP.ERB", TwoSameEvents);

		var first = called.CurrentLabel;
		first.IsSingle = true; // #SINGLE：HasSingleFlag 读的就是当前标签的这个可写属性

		stack.IntoFunction(called, null!, null!);

		bool emptied = stack.Return(ret);

		Assert.Equal(expectEmptied, emptied);
		if (!expectEmptied)
			Assert.NotSame(first, called.CurrentLabel); // 推进到了同组的下一个
	}

	[Fact]
	public async Task Only_flag_finishes_the_event_regardless_of_the_return_value()
	{
		using var harness = new LoaderTestHarness();
		var stack = new CallStack(ProcessTrace.Disabled);
		// 用双同名事件：同组还剩一个 —— 若没有 #ONLY 语义，Return 会走到它而不是结束事件，
		// 这条断言才有区分力（单事件时两条路径都以栈清空收场，测不出差别）。
		var called = await NewEventCallAsync(harness, "EVENTONLY.ERB", TwoSameEvents);

		called.CurrentLabel.IsOnly = true; // #ONLY：直接结束整个事件，不看 RETURN 值

		stack.IntoFunction(called, null!, null!);

		bool emptied = stack.Return(1); // 即便 ret == 1 也不走 #SINGLE 的跳组语义

		Assert.True(emptied);
		Assert.True(stack.ScriptEnd);
	}

	// ---------- fixture ----------

	/// <summary>
	/// harness 必须由测试方法自己创建并 using：GlobalStatic scope 走 AsyncLocal，
	/// 在被调 async 方法里设置的值不会向上传播回调用者（见 ADR-0052 踩坑记录）。
	/// </summary>
	static async Task<CalledFunction> NewEventCallAsync(LoaderTestHarness harness, string erbName, string content)
	{
		await harness.InitializeAsync();
		ConfigData.Current!.GetConfigItem(ConfigCode.IgnoreUncalledFunction).SetValue(false);
		harness.WriteErb(erbName, content);
		await harness.PreloadAsync();
		var loader = new ErbLoader(harness.Console, harness.Exm, harness.IdDic, harness.Env,
			line => harness.Process.scaningLine = line);
		await loader.LoadErbDir(harness.ErbDir, displayReport: false, harness.LabelDic);

		// 返回地址传 null：事件链走完后 CurrentLine 即为 null，栈随之清空。
		var called = CalledFunction.CallEventFunction(harness.LabelDic, "EVENTFIRST", null!);
		Assert.NotNull(called);
		return called;
	}
}
