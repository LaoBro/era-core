using MinorShift.Emuera.Files;
using System;
using System.IO;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// A0（saf-accel 计划）：AgentLog 默认关闭 + 运行时切换单测。
/// <para>
/// <b>时序无关性</b>：AgentLog 是进程级静态 Lazy 单例，xUnit 并行下其他测试类（Server 路径）
/// 可能先触发 <see cref="AgentLog.Instance"/> 访问（Lazy 按环境变量默认开初始化）。本测试
/// 只断言「无论 Lazy 何时初始化都成立」的行为——Configure 内部已含「Lazy 已初始化则立即
/// 应用」兜底（见 AgentLog.Configure），故 Configure(false) 恒使 Enabled 变 false。
/// 不断言 FilePath 初值为 null / 文件落点目录——writer 可能在 Lazy 初始化时已建于其他
/// AppDataPaths 目录（并行测试配置），只验证同一实例的写入经 Dispose flush 后内容可达。
/// </para>
/// <para>
/// AppDataPaths 为全局可变状态——沿用 SafStage2StorageTests.AppDataScope 的保存/恢复惯例，
/// 避免与并行测试互相污染；同时本类放入 AppDataIsolated 串行集合（DisableParallelization=true），
/// 保证与其他切 AppDataPaths 的测试（如 SafStage2StorageTests.AppDataScope）不并行
/// （T-027 Phase 4：完整套件下间歇失败，根因即并行切目录）。
/// </para>
/// </summary>
[Collection("AppDataIsolated")]
public sealed class AgentLogTests : IDisposable
{
    private readonly string _previousAppData = AppDataPaths.Directory;
    private readonly string _tempDir;
    private bool _disposed;

    public AgentLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "emuera-agentlog-a0-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Configure_false_disables_then_runtime_toggle()
    {
        AppDataPaths.Configure(_tempDir);

        // A0 主场景：Configure(false) → Enabled false（初值或立即应用，两种时序都成立）
        AgentLog.Configure(false);
        var log = AgentLog.Instance;
        Assert.False(log.Enabled);
        log.Write("should-not-write-when-disabled"); // 关闭时静默，不抛异常

        // 运行时开启 → Enabled true；writer 必然已存在（惰性创建或 Lazy 初始化时已建）
        log.Enabled = true;
        Assert.True(log.Enabled);
        Assert.NotNull(log.FilePath);

        log.Write("a0-test-message");

        // ReadAllText 内部 flush + FileShare.ReadWrite 读取——无需退出进程即可读到最新日志
        // （app 内日志查看器路径；FileShare.ReadWrite 是必须的：writer 持有 Write 访问句柄，
        // 读取句柄须允许「他人写」，否则 File.ReadAllText 默认 FileShare.Read 会 IOException）
        var text = log.ReadAllText();
        Assert.NotNull(text);
        Assert.Contains("a0-test-message", text);
        Assert.DoesNotContain("should-not-write-when-disabled", text); // 禁用时 Write 静默未落盘

        // 再关闭 → Enabled 变 false；Dispose flush 后文件内容可读（同一实例的写入）
        log.Enabled = false;
        Assert.False(log.Enabled);
        log.Dispose();
        Assert.NotNull(log.FilePath);
        var content = File.ReadAllText(log.FilePath!);
        Assert.Contains("a0-test-message", content);

        // Dispose 后 Write 静默不抛
        log.Write("after-dispose");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDataPaths.Configure(_previousAppData); // 恢复全局状态，避免污染并行测试
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果
        }
    }
}
