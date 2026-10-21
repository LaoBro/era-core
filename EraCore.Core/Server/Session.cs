using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;

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
    /// <summary>
    /// 会话级统一输入宿主（ADR-0031 / spec-v3 M2）。会话独占 —— 换游戏重建 Session 时
    /// 旧宿主随会话一起丢弃，天然消除 <c>keytoggle</c> 时代的跨会话状态泄漏。
    /// M2 声明 <see cref="InputCapability.KeyboardState"/>；M5a 追加 <see cref="InputCapability.Hover"/>
    /// （Web 前端上报真实 <c>mouseenter</c>/<c>mouseleave</c> ⇒ <c>MOUSEB()</c> 有真悬停值）；
    /// M6 追加 <see cref="InputCapability.Focus"/>（前端 <c>blur</c>/<c>focus</c> 上报 ⇒
    /// <c>ISACTIVE</c> 有真值，且失焦自动清键）。
    /// <c>Geometry</c> 待 M5b（Web 无坐标语义）。
    /// </summary>
    private readonly DefaultInputHost _inputHost = new(
        InputCapability.KeyboardState | InputCapability.Hover | InputCapability.Focus);
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
    /// <summary>测试接缝：替代真实游戏循环的任务（null = 生产路径）。见 internal 构造。</summary>
    private readonly Task? _loopOverride;

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

    /// <summary>
    /// 测试接缝（ADR-0049 D4）：用给定任务替代真实游戏循环，供 registry 的相位迁移
    /// （timeout → Retiring → 拒绝建会话）确定性复现。
    /// <para>
    /// 接缝刻意只有<b>一个 <see cref="Task"/> 引用</b>——与 <see cref="BoundedLoopWait"/> 同一手法；
    /// 不引入 <c>ISession</c> 接口（成员数会逼近实现，属浅模块）。生产路径不得使用本构造。
    /// </para>
    /// <para>
    /// 注意：注入的循环不会走 <c>FinalizeEndedLoop</c>，故 <see cref="HasEnded"/> 恒为 false、
    /// <see cref="StateString"/> 恒为 <c>Loading</c>。用例据此只断言「循环是否退出」相关语义。
    /// </para>
    /// </summary>
    internal Session(HttpSessionIO io, ITerminalSetup terminalSetup, ConfigData configData, TimeSpan? agentLease, bool fullDiffOnFirstTurn, string? dialectProfileId, Task loop)
        : this(io, terminalSetup, configData, agentLease, fullDiffOnFirstTurn, dialectProfileId)
        => _loopOverride = loop ?? throw new ArgumentNullException(nameof(loop));

    public void Start()
    {
        // Issue 11 D1：进入 Task.Run 前置 _loading=true——/load-game 同步返回时
        // GameLoopAsync 可能尚未跑到 console.Initialize()，但 _loading 已可见（volatile）。
        // runLoop 回调首行（console.Initialize 完成后）置 false。
        _loading = true;
        _gameTask = _loopOverride ?? Task.Run(GameLoopAsync);
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
                        // 目标 1：errorInfo 结构化（type/message/inner），agent 一次定位；error 保留完整串兼容既有消费者。
                        // v12：改用共享构造点——终态形状 awaiting.ended{terminal:"Error"}（原先手拼 JsonObject
                        // 带旧顶层 state 且无 awaiting，前端会判为协议解析失败，真实错误反而看不到）。
                        _io.WriteLine(TurnFrames.Error(ex.ToString(), ErrorInfo.From(ex)));
                    }
                    finally
                    {
                        FinalizeEndedLoop();
                    }
                },
                _dialectProfileId,
                _inputHost);
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
        try
        {
            // v12：与 runLoop 回调内 catch 同一条共享构造点（终态 awaiting.ended{terminal:"Error"}）。
            _io.WriteLine(TurnFrames.Error(ex.ToString(), ErrorInfo.From(ex)));
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
    ///
    /// <para><b>拆除顺序的唯一编排点</b>（ADR-0049 D1）：控制权终止（<c>Controller.End</c>）由
    /// <see cref="TurnDelivery.Dispose"/> 在置拆除旗标<b>之后</b>发出。调用方不得自行先调
    /// <c>Controller.End</c>——那会在「End 已发布、旗标未置」的窗口里让在途 GET /turn
    /// 把 404 误报成 409。</para>
    /// </summary>
    /// <param name="reason">控制权终止原因：<c>session_ended</c>（删除/关停）或 <c>session_replaced</c>（换游戏/重开）。</param>
    public void RequestTeardown(string reason = "session_ended")
    {
        if (_disposed)
            return;

        _disposed = true;
        // 会话拆除交 TurnDelivery（置旗标 → End(reason) → Close[即 _io.Close]，保留 Controller 归属权）
        _delivery.Dispose(reason);
        // issue-002：把停止信号送达脚本层 —— 此前没有任何一步触及脚本的运行判据，
        // 脚本唯一的出口是它自己跑到 QUIT/ERROR，拆除只能干等。
        // 发出后 IsRunning 恒假，脚本在下一个指令边界停下（ScriptProc.Run 的循环体首部）。
        _console?.RequestStop();
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
    /// 超时<b>不</b>抛异常也不硬杀线程：issue-002 之后脚本层已有中断检查点，绝大多数长回合
    /// 会在下一个指令边界自行收敛；issue-003 之后连 <c>AWAIT</c> 也走回合边界。
    /// 换言之，脚本层已无「外部插不上手」的窗口 —— 本分支如今是<b>兜底</b>：
    /// 进程级异常、单条指令内部的原生阻塞等仍可能撞上它，故机制必须保留。
    /// 失败以返回值表达，调用方据此回明确错误（spec 决策一/二）。
    ///
    /// 等待与异常观察本身收在 <see cref="BoundedLoopWait"/>（窄接缝 = 一个 Task），
    /// 使「超时」分支可用永不完成的任务确定性覆盖 —— 集成层面已构造不出该窗口。
    /// </summary>
    public async Task<bool> WaitForLoopExitAsync(TimeSpan timeout)
    {
        bool unbounded = timeout == Timeout.InfiniteTimeSpan;
        var exited = await BoundedLoopWait.ForCompletionAsync(_gameTask, timeout, ex =>
            AgentLog.Instance.Write(unbounded
                ? "session game loop ended with error during dispose: " + ex
                : "session game loop ended with error during teardown: " + ex)).ConfigureAwait(false);

        if (!exited)
            AgentLog.Instance.Write(
                $"session game loop did not exit within {timeout.TotalSeconds:F1}s; " +
                "a long-running turn is still executing. New session creation is refused until it exits.");
        return exited;
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
