using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
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
        private static string SerializeTurn(TurnRecord turn)
            => JsonSerializer.Serialize(turn, (JsonTypeInfo<TurnRecord>)TurnJsonOptions.GetTypeInfo(typeof(TurnRecord)));

        private readonly SessionIO _io;
        private readonly DisplayState _displayState;
        private string? _pendingRejectReason;
        /// <summary>
        /// ADR-0016：TINPUT 超时 flag——SubmitTimeoutAsync 路径置 true，
        /// 下次 BuildTurn 写出后清空。复用 _pendingRejectReason 同样的"一次性旗标"模式。
        /// RunLoopAsync 的 OperationCanceledException 分支调 HandleTimeoutAndWriteAsync
        /// → SubmitTimeoutAsync → 此处置 true → BuildTurn 读出写入 turn.timedOut → 清空。
        /// </summary>
        private bool _pendingTimeoutFlag;

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

            return BuildTurn(isInitial: true);
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

                return BuildTurn();
            }
            catch (GameExitException)
            {
                // QUIT/EXIT：正常退出。
                // 不能走下方 catch-all——那会构造 state=Running + error 文本的回合，
                // 前端画面不变、无可见错误，表现为假死。这里构造干净 Quit 回合并结束会话。
                var quitTurn = SerializeTurn(new TurnRecord(
                    state: "Quit",
                    inputType: null,
                    needValue: false,
                    diff: null
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
                    state: console.State.ToString(),
                    inputType: null,
                    needValue: false,
                    diff: null,
                    error: ex.Message
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
        /// turn.timedOut=true，前端据此清空启发式检测（issue 04 已删，改消费 timedOut 旗标）。
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
        internal string? BuildFinalTurn() => BuildTurn();

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

            // 退出条件：外部取消 / Stop()（QUIT 经 GameExitException 路径）/ IO 关闭 /
            // 游戏已进入 Quit/Error 终态（部分 QUIT 路径不抛 GameExitException，StepAsync 正常返回
            // 而 IsStopped=false——此时若只依赖异常机制，循环会继续阻塞 ReadLineAsync 永不退出）。
            while (!externalCt.IsCancellationRequested && !IsStopped && _io.IsConnected
                   && !IsTerminalState(console.State))
            {
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

                var stepTurn = await StepAsync(cmd.value ?? "");
                if (stepTurn != null)
                    _io.WriteLine(stepTurn);
                else
                    break;
            }
        }

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

        private string BuildTurn(bool isInitial = false)
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

            // ADR-0016：TINPUT timer 字段——仅 TINPUT 期间（Timelimit > 0）填充。
            // displayTime 取 InputRequest.DisplayTime（ERB 可设 false 让前端不显示倒计时）。
            // timeUpMessage 仅在非空时携带。
            long? timeLimit = req is { Timelimit: > 0 } ? req.Timelimit : null;
            bool? displayTime = req is { Timelimit: > 0, DisplayTime: true } ? true : null;
            string? timeUpMessage = req is { Timelimit: > 0 } reqWithMes
                && !string.IsNullOrEmpty(reqWithMes.TimeUpMes)
                ? reqWithMes.TimeUpMes
                : null;

            return SerializeTurn(new TurnRecord(
                state: console.State.ToString(),
                inputType: req?.InputType.ToString(),
                needValue: req?.NeedValue ?? false,
                diff: diff,
                error: error,
                protocolVersion: isInitial ? TurnRecord.CurrentProtocolVersion : null,
                timeLimit: timeLimit,
                displayTime: displayTime,
                timeUpMessage: timeUpMessage,
                timedOut: timedOut,
                generation: console.LastButtonGeneration
            ));
        }

        #endregion
    }
}
