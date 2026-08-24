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
    private EmueraConsole? _console;
    private DisplayState? _displayState;
    private AgentJsonlProtocol? _protocol;
    private Task? _gameTask;
    private readonly CancellationTokenSource _cts = new();
    /// <summary>turn 投递深模块：独占长轮询/抢占/final-turn-once 竞态不变式（含消费侧锁）。</summary>
    private readonly TurnDelivery _delivery;
    private bool _disposed;
    /// <summary>
    /// Issue 11 D1：加载中标志——Session.Start() 设 true，runLoop 回调首行设 false。
    /// volatile：HTTP 线程（/load-game / GET /state / GET /snapshot）读、游戏循环 Task 线程写。
    /// </summary>
    private volatile bool _loading;

    public Session(HttpSessionIO io, ITerminalSetup terminalSetup, ConfigData configData, TimeSpan? agentLease = null, bool fullDiffOnFirstTurn = false)
    {
        _io = io;
        _configData = configData;
        _terminalSetup = terminalSetup;
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
                    // 产出最终 turn（可失败/为空），保序交 TurnDelivery.EndOfGame
                    // （内部：置 ready → 写 final turn → End(game_ended) → Close）。
                    string? finalTurn = null;
                    try
                    {
                        finalTurn = _protocol!.BuildFinalTurn();
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
            });
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
        if (_disposed)
            return;

        _disposed = true;
        // 会话拆除交 TurnDelivery（内部 End(session_ended) + Close[即 _io.Close]，保留 Controller 归属权）
        _delivery.Dispose();
        _cts.Cancel();
        _protocol?.Stop();
        _console?.Dispose();

        // 等待游戏循环（含其 finally 中的清理）彻底结束，避免旧 loop 仍在使用 scope 时
        // 提前释放/复用。scope 在 GameLoopAsync 的 using 块退出时自动 Dispose，
        // join 确保游戏循环一定先退出，scope 释放时无 dangling 读取。
        try
        {
            _gameTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AgentLog.Instance.Write("session game loop ended with error during dispose: " + ex);
        }
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
