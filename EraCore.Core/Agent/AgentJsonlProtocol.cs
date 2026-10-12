using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.GameView
{
    internal class AgentJsonlProtocol : AgentProtocolBase
    {
        // Issue 08：internal（原 private）—— BridgeHost.ShowFatalError 序列化 error turn 时复用，
        // 保证与 AgentJsonlProtocol.StepAsync 的 error turn 完全一致（同 converters + ignore condition）。
        // MAUI 项目经 InternalsVisibleTo("EraCore.Maui") 可访问。
        // 3.3（NativeAOT）：TypeInfoResolver 指向源生成上下文——AOT 下反射序列化被禁用，
        // TurnOpConverter/LineOpConverter 的装箱 Write 经此 resolver 按运行时类型解析，wire 不变。
        internal static readonly JsonSerializerOptions TurnJsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new TurnOpConverter(), new LineOpConverter() },
            TypeInfoResolver = EmueraJsonContext.Default,
        };

        /// <summary>
        /// TurnRecord 序列化（converter 多态 + 源生成 resolver 的完整形态）。
        /// 用 JsonTypeInfo 重载而非 options 重载：`Serialize(T, JsonSerializerOptions)` 标了
        /// RequiresUnreferencedCode/RequiresDynamicCode（IL2026/IL3050），即使运行时经
        /// TurnJsonOptions.TypeInfoResolver 走源生成也报静态警告；JsonTypeInfo 重载无标注。
        /// <see cref="JsonSerializerOptions.GetTypeInfo"/> 从 TurnJsonOptions 解析——converter 链
        /// （TurnOpConverter/LineOpConverter 的多态装箱 Write）完整保留，wire 与 options 重载一致。
        /// 注意：不可改用 <c>EmueraJsonContext.Default.TurnRecord</c>（context 无 Converters）——
        /// LineOp/TurnOp 抽象基类直接序列化只输出 type 鉴别符，diff 内容丢失
        /// （test_snapshot 抓到的 {"lineOps":[{"type":"append"}]} 空壳回归，2026.8.7）。
        /// </summary>
        internal static string SerializeTurn(TurnRecord turn)
            => JsonSerializer.Serialize(turn, (JsonTypeInfo<TurnRecord>)TurnJsonOptions.GetTypeInfo(typeof(TurnRecord)));

        /// <summary>
        /// 致命错误帧（轴 A 的例外 B5）：<b>不经过脚本</b>、不对应任何停稳点，由第二产出者
        /// （<c>EraCore.Maui/BridgeHost.ShowError</c>）构造，形态是终态键
        /// <c>awaiting.ended{terminal:"Error"}</c>（<c>input</c> / <c>wait</c> 不得同时出现）。
        ///
        /// <para>🔴 <b>必须与主流帧共用同一条序列化路径</b>（<see cref="SerializeTurn"/>，含多态
        /// converter + 源生成 resolver）。MAUI 侧原先直接用 <c>EmueraJsonContext.Default.TurnRecord</c>
        /// ——那是<b>没有 Converters</b> 的上下文，历史上只因 fatal turn 的 <c>diff:null</c> 才侥幸不炸；
        /// <c>awaiting</c> 一变成内嵌对象，这条路径的风险就不再是「改一行注册」的量级。</para>
        /// </summary>
        internal static string BuildFatalTurn(string message) => SerializeTurn(new TurnRecord(
            diff: null,
            awaiting: AwaitInfo.Of(null, ConsoleState.Error, timedOut: false),
            error: message));

        /// <summary>
        /// 会话级异常帧（初始化阶段失败 / 游戏循环回调异常）——同样<b>不经过脚本</b>、是终态。
        ///
        /// <para>与 <see cref="BuildFatalTurn"/> 同属「第二产出者」：必须与主流帧共用同一条
        /// 序列化路径（<see cref="SerializeTurn"/>），形状是 <c>awaiting.ended{terminal:"Error"}</c>。
        /// 差别只在这里额外携带结构化诊断 <paramref name="errorInfo"/>，且 <c>error</c> 用完整串
        /// （含堆栈）以兼容既有消费者。</para>
        ///
        /// <para>⚠️ 这两个调用点原先是手拼 <c>JsonObject</c>（含旧的顶层 <c>state</c>、无
        /// <c>awaiting</c>）——v12 起前端要求 <c>awaiting</c> 必填，手拼帧会被判为协议解析失败、
        /// 真实错误反而看不到。</para>
        /// </summary>
        internal static string BuildErrorTurn(string errorText, ErrorInfo? errorInfo)
            => SerializeTurn(new TurnRecord(
                diff: null,
                awaiting: AwaitInfo.Of(null, ConsoleState.Error, timedOut: false),
                error: errorText,
                errorInfo: errorInfo));

        private readonly SessionIO _io;
        private readonly DisplayState _displayState;
        private string? _pendingRejectReason;
        /// <summary>
        /// ADR-0016：TINPUT 超时 flag——SubmitTimeoutAsync 路径置 true，
        /// 下次 BuildTurn 写出后清空。复用 _pendingRejectReason 同样的"一次性旗标"模式。
        /// RunLoopAsync 的 OperationCanceledException 分支调 HandleTimeoutAndWriteAsync
        /// → SubmitTimeoutAsync → 此处置 true → BuildTurn 读出写入 `awaiting.wait.timedOut` → 清空。
        /// </summary>
        private bool _pendingTimeoutFlag;

        /// <summary>
        /// issue-004（空转帧抑制）：上一个<b>已产出</b>回合的指纹。
        /// 回合 diff 为空且指纹未变 = 画面没变、客户端也没什么新东西要知道 → 不产出回合。
        /// 仅在游戏循环线程（BuildTurn）访问，无并发。
        ///
        /// <para>v5-r2（spec §2.3）：指纹 = <b>形状</b>（哪个键出现：input / wait / ended）+
        /// <b>两个维度各自的性质</b>（input.type / wait 的 durationMs +
        /// displayTime + timeUpMessage）。v11 那份「state + inputType + needValue」与它语义等价
        /// 但更粗 —— 旧形态里 <c>input{type:A,timeLimit:3000}</c> vs <c>5000</c> 靠载荷里的
        /// <c>timeLimit</c> 比较，新形态里直接是 <c>wait.durationMs</c>。</para>
        /// </summary>
        private bool _hasEmittedTurn;
        private string? _emittedShape;
        private string? _emittedInputProps;
        private string? _emittedWaitProps;

        public AgentJsonlProtocol(EmueraConsole console, IConsoleUI ui, SessionIO io, DisplayState displayState)
            : base(console, ui)
        {
            _io = io;
            _displayState = displayState;
        }

        internal async Task<string?> GetInitialTurnAsync()
        {
            if (!await WaitForInputAsync())
                return null;

            // 首帧：携带协议版本，永不抑制（isInitial 已保证，显式传参只为把意图写清楚）。
            return BuildTurn(isInitial: true, allowSuppression: false);
        }

        internal async Task<string?> StepAsync(string input)
        {
            if (IsStopped)
                return null;

            if (!await WaitForInputAsync())
                return null;

            if (console.State != ConsoleState.WaitInput)
                return null;

            try
            {
                ui.Invoke(() => DispatchInput(input));

                // 应答链路：客户端发了输入就必须有回音，不参与空转抑制。
                return BuildTurn(allowSuppression: false);
            }
            catch (GameExitException)
            {
                // QUIT/EXIT：正常退出。
                // 不能走下方 catch-all——那会构造 state=Running + error 文本的回合，
                // 前端画面不变、无可见错误，表现为假死。这里构造干净 Quit 回合并结束会话。
                var quitTurn = SerializeTurn(new TurnRecord(
                    diff: null,
                    // 终态：只剩 ended（§0.1 ① 那个「Quit 帧还带 inputType/timeLimit」的残留
                    // bug 在新形态下结构性不可能构造出来）。
                    awaiting: AwaitInfo.Of(null, ConsoleState.Quit, timedOut: false)
                ));
                Stop();
                return quitTurn;
            }
            catch (Exception ex)
            {
                // I-08：脚本运行期异常一律 fatal。
                var msg = $"StepAsync fatal: {ex}";
                EmueraLog.Error("jsonl", msg);
                var errorTurn = SerializeTurn(new TurnRecord(
                    diff: null,
                    // 运行期异常一律 fatal（Process 内部状态不可逆）⇒ awaiting 按终态构造。
                    // 不照抄 console.State：异常可能发生在 Running 态，那时按停稳点判据
                    // 会得到一个「既无 input 又无 wait」的非法等待（§1.2 规则 1）。
                    awaiting: AwaitInfo.Of(null, ConsoleState.Error, timedOut: false),
                    error: ex.Message,
                    errorInfo: ErrorInfo.From(ex)
                ));
                Stop();
                return errorTurn;
            }
        }

        /// <summary>
        /// TINPUT 超时专用路径，调用 EmueraConsole.SubmitTimeout() 后等待游戏进入
        /// WaitInput/Quit/Error 稳定状态再 BuildTurn，避免返回半运行态 turn。
        /// 仅由 server 模式的 Session 轮询线程调用；JSONL 管道模式不检查 InputTimeoutMs，
        /// 不会自动触发超时，客户端不发 input 时进程永久阻塞等待。
        /// I-08：原先同步 BuildTurn 拿快照，绕过 WaitForInputAsync 的 30s 超时与取消保护；
        /// 若 RunEmueraProgram 卡死整个 server 线程被阻塞。
        /// ADR-0016：进入此路径前置 _pendingTimeoutFlag=true，让下一帧 BuildTurn 写出
        /// `awaiting.wait.timedOut=true`，前端据此显示超时通知（issue 04 的启发式检测已删）。
        /// </summary>
        internal async Task<string?> SubmitTimeoutAsync()
        {
            _pendingTimeoutFlag = true;
            console.SubmitTimeout();
            if (!await WaitForInputAsync())
                return null;
            return BuildTurn();
        }

        /// <summary>
        /// 生成最终 turn（Quit/Error 状态），由 Session.GameLoop 结束时调用一次，
        /// 写入 SessionIO 供 GET /turn 取走。复用 BuildTurn 逻辑，保持 turn 结构一致。
        /// </summary>
        // 终帧：会话的最后一次交付（Quit/Error 状态转换），永不抑制。
        internal string? BuildFinalTurn() => BuildTurn(allowSuppression: false);

        /// <summary>执行超时处理并将 turn 写入 IO，返回 true。</summary>
        private async Task<bool> HandleTimeoutAndWriteAsync()
        {
            var turn = await SubmitTimeoutAsync();
            if (turn != null)
                _io.WriteLine(turn);
            return true;
        }

        /// <summary>
        /// JSONL 协议主循环（async）：读 input → Step → 写 turn，可选 TINPUT 超时处理。
        /// 由 server 模式的 Session（Task.Run(GameLoopAsync)）与 JSONL 管道模式
        /// （Program.RunHeadless，.GetAwaiter().GetResult()）共享。
        /// enableTimeout=true 启用 TINPUT 超时分支：每轮 linked CTS + CancelAfter，
        /// 超时抛 OperationCanceledException 后调 SubmitTimeout。
        /// enableTimeout=false 纯阻塞读取，TINPUT 不触发（管道 stdin 无可靠 timeout）。
        /// </summary>
        internal async Task RunLoopAsync(bool enableTimeout, CancellationToken externalCt)
        {
            var initialTurn = await GetInitialTurnAsync();
            if (initialTurn != null)
                _io.WriteLine(initialTurn);

            // spec §3.2 ④ 调用位置一：首帧之后。若脚本的第一个停稳点就是让路点
            // （会话一开始就是脚本动作 = 无参 AWAIT），这里立即把它 drain 掉。
            await DrainYieldsAsync();

            // 退出条件：外部取消 / Stop()（QUIT 经 GameExitException 路径）/ IO 关闭 /
            // 游戏已进入 Quit/Error 终态（部分 QUIT 路径不抛 GameExitException，StepAsync 正常返回
            // 而 IsStopped=false——此时若只依赖异常机制，循环会继续阻塞 ReadLineAsync 永不退出）。
            while (!externalCt.IsCancellationRequested && !IsStopped && _io.IsConnected
                   && !IsTerminalState(console.State))
            {
                // spec §3.2 ④ 调用位置二：循环体开头。放在此处可覆盖主循环里所有跳回顶部的
                // continue 路径（超时续跑 / 非法 jsonl / 非 input 命令）——它们都会经过这里。
                await DrainYieldsAsync();
                // drain 可能把脚本推到终态（QUIT 不抛 GameExitException 的那条路径）。循环条件
                // 只在顶部求值，这里必须补判一次，否则会在终态上阻塞读输入。
                if (IsTerminalState(console.State) || IsStopped || !_io.IsConnected)
                    break;

                string? line = null;
                if (enableTimeout)
                {
                    long? timeoutMs = console.InputTimeoutMs;

                    if (timeoutMs.HasValue && timeoutMs.Value <= 0)
                    {
                        await HandleTimeoutAndWriteAsync();
                        continue;
                    }

                    if (timeoutMs.HasValue)
                    {
                        // 每轮 linked CTS + CancelAfter，超时取消 ReadLineAsync
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, StopToken);
                        linkedCts.CancelAfter((int)timeoutMs.Value);
                        try
                        {
                            line = await _io.ReadLineAsync(linkedCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            // 外部取消或 Stop：退出循环
                            if (externalCt.IsCancellationRequested || IsStopped)
                                break;
                            // timeout 触发：调 SubmitTimeout 并继续
                            await HandleTimeoutAndWriteAsync();
                            continue;
                        }
                    }
                    else
                    {
                        line = await _io.ReadLineAsync(externalCt);
                    }

                    if (line == null)
                    {
                        // ReadLineAsync 返回 null = EOF/Channel 关闭
                        break;
                    }
                }
                else
                {
                    line = await _io.ReadLineAsync(externalCt);
                    if (line == null)
                        break;
                }

                JsonlCommand? cmd;
                try { cmd = JsonSerializer.Deserialize(line, EmueraJsonContext.Default.JsonlCommand); }
                catch (Exception ex) { AgentLog.Instance.Write("invalid jsonl input ignored: " + ex.Message); continue; }

                if (cmd?.type != "input")
                    continue;

                // spec-v3 §5.1/§5.3：先把捎带的状态字段写进 IInputHost，再决定派发与否。
                // 顺序要紧 —— 写状态必须**先于** DispatchInput，否则脚本本轮读到的仍是旧值。
                ApplyInputState(cmd);

                // 状态类（value 缺省）：只更新状态，不派发、不产帧。
                if (cmd.value == null)
                    continue;

                var stepTurn = await StepAsync(cmd.value);
                if (stepTurn != null)
                    _io.WriteLine(stepTurn);
                else
                    break;
            }
        }

        /// <summary>
        /// spec-v3 §5.1：把 <c>input</c> 帧的可选状态字段写进统一输入宿主（<see cref="IInputHost"/>）。
        /// <para>
        /// 全部字段都是<b>可选</b>的：一个都不带时本方法是空操作（旧客户端行为不变）。
        /// 宿主为 <see cref="NullInputHost"/> 时写入被丢弃 —— 骨架阶段无能力即无效果。
        /// </para>
        /// <para>
        /// <c>button</c>（1/2/4 = 左/右/中）与 Win32 虚拟键码 <c>VK_LBUTTON/RBUTTON/MBUTTON</c>
        /// 数值相同，故直接当键码用；一次点击合成 down+up 一对（电平归位，只留边沿戳）。
        /// </para>
        /// </summary>
        private void ApplyInputState(JsonlCommand cmd)
        {
            IInputHost host = ui.Input;

            if (cmd.focus is { } focused)
                host.NotifyFocusChanged(focused);

            if (cmd.geometry is { } g)
                host.SubmitGeometry(new EmuPoint(g.x, g.y));

            if (cmd.hoverValue != null)
                host.SubmitHover(cmd.hoverValue);

            if (cmd.button is { } button && button > 0 && button < 256)
            {
                host.SubmitKey(button, true);
                host.SubmitKey(button, false);
            }

            if (cmd.keys == null)
                return;
            foreach (var k in cmd.keys)
            {
                if (k == null || k.code < 0 || k.code >= 256)
                    continue;
                host.SubmitKey(k.code, k.down);
            }
        }

        /// <summary>
        /// B4（spec v5 §3.2 ④）：让路点 drain —— 无参 <c>AWAIT</c> / <c>AWAIT 0</c>（零时长让路点）
        /// 交付一帧后<b>同线程续跑</b>，直到脚本停在一个<b>真</b>等待上。
        ///
        /// <para>🔒 <b>终止性不变量</b>：本循环的终止点与改动前那次「连续执行」的终止点<b>完全相同</b> ——
        /// 我们只是把「一次跑到底」切成「跑一段 → 发帧 → 续跑」，<b>没有改变控制流的终止性</b>。
        /// 今天会死循环（无尽无参 <c>AWAIT</c> 且永不 WaitInput 于真输入）的脚本，改后照样死循环。</para>
        ///
        /// <para><b>让路帧参与抑制</b>（<c>allowSuppression: true</c>）：画面没变的那一轮不落 wire ——
        /// 这正是闸住让路帧洪水的安全阀（spec §3.4）；画面真变了的那一轮是<b>必须交付</b>的内容。</para>
        /// </summary>
        private async Task DrainYieldsAsync()
        {
            while (IsYieldPoint() && !IsStopped)
            {
                if (!await WaitForInputAsync())
                    return;
                var turn = BuildTurn(allowSuppression: true);
                if (turn != null)
                    _io.WriteLine(turn);
                // 同线程续跑：脚本执行与帧生成永远在同一线程、同一调用栈（IConsoleUI.Invoke 的无头实现
                // 是同步内联），drain 期间没有人并发改动画面，故 BuildTurn 的 ComputeDiff 无撕裂风险。
                console.EndYield();
            }
        }

        /// <summary>
        /// 让路点判据：State=WaitInput + <see cref="InputType.Void"/> + <b>无时长</b>。
        ///
        /// <para>这正是让路帧在 wire 上的可观测形态（spec §3.6：<c>state:"WaitInput"</c> +
        /// <c>inputType:"Void"</c> + <c>Timelimit&lt;=0</c>）——故判据与实际形态同源，
        /// 不引入第二处真相。刻意<b>不</b>再叠加内部标记 <c>InputRequest.IsYield</c>：
        /// 它只由 <c>ConsoleStateManager.Await(time&lt;=0)</c> 置位、与 <c>Timelimit=0</c> 恒等，
        /// 叠加它只会漏掉另一类零时长 Void 等待（<c>TWAIT 0,1</c>）——那类等待
        /// <c>InputTimeoutMs</c> 返回 null（无超时续跑）又不被判为让路点 ⇒ server 循环
        /// 阻塞在读输入上、脚本永久停住。</para>
        ///
        /// <para>🔒 <c>Timelimit &lt;= 0</c> 是安全阀，不可省：省掉会把<b>真</b>的定时等待
        /// （<c>TWAIT t,1</c> / <c>AWAIT t</c>：Void 且 <c>Timelimit &gt; 0</c>）误判为让路点，
        /// drain 会把它提前 <c>EndYield</c>，吞掉本次等待的输入 / 计时能力
        /// （「画面无变化时不发帧」「超时回归」用例即为此闸门）。</para>
        /// </summary>
        private bool IsYieldPoint() => IsAtYieldPoint();

        protected override void OnInputRejected(string reason)
        {
            _pendingRejectReason = reason;
        }

        #region Turn helpers

        /// <summary>游戏终态判定：Quit / Error——主循环应退出、输入等待应放行的稳定状态。</summary>
        private static bool IsTerminalState(ConsoleState state) =>
            state is ConsoleState.Quit or ConsoleState.Error;

        private async Task<bool> WaitForInputAsync()
        {
            var sw = Stopwatch.StartNew();
            var token = StopToken;
            while (!token.IsCancellationRequested)
            {
                var state = console.State;
                if (state == ConsoleState.WaitInput || IsTerminalState(state))
                    return true;
                if (sw.ElapsedMilliseconds > TurnTimeoutMs)
                    return false;
                try { await Task.Delay(PollIntervalMs, token); }
                catch (OperationCanceledException) { return false; }
            }
            return false;
        }

        /// <param name="isInitial">
        /// 首帧：携带协议版本，是客户端与服务端对齐的唯一机会，永不抑制。
        /// </param>
        /// <param name="allowSuppression">
        /// 是否允许空转帧抑制。仅「等待超时 → 自动续跑」那条路径允许 ——
        /// 空转帧<b>只</b>产自那里（客户端没发输入，是引擎自己推进的）；
        /// 而「客户端输入 → 回合」是应答链路，抑制了会让客户端对一次输入收不到回应，
        /// 且 StepAsync 的 null 返回在 RunLoopAsync 里被当作「退出循环」，语义冲突。
        /// </param>
        private string? BuildTurn(bool isInitial = false, bool allowSuppression = true)
        {
            // plan C（v5）：ComputeDiff 原子化刷新 _current 并 drain 分类权威清空信号，
            // 不再前置 TryUpdate（避免跨调用窗口 + 清空信号错配，见 DisplayState.ComputeDiff）。
            // ComputeDiff 与 _previous 比对产出 DisplayDiff；二者同处 _gate 锁，
            // 但 BuildTurn 是单线程（游戏循环）调用，无重入风险。
            var diff = _displayState.ComputeDiff();
            var req = console.CurrentRequest;
            string? error = _pendingRejectReason;
            _pendingRejectReason = null;

            // ADR-0016：读出超时 flag 并清空（一次性旗标），与 _pendingRejectReason 同模式
            bool timedOut = _pendingTimeoutFlag;
            _pendingTimeoutFlag = false;

            // issue-004：空转帧抑制。返回 null = 本回合不产出（调用方据此不写 IO）。
            //
            // v12：唯一契约字段（§2.2 的两条维度判据在工厂里，两个维度各自独立判定）。
            var awaiting = AwaitInfo.Of(req, console.State, timedOut);

            // v5-r2（§2.3）：指纹 = 「形状 + 两个维度各自的性质」——比 r1 的「kind + 一份载荷」更细，
            // 且**更直白**：input 的 type 与 wait 的 durationMs 直接参与比较。
            // 🔒 比较「性质」不得比较「实例」：每次等待都是新实例，比较实例会让抑制形同虚设
            // （直接打破 IdleFrameSuppressionTests）。
            string shape = DescribeShape(awaiting);
            string? inputProps = awaiting.input?.type;
            string? waitProps = DescribeWaitProps(awaiting.wait);

            if (allowSuppression && !isInitial && _hasEmittedTurn && IsEmptyDiff(diff)
                && shape == _emittedShape && inputProps == _emittedInputProps && waitProps == _emittedWaitProps)
                return null;

            _hasEmittedTurn = true;
            _emittedShape = shape;
            _emittedInputProps = inputProps;
            _emittedWaitProps = waitProps;

            // ADR-0016：TINPUT 计时元数据（timeLimit / displayTime / timeUpMessage）自 v12 起
            // 只挂在 `awaiting.wait` 上（由 AwaitInfo.Of 从同一个 req 填充），顶层不再重复。
            return SerializeTurn(new TurnRecord(
                diff: diff,
                awaiting: awaiting,
                error: error,
                protocolVersion: isInitial ? TurnRecord.CurrentProtocolVersion : null,
                generation: console.LastButtonGeneration
            ));
        }

        /// <summary>
        /// 抑制指纹的「形状」部分（spec §2.3）：哪个键出现。
        /// 终态带上 <c>terminal</c>——<c>Quit</c> 与 <c>Error</c> 都是终态但<b>不是</b>同一件事。
        /// </summary>
        private static string DescribeShape(AwaitInfo awaiting)
        {
            if (awaiting.ended is { } ended)
                return "ended:" + ended.terminal;
            return (awaiting.input != null ? "input" : "")
                   + (awaiting.wait != null ? "+wait" : "");
        }

        /// <summary>
        /// 抑制指纹的「wait 的性质」部分（spec §2.3）：durationMs + displayTime + timeUpMessage。
        /// 刻意<b>不</b>含 <c>timedOut</c>（一次性旗标，不是等待本身的性质；含它会让 B3 永远不可抑制）。
        /// <c>wait</c> 缺省 = 无限期，返回 null（此时没有任何「wait 的性质」参与比较）。
        ///
        /// <para>issue 10 后指纹里不再有 <c>interruptible</c> —— 该键已从契约撤回，
        /// 「AWAIT 与 TWAIT t,1 同形」也因此反映在抑制判据上（两者不再因它产生假差异）。</para>
        /// </summary>
        private static string? DescribeWaitProps(AwaitWait? wait)
            => wait == null
                ? null
                : $"{wait.durationMs}|{wait.displayTime}|{wait.timeUpMessage}";

        /// <summary>
        /// issue-004：diff 是否为空 —— 无行级操作、无背景色变更、无背景图变更。
        /// <c>null</c>（画面完全未变）同样视为空。
        /// </summary>
        private static bool IsEmptyDiff(DisplayDiff? diff)
            => diff == null
               || (diff.lineOps.Count == 0 && diff.bgColor == null && diff.bgImages == null);

        #endregion
    }
}
