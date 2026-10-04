using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

/// <summary>
/// Session 生命周期管理（issue 03/05 重构）——从 <see cref="KestrelGameServer"/> 拆出。
/// 串行化 /session、/load-game、DELETE /session 三个会话变更操作与 WS 订阅的
/// accept→re-check→subscribe 原子序列。
///
/// 锁语义（与原 KestrelGameServer 一致）：
/// - 本类内所有变更操作在 <c>_sessionLock</c>（SemaphoreSlim(1,1)）内完成；
/// - <c>_session</c> 是 volatile，只读操作（/input、/snapshot 等）可无锁读；
/// - <c>_sessionHub</c> 与 <c>_session</c> 同生命周期，仅在锁内赋值/置空；
/// - 方法返回 outcome record 而非 IResult，测试可直接断言状态。
/// </summary>
internal sealed class SessionRegistry
{
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private volatile Session? _session;
    private OutputHub? _sessionHub;
    private readonly ITerminalSetup _terminalSetup;
    private readonly GameConfigService _configService;
    private readonly TimeSpan? _agentLease;
    /// <summary>会话级方言 profile id（实现票 25）——本 server 新建的所有会话共用。null = v24pure。</summary>
    private readonly string? _dialectProfileId;

    public SessionRegistry(ITerminalSetup terminalSetup, GameConfigService configService, TimeSpan? agentLease = null, string? dialectProfileId = null)
    {
        _terminalSetup = terminalSetup ?? throw new ArgumentNullException(nameof(terminalSetup));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _agentLease = agentLease;
        _dialectProfileId = dialectProfileId;
    }

    /// <summary>当前 session 引用（volatile 读）——仅供只读端点（/turn /input /snapshot）使用。</summary>
    public Session? CurrentSession => _session;

    /// <summary>
    /// POST /session（迁移自原 HandleCreateSessionAsync）。
    /// - <c>_session == null</c>（未加载游戏）→ <see cref="SessionCreateStatus.NoGameLoaded"/>
    /// - 已有活跃 session → <see cref="SessionCreateStatus.Conflict"/>
    /// - 旧 session 已结束（HasEnded）→ dispose 后重建新 session → <see cref="SessionCreateStatus.Created"/>
    /// </summary>
    public async Task<SessionCreateResult> CreateNewSessionAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            // T-025 D4/D16：空闲态守卫——_session == null 表示尚未加载游戏（POST /load-game
            // 成功才会建 session）。503 语义早于「已有活跃会话 409」判断。
            if (_session == null)
                return new SessionCreateResult(SessionCreateStatus.NoGameLoaded, null, default, null);

            if (!_session.HasEnded)
                return new SessionCreateResult(SessionCreateStatus.Conflict, null, default, null);

            DestroySession("session_replaced");

            // issue 03：SessionRegistry 自持 OutputHub 引用，HttpSessionIO 只接 IOutputBroadcaster 抽象。
            var session = CreateSessionCore(_configService.Current);
            return new SessionCreateResult(SessionCreateStatus.Created, session.Id, session.CreatedAt, session.StateString);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// MAUI 托管模式建会话（issue 05）——游戏已由 MAUI <c>OnReloadGame</c> 初始化
    /// （<c>GamePaths.Current</c> 已 Resolve + <c>ConfigData</c> 已 LoadConfig），无需再解析路径/重载配置。
    /// 与 <see cref="CreateNewSessionAsync"/> 的区别：允许 <c>_session == null</c>（MAUI 首次起 server
    /// 时注册表为空）——HTTP 模式靠 /load-game 建首个 session，POST /session 只负责结束后重建，
    /// 因此其 NoGameLoaded 守卫不适用于 MAUI。
    /// 已有活跃 session → Conflict；否则销毁旧 session（若有）后建新 session。
    /// </summary>
    /// <param name="fullDiffOnFirstTurn">首帧 diff 是否返回全量（MAUI WebView 桥接无 snapshot 入口，须 true）。</param>
    /// <summary>
    /// 用 <c>_configService.Current</c> 建会话——无显式 ConfigData 时的原行为（Kestrel / 测试路径）。
    /// </summary>
    public Task<SessionCreateResult> CreateMauiSessionAsync(bool fullDiffOnFirstTurn = true)
        => CreateMauiSessionAsync(_configService.Current, fullDiffOnFirstTurn);

    /// <summary>
    /// 用显式 <paramref name="configData"/> 建 MAUI 会话。
    /// <para>
    /// 为何需要：server 生命周期提升到 app 级后（agent 在未加载游戏时也要能连上），
    /// <see cref="HttpListenerHost"/> 构造时的 <c>ConfigData</c> 是启动期占位实例，
    /// <c>_configService.Current</c> 不再是游戏真实配置。MAUI 侧
    /// <c>MainPage.OnReloadGame</c> 已初始化好真实 <c>ConfigData</c>（含性能覆盖旋钮），
    /// 必须由此重载注入，否则会话会用占位配置加载 ERB。
    /// </para>
    /// </summary>
    /// <param name="dialectProfileId">
    /// 本会话的方言 profile 覆盖（MAUI 设置项 <c>app.dialectProfile</c>）。<c>null</c> = 用注册表构造时绑定的
    /// <c>_dialectProfileId</c>（Kestrel 的 <c>--profile</c> / 缺省 v24pure）。MAUI 托管的 server 是 app 级单例、
    /// 注册表只构造一次，故 profile 必须按会话传入才能做到「重载游戏即生效」。
    /// </param>
    public async Task<SessionCreateResult> CreateMauiSessionAsync(ConfigData configData, bool fullDiffOnFirstTurn = true, string? dialectProfileId = null)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_session is { HasEnded: false })
                return new SessionCreateResult(SessionCreateStatus.Conflict, null, default, null);

            DestroySession("session_replaced");

            var session = CreateSessionCore(configData, fullDiffOnFirstTurn, dialectProfileId);
            return new SessionCreateResult(SessionCreateStatus.Created, session.Id, session.CreatedAt, session.StateString);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// DELETE /session（迁移自原 HandleDeleteSessionAsync）。
    /// 存在 session → dispose 并置空 → <see cref="SessionDeleteStatus.Removed"/>；否则 NotFound。
    /// </summary>
    public Task<SessionDeleteResult> DeleteSessionAsync()
    {
        return DeleteSessionAsync(null);
    }

    public async Task<SessionDeleteResult> DeleteSessionAsync(ControlIdentity? identity)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_session == null)
                return new SessionDeleteResult(SessionDeleteStatus.NotFound);
            if (identity.HasValue && !_session.HasEnded)
            {
                var gate = _session.Controller.CheckLifecycle(identity.Value, sessionEnded: false);
                if (gate.Status != ControlGateStatus.Allowed)
                    return new SessionDeleteResult(SessionDeleteStatus.ControlDenied, gate.Reason);
            }
            DestroySession("session_ended");
            return new SessionDeleteResult(SessionDeleteStatus.Removed);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// POST /load-game（迁移自原 HandleLoadGameAsync）——原子重载游戏目录（issue 05）。
    /// 全程序在 _sessionLock 内完成（spec L224-229）：
    /// 1. 校验路径（拆旧前拦路径级错误）——GamePaths.Resolve + Validate，失败抛 GamePathValidationException
    /// 2. dispose 旧 Session（若有）
    /// 3. Preload.Clear()
    /// 4. GamePaths.Resolve 已在步骤 1 完成——GamePaths.Current 指向新目录
    /// 5. 重建 ConfigData：GameConfigService.Reload(exeDir)——LoadConfig + SetCurrent + 字段替换
    /// 6. Preload.Load 由新 Session 的 ConsoleStateManager.Initialize 异步执行（issue 11 D2）
    /// 7. 建新 Session（传入新 ConfigData）
    /// 8. 返 Success(sessionId, state, gameDir)
    ///
    /// 错误契约（spec L229）：
    /// - 路径级错误（DIR_NOT_FOUND / MISSING_CSV / MISSING_ERB）→ <see cref="LoadGameStatus.PathError"/>
    /// - 加载级失败（ConfigData.LoadConfig 等同步步骤异常、未预期异常）→ <see cref="LoadGameStatus.LoadFailed"/>
    ///   异步阶段（Preload.Load / Process.Initialize）失败由 ConsoleStateManager 置 State=Error。
    /// </summary>
    public Task<LoadGameResult> ReplaceForLoadGameAsync(string gameDir)
    {
        return ReplaceForLoadGameAsync(gameDir, null);
    }

    public async Task<LoadGameResult> ReplaceForLoadGameAsync(string gameDir, ControlIdentity? identity)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (_session is { HasEnded: false } activeSession && identity.HasValue)
            {
                var gate = activeSession.Controller.CheckLifecycle(identity.Value, sessionEnded: false);
                if (gate.Status != ControlGateStatus.Allowed)
                    return LoadGameResult.ControlDenied(gate.Reason);
            }

            // 1. 校验路径——GamePathValidationException 转 PathError。
            //
            // 注意：GamePaths.Resolve(gameDir) 内部会先把静态 Current 指向新 paths 再返回。
            // 若 Validate 抛异常，Current 已被污染——spec L229 要求"拆旧前拦路径级错误"，
            // 即 server 状态不应被失败的 /load-game 改动。故在 catch 内回滚 Current 到旧值
            // （旧值 = 进入 /load-game 前的 GamePaths.Current，可能为 null 启动期场景）。
            GamePaths paths;
            GamePaths? previousPaths = GamePaths.Current;
            try
            {
                paths = GamePaths.Resolve(gameDir, new FileSystemGameDirAccessor());
                paths.Validate();
            }
            catch (GamePathValidationException ex)
            {
                // 回滚 Current 到校验前的值——避免 GET /state 返回被拒绝的非法路径
                if (previousPaths != null)
                {
                    GamePaths.SetCurrent(previousPaths);
                }
                return LoadGameResult.PathError(ex.Code, ex.Message);
            }

            // 2. dispose 旧 Session（若有）——Dispose 等待旧 GameLoopAsync 退出，scope 自动清理
            DestroySession("session_replaced");

            // 3. Preload.Clear —— 清空旧 ERB/CSV 文件缓存
            Preload.Clear();

            // 4. GamePaths.Resolve 已在步骤 1 完成——GamePaths.Current 指向新目录

            // 5. 重建 ConfigData 并 SetCurrent（HTTP 线程 AsyncLocal——主要供新 Session 异步
            //    Preload.Load 间接读）。注意：SetCurrent 与下方 new Session 必须同一 async 流。
            var newConfig = _configService.Reload(paths.ExeDir);

            // 6. Preload.Load 已下沉到 ConsoleStateManager.Initialize（issue 11 D2）——
            //    新 Session.Start() 排 GameLoopAsync 到独立 Task，_loading=true 让 StateString 返 "Loading"。

            // 7. 建新 Session——GameLoopAsync 内 GameLoopComposer.OpenScope(_configData) 注入新配置
            var newSession = CreateSessionCore(newConfig);

            // 8. Success——state="Loading"（issue 11 D1 落地后自动生效）
            return LoadGameResult.Success(newSession.Id, newSession.StateString, paths.ExeDir);
        }
        catch (Exception ex)
        {
            // 兜底：未预期异常归为 LOAD_FAILED
            EmueraLog.Error("load-game", "unexpected exception");
            EmueraLog.Error("load-game", ex.ToString());
            return LoadGameResult.LoadFailed(ex.Message);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// GET /ws 订阅（迁移自原 HandleWebSocketAsync 的锁内段）。
    /// 锁内完成 accept→re-check→subscribe 原子序列，避免 session 被另一线程
    /// DELETE+POST 替换后仍从旧 hub 订阅。
    /// 无活跃 session（或已结束）→ 返回 null（调用方下发 4004 关闭码）。
    /// </summary>
    public Task<WsSubscription?> TryGetWsSubscriptionAsync()
    {
        return TryGetWsSubscriptionAsync(null);
    }

    public async Task<WsSubscription?> TryGetWsSubscriptionAsync(ControlIdentity? identity)
    {
        await _sessionLock.WaitAsync();
        try
        {
            var session = _session;
            if (session is { HasEnded: false })
            {
                // issue 03：hub 引用源为 SessionRegistry 自持的 _sessionHub——
                // 与 _session 在锁内同生命周期赋值/置空，此处持锁读取安全。
                var hub = _sessionHub;
                var reader = hub?.Subscribe();
                if (reader != null)
                    return new WsSubscription(session, reader, hub!, identity ?? ControlIdentity.User);
            }
            return null;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>整体释放（服务器关闭，迁移自原 Dispose 的 session 段）——先 join 游戏循环再释放。</summary>
    public void DisposeAll()
    {
        _sessionLock.Wait();
        try
        {
            DestroySession("session_ended");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// 建新会话的统一助手（建会话三处去重）——新 OutputHub + HttpSessionIO + Session + Start，
    /// 并写入 <c>_sessionHub</c> / <c>_session</c>（三态同生命周期）。
    /// 仅在持有 <c>_sessionLock</c> 时调用（<see cref="CreateNewSessionAsync"/> /
    /// <see cref="CreateMauiSessionAsync"/> / <see cref="ReplaceForLoadGameAsync(string, System.Nullable{ControlIdentity})"/>）。
    /// </summary>
    /// <param name="configData">会话使用的 ConfigData（HTTP /load-game 用重载后的 newConfig，其余用 <c>_configService.Current</c>）。</param>
    /// <param name="fullDiffOnFirstTurn">首帧 diff 是否返回全量（MAUI 托管 true）。</param>
    /// <param name="dialectProfileId">本会话的方言 profile 覆盖；<c>null</c> = 用注册表构造时的 <c>_dialectProfileId</c>。</param>
    private Session CreateSessionCore(ConfigData configData, bool fullDiffOnFirstTurn = false, string? dialectProfileId = null)
    {
        // issue 03：SessionRegistry 自持 OutputHub 引用，HttpSessionIO 只接 IOutputBroadcaster 抽象。
        var hub = new OutputHub();
        var io = new HttpSessionIO(hub);
        _sessionHub = hub;
        var session = new Session(io, _terminalSetup, configData, _agentLease, fullDiffOnFirstTurn, dialectProfileId ?? _dialectProfileId);
        session.Start();
        _session = session;
        return session;
    }

    /// <summary>
    /// 销毁当前 session（若有）：Dispose（join 游戏循环，scope 自动清理）并置空 _session/_sessionHub。
    /// 三态同生命周期，仅应在持 _sessionLock 时调用。
    /// </summary>
    private void DestroySession(string reason = "session_ended")
    {
        if (_session is { HasEnded: false } session)
            session.Controller.End(reason);
        _session?.Dispose();
        _session = null;
        _sessionHub = null;
    }
}

/// <summary>POST /session 结果状态。</summary>
internal enum SessionCreateStatus
{
    /// <summary>未加载游戏（_session == null）→ HTTP 503。</summary>
    NoGameLoaded,
    /// <summary>已有活跃 session → HTTP 409。</summary>
    Conflict,
    /// <summary>已结束 session 重建成功 → HTTP 201。</summary>
    Created,
}

/// <summary>POST /session 结果。</summary>
internal readonly record struct SessionCreateResult(SessionCreateStatus Status, string? SessionId, DateTimeOffset CreatedAt, string? State);

/// <summary>DELETE /session 结果状态。</summary>
internal enum SessionDeleteStatus
{
    /// <summary>已删除 → HTTP 200 {removed:true}。</summary>
    Removed,
    /// <summary>无 session → HTTP 404 {removed:false}。</summary>
    NotFound,
    /// <summary>活跃会话由其他 Controller 持有 → HTTP 409。</summary>
    ControlDenied,
}

/// <summary>DELETE /session 结果。</summary>
internal readonly record struct SessionDeleteResult(SessionDeleteStatus Status, string? Reason = null);

/// <summary>POST /load-game 结果状态。</summary>
internal enum LoadGameStatus
{
    /// <summary>路径级错误（DIR_NOT_FOUND / MISSING_CSV / MISSING_ERB）→ HTTP 400。</summary>
    PathError,
    /// <summary>加载级失败/未预期异常 → HTTP 500 LOAD_FAILED。</summary>
    LoadFailed,
    /// <summary>成功 → HTTP 200。</summary>
    Success,
    /// <summary>活跃会话由其他 Controller 持有 → HTTP 409。</summary>
    ControlDenied,
}

/// <summary>POST /load-game 结果。</summary>
internal readonly record struct LoadGameResult(LoadGameStatus Status, string? Code, string? Message, string? SessionId, string? State, string? GameDir)
{
    public static LoadGameResult ControlDenied(string reason) => new(LoadGameStatus.ControlDenied, reason, "Control is held by another controller", null, null, null);
    public static LoadGameResult PathError(string code, string message) => new(LoadGameStatus.PathError, code, message, null, null, null);
    public static LoadGameResult LoadFailed(string message) => new(LoadGameStatus.LoadFailed, null, message, null, null, null);
    public static LoadGameResult Success(string sessionId, string state, string gameDir) => new(LoadGameStatus.Success, null, null, sessionId, state, gameDir);
}

/// <summary>WS 订阅三元组：session + 独占 reader + 所属 hub（finally 退订用）。</summary>
internal sealed record WsSubscription(
    Session Session,
    ChannelReader<string> Reader,
    OutputHub Hub,
    ControlIdentity Identity = default);
