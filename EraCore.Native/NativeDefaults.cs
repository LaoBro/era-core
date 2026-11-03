using MinorShift.Emuera.Sessions;

namespace EraCore.Native;

/// <summary>
/// 冷启动/元信息尚未到达时的默认值——仅用于渲染器在 StartAsync 完成前保持可用布局。
/// 启动成功后必须用 <see cref="HostMetrics"/> 覆盖。
/// </summary>
public static class NativeDefaults
{
    public static HostMetrics Metrics { get; } = new(
        WindowWidth: 760,
        WindowHeight: 480,
        FontSize: 18,
        LineHeight: 19,
        FontName: "ＭＳ ゴシック",
        ForeColor: "#C0C0C0",
        MaxLog: 5000,
        GameColumns: 84,
        VisibleRows: 25);
}
