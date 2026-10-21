using MinorShift.Emuera.Output;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 测试接线用：把一段现成 JSON 包成 <see cref="Turn"/>，让不关心结构化记录的用例
/// （WS 转发、长轮询注入、投递编排）能像以前直接写字符串一样构造回合。
/// Record 为 null —— 这些用例只走 <see cref="Turn.Wire"/>。
/// </summary>
internal static class TestTurnFactory
{
    public static Turn Wire(string json) => new(record: null!, json);
}
