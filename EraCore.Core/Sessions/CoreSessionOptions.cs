using System;
using MinorShift.Emuera.Files;
using MinorShift.Emuera.Input;

namespace MinorShift.Emuera.Sessions;

/// <summary>
/// 会话相位（spec §3.3）。公共宿主只需这个粗粒度状态；协议层内部的字符串状态仍由 Server 维护。
/// </summary>
public enum CoreSessionState
{
    /// <summary>已组装但尚未启动。</summary>
    Created,
    /// <summary>已启动，游戏循环仍在加载（ERB / CSV 预载、脚本初始化）。</summary>
    Starting,
    /// <summary>游戏循环运行中。</summary>
    Running,
    /// <summary>等待宿主输入（<c>INPUT</c> 等）。</summary>
    WaitingForInput,
    /// <summary>游戏自然结束（脚本 <c>QUIT</c> / <c>EXIT</c>）。</summary>
    Completed,
    /// <summary>宿主调用 <see cref="CoreSession.StopAsync"/> 后结束。</summary>
    Stopped,
    /// <summary>启动失败或游戏循环异常终止；原因见 <see cref="CoreSession.LastError"/>。</summary>
    Failed,
}

/// <summary>
/// 组装参数：只包含宿主能理解的东西——游戏根、目录访问实现、输入能力位、可选方言 profile。
/// 运行时初始化（编码 / 配置 / 语言）由 <see cref="CoreSession.StartAsync"/> 内部完成。
/// </summary>
public sealed class CoreSessionOptions
{
    public CoreSessionOptions(string gameRoot, IGameDirAccessor dirAccessor)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameRoot);
        ArgumentNullException.ThrowIfNull(dirAccessor);
        GameRoot = gameRoot;
        DirAccessor = dirAccessor;
    }

    /// <summary>游戏根（本地绝对路径或 SAF 根 URI）。</summary>
    public string GameRoot { get; }

    /// <summary>与 <see cref="GameRoot"/> 形态匹配的目录访问实现。</summary>
    public IGameDirAccessor DirAccessor { get; }

    /// <summary>宿主构造期声明的输入能力位；运行期不变。缺省 = 无能力（仅值输入）。</summary>
    public InputCapabilities InputCapabilities { get; init; }

    /// <summary>方言 profile id；<c>null</c> = v24pure（移植前行为）。</summary>
    public string? DialectProfileId { get; init; }

    /// <summary>
    /// 首帧 diff 是否返回全量。WebView 桥接宿主没有快照入口时为 true；
    /// 走 <see cref="OutputPort.Snapshot"/> 的宿主保持 false。
    /// </summary>
    public bool FullDiffOnFirstTurn { get; init; }

    /// <summary>停止时等待游戏循环退出的上界（缺省 5 秒）。</summary>
    public TimeSpan TeardownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// <see cref="CoreSession.StartAsync"/> 的结果。失败以可读代码/消息返回，不抛异常、不杀进程。
/// </summary>
public sealed class CoreSessionStartResult
{
    private CoreSessionStartResult(bool succeeded, string? errorCode, string? errorMessage)
    {
        Succeeded = succeeded;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool Succeeded { get; }

    /// <summary>
    /// <c>DIR_NOT_FOUND</c> / <c>MISSING_CSV</c> / <c>MISSING_ERB</c> / <c>INVALID_GAME_ROOT</c> /
    /// <c>INIT_FAILED</c> / <c>SESSION_STOPPED</c> / <c>SESSION_FAILED</c>。成功时为 null。
    /// </summary>
    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    internal static CoreSessionStartResult Success() => new(true, null, null);

    internal static CoreSessionStartResult Failure(string code, string message) => new(false, code, message);
}
