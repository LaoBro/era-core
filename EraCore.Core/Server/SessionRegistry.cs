using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Terminal.Platform;

namespace MinorShift.Emuera.Server;

/// <summary>
/// Session 生命周期管理（issue 03/05 重构）——从 <see cref="KestrelGameServer"/> 拆出。
/// 串行化 /session、/load-game、DELETE /session 三个会话变更操作与 WS 订阅的
/// accept→re-check→subscribe 原子序列。
///
/// <para><b>锁语义（issue-001 修正为双锁）</b>：</para>
/// <list type="bullet">
/// <item><c>_sessionLock</c>——只保护 <c>_session</c> / <c>_sessionHub</c> 两个字段的读写，
/// 临界区为微秒级（发拆除请求 + 摘引用，不含任何等待）。</item>
/// <item><c>_mutationGate</c>——串行化整个<b>变更操作</b>（拆除 → 有界等待旧循环 → 建会话），
/// 跨秒级。<b>只读端点（/state /turn /input /snapshot /ws）不碰它</b>，
/// 故拆除等待期间 server 仍可响应（spec 验收项）。</item>
/// </list>
///
/// <para><b>为什么不一把锁走到底</b>：原实现把「同步 join 游戏循环」放在
/// <c>_sessionLock</c> 内，而游戏循环可能正卡在长回合脚本里（无让路点），
/// join 永不返回 → 锁永不释放 → 需该锁的端点全部无限阻塞，server 整体冻死。
/// 拆成两把锁后：锁内只做原子摘除（立即让其他端点恢复），等待移到锁外并加上界。</para>
///
/// <para><b>新会话与旧循环仍保持串行</b>（spec 决策四）：进程内有 11 项全局静态在 scope
/// 释放时被清（含非并发安全的普通字典），<c>GamePaths.Current</c> 亦为进程级单槽——
/// 旧循环未退出就启动新循环会造成数据竞争。故超时后<b>不</b>建新会话，
/// 而是把错误如实回给调用方，待旧循环自然退出后重试即可。</para>
///
/// <para><c>_session</c> 是 volatile，只读操作（/input、/snapshot 等）可无锁读；
/// 方法返回 outcome record 而非 IResult，测试可直接断言状态。</para>
/// </summary>
internal sealed class SessionRegistry
{
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private volatile Session? _session;
    private OutputHub? _sessionHub;
    /// <summary>
    /// <b>Retiring 相位</b>（ADR-0049 D2）：<c>_session == null</c> 且上一轮拆除的旧循环仍在跑。
    ///
    /// 拆除超时后 <c>_session</c> 已是 null，但旧循环仍在运行且仍读 <see cref="GamePaths.Current"/>、
    /// 仍持有 scope 静态。此字段让<b>后续</b>变更操作知道"还有个循环活着"——
    /// 否则它们会看到 <c>_session == null</c> 以为无旧循环，直接建新会话，
    /// 造成 spec 决策四禁止的静态竞争（新循环初始化清掉旧循环正在用的缓存）。
    ///
    /// <para>它不再是「残留引用」而是相位载体：create 拒绝、DELETE 幂等等待、<c>/state</c> 的
    /// <c>retiring</c> 字段都读它。清除只发生在 <see cref="AwaitTeardownAsync"/> 的成功返回处
    /// （S-2：不由时间清、不由新建会话清）。volatile：变更操作在锁内写、只读端点无锁读。</para>
    /// </summary>
    private volatile Session? _retiring;
    private readonly ITerminalSetup _terminalSetup;
    private readonly GameConfigService _configService;
    private readonly TimeSpan? _agentLease;
    /// <summary>会话级方言 profile id（实现票 25）——本 server 新建的所有会话共用。null = v24pure。</summary>
    private readonly string? _dialectProfileId;

    /// <summary>
    /// 拆除会话时等待游戏循环退出的上界。超时即返回明确失败（不再无限等待）。
    /// 缺省读环境变量 <c>EMUERA_TEARDOWN_TIMEOUT_SECONDS</c>（同 <c>EMUERA_CONTROL_LEASE_SECONDS</c> 模式）。
    /// 测试经构造参数注入更小的值。
    /// </summary>
    internal TimeSpan TeardownTimeout { get; init; } = ReadTeardownTimeout();

    /// <summary>
    /// 拆除等待上界的缺省值。取 5 秒：正常拆除（脚本停在等待输入）实测毫秒级，
    /// 超时仅在脚本卡死时触发，故宁可宽松以免误伤正在慢退出的正常流程
    /// （如启动期的数据库重建）。env 可覆盖。
    /// </summary>
    private static TimeSpan ReadTeardownTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("EMUERA_TEARDOWN_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(5);
    }

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
    /// 是否处于 Retiring 相位（无 Live 会话，但上一轮拆除的旧循环尚未退出，ADR-0049 D2）。
    /// 只读端点（<c>GET /state</c>）据此透出 <c>retiring</c> 字段；无锁读，仅作诊断与客户端提示。
    /// </summary>
    public bool IsRetiring => _retiring != null;

    /// <summary>
    /// 测试接缝（ADR-0049 D4）：替代 <c>new Session(...)</c> 的会话工厂。
    /// <para>
    /// registry 仍自持 <see cref="OutputHub"/> 与 <see cref="HttpSessionIO"/>，故 WS 订阅路径与生产
    /// 完全一致；注入方只决定「会话怎么建」与「它的游戏循环任务是谁」。生产路径为 null。
    /// </para>
    /// </summary>
    internal Func<HttpSessionIO, ConfigData, Session>? SessionFactory { get; init; }

    /// <summary>
    /// POST /session（迁移自原 HandleCreateSessionAsync）。
    /// - <c>_session == null</c>（未加载游戏）→ <see cref="SessionCreateStatus.NoGameLoaded"/>
    /// - 已有活跃 session → <see cref="SessionCreateStatus.Conflict"/>
    /// - 旧 session 已结束（HasEnded）→ dispose 后重建新 session → <see cref="SessionCreateStatus.Created"/>
    /// </summary>
    public async Task<SessionCreateResult> CreateNewSessionAsync()
    {
        await _mutationGate.WaitAsync();
        try
        {
            // 先确认上一轮拆除的旧循环已退出（拆除超时后 _session 已为 null，但循环可能仍活着）。
            // 未退出则拒绝建会话——spec 决策四：静态隔离要求两者串行。
            if (_retiring != null && !await AwaitTeardownAsync(null))
                return new SessionCreateResult(SessionCreateStatus.PreviousSessionStillRunning, null, default, null);

            Session? detached;
            await _sessionLock.WaitAsync();
            try
            {
                // T-025 D4/D16：空闲态守卫——_session == null 表示尚未加载游戏（POST /load-game
                // 成功才会建 session）。503 语义早于「已有活跃会话 409」判断。
                if (_session == null)
                    return new SessionCreateResult(SessionCreateStatus.NoGameLoaded, null, default, null);

                if (!_session.HasEnded)
                    return new SessionCreateResult(SessionCreateStatus.Conflict, null, default, null);

                detached = DetachSessionLocked("session_replaced");
            }
            finally
            {
                _sessionLock.Release();
            }

            // 锁外有界等待：旧循环（此处应已结束）——超时则拒绝建新会话。
            if (!await AwaitTeardownAsync(detached))
                return new SessionCreateResult(SessionCreateStatus.PreviousSessionStillRunning, null, default, null);

            // issue 03：SessionRegistry 自持 OutputHub 引用，HttpSessionIO 直持同一实例（2026-10-19 起不再经 IOutputBroadcaster 抽象）。
            await _sessionLock.WaitAsync();
            try
            {
                var session = CreateSessionCore(_configService.Current);
                return new SessionCreateResult(SessionCreateStatus.Created, session.Id, session.CreatedAt, session.StateString);
            }
            finally
            {
                _sessionLock.Release();
            }
        }
        finally
        {
            _mutationGate.Release();
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
        await _mutationGate.WaitAsync();
        try
        {
            // 同 CreateNewSessionAsync：旧循环未退出前不得建新会话。
            if (_retiring != null && !await AwaitTeardownAsync(null))
                return new SessionCreateResult(SessionCreateStatus.PreviousSessionStillRunning, null, default, null);

            Session? detached;
            await _sessionLock.WaitAsync();
            try
            {
                if (_session is { HasEnded: false })
                    return new SessionCreateResult(SessionCreateStatus.Conflict, null, default, null);

                detached = DetachSessionLocked("session_replaced");
            }
            finally
            {
                _sessionLock.Release();
            }

            if (!await AwaitTeardownAsync(detached))
                return new SessionCreateResult(SessionCreateStatus.PreviousSessionStillRunning, null, default, null);

            await _sessionLock.WaitAsync();
            try
            {
                var session = CreateSessionCore(configData, fullDiffOnFirstTurn, dialectProfileId);
                return new SessionCreateResult(SessionCreateStatus.Created, session.Id, session.CreatedAt, session.StateString);
            }
            finally
            {
                _sessionLock.Release();
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// MAUI 托管模式的<b>原子重开</b>（ADR-0049 D5）：一次 gate、一次有界等待、一次建会话。
    ///
    /// <para><b>为什么需要</b>：宿主此前是「<c>DeleteSessionAsync</c>（忽略结果）→ <c>CreateMauiSessionAsync</c>」
    /// 两次调用。旧循环若在上界内没退出，第一次吞掉 <c>TeardownTimeout</c>，第二次再等一遍上界
    /// 才返回 <see cref="SessionCreateStatus.PreviousSessionStillRunning"/> —— 用户侧表现是切换游戏
    /// 卡住 10 秒后弹出一个枚举名。把两步合成一次调用后，最坏等待回到一个上界，且失败原因单一。</para>
    ///
    /// <para><b>与 <see cref="CreateMauiSessionAsync"/> 的区别</b>：不返回 <c>Conflict</c>——重开的语义
    /// 就是「替换当前会话」，宿主已持有唯一会话的归属权。与 <see cref="ReplaceForLoadGameAsync(string)"/>
    /// 的区别：<b>不碰</b> <see cref="GamePaths"/>（MAUI 的路径由 <c>OnReloadGame</c> 提前 Resolve，
    /// 由此处改写会让旧循环读到新路径——issue-001 严防的形态）。</para>
    /// </summary>
    public async Task<SessionCreateResult> ReplaceMauiSessionAsync(ConfigData configData, bool fullDiffOnFirstTurn = true, string? dialectProfileId = null)
    {
        await _mutationGate.WaitAsync();
        try
        {
            // 上一轮拆除遗留的旧循环同样要先退出（S-1：Retiring 必须先清）。
            if (_retiring != null && !await AwaitTeardownAsync(null))
                return new SessionCreateResult(SessionCreateStatus.PreviousSessionStillRunning, null, default, null);

            Session? detached;
            await _sessionLock.WaitAsync();
            try
            {
                detached = DetachSessionLocked("session_replaced");
            }
            finally
            {
                _sessionLock.Release();
            }

            if (!await AwaitTeardownAsync(detached))
                return new SessionCreateResult(SessionCreateStatus.PreviousSessionStillRunning, null, default, null);

            await _sessionLock.WaitAsync();
            try
            {
                var session = CreateSessionCore(configData, fullDiffOnFirstTurn, dialectProfileId);
                return new SessionCreateResult(SessionCreateStatus.Created, session.Id, session.CreatedAt, session.StateString);
            }
            finally
            {
                _sessionLock.Release();
            }
        }
        finally
        {
            _mutationGate.Release();
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
        await _mutationGate.WaitAsync();
        try
        {
            Session? detached;
            await _sessionLock.WaitAsync();
            try
            {
                if (_session == null)
                {
                    // ADR-0049 D3（幂等 DELETE）：没有 Live 会话时仍要区分
                    // 「真没了」(→ 404) 与「上一轮拆除超时、旧循环还在跑」(→ 有界等待后 Removed)。
                    // 否则调用方拿着 503 的重试指引再调一次，反而拿到 404 —— 同一事实两种答案。
                    if (_retiring == null)
                        return new SessionDeleteResult(SessionDeleteStatus.NotFound);
                    detached = null;
                }
                else
                {
                    if (identity.HasValue && !_session.HasEnded)
                    {
                        var gate = _session.Controller.CheckLifecycle(identity.Value, sessionEnded: false);
                        if (gate.Status != ControlGateStatus.Allowed)
                            return new SessionDeleteResult(SessionDeleteStatus.ControlDenied, gate.Reason);
                    }
                    detached = DetachSessionLocked("session_ended");
                }
            }
            finally
            {
                _sessionLock.Release();
            }

            // 锁外有界等待——issue-001 的核心：等待期间 server 仍可响应，
            // 超时则回明确失败而非让调用方无限期等一个永不返回的 join。
            // detached 为 null 时等的是 _retiring（上次拆除遗留的循环）。
            if (!await AwaitTeardownAsync(detached))
                return new SessionDeleteResult(SessionDeleteStatus.TeardownTimeout, "teardown_timeout");

            return new SessionDeleteResult(SessionDeleteStatus.Removed);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// POST /load-game（迁移自原 HandleLoadGameAsync）——原子重载游戏目录（issue 05）。
    ///
    /// <para><b>issue-001 重排后的顺序</b>（关键：不让旧循环对着被改写的 <see cref="GamePaths.Current"/> 运行）：</para>
    /// <list type="number">
    /// <item>控制权门禁（锁内，微秒级）</item>
    /// <item><see cref="GamePaths.ResolvePreview"/> + <c>Validate</c> —— <b>不写</b> <c>GamePaths.Current</c>，
    /// 故校验失败时 server 状态零改动（原实现需回滚 Current，见下）</item>
    /// <item>锁内发拆除请求 + 摘引用（微秒级）</item>
    /// <item><b>锁外</b>有界等待旧游戏循环退出——等待期间其他端点可响应</li>
    /// <item>超时 → 回 <see cref="LoadGameStatus.PreviousSessionStillRunning"/>，
    /// <b>不</b>建新会话（守住静态隔离不变量）；此时才 <see cref="GamePaths.Resolve"/> 提交新路径</item>
    /// <item><c>Preload.Clear()</c> + 重建 <c>ConfigData</c> + 建新 Session</li>
    /// </list>
    ///
    /// <para>第2 步用预览式校验是必需的：若沿用 <c>Resolve</c>（会写 <c>Current</c>），
    /// 第 4 步超时时旧循环仍在运行，而它读的正是 <c>Current</c>（<c>Program.ErbDir</c> /
    /// <c>ConsoleStateManager</c> / <c>Process</c> / <c>SafCompat</c> 等）——
    /// 会去新游戏目录找 ERB/CSV。原实现靠"join 永不返回所以走不到后面"侥幸躲过该形态。</para>
    ///
    /// <para>错误契约：路径级错误（DIR_NOT_FOUND / MISSING_CSV / MISSING_ERB）→
    /// <see cref="LoadGameStatus.PathError"/>；加载级失败→ <see cref="LoadGameStatus.LoadFailed"/>；
    /// 旧循环未退出 → <see cref="LoadGameStatus.PreviousSessionStillRunning"/>。
    /// 异步阶段（Preload.Load / Process.Initialize）失败由 ConsoleStateManager 置 State=Error。</para>
    /// </summary>
    public Task<LoadGameResult> ReplaceForLoadGameAsync(string gameDir)
    {
        return ReplaceForLoadGameAsync(gameDir, null);
    }

    public async Task<LoadGameResult> ReplaceForLoadGameAsync(string gameDir, ControlIdentity? identity)
    {
        await _mutationGate.WaitAsync();
        try
        {
            Session? detached;
            // paths 需跨两个锁块使用（校验在锁内、提交在锁外），故声明在此处。
            GamePaths paths;
            await _sessionLock.WaitAsync();
            try
            {
                if (_session is { HasEnded: false } activeSession && identity.HasValue)
                {
                    var gate = activeSession.Controller.CheckLifecycle(identity.Value, sessionEnded: false);
                    if (gate.Status != ControlGateStatus.Allowed)
                        return LoadGameResult.ControlDenied(gate.Reason);
                }

                // 1. 预览式路径校验——不污染 GamePaths.Current，失败无需回滚。
                try
                {
                    paths = GamePaths.ResolvePreview(gameDir, new FileSystemGameDirAccessor());
                    paths.Validate();
                }
                catch (GamePathValidationException ex)
                {
                    return LoadGameResult.PathError(ex.Code, ex.Message);
                }

                // 2. 锁内拆除：发请求 + 摘引用（微秒级，不等游戏循环）
                detached = DetachSessionLocked("session_replaced");
            }
            finally
            {
                _sessionLock.Release();
            }

            // 3. 锁外有界等待旧循环退出——server 此刻仍可响应。
            if (!await AwaitTeardownAsync(detached))
                return LoadGameResult.PreviousStillRunning();

            await _sessionLock.WaitAsync();
            try
            {
                // 4. 旧循环确已退出，此刻才提交新路径（原 Resolve 的副作用）。
                GamePaths.Resolve(gameDir, new FileSystemGameDirAccessor());

                // 5. 清空旧 ERB/CSV 文件缓存
                Preload.Clear();

                // 6. 重建 ConfigData 并 SetCurrent（HTTP 线程 AsyncLocal——主要供新 Session 异步
                //    Preload.Load 间接读）。注意：SetCurrent 与下方 new Session 必须同一 async 流。
                var newConfig = _configService.Reload(paths.ExeDir);

                // 7. Preload.Load 已下沉到 ConsoleStateManager.Initialize（issue 11 D2）——
                //    新 Session.Start() 排 GameLoopAsync 到独立 Task，_loading=true 让 StateString 返 "Loading"。

                // 8. 建新 Session——GameLoopAsync 内 GameLoopComposer.OpenScope(_configData) 注入新配置
                var newSession = CreateSessionCore(newConfig);

                // 9. Success——state="Loading"（issue 11 D1 落地后自动生效）
                return LoadGameResult.Success(newSession.Id, newSession.StateString, paths.ExeDir);
            }
            finally
            {
                _sessionLock.Release();
            }
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
            _mutationGate.Release();
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

    /// <summary>
    /// 整体释放（服务器关闭）——同 <see cref="DeleteSessionAsync"/> 的两段式拆除。
    /// <para>
    /// 关停路径上的等待<b>不设上界</b>：进程即将退出，截断等待只会留下未清理的
    /// 全局静态与半初始化状态（spec 决策二：不硬杀、也不半途放弃清理）。
    /// 真正的风险（server 带病存活、需继续服务）只出现在变更操作上，那些走有界路径。
    /// </para>
    /// </summary>
    public void DisposeAll()
    {
        _mutationGate.Wait();
        try
        {
            Session? detached;
            _sessionLock.Wait();
            try
            {
                detached = DetachSessionLocked("session_ended");
            }
            finally
            {
                _sessionLock.Release();
            }
            if (detached != null)
            {
                // 无上界：进程退出前必须让scope 清理完整跑完。
                detached.Dispose();
            }
            // 上一轮拆除超时留下的旧循环同样要等干净——否则进程退出时它仍在读
            // 已被 Clear 的静态，而 App 域卸载会与之竞争。
            if (_retiring != null)
            {
                _retiring.Dispose();
                _retiring = null;
            }
        }
        finally
        {
            _mutationGate.Release();
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
        // issue 03：SessionRegistry 自持 OutputHub 引用，HttpSessionIO 直持同一实例（2026-10-19 起不再经 IOutputBroadcaster 抽象）。
        // 接缝（ADR-0049 D4）：hub/io 始终由本方法建（WS 订阅路径与生产一致），工厂只替换 Session 本体。
        var hub = new OutputHub();
        var io = new HttpSessionIO(hub);
        _sessionHub = hub;
        var session = SessionFactory is { } factory
            ? factory(io, configData)
            : new Session(io, _terminalSetup, configData, _agentLease, fullDiffOnFirstTurn, dialectProfileId ?? _dialectProfileId);
        session.Start();
        _session = session;
        return session;
    }

    /// <summary>
    /// 锁内拆除：发拆除请求 + 摘除 <c>_session</c>/<c>_sessionHub</c>，返回被摘下的会话。
    /// <b>不等待游戏循环</b>——调用方必须在锁外做有界等待（<see cref="AwaitTeardownAsync"/>）。
    /// 临界区内不含任何阻塞，故其他端点立即恢复可响应。
    /// </summary>
    private Session? DetachSessionLocked(string reason = "session_ended")
    {
        var detached = _session;
        // 顺序要点：先摘引用，后发拆除请求。反过来的话，摘引用与请求之间若有读端点
        // 拿到 null，会漏掉"会话正在拆除"这一事实并误判为"未加载游戏"。
        _session = null;
        _sessionHub = null;
        // 控制权终止（Controller.End）由 RequestTeardown 内部发出，且必须晚于
        // TurnDelivery 的拆除旗标（ADR-0049 D1）。此处<b>不得</b>自行再调 Controller.End：
        // 那会把「End 已发布、旗标未置」的窗口重新打开，使在途 GET /turn 误报 409。
        detached?.RequestTeardown(reason);
        // 循环仍活着才登记为 _retiring：已结束的会话（如刚 QUIT）不影响后续建会话。
        if (detached is { HasEnded: false })
            _retiring = detached;
        return detached;
    }

    /// <summary>
    /// 锁外有界等待被摘下的游戏循环退出。返回 <c>false</c> 表示超时（旧循环仍在跑）。
    ///
    /// 超时后调用方<b>不得</b>启动新会话——见类型注释的「串行」段。
    /// 本方法可在已持有 <c>_mutationGate</c> 时调用（它不碰任何本类的锁）。
    /// </summary>
    private async Task<bool> AwaitTeardownAsync(Session? detached)
    {
        var target = detached ?? _retiring;
        if (target == null)
            return true;

        if (!await target.WaitForLoopExitAsync(TeardownTimeout).ConfigureAwait(false))
            return false;

        // 确实退出了才清登记——后续变更操作可正常建会话。
        if (ReferenceEquals(_retiring, target))
            _retiring = null;
        return true;
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
    /// <summary>上一局的游戏循环仍在运行（拆除超时）→ HTTP 503。待其退出后重试。</summary>
    PreviousSessionStillRunning,
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
    /// <summary>拆除超时：游戏循环仍在长回合中执行 → HTTP 503（issue-001）。</summary>
    TeardownTimeout,
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
    /// <summary>旧游戏循环仍在运行（拆除超时）→ HTTP 503（issue-001）。</summary>
    PreviousSessionStillRunning,
}

/// <summary>POST /load-game 结果。</summary>
internal readonly record struct LoadGameResult(LoadGameStatus Status, string? Code, string? Message, string? SessionId, string? State, string? GameDir)
{
    public static LoadGameResult ControlDenied(string reason) => new(LoadGameStatus.ControlDenied, reason, "Control is held by another controller", null, null, null);
    public static LoadGameResult PathError(string code, string message) => new(LoadGameStatus.PathError, code, message, null, null, null);
    public static LoadGameResult LoadFailed(string message) => new(LoadGameStatus.LoadFailed, null, message, null, null, null);
    /// <summary>旧循环未退出——不建新会话（spec 决策四：全局静态隔离要求两者串行）。</summary>
    public static LoadGameResult PreviousStillRunning() => new(LoadGameStatus.PreviousSessionStillRunning, "SESSION_STILL_RUNNING", "The previous game loop is still running a long turn. Wait for it to finish, then retry.", null, null, null);
    public static LoadGameResult Success(string sessionId, string state, string gameDir) => new(LoadGameStatus.Success, null, null, sessionId, state, gameDir);
}

/// <summary>WS 订阅三元组：session + 独占 reader + 所属 hub（finally 退订用）。</summary>
internal sealed record WsSubscription(
    Session Session,
    ChannelReader<Turn> Reader,
    OutputHub Hub,
    ControlIdentity Identity = default);
