using System;
using System.Threading;

namespace MinorShift.Emuera.Files;

/// <summary>
/// 会话作用域的游戏根环境（issue-007 引入，issue-009 收敛为唯一视图）。
///
/// <para><b>它替代的是什么</b>：<c>GamePaths.Current</c> 原来是一个进程级可变单例字段——
/// 谁最后 <c>Resolve</c>，谁就改写了<b>所有</b>读者，包括上一局还没退出的游戏循环。
/// issue-001 的故障站点正是这个形状：拆除超时期间，旧循环对着新目录找 ERB/CSV。
/// 现在根由<b>环境</b>承载，会话作用域那一层随建立者（组装根 <c>CoreSession</c>）生灭。</para>
///
/// <para><b>唯一的写入者</b>是 <see cref="Establish"/>：写入 <see cref="AsyncLocal{T}"/>，
/// 随执行上下文流动。在建立之后创建的任务与线程都读到本会话的根；别的流上稍后建立的根
/// 改不到它。因此建立必须<b>早于</b>循环任务创建，或在循环流内部自己建立
/// （<c>CoreSession.GameLoopAsync</c> 取后者）。</para>
///
/// <para><b>没有进程级槽位</b>（issue-009 contract）：原装配期提交已删除，
/// 作用域外 <see cref="Current"/> 为 null。需要根的兼容垫片必须在作用域内；
/// 初始化与配置加载由调用方显式携带根（<c>ConfigData(GamePaths)</c> / <c>IGameDirAccessor</c>），
/// 不再依赖「最后一次提交」。</para>
/// </summary>
internal static class GameRootEnvironment
{
    private static readonly AsyncLocal<GamePaths?> _scope = new();

    /// <summary>本执行作用域建立的根；作用域外为 null。</summary>
    internal static GamePaths? Current => _scope.Value;

    /// <summary>
    /// 取本执行作用域的根；作用域外抛 <see cref="InvalidOperationException"/>。
    /// 兼容垫片（<c>Program.*</c> / <c>SafCompat</c>）用它把「无根」显性化，而不是回落到进程级默认目录。
    /// </summary>
    internal static GamePaths RequireCurrent(string caller)
        => _scope.Value ?? throw new InvalidOperationException(
            $"{caller} 需要游戏根，但当前执行作用域没有建立根——该读取只能在 CoreSession 的游戏循环流内进行。");

    /// <summary>
    /// 建立本会话作用域的根环境；返回句柄在 <c>Dispose</c> 时还原本流原先的根——
    /// 循环退出即销毁，不残留给下一次启动。可嵌套（内层退出后回到外层根）。
    /// <paramref name="paths"/> 为 null 时不建立，返回空句柄。
    /// </summary>
    internal static IDisposable Establish(GamePaths? paths)
        => paths is null ? EmptyScope.Instance : new Scope(paths);

    /// <summary>作用域句柄：记住进入前的根，退出时还原——RAII，重复 Dispose 安全。</summary>
    private sealed class Scope : IDisposable
    {
        private readonly GamePaths? _previous;
        private GamePaths? _owned;

        internal Scope(GamePaths paths)
        {
            _previous = _scope.Value;
            _owned = paths;
            _scope.Value = paths;
        }

        public void Dispose()
        {
            if (_owned is null)
                return;
            _owned = null;
            _scope.Value = _previous;
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        internal static readonly EmptyScope Instance = new();
        public void Dispose() { }
    }
}
