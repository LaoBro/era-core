using MinorShift.Emuera.Files;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// AppDataPaths / AgentLog 全局状态依赖测试类的串行隔离集合。
/// AppDataPaths.Directory 与 AgentLog.Instance 都是进程级全局静态，xUnit 默认类级并行时，
/// 跨类切换 AppDataPaths 会把 AgentLog 的 writer 落点/FilePath 切到别的目录（SafStage2StorageTests
/// 的 AppDataScope 即会全局切换）——依赖它们的测试放入本集合：
/// DisableParallelization=true 使集合内测试与其他所有测试串行执行，消除竞态
/// （T-027 Phase 4 实战教训：AgentLogTests 在完整套件下间歇失败，2/3 复现）。
/// </summary>
[CollectionDefinition("AppDataIsolated", DisableParallelization = true)]
public class AppDataIsolatedCollection
{
}
