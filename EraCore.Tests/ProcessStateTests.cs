using System.Collections.Generic;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Parser;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0011 / Issue 03：ProcessState 隔离单测。
/// 候选 4 · 第 1 刀后：调试 trace 经 <see cref="ProcessTrace"/> 注入，默认走
/// <see cref="ProcessTrace.Disabled"/>，不开启 GlobalStatic scope、不碰 AsyncLocal ambient。
/// </summary>
public class ProcessStateTests
{
    // LogicalLine 为抽象类且无抽象成员，测试内造一个最小具体行。
    private sealed class FakeLine : LogicalLine { }

    private static ProcessState NewState() => new(ProcessTrace.Disabled);

    [Fact]
    public void ShiftNextLine_advances_to_NextLine_and_increments_lineCount()
    {
        var state = NewState();
        var a = new FakeLine();
        var b = new FakeLine();
        a.NextLine = b;
        state.CurrentLine = a;
        var before = state.lineCount;

        state.ShiftNextLine();

        Assert.Same(b, state.CurrentLine);
        Assert.Equal(before + 1, state.lineCount);
    }

    [Fact]
    public void JumpTo_sets_CurrentLine_and_increments_lineCount()
    {
        var state = NewState();
        var target = new FakeLine();
        var before = state.lineCount;

        state.JumpTo(target);

        Assert.Same(target, state.CurrentLine);
        Assert.Equal(before + 1, state.lineCount);
    }

    [Fact]
    public void ScriptEnd_is_true_when_no_function_on_stack()
    {
        var state = NewState();
        Assert.True(state.ScriptEnd);
    }

    [Fact]
    public void Begin_TITLE_transitions_State_to_Title_Begin()
    {
        var state = NewState();
        state.Begin(BeginType.TITLE);
        Assert.Equal(SystemStateCode.Title_Begin, state.SystemState);
        Assert.False(state.isBegun); // Begin() 末尾将 begintype 复位为 NULL
    }

    [Fact]
    public void Clone_copies_execution_state_fields()
    {
        var state = NewState();
        var line = new FakeLine();
        state.CurrentLine = line;
        state.SystemState = SystemStateCode.Train_Begin;
        state.Begin(BeginType.TRAIN);

        var clone = state.Clone();

        Assert.NotSame(state, clone);
        Assert.True(clone.IsClone);
        Assert.Same(line, clone.CurrentLine);
        Assert.Equal(SystemStateCode.Train_Begin, clone.SystemState);
    }

    [Fact]
    public void IntoFunction_pushes_and_Return_pops_the_call_frame()
    {
        // 注：FunctionLabelLine 构造依赖 Config（privateVar 字典用 Config.Config.StrComper），
        // 此为 FunctionLabelLine 既有耦合，非本重构引入；函数栈 push/pop 行为本身不依赖
        // GlobalStatic 引擎字段。故仅此处开最小 Config scope。
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var state = NewState();
        var label = new FunctionLabelLine(null, "TESTFUNC", new WordCollection());
        var called = CalledFunction.CreateCalledFunctionMethod(label, "TESTFUNC");

        Assert.True(state.ScriptEnd); // 空栈
        state.IntoFunction(called, null!, null!);
        Assert.False(state.ScriptEnd); // 已压入一帧
        Assert.Same(label, state.CurrentLine);

        state.Return(0);
        Assert.True(state.ScriptEnd); // 帧已弹出
        Assert.Null(state.CurrentLine);
    }

    // 候选 4 · 第 1 刀：trace 改经 ProcessTrace 注入后，原先只在 Program.DebugMode=true 下
    // 可观察的 Add/Remove 配对，现在可在默认（DebugMode=false）测试环境直接断言。
    [Fact]
    public void IntoFunction_and_Return_emit_balanced_trace_through_injected_sink()
    {
        using var scope = GlobalStatic.OpenScope(new ConfigData());
        var ops = new List<string>();
        var trace = ProcessTrace.FromDelegates(
            s => ops.Add("add:" + s),
            () => ops.Add("remove"),
            () => ops.Add("clear"));
        var state = new ProcessState(trace);
        var label = new FunctionLabelLine(new ScriptPosition("TEST.ERB", 1), "TESTFUNC", new WordCollection());
        var called = CalledFunction.CreateCalledFunctionMethod(label, "TESTFUNC");

        state.IntoFunction(called, null!, null!);
        state.Return(0);

        Assert.Equal(2, ops.Count);
        Assert.StartsWith("add:CALL @TESTFUNC", ops[0]);
        Assert.Equal("remove", ops[1]); // 出栈必须消耗掉入栈时压入的 trace 行
    }

    [Fact]
    public void ClearFunctionList_respects_injected_clear_gate()
    {
        // gate 替代原先内联的 GlobalStatic.Process.MethodStack() == 0 判定
        //（ADR-0011 决策三：子模块不持 Process 反向引用）。
        var ops = new List<string>();
        var trace = ProcessTrace.FromDelegates(ops.Add, clear: () => ops.Add("clear"));

        var blocked = new ProcessState(trace, () => false);
        blocked.ClearFunctionList();
        Assert.DoesNotContain("clear", ops);

        var allowed = new ProcessState(trace, () => true);
        allowed.ClearFunctionList();
        Assert.Single(ops);
        Assert.Equal("clear", ops[0]);
    }
}
