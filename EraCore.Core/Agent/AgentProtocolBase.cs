using System;
using System.Threading;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal abstract class AgentProtocolBase
    {
        private readonly CancellationTokenSource _cts = new();
        protected readonly EmueraConsole console;
        protected readonly IConsoleUI ui;
        protected const int TurnTimeoutMs = 30000;
        protected const int PollIntervalMs = 50;

        internal AgentProtocolBase(EmueraConsole console, IConsoleUI ui)
        {
            this.console = console;
            this.ui = ui;
        }

        internal bool IsStopped => _cts.IsCancellationRequested;
        internal CancellationToken StopToken => _cts.Token;

        internal virtual void Stop() => _cts.Cancel();

        /// <summary>是否允许以空输入（模拟回车/点击空白）提交。空串在 IntValue/AnyValue 下会被
        /// long.TryParse 拒绝，故仅 EnterKey/AnyKey/StrValue/IntButton/StrButton 返回 true。
        /// 单一真相源，供 DispatchInput 与 DispatchMouseMiss 共用，避免两份 InputType 清单漂移。</summary>
        protected static bool AllowsEmptyInput(InputType type) => type switch
        {
            InputType.EnterKey => true,
            InputType.AnyKey => true,
            InputType.StrValue => true,
            InputType.IntButton => true,
            InputType.StrButton => true,
            _ => false,
        };

        /// <summary>
        /// 让路点判据（<b>单一真相源</b>：CLI 与 server 共用，避免两份清单漂移）：
        /// <c>State=WaitInput</c> + <see cref="InputType.Void"/> + <b>无时长</b>（<c>Timelimit&lt;=0</c>）。
        ///
        /// <para>这正是让路帧在 wire 上的可观测形态（spec §3.6：<c>state:"WaitInput"</c> +
        /// <c>inputType:"Void"</c> + <c>Timelimit&lt;=0</c>），故判据与实际形态同源，
        /// 不引入第二处真相。刻意<b>不</b>再叠加内部标记 <c>InputRequest.IsYield</c>：
        /// 它只由 <c>ConsoleStateManager.Await(time&lt;=0)</c> 置位、与 <c>Timelimit=0</c> 恒等，
        /// 叠加它只会漏掉另一类零时长 Void 等待（<c>TWAIT 0,1</c>）——那类等待
        /// <c>InputTimeoutMs</c> 返回 null（无超时续跑）又不被判为让路点 ⇒ 宿主循环
        /// 阻塞在读输入上、脚本永久停住。</para>
        ///
        /// <para>🔒 <c>Timelimit &lt;= 0</c> 是安全阀，不可省：省掉会把<b>真</b>的定时等待
        /// （<c>TWAIT t,1</c> / <c>AWAIT t</c>：Void 且 <c>Timelimit &gt; 0</c>）误判为让路点，
        /// drain 会把它提前续跑，吞掉本次等待的输入 / 计时能力。</para>
        /// </summary>
        protected bool IsAtYieldPoint()
            => console.State == ConsoleState.WaitInput
               && console.CurrentRequest is { InputType: InputType.Void, Timelimit: <= 0 };

        protected virtual void DispatchInput(string input)
        {
            if (console.State != ConsoleState.WaitInput)
                return;
            var req = console.CurrentRequest;
            if (req == null) return;

            // 系统命令（@QUIT/@REBOOT 等）绕过输入类型校验，直接走 PressEnterKey → DoSystemCommand。
            // 与 ConsoleInputHandler.PressEnterKey 内的 @ 前缀检查对齐：IntValue/AnyValue
            // 否则会因 long.TryParse("@QUIT") 失败而拒绝系统命令。
            if (input.Length > 1 && !req.OneInput && input.StartsWith('@'))
            {
                console.PressEnterKey(false, input, false);
                return;
            }

            switch (req.InputType)
            {
                case InputType.EnterKey:
                case InputType.AnyKey:
                case InputType.StrValue:
                case InputType.IntButton:
                case InputType.StrButton:
                    console.PressEnterKey(false, input, false);
                    break;
                case InputType.IntValue:
                case InputType.AnyValue:
                    if (long.TryParse(input, out _))
                        console.PressEnterKey(false, input, false);
                    else
                        OnInputRejected("当前需要整数输入，请重试");
                    break;
                case InputType.PrimitiveMouseKey:
                    OnInputRejected("当前等待原始鼠标/键盘事件，请通过鼠标点击或键盘输入");
                    break;
                case InputType.Void:
                    // 定时等待（AWAIT / TWAIT t,1 / 零时长让路点）：**一律吞掉**
                    // —— 不推进、不报错、不发 error，等待只由定时器正常到期结束。
                    //
                    // 判据（spec §10.1，写死）：参考实现不能打断的指令，era-core 也不得能打断。
                    // 参考实现里 Void 的语义是「入力不能、待つしかない」（PressEnterKey 对 Void
                    // 直接 return），且 AWAIT 压根是 Sleep 态、不建立 InputRequest。
                    // era-core 为「无头架构下能交付帧」把 AWAIT 落成了 WaitInput(Void)，
                    // 形态不同，但**可观测行为必须相同** ⇒ 输入一律吞掉。
                    //
                    // 🔴 issue 10 撤回：这里曾按 `req.Skippable` 走 SubmitTimeout 提前结束
                    // AWAIT 的等待（e71d5e6 还补了 MesSkip 的 scoped 置位）。那条后门是
                    // era-core 自创语义，实测在 eraTW @BADAPPLE 上表现为前端「跳过」图标
                    // 逐帧闪烁，且违背上述判据 ⇒ 连同 wire 上的 wait.interruptible 一并撤回。
                    //
                    // ⚠️ 本分支必须保留：Void 此前不在本 switch 里，落到 default 会抛
                    // ArgumentOutOfRangeException —— server 侧被 StepAsync 的 catch-all
                    // 判成 fatal 杀掉会话，CLI 侧直接 fatal 退出。吞掉 ≠ 抛异常。
                    //
                    // ⚠️ 快进不走这里：Void 等待上**不能启动**跳过（参考实现 PressEnterKey
                    // 的 Void 早退在 \e 检测之前）。请求侧快进由值输入等待发起，见 issue 11。
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input), req.InputType, $"未处理的输入类型: {req.InputType}");
            }
        }

        protected virtual void OnInputRejected(string reason) { }
    }
}
