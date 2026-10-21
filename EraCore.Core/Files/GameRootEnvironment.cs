using System;
using System.Threading;

namespace MinorShift.Emuera.Files;

/// <summary>
/// 会话作用域的游戏根环境（issue-007，expand 段）。
///
/// <para><b>它替代的是什么</b>：<c>GamePaths.Current</c> 原来是一个进程级可变单例字段——
/// 谁最后 <c>Resolve</c>，谁就改写了<b>所有</b>读者，包括上一局还没退出的游戏循环。
/// issue-001 的故障站点正是这个形状：拆除超时期间，旧循环对着新目录找 ERB/CSV，
/// 只能靠「校验阶段先别提交」的顺序侥幸躲过。现在根由<b>环境</b>承载，
/// 会话作用域那一层随建立者（组装根 <c>CoreSession</c>）生灭，后续改写影响不到它。</para>
///
/// <para><b>expand 期间的两层视图</b>（第二层由 issue-009 删除）：
/// <list type="number">
/// <item><b>作用域层</b>：<see cref="Establish"/> 写入的 <see cref="AsyncLocal{T}"/>。
/// 它随执行上下文流动：在建立之后创建的任务与线程都读到本会话的根，
/// 而别的流上稍后发生的提交改不到它。这是「旧循环拆除期间仍读旧根」的机制来源——
/// 因此建立必须<b>早于</b>循环任务创建，或在循环流内部自己建立（<c>CoreSession.GameLoopAsync</c> 取后者）。</item>
/// <item><b>装配期视图</b>：<see cref="Commit"/> 写入的进程级槽位，只服务<b>尚未迁移</b>的读者——
/// HTTP 请求流上的 <c>/assets</c>、MAUI 的 UI 线程、既有测试脚手架。它们本来就没有会话上下文，
/// 读到的是「最后一次装配提交的根」，与改动前逐位一致。</item>
/// </list>
/// 读取优先级：作用域层 → 装配期视图。</para>
///
/// <para><b>写入者只有两个</b>：<see cref="Establish"/>（会话作用域，组装根专用）与
/// <see cref="Commit"/>（装配期提交，旧 API 的门）。<see cref="Current"/> 没有 setter，
/// 是纯只读视图——引擎调用点在 issue-008 分批迁移到显式根，issue-009 删掉装配期视图。</para>
/// </summary>
internal static class GameRootEnvironment
{
    private static readonly AsyncLocal<GamePaths?> _scope = new();

    /// <summary>装配期视图（尚未迁移的读者用）。volatile：跨流可见，与旧单例字段的可见性一致。</summary>
    private static volatile GamePaths? _committed;

    /// <summary>
    /// 当前根环境的只读视图：本执行作用域建立的根优先，其次装配期最后一次提交的根。
    /// 无任何游戏根时可为 null（调用方语义与改动前一致）。
    /// </summary>
    internal static GamePaths? Current => _scope.Value ?? _committed;

    /// <summary>
    /// 建立本会话作用域的根环境；返回句柄在 <c>Dispose</c> 时还原本流原先的根——
    /// 循环退出即销毁，不残留给下一次启动。可嵌套（内层退出后回到外层根）。
    /// <paramref name="paths"/> 为 null 时不建立，返回空句柄。
    /// </summary>
    internal static IDisposable Establish(GamePaths? paths)
        => paths is null ? EmptyScope.Instance : new Scope(paths);

    /// <summary>
    /// 装配期提交：把根登记到装配期视图，并写入<b>本流</b>的作用域层
    /// （本流随后创建的循环自然继承它）。这是 <c>GamePaths.Resolve</c> /
    /// <c>GamePaths.SetCurrent</c> 背后的唯一写入动作。
    /// </summary>
    internal static void Commit(GamePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _committed = paths;
        _scope.Value = paths;
    }

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
