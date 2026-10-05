using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

internal sealed class Session : IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public bool IsRunning => _gameTask != null && !_gameTask.IsCompleted;
    /// <summary>游戏循环是否已结束（runLoop 回调 finally 置位）。</summary>
    private volatile bool _hasEnded;
    public bool HasEnded => _hasEnded;

    /// <summary>
    /// 游戏循环最后一次未料异常（如 SAVEGLOBAL IOException）。崩溃/退出因果的诊断字段，
    /// GET /state 据此透出 lastError；null 表示无未料异常（正常 Quit 不记录）。
    /// volatile：游戏循环 Task 线程写、HTTP 线程读（对标 _hasEnded）。
    /// </summary>
    private volatile string? _lastError;
    public string? LastError => _lastError;
    public HttpSessionIO IO => _io;
    public Controller Controller { get; }
    /// <summary>
    /// Issue 11 D1：当前显示状态字符串。
    /// - <c>"Loading"</c>：Session.Start() 已排 GameLoopAsync 到独立 Task，但 runLoop 回调尚未启动
    ///   （即 <c>console.Initialize()</c>——含 Preload.Load + Process.Initialize——仍在跑）。
    ///   /load-game 同步返回时 _loading=true，前端据此显示"加载中…"而非"Idle"。
    /// - <c>_console.State.ToString()</c>：runLoop 启动后 _loading=false，状态由 EmueraConsole 维护
    ///   （Running / WaitInput / Quit / Error）。
    /// - <c>"Idle"</c>：理论上不会到达（StateString 仅在 session 已存在时被读），保留作兜底。
    /// </summary>
    public string StateString => _loading ? "Loading" : (_console?.State.ToString() ?? "Idle");
    public bool IsFinalTurnDelivered => _delivery.IsFinalTurnDelivered;

    /// <summary>
    /// 生成当前显示状态的全量快照 JSON（ADR-0013 决策一/二）。
    /// 供 GET /snapshot 端点调用——WS 晚加入者先调此端点拿初始状态，再订阅 WS 收增量 diff。
    ///
    /// Phase 1：改用 Session 持有的单个 DisplayState 实例（_displayState），经 Current → TryUpdate
    /// 保证快照随游戏打印实时推进，且与 BuildTurn 同步（BuildTurn 调 TryUpdate 刷新 _current）。
    /// Phase 5-3：TryUpdate 消费式清空 _pendingOps，不再由 BuildTurn 调 TakePendingOps。
    ///
    /// session 未初始化（_displayState==null，通常发生在 POST /session 后极短时间内）→ 返回 null，
    ///   调用方返回 503；
    /// session 运行中或已结束（HasEnded=true，_console 已 Dispose 但 DisplayLineList 仍可读）→ 返回快照 JSON。
    /// </summary>
    public string? GetDisplaySnapshot()
    {
        var state = _displayState;
        if (state == null)
            return null;
        return state.GetSnapshotJson();
    }

    private readonly HttpSessionIO _io;
    private readonly ConfigData _configData;
    private readonly ITerminalSetup _terminalSetup;
    private readonly bool _fullDiffOnFirstTurn;
    /// <summary>会话级方言 profile id（实现票 25）。null = v24pure（移植前行为）。</summary>
    private readonly string? _dialectProfileId;
    private EmueraConsole? _console;
    private DisplayState? _displayState;
    private AgentJsonlProtocol? _protocol;
    private Task? _gameTask;
    private readonly CancellationTokenSource _cts = new();
    /// <summary>turn 投递深模块：独占长轮询/抢占/final-turn-once 竞态不变式（含消费侧锁）。</summary>
    private readonly TurnDelivery _delivery;
    private bool _disposed;
    /// <summary>Controller 已释放（<see cref="ReleaseController"/> 幂等守卫）。</summary>
    private bool _controllerReleased;
    /// <summary>
    /// Issue 11 D1：加载中标志——Session.Start() 设 true，runLoop 回调首行设 false。
    /// volatile：HTTP 线程（/load-game / GET /state / GET /snapshot）读、游戏循环 Task 线程写。
    /// </summary>
    private volatile bool _loading;

    public Session(HttpSessionIO io, ITerminalSetup terminalSetup, ConfigData configData, TimeSpan? agentLease = null, bool fullDiffOnFirstTurn = false, string? dialectProfileId = null)
    {
        _io = io;
        _configData = configData;
        _terminalSetup = terminalSetup;
        _dialectProfileId = dialectProfileId;
        Controller = new Controller(agentLease);
        _delivery = new TurnDelivery(io, Controller);
        _fullDiffOnFirstTurn = fullDiffOnFirstTurn;
        // 注意：HeadlessConsole / EmueraConsole / AgentJsonlProtocol 的构造推迟到
        // GameLoopAsync 内、GlobalStatic.OpenScope 之后——它们构造时读 Config.*（候选 2/ADR-0009
        // 后 Config 仅经 scope 注入），scope 未开即构造会 NPE（POST /session 500）。
        // 此处的 Start() 仅把游戏循环排到独立 Task，构造留待 scope 内。
    }

    public void Start()
    {
        // Issue 11 D1：进入 Task.Run 前置 _loading=true——/load-game 同步返回时
        // GameLoopAsync 可能尚未跑到 console.Initialize()，但 _loading 已可见（volatile）。
        // runLoop 回调首行（console.Initialize 完成后）置 false。
        _loading = true;
        _gameTask = Task.Run(GameLoopAsync);
    }

    private async Task GameLoopAsync()
    {
        // 失败可见切片（handoff-nativeaot-parallel-erb §6.2）：RunAsync 内 await console.Initialize()
        // 与 runLoop 回调之间没有保护——初始化阶段（OpenScope / console 构造 / Initialize）抛出时，
        // 若不在此收束，_gameTask 静默 fault、_loading 永久为 true、前端永远 Loading。此 catch 仅作
        // 最后防线；正常失败路径由 ConsoleStateManager 置 State=Error、runLoop 回调内 catch 处理。
        try
        {
            await GameLoopComposer.RunAsync(
                _configData,
                _terminalSetup,
                (console, ui, ts) =>
                {
                    _console = console;
                    // Phase 1：单个 DisplayState 实例由 Session 持有，注入 AgentJsonlProtocol。
                    // HTTP 线程上 Config.Current 不可用（AsyncLocal 仅在游戏循环 task 设置），
                    // 直接从 ConfigData 读默认字体名，传给 DisplayState 避免 BuildPrintOpsForLine 访问 Config.FontName 时 NRE。
                    var defaultFontName = _configData.GetConfigValue<string>(ConfigCode.FontName) ?? "";
                    // MAUI 托管模式（fullDiffOnFirstTurn=true）：WebView 桥接无 GET /snapshot 端点为入口，
                    // 首帧 diff 是唯一画面来源——ComputeDiff 首帧返回全量 AppendLinesOp 而非 null。
                    // HTTP 模式靠 GET /snapshot 拿画面，首帧 diff=null 正确（默认 false）。
                    _displayState = new DisplayState(_console, defaultFontName, _fullDiffOnFirstTurn);
                    _protocol = new AgentJsonlProtocol(_console, ui, _io, _displayState);
                    return _protocol;
                },
                async p =>
                {
                    // Issue 11 D1：runLoop 被 GameLoopComposer 调用时 console.Initialize() 已完成
                    // （无论成败——ConsoleStateManager.Initialize 失败时置 State=Error 并 return，不抛）。
                    // 故 runLoop 开始即加载结束，立即置 _loading=false 让 StateString 返真实状态。
                    _loading = false;
                    try
                    {
                        await ((AgentJsonlProtocol)p).RunLoopAsync(enableTimeout: true, _cts.Token);
                    }
                    catch (GameExitException)
                    {
                        // 脚本 QUIT/EXIT：正常终结 session，走 finally 清理（BuildFinalTurn/IO.Close）
                    }
                    catch (Exception ex)
                    {
                        AgentLog.Instance.Write("session game loop exception: " + ex);
                        // 目标 3：记录 LastError，GET /state 透出 lastError（threadAlive=false 的因果）。
                        _lastError = ex.ToString();
                        // 3.3（NativeAOT）：JsonObject.ToJsonString()——匿名类型在 AOT 下反射序列化被禁用。
                        // 目标 1：errorInfo 结构化（type/message/inner），agent 一次定位；error 保留完整串兼容既有消费者。
                        var structured = ErrorInfo.From(ex);
                        _io.WriteLine(new JsonObject
                        {
                            ["error"] = ex.ToString(),
                            ["errorInfo"] = structured?.ToJsonObject(),
                            ["state"] = _console!.State.ToString()
                        }.ToJsonString());
                    }
                    finally
                    {
                        FinalizeEndedLoop();
                    }
                },
                _dialectProfileId);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested || _disposed)
        {
            // Dispose/服务器关闭导致的初始化期取消：session_ended 已由 TurnDelivery/TurnDelivery.Dispose
            // 表达，不发错误回合，只收束本地标志防止 StateString 停在 "Loading"。
            AgentLog.Instance.Write("session game loop canceled during initialization");
            _loading = false;
            _hasEnded = true;
        }
        catch (Exception ex)
        {
            DeliverInitStageFailure(ex);
        }
    }

    /// <summary>
    /// 初始化阶段失败的干净收束：与 runLoop 回调内 catch/finally 同构——记 LastError、
    /// 向 IO 写结构化 error 行（前端由此读到错误原因）、派发 final turn、Dispose 控制台并置位 HasEnded。
    /// </summary>
    private void DeliverInitStageFailure(Exception ex)
    {
        AgentLog.Instance.Write("session game loop exception (init stage): " + ex);
        _lastError = ex.ToString();
        _loading = false;
        var structured = ErrorInfo.From(ex);
        try
        {
            // NativeAOT：JsonObject.ToJsonString()，禁用匿名类型反射序列化（同 runLoop 回调内约定）。
            _io.WriteLine(new JsonObject
            {
                ["error"] = ex.ToString(),
                ["errorInfo"] = structured?.ToJsonObject(),
                ["state"] = _console?.State.ToString() ?? "Idle"
            }.ToJsonString());
        }
        catch (Exception wex)
        {
            // IO 已断开等写失败不阻断清理——LastError/HasEnded 已保证 /state 侧可诊断。
            AgentLog.Instance.Write("failed to deliver init-stage error line: " + wex.Message);
        }
        FinalizeEndedLoop();
    }

    /// <summary>
    /// 游戏循环终结的公共收尾（原 runLoop 回调 finally 的主体）：产出最终 turn（可失败/为空）、
    /// 保序交 TurnDelivery.EndOfGame、停协议、Dispose 控制台并置位 HasEnded。
    /// scope 由 GameLoopComposer 的 using 块退出时 Dispose（Pfc.Dispose + Current 归 null），RAII 单一归属。
    /// </summary>
    private void FinalizeEndedLoop()
    {
        string? finalTurn = null;
        try
        {
            finalTurn = _protocol?.BuildFinalTurn();
        }
        catch (Exception ex)
        {
            // 最终 turn 生成失败不阻断 Dispose；HasEnded 会让 GET /turn 返回 404
            AgentLog.Instance.Write("final turn build failed: " + ex.Message);
        }
        _delivery.EndOfGame(finalTurn);
        _protocol?.Stop();
        _console?.Dispose();
        _hasEnded = true;
        // scope 在 composer 的 using 块退出时自动 Dispose（Pfc.Dispose + Current 归 null）。
        // 旧 Reset() 已删除——RAII 单一归属。
    }

    /// <summary>
    /// 异步等待一个 turn。用于 GET /turn 长轮询，转交 <see cref="TurnDelivery"/>。
    /// - 拿到 turn（含 finalTurn）→ Turn；Time out → Timeout（204）；session 结束 → Closed（404）
    /// - externalCt 取消（服务器关闭）→ 抛 OperationCanceledException
    /// - 控制权中途易主 → ControlLost（409），已取 turn 回滚给新持有者
    /// </summary>
    public Task<TurnWaitResult> WaitForTurnAsync(int timeoutMs, CancellationToken externalCt)
    {
        return _delivery.WaitForTurnAsync(timeoutMs, externalCt);
    }

    public Task<TurnWaitResult> WaitForTurnAsync(
        int timeoutMs,
        ControlIdentity identity,
        CancellationToken externalCt)
    {
        return _delivery.WaitForTurnAsync(timeoutMs, identity, externalCt);
    }

    public void Dispose()
    {
        // 保留 IDisposable 契约：拆除请求 + 无上界等待。
        // 内部复用有界等待的实现（Timeout.InfiniteTimeSpan 即无上界）。
        RequestTeardown();
        WaitForLoopExitAsync(Timeout.InfiniteTimeSpan).GetAwaiter().GetResult();
        ReleaseController();
    }

    /// <summary>
    /// 发出拆除请求并**立即返回**（不等待游戏循环）。
    ///
    /// issue-001：原 <see cref="Dispose"/> 内含同步 join（<c>_gameTask.GetAwaiter().GetResult()</c>），
    /// 而 join 发生在 <c>SessionRegistry</c> 的会话锁内。游戏循环线程若正阻塞在长回合脚本里
    /// （无 AWAIT 等让路点，见 test_game/erb/LONGLOOP.ERB），join 永不返回且锁一直被占——
    /// 所有需要该锁的端点全部无限阻塞，server 整体失去响应。
    ///
    /// 拆成两段的意义：调用方先在锁内做「发拆除请求 + 摘引用」（本方法，微秒级），
    /// 再在锁外做有界等待（<see cref="WaitForLoopExitAsync"/>），从而让其他端点恢复可响应。
    ///
    /// 幂等：重复调用只生效一次。
    /// </summary>
    public void RequestTeardown()
    {
        if (_disposed)
            return;

        _disposed = true;
        // 会话拆除交 TurnDelivery（内部 End(session_ended) + Close[即 _io.Close]，保留 Controller 归属权）
        _delivery.Dispose();
        _cts.Cancel();
        _protocol?.Stop();
        _console?.Dispose();
    }

    /// <summary>
    /// 有界等待游戏循环（含其 finally 中的清理与 scope 释放）彻底结束。
    ///
    /// 返回 <c>true</c> = 游戏循环已退出（<paramref name="timeout"/> 内）；
    /// <c>false</c> = 超时，循环仍在运行 —— 调用方<b>不得</b>在此状态下启动新游戏循环
    /// （旧循环仍会读 <see cref="GamePaths.Current"/> 等进程级静态，而新循环初始化会清掉它们）。
    ///
    /// 超时<b>不</b>抛异常也不硬杀线程：脚本层当前无中断检查点，唯一的收敛途径是让它自己跑完。
    /// 失败以返回值表达，调用方据此回明确错误（spec 决策一/二）。
    /// </summary>
    public async Task<bool> WaitForLoopExitAsync(TimeSpan timeout)
    {
        var task = _gameTask;
        if (task == null || task.IsCompleted)
            return true;

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            // 无上界：保留原 Dispose 语义（含游戏循环的异常传播与记录）
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AgentLog.Instance.Write("session game loop ended with error during dispose: " + ex);
            }
            return true;
        }

        // 有上界：Task.WhenAny 而非 WaitAsync —— 后者在超时后抛
        // OperationCanceledException/TimeoutException，前者只需一个 bool。
        var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished == task)
        {
            // 已退出：观察其异常，避免未观察异常在 continuation 触发。
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AgentLog.Instance.Write("session game loop ended with error during teardown: " + ex);
            }
            return true;
        }

        AgentLog.Instance.Write(
            $"session game loop did not exit within {timeout.TotalSeconds:F1}s; " +
            "a long-running turn is still executing. New session creation is refused until it exits.");
        return false;
    }

    /// <summary>游戏循环已确认退出后释放控制权。超时路径下不调用——控制器状态需留给仍存活的循环。</summary>
    private void ReleaseController()
    {
        if (_controllerReleased)
            return;
        _controllerReleased = true;
        Controller.Dispose();
    }

    public ControlAcquireSessionResult AcquireControl(ControlIdentity identity)
    {
        var result = Controller.Acquire(identity);
        if (result.Status != ControlAcquireStatus.Acquired)
            return new ControlAcquireSessionResult(result, null, 0);

        // 排水转交 TurnDelivery（内含 _turnAccessLock 边界）
        var (advanced, last) = _delivery.DrainOnAcquire();
        return new ControlAcquireSessionResult(result, last, advanced);
    }

    /// <summary>测试和 WS 接缝使用的身份判定。</summary>
    public bool IsCurrentController(ControlIdentity identity) => Controller.IsCurrent(identity);
}

internal readonly record struct ControlAcquireSessionResult(
    ControlAcquireResult Control,
    string? Turn,
    int TurnsAdvanced);
