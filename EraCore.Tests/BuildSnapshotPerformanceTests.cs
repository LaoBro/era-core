using System;
using System.Collections.Generic;
using System.Diagnostics;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// Phase 0-6: BuildSnapshot 性能基线测试（DisplayState 统一真相源执行计划）。
///
/// 生产目标：1000 行 &lt; 5ms（CLI 60fps 帧预算 16ms，留 ~10ms 给渲染 + VT I/O）。
/// Phase 0 实测基线：1000 行 ~14ms（Debug/Release 均如此），**超出生产目标**。
/// 超标根因：Phase 1 TryUpdate 每次 PendingOpCount &gt; 0 就 Rebuild() → BuildSnapshot 全量遍历
/// displayLineList，对每行调 BuildPrintOpsForLine（含 TerminalDisplayWidth CJK 双宽计算）。
/// CLI FlushBuffer 帧级调 TryUpdate，大屏累积后可能每帧全量 rebuild。
///
/// Phase 0 护栏阈值设定为 30ms（基线 ~14ms × 2 + 余量），用于捕获 gross 回归（如 O(n²) 退化）。
/// 将生产目标 5ms 作为 Phase 1 的优化任务：按 ConsoleDisplayLine 引用 memoize BuildPrintOpsForLine 结果。
/// Phase 1 落地 memoize 后，本测试阈值应收紧到 5ms。
/// </summary>
[Collection("PerformanceIsolated")]
public class BuildSnapshotPerformanceTests
{
    private const string DefaultFontName = "MS Gothic";

    /// <summary>Phase 0 护栏阈值（基线 ~14ms × 2 + 余量）。Phase 1 memoize 后收紧到生产目标 5ms。</summary>
    private const int Phase0GuardThresholdMs = 30;

    /// <summary>生产目标阈值：1000 行 &lt; 5ms。Phase 1 memoize 落地后替换 Phase0GuardThresholdMs。</summary>
    private const int ProductionTargetMs = 5;

    [Fact]
    public void BuildSnapshot_100_lines_baseline()
    {
        var list = BuildLineList(100);
        // 预热：首次调用含 JIT + 字体缓存初始化开销
        DisplayState.BuildSnapshot(list, EmuColor.Black, ConsoleState.WaitInput, null, DefaultFontName);

        var sw = Stopwatch.StartNew();
        var snapshot = DisplayState.BuildSnapshot(list, EmuColor.Black, ConsoleState.WaitInput, null, DefaultFontName);
        sw.Stop();

        Assert.Equal(100, snapshot.lines.Count);
        // 100 行无规范阈值，用生产目标 5ms 作宽松上限（基线 ~1.4ms）
        Assert.True(sw.ElapsedMilliseconds < ProductionTargetMs,
            $"BuildSnapshot(100 行) 耗时 {sw.ElapsedMilliseconds}ms，期望 &lt; {ProductionTargetMs}ms。");
    }

    /// <summary>
    /// 核心护栏：1000 行。Phase 0 使用 30ms 阈值（基线 ~14ms × 2 + 余量），
    /// 捕获 gross 回归。生产目标 5ms 由 Phase 1 memoize 优化达成后收紧。
    /// </summary>
    [Fact]
    public void BuildSnapshot_1000_lines_within_phase0_guard()
    {
        var list = BuildLineList(1000);
        DisplayState.BuildSnapshot(list, EmuColor.Black, ConsoleState.WaitInput, null, DefaultFontName); // 预热

        var sw = Stopwatch.StartNew();
        var snapshot = DisplayState.BuildSnapshot(list, EmuColor.Black, ConsoleState.WaitInput, null, DefaultFontName);
        sw.Stop();

        Assert.Equal(1000, snapshot.lines.Count);
        // Phase 0 护栏：30ms（基线 ~14ms，2x 余量捕获 gross 回归）
        Assert.True(sw.ElapsedMilliseconds < Phase0GuardThresholdMs,
            $"BuildSnapshot(1000 行) 耗时 {sw.ElapsedMilliseconds}ms，Phase 0 护栏阈值 {Phase0GuardThresholdMs}ms。" +
            $"生产目标 {ProductionTargetMs}ms（当前基线 ~14ms，Phase 1 须 memoize BuildPrintOpsForLine 优化）。");
    }

    [Fact]
    public void BuildSnapshot_5000_lines_does_not_explode()
    {
        var list = BuildLineList(5000);
        DisplayState.BuildSnapshot(list, EmuColor.Black, ConsoleState.WaitInput, null, DefaultFontName); // 预热

        var sw = Stopwatch.StartNew();
        var snapshot = DisplayState.BuildSnapshot(list, EmuColor.Black, ConsoleState.WaitInput, null, DefaultFontName);
        sw.Stop();

        Assert.Equal(5000, snapshot.lines.Count);
        // 5000 行不设紧阈值，仅验证不爆炸（< 150ms 宽松上限，确认无 O(n²) 退化）
        Assert.True(sw.ElapsedMilliseconds < 150,
            $"BuildSnapshot(5000 行) 耗时 {sw.ElapsedMilliseconds}ms，期望 &lt; 150ms（无 O(n²) 退化）。");
    }

    private static List<ConsoleDisplayLine> BuildLineList(int count)
    {
        var list = new List<ConsoleDisplayLine>(count);
        for (int i = 0; i < count; i++)
        {
            // 每行 2 个按钮，混合 ASCII + CJK 以覆盖双宽计算路径
            var buttons = TestButtonFactory.CreateButtons(("[OK]", i), ("確定", i + 1));
            list.Add(new ConsoleDisplayLine(buttons, isLogical: true, temporary: false));
        }
        return list;
    }
}
