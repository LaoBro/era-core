using System;
using MinorShift.Emuera.Files;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// issue-009：测试夹具显式建立游戏根作用域。
/// <para>
/// <c>GamePaths.Resolve</c> 只构造、不提交；需要 <c>Program.*</c> / <c>SafCompat</c> 读根的
/// 测试必须自己 <see cref="GameRootEnvironment.Establish"/>。AsyncLocal 作用域随每个测试的
/// ExecutionContext 流动，测试间天然隔离——不再需要 <c>GamePathsIsolated</c> 串行集合。
/// </para>
/// </summary>
internal sealed class RootScope : IDisposable
{
    private readonly IDisposable _scope;

    public GamePaths Paths { get; }

    public RootScope(string root, IGameDirAccessor accessor)
    {
        Paths = GamePaths.Resolve(root, accessor);
        _scope = GameRootEnvironment.Establish(Paths);
    }

    public void Dispose() => _scope.Dispose();
}
