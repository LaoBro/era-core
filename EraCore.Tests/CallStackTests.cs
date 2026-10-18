using MinorShift.Emuera;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 候选 4 · 第 2 刀：CallStack 隔离单测。
/// 直接以 <see cref="ProcessTrace.Disabled"/> 构造，不经过 ProcessState / Process /
/// GlobalStatic 引擎字段 —— 这正是劈分前拿不到的：此前调用栈语义只能借 ProcessState 间接触达。
///
/// 注：FunctionLabelLine 构造依赖 Config（privateVar 字典用 Config.Config.StrComper），
/// 故涉及标签的用例开最小 Config scope。此为 FunctionLabelLine 既有耦合，非本刀引入。
/// </summary>
public class CallStackTests
{
    static CallStack NewStack() => new(ProcessTrace.Disabled);

    static FunctionLabelLine NewLabel(string name)
        => new(new ScriptPosition("TEST.ERB", 1), name, new WordCollection());

    static CalledFunction NewCall(string name) => CalledFunction.CreateCalledFunctionMethod(NewLabel(name), name);

    [Fact]
    public void Empty_stack_reports_ScriptEnd_and_no_scope()
    {
        var stack = NewStack();

        Assert.True(stack.ScriptEnd);
        Assert.Equal(0, stack.FunctionCount);
        Assert.Null(stack.Scope);
        Assert.Null(stack.GetCurrentReturnAddress);
    }

    [Fact]
    public void IntoFunction_moves_PC_to_function_head_and_counts_a_step()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var stack = NewStack();
        var label = NewLabel("TESTFUNC");

        stack.IntoFunction(CalledFunction.CreateCalledFunctionMethod(label, "TESTFUNC"), null!, null!);

        Assert.False(stack.ScriptEnd);
        Assert.Equal(1, stack.FunctionCount);
        Assert.Same(label, stack.CurrentLine); // PC = 函数头
        Assert.Equal(1, stack.LineCount);
        Assert.Equal("TESTFUNC", stack.Scope);
    }

    [Fact]
    public void Return_empties_stack_and_reports_it_when_no_return_address()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var stack = NewStack();
        stack.IntoFunction(NewCall("TESTFUNC"), null!, null!);

        bool emptied = stack.Return(0);

        Assert.True(emptied); // 调用方（ProcessState）据此触发挂起的 BEGIN
        Assert.True(stack.ScriptEnd);
        Assert.Null(stack.CurrentLine);
    }

    [Fact]
    public void CurrentMin_raises_the_stack_floor_for_in_expression_functions()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var stack = NewStack();
        stack.IntoFunction(NewCall("TESTFUNC"), null!, null!);
        Assert.False(stack.ScriptEnd);

        stack.CurrentMin = stack.FunctionCount; // Process.GetValue 的既有手法：抬起栈底指针

        Assert.True(stack.ScriptEnd); // 子栈视角为空
        stack.CurrentMin = 0;
        Assert.False(stack.ScriptEnd);
    }

    [Fact]
    public void Return_on_a_jump_frame_pops_it_and_continues_into_the_caller()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var stack = NewStack();
        stack.IntoFunction(NewCall("CALLER"), null!, null!);

        var jumped = NewCall("JUMPED");
        jumped.IsJump = true; // IsJump 是 public setter —— 该分支无需真实 LabelDictionary 即可直达
        stack.IntoFunction(jumped, null!, null!);
        Assert.Equal(2, stack.FunctionCount);

        bool emptied = stack.Return(0);

        // JUMP 帧被立即弹出并继续 RETURN RESULT 到下一帧，而不是把控制权留在 JUMP 帧上
        Assert.Equal(0, stack.FunctionCount);
        Assert.True(emptied);
    }

    [Fact]
    public void ReturnF_pops_and_publishes_the_method_return_value()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var stack = NewStack();
        stack.IntoFunction(NewCall("TESTMETHOD"), null!, null!);

        var term = new SingleLongTerm(42);
        stack.ReturnF(term);

        Assert.Same(term, stack.MethodReturnValue);
        Assert.True(stack.ScriptEnd);
    }

    [Fact]
    public void Clear_empties_the_stack_without_moving_the_PC()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var stack = NewStack();
        var label = NewLabel("TESTFUNC");
        stack.IntoFunction(CalledFunction.CreateCalledFunctionMethod(label, "TESTFUNC"), null!, null!);

        stack.Clear();

        Assert.True(stack.ScriptEnd);
        Assert.Equal(0, stack.FunctionCount);
        Assert.Same(label, stack.CurrentLine); // 既有语义：ClearFunctionList / Begin 都不动 PC
    }
}
