using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Server;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 「有界等待长跑任务收束」这条不变式的确定性覆盖（issue-003 引入的窄接缝）。
///
/// <para><b>为什么在这里测而不是在集成层</b>：超时分支要求被等待的任务在窗口内<b>不</b>结束。
/// 集成层唯一的来源是「游戏循环卡在外部插不上手的窗口里」，而 issue-002/003 把这些窗口
/// 逐个堵掉后它已不可达（实测结论见 <c>BoundedLoopWait</c> 的注释与 issue-003 票面）。
/// 本模块的接缝只是一个 <see cref="Task"/> 引用，用一个永不完成的任务就能确定性地覆盖。</para>
/// </summary>
public class BoundedLoopWaitTests
{
    /// <summary>测试用上界。取小值以免拖慢套件。</summary>
    private static readonly TimeSpan WaitBound = TimeSpan.FromMilliseconds(300);

    [Fact]
    public async Task Null_task_is_treated_as_already_exited()
    {
        Assert.True(await BoundedLoopWait.ForCompletionAsync(null, WaitBound, null));
    }

    [Fact]
    public async Task Completed_task_returns_true_without_waiting()
    {
        var watch = Stopwatch.StartNew();
        Assert.True(await BoundedLoopWait.ForCompletionAsync(Task.CompletedTask, WaitBound, null));
        watch.Stop();
        Assert.True(watch.ElapsedMilliseconds < 100, $"已完成的任务不应等待，实测 {watch.ElapsedMilliseconds}ms");
    }

    /// <summary>核心分支：任务永不结束 → 到达上界后返回 false，而不是挂起。</summary>
    [Fact]
    public async Task Never_completing_task_times_out_bounded()
    {
        var never = new TaskCompletionSource().Task;

        var watch = Stopwatch.StartNew();
        var exited = await BoundedLoopWait.ForCompletionAsync(never, WaitBound, null);
        watch.Stop();

        Assert.False(exited);
        // 有界：不得早退（否则等于没等），也不得远超上界（否则等于挂起）
        Assert.True(watch.Elapsed >= WaitBound - TimeSpan.FromMilliseconds(50),
            $"等待 {watch.ElapsedMilliseconds}ms 就返回了，小于上界 {WaitBound.TotalMilliseconds}ms —— 没等到上界");
        Assert.True(watch.Elapsed < WaitBound + TimeSpan.FromSeconds(2),
            $"等待 {watch.ElapsedMilliseconds}ms 远超上界 {WaitBound.TotalMilliseconds}ms —— 疑似挂起");
    }

    /// <summary>上界内结束 → true（不做无谓的等待）。</summary>
    [Fact]
    public async Task Task_completing_within_bound_returns_true()
    {
        var tcs = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            tcs.SetResult();
        });

        Assert.True(await BoundedLoopWait.ForCompletionAsync(tcs.Task, WaitBound, null));
    }

    /// <summary>无上界：等到它真的结束，返回 true。</summary>
    [Fact]
    public async Task Unbounded_wait_returns_true_after_task_ends()
    {
        var tcs = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            tcs.SetResult();
        });

        Assert.True(await BoundedLoopWait.ForCompletionAsync(tcs.Task, System.Threading.Timeout.InfiniteTimeSpan, null));
    }

    /// <summary>异常收束：仍判定为「已退出」，但必须上报 —— 否则异常会被静默吞掉。</summary>
    [Fact]
    public async Task Faulted_task_reports_and_still_counts_as_exited()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("boom"));

        Exception? reported = null;
        Assert.True(await BoundedLoopWait.ForCompletionAsync(tcs.Task, WaitBound, ex => reported = ex));
        Assert.IsType<InvalidOperationException>(reported);
    }

    /// <summary>无上界 + 异常收束：同样必须上报（Dispose 路径的诊断依赖它）。</summary>
    [Fact]
    public async Task Faulted_task_reports_on_unbounded_path_too()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetException(new InvalidOperationException("boom"));

        Exception? reported = null;
        Assert.True(await BoundedLoopWait.ForCompletionAsync(tcs.Task, System.Threading.Timeout.InfiniteTimeSpan, ex => reported = ex));
        Assert.IsType<InvalidOperationException>(reported);
    }
}
