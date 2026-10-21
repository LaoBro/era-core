using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MinorShift.Emuera;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// issue-004 步骤 0：<see cref="OutputPort"/> 的行为锁 —— 快照（简单路径）与增量回合流（完整路径）
/// 必须描述<b>同一幅画面</b>；宿主只经公共属性即可读完全部 wire 字段（含图片/矩形/按钮），
/// 不必解析 JSON。
///
/// <para>用例体只通过公共类型（<see cref="OutputPort"/> / <see cref="Turn"/> / <see cref="TurnRecord"/> /
/// <see cref="DisplaySnapshot"/> / <see cref="LineOp"/> …）读取输出；门面与引擎的装配留在 Core
/// （宿主拿不到 <c>DisplayState</c>/<c>OutputHub</c>），测试经 InternalsVisibleTo 构造。</para>
/// </summary>
public sealed class OutputPortTests
{
    private static readonly InputRequest EnterKey = new() { InputType = InputType.EnterKey };

    [Fact]
    public async Task Snapshot_plus_delivered_diff_reconstructs_the_same_picture()
    {
        using var h = new Harness();

        h.Print("hello");
        var turns = h.Port.Turns;
        Assert.NotNull(turns);                      // 有中枢 ⇒ 完整路径可用

        Assert.Null(h.ComputeDiff());               // 首帧只设基线
        var baseRows = Rows(h.Port.Snapshot);       // 简单路径：当前全量画面

        h.Print("world");
        var diff = h.ComputeDiff();
        Assert.NotNull(diff);
        h.PublishTurn(diff!);

        var turn = await turns.ReadAsync();
        Assert.Same(diff, turn.Record.diff);
        Assert.Contains("world", turn.Wire, StringComparison.Ordinal);

        // 增量流 + 基线快照 ⇒ 与最新全量快照逐行一致。
        var rebuilt = ApplyLineOps(baseRows, turn.Record.diff!.lineOps);
        Assert.Equal(Rows(h.Port.Snapshot), rebuilt);
    }

    [Fact]
    public void Public_wire_model_exposes_image_rect_and_button_fields_without_json()
    {
        var segment = new PrintSegment(
            "txt", "#ffffff", bold: true, italic: false, fontname: "MS Gothic",
            image: new SegmentImage("img/face.png", "img/face_b.png", "img/face_m.png",
                width: 128, height: 96, ypos: 4, crop: new SegmentCrop(-3, -2, 256, 192)),
            shape: new SegmentShape("rect", 1, 2, 30, 40, "#123456"),
            underline: true, strikeout: false);
        var entry = new DisplayEntry(
            new List<PrintSegment> { segment },
            new ButtonRef(42L, isInteger: true, generation: 7, col: 5, width: 9));
        var line = new DisplayLine(new List<DisplayEntry> { entry }, "left", isLineEnd: true);
        var snapshot = new DisplaySnapshot(
            new List<DisplayLine> { line }, "#000000",
            AwaitInfo.Of(EnterKey, ConsoleState.WaitInput, timedOut: false),
            TurnRecord.CurrentProtocolVersion, generation: 3);

        // 全部经公共只读属性读取。
        var readSegment = snapshot.lines[0].entries[0].segments[0];
        var image = readSegment.image!;
        Assert.Equal("img/face.png", image.src);
        Assert.Equal("img/face_b.png", image.srcb);
        Assert.Equal(-3, image.crop!.x);
        Assert.Equal(256, image.crop.imgWidth);
        Assert.Equal("rect", readSegment.shape!.type);
        Assert.Equal(30, readSegment.shape.width);
        Assert.Equal("#123456", readSegment.shape.color);
        var button = snapshot.lines[0].entries[0].button!;
        Assert.Equal(42L, button.value);
        Assert.Equal(5, button.col);

        Assert.Equal(12, snapshot.protocolVersion);
        Assert.Equal(TurnRecord.CurrentProtocolVersion, snapshot.protocolVersion);
        Assert.Equal("EnterKey", snapshot.awaiting.input!.type);
    }

    [Fact]
    public void Each_Turns_read_registers_an_independent_observer()
    {
        using var h = new Harness();

        var first = h.Port.Turns;
        var second = h.Port.Turns;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Snapshot_only_host_has_no_turn_stream()
    {
        using var h = new Harness(withHub: false);

        Assert.Null(h.Port.Turns);   // CLI VT 渲染只消费快照
        Assert.NotNull(h.Port.Snapshot);
    }

    // ------------------------------------------------------------------
    // 与前端 applyDiff 同语义的还原器（shift_head → clear → append，按序）
    // ------------------------------------------------------------------

    private static List<string> Rows(DisplaySnapshot snapshot) => snapshot.lines.Select(RowText).ToList();

    private static string RowText(DisplayLine line)
        => string.Concat(line.entries.SelectMany(e => e.segments.Select(s => s.text)));

    private static List<string> ApplyLineOps(List<string> prev, List<LineOp> ops)
    {
        var lines = new List<string>(prev);
        foreach (var op in ops)
        {
            switch (op)
            {
                case ClearScreenOp:
                    lines.Clear();
                    break;
                case ShiftHeadLineOp shift:
                    lines.RemoveRange(0, Math.Min(shift.count, lines.Count));
                    break;
                case ClearLineDiffOp clear:
                    int n = Math.Min(clear.clearCount, lines.Count);
                    lines.RemoveRange(lines.Count - n, n);
                    break;
                case AppendLinesOp append:
                    lines.AddRange(append.newLines.Select(RowText));
                    break;
                default:
                    throw new InvalidOperationException($"未知 lineOp: {op.type}");
            }
        }
        return lines;
    }

    private sealed class Harness : IDisposable
    {
        private readonly IDisposable _scope;

        internal EmueraConsole Console { get; }
        internal DisplayState State { get; }
        internal OutputHub Hub { get; }
        internal OutputPort Port { get; }

        internal Harness(bool withHub = true)
        {
            _scope = GlobalStatic.OpenScope(new ConfigData());
            Console = new EmueraConsole(new HeadlessConsole(), new NullTerminalSetup());
            State = new DisplayState(Console, "MS Gothic");
            Hub = new OutputHub();
            Port = new OutputPort(State, withHub ? Hub : null);
        }

        internal void Print(string text)
        {
            var line = new ConsoleDisplayLine(
                new[] { TestButtonFactory.CreateNonButton(text) },
                isLogical: true, temporary: false);
            line.LineNo = Console.DisplayLineList.Count;
            Console.DisplayLineList.Add(line);
        }

        internal DisplayDiff? ComputeDiff() => State.ComputeDiff();

        internal void PublishTurn(DisplayDiff diff)
            => Hub.Publish(TurnFrames.Ok(
                diff,
                AwaitInfo.Of(EnterKey, ConsoleState.WaitInput, timedOut: false),
                error: null, generation: 0, isInitial: false));

        public void Dispose() => _scope.Dispose();
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
