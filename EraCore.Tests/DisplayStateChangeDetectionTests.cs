using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// Phase 1-3：DisplayState 变更检测单测。
/// 验证 TryUpdate 以 _pendingOps 为权威变更信号（不读 LineNo），
/// CLEARLINE 回退后再 print 使 count+LineNo 回到旧值仍判定为变化，
/// 以及并发安全（_gate 锁 + Rebuild 浅拷贝 displayLineList）。
/// </summary>
public class DisplayStateChangeDetectionTests : IDisposable
{
    private readonly IDisposable _scope;
    private readonly EmueraConsole _console;
    private readonly DisplayState _displayState;

    public DisplayStateChangeDetectionTests()
    {
        _scope = GlobalStatic.OpenScope(new ConfigData());
        var ui = new HeadlessConsole();
        _console = new EmueraConsole(ui, new NullTerminalSetup());
        _displayState = new DisplayState(_console, "MS Gothic");
    }

    public void Dispose() => _scope.Dispose();

    /// <summary>直接向 _pendingOps 添加 op（绕过 ConsolePrintManager，模拟引擎打印后的状态）。</summary>
    private void AddPendingOp(TurnOp op) => _console._state._pendingOps.Enqueue(op);

    /// <summary>排空 _pendingOps（模拟 Phase 5-3 之前 BuildTurn 的 drain；
    /// Phase 5-3 后 TryUpdate 自身已消费式清空，本方法仅供测试在 TryUpdate 之外手动清空使用）。</summary>
    private void DrainPendingOps() => _console._state._pendingOps.Clear();

    private static ConsoleDisplayLine Line(string text) =>
        new(new[] { TestButtonFactory.CreateNonButton(text) },
            isLogical: true, temporary: false);

    /// <summary>模拟引擎打印一行文本：同时向 displayLineList 追加行 + 向 _pendingOps 追加 PrintOp。</summary>
    private void PrintLine(string text)
    {
        _console.DisplayLineList.Add(Line(text));
        AddPendingOp(new PrintOp(
            new List<PrintSegment> { new(text, null, null, null, null) },
            button: null));
    }

    // ---------- 变更检测核心 ----------

    [Fact]
    public void TryUpdate_first_call_builds_current_and_returns_true()
    {
        Assert.True(_displayState.TryUpdate());
        Assert.NotNull(_displayState.Current);
        Assert.Empty(_displayState.Current.lines);
    }

    [Fact]
    public void TryUpdate_returns_false_when_no_pending_ops_after_initial_build()
    {
        _displayState.TryUpdate(); // 初始构建
        var firstSnapshot = _displayState.Current;

        Assert.False(_displayState.TryUpdate());
        Assert.Same(firstSnapshot, _displayState.Current); // _current 未变
    }

    [Fact]
    public void TryUpdate_returns_true_and_rebuilds_when_pending_ops_non_empty()
    {
        _displayState.TryUpdate(); // 初始构建（空屏）
        Assert.Empty(_displayState.Current.lines);

        PrintLine("hello");

        Assert.True(_displayState.TryUpdate());
        Assert.Single(_displayState.Current.lines);
    }

    [Fact]
    public void TryUpdate_after_drain_returns_false_until_new_ops_arrive()
    {
        PrintLine("a");
        _displayState.TryUpdate(); // 重建（Phase 5-3：内部已消费式清空 _pendingOps）

        Assert.False(_displayState.TryUpdate()); // 无新 op → 无变化

        // 新 op 到达 → 变化
        PrintLine("b");
        Assert.True(_displayState.TryUpdate());
        Assert.Equal(2, _displayState.Current.lines.Count);
    }

    // ---------- CLEARLINE 回归用例 ----------
    // 直接验证 LineNo 方案会漏检的反例：
    // 行数 / LineNo 回退后再 print 使 count + LineNo 回到旧值，
    // 必须判定为「变化」（因为 pendingOps 非空，TryUpdate 读的是 pendingOps 不是 LineNo）。

    [Fact]
    public void TryUpdate_detects_change_after_clearline_reprint_cycle()
    {
        // 初始：2 行
        PrintLine("line1");
        AddPendingOp(new NewLineOp(null));
        PrintLine("line2");
        _displayState.TryUpdate();
        Assert.Equal(2, _displayState.Current.lines.Count);
        DrainPendingOps();

        // CLEARLINE 1：删除末行 → 1 行，LineNo 回退
        _console.DisplayLineList.RemoveAt(_console.DisplayLineList.Count - 1);
        AddPendingOp(new ClearLineOp(1));
        Assert.True(_displayState.TryUpdate());
        Assert.Single(_displayState.Current.lines);
        DrainPendingOps();

        // 重新 print 同样内容 → 行数回到 2，LineNo 也回到旧值
        // 若用 LineNo 方案会误判为「无变化」，但 TryUpdate 读 pendingOps → 正确判定为变化
        PrintLine("line2");

        Assert.True(_displayState.TryUpdate());
        Assert.Equal(2, _displayState.Current.lines.Count);
    }

    // ---------- Current 自动触发 TryUpdate ----------

    [Fact]
    public void Current_triggers_tryupdate_implicitly()
    {
        // 首次读 Current → 隐式 TryUpdate 构建
        var snap = _displayState.Current;
        Assert.NotNull(snap);
        Assert.Empty(snap.lines);

        // 有新 op 后读 Current → 隐式重建
        PrintLine("x");

        var updated = _displayState.Current;
        Assert.Single(updated.lines);
    }

    // ---------- 浅拷贝隔离（Q5）----------

    [Fact]
    public void Rebuild_shallow_copies_displayLineList_isolating_snapshot_from_mutation()
    {
        PrintLine("orig");
        _displayState.TryUpdate();
        var snapshotBefore = _displayState.Current;
        Assert.Single(snapshotBefore.lines);

        // 游戏线程继续向 displayLineList 追加行——不应影响已取出的快照行数
        _console.DisplayLineList.Add(Line("new"));

        // 快照的 lines 是浅拷贝的独立 List，不受原 list 追加影响
        Assert.Single(snapshotBefore.lines);
    }

    // ---------- 并发：_gate 锁下无竞争 ----------

    [Fact]
    public async Task Concurrent_read_and_write_do_not_corrupt()
    {
        // 预填充一行
        PrintLine("seed");

        var readers = new Task[8];
        for (int i = 0; i < 8; i++)
        {
            readers[i] = Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    // 并发读 Current（内部 _gate 锁 + TryUpdate + Rebuild 浅拷贝）
                    var snap = _displayState.Current;
                    Assert.NotNull(snap);
                    // 快照行数应 >= 1（至少 seed 行；writer 追加更多行时 TryUpdate 重建会反映）
                    Assert.True(snap.lines.Count >= 1);
                }
            });
        }

        // 同时并发写——模拟游戏线程：向 displayLineList 追加行 + 向 _pendingOps 追加 op
        // 这正是 Q5 场景：HTTP 线程在 Rebuild 内浅拷贝 displayLineList 时，游戏线程正在追加
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                PrintLine("w" + i);
                DrainPendingOps();
            }
        });

        await Task.WhenAll(readers);
        await writer;
    }

    private sealed class NullTerminalSetup : ITerminalSetup
    {
        public bool IsAnsiEnabled => false;
        public bool TryEnableAnsi() => false;
        public bool TrySetConsoleSize(int cols, int rows) => false;
        public string? DetectFont() => null;
        public bool TryPrepareVtInput() => false;
    }
}
