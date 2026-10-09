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
                    // 定时等待（AWAIT / TWAIT t,1）。参考实现语义是「入力不能、待つしかない」
                    // —— PressEnterKey 对 Void 直接 return，输入被吞掉。
                    //
                    // 但 Void 此前根本不在本 switch 里：落到 default 会抛
                    // ArgumentOutOfRangeException —— server 侧被 StepAsync 的 catch-all
                    // 判成 fatal 并杀掉会话，CLI 侧直接 fatal 退出。改动前只有 TWAIT t,1
                    // 能踩到；issue-003 让 AWAIT 也进入该状态后，每个动画都是雷，故必补。
                    //
                    // Skippable（仅 AWAIT 置位）：走与超时续跑相同的 SubmitTimeout 路径
                    // 提前结束等待 —— issue-003 验收项「长动画期间按任意键可中断」。
                    // 未置位则吞掉输入，保持参考实现语义。
                    if (req.Skippable && req.Timelimit > 0)
                    {
                        // 请求侧快进仍用 \e（spec v5 §4.2，对齐优先于 API 优雅）。
                        // 置位后再走续跑：本次 RunEmueraProgram 期间 MESSKIP() 为真，
                        // 脚本里的 `SIF MESSKIP() RETURN` 由此从死代码变为可执行。
                        // ——这正是「打断」与「只迈一步」的分水岭：不置位则脚本读到 0，
                        // 动画只会往后走一帧然后再次等待。
                        //
                        // scoped 语义（§4.3）：MesSkip 不是「只由输入侧拥有」的状态（脚本
                        // SKIPLOG 也写它，ScriptProc.cs:787），故这里只收回「本文自己写的
                        // 那一置位」，且必须自己归零 —— 本路径绕过了 ConsoleInputHandler 既有
                        // 的两处清零点（PressEnterKey 对 Void 在 :106 早退，循环后归零到不了）。
                        // 不归零会让后续 10 处 CanSkip 输入被静默填默认值。try/finally 覆盖
                        // SubmitTimeout 的异常路径。
                        //
                        // ⚠️ 已知范围边界（§4.4 ⚠️）：本路径是 SubmitTimeout() → EndTimerCore()
                        // 的单次 RunEmueraProgram("")，不进 PressEnterKey 的跨 WaitInput while
                        // 快进链（ConsoleInputHandler.cs:134-142）。故只承诺**单个续跑区间内**
                        // 跳过：脚本若在两次等待之间夹了 WAIT，不承诺跨过去（有意收窄，避免
                        // MesSkip 长期为真污染上述 CanSkip 分支）。
                        if (input.Contains("\\e", StringComparison.Ordinal))
                            console.MesSkip = true;
                        try { console.SubmitTimeout(); }
                        finally { console.MesSkip = false; }
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(input), req.InputType, $"未处理的输入类型: {req.InputType}");
            }
        }

        protected virtual void OnInputRejected(string reason) { }
    }
}
