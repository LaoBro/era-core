using MinorShift.Emuera.Files;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// GamePaths.Current 依赖测试类的串行隔离集合。
/// GamePaths.Current 是全局静态，xUnit 默认类级并行时，跨类 Resolve 会互相覆盖——
/// 依赖它的测试类（图片探针路径）放入本集合：DisableParallelization=true 使集合内
/// 测试与其他所有测试串行执行，消除竞态（issue 01/02 实战教训）。
/// </summary>
[CollectionDefinition("GamePathsIsolated", DisableParallelization = true)]
public class GamePathsIsolatedCollection
{
}
