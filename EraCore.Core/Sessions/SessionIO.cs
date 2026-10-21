using System;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.Output;

namespace MinorShift.Emuera.Sessions;

/// <summary>
/// 替换 Console.ReadLine/WriteLine 的抽象，使 AgentJsonlProtocol 不依赖全局 stdin/stdout。
/// </summary>
internal abstract class SessionIO
{
    /// <summary>
    /// 异步读取一行输入。返回 null 表示 EOF/关闭/取消。
    /// 实现应支持 CancellationToken 取消（TINPUT timeout 通过 CancelAfter 实现）。
    /// </summary>
    public abstract Task<string?> ReadLineAsync(CancellationToken ct);
    public abstract void WriteLine(Turn turn);
    public abstract void Close();
    public abstract bool IsConnected { get; }
}
