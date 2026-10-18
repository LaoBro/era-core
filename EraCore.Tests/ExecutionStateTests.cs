using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// Issue 07：ExecutionState 收口 SystemProc/ScriptProc 共享可变上下文为纯数据容器，
/// 不依赖 Process/Console/Config scope，可直测。
/// </summary>
public sealed class ExecutionStateTests
{
    [Fact]
    public void defaults_match_ERB_engine_contract()
    {
        var es = new ExecutionState();
        Assert.False(es.skipPrint);
        Assert.False(es.isCTrain);
        Assert.Empty(es.coms);
        Assert.Equal(0, es.count);
        Assert.Equal(-1, es.doTrainSelectCom);
        Assert.Equal(0, es.systemResult);
        Assert.False(es.flowinput);
        Assert.False(es.flowinputCanSkip);
        Assert.Equal("", es.flowinputDefString);
        Assert.False(es.flowinputString);
        Assert.False(es.flowinputForceSkip);
        Assert.False(es.noError);
        Assert.Null(es.CurrentState);
    }

    [Fact]
    public void properties_round_trip()
    {
        var es = new ExecutionState();
        var state = new FakeProcessState();

        es.skipPrint = true;
        es.isCTrain = true;
        es.coms = [1, 2, 3];
        es.count = 5;
        es.doTrainSelectCom = 42;
        es.systemResult = 99;
        es.flowinput = true;
        es.flowinputDef = 7;
        es.flowinputDefString = "default";
        es.noError = true;
        es.CurrentState = state;

        Assert.True(es.skipPrint);
        Assert.True(es.isCTrain);
        Assert.Equal([1, 2, 3], es.coms);
        Assert.Equal(5, es.count);
        Assert.Equal(42, es.doTrainSelectCom);
        Assert.Equal(99, es.systemResult);
        Assert.True(es.flowinput);
        Assert.Equal(7, es.flowinputDef);
        Assert.Equal("default", es.flowinputDefString);
        Assert.True(es.noError);
        Assert.Same(state, es.CurrentState);
    }

    [Fact]
    public void CurrentState_is_shared_mutable_reference()
    {
        var es = new ExecutionState();
        var a = new FakeProcessState { SystemState = SystemStateCode.Title_Begin };
        var b = new FakeProcessState { SystemState = SystemStateCode.Shop_Begin };

        es.CurrentState = a;
        Assert.Equal(SystemStateCode.Title_Begin, es.CurrentState.SystemState);

        es.CurrentState = b;
        Assert.Equal(SystemStateCode.Shop_Begin, es.CurrentState.SystemState);
    }

    sealed class FakeProcessState : IProcessState
    {
        public SystemStateCode SystemState { get; set; }
        public LogicalLine CurrentLine { get; set; } = null!;
        public bool ScriptEnd { get; set; }
        public bool IsFunctionMethod { get; set; }
        public bool isBegun { get; set; }
        public bool calledWhenNormal { get; set; }
        public CalledFunction CurrentCalled { get; set; } = null!;
        public int lineCount { get; set; }
        // 候选 4 · 第 3 刀：接口推广到指令层后补上的 3 个成员
        public string Scope { get; set; } = null!;

        public void ShiftNextLine() { }
        public void JumpTo(LogicalLine line) { }
        public void Return(long ret) { }
        public void IntoFunction(CalledFunction call, UserDefinedFunctionArgument? srcArgs, ExpressionMediator exm) { }
        public void Begin() { }
        public void ClearFunctionList() { }
        public void SetBegin(string keyword, bool force) { }
        public void ReturnF(SingleTerm ret) { }
        public IProcessState Clone() => new FakeProcessState
        {
            SystemState = SystemState,
            lineCount = lineCount,
            calledWhenNormal = calledWhenNormal,
            ScriptEnd = ScriptEnd,
            IsFunctionMethod = IsFunctionMethod,
            isBegun = isBegun,
            CurrentCalled = CurrentCalled,
            CurrentLine = CurrentLine,
            Scope = Scope,
        };
    }
}
