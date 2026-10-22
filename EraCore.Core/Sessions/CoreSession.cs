using MinorShift.Emuera.Files;
using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.Sessions;

/// <summary>
/// 组装根：把三轴（<see cref="Input"/> / <see cref="Output"/> / <see cref="Files"/>）与游戏循环
/// 装成一个可启动单元（issue-006 / spec §3.3）。
///
/// <para><b>公共两阶段用法</b>：构造（只给宿主能理解的参数）→ <see cref="StartAsync"/> →
/// 只经三轴读写 → <see cref="StopAsync"/>。构造权留在 Core：宿主拿不到输入宿主、显示状态或广播中枢。</para>
///
/// <para><b>与协议层的关系</b>：HTTP / JSONL / WS 仍在 Server 侧，不属于三轴。本类只负责
/// 「游戏循环 + 三轴」的装配；Server 的 <see cref="SessionRegistry"/> 经 internal 构造复用同一实现，
/// 自行持有 <see cref="OutputHub"/> 与 <see cref="HttpSessionIO"/>。</para>
///
/// <para><b>停止后语义</b>：<see cref="State"/> 变为 <see cref="CoreSessionState.Stopped"/>；
/// <see cref="Input"/> 的提交被静默丢弃（IO 已关闭）；<see cref="Output"/> 仍可读终止画面；
/// <see cref="Files"/> 始终可用。重复 <see cref="StopAsync"/> 幂等。</para>
/// </summary>
public sealed class CoreSession : IDisposable
{
    /// <summary>会话标识（短 GUID）。</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>输入轴门面。构造后即可用；停止后提交被丢弃。</summary>
    public InputPort Input { get; }

    /// <summary>
    /// 输出轴门面。仅在 <see cref="StartAsync"/> 成功返回后可用（游戏循环初始化完成）。
    /// 停止后仍可读取终止画面。
    /// </summary>
    public OutputPort Output => _outputPort
        ?? throw new InvalidOperationException("输出轴在 StartAsync 成功后才可用。");

    /// <summary>文件轴门面（以游戏根为界）。构造后即可用，与游戏循环状态无关。</summary>
    public GameTree Files { get; }

    /// <summary>当前会话状态。</summary>
    public CoreSessionState State
    {
        get
        {
            if (_failed)
                return CoreSessionState.Failed;
            if (_gameTask == null)
                return _stopRequested ? CoreSessionState.Stopped : CoreSessionState.Created;
            if (_hasEnded)
            {
                if (_stopRequested)
                    return CoreSessionState.Stopped;
                if (_lastError != null || _console?.State == ConsoleState.Error)
                    return CoreSessionState.Failed;
                return CoreSessionState.Completed;
            }
            if (_loading)
                return CoreSessionState.Starting;
            return _console?.State switch
            {
                ConsoleState.Running => CoreSessionState.Running,
                ConsoleState.WaitInput => CoreSessionState.WaitingForInput,
                ConsoleState.Quit => CoreSessionState.Completed,
                ConsoleState.Error => CoreSessionState.Failed,
                _ => CoreSessionState.Starting,
            };
        }
    }

    /// <summary>最近一次未料异常（初始化失败 / 游戏循环崩溃）。null = 无错误。</summary>
    public string? LastError => _lastError;

    internal bool IsRunning => _gameTask != null && !_gameTask.IsCompleted;
    /// <summary>游戏循环是否已结束（runLoop 回调 finally 置位）。</summary>
    internal bool HasEnded => _hasEnded;
    internal HttpSessionIO IO => _io;
    internal Controller Controller { get; }
    /// <summary>
    /// 供 Server 的字符串协议沿用（Loading / Running / WaitInput / Quit / Error）。
    /// 公共宿主应使用 <see cref="State"/>。
    /// </summary>
    internal string StateString => _loading ? "Loading" : (_console?.State.ToString() ?? "Idle");
    internal bool IsFinalTurnDelivered => _delivery.IsFinalTurnDelivered;

    /// <summary>当前会话的协议，供 CLI 宿主等待循环结束时判定 Aborted。</summary>
    internal bool IsAborted { get; private set; }

    /// <summary>初始化阶段抛出/捕获的原始异常（供 CLI 保留原有诊断与退出码）。</summary>
    internal Exception? StartupException => _startupException;

    internal string? GetDisplaySnapshot()
    {
        var state = _displayState;
        if (state == null)
            return null;
        return state.GetSnapshotJson();
    }

    private readonly HttpSessionIO _io;
    private readonly OutputHub? _hub;
    private ConfigData _configData;
    private ITerminalSetup _terminalSetup;
    private readonly bool _standalone;
    private readonly string? _gameRoot;
    private readonly IGameDirAccessor? _dirAccessor;
    private readonly bool _fullDiffOnFirstTurn;
    private readonly string? _dialectProfileId;
    private readonly IInputHost _inputHost;
    private readonly TimeSpan _teardownTimeout;
    private Action<string> _dispatchValue;
    private readonly CoreSessionProtocolFactory? _protocolFactory;
    private readonly Func<AgentProtocolBase, Task>? _runLoop;

    /// <summary>
    /// 本会话的游戏根（issue-007 / issue-009）。<see cref="GameRootEnvironment"/> 的唯一建立者是组装根，
    /// 而建立点 <see cref="GameLoopAsync"/> 运行在 <c>Task.Run</c> 之后的独立流里——
    /// 所以根必须在构造/启动时就钉在本字段上，不能等到循环流里再读视图。
    /// 公共路径在 <see cref="StartAsync"/> 内校验后赋值；宿主路径取装配好的 <see cref="ConfigData.Root"/>。
    /// </summary>
    private GamePaths? _root;

    private EmueraConsole? _console;
    private DisplayState? _displayState;
    private OutputPort? _outputPort;
    private AgentProtocolBase? _protocol;
    private AgentJsonlProtocol? _jsonlProtocol;
    private Task? _gameTask;
    private Task<GameLoopResult>? _loopAwaiter;
    private readonly CancellationTokenSource _cts = new();
    private readonly TurnDelivery _delivery;
    private bool _disposed;
    private bool _controllerReleased;
    private volatile bool _loading;
    private volatile bool _hasEnded;
    private volatile bool _stopRequested;
    private volatile bool _failed;
    private volatile string? _lastError;
    private Exception? _startupException;
    private readonly Task? _loopOverride;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _startGate = new(1, 1);

    /// <summary>
    /// 公共组装入口。参数只包含宿主能理解的输入：游戏根、目录访问实现、输入能力位、可选方言 profile。
    /// 运行时（编码 / 配置 / 语言）与路径校验在 <see cref="StartAsync"/> 内完成。
    /// </summary>
    public CoreSession(CoreSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _standalone = true;
        _gameRoot = options.GameRoot;
        _dirAccessor = options.DirAccessor;
        _hub = new OutputHub();
        _io = new HttpSessionIO(_hub);
        _configData = null!;
        _terminalSetup = null!;
        _fullDiffOnFirstTurn = options.FullDiffOnFirstTurn;
        _dialectProfileId = options.DialectProfileId;
        _teardownTimeout = options.TeardownTimeout;
        if (_teardownTimeout <= TimeSpan.Zero && _teardownTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(options), "TeardownTimeout 必须为正，或 Timeout.InfiniteTimeSpan。");
        _inputHost = new DefaultInputHost(options.InputCapabilities.Flags);
        _dispatchValue = value => _io.EnqueueInput(GameServerProtocol.BuildInputJsonl(value));
        Files = new GameTree(options.GameRoot, options.DirAccessor);
        Input = new InputPort(_inputHost, value => _dispatchValue(value));
        Controller = new Controller(agentLease: null);
        _delivery = new TurnDelivery(_io, Controller);
    }

    /// <summary>
    /// Server / 测试组装：hub、io、配置与终端已由调用方建好，本类只接管生命周期。
    /// </summary>
    internal CoreSession(
        HttpSessionIO io,
        ITerminalSetup terminalSetup,
        ConfigData configData,
        TimeSpan? agentLease = null,
        bool fullDiffOnFirstTurn = false,
        string? dialectProfileId = null,
        InputCapability capabilities = InputCapability.KeyboardState | InputCapability.Hover | InputCapability.Focus,
        OutputHub? hub = null,
        GameTree? files = null,
        TimeSpan? teardownTimeout = null,
        CoreSessionProtocolFactory? protocolFactory = null,
        Func<AgentProtocolBase, Task>? runLoop = null)
    {
        _io = io;
        _hub = hub;
        _configData = configData;
        _terminalSetup = terminalSetup;
        _fullDiffOnFirstTurn = fullDiffOnFirstTurn;
        _dialectProfileId = dialectProfileId;
        _teardownTimeout = teardownTimeout ?? TimeSpan.FromSeconds(5);
        _inputHost = new DefaultInputHost(capabilities);
        _dispatchValue = value => _io.EnqueueInput(GameServerProtocol.BuildInputJsonl(value));
        // 宿主路径（Server / MAUI / CLI / 测试）的根随装配好的配置显式传入；
        // issue-009 起不再读进程级视图，也不再有装配期提交可读。
        _root = configData.Root;
        Files = files ?? CreateDefaultFiles();
        Input = new InputPort(_inputHost, value => _dispatchValue(value));
        Controller = new Controller(agentLease);
        _delivery = new TurnDelivery(_io, Controller);
        _protocolFactory = protocolFactory;
        _runLoop = runLoop;
    }

    /// <summary>
    /// CLI 组装：宿主自带渲染协议（<see cref="AgentCliProtocol"/>），但输入 / 输出 / 文件三轴
    /// 仍由本组装根持有。协议工厂拿到的是会话的三轴门面，不得自建第二套。
    /// </summary>
    internal static CoreSession CreateForCli(
        ConfigData configData,
        ITerminalSetup terminalSetup,
        InputCapability capabilities,
        string? dialectProfileId,
        CoreSessionProtocolFactory protocolFactory,
        Func<AgentProtocolBase, Task> runLoop)
    {
        var io = new HttpSessionIO(new OutputHub());
        return new CoreSession(
            io,
            terminalSetup,
            configData,
            agentLease: null,
            fullDiffOnFirstTurn: false,
            dialectProfileId,
            capabilities,
            hub: null,
            files: null,
            teardownTimeout: null,
            protocolFactory,
            runLoop);
    }

    /// <summary>
    /// 测试接缝（ADR-0049 D4）：用给定任务替代真实游戏循环。
    /// </summary>
    internal CoreSession(HttpSessionIO io, ITerminalSetup terminalSetup, ConfigData configData, TimeSpan? agentLease, bool fullDiffOnFirstTurn, string? dialectProfileId, Task loop)
        : this(io, terminalSetup, configData, agentLease, fullDiffOnFirstTurn, dialectProfileId)
        => _loopOverride = loop ?? throw new ArgumentNullException(nameof(loop));

    /// <summary>
    /// 公共启动入口：校验游戏目录、初始化运行时，等游戏循环进入可交互状态后返回。
    /// 目录/配置级失败以 <see cref="CoreSessionStartResult"/> 返回，不杀进程。
    /// </summary>
    public async Task<CoreSessionStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_failed)
                return CoreSessionStartResult.Failure("SESSION_FAILED", _lastError ?? "会话启动已失败。");
            if (_stopRequested)
                return CoreSessionStartResult.Failure("SESSION_STOPPED", "会话已停止，不能重新启动。");

            if (_standalone && _gameTask == null)
            {
                GamePaths paths;
                try
                {
                    paths = GamePaths.Resolve(_gameRoot, _dirAccessor!);
                    paths.Validate();
                }
                catch (GamePathValidationException ex)
                {
                    return FailStart(ex.Code, ex.Message, ex);
                }
                catch (Exception ex)
                {
                    return FailStart("INVALID_GAME_ROOT", ex.Message, ex);
                }

                try
                {
                    // 会话的根钉在字段上，由游戏循环流自己建立作用域（issue-009：无隐式提交）。
                    _root = paths;
                    var (configData, terminalSetup) = EmueraRuntimeInitializer.Initialize(paths, _dirAccessor!);
                    _configData = configData;
                    _terminalSetup = terminalSetup;
                }
                catch (Exception ex)
                {
                    return FailStart("INIT_FAILED", ex.Message, ex);
                }
            }

            Start();
            await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (_startupException != null)
                return CoreSessionStartResult.Failure("INIT_FAILED", _startupException.Message);
            if (_console?.State == ConsoleState.Error)
                return CoreSessionStartResult.Failure("INIT_FAILED", _lastError ?? "游戏初始化失败。");

            return CoreSessionStartResult.Success();
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// 公共停止入口：请求拆除并有界等待游戏循环退出。幂等。
    /// 返回 <c>false</c> = 上界内循环仍在运行（应视为明确失败，而不是继续启动新会话）。
    /// </summary>
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        _stopRequested = true;
        RequestTeardown();
        var exited = await WaitForLoopExitAsync(_teardownTimeout).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (exited)
            ReleaseController();
        return exited;
    }

    internal void Start()
    {
        if (_gameTask != null)
            return;

        _loading = true;
        _gameTask = _loopOverride ?? Task.Run(GameLoopAsync);
        if (_loopOverride != null)
            _ready.TrySetResult(true);
        _loopAwaiter = ObserveLoopAsync(_gameTask);
    }

    /// <summary>CLI 宿主用：启动并等待游戏循环自然结束（含 QUIT）。</summary>
    internal Task<GameLoopResult> WaitForLoopAsync()
    {
        Start();
        return _loopAwaiter!;
    }

    private async Task<GameLoopResult> ObserveLoopAsync(Task loop)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AgentLog.Instance.Write("session game loop task faulted: " + ex);
        }
        return IsAborted ? GameLoopResult.Aborted : GameLoopResult.Completed;
    }

    private CoreSessionStartResult FailStart(string code, string message, Exception exception)
    {
        _lastError = message;
        _startupException = exception;
        _failed = true;
        _hasEnded = true;
        _loading = false;
        _ready.TrySetResult(false);
        return CoreSessionStartResult.Failure(code, message);
    }

    private async Task GameLoopAsync()
    {
        // 本会话的根环境：建立在游戏循环流的最外层，随循环退出销毁（issue-007）。
        // 从这里往后的所有引擎读取（Program.* 转发、ConsoleStateManager、Process、SafCompat……）
        // 读的都是本会话的根——别的流上稍后建立的根改不到它；
        // 反过来，本会话停止后这个作用域也不再残留，下一次启动从它自己的根开始。
        using var rootScope = GameRootEnvironment.Establish(_root);
        try
        {
            var result = await GameLoopComposer.RunAsync(
                _configData,
                _terminalSetup,
                (console, ui, ts) =>
                {
                    _console = console;
                    var defaultFontName = _configData.GetConfigValue<string>(ConfigCode.FontName) ?? "";
                    _displayState = new DisplayState(_console, defaultFontName, _fullDiffOnFirstTurn);
                    _outputPort = new OutputPort(_displayState, _hub);

                    var protocol = _protocolFactory != null
                        ? _protocolFactory(console, ui, ts, _displayState, Input, _outputPort)
                        : new AgentJsonlProtocol(console, ui, _io, _displayState);

                    _protocol = protocol;
                    _jsonlProtocol = protocol as AgentJsonlProtocol;
                    if (protocol is AgentCliProtocol cli)
                        _dispatchValue = cli.DispatchValue;
                    return protocol;
                },
                async p =>
                {
                    _loading = false;
                    _ready.TrySetResult(true);
                    try
                    {
                        if (_runLoop != null)
                            await _runLoop(p);
                        else
                            await ((AgentJsonlProtocol)p).RunLoopAsync(enableTimeout: true, _cts.Token);
                    }
                    catch (GameExitException)
                    {
                        // 脚本 QUIT/EXIT：正常终结 session，走 finally 清理。
                    }
                    catch (Exception ex)
                    {
                        AgentLog.Instance.Write("session game loop exception: " + ex);
                        _lastError = ex.ToString();
                        _io.WriteLine(TurnFrames.Error(ex.ToString(), ErrorInfo.From(ex)));
                    }
                    finally
                    {
                        FinalizeEndedLoop();
                    }
                },
                _dialectProfileId,
                inputHost: _inputHost);

            IsAborted = result == GameLoopResult.Aborted;
            _ready.TrySetResult(true);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested || _disposed)
        {
            AgentLog.Instance.Write("session game loop canceled during initialization");
            _loading = false;
            _hasEnded = true;
            _ready.TrySetResult(false);
        }
        catch (Exception ex)
        {
            DeliverInitStageFailure(ex);
        }
    }

    private void DeliverInitStageFailure(Exception ex)
    {
        AgentLog.Instance.Write("session game loop exception (init stage): " + ex);
        _lastError = ex.ToString();
        _startupException = ex;
        _loading = false;
        try
        {
            _io.WriteLine(TurnFrames.Error(ex.ToString(), ErrorInfo.From(ex)));
        }
        catch (Exception wex)
        {
            AgentLog.Instance.Write("failed to deliver init-stage error line: " + wex.Message);
        }
        FinalizeEndedLoop();
        _ready.TrySetResult(false);
    }

    private void FinalizeEndedLoop()
    {
        Turn? finalTurn = null;
        try
        {
            finalTurn = _jsonlProtocol?.BuildFinalTurn();
        }
        catch (Exception ex)
        {
            AgentLog.Instance.Write("final turn build failed: " + ex.Message);
        }
        _delivery.EndOfGame(finalTurn);
        _protocol?.Stop();
        _console?.Dispose();
        _hasEnded = true;
    }

    internal Task<TurnWaitResult> WaitForTurnAsync(int timeoutMs, CancellationToken externalCt)
        => _delivery.WaitForTurnAsync(timeoutMs, externalCt);

    internal Task<TurnWaitResult> WaitForTurnAsync(
        int timeoutMs,
        ControlIdentity identity,
        CancellationToken externalCt)
        => _delivery.WaitForTurnAsync(timeoutMs, identity, externalCt);

    public void Dispose()
    {
        _stopRequested = true;
        RequestTeardown();
        WaitForLoopExitAsync(Timeout.InfiniteTimeSpan).GetAwaiter().GetResult();
        ReleaseController();
    }

    /// <summary>
    /// 发出拆除请求并立即返回（不等待游戏循环）。幂等。
    /// 拆除顺序的唯一编排点是 <see cref="TurnDelivery.Dispose"/>：置旗标 → <c>Controller.End</c> → <c>Close</c>。
    /// </summary>
    internal void RequestTeardown(string reason = "session_ended")
    {
        if (_disposed)
            return;

        _disposed = true;
        _delivery.Dispose(reason);
        _console?.RequestStop();
        _cts.Cancel();
        _protocol?.Stop();
        _console?.Dispose();
    }

    internal async Task<bool> WaitForLoopExitAsync(TimeSpan timeout)
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

    private void ReleaseController()
    {
        if (_controllerReleased)
            return;
        _controllerReleased = true;
        Controller.Dispose();
    }

    internal ControlAcquireSessionResult AcquireControl(ControlIdentity identity)
    {
        var result = Controller.Acquire(identity);
        if (result.Status != ControlAcquireStatus.Acquired)
            return new ControlAcquireSessionResult(result, null, 0);

        var (advanced, last) = _delivery.DrainOnAcquire();
        return new ControlAcquireSessionResult(result, last, advanced);
    }

    internal bool IsCurrentController(ControlIdentity identity) => Controller.IsCurrent(identity);

    private GameTree CreateDefaultFiles()
    {
        var paths = _root;
        return paths == null
            ? new GameTree(AppContext.BaseDirectory, new FileSystemGameDirAccessor())
            : new GameTree(paths.ExeDir, paths.DirAccessor);
    }
}

internal delegate AgentProtocolBase? CoreSessionProtocolFactory(
    EmueraConsole console,
    IConsoleUI ui,
    ITerminalSetup terminalSetup,
    DisplayState displayState,
    InputPort input,
    OutputPort output);

internal readonly record struct ControlAcquireSessionResult(
    ControlAcquireResult Control,
    Turn? Turn,
    int TurnsAdvanced);
