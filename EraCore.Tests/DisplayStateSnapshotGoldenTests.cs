using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace Emuera.Headless.Tests;

/// <summary>
/// Phase 0-1: Web 快照 golden 测试（DisplayState 统一真相源执行计划）。
/// 用 TestButtonFactory 轻量构造器（Q15）合成 5 个场景的 ConsoleDisplayLine 列表，
/// 喂 BuildSnapshot 序列化，与提交的 .json golden 资源（TestData/Phase0/）比对。
/// 不走服务器，毫秒级、无 ConPTY 依赖。字体固定 MS Gothic 以保证 width 确定。
///
/// golden 文件重新生成：
///   设置环境变量 EMUERA_REGENERATE_GOLDEN=1 后运行本测试类，golden 文件写入源目录
///   TestData/Phase0/，提交即可。常规运行（无该环境变量）执行比对。
/// </summary>
public class DisplayStateSnapshotGoldenTests
{
    private const string DefaultFontName = "MS Gothic";
    private const int ExpectedProtocolVersion = 12; // v12：awaiting 容器

    /// <summary>
    /// golden 序列化选项：与生产 DisplayState.JsonOpts 一致使用 WhenWritingNull，
    /// 额外启用 WriteIndented 以提高 golden 文件可读性 / 可 diff 性。
    /// </summary>
    private static readonly JsonSerializerOptions GoldenJsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    // ---------- 5 个场景构造 ----------

    /// <summary>
    /// WaitInput 态的快照必须带一个<b>真实请求</b>：v12 契约要求非终态帧的 <c>awaiting</c>
    /// 至少一维必现（§1.2 规则 1）—— <c>currentRequest: null</c> 会产出 <c>"awaiting": {}</c>，
    /// 那正是「没有任何东西能推进这个等待」的非法形态，不该被固化进 golden。
    /// </summary>
    private static InputRequest EnterKeyWait() => new() { InputType = InputType.EnterKey };

    /// <summary>场景 1：空屏（无显示行）。</summary>
    private static DisplaySnapshot BuildEmptyScreen() =>
        DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>(),
            EmuColor.Black,
            ConsoleState.WaitInput,
            EnterKeyWait(),
            DefaultFontName);

    /// <summary>场景 2：单行单按钮。</summary>
    private static DisplaySnapshot BuildSingleLine() =>
        DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>
            {
                new(new[] { TestButtonFactory.CreateButton("[OK]", input: 1) },
                    isLogical: true, temporary: false),
            },
            EmuColor.Black,
            ConsoleState.WaitInput,
            EnterKeyWait(),
            DefaultFontName);

    /// <summary>场景 3：多行多按钮（含几何累加 + 空行）。</summary>
    private static DisplaySnapshot BuildMultiLineButtons() =>
        DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>
            {
                new(TestButtonFactory.CreateButtons(("[OK]", 1)),
                    isLogical: true, temporary: false),
                new(TestButtonFactory.CreateButtons(("[Cancel]", 2), ("[Exit]", 3)),
                    isLogical: true, temporary: false),
                new(Array.Empty<ConsoleButtonString>(),
                    isLogical: true, temporary: false),
            },
            EmuColor.Black,
            ConsoleState.WaitInput,
            EnterKeyWait(),
            DefaultFontName);

    /// <summary>
    /// 场景 4：CLEARLINE 态——CLEARLINE 清除末行后剩余的显示状态。
    /// 快照只反映当前 displayLineList 内容，不含 LineNo，故 CLEARLINE 态的特征是
    /// 行数为清除后的子集。此处构造 2 行（末行已清除后的状态）。
    /// </summary>
    private static DisplaySnapshot BuildClearlineState() =>
        DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>
            {
                new(new[] { TestButtonFactory.CreateNonButton("Line 1 survives CLEARLINE") },
                    isLogical: true, temporary: false),
                new(new[] { TestButtonFactory.CreateButton("[Continue]", input: 1) },
                    isLogical: true, temporary: false),
            },
            EmuColor.Black,
            ConsoleState.WaitInput,
            EnterKeyWait(),
            DefaultFontName);

    /// <summary>场景 5：SET_BG 态——背景色非默认（DarkBlue #00008B）+ 带 inputType。</summary>
    private static DisplaySnapshot BuildSetBgState() =>
        DisplayState.BuildSnapshot(
            new List<ConsoleDisplayLine>
            {
                new(new[] { TestButtonFactory.CreateNonButton("Text on dark blue background") },
                    isLogical: true, temporary: false),
            },
            EmuColor.FromArgb(0, 0, 139), // DarkBlue → #00008B
            ConsoleState.WaitInput,
            new InputRequest { InputType = InputType.IntValue },
            DefaultFontName);

    // ---------- golden 比对 ----------

    [Fact]
    public void Empty_screen_matches_golden()
        => AssertSnapshotMatchesGolden(BuildEmptyScreen(), "empty_screen.json");

    [Fact]
    public void Single_line_matches_golden()
        => AssertSnapshotMatchesGolden(BuildSingleLine(), "single_line.json");

    [Fact]
    public void Multi_line_buttons_matches_golden()
        => AssertSnapshotMatchesGolden(BuildMultiLineButtons(), "multi_line_buttons.json");

    [Fact]
    public void Clearline_state_matches_golden()
        => AssertSnapshotMatchesGolden(BuildClearlineState(), "clearline_state.json");

    [Fact]
    public void Set_bg_state_matches_golden()
        => AssertSnapshotMatchesGolden(BuildSetBgState(), "set_bg_state.json");

    /// <summary>
    /// 将 snapshot 序列化为 JSON 与提交的 golden 文件比对。
    /// 若环境变量 EMUERA_REGENERATE_GOLDEN=1，则将实际 JSON 写入源目录的 golden 文件
    /// （TestData/Phase0/），用于首次生成或场景变更后刷新。
    /// </summary>
    private static void AssertSnapshotMatchesGolden(DisplaySnapshot snapshot, string goldenFileName)
    {
        Assert.Equal(ExpectedProtocolVersion, snapshot.protocolVersion);

        var actual = JsonSerializer.Serialize(snapshot, GoldenJsonOpts);
        var outputGoldenPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "TestData", "Phase0", goldenFileName);

        if (Environment.GetEnvironmentVariable("EMUERA_REGENERATE_GOLDEN") == "1")
        {
            // 重新生成模式：写入源目录（BaseDirectory = bin/<config>/<tfm>/，上溯 3 级到项目根）
            var srcGoldenPath = Path.GetFullPath(Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..",
                "TestData", "Phase0", goldenFileName));
            Directory.CreateDirectory(Path.GetDirectoryName(srcGoldenPath)!);
            File.WriteAllText(srcGoldenPath, actual);
            return; // 不断言，仅生成
        }

        Assert.True(File.Exists(outputGoldenPath),
            $"Golden 文件不存在: {outputGoldenPath}。" +
            "运行测试时设置 EMUERA_REGENERATE_GOLDEN=1 以生成 golden 文件。");
        var expected = File.ReadAllText(outputGoldenPath);
        Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
    }

    private static string NormalizeLineEndings(string s) =>
        s.Replace("\r\n", "\n").Replace("\r", "\n");
}
