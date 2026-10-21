using System;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 深模块：独占「有界等待一个长跑任务收束」这一条不变式，供会话拆除路径复用。
///
/// <para><b>为什么从 <see cref="Session"/> 抽出来</b>（issue-003 实现记录）：
/// 这条不变式的关键分支是<b>超时</b>，而在集成层面构造超时要求游戏循环卡在一个
/// 「外部插不上手」的窗口里。issue-002（脚本层每条指令前的检查点）与 issue-003
/// （<c>AWAIT</c> 对齐成等待输入态）把这类窗口逐个堵掉之后，引擎里已<b>不存在</b>
/// 这样的窗口 —— 票面给的两个候选也在实现中被实测否掉（单指令重计算受引擎
/// 「用户数组 ≤ 100 万」硬上限约束，最多约 0.2 秒；初始化期会被拆除本身打断）。
/// 于是超时分支在集成测试里不可达。</para>
///
/// <para>把等待逻辑收进本模块后，它的接缝只是「一个 <see cref="Task"/> 引用」，
/// 用一个永不完成的任务即可<b>确定性</b>地覆盖超时分支 —— 与 <see cref="TurnDelivery"/>
/// 用 <c>IControlSignal</c>/<c>ITurnSink</c> 窄接缝换确定性编排同一手法。
/// 这正是本模块作为深模块的价值：删除它，那两个集成测试就要自己拼等待与异常观察，
/// 且超时分支再也测不到。</para>
///
/// <para><b>不变式</b>：
/// <list type="bullet">
/// <item>任务为 <c>null</c> 或已结束 → 立即返回 <c>true</c>（不等）。</item>
/// <item>无上界（<see cref="Timeout.InfiniteTimeSpan"/>）→ 等到它结束，返回 <c>true</c>。</item>
/// <item>有上界 → 窗口内结束返回 <c>true</c>；否则返回 <c>false</c>，
/// <b>不抛异常、不硬杀线程</b>（硬杀会留下未释放的进程级静态与不一致的游戏状态，
/// 见 spec 决策二）。</item>
/// <item>任务以异常收束 → 交 <paramref name="onFault"/> 报告后仍返回 <c>true</c>：
/// 「已退出」与「为什么退出」是两件事，退出判定不该因异常翻转。</item>
/// </list></para>
/// </summary>
internal static class BoundedLoopWait
{
    /// <param name="task">被等待的任务；<c>null</c> 视为已完成。</param>
    /// <param name="timeout">等待上界；<see cref="Timeout.InfiniteTimeSpan"/> 表示无上界。</param>
    /// <param name="onFault">任务以异常收束时的上报回调（调用方据此写诊断日志），可为 <c>null</c>。</param>
    /// <returns><c>true</c> = 任务已结束；<c>false</c> = 超时，任务仍在跑。</returns>
    internal static async Task<bool> ForCompletionAsync(Task? task, TimeSpan timeout, Action<Exception>? onFault)
    {
        if (task == null)
            return true;

        if (task.IsCompleted)
        {
            // 已结束也要观察异常：否则该异常无人观察（会走到 TaskScheduler.UnobservedTaskException），
            // 且「为什么退出」这一诊断信息丢失 —— 调用方拿到的是一个静默的 true。
            // 这条改动来自单测暴露：faulted 任务在进入等待前就已 IsCompleted。
            if (task.IsFaulted)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    onFault?.Invoke(ex);
                }
            }
            return true;
        }

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onFault?.Invoke(ex);
            }
            return true;
        }

        // 有上界：Task.WhenAny 而非 WaitAsync —— 后者在超时后抛
        // OperationCanceledException/TimeoutException，前者只需一个 bool。
        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != task)
            return false;

        // 已退出：观察其异常，避免未观察异常在 continuation 触发。
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onFault?.Invoke(ex);
        }
        return true;
    }
}
