using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Sessions;
using Session = MinorShift.Emuera.Sessions.CoreSession;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 失败可见切片红测（handoff-nativeaot-parallel-erb-2026-08-27 §6.2）：
/// 初始化阶段（runLoop 回调之前——OpenScope / console.Initialize）抛出异常时，
/// Session 必须干净终结：派发错误回合、HasEnded=true、StateString 离开 "Loading"、
/// LastError 记录异常——而不是 _gameTask 静默 fault 导致前端永久 Loading。
///
/// 失败注入方式：Session 以 configData=null 构造。GameLoopAsync 的 buildProtocol 回调
/// 内部 <c>_configData.GetConfigValue&lt;string&gt;(ConfigCode.FontName)</c> 解引用空配置，
/// 在 GameLoopComposer.RunAsync 进入 runLoop 回调之前确定性抛出 NRE——不需要任何全局
/// 状态体操，也不依赖测试执行顺序。
/// </summary>
public class SessionInitFailureTests
{
    [Fact]
    public async Task Init_stage_failure_delivers_error_turn_and_ends_loading()
    {
        var hub = new OutputHub();
        var io = new HttpSessionIO(hub);
        // null! —— 故意的失败注入（见类注释）；类型系统层面用 null! 显式标注。
        var session = new Session(io, new SessionRegistryTests.NullTerminalSetup(), null!);

        session.Start();

        // 轮询等待游戏循环终结（真实并发；设上限防止回归后退化为无限挂起的测试）
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!session.HasEnded && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(session.HasEnded, "初始化阶段失败后游戏循环应终结并派发错误回合，而非静默 fault 挂死");
        Assert.True(session.LastError != null, "LastError 应记录初始化阶段的原始异常");
        Assert.NotEqual("Loading", session.StateString);

        // 错误原因必须经 IO 可达前端：error JSON 行先于 final turn 入队
        var wait = await session.WaitForTurnAsync(1000, CancellationToken.None);
        Assert.Equal(TurnWaitStatus.Turn, wait.Status);
        Assert.Contains("error", wait.Turn?.Wire ?? "");
    }
}
